/* =========================================================================
   PLAYER PAGE SCRIPT (yt-app.js)
   =========================================================================

   WHAT THIS FILE IS:
     The JavaScript "brain" of the player page that runs inside WebView2.
     The C# app (MainWindow) can't play YouTube videos directly, so it
     hosts a small embedded browser (WebView2) that loads this page.
     This file does two jobs:

       1. RECEIVES COMMANDS from C# (play, pause, seek, change pitch, etc.)
          via the `window.ytp.*` API defined at the bottom.

       2. REPORTS BACK to C# (state changes, playhead position, errors,
          log messages) via `window.chrome.webview.postMessage(...)`.

   ARCHITECTURE (non-MSE mode):
     YouTube streams are split into a video-only track and an audio-only
     track. This page plays them in two elements simultaneously:
        <video>  → plays the picture (muted, since audio is separate)
        <audio>  → plays the sound through a Web Audio graph

     The Web Audio graph routes the audio through an AudioWorklet
     (pitch-shifter-worklet-edit.js) that runs the SoundTouch DSP,
     enabling independent pitch shifting while the <audio> element's
     .playbackRate handles tempo natively.

   THE MESSAGE PROTOCOL (JSON objects sent to C#):
     { type: "ready" }                         page loaded
     { type: "state",  state: {...} }          status changed
     { type: "time",   position, duration, ... } playhead moved
     { type: "error",  message }               something failed
     { type: "log",    message }               debug info
   ========================================================================= */

