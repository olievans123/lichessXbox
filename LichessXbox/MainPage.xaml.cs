using LichessXbox.Helpers;
using LichessXbox.Models;
using LichessXbox.Services;
using LichessXbox.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace LichessXbox
{
    /// <summary>A page that can consume the B button before frame-level Back runs —
    /// closing its own overlays / inner panels (result card, chapter list) instead of
    /// leaving the page. Return true when the press was used.</summary>
    public interface IBackHandler
    {
        bool HandleBack();
    }

    public sealed partial class MainPage : Page
    {
        string _pendingAnalysisParam;
        string _currentTag = "home";
        readonly ObservableCollection<OngoingGame> _ongoing = new ObservableCollection<OngoingGame>();
        readonly ObservableCollection<IncomingChallenge> _challenges = new ObservableCollection<IncomingChallenge>();
        readonly DispatcherTimer _ongoingTimer = new DispatcherTimer();
        bool _ongoingFlyoutOpen, _challengeFlyoutOpen;   // don't rebuild a flyout's cards while it's open

        public MainPage()
        {
            this.InitializeComponent();
            this.KeyDown += Page_KeyDown;
            OngoingList.ItemsSource = _ongoing;   // resume cards in the gutter tab's flyout
            ChallengeList.ItemsSource = _challenges;   // incoming-challenge cards in the challenge tab's flyout
            // A 15s poll that rebuilds the cards while their flyout is open recycles the focused
            // card and strands the gamepad — hold rebuilds until the flyout closes, then refresh.
            if (OngoingTab.Flyout != null)
            {
                OngoingTab.Flyout.Opened += (s, e) => { _ongoingFlyoutOpen = true; FocusFirst(OngoingList); };
                OngoingTab.Flyout.Closed += (s, e) => { _ongoingFlyoutOpen = false; _ = RefreshOngoingAsync(); };
            }
            if (ChallengeTab.Flyout != null)
            {
                ChallengeTab.Flyout.Opened += (s, e) => { _challengeFlyoutOpen = true; FocusFirst(ChallengeList); };
                ChallengeTab.Flyout.Closed += (s, e) => { _challengeFlyoutOpen = false; _ = RefreshOngoingAsync(); };
            }
            // Keep nav state in sync on every navigation (forward AND Back); drive the Back button.
            ContentFrame.Navigated += ContentFrame_Navigated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            _ongoingTimer.Interval = TimeSpan.FromSeconds(15);
            _ongoingTimer.Tick += async (s, e) => await RefreshOngoingAsync();
            // Pause the poll while the app is suspended/backgrounded; resume + refresh on return.
            Window.Current.VisibilityChanged += (s, e) =>
            {
                if (e.Visible) { if (!_ongoingTimer.IsEnabled) { _ongoingTimer.Start(); _ = RefreshOngoingAsync(); } }
                else _ongoingTimer.Stop();
            };
            // Pane stays closed at launch so the content gets full width; the page
            // focuses its own first control.
            this.Loaded += async (s, e) =>
            {
                GoTo("home");
                _ongoingTimer.Start();
                await RefreshOngoingAsync();
            };
        }

        // Gamepad Menu toggles the nav from anywhere; View jumps to your games (where arena
        // pairings and resumable boards live) — falling back to the nav when there are none.
        void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadMenu)
            {
                SetPane(!NavSplit.IsPaneOpen);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.GamepadView)
            {
                if (OngoingTab.Visibility == Visibility.Visible) OngoingTab.Flyout?.ShowAt(OngoingTab);
                else SetPane(!NavSplit.IsPaneOpen);
                e.Handled = true;
            }
            else if (NavSplit.IsPaneOpen && (e.Key == VirtualKey.GamepadB || e.Key == VirtualKey.Escape))
            {
                ClosePane();
                e.Handled = true;
            }
            else if (!NavSplit.IsPaneOpen &&
                     (e.Key == VirtualKey.GamepadDPadLeft || e.Key == VirtualKey.GamepadLeftThumbstickLeft || e.Key == VirtualKey.Left))
            {
                // At the left edge of a page (nothing further left inside it), Left jumps to the
                // gutter — the burger, or the games tab if it's showing — so the menu is always
                // one press away. If there IS something to the left in the page, let XY-nav run.
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                if (focused != null && IsDescendantOf(focused, ContentFrame))
                {
                    var next = FocusManager.FindNextElement(
                        FocusNavigationDirection.Left,
                        new FindNextElementOptions { SearchRoot = ContentFrame });
                    if (next == null)
                    {
                        MenuButton.Focus(FocusState.Programmatic);
                        e.Handled = true;
                    }
                }
            }
        }

        static bool IsDescendantOf(DependencyObject node, DependencyObject root)
        {
            for (; node != null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, root)) return true;
            return false;
        }

        // Land focus on the first focusable element inside a just-opened flyout, so its top
        // card is selected immediately (not left on the gutter button behind it). Deferred a
        // tick so the flyout's content is realized before we search it.
        void FocusFirst(DependencyObject scope) => this.FocusFirstInside(scope);

        void ToggleNav_Click(object sender, RoutedEventArgs e) => SetPane(!NavSplit.IsPaneOpen);

        void SetPane(bool open)
        {
            NavSplit.IsPaneOpen = open;
            if (open)
            {
                NavScroller.ChangeView(null, 0, null, true);   // always open scrolled to the top
                (NavItems.Children.OfType<Button>().FirstOrDefault())?.Focus(FocusState.Programmatic);
            }
            else MenuButton.Focus(FocusState.Programmatic);
        }

        void ClosePane()
        {
            NavSplit.IsPaneOpen = false;
            MenuButton.Focus(FocusState.Programmatic);
        }

        // ----------------------------------------------------- back navigation

        // The gamepad B button (and the system back chrome) route here. B peels layers
        // outside-in: nav pane → the page's own overlays/panels → frame back-stack → Home.
        // A root page (no back-stack) routes to Home rather than silently EXITING the app —
        // which is what happened on Puzzles etc. (e.g. focusing the board then pressing B).
        // Only Home itself leaves B unhandled, so the dashboard-exit still works from there.
        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled) return;
            if (NavSplit.IsPaneOpen) { ClosePane(); e.Handled = true; return; }
            if (ContentFrame.Content is IBackHandler page && page.HandleBack()) { e.Handled = true; return; }
            if (ContentFrame.CanGoBack) { ContentFrame.GoBack(); e.Handled = true; return; }
            if (_currentTag != "home") { GoTo("home"); e.Handled = true; }
        }

        // Single source of truth for nav state — runs on forward navigation AND on Back.
        void ContentFrame_Navigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _currentTag = TagForPage(e.SourcePageType);
            HighlightNav(_currentTag);
            UpdateChallengeTab();   // the tab is hidden on Play — reflect the page change immediately
            _ = RefreshOngoingAsync();
        }

        static string TagForPage(Type t)
        {
            if (t == typeof(PlayPage)) return "play";
            if (t == typeof(WatchPage)) return "watch";
            if (t == typeof(AnalysisPage)) return "analysis";
            if (t == typeof(PuzzlesPage)) return "puzzles";
            if (t == typeof(TournamentsPage)) return "tournaments";
            if (t == typeof(StudiesPage)) return "studies";
            if (t == typeof(GamesPage)) return "games";
            if (t == typeof(ProfilePage)) return "profile";
            if (t == typeof(BoardEditorPage)) return "editor";
            if (t == typeof(CoordinatesPage)) return "coords";
            if (t == typeof(SettingsPage)) return "settings";
            return "home";
        }

        void Nav_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (tag == null) return;
            NavSplit.IsPaneOpen = false;   // close first; the destination page focuses its own content
            GoTo(tag);
        }

        /// <summary>Lets pages request navigation (e.g. Home → Play).</summary>
        public void NavigateTo(string tag)
        {
            NavSplit.IsPaneOpen = false;
            GoTo(tag);
        }

        /// <summary>Open the analysis board pre-loaded with a game ("initialFen|movesUci"). This is a
        /// contextual drill-in (from Games/Profile/Review), so it keeps a Back target.</summary>
        public void OpenAnalysis(string param)
        {
            _pendingAnalysisParam = param;
            NavSplit.IsPaneOpen = false;
            GoTo("analysis", drillIn: true);
        }

        void GoTo(string tag, bool drillIn = false)
        {
            switch (tag)
            {
                case "home": ContentFrame.Navigate(typeof(HomePage)); break;
                case "play": ContentFrame.Navigate(typeof(PlayPage)); break;
                case "watch": ContentFrame.Navigate(typeof(WatchPage)); break;
                case "analysis":
                    ContentFrame.Navigate(typeof(AnalysisPage), _pendingAnalysisParam);
                    _pendingAnalysisParam = null;
                    break;
                case "puzzles": ContentFrame.Navigate(typeof(PuzzlesPage)); break;
                case "tournaments": ContentFrame.Navigate(typeof(TournamentsPage)); break;
                case "studies": ContentFrame.Navigate(typeof(StudiesPage)); break;
                case "games": ContentFrame.Navigate(typeof(GamesPage)); break;
                case "profile": ContentFrame.Navigate(typeof(ProfilePage)); break;
                case "editor": ContentFrame.Navigate(typeof(BoardEditorPage)); break;
                case "coords": ContentFrame.Navigate(typeof(CoordinatesPage)); break;
                case "settings": ContentFrame.Navigate(typeof(SettingsPage)); break;
            }
            // A top-level menu/section switch is lateral navigation (the menu IS the nav), so it
            // leaves no Back trail. Only contextual drill-ins (e.g. a game -> analysis) keep one.
            if (!drillIn) ContentFrame.BackStack.Clear();
        }

        // ----------------------------------------------------- in-progress games

        bool _refreshingOngoing;   // guards against overlapping timer/navigation refreshes

        async Task RefreshOngoingAsync()
        {
            if (!AppState.Current.IsSignedIn)
            {
                if (_ongoing.Count > 0) _ongoing.Clear();
                if (_challenges.Count > 0) _challenges.Clear();
                UpdateOngoingTab();
                UpdateChallengeTab();
                return;
            }
            if (_refreshingOngoing) return;   // a refresh is already in flight
            _refreshingOngoing = true;
            try { await RefreshOngoingCoreAsync(); }
            finally { _refreshingOngoing = false; }
        }

        async Task RefreshOngoingCoreAsync()
        {
            List<OngoingGame> games = null;
            try { games = await AppState.Current.Api.GetOngoingGamesAsync(); }
            catch { }
            // Only rebuild the cards when something actually changed, so the board snapshots
            // don't re-render (flicker) on every refresh.
            if (games != null && !_ongoingFlyoutOpen)
            {
                bool same = games.Count == _ongoing.Count;
                if (same)
                    for (int i = 0; i < games.Count; i++)
                        if (games[i].GameId != _ongoing[i].GameId || games[i].IsMyTurn != _ongoing[i].IsMyTurn || games[i].Fen != _ongoing[i].Fen)
                        { same = false; break; }
                if (!same)
                {
                    _ongoing.Clear();
                    foreach (var g in games) _ongoing.Add(g);
                }
            }
            UpdateOngoingTab();

            // Incoming challenges → the gutter challenge tab (independent of the ongoing fetch).
            try
            {
                var challenges = await AppState.Current.Api.GetIncomingChallengesAsync();
                bool same = challenges.Count == _challenges.Count;
                if (same)
                    for (int i = 0; i < challenges.Count; i++)
                        if (challenges[i].Id != _challenges[i].Id) { same = false; break; }
                if (!same && !_challengeFlyoutOpen)
                {
                    _challenges.Clear();
                    foreach (var c in challenges) _challenges.Add(c);
                }
            }
            catch { }
            UpdateChallengeTab();
        }

        // The gutter games tab: shown when there are games in progress, with the count and a
        // your-move dot. Tapping it opens the resume cards (its flyout) on any page.
        void UpdateOngoingTab()
        {
            OngoingTab.Visibility = _ongoing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            OngoingBadgeCount.Text = _ongoing.Count.ToString();
            // iOS-style count badge: green (your move somewhere) vs muted (waiting on opponents).
            bool myTurn = _ongoing.Any(g => g.IsMyTurn);
            OngoingBadge.Background = myTurn
                ? (Brush)Application.Current.Resources["AccentGreenLightBrush"]
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x4A, 0x44));
            OngoingBadgeCount.Foreground = myTurn
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x12, 0x07))
                : (Brush)Application.Current.Resources["TextPrimaryBrush"];
        }

        void OngoingGame_Click(object sender, RoutedEventArgs e)
        {
            OngoingTab.Flyout?.Hide();
            if ((sender as FrameworkElement)?.Tag is string gameId && !string.IsNullOrEmpty(gameId))
                OpenGame(gameId);
        }

        /// <summary>Resume a specific in-progress game on the Play page.</summary>
        public void OpenGame(string gameId)
        {
            NavSplit.IsPaneOpen = false;
            ContentFrame.Navigate(typeof(PlayPage), gameId);
            _currentTag = "play";
            HighlightNav("play");
        }

        // The gutter challenge tab: shown when there are incoming challenges AND we're not on the
        // Play page (which surfaces its own challenge banner in real time).
        void UpdateChallengeTab()
        {
            ChallengeTab.Visibility = _challenges.Count > 0 && _currentTag != "play"
                ? Visibility.Visible : Visibility.Collapsed;
            ChallengeBadgeCount.Text = _challenges.Count.ToString();
        }

        async void AcceptChallenge_Click(object sender, RoutedEventArgs e)
        {
            ChallengeTab.Flyout?.Hide();
            if ((sender as FrameworkElement)?.Tag is string id && !string.IsNullOrEmpty(id))
            {
                bool ok;
                try { ok = await AppState.Current.Api.AcceptChallengeAsync(id); }
                catch { ok = false; }
                // Only open on success — an expired/withdrawn challenge would land on a dead board.
                if (ok) OpenGame(id);   // an accepted challenge's game shares the challenge id
                else await RefreshOngoingAsync();   // re-poll so the stale card disappears
            }
        }

        async void DeclineChallenge_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string id && !string.IsNullOrEmpty(id))
            {
                // Close first: declining the last card would collapse the flyout's content under
                // the focused button and strand gamepad focus in an empty popup.
                ChallengeTab.Flyout?.Hide();
                try { await AppState.Current.Api.DeclineChallengeAsync(id); } catch { }
                await RefreshOngoingAsync();
                (ChallengeTab.Visibility == Visibility.Visible ? ChallengeTab : MenuButton)
                    .Focus(FocusState.Programmatic);
            }
        }

        /// <summary>Marks the active nav button green and resets the rest.</summary>
        void HighlightNav(string tag)
        {
            var activeBg = (Brush)Application.Current.Resources["AccentGreenBrush"];
            var activeFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x12, 0x07));
            var inactiveBg = (Brush)Application.Current.Resources["AppSurfaceHighBrush"];
            var inactiveFg = (Brush)Application.Current.Resources["TextPrimaryBrush"];

            foreach (var b in NavItems.Children.OfType<Button>())
            {
                if ((b.Tag as string) == tag)
                {
                    b.Background = activeBg;
                    b.Foreground = activeFg;
                }
                else
                {
                    b.Background = inactiveBg;
                    b.Foreground = inactiveFg;
                }
            }
        }
    }
}
