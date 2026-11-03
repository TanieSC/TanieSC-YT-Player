using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TanieSC_YT_Player.Services
{
    /// <summary>
    /// This class is responsible for ONE thing: saving and loading the user's
    /// list of favorite tracks to and from a file on disk.
    ///
    /// Think of it as a small notebook that lives at:
    ///     C:\Users\&lt;username&gt;\AppData\Local\TanieSC_YT_Player\favorites.json
    ///
    /// When the app starts, Load() opens the notebook and reads off the list.
    /// When the user stars a new track, Save() writes the updated list back.
    ///
    /// Why a separate class?
    ///   MainWindow does a lot of things — UI, playback, search. Keeping
    ///   file I/O in its own class makes the rest of the code easier to
    ///   read and test, and means the "where do favorites live?" question
    ///   is answered in exactly one place.
    ///
    /// Why "best-effort"?
    ///   Every operation here is wrapped in try/catch that silently swallows
    ///   errors. If the disk is full, the file is corrupt, or permissions
    ///   are wrong, we don't want the whole player to fail — the user
    ///   should still be able to search and play tracks, just without
    ///   persistent favorites. A crash on startup is much worse than
    ///   losing favorite data.
    /// </summary>
    public sealed class FavoritesStore
    {
        // ─────────────────────────────────────────────────────────────────
        // JSON FORMATTING OPTIONS
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Settings that control how the JSON file is written.
        ///
        /// WriteIndented = true means the file is pretty-printed with
        /// line breaks and indentation. Example:
        ///
        ///     [
        ///       {
        ///         "Id": "dQw4w9WgXcQ",
        ///         "Title": "Example Song"
        ///       }
        ///     ]
        ///
        /// Instead of one giant line. It's slightly larger on disk, but
        /// it's readable in a text editor — useful when debugging, and
        /// when a user reports a bug by pasting their file.
        ///
        /// "static readonly" means: created once, shared by every instance,
        /// never modified afterwards. Creating a new options object for
        /// every Save() would be wasteful.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true
        };

        // ─────────────────────────────────────────────────────────────────
        // WHERE THE FILE LIVES
        // ─────────────────────────────────────────────────────────────────

        /// <summary>The full path to favorites.json (set in the constructor).</summary>
        private readonly string _path;

        /// <summary>Public read-only accessor for the file path — useful for logging or showing to the user.</summary>
        public string Path => _path;

        // ─────────────────────────────────────────────────────────────────
        // CONSTRUCTOR — runs once when the app starts
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Works out the file path and makes sure the folder exists.
        ///
        /// LocalApplicationData is a special Windows folder meant for
        /// "user-specific app data that isn't essential to roaming".
        /// For a typical user, that resolves to:
        ///     C:\Users\Bob\AppData\Local
        ///
        /// We put our file in a subfolder named "TanieSC_YT_Player" so
        /// it doesn't mix with other apps' data.
        ///
        /// Why AppData\Local and not Program Files?
        ///   Program Files requires admin rights to write — the app
        ///   would fail on first Save. LocalApplicationData is always
        ///   writable by the current user, with no elevation prompts.
        /// </summary>
        public FavoritesStore()
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TanieSC_YT_Player");

            // Create the folder if it doesn't exist yet.
            // Directory.CreateDirectory does nothing if it already exists —
            // it's safe to call every time the app starts.
            Directory.CreateDirectory(dir);

            // Full path: C:\Users\Bob\AppData\Local\TanieSC_YT_Player\favorites.json
            _path = System.IO.Path.Combine(dir, "favorites.json");
        }

        // ─────────────────────────────────────────────────────────────────
        // LOAD — reads the favorites list from disk
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the JSON file and turns it back into a list of TrackInfo
        /// objects.
        ///
        /// Returns an empty list in every failure case — never throws.
        /// That way, MainWindow can just call Load() and iterate over the
        /// result without any try/catch of its own.
        ///
        /// Possible failure cases we handle silently:
        ///   • File doesn't exist yet (first run — perfectly normal)
        ///   • File is empty (user edited it and left it blank)
        ///   • File is corrupt or has invalid JSON (someone hand-edited it badly)
        ///   • File is locked by another program (rare, but possible)
        ///   • Disk read error (hardware issue)
        /// </summary>
        public List<TrackInfo> Load()
        {
            try
            {
                // Case 1: no file yet — return an empty list rather than an error.
                if (!File.Exists(_path)) return new List<TrackInfo>();

                // Read the entire file as a single string.
                var json = File.ReadAllText(_path);

                // Case 2: file exists but is empty/whitespace — treat like "no favorites".
                if (string.IsNullOrWhiteSpace(json)) return new List<TrackInfo>();

                // Case 3: parse the JSON into a list of TrackInfo objects.
                // Deserialize = "turn the text back into objects".
                // The `?? new List<TrackInfo>()` handles the edge case where
                // the JSON is literally "null" (deserialization would return
                // a null reference otherwise).
                var list = JsonSerializer.Deserialize<List<TrackInfo>>(json, JsonOpts)
                           ?? new List<TrackInfo>();

                // Force IsFavorite = true on every loaded item.
                // Why? The JSON might say "IsFavorite": false if the user
                // was mid-toggle when the app crashed, or if the field
                // wasn't serialized. But logically: "if this file contains
                // the track, it IS a favorite". So we set the flag
                // unconditionally to keep the UI consistent.
                foreach (var t in list) t.IsFavorite = true;

                return list;
            }
            catch
            {
                // Any failure (corrupt JSON, permissions, IO error) means
                // we return an empty list. The user loses their favorites
                // for this session, but the app keeps working. On the next
                // save, we'll overwrite the corrupt file with a clean one.
                return new List<TrackInfo>();
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // SAVE — writes the favorites list to disk
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes every favorite to the JSON file.
        ///
        /// Takes `IEnumerable<TrackInfo>` rather than a specific collection
        /// type — this means the caller can pass an ObservableCollection,
        /// a List, an array, or even a LINQ query result. Flexible.
        ///
        /// Also best-effort: if saving fails, we silently give up rather
        /// than show an error dialog. Worst case, the user loses their most
        /// recent favorite changes when they close the app. Much better
        /// than blocking the UI with an error popup every time the disk
        /// hiccups.
        /// </summary>
        public void Save(IEnumerable<TrackInfo> favorites)
        {
            try
            {
                // ── Step 1: copy the list into a plain List<TrackInfo> ──
                // Why copy? The caller's collection (ObservableCollection)
                // could be mutated from the UI thread while we're serializing,
                // which would throw "Collection was modified during
                // enumeration". Copying first freezes the contents.
                var snapshot = new List<TrackInfo>(favorites);

                // ── Step 2: serialize objects into a JSON string ──
                // Serialize = "turn objects into text".
                var json = JsonSerializer.Serialize(snapshot, JsonOpts);

                // ── Step 3: write to a temp file, then rename ──
                // This is the "atomic write" pattern. Why bother?
                //
                // If we wrote directly to favorites.json and the app
                // crashed / lost power halfway through File.WriteAllText,
                // the file would be half-written and corrupt. On next
                // startup, Load() would fail and return an empty list —
                // the user loses all their favorites.
                //
                // Instead:
                //   1. Write the complete JSON to favorites.json.tmp
                //      (if this fails, the original is untouched)
                //   2. Rename .tmp → .json in one atomic OS operation
                //      (either succeeds fully or fails fully)
                //
                // Result: the real file is either the OLD complete
                // version or the NEW complete version — never a corrupt
                // partial in between.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);

                // File.Move with overwrite=true is an atomic replace on
                // Windows (uses MoveFileEx with MOVEFILE_REPLACE_EXISTING
                // under the hood). This is what makes the pattern safe.
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // Disk full, permissions, locked file, whatever — the
                // user's in-memory list is still correct for this session.
                // They'll see their changes until they close the app; the
                // next successful Save will catch up.
            }
        }
    }
}