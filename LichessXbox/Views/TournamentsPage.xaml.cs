using System.Linq;
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
        bool _joined;   // current selection: are we entered? (Join ↔ Leave toggle)
        readonly System.Collections.Generic.HashSet<string> _joinedIds = new System.Collections.Generic.HashSet<string>();

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
                var all = await AppState.Current.Api.GetTournamentsAsync();
                // Only list tournaments we can actually play (Rapid/Classical via the Board API).
                var tournaments = all?.Where(t => t.Playable).ToList();
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
            _joined = _joinedIds.Contains(t.Id);   // remember arenas we entered this session
            DetailTitle.Text = t.Name;
            // The list is pre-filtered to Board-API-playable arenas (Rapid/Classical), so any
            // selectable tournament can be joined once signed in.
            JoinButton.Visibility = AppState.Current.IsSignedIn && t.Group != "Finished"
                ? Visibility.Visible : Visibility.Collapsed;
            JoinButton.Content = _joined ? "Leave arena" : "Join";
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

        // Join ↔ Leave toggle. While joined, the arena pairs you automatically — games surface
        // via the games-in-progress tab (the pawn, top-left), so we say exactly that.
        async void Join_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId) || !JoinButton.IsEnabled) return;   // guard double-press
            string id = _selectedId;
            bool leaving = _joined;
            JoinButton.IsEnabled = false;
            JoinButton.Content = leaving ? "Leaving…" : "Joining…";
            string err;
            try
            {
                err = leaving
                    ? await AppState.Current.Api.WithdrawTournamentAsync(id)
                    : await AppState.Current.Api.JoinTournamentAsync(id);
            }
            catch { err = leaving ? "Couldn't leave — try again." : "Couldn't join — try again."; }
            if (_selectedId != id) return;   // user moved to another tournament meanwhile
            JoinButton.IsEnabled = true;
            if (err == null)
            {
                _joined = !leaving;
                if (_joined) _joinedIds.Add(id); else _joinedIds.Remove(id);
                JoinButton.Content = _joined ? "Leave arena" : "Join";
                DetailStatus.Text = _joined ? "You're in! Pairings appear in the games tab (the pawn, top-left)." : "";
                DetailStatus.Visibility = _joined ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                JoinButton.Content = leaving ? "Leave arena" : "Join";
                DetailStatus.Text = err;   // the actual reason (lichess error, or re-auth hint on 401)
                DetailStatus.Visibility = Visibility.Visible;
            }
        }
    }
}
