using System;
using LichessXbox.Views;
using muxc = Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) =>
            {
                Nav.SelectedItem = Nav.MenuItems[0];
                ContentFrame.Navigate(typeof(HomePage));
            };
        }

        string _pendingAnalysisParam;

        void Nav_SelectionChanged(muxc.NavigationView sender, muxc.NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is muxc.NavigationViewItem item)
            {
                switch (item.Tag as string)
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
            }
        }

        /// <summary>Open the analysis board pre-loaded with a game ("initialFen|movesUci").</summary>
        public void OpenAnalysis(string param)
        {
            _pendingAnalysisParam = param;
            NavigateTo("analysis");
        }

        /// <summary>Lets pages request navigation (e.g. Home → Play). Searches main and footer items.</summary>
        public void NavigateTo(string tag)
        {
            foreach (var mi in Nav.MenuItems)
                if (mi is muxc.NavigationViewItem item && (item.Tag as string) == tag)
                {
                    Nav.SelectedItem = item;
                    return;
                }
            foreach (var mi in Nav.FooterMenuItems)
                if (mi is muxc.NavigationViewItem item && (item.Tag as string) == tag)
                {
                    Nav.SelectedItem = item;
                    return;
                }
        }
    }
}
