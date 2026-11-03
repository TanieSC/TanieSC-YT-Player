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
        /// </summary>
        private readonly record struct Entry(string Url, string Mime);

        /// <summary>
        /// The dictionary that maps proxy keys → upstream URLs.
        /// Thread-safe because multiple browser connections arrive in
        /// parallel.
        /// </summary>
        private readonly ConcurrentDictionary<string, Entry> _entries = new();

        /// <summary>The actual mini-web-server.</summary>
        private readonly HttpListener _listener;

        /// <summary>
        /// HttpClient shared across all requests. Configured with a real
        /// Chrome user-agent because YouTube throttles the default .NET one.
        /// </summary>
        private readonly HttpClient _http;

        /// <summary>Global cancellation token: shutdown signal.</summary>
        private readonly CancellationTokenSource _cts = new();

        /// <summary>Full path to the wwwroot folder.</summary>
        private readonly string _assetsRoot;

        /// <summary>Diagnostic log sink.</summary>
        public event Action<string>? Log;

        /// <summary>Background accept-loop task handle.</summary>
        private Task? _loop;

        /// <summary>Base URL the WebView2 navigates to.</summary>
        public string BaseUrl { get; }

        // ─────────────────────────────────────────────────────────────────
        // CONSTRUCTOR
        // ─────────────────────────────────────────────────────────────────

        public StreamProxy(string assetsRoot)
        {
            _assetsRoot = assetsRoot ?? throw new ArgumentNullException(nameof(assetsRoot));

            int port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";

            _listener = new HttpListener();
            _listener.Prefixes.Add($"{BaseUrl}/");

            // Tuned for media streaming:
            //   MaxConnectionsPerServer = 32 gives plenty of headroom for
            //     a browser pulling 2-6 ranges per stream plus preloads.
            //   EnableMultipleHttp2Connections = true lets new streams
            //     multiplex over an existing TLS session rather than wait
            //     behind an in-flight request.
            //   PooledConnectionLifetime / IdleTimeout keep stale sockets
            //     from accumulating when the CDN closes them silently.
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                AllowAutoRedirect = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 32,
                EnableMultipleHttp2Connections = true
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
        }

        // ─────────────────────────────────────────────────────────────────
        // START
        // ─────────────────────────────────────────────────────────────────

        public void Start()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        // ─────────────────────────────────────────────────────────────────
        // REGISTER / UNREGISTER
        // ─────────────────────────────────────────────────────────────────

        public string Register(string upstreamUrl, string mime)
        {
            var key = Guid.NewGuid().ToString("N");
            _entries[key] = new Entry(StripRange(upstreamUrl), mime);
            return $"{BaseUrl}/s/{key}";
        }

        public void Unregister(string proxyUrl)
        {
            var idx = proxyUrl.LastIndexOf('/');
            if (idx < 0) return;
            _entries.TryRemove(proxyUrl[(idx + 1)..], out _);
        }

        // ─────────────────────────────────────────────────────────────────
        // ACCEPT LOOP
        // ─────────────────────────────────────────────────────────────────

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            try
            {
                if (req.HttpMethod == "OPTIONS")
                {
                    resp.StatusCode = 204;
                    WriteCors(resp);
                    resp.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                    resp.Headers["Access-Control-Allow-Headers"] = "Range, Accept, Accept-Language";
                    resp.Headers["Access-Control-Max-Age"] = "86400";
                    resp.Close();
                    return;
                }

                var path = req.Url?.AbsolutePath ?? "/";

                if (path.StartsWith("/s/", StringComparison.Ordinal))
                {
                    var key = path.Substring(3);
                    if (!_entries.TryGetValue(key, out var entry))
                    {
                        resp.StatusCode = 404;
                        resp.Close();
                        return;
                    }
                    await ServeMediaAsync(req, resp, entry);
                    return;
                }

                await ServeStaticAsync(req, resp, path);
            }
            catch
            {
                try { resp.StatusCode = 502; } catch { }
            }
            finally
            {
                try { resp.Close(); } catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // STATIC FILE SERVING
        // ─────────────────────────────────────────────────────────────────

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
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".gif"] = "image/gif",
                [".webp"] = "image/webp",
                [".svg"] = "image/svg+xml",
            };

        private async Task ServeStaticAsync(HttpListenerRequest req, HttpListenerResponse resp, string urlPath)
        {
            if (urlPath == "/" || urlPath.Length == 0) urlPath = "/index.html";

            if (urlPath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 204;
                resp.ContentLength64 = 0;
                return;
            }

            if (urlPath.Contains("..", StringComparison.Ordinal))
            {
                resp.StatusCode = 400;
                return;
            }

            var relPath = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(_assetsRoot, relPath));

            var rootFull = Path.GetFullPath(_assetsRoot);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                resp.StatusCode = 404;
                return;
            }

            var ext = Path.GetExtension(full);
            resp.ContentType = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";

            // Static assets change on every build. Never let the WebView2
            // heuristic-cache them; otherwise edits to yt_app.js don't reach
            // the running page until the user-data folder is deleted.
            TrySet(resp, "Cache-Control", "no-cache, no-store, must-revalidate");
            TrySet(resp, "Pragma", "no-cache");
            TrySet(resp, "Expires", "0");

            if (req.HttpMethod == "HEAD")
            {
                resp.ContentLength64 = new FileInfo(full).Length;
                return;
            }

            await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                                                FileShare.Read, 81920, useAsync: true);
            resp.ContentLength64 = fs.Length;
            await fs.CopyToAsync(resp.OutputStream, 81920, _cts.Token);
        }

        // ─────────────────────────────────────────────────────────────────
        // MEDIA PROXYING
        // ─────────────────────────────────────────────────────────────────

        private async Task ServeMediaAsync(HttpListenerRequest req, HttpListenerResponse resp, Entry entry)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rangeHeader = req.Headers["Range"] ?? "(none)";
            var key = req.Url?.AbsolutePath ?? "";

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            try { resp.OutputStream.WriteTimeout = 15_000; } catch { }

            var upReq = new HttpRequestMessage(HttpMethod.Get, entry.Url);

            var range = req.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(range))
                upReq.Headers.TryAddWithoutValidation("Range", range);

            upReq.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            upReq.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");

            long sent = 0;
            long ttfb = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);

            try
            {
                using var upResp = await _http.SendAsync(
                    upReq, HttpCompletionOption.ResponseHeadersRead, linked.Token);

                // TTFB is the time to response HEADERS (not first body byte).
                // Measured here, after SendAsync returns, before any reads.
                ttfb = sw.ElapsedMilliseconds;

                resp.StatusCode = (int)upResp.StatusCode;

                // Copy headers FIRST. This preserves Content-Range on 206
                // responses and the required Content-Range on 416 responses.
                foreach (var h in upResp.Headers)
                    if (!IsHopByHop(h.Key)) TrySet(resp, h.Key, string.Join(",", h.Value));
                foreach (var h in upResp.Content.Headers)
                    if (!IsHopByHop(h.Key)) TrySet(resp, h.Key, string.Join(",", h.Value));

                resp.ContentType = entry.Mime;
                TrySet(resp, "Accept-Ranges", "bytes");
                TrySet(resp, "Cache-Control", "public, max-age=86400, immutable");
                WriteCors(resp);

                // 416 Range Not Satisfiable. The browser asked for a range
                // past the end of the file (usually because it thinks the
                // file is longer than it is). Reply with an empty body.
                // Headers — notably Content-Range: bytes */N — were already
                // copied above, so the browser knows the true length.
                if ((int)upResp.StatusCode == 416)
                {
                    resp.ContentLength64 = 0;
                    return;
                }

                // Preserve Content-Length so Chromium can plan buffering
                // instead of using chunked transfer.
                if (upResp.Content.Headers.ContentLength is { } cl)
                    resp.ContentLength64 = cl;

                // HEAD: headers only, no body. Rare for media but correct.
                if (req.HttpMethod == "HEAD")
                    return;

                // Stream the body.
                await using var src = await upResp.Content.ReadAsStreamAsync(linked.Token);
                int n;

                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    readCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Log?.Invoke($"media {key} read timeout at {sent}B — aborting");
                        break;
                    }

                    if (n == 0) break;   // end of stream

                    try
                    {
                        await resp.OutputStream.WriteAsync(buffer.AsMemory(0, n), linked.Token);
                    }
                    catch (HttpListenerException)
                    {
                        // Client disconnected (seek, close, track switch).
                        // Cancel upstream so the read aborts and upResp
                        // disposes promptly — this is what keeps pool
                        // slots from being held by dead requests.
                        linked.Cancel();
                        Log?.Invoke($"media {key} client aborted at {sent}B");
                        break;
                    }
                    catch (IOException)
                    {
                        linked.Cancel();
                        Log?.Invoke($"media {key} socket closed at {sent}B");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown or explicit cancel during write.
                        break;
                    }

                    sent += n;
                }
            }
            catch (HttpListenerException) { /* response already closed */ }
            catch (OperationCanceledException) { /* shutdown / abort */ }
            catch (Exception ex)
            {
                Log?.Invoke($"media {key} error: {ex.Message}");
                try { resp.StatusCode = 502; } catch { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

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

        private static void WriteCors(HttpListenerResponse resp)
        {
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            resp.Headers["Access-Control-Expose-Headers"] = "Content-Range, Content-Length, Accept-Ranges";
        }

        private static void TrySet(HttpListenerResponse resp, string k, string v)
        {
            try { resp.Headers[k] = v; } catch { }
        }

        private static bool IsHopByHop(string h) =>
            h.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Content-Length", StringComparison.OrdinalIgnoreCase);

        private static string StripRange(string url)
        {
            var q = url.IndexOf('?');
            if (q < 0) return url;
            var head = url.Substring(0, q);
            var query = url.Substring(q + 1)
                           .Split('&')
                           .Where(p => !p.StartsWith("range=", StringComparison.OrdinalIgnoreCase))
                           .ToArray();
            return query.Length == 0 ? head : head + "?" + string.Join("&", query);
        }

        private static int GetFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _http.Dispose();
            if (_loop is not null)
                try { await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}