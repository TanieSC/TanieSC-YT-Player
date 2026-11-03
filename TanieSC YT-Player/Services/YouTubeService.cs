using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using YoutubeExplode;
using YoutubeExplode.Search;
using Container = YoutubeExplode.Videos.Streams.Container;

namespace TanieSC_YT_Player.Services
{
    /// <summary>
    /// Represents ONE track (one YouTube video) that the user is looking at.
    /// This class is a "data model" — it holds information, it doesn't DO
    /// anything. All playback, search, and networking live in other classes.
    ///
    /// Its job is to be displayed in lists. Every property here shows up
    /// somewhere in the UI:
    ///   • Id           → the YouTube video ID (needed to fetch streams)
    ///   • Title        → the name shown in bold
    ///   • Author       → the channel name shown below the title
    ///   • Duration     → formatted as "3:45" in the subtitle
    ///   • ThumbnailUrl → the small preview image on the left
    ///   • IsFavorite   → whether the star/checkbox is ticked
    ///   • PitchSemitones / PitchCents → the saved key for that favorite
    ///
    /// WHY DOES IT IMPLEMENT INotifyPropertyChanged?
    ///   WPF needs to know when a property changes so it can update the
    ///   UI. Without this interface, changing IsFavorite from code would
    ///   not make the checkbox visually tick. With it, WPF automatically
    ///   refreshes the affected controls.
    /// </summary>
    public sealed class TrackInfo : INotifyPropertyChanged
    {
        // ─────────────────────────────────────────────────────────────────
        // IMMUTABLE FIELDS — set once when the object is created, never changed
        // ─────────────────────────────────────────────────────────────────

        /// <summary>The YouTube video ID. Example: "dQw4w9WgXcQ".</summary>
        public string Id { get; init; } = "";

        /// <summary>The song or video title.</summary>
        public string Title { get; init; } = "";

        /// <summary>The channel name that uploaded the video.</summary>
        public string Author { get; init; } = "";

        /// <summary>How long the video is. Used to format "3:45" in the UI.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// URL of the preview thumbnail image.
        /// Always something like "https://i.ytimg.com/vi/{id}/mqdefault.jpg".
        /// The ThumbnailConverter turns this string into an actual image.
        /// </summary>
        public string ThumbnailUrl { get; init; } = "";

        // ─────────────────────────────────────────────────────────────────
        // COMPUTED PROPERTY — calculated on the fly, not stored
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The one-line description shown under the title in each row.
        /// Format: "Channel Name  -  3:45"
        /// For long videos (over 1 hour): "Channel  -  1:23:45"
        ///
        /// The backslash-escaped colons (\:) in the format string are
        /// needed because ":" is normally a special character in TimeSpan
        /// formatting (it means "time separator" not literal colon).
        ///
        /// This property has no setter — WPF just reads it when displaying.
        /// </summary>
        public string Subtitle =>
            Duration.TotalHours >= 1
                ? $"{Author}  -  {Duration:h\\:mm\\:ss}"
                : $"{Author}  -  {Duration:m\\:ss}";

        // ─────────────────────────────────────────────────────────────────
        // MUTABLE FAVOURITE STATE — changes at runtime, needs to notify WPF
        // ─────────────────────────────────────────────────────────────────

        private bool _isFavorite;

        /// <summary>
        /// True if this track has been starred/saved to favorites.
        ///
        /// The pattern used here is called a "backing field":
        ///   • `_isFavorite` is the real storage.
        ///   • The public `IsFavorite` property wraps it and fires
        ///     PropertyChanged whenever it changes.
        ///
        /// WHY THE EARLY RETURN (`if (_isFavorite == value) return;`)?
        ///   Setting a property to the same value it already has is a
        ///   no-op. Firing PropertyChanged unnecessarily would cause
        ///   WPF to rebind and repaint for nothing — wasteful. So we
        ///   skip the notification when nothing actually changed.
        /// </summary>
        public bool IsFavorite
        {
            get => _isFavorite;
            set { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(); }
        }

        // ─────────────────────────────────────────────────────────────────
        // SAVED PITCH FOR FAVORITES
        // ─────────────────────────────────────────────────────────────────

        private int _pitchSemitones;

