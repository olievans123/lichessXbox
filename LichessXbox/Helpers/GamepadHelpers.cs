using System;
using Windows.System;
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
        /// tick so the framework's own post-event focus (item-click, flyout-open) has settled
        /// first — a synchronous Focus() there gets overridden.</summary>
        public static void FocusFirstInside(this Control owner, DependencyObject scope)
        {
            if (owner == null || scope == null) return;
            _ = owner.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                FirstFocusable(scope)?.Focus(FocusState.Keyboard));
        }

        /// <summary>Frame a side-panel card (Moves, Explorer…) with <paramref name="ring"/> while
        /// it holds focus AS A UNIT. Pressing A engages the card: focus moves to an inner item
        /// (which shows its own highlight) and the outer ring HIDES — like the board's whole-board
        /// glow giving way to the per-square cursor. B brings the ring back.</summary>
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

        /// <summary>While <paramref name="host"/> holds focus, the RIGHT stick scrolls
        /// <paramref name="scroller"/> a row at a time. (A focused ScrollViewer would grab the
        /// LEFT stick to scroll, blocking focus from leaving — so the host is a non-scrolling
        /// element and the inner scroller is scrolled here instead.)</summary>
        public static void ScrollOnRightStick(this Control host, ScrollViewer scroller, double step = 28)
        {
            if (host == null || scroller == null) return;
            host.KeyDown += (s, e) =>
            {
                if (e.Key == VirtualKey.GamepadRightThumbstickUp)
                { scroller.ChangeView(null, scroller.VerticalOffset - step, null, false); e.Handled = true; }
                else if (e.Key == VirtualKey.GamepadRightThumbstickDown)
                { scroller.ChangeView(null, scroller.VerticalOffset + step, null, false); e.Handled = true; }
            };
        }

        public static bool IsRunningOnXbox =>
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
    }

    /// <summary>
    /// Manual focus engagement for a scroller full of clickable buttons (the move list).
    /// Built-in ScrollViewer engagement is scroll-mode — the stick scrolls but NO move gets
    /// focus — so we mirror the board: the inner buttons are non-focusable (IsTabStop=false
    /// via style) until the user presses A on the box, which enables them and focuses the
    /// LAST (current) move; B (via the page's back handler) puts them away again. The ring
    /// frames the box only while it's the focus unit.
    /// </summary>
    public sealed class ButtonListEngager
    {
        readonly Control _host;   // a non-scrolling focus host (ContentControl); buttons live inside it
        readonly UIElement _ring;
        bool _engaged;

        /// <summary>Optional: which button A should land on when engaging — return true for the
        /// desired one (e.g. the current move). Unset or no match → first (when set) or last button.</summary>
        public Func<Control, bool> EngageTarget;

        public ButtonListEngager(Control host, UIElement ring)
        {
            _host = host;
            _ring = ring;
            _host.IsTabStop = true;   // the box is a single focus stop (its buttons are not)
            _host.KeyDown += OnKeyDown;
            _host.GotFocus += (s, e) =>
            {
                bool onBox = ReferenceEquals(e.OriginalSource, _host);
                _ring.Visibility = onBox ? Visibility.Visible : Visibility.Collapsed;
                // On the box itself = the un-entered, "highlighted" state. Force the move buttons
                // non-focusable so a stray IsTabStop=true (left on a recycled row after a previous
                // engage) can't be a directional-focus target on the next press.
                if (onBox) { _engaged = false; SetButtonsFocusable(false); }
            };
            _host.LostFocus += (s, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, _host)) _ring.Visibility = Visibility.Collapsed;
            };
            // HARD INVARIANT: while NOT engaged, no descendant move button may receive focus — the
            // ONLY way in is pressing A (OnKeyDown). GettingFocus fires on the host BEFORE focus
            // actually lands on a descendant, and is cancelable/redirectable, so it is the race-free
            // choke point. This holds regardless of HOW a button became a focus candidate: the move
            // buttons are generated by an ItemsControl whose containers are recycled/regenerated on
            // every list rebuild (Sync → RebuildAnalysisMoves mutates the bound ObservableCollection),
            // so SetButtonsFocusable's one-shot tree walk can leave a regenerated container with a
            // stale IsTabStop=true that no later ClearEngaged sweep ever revisits. Bouncing focus back
            // to the host as a unit here makes that leak unreachable instead of trying to plug it.
            _host.GettingFocus += (s, e) =>
            {
                if (_engaged) return;                                   // engaged: inner buttons own focus
                var target = e.NewFocusedElement as DependencyObject;
                if (target == null || ReferenceEquals(target, _host)) return;   // landing on the box itself is fine
                for (var d = target; d != null; d = VisualTreeHelper.GetParent(d))
                {
                    if (ReferenceEquals(d, _host))
                    {
                        // Target is a descendant (a stray-focusable move button). Re-assert the
                        // non-focusable state so it can't be chosen again, and redirect to the host.
                        SetButtonsFocusable(false);
                        if (!e.TrySetNewFocusedElement(_host)) e.Cancel = true;
                        return;
                    }
                }
                // Target is OUTSIDE the host (nav row / Explorer) — let it through.
            };
            // While engaged, focus is trapped inside the moves for the STICK/keys — B is the only
            // way out, and pushing past the first/last move does nothing. But when focus is taken
            // programmatically (the board re-focusing right after a move is played) or by a pointer,
            // we must let it go AND tear the engagement down — otherwise the move buttons stay
            // focusable and the next hover lands straight inside the list, scrolling without an A
            // press. Gamepad/keyboard out → cancel (trap); code/pointer out → disengage cleanly.
            _host.LosingFocus += (s, e) =>
            {
                if (!_engaged) return;
                for (var d = e.NewFocusedElement as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
                    if (ReferenceEquals(d, _host)) return;   // staying inside the moves — keep engaged
                if (e.InputDevice == FocusInputDeviceKind.GameController || e.InputDevice == FocusInputDeviceKind.Keyboard)
                {
                    e.TryCancel();
                    return;
                }
                ClearEngaged();   // let the external focus through, but un-enter the list
            };
        }

        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_engaged) return;
            if (e.Key == VirtualKey.GamepadA || e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space)
            {
                SetButtonsFocusable(true);
                var target = EngageButtonFor(_host);
                if (target == null) { SetButtonsFocusable(false); return; }   // empty list — nothing to enter
                _engaged = true;                       // set BEFORE Focus so the GettingFocus guard early-outs
                target.Focus(FocusState.Keyboard);
                target.StartBringIntoView();           // scroll the list to the engaged (current) move
                _ring.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }

        /// <summary>Re-assert the not-engaged invariant after the list's containers are (re)generated:
        /// every move button back to IsTabStop=false. Safe to call any time we are not engaged; a no-op
        /// guard keeps it from disturbing an active engagement.</summary>
        public void ResyncFocusable()
        {
            if (_engaged) return;
            SetButtonsFocusable(false);
        }

        /// <summary>B: put the buttons away and return to the box. True if it was engaged.</summary>
        public bool Disengage()
        {
            if (!_engaged) return false;
            ClearEngaged();
            _host.Focus(FocusState.Programmatic);   // B returns to the highlighted box
            return true;
        }

        /// <summary>Tear down the entered state WITHOUT moving focus — used when something else
        /// (the board re-focusing, a pointer) takes focus, so the move buttons never stay focusable
        /// behind us (which would let the next hover land inside the list and scroll without an A).</summary>
        void ClearEngaged()
        {
            _engaged = false;
            SetButtonsFocusable(false);
            _ring.Visibility = Visibility.Collapsed;
        }

        void SetButtonsFocusable(bool on)
        {
            void Walk(DependencyObject node)
            {
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(node, i);
                    if (c is Button b) b.IsTabStop = on;
                    Walk(c);
                }
            }
            Walk(_host);
        }

        // The button A lands on when engaging: the current move (via EngageTarget), else the first
        // move when a selector is set (the board sits at the start / there's no current row), else the
        // last visible button (default — live game, or no selector). Single pass over the realized rows.
        Control EngageButtonFor(DependencyObject root)
        {
            Control match = null, first = null, last = null;
            void Walk(DependencyObject node)
            {
                int n = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(node, i);
                    if (c is Button b && b.IsTabStop && b.Visibility == Visibility.Visible)
                    {
                        first = first ?? b;
                        last = b;
                        if (match == null && EngageTarget != null && EngageTarget(b)) match = b;
                    }
                    Walk(c);
                }
            }
            Walk(root);
            return match ?? (EngageTarget != null ? first : last);
        }
    }
}
