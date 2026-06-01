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
        CancellationTokenSource _analysisCts;
        LocalEngine _engine;
        bool _useLocalEngine;

        public AnalysisPage()
        {
            this.InitializeComponent();
            ExplorerList.ItemsSource = _explorer;
            TablebaseList.ItemsSource = _tablebase;
            Board.MoveRequested += Board_MoveRequested;
            this.KeyDown += Page_KeyDown;
            this.Loaded += (s, e) => { Sync(); Board.FocusBoard(); };
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
            }
            _history.Add(next);
            _moves.Add(move);
            _ply = _history.Count - 1;
            Sync();
        }

        void Sync()
        {
            var cur = Current;
            Board.Position = cur;
            Board.PlayerIsWhite = cur.WhiteToMove;   // free exploration: side-to-move is always "yours"
            Board.Interactive = true;
            Board.LastMove = _ply > 0 ? (ChessMove?)_moves[_ply - 1] : null;

            EvalText.Text = "…";
            DepthText.Text = "";
            BestLineText.Text = "Evaluating…";
            _ = RefreshAnalysisAsync(cur);
        }

        async Task RefreshAnalysisAsync(ChessPosition pos)
        {
            _analysisCts?.Cancel();
            var cts = new CancellationTokenSource();
            _analysisCts = cts;
            string fen = pos.ToFen();

            // Engine eval: local Stockfish if enabled, otherwise the cloud.
            if (_useLocalEngine && _engine != null)
            {
                BestLineText.Text = _engine.IsReady ? "Analysing locally…" : "Starting engine…";
                _engine.Analyze(pos);   // results stream back via OnEngineInfo
            }
            else
            {
                try
                {
                    var eval = await AppState.Current.Api.GetCloudEvalAsync(fen, pos.WhiteToMove);
                    if (cts.IsCancellationRequested) return;
                    if (eval != null)
                    {
                        EvalText.Text = eval.EvalText;
                        DepthText.Text = "depth " + eval.Depth;
                        BestLineText.Text = string.IsNullOrEmpty(eval.PvUci) ? "" : pos.LineToSan(eval.PvUci.Split(' '));
                        SetEvalBar(eval.WhiteAdvantage);
                    }
                    else
                    {
                        EvalText.Text = "—";
                        DepthText.Text = "";
                        BestLineText.Text = "No cloud evaluation for this position.";
                        SetEvalBar(0);
                    }
                }
                catch { /* offline / not found */ }
            }

            // Opening explorer
            try
            {
                var exp = await AppState.Current.Api.GetExplorerAsync(fen);
                if (cts.IsCancellationRequested) return;
                _explorer.Clear();
                if (exp != null)
                {
                    OpeningText.Text = string.IsNullOrEmpty(exp.OpeningName) ? "Opening explorer" : exp.OpeningName;
                    foreach (var m in exp.Moves) _explorer.Add(m);
                }
            }
            catch { }

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
        void Flip_Click(object sender, RoutedEventArgs e) { Board.WhiteAtBottom = !Board.WhiteAtBottom; }

        // -------------------------------------------------------- local engine

        void LocalEngine_Toggled(object sender, RoutedEventArgs e)
        {
            _useLocalEngine = LocalEngineToggle.IsOn;
            if (_useLocalEngine && _engine == null)
            {
                _engine = new LocalEngine(EngineWeb);
                _engine.Info += OnEngineInfo;
            }
            if (!_useLocalEngine) _engine?.Stop();
            Sync();
        }

        void OnEngineInfo(int depth, string evalText, string pvSan)
        {
            if (!_useLocalEngine) return;
            EvalText.Text = evalText;
            DepthText.Text = $"depth {depth} · local";
            if (!string.IsNullOrEmpty(pvSan)) BestLineText.Text = pvSan;
            SetEvalBar(AdvantageFromEval(evalText));
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
            ResetTo(ChessPosition.FromFen(fen));
        }

        void StartPos_Click(object sender, RoutedEventArgs e) => ResetTo(ChessPosition.Starting());

        void ResetTo(ChessPosition pos)
        {
            _history.Clear();
            _moves.Clear();
            _history.Add(pos);
            _ply = 0;
            Sync();
        }

        /// <summary>Load a full game (UCI move list) for replay — used by the Games list.</summary>
        public void LoadGame(string initialFen, string movesUci)
        {
            _history.Clear();
            _moves.Clear();
            var pos = string.IsNullOrEmpty(initialFen) || initialFen == "startpos"
                ? ChessPosition.Starting() : ChessPosition.FromFen(initialFen);
            _history.Add(pos);
            if (!string.IsNullOrWhiteSpace(movesUci))
            {
                foreach (var uci in movesUci.Split(' '))
                {
                    if (string.IsNullOrWhiteSpace(uci)) continue;
                    var mv = ChessMove.FromUci(uci);
                    var next = pos.Apply(mv);
                    if (next == null) break;
                    pos = next;
                    _history.Add(pos);
                    _moves.Add(mv);
                }
            }
            _ply = _history.Count - 1;
            Sync();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Optionally arrive with a game to replay: "initialFen|movesUci".
            if (e.Parameter is string param && param.Contains("|"))
            {
                var parts = param.Split(new[] { '|' }, 2);
                LoadGame(parts[0], parts[1]);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _analysisCts?.Cancel();
            _engine?.Stop();
        }
    }
}
