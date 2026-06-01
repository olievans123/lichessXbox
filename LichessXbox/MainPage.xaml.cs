using LichessXbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LichessXbox
{
    public sealed partial class MainPage : Page
    {
        string _pendingAnalysisParam;

        public MainPage()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => GoTo("home");
        }

        void Nav_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (tag != null) GoTo(tag);
        }

        /// <summary>Lets pages request navigation (e.g. Home → Play).</summary>
        public void NavigateTo(string tag) => GoTo(tag);

        /// <summary>Open the analysis board pre-loaded with a game ("initialFen|movesUci").</summary>
        public void OpenAnalysis(string param)
        {
            _pendingAnalysisParam = param;
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
        }
    }
}
