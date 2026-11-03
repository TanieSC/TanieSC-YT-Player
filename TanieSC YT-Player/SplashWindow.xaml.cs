using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TanieSC_YT_Player
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Fade the whole window in.
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            BeginAnimation(OpacityProperty, fadeIn);

            // Gently scale the logo from 0.9 to 1.0 at the same time.
            var scaleUp = new DoubleAnimation(0.9, 1.0, TimeSpan.FromMilliseconds(450))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            LogoScaler.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            LogoScaler.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        }

        /// <summary>Fades the splash out; completes when fully transparent.</summary>
        public Task FadeOutAsync(int durationMs = 300)
        {
            var tcs = new TaskCompletionSource<bool>();

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.HoldEnd
            };
            fade.Completed += (_, __) => tcs.SetResult(true);

            BeginAnimation(OpacityProperty, fade);
            return tcs.Task;
        }
    }
}
