using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TanieSC_YT_Player.Services;
using YoutubeExplode.Videos;

namespace TanieSC_YT_Player
{
    public partial class MainWindow : Window
    {
        private readonly YouTubeService _yt = new();
        private readonly ObservableCollection<TrackInfo> _results = [];
        private readonly ObservableCollection<TrackInfo> _favorites = [];
        private readonly ObservableCollection<TrackInfo> _favoritesView = [];
        private bool _shuffled;
        private const int ShuffleCount = 25;

        // Favorites survive across searches, keyed by video id.
        private readonly Dictionary<string, TrackInfo> _favoriteCache = [];

        // The TrackInfo that's currently loaded in the player (null if none).
        private TrackInfo? _currentTrack;

        // Tracks subscribed to PropertyChanged, so we never double-subscribe.
        private readonly HashSet<TrackInfo> _subscribed = [];
        private readonly FavoritesStore _favoritesStore = new();
        private bool _loadingFavorites;
        private StreamProxy? _proxy;
        private PlayerBridge? _bridge;
        private CancellationTokenSource? _searchCts;

        private double _lastPosition;
        private double _lastDuration;
        private string? _currentVideoProxy;
        private string? _currentAudioProxy;
        private CancellationTokenSource? _playCts;
        private bool _applyingSavedPitch;
        private static readonly SolidColorBrush ShuffleOnBg = new(Color.FromRgb(0x1F, 0x3A, 0x57)); // matches selected item
        private static readonly SolidColorBrush ShuffleOnBorder = new(Color.FromRgb(0x7F, 0xD1, 0xFF));
        private static readonly SolidColorBrush ShuffleOnText = new(Color.FromRgb(0x7F, 0xD1, 0xFF));

        public MainWindow()
        {
            InitializeComponent();
            ResultsList.ItemsSource = _results;
            FavoritesList.ItemsSource = _favoritesView;   // ← was _favorites

            LoadFavorites();

            _favorites.CollectionChanged += (_, __) =>
            {
                if (!_loadingFavorites) SaveFavorites();
                RefreshFavoritesView();                    // mirror into the view
            };

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        // =================================================================
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var assetsDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                if (!Directory.Exists(assetsDir))
                {
                    LblStatus.Text = "wwwroot folder not found at " + assetsDir;
                    return;
                }

                // ---- stream proxy (serves page + media) -----------------
                _proxy = new StreamProxy(assetsDir);
                _proxy.Start();

                //_proxy.Log += msg =>
                //{
                //    try { File.AppendAllText("proxy.log", $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n"); }
                //    catch { }
                //};

                LblStatus.Text = $"Local server ready on {_proxy.BaseUrl}";

                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TanieSC_YT_Player", "WebView2");

                //var options = new CoreWebView2EnvironmentOptions
                //{
                //    AdditionalBrowserArguments =
                //        "--autoplay-policy=no-user-gesture-required " +
                //        "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection"
                //};

                var options = new CoreWebView2EnvironmentOptions
                {
                        AdditionalBrowserArguments =
                            "--autoplay-policy=no-user-gesture-required " +
                            "--enable-features=PlatformHEVCDecoderSupport,AcceleratedVideoDecodeLinuxGL " +
                            "--enable-gpu-rasterization " +
                            "--enable-zero-copy " +
                            "--ignore-gpu-blocklist"
                };

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userData,
                    options: options);

                await Player.EnsureCoreWebView2Async(env);
//#if DEBUG
//                Player.CoreWebView2.OpenDevToolsWindow();
//#endif

                Player.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Player.CoreWebView2.Settings.AreDevToolsEnabled = false;
                Player.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Player.CoreWebView2.Settings.IsZoomControlEnabled = false;

                _bridge = new PlayerBridge(Player.CoreWebView2);
                _bridge.Ready += Bridge_Ready;
                _bridge.StateChanged += Bridge_StateChanged;
                _bridge.TimeChanged += Bridge_TimeChanged;
                _bridge.ErrorOccurred += Bridge_ErrorOccurred;
                _bridge.LogMessage += Bridge_LogMessage;

