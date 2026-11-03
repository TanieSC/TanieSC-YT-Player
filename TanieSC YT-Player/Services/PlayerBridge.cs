using Microsoft.Web.WebView2.Core;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanieSC_YT_Player.Services
{
    /// <summary>
    /// A "short list" of every possible state the player can be in.
    /// Think of it like traffic lights: at any moment the player is in
    /// exactly one of these. The JavaScript page sends us strings
    /// ("playing", "paused", etc.) and we translate them to these
    /// strongly-typed values so the C# code can use them safely.
    ///
    /// Why does this exist?
    ///   Without an enum, code would compare strings:
    ///       if (snap.Status == "playing")   // easy to typo
    ///   With an enum, the compiler checks it:
    ///       if (snap.ParsedStatus == PlayerStatus.Playing)  // typo → compile error
    /// </summary>
    public enum PlayerStatus
    {
        Idle,       // nothing loaded
        Loading,    // resolving streams / buffering initially
        Ready,      // loaded, waiting for play
        Playing,    // actively playing
        Paused,     // user paused
        Buffering,  // temporary stall (network hiccup)
        Seeking,    // user scrubbed the timeline
        Ended,      // reached the end
        Error,      // something failed
        Unknown     // JavaScript sent something we don't recognize
    }

    /// <summary>
    /// A complete snapshot of everything the JavaScript player knows about
    /// itself at one moment in time. The page sends this to C# whenever
    /// anything important changes (play state, pitch, worklet status, etc.).
    ///
    /// Think of it like a "report card" the player fills in and hands to us:
    ///   "Here's my current status, position, duration, pitch, and whether
    ///    my audio effects are working."
    ///
    /// The [JsonPropertyName] attributes tell the JSON deserializer the
    /// exact names to look for in the incoming JSON. Without them, C#
    /// would expect "Status" but the JavaScript sends "status" — the
    /// attributes bridge that gap.
    /// </summary>
    public sealed class PlayerSnapshot
    {
        // ── What the player is doing right now ──
        [JsonPropertyName("status")] public string Status { get; set; } = "idle";
        [JsonPropertyName("trackId")] public string? TrackId { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("author")] public string Author { get; set; } = "";

        // ── Timeline ──
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("position")] public double Position { get; set; }
        [JsonPropertyName("buffered")] public double Buffered { get; set; }

        // ── Playback state ──
        [JsonPropertyName("paused")] public bool Paused { get; set; }
        [JsonPropertyName("muted")] public bool Muted { get; set; }

        // ── Low-level media engine numbers (rarely used by the UI) ──
        // readyState: how much data the browser has decoded (0-4)
        // networkState: network activity status (0-3)
        [JsonPropertyName("readyState")] public int ReadyState { get; set; }
        [JsonPropertyName("networkState")] public int NetworkState { get; set; }

        // ── Pitch / tempo settings ──
        [JsonPropertyName("tempo")] public double Tempo { get; set; }
        [JsonPropertyName("pitchSemitones")] public double PitchSemitones { get; set; }
        [JsonPropertyName("pitchScale")] public double PitchScale { get; set; }

        // ── Audio worklet status (the pitch-shifting engine) ──
        // workletReady:  the DSP engine loaded successfully
        // workletFailed: the engine couldn't load (browser too old, or a bug)
        [JsonPropertyName("workletReady")] public bool WorkletReady { get; set; }
        [JsonPropertyName("workletFailed")] public bool WorkletFailed { get; set; }

        // ── Volume ──
        [JsonPropertyName("volume")] public double Volume { get; set; }

        // ── Optional audio-video sync offset (milliseconds) ──
        [JsonPropertyName("audioOffsetMs")] public double AudioOffsetMs { get; set; }

        // ── Error info if something went wrong ──
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }

        // ── Internal timestamp the page uses to track when things changed ──
        [JsonPropertyName("lastChange")] public double LastChange { get; set; }

        /// <summary>
        /// A handy read-only property that turns the string Status into an
        /// enum for safe comparisons. The [JsonIgnore] attribute tells the
        /// JSON deserializer "don't try to read this from the incoming JSON"
        /// — it's computed on demand from Status.
        ///
        /// Example:
        ///   if (snap.ParsedStatus == PlayerStatus.Playing)
        ///       ...
        /// </summary>
        [JsonIgnore]
        public PlayerStatus ParsedStatus => Status switch
        {
            "idle" => PlayerStatus.Idle,
            "loading" => PlayerStatus.Loading,
            "ready" => PlayerStatus.Ready,
            "playing" => PlayerStatus.Playing,
            "paused" => PlayerStatus.Paused,
            "buffering" => PlayerStatus.Buffering,
            "seeking" => PlayerStatus.Seeking,
            "ended" => PlayerStatus.Ended,
            "error" => PlayerStatus.Error,
            _ => PlayerStatus.Unknown
        };
    }

    /// <summary>
    /// Event data for "the playhead moved". Fires about 4 times per second
    /// while a track is loaded. The UI uses this to update the
    /// "t = 12.3 s / 180.0 s" label, and the seek-forward/back buttons
    /// use it to know where "here" is.
    ///
    /// "init" on the properties means they can only be set when the object
    /// is created — this is a tiny immutable snapshot, never mutated.
    /// </summary>
    public sealed class PlayerTimeEventArgs : EventArgs
    {
        public double Position { get; init; }   // current playhead, seconds
        public double Duration { get; init; }   // total length, seconds
        public bool Paused { get; init; }   // true if paused
        public double Buffered { get; init; }   // how many seconds ahead are ready
        public string State { get; init; } = "unknown"; // raw status string
    }

    /// <summary>
    /// Event data for "the player encountered an error". Message is a
    /// human-readable string ("video error 4", "no audio stream", etc.)
    /// suitable for showing in the status bar.
    /// </summary>
    public sealed class PlayerErrorEventArgs : EventArgs
    {
        public string Message { get; init; } = "";
    }

    /// <summary>
    /// The bridge between C# (this WPF app) and JavaScript (the player page
    /// inside WebView2). It's a two-way translator:
    ///
    ///   C# → JS:  you call methods like PlayAsync() or SetPitchAsync().
    ///             They turn into JavaScript calls running in the page.
    ///
    ///   JS → C#:  the page sends messages like { type: "time", position: 12.3 }.
    ///             This class catches them and raises .NET events that
    ///             MainWindow subscribes to.
    ///
    /// Think of it as a walkie-talkie: the C# side presses the button and
    /// speaks a command; the JS side listens and acts on it. The JS side
    /// also speaks back, and this class listens and rebroadcasts to whoever
    /// in C# wants to know.
    ///
    /// IAsyncDisposable is implemented so we can cleanly unsubscribe from
    /// the WebView2 event when the window closes. Without it, WebView2
    /// would hold onto our bridge object forever.
    /// </summary>
    public sealed class PlayerBridge : IAsyncDisposable
    {
        /// <summary>
        /// JSON parser settings used when reading the player's messages.
        /// PropertyNameCaseInsensitive: "Status" and "status" both work
        ///   (JavaScript uses lowercase, C# conventions use PascalCase).
        /// NumberHandling.AllowReadingFromString: some JSON serializers
        ///   emit numbers as strings ("12.3" instead of 12.3); this accepts
        ///   both forms without extra work.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        /// <summary>The underlying WebView2 "engine" object we talk to.</summary>
        private readonly CoreWebView2 _core;

        /// <summary>
        /// True once the page has signalled it's ready to receive commands.
        /// MainWindow checks this before trying to load a track, so the user
        /// sees "Player initialising" instead of a cryptic error if they
        /// click too fast after launch.
        /// </summary>
        private bool _ready;

        public bool IsReady => _ready;

        // ─────────────────────────────────────────────────────────────────
        // EVENTS — main window subscribes to these to react to the player
        // ─────────────────────────────────────────────────────────────────

        /// <summary>Fires once when the player page has finished loading.</summary>
        public event EventHandler? Ready;

        /// <summary>Fires whenever the status changes (play/pause/buffering/etc.).</summary>
        public event EventHandler<PlayerSnapshot>? StateChanged;

        /// <summary>Fires ~4× per second with the current playhead position.</summary>
        public event EventHandler<PlayerTimeEventArgs>? TimeChanged;

        /// <summary>Fires when the page reports a fatal error.</summary>
        public event EventHandler<PlayerErrorEventArgs>? ErrorOccurred;

        /// <summary>Fires for debug messages (like "worklet loaded").</summary>
        public event EventHandler<string>? LogMessage;

        // ─────────────────────────────────────────────────────────────────
        // CONSTRUCTOR
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wire up the message receiver. After this, any message the page
        /// posts via `window.chrome.webview.postMessage(...)` will land
        /// in OnWebMessageReceived.
        /// </summary>
        public PlayerBridge(CoreWebView2 core)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));
            _core.WebMessageReceived += OnWebMessageReceived;
        }

        // ─────────────────────────────────────────────────────────────────
        // INCOMING MESSAGES (JS → C#)
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every message from the page lands here. It's always a JSON
        /// string with a "type" field that tells us what kind of message
        /// it is:
        ///
        ///   { "type": "ready" }                                     page loaded
        ///   { "type": "state", "state": { ... } }                   status changed
        ///   { "type": "time", "position": 12.3, "duration": 180 }   playhead moved
        ///   { "type": "error", "message": "..." }                   something failed
        ///   { "type": "log", "message": "..." }                     debug info
        ///
        /// We parse it, dispatch to the right event, and swallow any
        /// parsing errors so a single bad message can't crash the app.
        /// </summary>
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // Read the raw string. If it isn't a string (WebView2 could
            // hand us a JSON object in some API versions), silently ignore.
            string json;
            try { json = e.TryGetWebMessageAsString(); } catch { return; }

            try
            {
                // JsonDocument.Parse gives us a read-only tree we can poke
                // at with TryGetProperty. Using `using` ensures it's freed.
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Every message must have a "type" — if not, we can't do
                // anything with it. Bail out silently.
                if (!root.TryGetProperty("type", out var typeEl)) return;

                // Dispatch based on the type.
                switch (typeEl.GetString())
                {
                    case "ready":
                        // The page finished loading. Remember that so
                        // MainWindow can start sending commands.
                        _ready = true;
                        Ready?.Invoke(this, EventArgs.Empty);
                        break;

                    case "state":
                        // The player's full status changed. Extract the
                        // nested "state" object, deserialize it into a
                        // PlayerSnapshot, and raise the event.
                        if (root.TryGetProperty("state", out var s))
                        {
                            var snap = JsonSerializer.Deserialize<PlayerSnapshot>(
                                s.GetRawText(), JsonOpts) ?? new PlayerSnapshot();
                            StateChanged?.Invoke(this, snap);
                        }
                        break;

                    case "time":
                        // The playhead moved. Build a lightweight event
                        // with just the fields the UI needs.
                        TimeChanged?.Invoke(this, new PlayerTimeEventArgs
                        {
                            Position = GetDouble(root, "position"),
                            Duration = GetDouble(root, "duration"),
                            Paused = GetBool(root, "paused"),
                            Buffered = GetDouble(root, "buffered"),
                            State = GetString(root, "state") ?? "unknown"
                        });
                        break;

                    case "error":
                        // The page hit a fatal error — report it.
                        ErrorOccurred?.Invoke(this, new PlayerErrorEventArgs
                        {
                            Message = GetString(root, "message") ?? ""
                        });
                        break;

                    case "log":
                        // Debug/diagnostic message. MainWindow writes these
                        // to player.log for later inspection.
                        LogMessage?.Invoke(this, GetString(root, "message") ?? "");
                        break;
                }
            }
            catch { /* malformed message — ignore silently */ }
        }

        // ─────────────────────────────────────────────────────────────────
        // OUTGOING COMMANDS (C# → JS)
        //
        // Every method below turns into one JavaScript call inside the page.
        // The "window.ytp && ..." prefix is a safety guard: if the page
        // hasn't loaded window.ytp yet, the call quietly does nothing
        // instead of throwing a "cannot read property of undefined" error.
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tells the page to load and play a track.
        ///
        /// The payload (URLs, title, etc.) is built as an anonymous object,
        /// then serialized to JSON, then embedded directly into a JavaScript
        /// call. Example resulting script:
        ///
        ///   window.ytp && window.ytp.load({
        ///     "trackId": "abc123",
        ///     "videoUrl": "http://127.0.0.1:.../s/xyz",
        ///     "audioUrl": "http://127.0.0.1:.../s/xyz",
        ///     "title": "Song Title",
        ///     "author": "Artist"
        ///   });
        ///
        /// JSON is valid JavaScript, so we can splice the JSON string right
        /// into the script without extra escaping.
        /// </summary>
        public Task LoadAsync(
            string videoUrl,
            string audioUrl,
            string? trackId = null,
            string? title = null,
            string? author = null)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                throw new ArgumentException("videoUrl is required", nameof(videoUrl));
            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new ArgumentException("audioUrl is required", nameof(audioUrl));

            // Build the payload as JSON. Anonymous objects serialize cleanly
            // to JSON with property names matching the C# names.
            var payloadJson = JsonSerializer.Serialize(new
            {
                trackId = trackId ?? "",
                videoUrl = videoUrl,
                audioUrl = audioUrl,
                title = title ?? "",
                author = author ?? ""
            });

            // Send the whole call to the page.
            return ExecuteAsync($"window.ytp && window.ytp.load({payloadJson});");
        }

        // ── Simple command wrappers ──
        // Each one sends a single JavaScript call. The player page defines
        // these methods in window.ytp.
        public Task TogglePlayAsync() => ExecuteAsync("window.ytp && window.ytp.togglePlay();");
        public Task PlayAsync() => ExecuteAsync("window.ytp && window.ytp.play();");
        public Task PauseAsync() => ExecuteAsync("window.ytp && window.ytp.pause();");
        public Task StopAsync() => ExecuteAsync("window.ytp && window.ytp.stop();");

        /// <summary>Jump the playhead to a specific time (in seconds).</summary>
        public Task SeekAsync(double seconds)
            => ExecuteAsync($"window.ytp && window.ytp.seek({Num(seconds)});");

        /// <summary>Change the playback speed without changing pitch.</summary>
        public Task SetTempoAsync(double tempo)
            => ExecuteAsync($"window.ytp && window.ytp.setTempo({Num(tempo)});");

        /// <summary>
        /// Change the pitch. `semitones` is the coarse shift (−12..+12),
        /// `fineScale` is a fine multiplier (0.5..2.0). The page combines
        /// them into a single pitch ratio for the audio worklet.
        /// </summary>
        public Task SetPitchAsync(double semitones, double fineScale)
            => ExecuteAsync($"window.ytp && window.ytp.setPitch({Num(semitones)}, {Num(fineScale)});");

        /// <summary>Set volume 0.0–1.0 (0 = silent, 1 = full).</summary>
        public Task SetVolumeAsync(double volume0to1)
            => ExecuteAsync($"window.ytp && window.ytp.setVolume({Num(volume0to1)});");

        /// <summary>
        /// Nudge the audio slightly ahead or behind the video, to correct
        /// lip-sync drift caused by the DSP worklet's processing delay.
        /// Positive = audio later, negative = audio earlier. Range ±2000 ms.
        /// </summary>
        public Task SetAudioOffsetAsync(double milliseconds)
            => ExecuteAsync($"window.ytp && window.ytp.setAudioOffset({Num(milliseconds)});");

        /// <summary>
        /// Ask the page for its current state and wait for the answer.
        /// Unlike the other methods, this one is asynchronous AND returns
        /// a value. Useful when you need to know the exact state right
        /// now, rather than reacting to an event.
        ///
        /// How it works:
        ///   1. ExecuteScriptAsync runs "JSON.stringify(...)" in the page.
        ///   2. The script result is a string (the JSON text).
        ///   3. But WebView2 wraps that string in another layer of JSON
        ///      quoting, so we have to unwrap twice:
        ///         raw          → "\"{ ... }\""
        ///         inner        → "{ ... }"
        ///         PlayerSnapshot
        /// </summary>
        public async Task<PlayerSnapshot?> GetStateAsync()
        {
            // Run "return JSON.stringify(state)" in the page. The result
            // is a string containing JSON text.
            var raw = await _core.ExecuteScriptAsync(
                "window.ytp ? JSON.stringify(window.ytp.getState()) : null");

            if (string.IsNullOrEmpty(raw) || raw == "null") return null;

            // Unwrap layer 1: the surrounding JSON string quotes.
            string inner;
            try { inner = JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch { return null; }

            if (string.IsNullOrEmpty(inner)) return null;

            // Unwrap layer 2: parse the actual state JSON into our object.
            try { return JsonSerializer.Deserialize<PlayerSnapshot>(inner, JsonOpts); }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────────
        // INTERNAL HELPERS
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Runs a JavaScript snippet in the page. If the page is mid-navigation
        /// or the WebView has been disposed, the call throws — we swallow that
        /// because it's not actionable. The user will just retry.
        /// </summary>
        private async Task ExecuteAsync(string script)
        {
            try { await _core.ExecuteScriptAsync(script); }
            catch { /* page navigating / disposed */ }
        }

        /// <summary>
        /// Formats a double for safe embedding in a JavaScript call.
        /// "R" = round-trip format (guarantees no precision loss when
        /// re-parsed). InvariantCulture = uses "." as decimal separator
        /// regardless of the user's locale, which matters because JavaScript
        /// can't parse a comma-decimal like "1,5".
        /// </summary>
        private static string Num(double v)
            => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        // ── Small JSON helpers ──
        // Each of these returns a safe default if the property is missing
        // or has the wrong type. Avoids NullReferenceException surprises.

        private static double GetDouble(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
               ? p.GetDouble() : 0;

        private static bool GetBool(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) &&
               (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
               ? p.GetBoolean() : false;

        private static string? GetString(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
               ? p.GetString() : null;

        /// <summary>
        /// Clean up when the app closes. We stop listening for messages so
        /// WebView2 doesn't keep this object alive after the window is gone.
        /// (Not unsubscribing would leak memory across app restarts and,
        /// in this app's case, cause duplicate event firings if a new bridge
        /// were created.)
        /// </summary>
        public ValueTask DisposeAsync()
        {
            try { _core.WebMessageReceived -= OnWebMessageReceived; } catch { }
            return ValueTask.CompletedTask;
        }
    }
}