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
                var full = e.Exception != null ? e.Exception.ToString() : e.Message;
                try { await LogErrorAsync(full); } catch { }
                try
                {
                    var msg = full != null && full.Length > 1200 ? full.Substring(0, 1200) : full;
                    await new Windows.UI.Popups.MessageDialog(msg, "Unexpected error").ShowAsync();
                }
                catch { /* dialog itself failed — nothing more we can do */ }
            };
        }

        // Append unhandled exceptions to LocalState/error.log so they can be pulled off the device
        // afterwards (Windows Device Portal → filesystem) — Release builds drop Debug output.
        static async System.Threading.Tasks.Task LogErrorAsync(string text)
        {
            var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync("error.log", Windows.Storage.CreationCollisionOption.OpenIfExists);
            await Windows.Storage.FileIO.AppendTextAsync(file, "==== " + DateTimeOffset.Now.ToString("u") + " ====\n" + text + "\n\n");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Restore the user's saved board theme before any board is built.
            LichessXbox.Helpers.BoardTheme.Load();

            // Explicitly apply the UI-sound preference. On Xbox the default ElementSoundPlayer
            // state is "Default" (which still PLAYS), so without this the toggle could read off
            // while sounds played. Setting On/Off keeps the setting and behaviour in sync.
            ElementSoundPlayer.State = LichessXbox.Helpers.BoardTheme.UiSounds
                ? ElementSoundPlayerState.On
                : ElementSoundPlayerState.Off;

            // The default set (cburnett) is bundled in the package and available instantly, so
            // this is a no-op for it. It only does work when the user previously chose a different
            // (downloaded) set — pre-fetching it before the first board renders. Fire-and-forget.
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
