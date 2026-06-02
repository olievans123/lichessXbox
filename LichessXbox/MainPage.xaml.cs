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
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace LichessXbox
{
    public sealed partial class MainPage : Page
    {
        string _pendingAnalysisParam;
        string _currentTag = "home";
        readonly ObservableCollection<OngoingGame> _ongoing = new ObservableCollection<OngoingGame>();
        readonly DispatcherTimer _ongoingTimer = new DispatcherTimer();
        bool _ongoingExpanded;   // tab (collapsed) vs cards (expanded)

        public MainPage()
        {
            this.InitializeComponent();
            this.KeyDown += Page_KeyDown;
            OngoingList.ItemsSource = _ongoing;
            _ongoingTimer.Interval = TimeSpan.FromSeconds(15);
            _ongoingTimer.Tick += async (s, e) => await RefreshOngoingAsync();
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
            if (open) FocusActiveNav();
            else MenuButton.Focus(FocusState.Programmatic);
        }

        void ClosePane()
        {
            NavSplit.IsPaneOpen = false;
            MenuButton.Focus(FocusState.Programmatic);
        }

        // When the pane opens, land focus on the current page's nav item.
        void FocusActiveNav()
        {
            var btn = NavItems.Children.OfType<Button>().FirstOrDefault(b => (b.Tag as string) == _currentTag)
                      ?? NavItems.Children.OfType<Button>().FirstOrDefault();
            btn?.Focus(FocusState.Programmatic);
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

        /// <summary>Open the analysis board pre-loaded with a game ("initialFen|movesUci").</summary>
        public void OpenAnalysis(string param)
        {
            _pendingAnalysisParam = param;
            NavSplit.IsPaneOpen = false;
            GoTo("analysis");
        }

        void GoTo(string tag)
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

            _currentTag = tag;
            HighlightNav(tag);
            _ongoingExpanded = false;    // collapse on navigation so it never sits over the new page
            UpdateOngoingVisibility();
            _ = RefreshOngoingAsync();   // keep the "continue playing" panel current as you move around
        }

        // ----------------------------------------------------- in-progress games

        async Task RefreshOngoingAsync()
        {
            if (!AppState.Current.IsSignedIn)
            {
                if (_ongoing.Count > 0) _ongoing.Clear();
                OngoingPanel.Visibility = Visibility.Collapsed;
                return;
            }
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
            bool show = _currentTag != "play" && _ongoing.Count > 0;
            OngoingTab.Visibility = (show && !_ongoingExpanded) ? Visibility.Visible : Visibility.Collapsed;
            OngoingPanel.Visibility = (show && _ongoingExpanded) ? Visibility.Visible : Visibility.Collapsed;
        }

        void ExpandOngoing_Click(object sender, RoutedEventArgs e)
        {
            _ongoingExpanded = true;
            UpdateOngoingVisibility();
            OngoingCollapseButton.Focus(FocusState.Programmatic);
        }

        void CollapseOngoing_Click(object sender, RoutedEventArgs e)
        {
            _ongoingExpanded = false;
            UpdateOngoingVisibility();
            OngoingTab.Focus(FocusState.Programmatic);
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
