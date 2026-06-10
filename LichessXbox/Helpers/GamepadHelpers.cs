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

        /// <summary>First focusable Control in the visual subtree (depth-first) — TextBlocks and
        /// other non-Control elements are skipped, so this returns the first real button/row.</summary>
        public static Control FirstFocusable(DependencyObject root)
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Control c && c.IsTabStop && c.IsEnabled && c.Visibility == Visibility.Visible)
                    return c;
                var deeper = FirstFocusable(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>Focus the first focusable Control inside <paramref name="scope"/>, deferred a
        /// tick so the framework's own post-event focus (engagement, item-click, flyout-open)
        /// has settled first — a synchronous Focus() there gets overridden.</summary>
        public static void FocusFirstInside(this Control owner, DependencyObject scope)
        {
            if (owner == null || scope == null) return;
            _ = owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                FirstFocusable(scope)?.Focus(FocusState.Keyboard));
        }

        /// <summary>Frame a side-panel card (Moves, Explorer…) with <paramref name="ring"/> while
        /// it holds focus AS A UNIT. Pressing A engages the card: focus moves to an inner item
        /// (which shows its own highlight) and the outer ring HIDES — exactly like the board's
        /// whole-board glow giving way to the per-square cursor. B brings the ring back.</summary>
        public static void FrameOnFocus(this Control host, UIElement ring)
        {
            if (host == null || ring == null) return;
            // OriginalSource == host  → the card itself is focused (not engaged) → show the ring.
            // OriginalSource == child → engaged; the inner element owns the highlight → hide it.
            host.GotFocus += (s, e) =>
                ring.Visibility = ReferenceEquals(e.OriginalSource, host) ? Visibility.Visible : Visibility.Collapsed;
            host.LostFocus += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, host)) ring.Visibility = Visibility.Collapsed;
            };
        }

        public static bool IsRunningOnXbox =>
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
    }
}
