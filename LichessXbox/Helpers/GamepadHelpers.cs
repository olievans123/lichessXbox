using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace LichessXbox.Helpers
{
    /// <summary>
    /// Small helpers for the 10-foot experience. XYFocus keyboard/gamepad navigation
    /// is on by default in UWP, but a couple of conveniences make the app feel native.
    /// </summary>
    public static class GamepadHelpers
    {
        /// <summary>Give an element initial focus once it is loaded (so a controller has something to land on).</summary>
        public static void FocusOnLoad(this Control control)
        {
            if (control == null) return;
            control.Loaded += (s, e) => control.Focus(FocusState.Programmatic);
        }

        /// <summary>Show <paramref name="ring"/> whenever focus is anywhere inside
        /// <paramref name="host"/> — used to frame a whole side-panel card (Moves, Explorer…)
        /// with the focus ring on the OUTER box instead of the inner row that holds focus.</summary>
        public static void FrameOnFocus(this Control host, UIElement ring)
        {
            if (host == null || ring == null) return;
            host.GotFocus += (s, e) => ring.Visibility = Visibility.Visible;
            host.LostFocus += (s, e) =>
            {
                // Keep the frame while focus moves BETWEEN children of the same card; drop it
                // only when focus has truly left the card's subtree.
                var f = FocusManager.GetFocusedElement() as DependencyObject;
                for (; f != null; f = VisualTreeHelper.GetParent(f))
                    if (ReferenceEquals(f, host)) { ring.Visibility = Visibility.Visible; return; }
                ring.Visibility = Visibility.Collapsed;
            };
        }

        public static bool IsRunningOnXbox =>
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
    }
}
