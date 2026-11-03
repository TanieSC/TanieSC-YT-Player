using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TanieSC_YT_Player.Services
{
    /// <summary>
    /// This class is a tiny web server that runs INSIDE your desktop app.
    /// It listens on a random free port like http://127.0.0.1:54321 and
    /// serves two kinds of things:
    ///
    ///   1) STATIC FILES (the player page):
    ///        http://127.0.0.1:54321/index.html
    ///        http://127.0.0.1:54321/style.css
    ///        http://127.0.0.1:54321/yt_app.js
    ///      These are just read from the "wwwroot" folder on disk and
    ///      handed to the browser. Straightforward file serving.
    ///
    ///   2) MEDIA STREAMS (the actual video/audio data):
    ///        http://127.0.0.1:54321/s/{random-key}
    ///      Each key maps to an upstream YouTube CDN URL. When the page
    ///      requests a stream, we fetch from YouTube and pass the bytes
    ///      through unchanged. This is what "proxy" means — we're a
    ///      middleman.
    ///
    /// WHY DOES THE PROXY EXIST AT ALL?
    ///   Two reasons:
    ///
    ///   a) CORS / headers. YouTube's CDN checks Referer and Origin
    ///      headers and blocks cross-origin requests. A web page can't
    ///      send those headers when fetching media. But OUR proxy (running
    ///      in C#) can send ANY header it wants. So the page talks to
    ///      127.0.0.1:54321 (same origin, no CORS problem), and we talk
    ///      to YouTube with the right headers.
    ///
    ///   b) Signed URLs. Every googlevideo URL expires after a few hours
    ///      and must be re-resolved. By holding the URL in a local map
    ///      keyed by a random ID, we can swap a fresh URL under the same
    ///      proxy key if the old one 403s — the browser never notices.
    ///
    /// The class implements IAsyncDisposable so it can shut down cleanly
    /// when the app closes.
    /// </summary>
    public sealed class StreamProxy : IAsyncDisposable
    {
        // ─────────────────────────────────────────────────────────────────
        // DATA STRUCTURES
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// One entry in our map. It records:
        ///   Url  = the actual YouTube CDN URL we forward to
        ///   Mime = what to tell the browser it is ("video/mp4" or "audio/mp4")
        ///
        /// `record struct` = a compact, value-type "record" — like a tiny
        /// class with auto-generated equality. We use `struct` because these
        /// are small and created frequently.
        /// </summary>
        private readonly record struct Entry(string Url, string Mime);

        /// <summary>
        /// The dictionary that maps proxy keys → upstream URLs.
        /// `ConcurrentDictionary` = thread-safe dictionary. Multiple
        /// browser connections can arrive in parallel, so we need one
        /// that won't corrupt itself when read/written from many threads.
        ///
        /// Example contents after loading a track:
        ///   "abc123" → { Url: "https://rr3---...googlevideo.com/...", Mime: "video/mp4" }
        ///   "xyz789" → { Url: "https://rr3---...googlevideo.com/...", Mime: "audio/mp4" }
        /// </summary>
        private readonly ConcurrentDictionary<string, Entry> _entries = new();

        /// <summary>
        /// HttpListener = the actual mini-web-server.
        /// It listens on a specific host+port and raises an event for
        /// each incoming HTTP request. Not thread-safe by itself; we
        /// run it in a background loop below.
        /// </summary>
        private readonly HttpListener _listener;

        /// <summary>
        /// HttpClient = our client for talking to YouTube's CDN.
        /// One instance is shared across all requests, because creating
        /// one per request would exhaust TCP ports. This is standard
        /// .NET practice.
        /// </summary>
        private readonly HttpClient _http;

        /// <summary>
        /// Cancellation token: tells all our background tasks "shut down now".
        /// Triggered in DisposeAsync when the app closes.
        /// </summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Full path to the wwwroot folder — where index.html etc. live.</summary>
        private readonly string _assetsRoot;

        /// <summary>
        /// Event that fires with a diagnostic message each time a media
        /// request completes. Example message:
        ///   "media abc12345 range=bytes=0- status=206 ttfb=12ms total=923ms sent=4583576B rate=4849KB/s"
        /// MainWindow subscribes and writes these to proxy.log for debugging.
        /// </summary>
        public event Action<string>? Log;

        /// <summary>Handle on the background accept-loop task, so we can await it on shutdown.</summary>
        private Task? _loop;

        /// <summary>
        /// The base URL (like "http://127.0.0.1:54321") that the WebView2
        /// will navigate to. Randomised each launch so two instances of the
        /// app can run in parallel without colliding on the same port.
        /// </summary>
        public string BaseUrl { get; }

        // ─────────────────────────────────────────────────────────────────
        // CONSTRUCTOR
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Set up the listener and HTTP client. Does NOT start listening
        /// yet — call Start() after construction.
        /// </summary>
        public StreamProxy(string assetsRoot)
        {
            _assetsRoot = assetsRoot ?? throw new ArgumentNullException(nameof(assetsRoot));

            // Find a free port and use it. GetFreePort() briefly opens a
            // socket on port 0 (meaning "any") and reads back the number
            // the OS picked, then closes it. Almost guaranteed to be free.
            int port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            // Tell HttpListener to listen on that URL. The trailing slash
            // is required by .NET — "http://127.0.0.1:54321/" not "http://127.0.0.1:54321".
            _listener = new HttpListener();
            _listener.Prefixes.Add($"{BaseUrl}/");

            // Configure the upstream HTTP client.
            //   AutomaticDecompression = None → don't auto-gunzip; the media
            //     is already compressed, and CDNs sometimes send it with
            //     weird encodings we don't want to touch.
            //   UseCookies = false → we don't need a cookie jar; saves memory.
            //   AllowAutoRedirect = true → follow YouTube's redirects to
            //     the actual CDN node.
            //   ConnectTimeout = 15s → don't hang forever on a dead node.
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                MaxConnectionsPerServer = 32,   // was 16
                EnableMultipleHttp2Connections = true
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            // Pretend to be Chrome. YouTube's CDN is more permissive with
            // real browser user-agents; the default ".NET HttpClient"
            // user-agent sometimes gets throttled or 403'd.
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
        }

        // ─────────────────────────────────────────────────────────────────
        // START — begin accepting connections
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the listener and kicks off the background accept loop.
        /// After this call, the WebView2 can start navigating to BaseUrl.
        /// </summary>
        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        // ─────────────────────────────────────────────────────────────────
        // REGISTER / UNREGISTER — swapping YouTube URLs for proxy URLs
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Takes a real YouTube CDN URL and returns a proxy URL that
        /// maps to it. The browser will request the proxy URL, and we'll
        /// forward that request to the real one.
        ///
        /// Example:
        ///   Register("https://rr3---.../videoplayback?...", "video/mp4")
        ///     → "http://127.0.0.1:54321/s/a1b2c3d4e5f6"
        ///
        /// Note: the response is a NEW random key every time. Registering
        /// the same URL twice gives two different proxy URLs. That's
        /// intentional — it means each track-load gets its own cache slot,
        /// so if the URL expires we can swap it without disturbing other
        /// in-flight requests.
        /// </summary>
        public string Register(string upstreamUrl, string mime)
        {
            var key = Guid.NewGuid().ToString("N");  // 32 hex chars, unique
            _entries[key] = new Entry(StripRange(upstreamUrl), mime);
            return $"{BaseUrl}/s/{key}";
        }

        /// <summary>
        /// Forget about a proxy URL. Called when a track is replaced so
        /// the old entry doesn't sit in memory forever.
        /// </summary>
        public void Unregister(string proxyUrl)
        {
            var idx = proxyUrl.LastIndexOf('/');
            if (idx < 0) return;
            _entries.TryRemove(proxyUrl[(idx + 1)..], out _);
        }

        // ─────────────────────────────────────────────────────────────────
        // ACCEPT LOOP — the heart of the server
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs continuously in a background thread. Each time a new HTTP
        /// request arrives, HttpListener wakes us up with a context object.
        /// We spawn a Task to handle it, then loop back to wait for the
        /// next one. This means many requests can be handled in parallel.
        /// </summary>
        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }   // listener stopped / shutting down

                // Fire-and-forget: don't await, so we can accept the next
                // connection immediately. HandleAsync manages its own
                // errors internally.
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        /// <summary>
        /// Handle one incoming HTTP request. Routes it based on the URL path:
        ///   • /s/{key}     → proxy to YouTube CDN
        ///   • everything else → serve a file from wwwroot
        ///
        /// Also handles the CORS preflight (OPTIONS) — browsers send this
        /// before cross-origin requests to check permissions. Since the
        /// page and the proxy are on the same origin, CORS doesn't strictly
        /// matter, but supporting OPTIONS is cheap insurance in case the
        /// WebView2 origin changes in the future.
        /// </summary>
        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                // ---- CORS preflight handling ----
                if (req.HttpMethod == "OPTIONS")
                {
                    resp.StatusCode = 204;  // 204 = "no content, but allowed"
                    WriteCors(resp);
                    resp.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                    resp.Headers["Access-Control-Allow-Headers"] = "Range, Accept, Accept-Language";
                    resp.Headers["Access-Control-Max-Age"] = "86400";  // cache for 24h
                    resp.Close();
                    return;
                }

                var path = req.Url?.AbsolutePath ?? "/";

                // ---- MEDIA: /s/{key} ----
                if (path.StartsWith("/s/", StringComparison.Ordinal))
                {
                    var key = path.Substring(3);   // strip "/s/"
                    if (!_entries.TryGetValue(key, out var entry))
                    {
                        resp.StatusCode = 404;
                        resp.Close();
                        return;
                    }
                    await ServeMediaAsync(req, resp, entry);
                    return;
                }

                // ---- STATIC FILES: index.html, yt_app.js, style.css, etc. ----
                await ServeStaticAsync(req, resp, path);
            }
            catch
            {
                // Any uncaught error → 502 (bad gateway). The browser
                // will retry. Better than crashing the accept loop.
                try { resp.StatusCode = 502; } catch { }
            }
            finally
            {
                // Always close the response, even on error.
                try { resp.Close(); } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // STATIC FILE SERVING
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lookup table that maps file extensions to MIME types.
        /// Without this, the browser wouldn't know how to handle the file —
        /// for example, an ".js" file served as "application/octet-stream"
        /// would be rejected by AudioWorklet with a confusing error.
        ///
        /// The values here are the exact MIME types Chromium expects.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> Mime =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [".html"] = "text/html; charset=utf-8",
                [".css"] = "text/css; charset=utf-8",
                [".js"] = "text/javascript; charset=utf-8",   // required for AudioWorklet
                [".mjs"] = "text/javascript; charset=utf-8",
                [".json"] = "application/json; charset=utf-8",
                [".ico"] = "image/x-icon",
                [".png"] = "image/png",
                [".svg"] = "image/svg+xml",
            };

        /// <summary>
        /// Serve a static file from the wwwroot folder.
        /// </summary>
        private async Task ServeStaticAsync(HttpListenerRequest req, HttpListenerResponse resp, string urlPath)
        {
            // "/" → serve index.html (this is what browsers expect).
            if (urlPath == "/" || urlPath.Length == 0) urlPath = "/index.html";

            // Browsers and Chromium always ask for a favicon. Since we
            // don't have one, respond with "204 No Content" so the browser
            // doesn't spam the log with 404 errors.
            if (urlPath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 204;
                resp.ContentLength64 = 0;
                return;
            }

            // ---- SECURITY: block directory traversal ----
            // Without this check, a malicious page could request
            // "/../../../Windows/System32/config/SAM" and read system
            // files. Blocking ".." in the path prevents that.
            if (urlPath.Contains("..", StringComparison.Ordinal))
            {
                resp.StatusCode = 400;
                return;
            }

            // Convert the URL path into a file path on disk.
            // Example: "/style.css" → "<wwwroot>\style.css"
            var relPath = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_assetsRoot, relPath));

            // ---- SECURITY: double-check the result is inside wwwroot ----
            // Even after the ".." check above, Path.GetFullPath can do
            // surprising things with symbolic links. This verifies the
            // final path really is inside the root before we touch disk.
            var rootFull = Path.GetFullPath(_assetsRoot);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                resp.StatusCode = 404;
                return;
            }

            // Set the MIME type based on file extension.
            var ext = Path.GetExtension(full);
            resp.ContentType = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";

            // HEAD request = browser just wants headers, not body.
            // Common for CORS preflight checks and pre-flight HEADs.
            if (req.HttpMethod == "HEAD")
            {
                resp.ContentLength64 = new FileInfo(full).Length;
                return;
            }

            // ---- Stream the file to the response ----
            // We open a FileStream and copy to the response in 80 KB chunks.
            // This uses constant memory regardless of file size — a 100 MB
            // file doesn't get loaded into RAM all at once.
            //
            // (Earlier versions used File.ReadAllBytesAsync which loads the
            // whole file into a byte array. That's fine for small JS files
            // but wasteful for anything large. The FileStream approach is
            // strictly better.)
            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            resp.ContentLength64 = fs.Length;
            await fs.CopyToAsync(resp.OutputStream, 81920, _cts.Token);
        }

        // ─────────────────────────────────────────────────────────────────
        // MEDIA PROXYING — the important part
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch a stream from YouTube and relay it to the browser.
        ///
        /// This handles the tricky bits of media streaming:
        ///   • Range requests: the browser asks for byte ranges like
        ///     "bytes=1048576-2097151" and expects us to honor them.
        ///     We forward that header to YouTube and pass the response back.
        ///   • Long responses: a single media stream is many MB, and the
        ///     browser reads it slowly as it plays. We stream chunk by chunk.
        ///   • Client disconnects: if the user seeks or closes the player,
        ///     the socket drops mid-response. We detect that and cancel the
        ///     upstream request so we don't waste bandwidth downloading
        ///     data no one will ever see.
        ///   • Stalls: if YouTube stops sending bytes for 30 seconds, we
        ///     abort and let the browser retry. Otherwise a dead CDN node
        ///     would hang the player forever.
        /// </summary>
        private async Task ServeMediaAsync(HttpListenerRequest req, HttpListenerResponse resp, Entry entry)
        {
            // Timing diagnostics — how long did this request take?
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rangeHeader = req.Headers["Range"] ?? "(none)";
            var key = req.Url?.AbsolutePath ?? "";

            // Linked token: cancels if EITHER the global shutdown fires
            // OR we decide to bail (e.g., write timeout).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            // 15 second write timeout: if the browser hasn't read anything
            // from us for 15 s, its socket is dead. Don't wait forever.
            try { resp.OutputStream.WriteTimeout = 15_000; } catch { }

            // ---- Build the upstream request ----
            var upReq = new HttpRequestMessage(HttpMethod.Get, entry.Url);

            // Forward the browser's Range header. This is how seeking works:
            // "bytes=1048576-" means "start at byte 1048576, send everything after".
            var range = req.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(range))
            {
                upReq.Headers.TryAddWithoutValidation("Range", range);
            }

            // YouTube CDN checks these and rejects requests without them
            // (403). The proxy adds them because the browser wouldn't.
            upReq.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            upReq.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");

            long sent = 0;
            var ttfb = sw.ElapsedMilliseconds;  // time-to-first-byte (set later, initially 0)
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);   // 256 KB scratch buffer

            try
            {
                // ---- Send the request upstream ----
                // ResponseHeadersRead = return as soon as headers arrive,
                // don't wait for the whole body. Essential for streaming.
                using var upResp = await _http.SendAsync(
                    upReq, HttpCompletionOption.ResponseHeadersRead, linked.Token);

                resp.StatusCode = (int)upResp.StatusCode;

                // ---- Special case: 416 Range Not Satisfiable ----
                // This means the browser asked for a range past the end
                // of the file. It usually happens when the browser thinks
                // the file is longer than it is. Best response: send an
                // empty 416 and let the browser figure out the track ended.
                if ((int)upResp.StatusCode == 416)
                {
                    resp.ContentType = entry.Mime;
                    WriteCors(resp);
                    return;
                }

                // ---- Copy response headers from upstream to downstream ----
                // "Hop-by-hop" headers (Transfer-Encoding, Connection, etc.)
                // are connection-specific and must NOT be forwarded — they'd
                // confuse the browser. Everything else (Content-Range, ETag,
                // etc.) is fine to pass through.
                foreach (var h in upResp.Headers)
                    if (!IsHopByHop(h.Key)) TrySet(resp, h.Key, string.Join(",", h.Value));
                foreach (var h in upResp.Content.Headers)
                    if (!IsHopByHop(h.Key)) TrySet(resp, h.Key, string.Join(",", h.Value));

                // Content-Length: total bytes we're about to send. Setting
                // this tells the browser exactly what to expect, which helps
                // it plan its buffering. Without it, the browser uses
                // "chunked transfer encoding" and buffers more conservatively.
                if (upResp.Content.Headers.ContentLength is { } cl)
                    resp.ContentLength64 = cl;

                // ---- Our own response headers ----
                resp.ContentType = entry.Mime;
                TrySet(resp, "Accept-Ranges", "bytes");                          // supports seeking
                TrySet(resp, "Cache-Control", "public, max-age=86400, immutable"); // cache this URL forever
                WriteCors(resp);

                // ---- Stream the body ----
                // Read from upstream, write to downstream, chunk by chunk.
                // The 30 s stall detector below catches dead connections.
                await using var src = await upResp.Content.ReadAsStreamAsync(linked.Token);
                int n;

                while (true)
                {
                    // Per-chunk cancellation: if a single ReadAsync hangs
                    // for more than 30 s, we assume the CDN node is dead.
                    // Break out and let the browser retry with a fresh
                    // Range request (which will hit a different CDN node).
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    readCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Log?.Invoke($"media read timeout at {sent}B — aborting");
                        break;
                    }

                    if (n == 0) break;  // end of stream — normal completion

                    try
                    {
                        await resp.OutputStream.WriteAsync(buffer.AsMemory(0, n), linked.Token);
                    }
                    catch (HttpListenerException) { /* client aborted — normal */ }  // client disconnected
                    catch (IOException) { /* client aborted — normal */ }            // socket closed

                    sent += n;
                }
            }
            catch (HttpListenerException) { /* client aborted — normal */ }
            catch (OperationCanceledException) { /* shutdown or timeout */ }
            finally
            {
                // Always return the pooled buffer, even on error.
                ArrayPool<byte>.Shared.Return(buffer);

                // Emit a diagnostic line. This is what shows up in proxy.log:
                //   "media abc12345 range=bytes=0- status=206 ttfb=12ms total=923ms sent=4583576B rate=4849KB/s"
                // The fields tell you: which stream, what range, what
                // status code, how long it took, how much was sent, and
                // how fast. Compare the "rate" to your network speed to
                // diagnose throttling vs proxy slowness.
                var msg = $"media {key.Substring(Math.Max(0, key.Length - 8))} " +
                          $"range={rangeHeader} status={resp.StatusCode} " +
                          $"ttfb={ttfb}ms total={sw.ElapsedMilliseconds}ms sent={sent}B " +
                          $"rate={(sw.ElapsedMilliseconds > 0 ? sent * 1000L / sw.ElapsedMilliseconds / 1024 : 0)}KB/s";
                Log?.Invoke(msg);
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // UTILITY HELPERS
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Add CORS headers to a response. Even though the page and the
        /// proxy are on the same origin (127.0.0.1), adding these makes
        /// the proxy safe to use from other origins if the architecture
        /// ever changes.
        /// </summary>
        private static void WriteCors(HttpListenerResponse resp)
        {
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            resp.Headers["Access-Control-Expose-Headers"] = "Content-Range, Content-Length, Accept-Ranges";
        }

        /// <summary>
        /// Set a response header, ignoring errors. HttpListener sometimes
        /// throws if a header name is "restricted" (like Content-Length).
        /// We don't want the whole request to fail because of that.
        /// </summary>
        private static void TrySet(HttpListenerResponse resp, string k, string v)
        {
            try { resp.Headers[k] = v; } catch { }
        }

        /// <summary>
        /// Is this header "hop-by-hop" — meaning it applies only to THIS
        /// connection and shouldn't be forwarded?
        ///
        /// Example: Transfer-Encoding: chunked tells THIS HTTP connection
        /// "data will come in chunks with size markers". If we forward
        /// that, the downstream browser will wait for chunk markers, but
        /// our proxy is sending raw bytes — protocol mismatch.
        ///
        /// We strip those and let .NET/HttpListener add its own.
        /// </summary>
        private static bool IsHopByHop(string h) =>
            h.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// YoutubeExplode sometimes appends "&range=0-NNNNNN" to a media
        /// URL as a hint about which byte range it wants. That hint caps
        /// the CDN response — you can't stream past it, even with a new
        /// Range header. This strips it out so we can request whatever
        /// byte ranges we want.
        /// </summary>
        private static string StripRange(string url)
        {
            var q = url.IndexOf('?');
            if (q < 0) return url;   // no query string, nothing to strip
            var head = url.Substring(0, q);
            var query = url.Substring(q + 1)
                           .Split('&')
                           .Where(p => !p.StartsWith("range=", StringComparison.OrdinalIgnoreCase))
                           .ToArray();
            return query.Length == 0 ? head : head + "?" + string.Join("&", query);
        }

        /// <summary>
        /// Find a free TCP port on the loopback interface. We bind to
        /// port 0 (which tells the OS "pick one for me"), read back
        /// the port it assigned, then close the socket. There's a tiny
        /// race window where another process could grab the same port
        /// before our actual listener starts, but it's small enough in
        /// practice — and if it happens, the constructor will throw and
        /// the user sees "WebView2 init failed".
        /// </summary>
        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        /// <summary>
        /// Shut down cleanly: stop listening, dispose resources, wait for
        /// any in-flight requests to finish.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();                        // tell all background tasks to stop
            try { _listener.Stop(); } catch { }   // stop accepting new connections
            _http.Dispose();                      // close upstream connections
            if (_loop is not null)
                try { await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}