(function () {
    'use strict';
    // "use strict" enables modern JavaScript error-checking (catches
    // typos, silent failures, dangerous patterns). Doesn't change
    // behavior for correct code, but makes broken code fail loudly.

    // =====================================================================
    // PLAYER STATE CONSTANTS
    // =====================================================================
    // The possible states the player can be in. Think of these like a
    // traffic light — at any moment the player is in exactly ONE state.
    // Both this JS file and the C# side track the current state so they
    // can react to changes (e.g., "show spinner when buffering").
    var PLAYER_STATE = {
        IDLE: 'idle',           // nothing loaded
        LOADING: 'loading',     // resolving/starting a track
        READY: 'ready',         // loaded, waiting for play
        PLAYING: 'playing',     // actively playing
        PAUSED: 'paused',       // user paused
        BUFFERING: 'buffering', // temporary stall (network hiccup)
        SEEKING: 'seeking',     // user scrubbed the timeline
        ENDED: 'ended',         // reached the end
        ERROR: 'error'          // something failed
    };

    // =====================================================================
    // PLAYER STATE — the single source of truth
    // =====================================================================
    // This object holds ALL the mutable state the page tracks. Every
    // property here is reported to C# in snapshots (see snapshot() below).
    // Keeping it all in one place makes it easy to save/restore and
    // to serialize for messaging.
    var playerState = {
        status: PLAYER_STATE.IDLE,
        trackId: null,           // YouTube video ID of the current track
        title: '', author: '',   // display info
        duration: 0, position: 0,
        paused: true, muted: false,
        readyState: 0, networkState: 0, buffered: 0,
        tempo: 1.0,              // 1.0 = normal speed
        pitchSemitones: 0,       // -12..+12 semitones (coarse)
        pitchScale: 1.0,         // 0.5..2.0 ratio (fine)
        volume: 1.0,             // 0.0..1.0
        audioOffsetMs: 0,        // reserved (not currently used)
        errorMessage: null,
        lastChange: 0            // timestamp of last state change
    };

    // =====================================================================
    // DOM ELEMENT REFERENCES
    // =====================================================================
    // Cached references to the two media elements. Populated by init().
    // Caching avoids repeated lookups — document.getElementById is slow
    // if called thousands of times per second.
    var videoEl = null, audioEl = null;

    // Handle for the setInterval that polls playhead position.
    // Stored here so it can be cancelled on page unload.
    var timePollerId = null;

    // =====================================================================
    // WEB AUDIO GRAPH STATE
    // =====================================================================
    // These variables track the pieces of the audio-processing pipeline:
    //
    //   <audio> element → mediaSrc → pitchNode (worklet) → speakers
    //
    // If any of these is null, the audio either plays directly through
    // the element (bypassing the graph) or pitch shifting is unavailable.
    var audioCtx      = null;      // the AudioContext (the "engine")
    var mediaSrc      = null;      // wraps the <audio> element
    var pitchNode     = null;      // the AudioWorklet node (does the DSP)
    var workletReady  = false;     // true once the pitch-shifter loaded
    var workletFailed = false;     // true if we know pitch shift won't work

    // =====================================================================
    // HELPER FUNCTIONS
    // =====================================================================

    /**
     * Send a message to the C# host via WebView2's message channel.
     * The C# side (PlayerBridge.cs) subscribes to WebView2's
     * WebMessageReceived event and will dispatch this to the right
     * handler based on the "type" field.
     *
     * Wrapped in try/catch because the channel doesn't exist when the
     * page is loaded in a normal browser (for testing) — in that case
     * the message is just dropped.
     */
    function post(obj) {
        try {
            if (window.chrome && window.chrome.webview)
                window.chrome.webview.postMessage(JSON.stringify(obj));
        } catch (_) {}
    }

    /** Send a debug/info log message. Shows up in player.log on the C# side. */
    function log(m)  { post({ type: 'log',   message: m }); }

    /** Send a fatal error message. Shows up as a dialog / status message. */
    function fail(m) { post({ type: 'error', message: m }); }

    /**
     * Build a JSON-friendly copy of the current player state.
     * Called whenever the state changes and whenever C# asks for it.
     *
     * We build this rather than send playerState directly because:
     *   • playerState might contain non-serializable fields in the future.
     *   • We want to control exactly what's exposed — the workletReady
     *     and workletFailed flags, for example, are informational only
     *     and shouldn't be settable from outside.
     */
    function snapshot() {
        return {
            status: playerState.status, trackId: playerState.trackId,
            title: playerState.title, author: playerState.author,
            duration: playerState.duration, position: playerState.position,
            paused: playerState.paused, muted: playerState.muted,
            readyState: playerState.readyState, networkState: playerState.networkState,
            buffered: playerState.buffered, tempo: playerState.tempo,
            pitchSemitones: playerState.pitchSemitones,
            pitchScale: playerState.pitchScale,
            volume: playerState.volume, audioOffsetMs: playerState.audioOffsetMs,
            workletReady:  workletReady,
            workletFailed: workletFailed,
            errorMessage: playerState.errorMessage, lastChange: playerState.lastChange
        };
    }

    /**
     * Change the player's state and notify the C# host.
     *
     * @param next  the new state (from PLAYER_STATE constants)
     * @param extra optional object of additional properties to set
     *              on playerState at the same time (e.g., { paused: true })
     *
     * Optimization: if the state is already `next` and no extra
     * properties were provided, do nothing — avoids spamming C# with
     * duplicate messages.
     */
    function setState(next, extra) {
        if (playerState.status === next && !extra) return;
        playerState.status = next;
        playerState.lastChange = performance.now();
        if (extra) for (var k in extra)
            if (Object.prototype.hasOwnProperty.call(extra, k))
                playerState[k] = extra[k];
        post({ type: 'state', state: snapshot() });
    }

    /** Show or hide the "nothing playing" splash screen. */
    function setPlaceholderVisible(v) {
        var el = document.getElementById('placeholder');
        if (el) v ? el.classList.remove('hidden') : el.classList.add('hidden');
    }

    /** Update the bottom-of-screen title/subtitle overlay. */
    function setHud(title, sub) {
        var t = document.getElementById('title');
        var s = document.getElementById('sub');
        if (t) t.textContent = title || '';
        if (s) s.textContent = sub   || '';
    }

    /**
     * Show a small popup notification that fades in for a few seconds.
     * Creates the element lazily if it doesn't exist in the HTML yet.
     * Reusing a single element means overlapping toast calls just
     * replace the message rather than stacking.
     */
    var toastEl = null, toastTimer = 0;
    function toast(msg, ms) {
        ms = ms || 2600;
        if (!toastEl) {
            toastEl = document.getElementById('toast') || document.createElement('div');
            if (!toastEl.id) toastEl.id = 'toast';
            if (!toastEl.parentNode) document.body.appendChild(toastEl);
        }
        toastEl.textContent = msg;
        toastEl.classList.add('show');
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { toastEl.classList.remove('show'); }, ms);
    }

    // =====================================================================
    // WEB AUDIO GRAPH SETUP
    // =====================================================================
    // This is what enables pitch shifting. The <audio> element alone can
    // only change tempo (via playbackRate, with preservesPitch = true).
    // To shift pitch independently, we route the element's output through
    // a Web Audio graph containing an AudioWorklet that runs SoundTouch.
    //
    // THE FLOW:
    //     <audio> ──► MediaElementSource ──► pitchNode ──► destination
    //                                         (worklet)
    //
    // FAILURE MODES (all handled):
    //   • Web Audio not available (old browser)
    //   • AudioWorklet not available (insecure origin or old browser)
    //   • Worklet file fails to load (404 or JS error)
    //   • Worklet init fails (bad processor code)
    //
    // In any failure case, we set workletFailed = true and let the
    // <audio> element play directly (audio works, pitch shift is disabled).
    // The user sees a message but can still enjoy the player.
    function setupAudioGraph() {
        if (!audioEl) return;

        // Keep tempo working even if Web Audio never comes up.
        // "preservesPitch" tells the <audio> element to time-stretch
        // when playbackRate ≠ 1, rather than pitching up/down like a tape.
        audioEl.preservesPitch = true;

        // ---- Create the AudioContext ----
        // The "context" is the audio engine — think of it as the mixing
        // board that connects sources to speakers. Only one is needed
        // per page.
        try {
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
            log('AudioContext created, state=' + audioCtx.state);
        } catch (e) {
            log('Web Audio unavailable: ' + e.message);
            audioCtx = null;
            workletFailed = true;    // no graph ⇒ no pitch shift
            post({ type: 'state', state: snapshot() });
            return;                  // <audio> stays in the direct-playback path
        }

        // Try an immediate resume – harmless if it fails. Browsers start
        // AudioContexts in "suspended" state until a user gesture (or a
        // launch flag, which our WebView2 sets). If already running, no-op.
        audioCtx.resume().catch(function () {});

        // ---- Check if AudioWorklet is available ----
        // AudioWorklet requires a "secure context" (https or 127.0.0.1).
        // Our page is served from http://127.0.0.1 via the proxy, so it
        // IS secure. But some edge cases exist (older Chromium, insecure
        // iframe), so we check.
        if (!audioCtx.audioWorklet) {
            workletFailed = true;
            log('AudioWorklet unavailable — tempo only, pitch shift disabled');
            post({ type: 'state', state: snapshot() });
            return;
        }

        // ---- Load the pitch-shifter worklet ----
        // addModule() fetches the worklet JS file and registers the
        // processor named 'pitch-shifter'. The .then() runs when it's ready,
        // .catch() runs if loading failed.
        audioCtx.audioWorklet
            .addModule('/pitch-shifter-worklet-edit.js')
            .then(function () {
                // The processor is registered. Create a node instance
                // that uses it. Every AudioWorkletNode instance runs its
                // own copy of the processor.
                pitchNode = new AudioWorkletNode(audioCtx, 'pitch-shifter', {
                    numberOfInputs:  1,
                    numberOfOutputs: 1,
                    outputChannelCount: [2]   // stereo output
                });
                // Default pitch = 1.0 (no shift)
                pitchNode.parameters.get('pitch').value = 1.0;

                // ---- Capture the <audio> element into the graph ----
                // This is a ONE-WAY operation: once an element is captured
                // with createMediaElementSource, its output goes through
                // the graph forever. If we do this before the worklet is
                // ready, and the worklet fails, audio goes silent. That's
                // why we only call this inside the success path.
                mediaSrc = audioCtx.createMediaElementSource(audioEl);
                mediaSrc.connect(pitchNode);
                pitchNode.connect(audioCtx.destination);

                workletReady = true;
                applyPitch();   // apply whatever pitch value is stored

                log('pitch-shifter ready, ctx=' + audioCtx.state);
                post({ type: 'state', state: snapshot() });

                // Resume again in case the first resume was before the
                // graph was connected.
                audioCtx.resume()
                    .then(function () { log('ctx resumed, state=' + audioCtx.state); })
                    .catch(function (e) { log('ctx resume failed: ' + e.message); });
            })
            .catch(function (err) {
                workletFailed = true;
                log('worklet load failed: ' + err.message +
                    ' — audio plays directly through <audio> (no pitch shift)');
                post({ type: 'state', state: snapshot() });
                // IMPORTANT: do NOT call createMediaElementSource here,
                // because that would capture the element and silence it
                // with no worklet to route the audio to.
            });
    }

    /**
     * Push the current pitch setting to the AudioWorklet.
     * The worklet's `pitch` parameter takes a RATIO (1.0 = no change,
     * 2.0 = octave up, 0.5 = octave down).
     *
     * We combine two settings into one ratio:
     *   pitchSemitones  →  semitone shift, converted to ratio via 2^(n/12)
     *   pitchScale      →  fine adjustment (e.g., 1.02 for slight detune)
     *
     * The worklet doesn't know or care which part of the ratio came from
     * which source — it just sees the final product.
     */
    function applyPitch() {
        if (!workletReady || !pitchNode) return;
        var ratio = Math.pow(2, playerState.pitchSemitones / 12)
                    * playerState.pitchScale;
        try {
            pitchNode.parameters.get('pitch').value = ratio;
        } catch (_) {}
    }

    /**
     * Push the current tempo to the media elements.
     *
     * Tempo is handled NATIVELY (not by the worklet) because the browser
     * has an optimized time-stretcher built in (activated via
     * preservesPitch = true). It's simpler, faster, and unaffected by
     * worklet failures.
     *
     * We set playbackRate on BOTH elements so video and audio stay in
     * sync. Even if the video is silent, its playbackRate needs to match
     * or the picture will lag the sound.
     */
    function applyTempo() {
        if (!audioEl) return;
        // Clamp to browser-supported range. Chrome allows 0.0625 to 16.
        audioEl.playbackRate = Math.max(0.0625, Math.min(16, playerState.tempo));
        try { videoEl.playbackRate = audioEl.playbackRate; } catch (_) {}
    }

    /** Update the placeholder's hint text at runtime. */
    function setPlaceholderText(text) {
        var el = document.querySelector('#placeholder .ph-text');
        if (el) el.textContent = text;
    }

    // =====================================================================
    // INITIALIZATION
    // =====================================================================
    // Called once when the page loads. Wires up all event listeners and
    // starts the audio graph.
    function init() {

        setPlaceholderText('Pick a favorite or search for a track');

        // ---- Cache element references ----
        videoEl = document.getElementById('video');
        audioEl = document.getElementById('audio');

        // ---- Media event diagnostics ----
        // Log every time the media engine transitions state. Useful for
        // diagnosing buffering ("why does it keep stalling?") — you can
        // see the exact moments when buffers drain or ready state drops.
        //
        // These events fire on BOTH elements (video and audio) because
        // they can stall independently. The logs are prefixed with
        // "video." or "audio." so you can tell them apart.
        ['waiting','stalled','suspend','abort','emptied'].forEach(function (name) {
            videoEl.addEventListener(name, function () {
                log('video.' + name + ' t=' + videoEl.currentTime.toFixed(2) +
                    ' bufEnd=' + (videoEl.buffered.length
                        ? videoEl.buffered.end(videoEl.buffered.length - 1).toFixed(2)
                        : '0') +
                    ' ready=' + videoEl.readyState);
            });
            audioEl.addEventListener(name, function () {
                log('audio.' + name + ' t=' + audioEl.currentTime.toFixed(2) +
                    ' bufEnd=' + (audioEl.buffered.length
                        ? audioEl.buffered.end(audioEl.buffered.length - 1).toFixed(2)
                        : '0') +
                    ' ready=' + audioEl.readyState);
            });
        });

        // ---- Build the audio graph (pitch shift engine) ----
        setupAudioGraph();

        // ---- Wire up video events to the state machine ----
        // The video element acts as the "master clock" — its events drive
        // the player state. The audio element mirrors video's play/pause
        // transitions in each handler below.

        // Metadata loaded (duration known, but no frames yet).
        videoEl.addEventListener('loadedmetadata', function () {
            playerState.duration = videoEl.duration || 0;
            playerState.readyState = videoEl.readyState;
            setState(PLAYER_STATE.READY, { paused: videoEl.paused });
        });

        // Enough data to start playing (but not necessarily smooth).
        videoEl.addEventListener('canplay', function () {
            playerState.readyState = videoEl.readyState;
        });

        // Playback started.
        videoEl.addEventListener('play', function () {
            // Bring audio along — the <audio> element doesn't auto-sync
            // when only <video>.play() is called.
            if (audioEl && audioEl.paused && audioEl.src) {
                var p = audioEl.play(); if (p && p.catch) p.catch(function(){});
            }
            // Resume audio context if it got suspended (happens after
            // background tab throttling or system audio events).
            if (audioCtx && audioCtx.state === 'suspended') audioCtx.resume();
            applyTempo();
            setState(PLAYER_STATE.PLAYING, { paused: false });
        });

        // Playback paused (by user or by the browser).
        videoEl.addEventListener('pause', function () {
            if (audioEl && !audioEl.paused) audioEl.pause();
            setState(PLAYER_STATE.PAUSED, { paused: true });
        });

        // Reached the end.
        videoEl.addEventListener('ended', function () {
            setState(PLAYER_STATE.ENDED, { paused: true });
        });

        // Temporary stall — buffer ran dry, waiting for data.
        //videoEl.addEventListener('waiting', function () {
        //    setState(PLAYER_STATE.BUFFERING);
        //});
        videoEl.addEventListener('waiting', function () {
        // If we're within 0.5 s of the end and the video has nothing more
        // to give, treat this as "ended" rather than "buffering forever".
            if (videoEl.duration > 0
                && videoEl.duration - videoEl.currentTime < 0.5) {
                setState(PLAYER_STATE.ENDED, { paused: true });
                try { videoEl.pause(); } catch (_) {}
                try { if (audioEl) audioEl.pause(); } catch (_) {}
                return;
            }
            setState(PLAYER_STATE.BUFFERING);
        });

        // Resumed after a stall.
        videoEl.addEventListener('playing', function () {
            setState(PLAYER_STATE.PLAYING, { paused: false });
        });

        // Fatal media errors on either element.
        videoEl.addEventListener('error', onMediaError);
        audioEl.addEventListener('error', onMediaError);

        // ---- Announce we're ready ----
        setState(PLAYER_STATE.READY);
        post({ type: 'ready' });
        log('player page loaded (direct streams)');
    }

    /**
     * Handle a fatal error from either media element. Reports it up to
     * C# so the UI can show the user what went wrong.
     *
     * Media error codes:
     *   1 = MEDIA_ERR_ABORTED       (user stopped loading)
     *   2 = MEDIA_ERR_NETWORK       (network issue)
     *   3 = MEDIA_ERR_DECODE        (corrupted data)
     *   4 = MEDIA_ERR_SRC_NOT_SUPPORTED (format not supported / 403)
     */
    function onMediaError(evt) {
        var el   = evt.target || videoEl;
        var err  = el.error;
        var code = err ? err.code : 0;
        var msg  = err ? err.message : '';
        var kind = el === audioEl ? 'audio' : 'video';
        var detail = kind + ' error ' + code + (msg ? ' – ' + msg : '');
        log(detail + ' src=' + (el.currentSrc || el.src || ''));
        setState(PLAYER_STATE.ERROR, { errorMessage: detail });
        fail(detail);
        toast('Playback error: ' + detail);
    }

    // =====================================================================
    // TIME POLLER + DRIFT CORRECTION
    // =====================================================================
    // Runs every 250 ms. Two jobs:
    //   1. Report current position/duration/buffered to C#.
    //   2. Correct drift between <video> and <audio> elements.
    //
    // WHY DRIFT CORRECTION?
    //   The two elements are separate browsers objects — they advance
    //   their own currentTime independently. Over minutes, small
    //   differences in decoding speed cause them to diverge (audio
    //   heard a few ms before or after the corresponding video frame).
    //   The correction nudges them back into sync.
    //
    // THE TRICKY PART:
    //   Seeking (<el>.currentTime = X) is EXPENSIVE — it flushes the
    //   buffer and forces a new network request. If we did that every
    //   250 ms, playback would stutter constantly (this is a bug we hit
    //   early on).
    //
    //   So for SMALL drift (< 2 s), we use playbackRate nudging instead:
    //   speed up or slow down audio by a few percent to let it catch up
    //   or wait for video. PreservesPitch = true keeps the key intact,
    //   so the adjustment is inaudible.
    //
    //   Only CATASTROPHIC drift (> 2 s) triggers a real seek.
    //
    //   The 0.15 s "graphLatency" compensates for the AudioWorklet's
    //   processing delay: the audio you HEAR is ~150 ms behind what
    //   audioEl.currentTime reports, because the worklet buffers that
    //   much before outputting. So we compare the PERCEIVED audio
    //   position (currentTime − latency) against video's actual position.
    timePollerId = setInterval(function () {
        if (!videoEl) return;

        // ---- Drift correction (only when both are playing) ----
        if (audioEl && audioEl.src && !videoEl.paused && !audioEl.paused) {
            // Worklet adds latency to the audio path. Estimate it.
            var graphLatency = workletReady ? 0.15 : 0.0;
            var perceived    = audioEl.currentTime - graphLatency;
            var drift        = perceived - videoEl.currentTime;

            if (Math.abs(drift) > 2.0) {
                // Catastrophic drift — one hard seek is justified.
                try { audioEl.currentTime = videoEl.currentTime + graphLatency; } catch (_) {}
                audioEl.playbackRate = playerState.tempo;
            }
            else if (Math.abs(drift) > 0.04) {
                // Small drift — nudge playbackRate by up to 5%.
                // The -drift * 0.10 formula means "correct 10% of the
                // drift per tick" — gentle convergence, no overshoot.
                var adjust = Math.max(-0.05, Math.min(0.05, -drift * 0.10));
                audioEl.playbackRate = Math.max(
                    0.0625, Math.min(16, playerState.tempo + adjust));
            }
            else if (audioEl.playbackRate !== playerState.tempo) {
                // In sync — restore the true tempo.
                audioEl.playbackRate = playerState.tempo;
            }
        }

        // ---- Report current state to C# ----
        playerState.position   = videoEl.currentTime || 0;
        playerState.duration   = videoEl.duration    || 0;
        playerState.readyState = videoEl.readyState;
        playerState.paused     = videoEl.paused;

        // How many seconds of buffered data are ahead of the playhead?
        // Buffered can have multiple ranges (gaps after seeks), so we
        // look at the end of the LAST range minus current position.
        try {
            var b = videoEl.buffered;
            if (b && b.length > 0) {
                playerState.buffered = Math.max(0,
                    b.end(b.length - 1) - playerState.position);
            }
        } catch (_) {}

        post({
            type: 'time',
            position: playerState.position,
            duration: playerState.duration,
            paused: playerState.paused,
            buffered: playerState.buffered,
            state: playerState.status
        });
    }, 250);

    // =====================================================================
    // CLEANUP ON PAGE UNLOAD
    // =====================================================================
    // When the page is navigated away (reload, close, or C# sends us
    // elsewhere), we need to stop the timer and close the AudioContext.
    // Otherwise:
    //   • The interval keeps firing on a dead page (wasted CPU).
    //   • The AudioContext keeps the hardware audio thread alive,
    //     which can cause the OS to hold onto the audio device.
    window.addEventListener('beforeunload', function () {
        if (timePollerId !== null) {
            clearInterval(timePollerId);
            timePollerId = null;
        }

        if (audioCtx && audioCtx.state !== 'closed') {
            audioCtx.close().catch(function () {});
            audioCtx = null;
        }
    });

    // =====================================================================
    // PUBLIC API — window.ytp.*
    // =====================================================================
    // These methods are called by C# via ExecuteScriptAsync. They form
    // the contract between the two sides. Changing any of these breaks
    // the C# app (unless you change PlayerBridge.cs to match).
    window.ytp = {

        /**
         * Load and play a track.
         *
         * @param payload {
         *   videoUrl: string,   // proxy URL of the video stream
         *   audioUrl: string,   // proxy URL of the audio stream
         *   trackId?: string,   // YouTube video ID (for reference)
         *   title?:   string,   // display in HUD
         *   author?:  string    // display in HUD
         * }
         *
         * If videoUrl === audioUrl, this is a MUXED stream (single file
         * with both tracks). We handle that case by playing it in the
         * <video> element only. Otherwise we set up both elements.
         */
        load: function (payload) {
            payload = payload || {};
            if (!payload.videoUrl || !payload.audioUrl) {
                fail('videoUrl and audioUrl required');
                return;
            }

            setPlaceholderVisible(false);
            setHud(payload.title || '', payload.author || '');

            playerState.trackId = payload.trackId || null;
            playerState.title   = payload.title   || '';
            playerState.author  = payload.author  || '';
            playerState.position = 0;
            playerState.duration = 0;
            playerState.buffered = 0;
            playerState.errorMessage = null;

            // Reset per-track playback parameters. This is important —
            // otherwise the new track would inherit the pitch of the
            // previous one. C# then overrides these values right after
            // calling load() if the user has favorites with saved pitch.
            playerState.tempo          = 1.0;
            playerState.pitchSemitones = 0;
            playerState.pitchScale     = 1.0;

            setState(PLAYER_STATE.LOADING);

            try {
                var muxed = (payload.videoUrl === payload.audioUrl);
                if (muxed) {
                    // Single stream carries both tracks. Play it unmuted
                    // in <video>, clear any audio src.
                    videoEl.src = payload.videoUrl;
                    videoEl.muted = false;
                    audioEl.removeAttribute('src');
                    audioEl.load();
                } else {
                    // Two streams. Video is muted (its audio track is
                    // ignored); audio plays in <audio>.
                    videoEl.src = payload.videoUrl;
                    videoEl.muted = true;
                    audioEl.src = payload.audioUrl;
                    log('audio src = ' + payload.audioUrl);
                }

                // Re-apply current pitch & tempo to the new source.
                applyTempo();
                applyPitch();
                try { audioEl.volume = playerState.volume; } catch (_) {}

                // Load resets the elements to a blank state; then play.
                videoEl.load();
                if (!muxed) audioEl.load();

                var p = videoEl.play();
                if (p && p.catch) p.catch(function (err) {
                    log('play() rejected: ' + err.message);
                });
            } catch (e) {
                setState(PLAYER_STATE.ERROR, { errorMessage: e.message });
                fail(e.message);
            }
        },

        /** Toggle between play and pause. */
        togglePlay: function () {
            if (!videoEl) return;
            setPlaceholderVisible(false);
            videoEl.paused ? videoEl.play() : videoEl.pause();
        },

        /** Explicitly start playback. */
        play:  function () { if (videoEl) videoEl.play(); },

        /** Explicitly pause playback. */
        pause: function () { if (videoEl) videoEl.pause(); },

        /**
         * Stop playback and rewind to the beginning. The source stays
         * loaded, so pressing Play afterwards resumes instantly — no
         * network round trip. Also restores the placeholder splash.
         */
        stop: function () {
            setPlaceholderVisible(true);
            try {
                videoEl.pause();
                videoEl.currentTime = 0;
            } catch (_) {}
            try {
                audioEl.pause();
                audioEl.currentTime = 0;
            } catch (_) {}

            setState(PLAYER_STATE.READY, {
                position: 0,
                paused:   true,
                buffered: 0,
                errorMessage: null
            });
        },

        /** Jump the playhead to a specific position (in seconds). */
        seek: function (seconds) {
            var s = Number(seconds);
            if (!isFinite(s)) return;
            setState(PLAYER_STATE.SEEKING);
            try { videoEl.currentTime = s; } catch (_) {}
            try { audioEl.currentTime = s; } catch (_) {}
        },

        /**
         * Set playback speed. Range: 0.25× to 4.0×.
         * Pitch is preserved (via preservesPitch = true on the elements).
         */
        setTempo: function (v) {
            var t = Number(v);
            if (!isFinite(t)) return;
            t = Math.max(0.25, Math.min(4.0, t));
            playerState.tempo = t;
            if (audioCtx && audioCtx.state === 'suspended') audioCtx.resume();
            applyTempo();
            post({ type: 'state', state: snapshot() });
        },

        /**
         * Set the pitch shift.
         * @param semitones   -12..+12 whole semitones (coarse key shift)
         * @param fineScale   0.5..2.0 fine multiplier (for detuning)
         *
         * The two combine into a single ratio passed to the worklet.
         * The worklet applies that ratio to shift the audio.
         */
        setPitch: function (semitones, fineScale) {
            var st = Number(semitones);  if (!isFinite(st)) st = 0;
            var fs = Number(fineScale);  if (!isFinite(fs) || fs <= 0) fs = 1.0;
            playerState.pitchSemitones = st;
            playerState.pitchScale     = fs;
            applyPitch();
            post({ type: 'state', state: snapshot() });
        },

        /** Set volume, 0.0 = silent to 1.0 = full. */
        setVolume: function (v) {
            var n = Number(v);
            if (!isFinite(n)) return;
            n = Math.max(0, Math.min(1, n));
            playerState.volume = n;
            try { audioEl.volume = n; } catch (_) {}
            try { videoEl.volume = videoEl.muted ? 0 : n; } catch (_) {}
            post({ type: 'state', state: snapshot() });
        },

        /**
         * Nudge audio relative to video (in milliseconds).
         * Reserved for future use — currently just stored, not applied.
         * Would be useful if the worklet's latency causes A/V desync
         * that the drift corrector can't fully compensate.
         */
        setAudioOffset: function (ms) {
            var m = Number(ms);
            if (!isFinite(m)) return;
            playerState.audioOffsetMs = Math.max(-2000, Math.min(2000, m));
            post({ type: 'state', state: snapshot() });
        },

        /** Return the current state as a plain object (for C# to poll). */
        getState: function () { return snapshot(); }
    };

    // Expose the state constants so external code can reference them
    // symbolically instead of by string literal.
    window.ytp.states = PLAYER_STATE;

    // ---- Bootstrap ----
    // If the DOM is still loading, wait for it. Otherwise (usually the
    // case since this script loads at the bottom of <body>), run init
    // immediately.
    if (document.readyState === 'loading')
        document.addEventListener('DOMContentLoaded', init);
    else
        init();
})();