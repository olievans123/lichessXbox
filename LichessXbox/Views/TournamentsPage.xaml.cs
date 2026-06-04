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
                if (_selectedId != t.Id) return;   // a newer selection won the race — don't clobber it
                if (!string.IsNullOrEmpty(title)) DetailTitle.Text = title;
                StandingsList.ItemsSource = players;
                bool anyStandings = players != null && players.Count > 0;
                DetailStatus.Text = anyStandings ? "" : "No standings yet.";
                DetailStatus.Visibility = anyStandings ? Visibility.Collapsed : Visibility.Visible;
            }
            catch
            {
                if (_selectedId != t.Id) return;
                DetailStatus.Text = "Standings unavailable.";
                DetailStatus.Visibility = Visibility.Visible;
            }
        }

        async void Join_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId) || !JoinButton.IsEnabled) return;   // guard double-press
            string id = _selectedId;
            JoinButton.IsEnabled = false;
            JoinButton.Content = "Joining…";
            bool ok;
            try { ok = await AppState.Current.Api.JoinTournamentAsync(id); }
            catch { ok = false; }
            if (_selectedId != id) return;   // user moved to another tournament while joining
            if (ok)
            {
                JoinButton.Content = "Joined ✓";   // arena game will surface via the continue-playing tab
            }
            else
            {
                JoinButton.Content = "Join";
                JoinButton.IsEnabled = true;
                DetailStatus.Text = "Couldn't join — try again.";
                DetailStatus.Visibility = Visibility.Visible;
            }
        }
    }
}