                // 127.0.0.1 is a potentially-trustworthy origin, so
                // AudioWorklet is available and everything is same-origin.
                Player.Source = new Uri(_proxy.BaseUrl + "/index.html");
            }
            catch (Exception ex)
            {
                LblStatus.Text = "WebView2 init failed: " + ex.Message;
                LblEngine.Text = "init failed";
            }
        }

        private async void OnClosed(object? sender, EventArgs e)
        {
            SaveFavorites();

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;

            if (_bridge is not null)
                await _bridge.DisposeAsync();

            if (_proxy is not null)
                await _proxy.DisposeAsync();
        }

        private void Bridge_Ready(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                LblEngine.Text = "ready";
                _ = _bridge!.SetVolumeAsync(SlVolume.Value / 100.0);
                _ = _bridge.SetTempoAsync(SlTempo.Value);

                // Push whatever the sliders currently show, not a hardcoded 0.
                // This preserves pitch across page reloads.
                int semi = (int)Math.Round(SlSemitones.Value);
                int cents = (int)Math.Round(SlCents.Value);
                _ = _bridge.SetPitchAsync(semi + cents / 100.0, 1.0);
            });
        }

        private void Bridge_StateChanged(object? sender, PlayerSnapshot snap)
        {
            Dispatcher.Invoke(() =>
            {
                var txt = snap.Status;
                // Surface worklet availability.
                if (snap.WorkletFailed)
                    txt += "  (pitch: off — worklet unavailable)";
                else if (!snap.WorkletReady)
                    txt += "  (pitch: loading)";
                else
                    txt += "  (pitch: ready)";

                if (!string.IsNullOrEmpty(snap.ErrorMessage))
                    txt += " — " + snap.ErrorMessage;

                LblEngine.Text = txt;
            });
        }

        private void Bridge_TimeChanged(object? sender, PlayerTimeEventArgs t)
        {
            _lastPosition = t.Position;
            _lastDuration = t.Duration;

            Dispatcher.Invoke(() =>
            {
                LblPlayback.Text =
                    $"state = {t.State}   t = {t.Position:F1} s / {t.Duration:F1} s";
            });
        }

        private void Bridge_ErrorOccurred(object? sender, PlayerErrorEventArgs e)
            => Dispatcher.Invoke(() => LblStatus.Text = "Player error: " + e.Message);

        private void Bridge_LogMessage(object? sender, string msg)
        {
            // Dispatcher.Invoke(() => StatusText.Text = msg);
            try
            {
                File.AppendAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "player.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}  {msg}\n");
            }
            catch { /* logging must never crash the app */ }
        }

        // =================================================================
        private async void Search_Click(object sender, RoutedEventArgs e)
            => await RunSearchAsync();

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await RunSearchAsync(); }
        }

        private async Task RunSearchAsync()
        {
            var q = SearchBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(q)) return;

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            // Unsubscribe from old results that aren't favorites anymore.
            foreach (var t in _results.ToList())
                if (!_favorites.Contains(t)) EnsureUnsubscribed(t);
            _results.Clear();

            StatusSearch.Text = $"Searching: {q} …";
            SearchButton.IsEnabled = false;

            const int MaxResults = 30;
            int count = 0;

            try
            {
                await foreach (var r in _yt.SearchStreamAsync(q, ct))
                {
                    // If we already have this video as a favorite, reuse that
                    // instance so the checkbox appears pre-checked.
                    TrackInfo t = _favoriteCache.TryGetValue(r.Id, out var cached)
                        ? cached
                        : r;

                    EnsureSubscribed(t);
                    _results.Add(t);

                    if (++count >= MaxResults) break;
                }
                StatusSearch.Text = $"Loaded {count} result(s).";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusSearch.Text = "Search failed: " + ex.Message; }
            finally { SearchButton.IsEnabled = true; }
        }

        // =================================================================
        private void EnsureSubscribed(TrackInfo t)
        {
            if (_subscribed.Add(t))
                t.PropertyChanged += OnTrackPropertyChanged;
        }

        private void EnsureUnsubscribed(TrackInfo t)
        {
            if (_subscribed.Remove(t))
                t.PropertyChanged -= OnTrackPropertyChanged;
        }

        private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not TrackInfo t) return;

            if (e.PropertyName == nameof(TrackInfo.IsFavorite))
            {
                if (t.IsFavorite)
                {
                    if (ReferenceEquals(t, _currentTrack))
                    {
                        t.PitchSemitones = (int)Math.Round(SlSemitones.Value);
                        t.PitchCents = (int)Math.Round(SlCents.Value);
                    }

                    if (!_favorites.Contains(t))
                    {
                        _favoriteCache[t.Id] = t;
                        _favorites.Add(t);      // CollectionChanged → SaveFavorites()
                    }
                }
                else
                {
                    if (_favorites.Contains(t))
                    {
                        _favorites.Remove(t);   // CollectionChanged → SaveFavorites()
                        _favoriteCache.Remove(t.Id);
                    }

                    // Explicitly drop it from the shuffled view (RefreshFavoritesView
                    // is a no-op while _shuffled is true).
                    if (_shuffled && _favoritesView.Contains(t))
                    {
                        _favoritesView.Remove(t);
                        UpdateFavoritesCaption();
                    }

                    if (!_results.Contains(t))
                        EnsureUnsubscribed(t);
                }
                return;
            }
        }

        private void LoadFavorites()
        {
            _loadingFavorites = true;
            try
            {
                foreach (var t in _favoritesStore.Load())
                {
                    if (_favoriteCache.ContainsKey(t.Id)) continue;

                    _favoriteCache[t.Id] = t;
                    EnsureSubscribed(t);
                    _favorites.Add(t);
                }

                UpdateShuffleButtonVisual();
            }
            finally
            {
                _loadingFavorites = false;
                RefreshFavoritesView();     // populate _favoritesView
            }
        }


        private void SaveFavorites()
        {
            _favoritesStore.Save(_favorites);
        }

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //if (ResultsList.SelectedItem is TrackInfo t)
            //    LblTitle.Text = t.Title + "  —  " + t.Author;
        }

        // Fires when the ▶ Play button inside a DataTemplate is clicked.
        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            if (b.DataContext is not TrackInfo t) return;
            await PlayTrackAsync(t);
        }

        private async void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is not TrackInfo t) return;
            await PlayTrackAsync(t);
        }

        private void FavoritesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // While a favorite is playing, the sliders control the live pitch,
            // not the selection. Changing the selection shouldn't disturb it.
            if (_currentTrack is { IsFavorite: true }) return;

            if (FavoritesList.SelectedItem is not TrackInfo t) return;

            // ApplySavedPitch sets _applyingSavedPitch = true during the Value
            // assignments, which prevents the mirror from firing and
            // re‑saving the same values to disk.
            ApplySavedPitch(t);
        }
        private async void FavoritesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FavoritesList.SelectedItem is not TrackInfo t) return;
            await PlayTrackAsync(t);
        }

        private void ClearFavorites_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot first — removing items mutates the collection.
            foreach (var f in _favorites.ToList())
                f.IsFavorite = false;
        }

        /// <summary>
        /// Rebuilds the Favorites ListBox contents from the backing collection.
        /// When shuffled, we keep whatever the shuffle produced until the user
        /// toggles back — the list stays a stable snapshot of random picks.
        /// </summary>
        private void RefreshFavoritesView()
        {
            if (_shuffled) return;   // shuffled view is a frozen snapshot

            _favoritesView.Clear();
            foreach (var t in _favorites)
                _favoritesView.Add(t);

            UpdateFavoritesCaption();
        }

        private void UpdateFavoritesCaption()
        {
            if (LblFavCount is null) return;
            LblFavCount.Text = _shuffled
                ? $"🎲 {_favoritesView.Count} of {_favorites.Count}"
                : $"{_favorites.Count} saved";
        }

        private void ShuffleFavorites_Click(object sender, RoutedEventArgs e)
        {
            if (_shuffled)
            {
                // Toggle back to the full list.
                _shuffled = false;
                RefreshFavoritesView();
                UpdateShuffleButtonVisual();
                return;
            }

            if (_favorites.Count == 0) return;

            _shuffled = true;

            // Fisher-Yates on a copy so we don't disturb _favorites order.
            var pool = _favorites.ToList();
            var rng = Random.Shared;

            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var picks = pool.Take(ShuffleCount).ToList();

            _favoritesView.Clear();
            foreach (var t in picks)
                _favoritesView.Add(t);

            UpdateFavoritesCaption();
            UpdateShuffleButtonVisual();          // ← highlight
        }

        private void UpdateShuffleButtonVisual()
        {
            if (ShuffleButton is null) return;

            if (_shuffled)
            {
                ShuffleButton.Background = ShuffleOnBg;
                ShuffleButton.BorderBrush = ShuffleOnBorder;
                ShuffleButton.Foreground = ShuffleOnText;
                ShuffleButton.ToolTip = "Showing 25 random favorites — click to restore the full list";
            }
            else
            {
                // Remove the local values so the Button style reasserts its defaults.
                ShuffleButton.ClearValue(BackgroundProperty);
                ShuffleButton.ClearValue(BorderBrushProperty);
                ShuffleButton.ClearValue(ForegroundProperty);
                ShuffleButton.ToolTip = "Show 25 random favorites (click again to restore)";
            }
        }

        private async Task PlayTrackAsync(TrackInfo t)
        {
            if (_bridge is null || _proxy is null) return;

            if (!_bridge.IsReady)
            {
                StatusSearch.Text = "Player initialising — try again in a moment.";
                return;
            }

            // Log which surface triggered playback. If a 403 later appears, you
            // can tell from the surrounding lines whether it was a favorite or
            // a search-result double-click.
            //WriteLog("host",
            //    $"play '{t.Title}' id={t.Id} src={(FavoritesList.SelectedItem == t ? "favorites" : "search")}");

            StatusSearch.Text = $"Resolving streams for: {t.Title} …";
            SearchButton.IsEnabled = false;

            _playCts?.Cancel();
            _playCts?.Dispose();
            _playCts = new CancellationTokenSource();
            var ct = _playCts.Token;

            try
            {
                var (videoUrl, audioUrl) = await _yt.GetStreamsAsync(t.Id, ct);

                var newVideoProxy = _proxy.Register(videoUrl, "video/mp4");
                var newAudioProxy = (videoUrl == audioUrl)
                    ? newVideoProxy
                    : _proxy.Register(audioUrl, "audio/mp4");

                ct.ThrowIfCancellationRequested();

                await _bridge.LoadAsync(
                    videoUrl: newVideoProxy,
                    audioUrl: newAudioProxy,
                    trackId: t.Id,
                    title: t.Title,
                    author: t.Author);

                // Swap current track & restore its saved key (0/0 if none).
                _currentTrack = t;


                ApplySavedPitch(t);

                // Old track is replaced now, safe to unregister old proxies.
                if (_currentVideoProxy is not null)
                    _proxy.Unregister(_currentVideoProxy);

                if (_currentAudioProxy is not null &&
                    _currentAudioProxy != _currentVideoProxy)
                    _proxy.Unregister(_currentAudioProxy);

                _currentVideoProxy = newVideoProxy;
                _currentAudioProxy = newAudioProxy;

                StatusSearch.Text = "Playing.";
            }
            catch (Exception ex)
            {
                StatusSearch.Text = "Load failed: " + ex.Message;
            }
            finally
            {
                SearchButton.IsEnabled = true;
            }
        }

        // =================================================================
        private async void SlVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblVolume is not null)
                LblVolume.Text = ((int)SlVolume.Value).ToString();

            if (_bridge is not null)
                await _bridge.SetVolumeAsync(SlVolume.Value / 100.0);
        }

        private void SlSemitones_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdatePitchLabelsAndSend();

        private void SlCents_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdatePitchLabelsAndSend();

        private void PitchSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            // The user released the thumb — flush the current pitch of the
            // playing favourite to favorites.json.
            FlushFavoritesIfPlayingFavorite();
        }

        /// <summary>
        /// The TrackInfo that a slider change should update.
        /// Priority: currently playing favorite → selected favorite in the list.
        /// Returns null if neither applies (nothing to save to).
        /// </summary>
        private TrackInfo? GetPitchTarget()
        {
            if (_currentTrack is { IsFavorite: true } playing)
                return playing;

            if (FavoritesList.SelectedItem is TrackInfo { IsFavorite: true } selected)
                return selected;

            return null;
        }
        private void ApplySavedPitch(TrackInfo t)
        {
            _applyingSavedPitch = true;
            try
            {
                int semi = (int)Math.Round(SlSemitones.Value);
                int cents = (int)Math.Round(SlCents.Value);

                if (semi != 0 || cents != 0)
                {
                    SlSemitones.Value = 0;
                    SlCents.Value = 0;
                }

                SlSemitones.Value = Math.Clamp(t.PitchSemitones,
                                               SlSemitones.Minimum, SlSemitones.Maximum);
                SlCents.Value = Math.Clamp(t.PitchCents,
                                               SlCents.Minimum, SlCents.Maximum);
            }
            finally
            {
                _applyingSavedPitch = false;
            }
        }

        private void UpdatePitchLabelsAndSend()
        {
            if (LblSemitones is null || LblCents is null || LblPitchTotal is null) return;

            int semi = (int)Math.Round(SlSemitones.Value);
            int cents = (int)Math.Round(SlCents.Value);

            double totalSemis = semi + cents / 100.0;
            double ratio = Math.Pow(2.0, totalSemis / 12.0);

            LblSemitones.Text = $"{semi:+#;-#;0} st ({NoteName(semi)})";
            LblCents.Text = $"{cents:+#;-#;0} ¢";
            LblPitchTotal.Text = $"{totalSemis:+0.00;-0.00;0.00} st   •   {ratio:F3}×";

            if (_bridge is not null)
                _ = _bridge.SetPitchAsync(totalSemis, 1.0);

            MirrorPitchToCurrentFavorite(semi, cents);
        }

        private void MirrorPitchToCurrentFavorite(int semitones, int cents)
        {
            if (_applyingSavedPitch) return;
            //if (_currentTrack is not { IsFavorite: true } track) return;

            var target = GetPitchTarget();
            if (target is null) return;

            // PropertyChanged(nameof(SavedKeyLabel)) inside the setters
            // refreshes the badge live, whether the target is playing or
            // just selected in the list.
            target.PitchSemitones = semitones;
            target.PitchCents = cents;
        }

        private void FlushFavoritesIfPlayingFavorite()
        {
            if (GetPitchTarget() is not null)
                SaveFavorites();
        }

        private static string NoteName(int semitones)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            int idx = ((semitones % 12) + 12) % 12;
            return names[idx];
        }
        private void BtnResetPitch_Click(object sender, RoutedEventArgs e)
        {
            SlSemitones.Value = 0;
            SlCents.Value = 0;
            FlushFavoritesIfPlayingFavorite();
        }
        private void BtnResetAll_Click(object sender, RoutedEventArgs e)
        {
            SlSemitones.Value = 0;
            SlCents.Value = 0;
            SlTempo.Value = 1.0;
            SlVolume.Value = 100;
            FlushFavoritesIfPlayingFavorite();
        }

        private void BtnKeyDown_Click(object sender, RoutedEventArgs e)
        {
            SlSemitones.Value = Math.Max(SlSemitones.Minimum, SlSemitones.Value - 1);
            FlushFavoritesIfPlayingFavorite();
        }
        private void BtnKeyUp_Click(object sender, RoutedEventArgs e)
        {
            SlSemitones.Value = Math.Min(SlSemitones.Maximum, SlSemitones.Value + 1);
            FlushFavoritesIfPlayingFavorite();
        }
        // =================================================================
        private async void SlTempo_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LblTempo is not null)
                LblTempo.Text = $"{SlTempo.Value:F3}×";

            if (_bridge is not null)
                await _bridge.SetTempoAsync(SlTempo.Value);
        }

        private async void BtnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_bridge is not null) await _bridge.TogglePlayAsync();
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (_bridge is not null) await _bridge.StopAsync();
        }

        private async void BtnSeekBack_Click(object sender, RoutedEventArgs e)
        {
            if (_bridge is null) return;
            await _bridge.SeekAsync(Math.Max(0, _lastPosition - 5));
        }

        private async void BtnSeekForward_Click(object sender, RoutedEventArgs e)
        {
            if (_bridge is null) return;
            var target = _lastPosition + 5;
            if (_lastDuration > 0) target = Math.Min(_lastDuration, target);
            await _bridge.SeekAsync(target);
        }

        private async void BtnTempoDown_Click(object sender, RoutedEventArgs e)
        {
            var v = Math.Round((SlTempo.Value - 0.05) / 0.01) * 0.01;
            SlTempo.Value = Math.Max(SlTempo.Minimum, v);
            if (_bridge is not null) await _bridge.SetTempoAsync(SlTempo.Value);
        }

        private async void BtnTempoUp_Click(object sender, RoutedEventArgs e)
        {
            var v = Math.Round((SlTempo.Value + 0.05) / 0.01) * 0.01;
            SlTempo.Value = Math.Min(SlTempo.Maximum, v);
            if (_bridge is not null) await _bridge.SetTempoAsync(SlTempo.Value);
        }
    }
}