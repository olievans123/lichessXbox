using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class PuzzlesPage : Page, IBackHandler
    {
        /// <summary>B drops a held piece / disengages the board before leaving the page —
        /// without this, B on the focused puzzle board fell through and exited the app.</summary>
        public bool HandleBack() => Board.CancelSelection() || Board.DisengageBoard();

        PuzzleInfo _puzzle;
        ChessPosition _start;
        ChessPosition _current;
        bool _solverIsWhite;
        int _solutionIndex;
        bool _solved;
        readonly DispatcherTimer _replyTimer = new DispatcherTimer();
        readonly DispatcherTimer _advanceTimer = new DispatcherTimer();

        string _mode = "training";   // training | streak | themed
        string _theme = "fork";
        int _streak;
        int _loadStamp;   // invalidates in-flight loads when a newer one starts (mode/theme spam)

        static readonly (string Key, string Name)[] Themes =
        {
            ("fork", "Fork"), ("pin", "Pin"), ("skewer", "Skewer"),
            ("mateIn1", "Mate in 1"), ("mateIn2", "Mate in 2"), ("mateIn3", "Mate in 3"),
            ("endgame", "Endgame"), ("opening", "Opening"), ("middlegame", "Middlegame"),
            ("advantage", "Advantage"), ("crushing", "Crushing"), ("sacrifice", "Sacrifice"),
            ("discoveredAttack", "Discovered attack"), ("hangingPiece", "Hanging piece"),
            ("backRankMate", "Back-rank mate"), ("promotion", "Promotion"), ("zugzwang", "Zugzwang"),
        };

        public PuzzlesPage()
        {
            this.InitializeComponent();
            Board.MoveRequested += Board_MoveRequested;
            _replyTimer.Interval = TimeSpan.FromMilliseconds(450);
            _replyTimer.Tick += PlayOpponentReply;
            _advanceTimer.Interval = TimeSpan.FromMilliseconds(900);
            _advanceTimer.Tick += async (s, e) => { _advanceTimer.Stop(); await LoadCurrentAsync(); };

            foreach (var t in Themes)
                ThemeBox.Items.Add(new ComboBoxItem { Content = t.Name, Tag = t.Key });
            ThemeBox.SelectedIndex = 0;
            SetActiveMode(ModeTraining);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadCurrentAsync();

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Otherwise a pending reply/auto-advance tick fires on the detached page —
            // a wasted puzzle fetch and a focus attempt on an off-tree board.
            _replyTimer.Stop();
            _advanceTimer.Stop();
        }

        // ------------------------------------------------------------- modes

        // Active mode = filled green (like the nav active state) so it's clearly distinct
        // from the green focus ring; inactive = the normal surface fill.
        void SetActiveMode(Button active)
        {
            var activeBg = (Brush)Application.Current.Resources["AccentGreenBrush"];
            var activeFg = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x12, 0x07));
            var idleBg = (Brush)Application.Current.Resources["AppSurfaceHighBrush"];
            var idleFg = (Brush)Application.Current.Resources["TextPrimaryBrush"];
            foreach (var b in new[] { ModeTraining, ModeStreak, ModeThemed })
            {
                bool on = b == active;
                b.Background = on ? activeBg : idleBg;
                b.Foreground = on ? activeFg : idleFg;
            }
        }

        void Mode_Click(object sender, RoutedEventArgs e)
        {
            _mode = (sender as FrameworkElement)?.Tag as string ?? "training";
            SetActiveMode(sender as Button);
            TitleText.Text = _mode == "streak" ? "Streak" : _mode == "themed" ? "Themed" : "Training";
            ThemeBox.Visibility = _mode == "themed" ? Visibility.Visible : Visibility.Collapsed;
            StreakCard.Visibility = _mode == "streak" ? Visibility.Visible : Visibility.Collapsed;
            _streak = 0;
            StreakText.Text = "0";
            _ = LoadCurrentAsync();
        }

        void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeBox.SelectedItem is ComboBoxItem item) _theme = item.Tag as string ?? "fork";
            if (_mode == "themed") _ = LoadCurrentAsync();
        }

        async Task LoadCurrentAsync()
        {
            // A newer load (mode/theme switched mid-fetch) supersedes this one; pending board
            // animation timers belong to the old puzzle and must not fire into the new one.
            int stamp = ++_loadStamp;
            _replyTimer.Stop();
            _advanceTimer.Stop();
            Busy.IsActive = true;
            Busy.Visibility = Visibility.Visible;
            ResultCard.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            try
            {
                switch (_mode)
                {
                    case "streak":
                        // Streak is just a local consecutive-solve counter over random puzzles
                        // (there is no public puzzle-streak endpoint). Daily fallback, like training.
                        _puzzle = await AppState.Current.Api.GetNextPuzzleAsync()
                                  ?? await AppState.Current.Api.GetDailyPuzzleAsync();
                        break;
                    case "themed":
                        _puzzle = await AppState.Current.Api.GetThemedPuzzleAsync(_theme);
                        break;
                    default:
                        // A fresh random puzzle each time (works signed in or out); daily as a fallback.
                        _puzzle = await AppState.Current.Api.GetNextPuzzleAsync()
                                  ?? await AppState.Current.Api.GetDailyPuzzleAsync();
                        break;
                }

                if (stamp != _loadStamp) return;   // superseded by a newer load — let it drive the UI

                if (_puzzle == null)
                {
                    HintText.Text = "Couldn't load a puzzle. Try again.";
                    Board.Interactive = false;
                    ResultText.Text = "Couldn't load a puzzle. Try again.";
                    ResultCard.Visibility = Visibility.Visible;
                    RetryButton.Visibility = Visibility.Visible;
                    RetryButton.Focus(FocusState.Programmatic);
                    return;
                }
                BuildStartPosition();
                SetupBoard();
            }
            catch (Exception)
            {
                if (stamp != _loadStamp) return;
                // A fetch/parse failure must never escape as an unhandled exception (it would crash the
                // page). Fall back to the same "couldn't load" state with a working Retry (re-fetches).
                _puzzle = null;
                HintText.Text = "Couldn't load a puzzle.";
                Board.Interactive = false;
                ResultText.Text = "Couldn't load a puzzle. Check your connection and retry.";
                ResultCard.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Visible;
                RetryButton.Focus(FocusState.Programmatic);
            }
            finally
            {
                // Only the load that still owns the page clears the spinner — an older one's
                // finally must not blank out the newer load's busy state.
                if (stamp == _loadStamp) { Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed; }
            }
        }

        // ------------------------------------------------------- puzzle setup

        void BuildStartPosition()
        {
            var tokens = (_puzzle.Pgn ?? "")
                .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => !t.Contains(".") && !t.StartsWith("[") && t != "1-0" && t != "0-1" && t != "1/2-1/2")
                .ToList();

            var positions = new List<ChessPosition> { ChessPosition.Starting() };
            var cur = positions[0];
            foreach (var san in tokens)
            {
                var mv = cur.ParseSan(san);
                if (mv == null) break;
                var next = cur.Apply(mv.Value);
                if (next == null) break;
                cur = next;
                positions.Add(cur);
            }

            int chosen = ChooseStartIndex(positions, _puzzle.InitialPly);
            _start = positions[chosen];
            _current = _start;
            _solverIsWhite = _start.WhiteToMove;
            _solutionIndex = 0;
            _solved = false;
            Board.LastMove = null;
        }

        int ChooseStartIndex(List<ChessPosition> positions, int ply)
        {
            string first = _puzzle.Solution.Count > 0 ? _puzzle.Solution[0] : null;
            foreach (int idx in new[] { ply, ply + 1, ply - 1 })
            {
                if (idx < 0 || idx >= positions.Count) continue;
                if (first == null) return idx;
                if (positions[idx].ApplyUci(first) != null) return idx;
            }
            return Math.Max(0, Math.Min(ply, positions.Count - 1));
        }

        void SetupBoard()
        {
            Board.WhiteAtBottom = _solverIsWhite;
            Board.PlayerIsWhite = _solverIsWhite;
            Board.Interactive = true;
            Board.Position = _current;

            ToMoveText.Text = (_solverIsWhite ? "White" : "Black") + " to move";
            HintText.Text = "Find the best move for " + (_solverIsWhite ? "White." : "Black.");
            RatingText.Text = _puzzle.Rating > 0 ? "Rating " + _puzzle.Rating : "";
            ThemesText.Text = _puzzle.Themes.Count > 0 ? string.Join(" · ", _puzzle.Themes.Take(3)) : "";
            ResultCard.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            Board.FocusBoard();
        }

        // -------------------------------------------------------- solving

        void Board_MoveRequested(object sender, ChessMove move)
        {
            if (_solved || _solutionIndex >= _puzzle.Solution.Count) return;

            string expected = _puzzle.Solution[_solutionIndex];
            if (!MovesEqual(move, expected))
            {
                Board.Position = _current;
                if (_mode == "streak")
                {
                    EndStreak();
                }
                else
                {
                    HintText.Text = "Not quite — try another move.";
                    RetryButton.Visibility = Visibility.Visible;
                }
                return;
            }

            _current = _current.Apply(move) ?? _current;
            _solutionIndex++;
            Board.LastMove = move;
            Board.Position = _current;

            if (_solutionIndex >= _puzzle.Solution.Count) { Solved(); return; }
            HintText.Text = "Good move!";
            _replyTimer.Start();
        }

        void PlayOpponentReply(object sender, object e)
        {
            _replyTimer.Stop();
            if (_solutionIndex >= _puzzle.Solution.Count) return;
            var reply = ChessMove.FromUci(_puzzle.Solution[_solutionIndex]);
            _current = _current.Apply(reply) ?? _current;
            _solutionIndex++;
            Board.LastMove = reply;
            Board.Position = _current;
            if (_solutionIndex >= _puzzle.Solution.Count) Solved();
            else HintText.Text = "Keep going — find the next move.";
        }

        void Solved()
        {
            _solved = true;
            Board.Interactive = false;
            HintText.Text = "Well played.";

            if (_mode == "streak")
            {
                _streak++;
                StreakText.Text = _streak.ToString();
                ResultText.Text = $"Solved! Streak {_streak} 🔥";
                ResultCard.Visibility = Visibility.Visible;
                _advanceTimer.Start();   // auto-load the next puzzle
            }
            else
            {
                ResultText.Text = "Solved! ✓";
                ResultCard.Visibility = Visibility.Visible;
                NextButton.Focus(FocusState.Programmatic);
            }
        }

        void EndStreak()
        {
            _solved = true;
            Board.Interactive = false;
            ResultText.Text = $"Streak ended at {_streak} — press “Next puzzle” to go again.";
            ResultCard.Visibility = Visibility.Visible;
            HintText.Text = "Wrong move — streak over.";
            _streak = 0;
            StreakText.Text = "0";
            NextButton.Focus(FocusState.Programmatic);
        }

        static bool MovesEqual(ChessMove user, string expectedUci)
        {
            var exp = ChessMove.FromUci(expectedUci);
            if (user.From != exp.From || user.To != exp.To) return false;
            char up = user.Promotion == '\0' ? 'q' : user.Promotion;
            char ep = exp.Promotion == '\0' ? 'q' : exp.Promotion;
            return exp.Promotion == '\0' || up == ep;
        }

        async void Next_Click(object sender, RoutedEventArgs e) => await LoadCurrentAsync();

        void Retry_Click(object sender, RoutedEventArgs e)
        {
            // If no puzzle is loaded (e.g. the last Streak/Themed fetch failed), Retry should re-fetch
            // rather than reset to a null start — which would NRE inside SetupBoard (_puzzle.Rating…).
            if (_puzzle == null || _start == null) { _ = LoadCurrentAsync(); return; }
            _current = _start;
            _solutionIndex = 0;
            _solved = false;
            SetupBoard();
            HintText.Text = "Find the best move.";
        }
    }
}
