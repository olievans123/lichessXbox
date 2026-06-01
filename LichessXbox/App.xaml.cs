using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox
{
    /// <summary>
    /// Application entry point. Configures a 10-foot, gamepad-first experience and
    /// draws the app edge-to-edge so the board fills the TV.
    /// </summary>
    sealed partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
            // Surface any unhandled exception on screen (Debug output is stripped in Release).
            this.UnhandledException += async (s, e) =>
            {
                e.Handled = true;
                try
                {
                    var msg = e.Exception != null ? e.Exception.ToString() : e.Message;
                    if (msg != null && msg.Length > 1200) msg = msg.Substring(0, 1200);
                    await new Windows.UI.Popups.MessageDialog(msg, "Unexpected error").ShowAsync();
                }
                catch { /* dialog itself failed — nothing more we can do */ }
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Restore the user's saved board theme before any board is built.
            LichessXbox.Helpers.BoardTheme.Load();

            // Warm the selected piece set (default: lichess's cburnett) in the background so
            // the SVGs are cached before the first board renders. Fire-and-forget; boards fall
            // back to the built-in Unicode glyphs until it's ready, and self-heal on next load.
            _ = LichessXbox.Helpers.PieceSets.EnsureAsync(LichessXbox.Helpers.BoardTheme.PieceSet);

            // Keep content inside the TV title-safe area (the default). NOTE: opting into
            // UseCoreWindow draws into the overscan region and gets clipped on many TVs.

            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();
            }
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
