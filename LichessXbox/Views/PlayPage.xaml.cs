using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Newtonsoft.Json.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class PlayPage : Page
    {
        readonly ObservableCollection<TimeControlPreset> _presets = new ObservableCollection<TimeControlPreset>();
        CancellationTokenSource _eventCts;
        CancellationTokenSource _seekCts;
        CancellationTokenSource _gameCts;

        string _gameId;
        string _opponentName;
        bool _playerIsWhite = true;
        string _initialFen = ChessPosition.StartFen;
        readonly DispatcherTimer _clockTimer = new DispatcherTimer();

        long _whiteMs, _blackMs;
        bool _whiteToMove = true;
        bool _gameActive;

        readonly ObservableCollection<IncomingChallenge> _challenges = new ObservableCollection<IncomingChallenge>();
        ChessVariant _selectedVariant = ChessVariant.All[0];
        string Variant => _selectedVariant?.Key ?? "standard";

        public PlayPage()
        {
            this.InitializeComponent();
            foreach (var p in TimeControlPreset.Defaults) _presets.Add(p);
            PresetGrid.ItemsSource = _presets;

            var levels = new List<AiLevel>();
            for (int i = 1; i <= 8; i++) levels.Add(new AiLevel(i));
            LevelGrid.ItemsSource = levels;

            var variants = ChessVariant.All;
            VariantGrid.ItemsSource = variants;
            _selectedVariant = variants[0];
            VariantGrid.SelectedIndex = 0;

            _challenges.CollectionChanged += (s, e) =>
                NoChallengesText.Visibility = _challenges.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChallengesList.ItemsSource = _challenges;

            Board.MoveRequested += Board_MoveRequested;
            _clockTimer.Interval = TimeSpan.FromMilliseconds(200);
            _clockTimer.Tick += ClockTick;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (!AppState.Current.IsSignedIn)
            {
                ShowOnly(SignInPrompt);
                return;
            }
            ShowOnly(LobbyPanel);
            StartEventStream();
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

            // Give the newly shown panel a sensible initial gamepad focus.
            Control target = null;
            if (panel == SignInPrompt) target = GoSignInButton;
            else if (panel == LobbyPanel) target = PresetGrid;
            else if (panel == SeekingPanel) target = CancelSeekButton;
            if (panel == GamePanel) Board.FocusBoard();
            else target?.Focus(FocusState.Programmatic);
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
            _ = RunStreamAsync(() => AppState.Current.Api.StreamEventsAsync(OnAccountEvent, ct), ct);
        }

        void OnAccountEvent(JObject ev)
        {
            string type = ev.Value<string>("type");
            switch (type)
            {
                case "gameStart":
                {
                    string id = ev["game"]?.Value<string>("id") ?? ev["game"]?.Value<string>("gameId");
                    if (!string.IsNullOrEmpty(id)) StartGame(id);
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
            SeekingText.Text = $"Starting a game vs Stockfish level {lvl.Level}…";
            ShowOnly(SeekingPanel);
            // Casual, no-clock game vs the AI for a relaxed couch experience.
            _seekCts = new CancellationTokenSource();
            string gameId = await AppState.Current.Api.CreateAiChallengeAsync(lvl.Level, null, Variant);
            if (_seekCts != null && _seekCts.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(gameId)) StartGame(gameId);
            else if (!_gameActive) ShowOnly(LobbyPanel);
        }

        async void Challenge_Click(object sender, RoutedEventArgs e)
        {
            string user = FriendBox.Text?.Trim();
            if (string.IsNullOrEmpty(user)) return;
            // Default to a Blitz 5+3 challenge.
            var clock = new TimeControlPreset("Blitz 5+3", 300, 3, "⚡");
            ChallengeErrorText.Visibility = Visibility.Collapsed;
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
                VariantNote.Visibility = v.Key == "standard" ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        async void Preset_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is TimeControlPreset preset)) return;
            SeekingText.Text = $"Finding a {preset.Label} game…";
            ShowOnly(SeekingPanel);

            _seekCts = new CancellationTokenSource();
            try { await AppState.Current.Api.CreateSeekAsync(preset, _seekCts.Token, Variant); }
            catch { /* cancelled or matched */ }

            // If a game started, StartGame already switched the view. Otherwise return to lobby.
            if (!_gameActive && SeekingPanel.Visibility == Visibility.Visible) ShowOnly(LobbyPanel);
        }

        void CancelSeek_Click(object sender, RoutedEventArgs e)
        {
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
            ResignButton.Visibility = Visibility.Visible;
            DrawButton.Visibility = Visibility.Visible;
            TakebackButton.Visibility = Visibility.Visible;
            RematchButton.Visibility = Visibility.Collapsed;
            NewGameButton.Visibility = Visibility.Collapsed;
            DrawOfferBanner.Visibility = Visibility.Collapsed;
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

            _ = RunStreamAsync(() => AppState.Current.Api.StreamBoardGameAsync(gameId, OnGameState, ct), ct);
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

            // Rebuild the position from the initial FEN + the UCI move list.
            var pos = ChessPosition.FromFen(_initialFen);
            string moves = state.Value<string>("moves") ?? "";
            ChessMove last = new ChessMove(-1, -1);
            if (moves.Length > 0)
            {
                foreach (var uci in moves.Split(' '))
                {
                    if (string.IsNullOrWhiteSpace(uci)) continue;
                    var mv = ChessMove.FromUci(uci);
                    pos = pos.ApplyUciLoose(uci);
                    if (mv.From >= 0) last = mv;
                }
            }

            Board.LastMove = last.From >= 0 ? (ChessMove?)last : null;
            Board.Position = pos;
            _whiteToMove = pos.WhiteToMove;
            UpdateCaptured(pos);

            _whiteMs = state.Value<long?>("wtime") ?? _whiteMs;
            _blackMs = state.Value<long?>("btime") ?? _blackMs;
            UpdateClocks();

            string status = state.Value<string>("status") ?? "started";
            string winner = state.Value<string>("winner");
            UpdateMoveList(moves);

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
            if (status == "mate")
            {
                bool iWon = (winner == "white") == _playerIsWhite;
                return iWon ? "Checkmate — you win! 🎉" : "Checkmate — you lose.";
            }
            if (status == "resign") return winner == (_playerIsWhite ? "white" : "black") ? "Opponent resigned — you win! 🎉" : "You resigned.";
            if (status == "draw" || status == "stalemate") return "Draw.";
            if (status == "timeout" || status == "outoftime")
                return (winner == "white") == _playerIsWhite ? "Opponent flagged — you win! 🎉" : "You ran out of time.";
            if (status == "aborted") return "Game aborted.";
            return "Game over.";
        }

        void UpdateMoveList(string moves)
        {
            if (string.IsNullOrWhiteSpace(moves)) { MoveList.Text = ""; return; }
            var parts = moves.Split(' ');
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 0) sb.Append((i / 2 + 1) + ". ");
                sb.Append(parts[i] + "  ");
            }
            MoveList.Text = sb.ToString();
        }

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
            var clock = new TimeControlPreset("Rematch", 300, 3, "⚡");
            SeekingText.Text = $"Rematch — waiting for {_opponentName}…";
            ShowOnly(SeekingPanel);
            await AppState.Current.Api.ChallengeUserAsync(_opponentName, clock, Variant);
        }

        void NewGame_Click(object sender, RoutedEventArgs e)
        {
            _gameCts?.Cancel();
            _gameId = null;
            _gameActive = false;
            ShowOnly(LobbyPanel);
        }

        // ----------------------------------------------------- stream runner

        static async Task RunStreamAsync(Func<Task> start, CancellationToken ct)
        {
            try { await start(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Stream ended: " + ex.Message); }
        }
    }
}
