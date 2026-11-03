using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace TanieSC_YT_Player
{
    /// <summary>
    /// A "converter" that turns a URL string (like a YouTube thumbnail link)
    /// into an actual image the WPF UI can display.
    ///
    /// Why is this needed?
    ///   In XAML we write things like:
    ///       <Image Source="{Binding ThumbnailUrl, Converter={StaticResource ThumbConv}}" />
    ///   The `Image.Source` property expects an `ImageSource` object, but
    ///   `ThumbnailUrl` is just a plain string. WPF doesn't know how to turn
    ///   one into the other automatically, so it calls this converter to do
    ///   the translation.
    ///
    ///   Think of it like a currency exchange desk: XAML hands over a "URL"
    ///   and expects an "image" in return. This class does that swap.
    /// </summary>
    public sealed class ThumbnailConverter : IValueConverter
    {
        /// <summary>
        /// Called by WPF every time an image needs to be shown (e.g., when a
        /// search result appears in the list, or when scrolling reveals a new
        /// row that was off-screen before).
        ///
        /// Parameters:
        ///   value       – the thing being converted. Here it's the URL string
        ///                 from `TrackInfo.ThumbnailUrl`.
        ///   targetType  – the type WPF wants back (an ImageSource, in this case).
        ///                 We don't actually need to check it because we always
        ///                 return a BitmapImage.
        ///   parameter   – an optional extra value passed from XAML. Not used here.
        ///   culture     – the user's locale (e.g., "en-US"). Not relevant for URLs.
        ///
        /// Returns:
        ///   A BitmapImage ready to be displayed, or `null` if the URL is
        ///   missing or the image can't be loaded.
        /// </summary>
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            // ── Safety checks ──
            // If the value isn't a string, or it's null/blank, there's
            // nothing to show — return null so WPF just displays a blank
            // space instead of crashing.
            if (value is not string url || string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                // ── Build an image from the URL ──
                // BitmapImage has to be configured in two stages:
                // BeginInit() ... set properties ... EndInit().
                // This is a WPF quirk — it delays the actual work until
                // EndInit() so we can set multiple options efficiently.
                var bmp = new BitmapImage();
                bmp.BeginInit();

                // Where the image lives (a URL like https://i.ytimg.com/...).
                bmp.UriSource = new Uri(url, UriKind.Absolute);

                // OnDemand = don't download the whole image immediately; only
                // fetch what's needed for display. For thumbnails this matters
                // when the user has 30 results — they don't all load at once,
                // only the ones actually visible on screen.
                bmp.CacheOption = BitmapCacheOption.OnDemand;

                // DecodePixelWidth = render the image at 224 pixels wide.
                // YouTube thumbnails are 320×180 or larger. We only need a
                // small version for the list, so telling WPF to decode at
                // 224 px wide uses less memory and is faster to draw.
                // (The actual list cell is 112 px wide × 2 for retina-DPI.)
                bmp.DecodePixelWidth = 224;

                // ── Finish ──
                // EndInit() actually kicks off the load. Because we used
                // OnDemand, the actual bytes are fetched lazily in the
                // background — this call returns immediately.
                bmp.EndInit();

                // Give the ready-to-show image back to WPF.
                return bmp;
            }
            catch
            {
                // If anything goes wrong (bad URL, offline, malformed image),
                // swallow the error and return null. A missing thumbnail
                // shouldn't crash the app or spam the user with error dialogs.
                // The list row just shows a blank square where the image
                // would have been.
                return null;
            }
        }

        /// <summary>
        /// The reverse direction: turning an image BACK into a URL.
        /// We never need this — the UI is read-only for thumbnails — so we
        /// throw NotSupportedException. WPF only calls ConvertBack if the
        /// binding is set to TwoWay, which ours isn't.
        /// </summary>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
