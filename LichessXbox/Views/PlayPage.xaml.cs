using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Newtonsoft.Json.Linq;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    /// <summary>One row of the move list: number + the white and black moves (SAN),
    /// with flags marking which (if either) is the move currently shown on the board.
    /// Notifies on change so rows can be reused (no flicker) as the game advances.</summary>
    public sealed class MoveRowVM : System.ComponentModel.INotifyPropertyChanged
    {
        string _no, _white, _black; bool _wc, _bc; int _wp, _bp; Visibility _bv = Visibility.Visible;
        public string No { get => _no; set { if (_no != value) { _no = value; Raise(nameof(No)); } } }
        public string White { get => _white; set { if (_white != value) { _white = value; Raise(nameof(White)); } } }
        public string Black { get => _black; set { if (_black != value) { _black = value; Raise(nameof(Black)); } } }
        public bool WhiteCurrent { get => _wc; set { if (_wc != value) { _wc = value; Raise(nameof(WhiteCurrent)); } } }
        public bool BlackCurrent { get => _bc; set { if (_bc != value) { _bc = value; Raise(nameof(BlackCurrent)); } } }
        public int WhitePly { get => _wp; set { if (_wp != value) { _wp = value; Raise(nameof(WhitePly)); } } }
        public int BlackPly { get => _bp; set { if (_bp != value) { _bp = value; Raise(nameof(BlackPly)); } } }
        public Visibility BlackVisible { get => _bv; set { if (_bv != value) { _bv = value; Raise(nameof(BlackVisible)); } } }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public sealed partial class PlayPage : Page
    {
        readonly ObservableCollection<TimeControlPreset> _presets = new ObservableCollection<TimeControlPreset>();
        CancellationTokenSource _eventCts;
        CancellationTokenSource _seekCts;
        CancellationTokenSource _gameCts;

        string _gameId;
        bool _autoOpen;   // true after the user starts a pairing here → open the next gameStart
        string _opponentName;
        bool _playerIsWhite = true;
        bool _resultShown;   // guards the game-over overlay against repeated terminal states
        string _initialFen = ChessPosition.StartFen;
        // Captured from gameFull so a rematch reuses the same time control / variant / rated-ness.
        int _gameClockLimitSec = 300, _gameClockIncSec = 3, _gameDays;
        bool _gameRated;
        string _gameVariantKey = "standard";
        TimeControlPreset _friendClock;   // chosen time control for a friend challenge (any speed)
        readonly DispatcherTimer _clockTimer = new DispatcherTimer();

        long _whiteMs, _blackMs;
        bool _whiteToMove = true;
        bool _gameActive;

        readonly List<string> _plies = new List<string>();   // UCI moves of the live game
        int _viewPly;                                         // plies shown on the board (== _plies.Count → live/latest)
        readonly List<string> _sans = new List<string>();    // algebraic notation per ply (cached)
        readonly ObservableCollection<MoveRowVM> _moveRows = new ObservableCollection<MoveRowVM>();

        readonly ObservableCollection<IncomingChallenge> _challenges = new ObservableCollection<IncomingChallenge>();
        ChessVariant _selectedVariant = ChessVariant.All[0];
        string Variant => _selectedVariant?.Key ?? "standard";

        public PlayPage()
        {
            this.InitializeComponent();
            foreach (var p in TimeControlPreset.Defaults) _presets.Add(p);
            PresetGrid.ItemsSource = _presets;

            // Friend challenges allow any speed (Bullet/Blitz included); default to Blitz 5+3.
            var friendClocks = TimeControlPreset.ChallengeClocks;
            FriendClockGrid.ItemsSource = friendClocks;
            _friendClock = friendClocks[3];
            FriendClockGrid.SelectedIndex = 3;

            var levels = new List<AiLevel>();
            for (int i = 1; i <= 8; i++) levels.Add(new AiLevel(i));
            LevelGrid.ItemsSource = levels;

            var variants = ChessVariant.All;
            VariantGrid.ItemsSource = variants;
            _selectedVariant = variants[0];
            VariantGrid.SelectedIndex = 0;

            _challenges.CollectionChanged += (s, e) =>
                ChallengeBanner.Visibility = _challenges.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ChallengesList.ItemsSource = _challenges;

            MoveRows.ItemsSource = _moveRows;
            Board.MoveRequested += Board_MoveRequested;
            _clockTimer.Interval = TimeSpan.FromMilliseconds(200);
            _clockTimer.Tick += ClockTick;

            // Deterministic rail <-> detail focus loop (a GridView/TextBox otherwise swallows D-pad
            // Left, so the rail could become unreachable). Wire both directions explicitly.
            PresetGrid.XYFocusLeft = OnlineModeButton;
            LevelGrid.XYFocusLeft = ComputerModeButton;
            FriendBox.XYFocusLeft = FriendModeButton;
            OnlineModeButton.XYFocusRight = PresetGrid;
            ComputerModeButton.XYFocusRight = LevelGrid;
            FriendModeButton.XYFocusRight = FriendBox;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (!AppState.Current.IsSignedIn)
            {
                ShowOnly(SignInPrompt);
                return;
            }
            StartEventStream();
            // Opened from the "continue playing" panel with a specific game → resume it.
            // Otherwise show the lobby (we no longer auto-jump into an ongoing game on entry).
            if (e.Parameter is string gid && !string.IsNullOrEmpty(gid))
                StartGame(gid);
            else
                ShowOnly(LobbyPanel);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _eventCts?.Cancel();
            _seekCts?.Cancel();
            _gameCts?.Cancel();
            _clockTimer.Stop();
        }

        void ShowOnly(FrameworkElement panel)
        {
            SignInPrompt.Visibility = panel == SignInPrompt ? Visibility.Visible : Visibility.Collapsed;
            LobbyPanel.Visibility = panel == LobbyPanel ? Visibility.Visible : Visibility.Collapsed;
            SeekingPanel.Visibility = panel == SeekingPanel ? Visibility.Visible : Visibility.Collapsed;
            GamePanel.Visibility = panel == GamePanel ? Visibility.Visible : Visibility.Collapsed;

            if (panel == LobbyPanel) SetMode("online");   // reset the rail to the default mode

            // Give the newly shown panel a sensible initial gamepad focus.
            Control target = null;
            if (panel == SignInPrompt) target = GoSignInButton;
            else if (panel == LobbyPanel) target = PresetGrid;   // land on the hero (quick-pair)
            else if (panel == SeekingPanel) target = CancelSeekButton;
            if (panel == GamePanel) Board.FocusBoard();
            else target?.Focus(FocusState.Programmatic);
        }

        // --------------------------------------------------- lobby mode rail

        // The left rail picks a mode; the matching detail panel shows and the active lane tints.
        void SetMode(string mode)
        {
            bool online = mode == "online", computer = mode == "computer", friend = mode == "friend";
            PlayOnlinePanel.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
            ComputerPanel.Visibility = computer ? Visibility.Visible : Visibility.Collapsed;
            FriendPanel.Visibility = friend ? Visibility.Visible : Visibility.Collapsed;
            // Mark the active lane with a green left bar — a different visual channel from the
            // green focus ring, so "selected" and "focused" never blur together.
            OnlineModeAccent.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
            ComputerModeAccent.Visibility = computer ? Visibility.Visible : Visibility.Collapsed;
            FriendModeAccent.Visibility = friend ? Visibility.Visible : Visibility.Collapsed;
        }

        void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is string mode)) return;
            SetMode(mode);
            // Move focus into the chosen panel's primary control.
            Control target = mode == "computer" ? (Control)LevelGrid
                           : mode == "friend" ? (Control)FriendBox
                           : PresetGrid;
            target?.Focus(FocusState.Programmatic);
        }

        void GoSignIn_Click(object sender, RoutedEventArgs e)
        {
            var shell = (Window.Current.Content as Frame)?.Content as MainPage;
            // Profile hosts the sign-in entry; send the user there.
            shell?.NavigateTo("profile");
        }

        // ------------------------------------------------------- event stream

        void StartEventStream()
        {
            _eventCts?.Cancel();
            _eventCts = new CancellationTokenSource();
            var ct = _eventCts.Token;
            // The event feed is subscribed for the whole time we're on this page — always reconnect.
            _ = RunStreamAsync(() => AppState.Current.Api.StreamEventsAsync(OnAccountEvent, ct), ct, () => true);
        }

        void OnAccountEvent(JObject ev)
        {
            string type = ev.Value<string>("type");
            switch (type)
            {
                case "gameStart":
                {
                    string id = ev["game"]?.Value<string>("id") ?? ev["game"]?.Value<string>("gameId");
                    // Only jump into a game the user just started here (seek/challenge/rematch).
                    // Pre-existing games replayed on connect stay in the "continue playing" panel.
                    if (!string.IsNullOrEmpty(id) && _autoOpen) { _autoOpen = false; StartGame(id); }
                    break;
                }
                case "challenge":
                    AddChallenge(ev["challenge"]);
                    break;
                case "challengeCanceled":
                case "challengeDeclined":
                    RemoveChallenge(ev["challenge"]?.Value<string>("id"));
                    break;
            }
        }

        void AddChallenge(JToken c)
        {
            if (c == null) return;
            string id = c.Value<string>("id");
            if (string.IsNullOrEmpty(id)) return;

            // Skip our own outgoing challenges.
            string challengerId = c["challenger"]?.Value<string>("id");
            if (challengerId != null && challengerId == AppState.Current.Account?.Id) return;
            foreach (var existing in _challenges) if (existing.Id == id) return;

            string name = c["challenger"]?.Value<string>("name") ?? "Someone";
            string speed = c.Value<string>("speed") ?? c["variant"]?.Value<string>("name") ?? "Challenge";
            string tc = c["timeControl"]?.Value<string>("show");
            bool rated = c.Value<bool?>("rated") ?? false;
            string desc = char.ToUpperInvariant(speed[0]) + speed.Substring(1);
            if (!string.IsNullOrEmpty(tc)) desc += " · " + tc;
            desc += rated ? " · rated" : " · casual";

            _challenges.Add(new IncomingChallenge { Id = id, ChallengerName = name, Description = desc });
        }

        void RemoveChallenge(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            for (int i = _challenges.Count - 1; i >= 0; i--)
                if (_challenges[i].Id == id) _challenges.RemoveAt(i);
        }

        // ----------------------------------------------------- computer / friends

        async void Ai_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is AiLevel lvl)) return;
            _autoOpen = true;
            SeekingText.Text = $"Starting a game vs Stockfish level {lvl.Level}…";
            ShowOnly(SeekingPanel);
            // Casual, no-clock game vs the AI for a relaxed couch experience.
            var cts = new CancellationTokenSource();
            _seekCts = cts;
            string gameId = await AppState.Current.Api.CreateAiChallengeAsync(lvl.Level, null, Variant);
            // Bail if THIS seek was cancelled or superseded by a newer one while we awaited.
            if (cts.IsCancellationRequested || _seekCts != cts) return;
            if (!string.IsNullOrEmpty(gameId)) StartGame(gameId);
            else if (!_gameActive) ShowOnly(LobbyPanel);
        }

        async void Challenge_Click(object sender, RoutedEventArgs e)
        {
            string user = FriendBox.Text?.Trim();
            if (string.IsNullOrEmpty(user)) return;
            // Use the time control the player picked (Bullet/Blitz/Rapid/Classical — all allowed for challenges).
            var clock = _friendClock ?? new TimeControlPreset("Blitz 5+3", 300, 3, "⚡", false);
            ChallengeErrorText.Visibility = Visibility.Collapsed;
            _autoOpen = true;
            SeekingText.Text = $"Waiting for {user} to accept…";
            ShowOnly(SeekingPanel);
            _seekCts = new CancellationTokenSource();
            bool ok = await AppState.Current.Api.ChallengeUserAsync(user, clock, Variant);
            if (!ok && !_gameActive)
            {
                SeekingText.Text = $"Could not challenge {user}.";
                await Task.Delay(1500);
                if (!_gameActive)
                {
                    // Leave a persistent error on the lobby so the failure isn't missed.
                    ChallengeErrorText.Text = $"Could not challenge {user}. Check the username and try again.";
                    ChallengeErrorText.Visibility = Visibility.Visible;
                    ShowOnly(LobbyPanel);
                }
            }
        }

        async void AcceptChallenge_Click(object sender, RoutedEventArgs e)
        {
            string id = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(id)) return;
            RemoveChallenge(id);
            _autoOpen = true;
            SeekingText.Text = "Joining game…";
            ShowOnly(SeekingPanel);
            await AppState.Current.Api.AcceptChallengeAsync(id);
            // gameStart arrives on the event stream and opens the board.
        }

        async void DeclineChallenge_Click(object sender, RoutedEventArgs e)
        {
            string id = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(id)) return;
            RemoveChallenge(id);
            await AppState.Current.Api.DeclineChallengeAsync(id);
        }

        // ------------------------------------------------------------- seeking

        void Variant_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChessVariant v)
            {
                _selectedVariant = v;
                VariantGrid.SelectedItem = v;
                VariantValueText.Text = v.Name;
                VariantNote.Visibility = v.Key == "standard" ? Visibility.Collapsed : Visibility.Visible;
                VariantFlyout.Hide();
            }
        }

        void FriendClock_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TimeControlPreset c)
            {
                _friendClock = c;
                FriendClockText.Text = c.Label;
                FriendClockFlyout.Hide();
            }
        }

        async void Preset_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is TimeControlPreset preset)) return;
            _autoOpen = true;
            ChallengeErrorText.Visibility = Visibility.Collapsed;
            SeekingText.Text = $"Finding a {preset.Label} game…";
            ShowOnly(SeekingPanel);

            _seekCts = new CancellationTokenSource();
            try { await AppState.Current.Api.CreateSeekAsync(preset, _seekCts.Token, Variant); }
            catch (OperationCanceledException) { /* withdrawn */ }
            catch (Exception ex)
            {
                _autoOpen = false;
                ShowOnly(LobbyPanel);
                ChallengeErrorText.Text = ex.Message;
                ChallengeErrorText.Visibility = Visibility.Visible;
                return;
            }

            // The seek stream closed. If it matched, the gameStart event opens the board — give it
            // a moment to arrive before falling back to the lobby (avoids a false bounce/flicker).
            if (!_gameActive && SeekingPanel.Visibility == Visibility.Visible)
            {
                await Task.Delay(700);
                if (!_gameActive && SeekingPanel.Visibility == Visibility.Visible) ShowOnly(LobbyPanel);
            }
        }

        void CancelSeek_Click(object sender, RoutedEventArgs e)
        {
            _autoOpen = false;
            _seekCts?.Cancel();
            ShowOnly(LobbyPanel);
        }

        // ---------------------------------------------------------- live game

        void StartGame(string gameId)
        {
            if (_gameActive && _gameId == gameId) return;
            _seekCts?.Cancel();
            _gameCts?.Cancel();
            _gameCts = new CancellationTokenSource();
            var ct = _gameCts.Token;

            _gameId = gameId;
            _gameActive = true;
            _plies.Clear();
            _sans.Clear();
            _viewPly = 0;
            ResignButton.Visibility = Visibility.Visible;
            DrawButton.Visibility = Visibility.Visible;
            TakebackButton.Visibility = Visibility.Visible;
            RematchButton.Visibility = Visibility.Collapsed;
            NewGameButton.Visibility = Visibility.Collapsed;
            DrawOfferBanner.Visibility = Visibility.Collapsed;
            ResultOverlay.Visibility = Visibility.Collapsed;
            _resultShown = false;
            Board.Interactive = true;
            ShowOnly(GamePanel);
            // Clear stale design-time labels until the first gameFull arrives.
            StatusBanner.Text = "Connecting…";
            TopName.Text = "";
            BottomName.Text = "";
            TopRating.Text = "";
            BottomRating.Text = "";
            TopCaptured.Text = ""; BottomCaptured.Text = "";
            TopAdvantage.Text = ""; BottomAdvantage.Text = "";

            // Reconnect the board stream only while THIS game is still live (not after the result
            // card shows, and not once a different game has started).
            string gid = gameId;
            _ = RunStreamAsync(() => AppState.Current.Api.StreamBoardGameAsync(gameId, OnGameState, ct), ct,
                               () => _gameId == gid && !_resultShown);
        }

        void OnGameState(JObject msg)
        {
            string type = msg.Value<string>("type");
            if (type == "gameFull")
            {
                _initialFen = msg.Value<string>("initialFen");
                if (string.IsNullOrEmpty(_initialFen) || _initialFen == "startpos")
                    _initialFen = ChessPosition.StartFen;

                var white = msg["white"];
                var black = msg["black"];
                string myId = AppState.Current.Account?.Id;
                // Default to white; flip if our account id is on the black side.
                _playerIsWhite = !(black?.Value<string>("id") == myId);

                SetPlayerLabels(white, black);

                string variantKey = msg["variant"]?.Value<string>("key") ?? "standard";
                Board.Permissive = variantKey != "standard";

                // Remember this game's time control etc. for a faithful rematch.
                var gclock = msg["clock"];
                _gameClockLimitSec = (gclock?.Value<int?>("initial") ?? 300000) / 1000;   // ms → s
                _gameClockIncSec = (gclock?.Value<int?>("increment") ?? 0) / 1000;
                _gameDays = msg.Value<int?>("daysPerTurn") ?? 0;
                _gameRated = msg.Value<bool?>("rated") ?? false;
                _gameVariantKey = variantKey;

                Board.WhiteAtBottom = _playerIsWhite;
                Board.PlayerIsWhite = _playerIsWhite;
                Board.Interactive = true;
                Board.FocusBoard();   // re-focus now that the game is live (focus on first show can fail)

                ApplyState(msg["state"] as JObject);
            }
            else if (type == "gameState")
            {
                ApplyState(msg);
            }
        }

        void SetPlayerLabels(JToken white, JToken black)
        {
            var me = _playerIsWhite ? white : black;
            var them = _playerIsWhite ? black : white;
            BottomName.Text = NameOf(me);
            BottomRating.Text = RatingOf(me);
            TopName.Text = NameOf(them);
            TopRating.Text = RatingOf(them);
            _opponentName = them?.Value<string>("name") ?? them?.Value<string>("id");
        }

        static string NameOf(JToken p)
        {
            if (p == null) return "Anonymous";
            string title = p.Value<string>("title");
            string name = p.Value<string>("name") ?? p.Value<string>("id") ?? "Anonymous";
            return string.IsNullOrEmpty(title) ? name : title + " " + name;
        }

        static string RatingOf(JToken p)
        {
            int? r = p?.Value<int?>("rating");
            return r.HasValue ? r.Value.ToString() : "";
        }

        void ApplyState(JObject state)
        {
            if (state == null) return;

            string moves = state.Value<string>("moves") ?? "";

            // Update ply history for navigation, keeping the user's review position unless
            // they were already viewing the latest (live) move — then follow the new move.
            bool wasLive = _viewPly >= _plies.Count;
            _plies.Clear();
            if (moves.Length > 0)
                foreach (var uci in moves.Split(' '))
                    if (!string.IsNullOrWhiteSpace(uci)) _plies.Add(uci);
            _viewPly = wasLive ? _plies.Count : Math.Min(_viewPly, _plies.Count);

            RebuildSans();   // recompute algebraic notation for the new move list

            // The live position drives the clock turn and the status text below.
            var pos = ChessPosition.FromFen(_initialFen);
            foreach (var uci in _plies) pos = pos.ApplyUciLoose(uci);
            _whiteToMove = pos.WhiteToMove;

            // Render whichever ply is being viewed (live, or a reviewed past position).
            RenderViewedPosition();

            _whiteMs = state.Value<long?>("wtime") ?? _whiteMs;
            _blackMs = state.Value<long?>("btime") ?? _blackMs;
            UpdateClocks();

            string status = state.Value<string>("status") ?? "started";
            string winner = state.Value<string>("winner");

            if (status != "started" && status != "created")
            {
                _gameActive = false;
                _clockTimer.Stop();
                Board.Interactive = false;
                ResignButton.Visibility = Visibility.Collapsed;
                DrawButton.Visibility = Visibility.Collapsed;
                TakebackButton.Visibility = Visibility.Collapsed;
                DrawOfferBanner.Visibility = Visibility.Collapsed;
                RematchButton.Visibility = string.IsNullOrEmpty(_opponentName) ? Visibility.Collapsed : Visibility.Visible;
                NewGameButton.Visibility = Visibility.Visible;
                StatusBanner.Text = ResultText(status, winner);
                // Reveal the satisfying win/loss/draw card over the board (once), which also
                // takes gamepad focus — board cells just lost their tab stops.
                if (!_resultShown) ShowResult(status, winner);
            }
            else
            {
                // Surface an opponent's draw offer.
                bool opponentOffersDraw = (_playerIsWhite ? state.Value<bool?>("bdraw") : state.Value<bool?>("wdraw")) ?? false;
                DrawOfferBanner.Visibility = opponentOffersDraw ? Visibility.Visible : Visibility.Collapsed;

                bool myTurn = pos.WhiteToMove == _playerIsWhite;
                StatusBanner.Text = pos.IsInCheck(pos.WhiteToMove)
                    ? (myTurn ? "You're in check!" : "Check!")
                    : (myTurn ? "Your move" : "Waiting for opponent…");
                if (!_clockTimer.IsEnabled) _clockTimer.Start();
            }
        }

        // ----------------------------------------------------- captured pieces
        static readonly char[] CapGlyph = { '♟', '♞', '♝', '♜', '♛' }; // P N B R Q
        static readonly int[] CapValue = { 1, 3, 3, 5, 9 };
        static readonly int[] CapStart = { 8, 2, 2, 2, 1 };

        // Show each player's taken pieces and a "+N" material lead.
        void UpdateCaptured(ChessPosition pos)
        {
            // start-minus-onboard only makes sense for the standard army; variants
            // (Horde/Crazyhouse/…) run the board in Permissive mode — skip it there.
            if (Board.Permissive)
            {
                TopCaptured.Text = ""; BottomCaptured.Text = "";
                TopAdvantage.Text = ""; BottomAdvantage.Text = "";
                return;
            }
            int[] w = new int[5], b = new int[5];   // P N B R Q (king ignored)
            for (int i = 0; i < 64; i++)
            {
                char p = pos.PieceAt(i);
                if (p == '.') continue;
                int idx = "PNBRQ".IndexOf(char.ToUpperInvariant(p));
                if (idx < 0) continue;
                if (char.IsUpper(p)) w[idx]++; else b[idx]++;
            }
            int adv = 0;
            for (int i = 0; i < 5; i++) adv += CapValue[i] * (w[i] - b[i]); // White minus Black

            string blackTaken = TakenGlyphs(b);   // Black pieces White has captured
            string whiteTaken = TakenGlyphs(w);   // White pieces Black has captured

            // Bottom = the local player, top = the opponent.
            SetCaptured(BottomCaptured, BottomAdvantage, _playerIsWhite ? blackTaken : whiteTaken, _playerIsWhite ? adv : -adv);
            SetCaptured(TopCaptured, TopAdvantage, _playerIsWhite ? whiteTaken : blackTaken, _playerIsWhite ? -adv : adv);
        }

        static string TakenGlyphs(int[] onboard)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 5; i++)
                for (int n = 0; n < CapStart[i] - onboard[i]; n++) sb.Append(CapGlyph[i]);
            return sb.ToString();
        }

        static void SetCaptured(TextBlock glyphs, TextBlock advantage, string taken, int lead)
        {
            glyphs.Text = taken;
            advantage.Text = lead > 0 ? "+" + lead : "";
        }

        string ResultText(string status, string winner)
        {
            // Kept concise and method-free: the result card already shows how the game ended,
            // so the side-panel banner just states the outcome (no duplicate "checkmate", no emoji).
            if (status == "aborted") return "Game aborted";
            if (status == "draw" || status == "stalemate") return "Draw";
            if (string.IsNullOrEmpty(winner)) return "Game over";
            bool iWon = status == "resign"
                ? winner == (_playerIsWhite ? "white" : "black")
                : (winner == "white") == _playerIsWhite;
            return iWon ? "You won" : "You lost";
        }

        // A satisfying win / loss / draw card over the board (lichess / chess.com style):
        // colour-coded accent, big outcome, the method, and quick actions.
        void ShowResult(string status, string winner)
        {
            _resultShown = true;
            ResultRatingRow.Visibility = Visibility.Collapsed;
            SoundService.GameEnd();   // the lichess game-over "dong"

            // 0 = win, 1 = loss, 2 = draw, 3 = aborted
            int kind;
            string method;
            switch (status)
            {
                case "mate":
                    kind = (winner == "white") == _playerIsWhite ? 0 : 1;
                    method = "by checkmate";
                    break;
                case "resign":
                    kind = winner == (_playerIsWhite ? "white" : "black") ? 0 : 1;
                    method = "by resignation";
                    break;
                case "timeout":
                case "outoftime":
                    kind = (winner == "white") == _playerIsWhite ? 0 : 1;
                    method = "by timeout";
                    break;
                case "stalemate":
                    kind = 2; method = "by stalemate";
                    break;
                case "draw":
                    kind = 2; method = "";   // lichess doesn't say agreement vs repetition — don't guess
                    break;
                case "aborted":
                    kind = 3; method = "";
                    break;
                default:
                    if (!string.IsNullOrEmpty(winner)) { kind = (winner == "white") == _playerIsWhite ? 0 : 1; method = ""; }
                    else { kind = 2; method = ""; }
                    break;
            }

            string title;
            ResultIcon.Visibility = Visibility.Collapsed;
            switch (kind)
            {
                case 0:
                    title = "You won!";
                    ResultAccent.Background = Res("AccentGreenBrush");
                    ResultTitle.Foreground = Res("AccentGreenLightBrush");
                    ResultIcon.Text = "";   // filled star
                    ResultIcon.Foreground = Res("AccentGreenLightBrush");
                    ResultIcon.Visibility = Visibility.Visible;
                    break;
                case 1:
                    title = "You lost";
                    ResultAccent.Background = Res("ErrorBrush");
                    ResultTitle.Foreground = Res("TextPrimaryBrush");
                    break;
                case 3:
                    title = "Game aborted";
                    ResultAccent.Background = Res("HairlineBrush");
                    ResultTitle.Foreground = Res("TextPrimaryBrush");
                    break;
                default:
                    title = "Draw";
                    ResultAccent.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xD2, 0x6B));
                    ResultTitle.Foreground = Res("TextPrimaryBrush");
                    break;
            }

            ResultTitle.Text = title;
            ResultMethod.Text = method;
            ResultMethod.Visibility = string.IsNullOrEmpty(method) ? Visibility.Collapsed : Visibility.Visible;
            ResultRematchButton.Visibility = string.IsNullOrEmpty(_opponentName) ? Visibility.Collapsed : Visibility.Visible;

            ResultOverlay.Visibility = Visibility.Visible;
            ((Storyboard)Resources["ResultIn"]).Begin();
            var primary = ResultRematchButton.Visibility == Visibility.Visible ? ResultRematchButton : ResultNewGameButton;
            primary.Focus(FocusState.Programmatic);

            _ = LoadRatingChangeAsync(_gameId, _playerIsWhite);
        }

        // After the game ends, fetch this game's rating + rating change and show it on the card.
        async Task LoadRatingChangeAsync(string gameId, bool amWhite)
        {
            var rc = await AppState.Current.Api.GetGameRatingChangeAsync(gameId, amWhite);
            // Bail if the card was dismissed or a new game started while we waited.
            if (rc == null || !_resultShown || gameId != _gameId) return;

            var (rating, diff) = rc.Value;
            ResultRating.Text = rating.ToString();
            if (diff != 0)
            {
                ResultRatingDelta.Text = (diff > 0 ? "+" : "") + diff;
                ResultRatingDelta.Foreground = Res(diff > 0 ? "AccentGreenLightBrush" : "ErrorBrush");
                ResultRatingDelta.Visibility = Visibility.Visible;
            }
            else ResultRatingDelta.Visibility = Visibility.Collapsed;
            ResultRatingRow.Visibility = Visibility.Visible;
        }

        static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        // Close the card to study the final position; leave the side-panel actions for next steps.
        void DismissResult_Click(object sender, RoutedEventArgs e)
        {
            ResultOverlay.Visibility = Visibility.Collapsed;
            var b = RematchButton.Visibility == Visibility.Visible ? RematchButton : NewGameButton;
            b.Focus(FocusState.Programmatic);
        }

        // Open this game on the analysis board.
        void ReviewGame_Click(object sender, RoutedEventArgs e)
        {
            ResultOverlay.Visibility = Visibility.Collapsed;
            string fen = string.IsNullOrEmpty(_initialFen) ? "startpos" : _initialFen;
            string opp = string.IsNullOrEmpty(_opponentName) ? "Opponent" : _opponentName;
            string white = _playerIsWhite ? "You" : opp;
            string black = _playerIsWhite ? opp : "You";
            string param = fen + "|" + string.Join(" ", _plies) + "|" + white + "|" + black;
            ((Window.Current.Content as Frame)?.Content as LichessXbox.MainPage)?.OpenAnalysis(param);
        }

        // Recompute algebraic notation (SAN) for the whole game; cheap to keep, run only when
        // the move list actually changes. Falls back to raw UCI for unparseable variant moves.
        void RebuildSans()
        {
            // Moves only grow during a game, so append SAN for new plies only; recompute from
            // scratch if the list shrank (takeback). ToSan runs the legal-move generator, so
            // this incremental approach avoids re-deriving the whole game on every move.
            if (_sans.Count > _plies.Count) _sans.Clear();
            var pos = ChessPosition.FromFen(_initialFen);
            for (int i = 0; i < _sans.Count; i++) pos = pos.ApplyUciLoose(_plies[i]);
            for (int i = _sans.Count; i < _plies.Count; i++)
            {
                var mv = ChessMove.FromUci(_plies[i]);
                try { _sans.Add(mv.From >= 0 && mv.From < 64 ? pos.ToSan(mv) : _plies[i]); }
                catch { _sans.Add(_plies[i]); }
                pos = pos.ApplyUciLoose(_plies[i]);
            }
        }

        // Pair the SAN moves into numbered rows, highlighting the move currently on the board.
        void RebuildMoveRows()
        {
            // Reconcile in place — reuse existing rows and only update changed properties — so
            // the list doesn't flicker (full Clear+re-add re-realizes every row each move/nav).
            int rowCount = (_sans.Count + 1) / 2;
            while (_moveRows.Count < rowCount) _moveRows.Add(new MoveRowVM());
            while (_moveRows.Count > rowCount) _moveRows.RemoveAt(_moveRows.Count - 1);
            for (int r = 0; r < rowCount; r++)
            {
                int i = r * 2;
                var row = _moveRows[r];
                row.No = (r + 1) + ".";
                row.White = _sans[i];
                bool hasBlack = i + 1 < _sans.Count;
                row.Black = hasBlack ? _sans[i + 1] : "";
                row.BlackVisible = hasBlack ? Visibility.Visible : Visibility.Collapsed;
                row.WhitePly = i + 1;   // plies applied after white's move on this row
                row.BlackPly = i + 2;   // plies applied after black's move
                row.WhiteCurrent = _viewPly == i + 1;
                row.BlackCurrent = _viewPly == i + 2;
            }
            // Keep the latest move visible while watching live.
            if (_viewPly >= _plies.Count)
            {
                MoveScroller.UpdateLayout();
                MoveScroller.ChangeView(null, MoveScroller.ScrollableHeight, null);
            }
        }

        // ------------------------------------------------------- move navigation

        // Render the position after _viewPly plies — the live game when at the latest move,
        // or a past position while reviewing. Reviewing disables board input.
        void RenderViewedPosition()
        {
            var pos = ChessPosition.FromFen(_initialFen);
            ChessMove last = new ChessMove(-1, -1);
            int upto = Math.Min(_viewPly, _plies.Count);
            for (int i = 0; i < upto; i++)
            {
                var mv = ChessMove.FromUci(_plies[i]);
                pos = pos.ApplyUciLoose(_plies[i]);
                if (mv.From >= 0) last = mv;
            }
            Board.LastMove = last.From >= 0 ? (ChessMove?)last : null;
            Board.Position = pos;
            UpdateCaptured(pos);

            bool live = _viewPly >= _plies.Count;
            Board.Interactive = live && _gameActive;
            UpdateNavUi(live);
            RebuildMoveRows();
        }

        void UpdateNavUi(bool live)
        {
            bool atStart = _viewPly <= 0;
            MoveFirstButton.IsEnabled = !atStart;
            MovePrevButton.IsEnabled = !atStart;
            MoveNextButton.IsEnabled = !live;
            MoveLastButton.IsEnabled = !live;
            MovePositionText.Text = _plies.Count == 0 ? ""
                : live ? "● Live"
                : $"Reviewing — move {_viewPly} of {_plies.Count}";
        }

        void SetViewPly(int ply)
        {
            _viewPly = Math.Max(0, Math.Min(ply, _plies.Count));
            RenderViewedPosition();
            // Returning to the live move during an active game hands input back to the board.
            if (_viewPly >= _plies.Count && _gameActive) Board.FocusBoard();
        }

        // Jump straight to a clicked move in the list.
        void Move_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is int ply) SetViewPly(ply);
        }

        void MoveFirst_Click(object sender, RoutedEventArgs e) => SetViewPly(0);
        void MovePrev_Click(object sender, RoutedEventArgs e) => SetViewPly(_viewPly - 1);
        void MoveNext_Click(object sender, RoutedEventArgs e) => SetViewPly(_viewPly + 1);
        void MoveLast_Click(object sender, RoutedEventArgs e) => SetViewPly(_plies.Count);

        // ------------------------------------------------------------- clocks

        void ClockTick(object sender, object e)
        {
            if (!_gameActive) return;
            if (_whiteToMove) _whiteMs = Math.Max(0, _whiteMs - 200);
            else _blackMs = Math.Max(0, _blackMs - 200);
            UpdateClocks();
        }

        void UpdateClocks()
        {
            long myMs = _playerIsWhite ? _whiteMs : _blackMs;
            long theirMs = _playerIsWhite ? _blackMs : _whiteMs;
            BottomClock.Text = FormatClock(myMs);
            TopClock.Text = FormatClock(theirMs);
            // Tint whoever is on the move so the active clock is obvious at a glance.
            bool myTurn = _whiteToMove == _playerIsWhite;
            HighlightClock(BottomClockChip, _gameActive && myTurn);
            HighlightClock(TopClockChip, _gameActive && !myTurn);
        }

        static void HighlightClock(Border chip, bool active)
        {
            chip.Background = new SolidColorBrush(active
                ? Color.FromArgb(0x55, 0x6F, 0xA6, 0x30)    // green tint = this player's turn
                : Color.FromArgb(0x22, 0x00, 0x00, 0x00));  // resting chip
        }

        static string FormatClock(long ms)
        {
            if (ms <= 0) return "0:00";
            var t = TimeSpan.FromMilliseconds(ms);
            if (t.TotalSeconds < 10) return string.Format("{0}.{1}", t.Seconds, (ms % 1000) / 100);
            if (t.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        // --------------------------------------------------------- user moves

        async void Board_MoveRequested(object sender, ChessMove move)
        {
            if (string.IsNullOrEmpty(_gameId)) return;
            bool ok = await AppState.Current.Api.MakeBoardMoveAsync(_gameId, move.ToUci());
            if (!ok) StatusBanner.Text = "Illegal move — try again.";
            // The game stream will deliver the authoritative new position.
        }

        async void Resign_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_gameId)) return;
            await AppState.Current.Api.ResignAsync(_gameId);
        }

        async void OfferDraw_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_gameId)) return;
            await AppState.Current.Api.OfferDrawAsync(_gameId, true);
            StatusBanner.Text = "Draw offered.";
        }

        async void Takeback_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_gameId)) return;
            await AppState.Current.Api.TakebackAsync(_gameId, true);
            StatusBanner.Text = "Takeback requested.";
        }

        async void AcceptDraw_Click(object sender, RoutedEventArgs e)
        {
            DrawOfferBanner.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(_gameId)) return;
            await AppState.Current.Api.OfferDrawAsync(_gameId, true);
        }

        async void DeclineDraw_Click(object sender, RoutedEventArgs e)
        {
            DrawOfferBanner.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(_gameId)) return;
            await AppState.Current.Api.OfferDrawAsync(_gameId, false);
        }

        async void Rematch_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_opponentName)) return;
            // Reuse the finished game's exact terms. A correspondence game (days per move, no
            // real-time clock) must rematch as correspondence — not silently become a 5+3 blitz.
            TimeControlPreset clock = _gameDays > 0
                ? new TimeControlPreset("Rematch", 0, 0, "📅", _gameRated, _gameDays)
                : new TimeControlPreset("Rematch",
                    _gameClockLimitSec > 0 ? _gameClockLimitSec : 300,
                    _gameClockLimitSec > 0 ? _gameClockIncSec : 3, "⚡", _gameRated);

            _autoOpen = true;
            ChallengeErrorText.Visibility = Visibility.Collapsed;
            SeekingText.Text = $"Rematch — waiting for {_opponentName}…";
            ShowOnly(SeekingPanel);

            // Arm the seek token so the Seeking screen's Cancel button can actually back out, and
            // surface a failure instead of stranding on "waiting…" forever.
            var cts = new CancellationTokenSource();
            _seekCts = cts;
            bool ok;
            try { ok = await AppState.Current.Api.ChallengeUserAsync(_opponentName, clock, _gameVariantKey ?? "standard"); }
            catch { ok = false; }
            if (cts.IsCancellationRequested || _seekCts != cts) return;   // cancelled / superseded
            if (!ok && !_gameActive)
            {
                _autoOpen = false;
                ChallengeErrorText.Text = $"Couldn't send a rematch to {_opponentName}.";
                ChallengeErrorText.Visibility = Visibility.Visible;
                ShowOnly(LobbyPanel);
            }
            // On success the rematch starts via the gameStart event; Cancel exits the wait meanwhile.
        }

        void NewGame_Click(object sender, RoutedEventArgs e)
        {
            _gameCts?.Cancel();
            _gameId = null;
            _autoOpen = false;
            _gameActive = false;
            ShowOnly(LobbyPanel);
        }

        // ----------------------------------------------------- stream runner

        // Run a stream, reconnecting with backoff when it drops — until cancelled or reconnect() says
        // stop. Lichess streams die on any transient blip (console sleep/resume, Wi-Fi hiccup); without
        // this the live board (or the event feed that delivers gameStart/challenges) silently freezes.
        static async Task RunStreamAsync(Func<Task> start, CancellationToken ct, Func<bool> reconnect)
        {
            int delayMs = 1000;
            while (!ct.IsCancellationRequested)
            {
                try { await start(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Stream dropped: " + ex.Message); }

                if (ct.IsCancellationRequested || !reconnect()) return;
                try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { return; }
                delayMs = Math.Min(delayMs * 2, 10000);   // exponential backoff, capped at 10s
            }
        }
    }
}
