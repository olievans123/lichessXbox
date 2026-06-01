using LichessXbox.Views;
using System.Linq;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

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

            HighlightNav(tag);
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
