using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace TanieSC_YT_Player
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var splash = new SplashWindow();
            splash.Show();

            await Task.Delay(1500);

            // ── Create the main window at full opacity ──────────────────────────
            var main = new MainWindow();
            MainWindow = main;
            main.Opacity = 1;      // ← base value is 1, NOT 0
            main.Show();

            // ── Fade the main window in with a "From" ramp ──────────────────────
            var mainFadeIn = new DoubleAnimation
            {
                From = 0,                       // start invisible, only for the animation
                To = 1,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop        // ← reverts to base (1) when done
            };

            main.BeginAnimation(UIElement.OpacityProperty, mainFadeIn);
            // No BeginAnimation(..., null) needed — FillBehavior.Stop handles it.

            await splash.FadeOutAsync(300);
            splash.Close();

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }

}
