using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class TournamentsPage : Page
    {
        string _selectedId;

        public TournamentsPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            await ReloadAsync();
        }

        async void Retry_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

        async System.Threading.Tasks.Task ReloadAsync()
        {
            Busy.IsActive = true;
            try
            {
                var tournaments = await AppState.Current.Api.GetTournamentsAsync();
                TournamentList.ItemsSource = tournaments;
                if (tournaments == null || tournaments.Count == 0)
                {
                    ListStatus.Text = "No tournaments right now.";
                    ListStatus.Visibility = Visibility.Visible;
                    RetryButton.Visibility = Visibility.Visible;
                    RetryButton.Focus(FocusState.Programmatic);
                }
                else
                {
                    ListStatus.Visibility = Visibility.Collapsed;
                    RetryButton.Visibility = Visibility.Collapsed;
                    TournamentList.Focus(FocusState.Programmatic);
                }
            }
            catch
            {
                ListStatus.Text = "Couldn't load tournaments.";
                ListStatus.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Visible;
                RetryButton.Focus(FocusState.Programmatic);
            }
            finally { Busy.IsActive = false; }
        }

        // Standings rows are read-only; click-enabled only so a gamepad can focus and scroll them.
        void Standings_ItemClick(object sender, ItemClickEventArgs e) { }

        async void Tournament_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is TournamentItem t)) return;
            _selectedId = t.Id;
            DetailTitle.Text = t.Name;
            JoinButton.Visibility = AppState.Current.IsSignedIn && t.Group != "Finished"
                ? Visibility.Visible : Visibility.Collapsed;
            JoinButton.Content = "Join";
            JoinButton.IsEnabled = true;
            StandingsList.ItemsSource = null;

            DetailStatus.Text = "Loading standings…";
            DetailStatus.Visibility = Visibility.Visible;
            try
            {
                var (title, players) = await AppState.Current.Api.GetTournamentStandingsAsync(t.Id);
                if (!string.IsNullOrEmpty(title)) DetailTitle.Text = title;
                StandingsList.ItemsSource = players;
                bool anyStandings = players != null && players.Count > 0;
                DetailStatus.Text = anyStandings ? "" : "No standings yet.";
                DetailStatus.Visibility = anyStandings ? Visibility.Collapsed : Visibility.Visible;
            }
            catch
            {
                DetailStatus.Text = "Standings unavailable.";
                DetailStatus.Visibility = Visibility.Visible;
            }
        }

        async void Join_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            bool ok = await AppState.Current.Api.JoinTournamentAsync(_selectedId);
            JoinButton.Content = ok ? "Joined ✓" : "Join failed";
            JoinButton.IsEnabled = !ok;
        }
    }
}
