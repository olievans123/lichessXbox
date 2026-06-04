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
    public sealed partial class MainPage : Page
    {
        string _pendingAnalysisParam;
        string _currentTag = "home";
        // Pages with a full-height right-side panel: the floating ongoing-games tab would
        // overlap their bottom-right content, so it's suppressed there.
        static readonly System.Collections.Generic.HashSet<string> _noOngoingTabPages =
            new System.Collections.Generic.HashSet<string> { "play", "analysis", "tournaments", "games", "settings", "watch" };
        readonly ObservableCollection<OngoingGame> _ongoing = new ObservableCollection<OngoingGame>();
        readonly DispatcherTimer _ongoingTimer = new DispatcherTimer();
        bool _ongoingExpanded;   // tab (collapsed) vs cards (expanded)

        public MainPage()
        {
            this.InitializeComponent();
            this.KeyDown += Page_KeyDown;
            OngoingList.ItemsSource = _ongoing;
            // Keep nav state in sync on every navigation (forward AND Back); drive the Back button.
            ContentFrame.Navigated += ContentFrame_Navigated;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            ((Storyboard)Resources["OngoingPanelClose"]).Completed += OnOngoingPanelClosed;
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

        // The gamepad Menu (or View) button toggles the nav from anywhere; B / Esc closes it.
        void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadMenu || e.Key == VirtualKey.GamepadView)
            {
                SetPane(!NavSplit.IsPaneOpen);
                e.Handled = true;
            }
            else if (NavSplit.IsPaneOpen && (e.Key == VirtualKey.GamepadB || e.Key == VirtualKey.Escape))
            {
                ClosePane();
                e.Handled = true;
            }
        }

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

        void Back_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        }

        // The gamepad B button (and the system back chrome) route here.
        void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled) return;
            if (NavSplit.IsPaneOpen) { ClosePane(); e.Handled = true; return; }
            if (ContentFrame.CanGoBack) { ContentFrame.GoBack(); e.Handled = true; }
        }

        // Single source of truth for nav state — runs on forward navigation AND on Back.
        void ContentFrame_Navigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _currentTag = TagForPage(e.SourcePageType);
            HighlightNav(_currentTag);
            _ongoingExpanded = false;
            UpdateOngoingVisibility();
            BackButton.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
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
            // ContentFrame_Navigated (fired during Navigate) already synced state; refresh the Back
            // button now that the stack may have been cleared.
            BackButton.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        }

        // ----------------------------------------------------- in-progress games

        bool _refreshingOngoing;   // guards against overlapping timer/navigation refreshes

        async Task RefreshOngoingAsync()
        {
            if (!AppState.Current.IsSignedIn)
            {
                if (_ongoing.Count > 0) _ongoing.Clear();
                OngoingPanel.Visibility = Visibility.Collapsed;
                return;
            }
            if (_refreshingOngoing) return;   // a refresh is already in flight
            _refreshingOngoing = true;
            try { await RefreshOngoingCoreAsync(); }
            finally { _refreshingOngoing = false; }
        }

        async Task RefreshOngoingCoreAsync()
        {
            List<OngoingGame> games;
            try { games = await AppState.Current.Api.GetOngoingGamesAsync(); }
            catch { return; }

            // Only rebuild the cards when something actually changed, so the board snapshots
            // don't re-render (flicker) on every refresh.
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
            // Update the collapsed tab's count + your-move dot.
            OngoingTabCount.Text = _ongoing.Count.ToString();
            bool anyMyTurn = false;
            foreach (var g in _ongoing) if (g.IsMyTurn) { anyMyTurn = true; break; }
            OngoingTabDot.Visibility = anyMyTurn ? Visibility.Visible : Visibility.Collapsed;
            UpdateOngoingVisibility();
        }

        // Show the tab (collapsed) or the cards (expanded) when there are games and we're not on
        // the Play page (which has its own lobby/board and would be overlapped).
        void UpdateOngoingVisibility()
        {
            bool show = !_noOngoingTabPages.Contains(_currentTag) && _ongoing.Count > 0;
            OngoingTab.Visibility = (show && !_ongoingExpanded) ? Visibility.Visible : Visibility.Collapsed;
            OngoingPanel.Visibility = (show && _ongoingExpanded) ? Visibility.Visible : Visibility.Collapsed;
        }

        void ExpandOngoing_Click(object sender, RoutedEventArgs e)
        {
            _ongoingExpanded = true;
            OngoingTab.Visibility = Visibility.Collapsed;
            OngoingPanel.Visibility = Visibility.Visible;
            ((Storyboard)Resources["OngoingPanelOpen"]).Begin();   // slide + fade in
            OngoingCollapseButton.Focus(FocusState.Programmatic);
        }

        void CollapseOngoing_Click(object sender, RoutedEventArgs e)
        {
            _ongoingExpanded = false;
            ((Storyboard)Resources["OngoingPanelClose"]).Begin();   // OnOngoingPanelClosed swaps to the tab
        }

        // After the close animation, hide the panel and bring back the tab (fading it in).
        void OnOngoingPanelClosed(object sender, object e)
        {
            OngoingPanel.Visibility = Visibility.Collapsed;
            if (!_noOngoingTabPages.Contains(_currentTag) && _ongoing.Count > 0)
            {
                OngoingTab.Visibility = Visibility.Visible;
                ((Storyboard)Resources["OngoingTabIn"]).Begin();
                OngoingTab.Focus(FocusState.Programmatic);
            }
        }

        void OngoingGame_Click(object sender, RoutedEventArgs e)
        {
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
            UpdateOngoingVisibility();   // suppressed on Play
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