        /// <summary>
        /// How many semitones (musical half-steps) this favorite should
        /// play at. Range: -12 to +12. Example: +2 means "two semitones up"
        /// (a whole step higher). 0 means "no change".
        ///
        /// When this changes, we fire TWO notifications:
        ///   1. PropertyChanged("PitchSemitones") — for anything bound to
        ///      the raw number.
        ///   2. PropertyChanged("SavedKeyLabel") — because the label
        ///      ("+2 st +0 ¢") is computed from both this AND PitchCents,
        ///      so it needs refreshing too.
        ///
        /// Without the second notification, the badge showing the saved
        /// key would show a stale value until the row was redrawn.
        /// </summary>
        public int PitchSemitones
        {
            get => _pitchSemitones;
            set
            {
                if (_pitchSemitones == value) return;
                _pitchSemitones = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SavedKeyLabel));
            }
        }

        private int _pitchCents;

        /// <summary>
        /// Fine-tuning in "cents" — one-hundredth of a semitone.
        /// Range: -100 to +100. Example: +20 means "20 cents sharp" (about
        /// a fifth of a semitone up, useful for slightly out-of-tune tracks).
        /// Combined with PitchSemitones, the total shift is
        /// (semitones + cents/100) semitones.
        ///
        /// Same double-notification logic as PitchSemitones above.
        /// </summary>
        public int PitchCents
        {
            get => _pitchCents;
            set
            {
                if (_pitchCents == value) return;
                _pitchCents = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SavedKeyLabel));
            }
        }

        /// <summary>
        /// A human-readable badge like "+2 st +20 ¢" or "-3 st -5 ¢".
        /// Shown on favorite rows so the user can see at a glance what
        /// key each favorite was saved at.
        ///
        /// The format "+#;-#;0" means:
        ///   • Positive numbers: prefix with "+"       → "+2"
        ///   • Negative numbers: prefix with "-"       → "-3"
        ///   • Zero: just show "0"                     → "0"
        /// This produces "+2 st" instead of "2 st", which reads more
        /// clearly as "an offset of +2" rather than "the number 2".
        /// </summary>
        public string SavedKeyLabel => $"{_pitchSemitones:+#;-#;0} st {_pitchCents:+#;-#;0} ¢";

        // ─────────────────────────────────────────────────────────────────
        // INotifyPropertyChanged PLUMBING
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// WPF subscribes to this event. When we fire it with a property
        /// name, WPF re-reads that property and updates the UI.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Fire the PropertyChanged event for the given property.
        ///
        /// The [CallerMemberName] attribute is a clever C# trick: if the
        /// caller doesn't specify a name, the compiler automatically
        /// fills in the name of the method/property that CALLED this one.
        /// So calling OnPropertyChanged() from inside IsFavorite's setter
        /// automatically becomes OnPropertyChanged("IsFavorite") — no
        /// hardcoded string that could get out of sync with a rename.
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// This class talks to YouTube on the app's behalf. It does exactly
    /// two things:
    ///
    ///   1. Search: given a query string, stream back a list of matching
    ///      videos (with titles, authors, thumbnails, durations).
    ///
    ///   2. Stream resolution: given a video ID, return the actual direct
    ///      URLs that contain the video and audio data.
    ///
    /// All of this uses a library called "YoutubeExplode" — it handles the
    /// messy parts (signature deciphering, client selection, etc.) and
    /// gives us clean .NET objects.
    ///
    /// This class has NO state, NO UI, and does NO caching. It's pure
    /// "do this thing, return the answer" code. That's deliberate — it
    /// makes testing easy and keeps the rest of the app simple.
    /// </summary>
    public sealed class YouTubeService
    {
        /// <summary>
        /// The YoutubeExplode client. One instance for the whole service —
        /// creating one per request would waste resources. Under the hood
        /// it holds the HTTP connection pool, cipher caches, etc.
        /// </summary>
        private readonly YoutubeClient _yt = new();

        /// <summary>
        /// Search YouTube for videos matching the given query.
        ///
        /// WHY "STREAM" AND "ASYNC ENUMERABLE"?
        ///   A normal `Task<List<TrackInfo>>` would wait for ALL results
        ///   before returning anything. That's a 2-second blank screen.
        ///
        ///   Instead, this method returns results ONE AT A TIME as they
        ///   arrive from YouTube. The caller (MainWindow) can start
        ///   displaying the first few results while the rest are still
        ///   downloading. Feels instant even when the network is slow.
        ///
        /// HOW THE CALLER USES IT:
        ///   await foreach (var t in _yt.SearchStreamAsync("query"))
        ///   {
        ///       _results.Add(t);   // add to UI one by one
        ///       if (++count >= 30) break;  // stop after 30
        ///   }
        ///
        /// THE [EnumeratorCancellation] ATTRIBUTE:
        ///   This is a compiler hint. When the caller does
        ///   `.WithCancellation(ct)`, the token would normally be lost
        ///   in the async-enumerable machinery. This attribute ensures
        ///   the token flows through correctly so the search can be
        ///   cancelled mid-stream (e.g., when the user types a new query).
        /// </summary>
        public async IAsyncEnumerable<TrackInfo> SearchStreamAsync(
            string query,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
        {
            // .WithCancellation(ct) tells YoutubeExplode to abort the HTTP
            // request if we cancel. Without it, a cancelled search would
            // still finish in the background, wasting bandwidth.
            await foreach (var result in _yt.Search.GetResultsAsync(query, ct).WithCancellation(ct))
            {
                // YouTube search returns several kinds of results: videos,
                // channels, playlists. We only want videos. The pattern
                // `is not VideoSearchResult` + `continue` skips everything
                // else cleanly.
                if (result is not VideoSearchResult v) continue;

                // Build a TrackInfo from the search result. Note the
                // thumbnail URL pattern — YouTube exposes thumbnails at
                // predictable URLs based on the video ID, no API call
                // needed. "mqdefault" = medium-quality default size.
                yield return new TrackInfo
                {
                    Id = v.Id.Value,
                    Title = v.Title,
                    Author = v.Author.ChannelTitle,
                    Duration = v.Duration ?? TimeSpan.Zero,  // some live streams have null duration
                    ThumbnailUrl = $"https://i.ytimg.com/vi/{v.Id.Value}/mqdefault.jpg",
                };
            }
        }

        /// <summary>
        /// Given a video ID, find the best video-only and audio-only stream
        /// URLs, plus a fallback if only a muxed (combined) stream exists.
        ///
        /// RETURNS A TUPLE: (VideoUrl, AudioUrl)
        ///   • Normal case: two different URLs — the browser feeds one to
        ///     a &lt;video&gt; and the other to a &lt;audio&gt; element.
        ///   • Muxed fallback: both URLs are the SAME — the browser can
        ///     play either one alone.
        ///
        /// WHY SPLIT VIDEO AND AUDIO?
        ///   YouTube offers higher quality when the two are delivered
        ///   separately. The video stream can be 1080p or 4K without the
        ///   audio track wasting bits on every frame. This app then routes
        ///   the audio stream through a pitch/tempo DSP chain (something
        ///   a muxed stream couldn't do as cleanly).
        ///
        /// THE FALLBACK CHAIN (the four `??` clauses for video):
        ///   YouTube offers many combinations of (codec, resolution, bitrate).
        ///   Our priority:
        ///     1. AVC (H.264) at ≤720p     — most compatible, cheapest to decode
        ///     2. AVC at ≤1080p             — still hardware-decodable everywhere
        ///     3. Any codec at ≤720p        — VP9/AV1 fallback if no AVC exists
        ///     4. Any codec at any quality  — last resort
        ///     5. Muxed stream              — if video-only and audio-only can't both be found
        ///
        ///   The `??` operator means "if this returns null, try the next one."
        ///   So it cascades down the priority list until something matches.
        ///
        /// WHY AVC FIRST?
        ///   Chromium (the WebView2 engine) has very reliable hardware
        ///   decode for H.264 on every GPU. VP9 and AV1 sometimes fall
        ///   back to software decode, which is slow enough on some machines
        ///   to cause video stutters even when the buffer is full. Since
        ///   this is a karaoke app (not 4K video), 720p AVC is the best
        ///   trade-off: cheap, smooth, plenty of visual quality.
        /// </summary>
        public async Task<(string VideoUrl, string AudioUrl)> GetStreamsAsync(
            string videoId, CancellationToken ct = default)
        {
            // Fetch the "manifest" — the master list of every stream
            // available for this video. This is one HTTP round trip to
            // YouTube's API (~200-500 ms) but gives us everything we need.
            var manifest = await _yt.Videos.Streams.GetManifestAsync(videoId, ct).ConfigureAwait(false);

            // ─────────────────────────────────────────────────────────────
            // PICK THE AUDIO STREAM
            // ─────────────────────────────────────────────────────────────
            // Sort by two criteria:
            //   • Prefer MP4 container (more compatible than WebM/Opus)
            //   • Then prefer the highest bitrate (better audio quality)
            // Take the top one — that's our audio.
            //
            // FirstOrDefault returns null if no streams exist; the code
            // below checks for that before returning.
            var audio = manifest.GetAudioOnlyStreams()
                            .OrderByDescending(s => s.Container == Container.Mp4)
                            .ThenByDescending(s => s.Bitrate)
                            .FirstOrDefault();

            // ─────────────────────────────────────────────────────────────
            // PICK THE VIDEO STREAM — priority cascade
            // ─────────────────────────────────────────────────────────────

            // Try 1: AVC (H.264) at 720p or lower. This is the sweet spot
            // for karaoke — smooth playback on any GPU. "IndexOf(...) >= 0"
            // is a case-insensitive "contains" check, catching codec names
            // like "avc1.640028".
            var video = manifest.GetVideoOnlyStreams()
                            .Where(s => s.Container == Container.Mp4
                                     && s.VideoCodec.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0
                                     && s.VideoQuality.MaxHeight <= 720)
                            .OrderByDescending(s => s.VideoQuality.MaxHeight)
                            .ThenByDescending(s => s.Bitrate)
                            .FirstOrDefault()

                        // Try 2: AVC at up to 1080p. Still hardware-decodable
                        // on most GPUs, just heavier. Used when no 720p
                        // AVC stream exists (rare for popular videos).
                        ?? manifest.GetVideoOnlyStreams()
                            .Where(s => s.Container == Container.Mp4
                                     && s.VideoCodec.IndexOf("avc", StringComparison.OrdinalIgnoreCase) >= 0
                                     && s.VideoQuality.MaxHeight <= 1080)
                            .OrderByDescending(s => s.VideoQuality.MaxHeight)
                            .ThenByDescending(s => s.Bitrate)
                            .FirstOrDefault()

                        // Try 3: Any codec (VP9, AV1, etc.) at 720p or lower.
                        // Rare — most videos have AVC at 720p. But some
                        // newer uploads skip AVC entirely.
                        ?? manifest.GetVideoOnlyStreams()
                            .Where(s => s.Container == Container.Mp4
                                     && s.VideoQuality.MaxHeight <= 720)
                            .OrderByDescending(s => s.VideoQuality.MaxHeight)
                            .ThenByDescending(s => s.Bitrate)
                            .FirstOrDefault()

                        // Try 4: Any MP4 stream at any quality. Last resort
                        // for video-only. If this is null, the whole video
                        // has no video-only streams at all and we fall
                        // through to the muxed fallback below.
                        ?? manifest.GetVideoOnlyStreams()
                            .Where(s => s.Container == Container.Mp4)
                            .OrderByDescending(s => s.VideoQuality.MaxHeight)
                            .FirstOrDefault();

            // ─────────────────────────────────────────────────────────────
            // RETURN: two separate URLs if we found both streams
            // ─────────────────────────────────────────────────────────────
            if (audio is not null && video is not null)
                return (video.Url, audio.Url);

            // ─────────────────────────────────────────────────────────────
            // MUXED FALLBACK
            // ─────────────────────────────────────────────────────────────
            // Reaching this point means at least one of video/audio-only
            // streams was missing. Try a "muxed" stream — one MP4 that
            // contains BOTH video and audio in a single file.
            //
            // Muxed streams max out at 720p (YouTube stopped offering
            // higher-res muxed a few years ago), and they cost more
            // bandwidth than separate streams. But they work everywhere.
            //
            // We return the SAME URL for both video and audio. The page
            // detects `videoUrl === audioUrl` and switches to single-element
            // playback mode. Pitch/tempo still work (they act on the
            // video element's audio track).
            var muxed = manifest.GetMuxedStreams()
                            .OrderByDescending(s => s.VideoQuality.MaxHeight)
                            .FirstOrDefault()

                        // If even a muxed stream doesn't exist, the video is
                        // completely unplayable — throw an exception with a
                        // message the UI can show.
                        ?? throw new InvalidOperationException("No playable stream found for this video.");

            return (muxed.Url, muxed.Url);
        }
    }
}