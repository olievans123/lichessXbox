using System;
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
        // Arenas we entered must survive page re-creation (each navigation builds a new
        // instance) — otherwise returning from an arena game forgets we're entered and the
        // pairing watch never resumes for the next round. Static = app-session lifetime.
        static readonly System.Collections.Generic.HashSet<string> _joinedIds = new System.Collections.Generic.HashSet<string>();

        // ---- pairing watch: poll ongoing games while entered; a NEW game = our pairing.
        DispatcherTimer _pairTimer;
        readonly System.Collections.Generic.HashSet<string> _knownGames = new System.Collections.Generic.HashSet<string>();
        bool _watching;
        bool _tickBusy;

        public TournamentsPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            await ReloadAsync();
            // Still entered in an arena (e.g. back from a finished round)? Re-assert
            // pairMeAsap (each pairing needs it afresh — we're never "on the page") and
            // resume the watch so the next pairing opens by itself.
            if (_joinedIds.Count > 0 && AppState.Current.IsSignedIn)
            {
                await NudgeJoinedArenasAsync();
                if (_joinedIds.Count > 0) await StartPairingWatchAsync(true);
            }
        }

        async System.Threading.Tasks.Task NudgeJoinedArenasAsync()
        {
            foreach (var id in _joinedIds.ToList())
            {
                try
                {
                    // Join is idempotent: it unpauses and re-requests a pairing.
                    string err = await AppState.Current.Api.JoinTournamentAsync(id);
                    if (err != null) _joinedIds.Remove(id);   // arena finished/closed — drop it
                }
                catch { /* transient network — keep the arena and try again next visit */ }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) => StopPairingWatch();

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
            // Land focus on Join the moment a tournament is picked, so it's one press to enter
            // (not a hunt across the standings).
            if (JoinButton.Visibility == Visibility.Visible) JoinButton.Focus(FocusState.Programmatic);
            StandingsList.ItemsSource = null;

            // The waiting banner belongs to the arena we ENTERED — selecting a different
            // tournament must not claim we're in that one too. The watch itself keeps
            // running in the background either way.
            if (_watching && _joined)
            {
                ShowWaitingBanner();
            }
            else
            {
                PairRing.IsActive = false;
                PairRing.Visibility = Visibility.Collapsed;
                DetailStatus.Text = "Loading standings…";
                DetailStatus.Visibility = Visibility.Visible;
            }
            await LoadStandingsAsync(t.Id);
        }

        async System.Threading.Tasks.Task LoadStandingsAsync(string id)
        {
            try
            {
                var (title, players) = await AppState.Current.Api.GetTournamentStandingsAsync(id);
                if (_selectedId != id) return;   // a newer selection won the race — don't clobber it
                if (!string.IsNullOrEmpty(title)) DetailTitle.Text = title;
                StandingsList.ItemsSource = players;
                if (_watching && _joined) return;   // keep the "waiting for your pairing" line on screen
                bool anyStandings = players != null && players.Count > 0;
                DetailStatus.Text = anyStandings ? "" : "No standings yet.";
                DetailStatus.Visibility = anyStandings ? Visibility.Collapsed : Visibility.Visible;
            }
            catch
            {
                if (_selectedId != id || _watching) return;
                DetailStatus.Text = "Standings unavailable.";
                DetailStatus.Visibility = Visibility.Visible;
            }
        }

        // Join ↔ Leave toggle. While entered, the arena pairs you automatically — the watch
        // below spots the new game and opens the board without any further input.
        async void Join_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId) || !JoinButton.IsEnabled) return;   // guard double-press
            string id = _selectedId;
            bool leaving = _joined;
            JoinButton.IsEnabled = false;
            JoinButton.Content = leaving ? "Leaving…" : "Joining…";
            // Snapshot BEFORE joining so a pairing that lands instantly still counts as "new".
            if (!leaving) await SnapshotKnownGamesAsync();
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
                if (_joined)
                {
                    await StartPairingWatchAsync(false);   // baseline already snapshotted above
                }
                else
                {
                    StopPairingWatch();
                    DetailStatus.Text = "";
                    DetailStatus.Visibility = Visibility.Collapsed;
                    // Still entered elsewhere? Keep watching for that arena's pairing.
                    if (_joinedIds.Count > 0) await StartPairingWatchAsync(true);
                }
            }
            else
            {
                JoinButton.Content = leaving ? "Leave arena" : "Join";
                StopPairingWatch();
                DetailStatus.Text = err;   // the actual reason (lichess error, or scope hint on 401/403)
                DetailStatus.Visibility = Visibility.Visible;
            }
        }

        // ------------------------------------------------------------ pairing watch

        async System.Threading.Tasks.Task SnapshotKnownGamesAsync()
        {
            // Games that already exist are NOT the pairing we're waiting for.
            _knownGames.Clear();
            try
            {
                var games = await AppState.Current.Api.GetOngoingGamesAsync();
                if (games != null) foreach (var gm in games) _knownGames.Add(gm.GameId);
            }
            catch { /* empty baseline just means the first poll can match an existing game */ }
        }

        async System.Threading.Tasks.Task StartPairingWatchAsync(bool resnapshot)
        {
            if (resnapshot) await SnapshotKnownGamesAsync();
            if (_pairTimer == null)
            {
                _pairTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _pairTimer.Tick += PairTimer_Tick;
            }
            _watching = true;
            _pairTimer.Start();
            // Only claim "you're in" when the selection IS an entered arena (or nothing
            // is selected yet) — not over some other tournament's standings.
            if (_selectedId == null || _joinedIds.Contains(_selectedId)) ShowWaitingBanner();
        }

        void ShowWaitingBanner()
        {
            PairRing.IsActive = true;
            PairRing.Visibility = Visibility.Visible;
            DetailStatus.Text = "You're in — waiting for your pairing. The game will open by itself.";
            DetailStatus.Visibility = Visibility.Visible;
        }

        void StopPairingWatch()
        {
            _watching = false;
            _pairTimer?.Stop();
            PairRing.IsActive = false;
            PairRing.Visibility = Visibility.Collapsed;
        }

        async void PairTimer_Tick(object sender, object e)
        {
            if (_tickBusy) return;   // a slow poll mustn't stack another on top
            _tickBusy = true;
            try
            {
                if (_joinedIds.Count == 0) { StopPairingWatch(); return; }
                System.Collections.Generic.List<OngoingGame> games = null;
                try { games = await AppState.Current.Api.GetOngoingGamesAsync(); } catch { }
                if (games == null) return;
                foreach (var gm in games)
                {
                    if (_knownGames.Contains(gm.GameId)) continue;
                    // A game we've never seen while entered in an arena = our pairing. Go.
                    StopPairingWatch();   // OnNavigatedTo resumes it when we come back
                    ((Window.Current.Content as Frame)?.Content as MainPage)?.OpenGame(gm.GameId);
                    return;
                }
            }
            finally { _tickBusy = false; }
        }
    }
}
