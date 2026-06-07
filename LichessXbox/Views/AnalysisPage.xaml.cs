using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    /// <summary>
    /// Free analysis board: explore any line, with cloud-engine eval, opening
    /// explorer, and tablebase — all from the Lichess APIs.
    /// </summary>
    public sealed partial class AnalysisPage : Page
    {
        readonly List<ChessPosition> _history = new List<ChessPosition> { ChessPosition.Starting() };
        readonly List<ChessMove> _moves = new List<ChessMove>();
        int _ply;

        readonly ObservableCollection<ExplorerMoveRow> _explorer = new ObservableCollection<ExplorerMoveRow>();
        readonly ObservableCollection<TablebaseRow> _tablebase = new ObservableCollection<TablebaseRow>();
        readonly ObservableCollection<MoveRowVM> _moveRows = new ObservableCollection<MoveRowVM>();
        readonly List<string> _sans = new List<string>();   // cached SAN per ply (ToSan runs legal-move gen)
        CancellationTokenSource _analysisCts;
        LocalEngine _engine;
        bool _useLocalEngine;
        bool _engineToggleReady;   // gates LocalEngine_Toggled until the page is loaded
        bool _showEngine;          // engine (Local toggle, or cloud-miss fallback) is the eval source
        string _whiteName, _blackName;

        public AnalysisPage()
        {
            this.InitializeComponent();
            ExplorerList.ItemsSource = _explorer;
            TablebaseList.ItemsSource = _tablebase;
            AnalysisMoveRows.ItemsSource = _moveRows;
            Board.MoveRequested += Board_MoveRequested;
            this.KeyDown += Page_KeyDown;
            // Default to the cloud eval; it falls back to the local Stockfish engine automatically
            // whenever the cloud has no evaluation for the position.
            LocalEngineToggle.IsOn = false;
            this.Loaded += (s, e) =>
            {
                _engineToggleReady = true;
                _useLocalEngine = LocalEngineToggle.IsOn;
                if (_useLocalEngine) EnsureEngine();
                Sync();
                Board.FocusBoard();
            };
        }

        void EnsureEngine()
        {
            if (_engine != null) return;
            _engine = new LocalEngine(EngineWeb);
            _engine.Info += OnEngineInfo;
            _engine.ReadyChanged += OnEngineReady;
        }

        ChessPosition Current => _history[_ply];

        void Board_MoveRequested(object sender, ChessMove move)
        {
            // The move is relative to the currently displayed position.
            var next = Current.Apply(move);
            if (next == null) return;
            PushPosition(next, move);
        }

        void PushPosition(ChessPosition next, ChessMove move)
        {
            // Truncate any forward variation, then append.
            if (_ply < _history.Count - 1)
            {
                _history.RemoveRange(_ply + 1, _history.Count - _ply - 1);
                _moves.RemoveRange(_ply, _moves.Count - _ply);
                // The move at _ply changes with the new variation, so drop its (now stale) SAN too.
                if (_sans.Count > _ply) _sans.RemoveRange(_ply, _sans.Count - _ply);
            }
            _history.Add(next);
            _moves.Add(move);
            _ply = _history.Count - 1;
            Sync();
        }

        void Sync()
        {
            var cur = Current;
            Board.PlayerIsWhite = cur.WhiteToMove;   // free exploration: side-to-move is always "yours"
            Board.Interactive = true;
            // Set LastMove BEFORE Position: assigning Position is what triggers the render (and
            // the slide animation), which needs the new move already in place to detect it.
            Board.LastMove = _ply > 0 ? (ChessMove?)_moves[_ply - 1] : null;
            Board.Position = cur;

            RebuildAnalysisMoves();

            EvalText.Text = "…";
            BestLineText.Text = "Evaluating…";
            _explorer.Clear();
            OpeningText.Text = "Opening explorer";
            OpeningNameText.Text = "";
            ExplorerEmpty.Visibility = Visibility.Collapsed;
            _ = RefreshAnalysisAsync(cur);
        }

        async Task RefreshAnalysisAsync(ChessPosition pos)
        {
            _analysisCts?.Cancel();
            var cts = new CancellationTokenSource();
            _analysisCts = cts;
            string fen = pos.ToFen();

            // Eval source: cloud first (instant for known positions), otherwise the local engine.
            // When the cloud has nothing cached — or the user picked Local — fall back to Stockfish.
            if (_useLocalEngine)
            {
                StartLocalAnalysis(pos);
            }
            else
            {
                _showEngine = false;
                try
                {
                    var eval = await AppState.Current.Api.GetCloudEvalAsync(fen, pos.WhiteToMove);
                    if (cts.IsCancellationRequested) return;
                    if (eval != null)
                    {
                        EvalText.Text = eval.EvalText;
                        BestLineText.Text = string.IsNullOrEmpty(eval.PvUci) ? "" : pos.LineToSan(eval.PvUci.Split(' '));
                        SetEvalBar(eval.WhiteAdvantage);
                        ShowBestArrow(eval.BestMoveUci);
                    }
                    else
                    {
                        StartLocalAnalysis(pos);   // no cloud eval cached → analyse locally
                    }
                }
                catch
                {
                    if (cts.IsCancellationRequested) return;
                    StartLocalAnalysis(pos);   // cloud unreachable → local
                }
            }

            // Opening explorer (lichess masters DB). It moved to explorer.lichess.org and now
            // requires an OAuth token, which GetExplorerAsync sends — so it needs the user signed in.
            try
            {
                var exp = await AppState.Current.Api.GetExplorerAsync(fen);
                if (cts.IsCancellationRequested) return;
                _explorer.Clear();
                bool any = exp != null && exp.Moves.Count > 0;
                if (exp != null) foreach (var m in exp.Moves) _explorer.Add(m);   // San + win/draw/loss bars

                string opening = exp?.OpeningName;
                OpeningNameText.Text = opening ?? "";

                ExplorerEmpty.Text = AppState.Current.IsSignedIn ? "No opening data for this position." : "Sign in to load the opening explorer.";
                ExplorerEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            }
            catch
            {
                if (cts.IsCancellationRequested) return;
                _explorer.Clear();
                ExplorerEmpty.Text = AppState.Current.IsSignedIn ? "Opening explorer unavailable." : "Sign in to load the opening explorer.";
                ExplorerEmpty.Visibility = Visibility.Visible;
                OpeningNameText.Text = "";
            }

            // Tablebase (only for <= 7 pieces)
            try
            {
                if (PieceCount(pos) <= 7)
                {
                    var tb = await AppState.Current.Api.GetTablebaseAsync(fen);
                    if (cts.IsCancellationRequested) return;
                    _tablebase.Clear();
                    if (tb != null && tb.Moves.Count > 0)
                    {
                        TablebaseSummary.Text = "Tablebase · " + tb.Summary;
                        foreach (var m in tb.Moves) _tablebase.Add(m);
                        TablebaseCard.Visibility = Visibility.Visible;
                    }
                    else TablebaseCard.Visibility = Visibility.Collapsed;
                }
                else TablebaseCard.Visibility = Visibility.Collapsed;
            }
            catch { TablebaseCard.Visibility = Visibility.Collapsed; }
        }

        static int PieceCount(ChessPosition p)
        {
            int n = 0;
            for (int i = 0; i < 64; i++) if (p.PieceAt(i) != '.') n++;
            return n;
        }

        void SetEvalBar(double whiteAdvantage)
        {
            double white = Math.Max(0.02, Math.Min(0.98, (whiteAdvantage + 1) / 2));
            WhiteShare.Height = new GridLength(white, GridUnitType.Star);
            BlackShare.Height = new GridLength(1 - white, GridUnitType.Star);
        }

        // ----------------------------------------------------- list interactions

        void Explorer_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ExplorerMoveRow row) PlayUci(row.Uci);
        }

        void Tablebase_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TablebaseRow row) PlayUci(row.Uci);
        }

        void PlayUci(string uci)
        {
            if (string.IsNullOrEmpty(uci)) return;
            var move = ChessMove.FromUci(uci);
            var next = Current.Apply(move);
            if (next != null) PushPosition(next, move);
        }

        // -------------------------------------------------------------- navigation

        void First_Click(object sender, RoutedEventArgs e) { _ply = 0; Sync(); }
        void Back_Click(object sender, RoutedEventArgs e) { if (_ply > 0) { _ply--; Sync(); } }
        void Forward_Click(object sender, RoutedEventArgs e) { if (_ply < _history.Count - 1) { _ply++; Sync(); } }
        void Last_Click(object sender, RoutedEventArgs e) { _ply = _history.Count - 1; Sync(); }
        void Flip_Click(object sender, RoutedEventArgs e)
        {
            Board.WhiteAtBottom = !Board.WhiteAtBottom;
            EvalBarFlip.ScaleY = Board.WhiteAtBottom ? 1 : -1;   // keep White's share on White's side
        }

        // -------------------------------------------------------- local engine

        void LocalEngine_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_engineToggleReady) return;   // ignore the initial IsOn set during construction
            _useLocalEngine = LocalEngineToggle.IsOn;
            if (_useLocalEngine) EnsureEngine();
            else _engine?.Stop();
            Sync();
        }

        // Pair the SAN moves into numbered, clickable rows; highlight the position on the board.
        void RebuildAnalysisMoves()
        {
            int n = _moves.Count;
            // Cache SAN incrementally — ToSan runs the legal-move generator, so never re-derive the
            // whole game on a navigation step. SAN doesn't depend on _ply, so this is append-only.
            if (_sans.Count > n) _sans.RemoveRange(n, _sans.Count - n);
            for (int i = _sans.Count; i < n; i++)
            {
                try { _sans.Add(_history[i].ToSan(_moves[i])); }
                catch { _sans.Add("…"); }
            }

            int rowCount = (n + 1) / 2;
            while (_moveRows.Count < rowCount) _moveRows.Add(new MoveRowVM());
            while (_moveRows.Count > rowCount) _moveRows.RemoveAt(_moveRows.Count - 1);
            for (int r = 0; r < rowCount; r++)
            {
                int i = r * 2;
                var row = _moveRows[r];
                row.No = (r + 1) + ".";
                row.White = _sans[i];
                bool hasBlack = i + 1 < n;
                row.Black = hasBlack ? _sans[i + 1] : "";
                row.BlackVisible = hasBlack ? Visibility.Visible : Visibility.Collapsed;
                row.WhitePly = i + 1;   // jump to the position after white's move on this row
                row.BlackPly = i + 2;
                row.WhiteCurrent = _ply == i + 1;
                row.BlackCurrent = _ply == i + 2;
            }
            MovesEmpty.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
            ScrollCurrentMoveIntoView();
        }

        // Keep the compact move list scrolled to the current move. When a new move comes in the
        // current ply is the latest, so this lands on the bottom row — the list "keeps up" on its own.
        void ScrollCurrentMoveIntoView()
        {
            int rows = _moveRows.Count;
            if (rows == 0) { AnalysisMoveScroller.ChangeView(null, 0d, null, true); return; }
            // Force a layout pass so ExtentHeight/ScrollableHeight reflect the rows just rebuilt.
            // (The board updates on a Canvas, not via layout, so this won't disturb its animation.)
            AnalysisMoveScroller.UpdateLayout();
            double extent = AnalysisMoveScroller.ExtentHeight;
            if (extent <= 0) return;
            double rowH = extent / rows;
            int rowIndex = _ply <= 0 ? 0 : (_ply - 1) / 2;   // the row holding the current ply
            double target = Math.Min(rowIndex * rowH, AnalysisMoveScroller.ScrollableHeight);
            AnalysisMoveScroller.ChangeView(null, target, null, true);
        }

        // Click a move to jump the board to that position.
        void Move_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is int ply)
            {
                _ply = Math.Max(0, Math.Min(ply, _history.Count - 1));
                Sync();
            }
        }

        // Run the local Stockfish engine on this position (booting it on first use). Used both for
        // the Local toggle and as the automatic fallback when the cloud has no eval.
        void StartLocalAnalysis(ChessPosition pos)
        {
            EnsureEngine();
            _showEngine = true;
            EvalText.Text = "…";
            BestLineText.Text = _engine.IsReady ? "Analysing locally…" : "Starting engine…";
            _engine.Analyze(pos);
        }

        // The engine boots asynchronously inside the WebView; once it's up, analyse the
        // position the user is currently looking at (the first toggle-on lands here).
        void OnEngineReady(bool ready)
        {
            if (!ready || !_showEngine || _engine == null) return;
            BestLineText.Text = "Analysing locally…";
            _engine.Analyze(Current);
        }

        void OnEngineInfo(int depth, string evalText, string pvSan, string bestUci)
        {
            if (!_showEngine) return;
            EvalText.Text = evalText;
            if (!string.IsNullOrEmpty(pvSan)) BestLineText.Text = pvSan;
            SetEvalBar(AdvantageFromEval(evalText));
            ShowBestArrow(bestUci);
        }

        // Draw the engine's top move as an arrow on the board (cleared automatically on the next move).
        void ShowBestArrow(string uci)
        {
            if (string.IsNullOrEmpty(uci)) return;
            var bm = ChessMove.FromUci(uci);
            if (bm.From >= 0 && bm.From < 64 && bm.To >= 0 && bm.To < 64) Board.SetBestArrow(bm.From, bm.To);
        }

        static double AdvantageFromEval(string evalText)
        {
            if (string.IsNullOrEmpty(evalText)) return 0;
            if (evalText.StartsWith("#")) return evalText.Contains("-") ? -1 : 1;
            if (double.TryParse(evalText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double pawns))
                return 2.0 / (1.0 + Math.Exp(-0.4 * pawns)) - 1.0;
            return 0;
        }

        void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadLeftShoulder) { Back_Click(null, null); e.Handled = true; }
            else if (e.Key == VirtualKey.GamepadRightShoulder) { Forward_Click(null, null); e.Handled = true; }
        }

        // ---------------------------------------------------------------- import

        void LoadFen_Click(object sender, RoutedEventArgs e)
        {
            string fen = FenBox.Text?.Trim();
            if (string.IsNullOrEmpty(fen)) return;
            try { ResetTo(ChessPosition.FromFen(fen)); }
            catch { BestLineText.Text = "That FEN could not be read."; }
        }

        void StartPos_Click(object sender, RoutedEventArgs e) => ResetTo(ChessPosition.Starting());

        void ResetTo(ChessPosition pos)
        {
            _history.Clear();
            _moves.Clear();
            _sans.Clear();
            _history.Add(pos);
            _ply = 0;
            Sync();
        }

        /// <summary>Load a full game (UCI move list) for replay — used by the Games list.</summary>
        public void LoadGame(string initialFen, string moves)
        {
            _history.Clear();
            _moves.Clear();
            _sans.Clear();
            ChessPosition pos;
            try
            {
                pos = string.IsNullOrEmpty(initialFen) || initialFen == "startpos"
                    ? ChessPosition.Starting() : ChessPosition.FromFen(initialFen);
            }
            catch
            {
                // A malformed/variant FEN must never crash the page on navigation.
                pos = ChessPosition.Starting();
                BestLineText.Text = "Couldn't read that position — starting from the initial board.";
            }
            _history.Add(pos);
            if (!string.IsNullOrWhiteSpace(moves))
            {
                foreach (var token in moves.Split(' '))
                {
                    if (string.IsNullOrWhiteSpace(token)) continue;
                    // Lichess game export gives SAN ("e4 Nf3 O-O exd5 …"); accept UCI too just in case.
                    ChessMove? mv = pos.ParseSan(token);
                    if (mv == null)
                    {
                        var u = ChessMove.FromUci(token);
                        if (u.From >= 0 && u.To >= 0) mv = u;
                    }
                    if (mv == null) break;
                    var next = pos.Apply(mv.Value);
                    if (next == null) break;
                    pos = next;
                    _history.Add(pos);
                    _moves.Add(mv.Value);
                }
            }
            _ply = _history.Count - 1;
            Sync();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Optionally arrive with a game to replay: "initialFen|moves[|whiteName|blackName]".
            if (e.Parameter is string param && param.Contains("|"))
            {
                var parts = param.Split('|');   // FEN/SAN never contain '|', so a full split is safe
                LoadGame(parts[0], parts.Length > 1 ? parts[1] : "");
                _whiteName = parts.Length > 2 ? parts[2] : null;
                _blackName = parts.Length > 3 ? parts[3] : null;
            }
            else { _whiteName = _blackName = null; }
            ShowPlayers();
            // Back-nav (drill-in from Games/Studies) reuses OnNavigatedTo but not Loaded, so land
            // gamepad focus on the board here too — otherwise focus is stranded on the back button.
            Board.FocusBoard();
        }

        void ShowPlayers()
        {
            if (string.IsNullOrWhiteSpace(_whiteName) && string.IsNullOrWhiteSpace(_blackName))
            {
                PlayersText.Visibility = Visibility.Collapsed;
                return;
            }
            PlayersText.Text = (_whiteName ?? "White") + "  vs  " + (_blackName ?? "Black");
            PlayersText.Visibility = Visibility.Visible;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _analysisCts?.Cancel();
            _engine?.Shutdown();   // free the Stockfish WASM heap, don't just halt the search
            _engine = null;
        }
    }
}
