using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Helpers;
using LichessXbox.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace LichessXbox.Controls
{
    /// <summary>
    /// A self-contained, gamepad-first chess board.
    ///
    /// Each of the 64 squares is a focusable <see cref="Button"/> (exactly like the
    /// board editor, which works perfectly): the gamepad's native XY-focus moves
    /// between squares, the green focus visual shows the current square, and pressing
    /// A clicks it — pick up a piece, then click a target to move. No custom cursor.
    ///
    /// Squares are focusable/clickable only while <see cref="Interactive"/> is true, so
    /// watch-only boards (TV / replays / the home preview) stay inert.
    /// </summary>
    public sealed partial class ChessBoardControl : UserControl
    {
        // Per-square visuals, indexed 0..63 (a1 = 0).
        readonly Button[] _cells = new Button[64];
        readonly Border[] _highlight = new Border[64];
        readonly Ellipse[] _legalDot = new Ellipse[64];
        readonly Border[] _captureRing = new Border[64];
        readonly Viewbox[] _pieceHost = new Viewbox[64];
        readonly TextBlock[] _pieceFill = new TextBlock[64];
        readonly TextBlock[] _pieceOutline = new TextBlock[64];
        readonly Image[] _pieceImg = new Image[64];   // SVG piece set (lichess); hidden unless a set is active
        // Decoded SVG sources, cached by URI so a piece is decoded once and reused across
        // squares and re-renders (re-creating SvgImageSource every move would flicker the board).
        readonly Dictionary<string, SvgImageSource> _svgCache = new Dictionary<string, SvgImageSource>();

        ChessPosition _position = ChessPosition.Starting();
        readonly List<ChessMove> _legalFromSelected = new List<ChessMove>();
        int _selected = -1;
        ChessMove _pendingPromotion;

        // Sound bookkeeping: play a sound whenever the highlighted last move changes.
        ChessMove? _lastSounded;
        int _lastPieceCount = -1;
        bool _soundReady;

        // Move-animation bookkeeping: a snapshot of the previous render so we can tell a
        // forward move (piece left From, now sits on To) apart from a jump/takeback.
        char[] _prevSquares;
        ChessMove? _prevLastMove;
        Storyboard _animStoryboard;
        FrameworkElement _animFly;     // the piece currently sliding (lives in AnimLayer)
        int _animSquare = -1;          // destination square whose real piece is hidden during the slide

        // Piece glyph colours (shared by the board and the flying overlay piece).
        static readonly Color WhiteFill = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xF6);
        static readonly Color WhiteOutline = Color.FromArgb(0xFF, 0x33, 0x2E, 0x26);
        static readonly Color BlackFill = Color.FromArgb(0xFF, 0x26, 0x22, 0x1C);
        static readonly Color BlackOutline = Color.FromArgb(0xFF, 0xB8, 0xB2, 0xA6);

        // The clean board-cell style (inset green focus ring, no default-button gray)
        // lives in Theme.xaml; resolve it once from the merged app resources.
        static Style _cellStyle;
        static Style CellStyle()
        {
            // The app-level indexer traverses the merged Theme.xaml (same pattern MainPage
            // uses for its brushes), so this resolves the style defined there. Don't cache a
            // null: a board built before Theme.xaml is merged must keep retrying, not get
            // stuck with gray default buttons for the app's lifetime.
            if (_cellStyle != null) return _cellStyle;
            var s = Application.Current.Resources["BoardCellButton"] as Style;
            if (s != null) _cellStyle = s;
            return s;
        }

        public event EventHandler<ChessMove> MoveRequested;

        readonly Action _reTheme;
        EventHandler<object> _focusRetryHandler;   // single pending FocusBoard retry (never stack them)
        int _focusRetryTries;
        int _arrowFrom = -1, _arrowTo = -1;         // best-move arrow squares (−1 = none)

        public ChessBoardControl()
        {
            this.InitializeComponent();
            BuildBoard();
            ApplyInteractivity();
            // Mid-gesture (a piece picked up) the cursor stays on the board: one overshoot
            // at the h-file otherwise dumps focus into the side panel with the clock running.
            // B cancels the selection (via CancelSelection) and frees the cursor again.
            this.LosingFocus += OnLosingFocus;
            _reTheme = () => ReTheme();
            this.Loaded += async (s, e) =>
            {
                // Pair the theme subscription with Loaded/Unloaded (not the ctor) so a recycled
                // board — e.g. a thumbnail in a virtualized list — re-subscribes when it returns.
                BoardTheme.Changed -= _reTheme;
                BoardTheme.Changed += _reTheme;
                Render();
                await EnsurePieceSetAsync();
            };
            this.Unloaded += (s, e) => { BoardTheme.Changed -= _reTheme; DetachFocusRetry(); };
        }

        #region public properties

        public ChessPosition Position
        {
            get => _position;
            // A new position invalidates any best-move arrow; the page re-sets it when the
            // fresh eval arrives.
            set { _position = value ?? ChessPosition.Starting(); ClearSelection(); ClearArrows(); Render(); }
        }

        /// <summary>Bindable FEN — sets the position from a FEN string (for snapshot/thumbnail boards).</summary>
        public static readonly DependencyProperty FenProperty =
            DependencyProperty.Register(nameof(Fen), typeof(string), typeof(ChessBoardControl),
                new PropertyMetadata(null, (d, e) =>
                {
                    var f = e.NewValue as string;
                    if (!string.IsNullOrEmpty(f)) ((ChessBoardControl)d).Position = ChessPosition.FromFen(f);
                }));
        public string Fen
        {
            get => (string)GetValue(FenProperty);
            set => SetValue(FenProperty, value);
        }

        public static readonly DependencyProperty WhiteAtBottomProperty =
            DependencyProperty.Register(nameof(WhiteAtBottom), typeof(bool), typeof(ChessBoardControl),
                // Clear any in-progress selection first: a flip moves every cell, so a stale
                // selection highlight + legal-dots would desync from the gamepad cursor. The arrow
                // survives the flip, so redraw it in the new orientation.
                new PropertyMetadata(true, (d, e) => { var b = (ChessBoardControl)d; b.ClearSelection(); b.Render(); b.DrawArrows(); }));
        public bool WhiteAtBottom
        {
            get => (bool)GetValue(WhiteAtBottomProperty);
            set => SetValue(WhiteAtBottomProperty, value);
        }

        public static readonly DependencyProperty InteractiveProperty =
            DependencyProperty.Register(nameof(Interactive), typeof(bool), typeof(ChessBoardControl),
                new PropertyMetadata(false, (d, e) => ((ChessBoardControl)d).ApplyInteractivity()));
        public bool Interactive
        {
            get => (bool)GetValue(InteractiveProperty);
            set => SetValue(InteractiveProperty, value);
        }

        /// <summary>Opt a non-interactive board into move/capture sounds (e.g. the Watch/TV board).
        /// Interactive boards always sound; previews and thumbnails never do.</summary>
        public static readonly DependencyProperty SoundsEnabledProperty =
            DependencyProperty.Register(nameof(SoundsEnabled), typeof(bool), typeof(ChessBoardControl),
                new PropertyMetadata(false));
        public bool SoundsEnabled
        {
            get => (bool)GetValue(SoundsEnabledProperty);
            set => SetValue(SoundsEnabledProperty, value);
        }

        /// <summary>The colour the local human controls (only relevant when Interactive).</summary>
        public bool PlayerIsWhite { get; set; } = true;

        /// <summary>
        /// In variant games, check rules differ and our standard engine would
        /// over-restrict moves. When true, the board offers pseudo-legal moves as
        /// input hints and defers final legality to the server.
        /// </summary>
        public bool Permissive { get; set; }

        /// <summary>Highlight this move as the most recent one.</summary>
        public ChessMove? LastMove { get; set; }

        string _pieceSetOverride;
        /// <summary>When set, the board renders this piece set instead of the global
        /// <see cref="BoardTheme.PieceSet"/> — used by Settings to preview a set before applying it.</summary>
        public string PieceSetOverride
        {
            get => _pieceSetOverride;
            set { if (_pieceSetOverride == value) return; _pieceSetOverride = value; Render(); }
        }

        #endregion

        #region geometry

        // Map a board square (0..63) to a visual (row, col) honouring orientation.
        void SquareToVisual(int sq, out int row, out int col)
        {
            int file = ChessMove.FileOf(sq), rank = ChessMove.RankOf(sq);
            if (WhiteAtBottom) { row = 7 - rank; col = file; }
            else { row = rank; col = 7 - file; }
        }

        #endregion

        #region build + render

        void BuildBoard()
        {
            var light = BoardTheme.Light;
            var dark = BoardTheme.Dark;

            for (int sq = 0; sq < 64; sq++)
            {
                int file = ChessMove.FileOf(sq), rank = ChessMove.RankOf(sq);
                bool isLight = (file + rank) % 2 == 1;

                var content = new Grid();

                var hl = new Border { Background = new SolidColorBrush(Color.FromArgb(0x88, 0xF2, 0xD2, 0x6B)), Visibility = Visibility.Collapsed };
                content.Children.Add(hl);
                _highlight[sq] = hl;

                var ring = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                    BorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(90),
                    Margin = new Thickness(4),
                    Visibility = Visibility.Collapsed
                };
                content.Children.Add(ring);
                _captureRing[sq] = ring;

                var dot = new Ellipse
                {
                    Width = 26, Height = 26,
                    Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x14, 0x14, 0x14)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                content.Children.Add(dot);
                _legalDot[sq] = dot;

                // Piece = outline glyph behind a fill glyph for crisp readability on any square.
                var outline = MakeGlyph();
                var fill = MakeGlyph();
                var pieceGrid = new Grid();
                pieceGrid.Children.Add(outline);
                pieceGrid.Children.Add(fill);
                var host = new Viewbox { Child = pieceGrid, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
                content.Children.Add(host);
                _pieceHost[sq] = host;
                _pieceFill[sq] = fill;
                _pieceOutline[sq] = outline;

                // SVG piece image (shown instead of the glyphs when a downloaded set is active).
                var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                content.Children.Add(img);
                _pieceImg[sq] = img;

                // coordinate hints in the corners (a-h on rank 1, 1-8 on file a)
                if (rank == 0 || file == 0)
                {
                    var coord = new TextBlock
                    {
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(isLight ? dark : light),
                        Opacity = 0.85,
                        Margin = new Thickness(4, 2, 4, 2),
                        IsHitTestVisible = false,
                    };
                    coord.Text = rank == 0 ? ((char)('a' + file)).ToString() : (rank + 1).ToString();
                    coord.HorizontalAlignment = rank == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    coord.VerticalAlignment = rank == 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    content.Children.Add(coord);
                }

                // The square itself is a focusable button — native gamepad nav + green focus.
                var cell = new Button
                {
                    Style = CellStyle(),
                    Background = new SolidColorBrush(isLight ? light : dark),
                    Content = content,
                    Tag = sq,
                };
                cell.Click += Cell_Click;
                Grid.SetRow(cell, 0);
                Grid.SetColumn(cell, 0); // positioned in Render()
                _cells[sq] = cell;
                BoardGrid.Children.Add(cell);
            }
        }

        static TextBlock MakeGlyph()
        {
            return new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 100,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
        }

        // Solid (filled) glyphs for both colours — recoloured per side.
        static string Glyph(char piece)
        {
            switch (char.ToUpperInvariant(piece))
            {
                case 'K': return "♚";
                case 'Q': return "♛";
                case 'R': return "♜";
                case 'B': return "♝";
                case 'N': return "♞";
                case 'P': return "♟";
                default: return "";
            }
        }

        // Optional outline piece set for White (classic "hollow" look).
        static string GlyphFor(char piece)
        {
            if (BoardTheme.OutlinePieces && char.IsUpper(piece))
            {
                switch (piece)
                {
                    case 'K': return "♔";
                    case 'Q': return "♕";
                    case 'R': return "♖";
                    case 'B': return "♗";
                    case 'N': return "♘";
                    case 'P': return "♙";
                }
            }
            return Glyph(piece);
        }

        /// <summary>Squares are focusable/clickable only while interactive.</summary>
        void ApplyInteractivity()
        {
            bool on = Interactive;
            // The board itself is a gamepad focus stop only while interactive, so a watch /
            // preview / reviewed board never steals focus (live boards focus a cell directly).
            IsTabStop = on;
            for (int sq = 0; sq < 64; sq++)
            {
                var cell = _cells[sq];
                if (cell == null) continue;
                cell.IsTabStop = on;
                cell.IsHitTestVisible = on;
            }
            // If input is disabled mid-promotion (game ended, or the user is reviewing a past
            // move), dismiss the picker so it can't commit a move for a position they've left.
            if (!on && PromotionOverlay != null)
            {
                PromotionOverlay.Visibility = Visibility.Collapsed;
                _pendingPromotion = new ChessMove(-1, -1);
            }
        }

        /// <summary>Re-colour the squares when the user changes the board theme.</summary>
        void ReTheme()
        {
            if (_cells[0] == null) return;
            for (int sq = 0; sq < 64; sq++)
            {
                int file = ChessMove.FileOf(sq), rank = ChessMove.RankOf(sq);
                bool isLight = (file + rank) % 2 == 1;
                var color = isLight ? BoardTheme.Light : BoardTheme.Dark;
                if (_cells[sq].Background is SolidColorBrush b) b.Color = color;
                else _cells[sq].Background = new SolidColorBrush(color);
            }
            Render();
        }

        // Ensure the selected SVG piece set is downloaded/cached, then redraw with it.
        async Task EnsurePieceSetAsync()
        {
            string set = BoardTheme.PieceSet;
            if (set == PieceSets.Native || PieceSets.IsReady(set)) return;
            if (await PieceSets.EnsureAsync(set)) Render();
        }

        void Render()
        {
            if (_cells[0] == null) return;

            int checkSquare = -1;
            if (_position.IsInCheck(_position.WhiteToMove))
                checkSquare = _position.FindKing(_position.WhiteToMove);

            var whiteFill = WhiteFill;
            var whiteOutline = WhiteOutline;
            var blackFill = BlackFill;
            var blackOutline = BlackOutline;

            for (int sq = 0; sq < 64; sq++)
            {
                // Position the square in the grid per orientation.
                SquareToVisual(sq, out int row, out int col);
                Grid.SetRow(_cells[sq], row);
                Grid.SetColumn(_cells[sq], col);

                char piece = _position.PieceAt(sq);
                bool empty = piece == '.';
                if (empty)
                {
                    _pieceHost[sq].Visibility = Visibility.Collapsed;
                    _pieceImg[sq].Visibility = Visibility.Collapsed;
                }
                else
                {
                    var img = SvgFor(piece);   // null → use the Unicode glyph
                    if (img != null)
                    {
                        if (!ReferenceEquals(_pieceImg[sq].Source, img)) _pieceImg[sq].Source = img;   // avoid needless reload
                        _pieceImg[sq].Visibility = Visibility.Visible;
                        _pieceHost[sq].Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        string g = GlyphFor(piece);
                        bool white = char.IsUpper(piece);
                        _pieceFill[sq].Text = g;
                        _pieceOutline[sq].Text = g;
                        _pieceFill[sq].Foreground = new SolidColorBrush(white ? whiteFill : blackFill);
                        _pieceOutline[sq].Foreground = new SolidColorBrush(white ? whiteOutline : blackOutline);
                        // Nudge the outline so it reads as a thin border.
                        _pieceOutline[sq].RenderTransform = new TranslateTransform { X = 0, Y = 2 };
                        _pieceHost[sq].Visibility = Visibility.Visible;
                        _pieceImg[sq].Visibility = Visibility.Collapsed;
                    }
                }

                // Highlights
                bool isLastMove = LastMove.HasValue && (LastMove.Value.From == sq || LastMove.Value.To == sq);
                bool isSelected = sq == _selected;
                bool isCheck = sq == checkSquare;

                if (isCheck)
                {
                    _highlight[sq].Background = new SolidColorBrush(Color.FromArgb(0x99, 0xD6, 0x3B, 0x2F));
                    _highlight[sq].Visibility = Visibility.Visible;
                }
                else if (isSelected)
                {
                    _highlight[sq].Background = new SolidColorBrush(Color.FromArgb(0xAA, 0x8F, 0xCB, 0x3F));
                    _highlight[sq].Visibility = Visibility.Visible;
                }
                else if (isLastMove)
                {
                    _highlight[sq].Background = new SolidColorBrush(Color.FromArgb(0x88, 0xF2, 0xD2, 0x6B));
                    _highlight[sq].Visibility = Visibility.Visible;
                }
                else _highlight[sq].Visibility = Visibility.Collapsed;

                _legalDot[sq].Visibility = Visibility.Collapsed;
                _captureRing[sq].Visibility = Visibility.Collapsed;
            }

            // Legal-target markers for the selected piece.
            foreach (var m in _legalFromSelected)
            {
                if (_position.PieceAt(m.To) != '.') _captureRing[m.To].Visibility = Visibility.Visible;
                else _legalDot[m.To].Visibility = Visibility.Visible;
            }

            TryAnimateLastMove();
            MaybePlayMoveSound();

            // Snapshot the board so the next render can classify the move that produced it.
            if (_prevSquares == null) _prevSquares = new char[64];
            for (int i = 0; i < 64; i++) _prevSquares[i] = _position.PieceAt(i);
            _prevLastMove = LastMove;
        }

        // --- Move animation ---------------------------------------------------

        SvgImageSource SvgFor(char piece)
        {
            var src = PieceSets.SourceFor(_pieceSetOverride ?? BoardTheme.PieceSet, piece);
            if (src == null) return null;
            if (!_svgCache.TryGetValue(src.AbsoluteUri, out var img))
            {
                // Pin a fixed square raster so a set's declared size (merida's "50mm", california's
                // non-square viewBox, alpha's 2048…) can't make pieces render at inconsistent sizes —
                // every piece normalizes to the same box and the viewBox maps into it.
                img = new SvgImageSource(src) { RasterizePixelWidth = 256, RasterizePixelHeight = 256 };
                _svgCache[src.AbsoluteUri] = img;
            }
            return img;
        }

        // Slide the moved piece from its old square into place — but only for a plain forward
        // move (the piece left From and now sits on To). Jumps, takebacks and the initial setup
        // render instantly, which keeps navigation crisp and avoids nonsensical slides.
        void TryAnimateLastMove()
        {
            CancelAnim();
            if (!BoardTheme.Animations || !LastMove.HasValue || _prevSquares == null) return;
            if (_prevLastMove.HasValue && LastMove.Value.Equals(_prevLastMove.Value)) return;  // same move (e.g. re-theme)
            if (BoardGrid.ActualWidth <= 0 || BoardGrid.ActualHeight <= 0) return;

            int from = LastMove.Value.From, to = LastMove.Value.To;
            if (from < 0 || from > 63 || to < 0 || to > 63 || from == to) return;
            // Forward move: From was occupied before, From is empty now, To is occupied now.
            if (_prevSquares[from] == '.' || _position.PieceAt(from) != '.' || _position.PieceAt(to) == '.') return;

            // A real single move changes at most 4 squares (castling). A larger diff means the board
            // jumped to an unrelated position — a TV game switch or a replay scrub — which must not
            // animate as one gliding piece.
            int changed = 0;
            for (int i = 0; i < 64; i++) if (_prevSquares[i] != _position.PieceAt(i)) changed++;
            if (changed > 4) return;

            AnimateMove(from, to, _position.PieceAt(to));
        }

        void AnimateMove(int from, int to, char piece)
        {
            double cw = BoardGrid.ActualWidth / 8.0, ch = BoardGrid.ActualHeight / 8.0;
            if (cw <= 0 || ch <= 0) return;

            SquareToVisual(from, out int fr, out int fc);
            SquareToVisual(to, out int tr, out int tc);

            var fly = BuildFlyingPiece(piece, cw, ch);
            Canvas.SetLeft(fly, tc * cw);
            Canvas.SetTop(fly, tr * ch);
            var tt = new TranslateTransform { X = (fc - tc) * cw, Y = (fr - tr) * ch };
            fly.RenderTransform = tt;
            AnimLayer.Children.Add(fly);

            // Hide the real piece sitting on the destination until the slide lands.
            _pieceImg[to].Opacity = 0;
            _pieceHost[to].Opacity = 0;
            _animSquare = to;
            _animFly = fly;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = new Duration(TimeSpan.FromMilliseconds(BoardTheme.AnimationDurationMs));
            var ax = new DoubleAnimation { To = 0, Duration = dur, EasingFunction = ease };
            Storyboard.SetTarget(ax, tt);
            Storyboard.SetTargetProperty(ax, "X");
            var ay = new DoubleAnimation { To = 0, Duration = dur, EasingFunction = ease };
            Storyboard.SetTarget(ay, tt);
            Storyboard.SetTargetProperty(ay, "Y");

            var sb = new Storyboard();
            sb.Children.Add(ax);
            sb.Children.Add(ay);
            sb.Completed += (s, e) => CancelAnim();
            _animStoryboard = sb;
            sb.Begin();
        }

        // Stop any in-flight slide, drop the flying piece and restore the real one.
        void CancelAnim()
        {
            if (_animStoryboard != null) { try { _animStoryboard.Stop(); } catch { } _animStoryboard = null; }
            if (_animFly != null) { AnimLayer.Children.Remove(_animFly); _animFly = null; }
            if (_animSquare >= 0)
            {
                _pieceImg[_animSquare].Opacity = 1;
                _pieceHost[_animSquare].Opacity = 1;
                _animSquare = -1;
            }
        }

        // -------------------------------------------------------------- arrows

        /// <summary>Show a best-move arrow between two squares (e.g. the engine's top move).</summary>
        public void SetBestArrow(int from, int to)
        {
            _arrowFrom = from; _arrowTo = to;
            DrawArrows();
        }

        public void ClearArrows()
        {
            _arrowFrom = _arrowTo = -1;
            ArrowLayer?.Children.Clear();
        }

        // The board root is a fixed 720x720 scaled by the Viewbox, so arrow geometry uses fixed
        // coordinates (704 inner / 8 = 88 per cell) — no dependence on ActualWidth / layout timing.
        void DrawArrows()
        {
            if (ArrowLayer == null) return;
            ArrowLayer.Children.Clear();
            if (_arrowFrom < 0 || _arrowFrom > 63 || _arrowTo < 0 || _arrowTo > 63 || _arrowFrom == _arrowTo) return;

            const double cell = 88.0;
            SquareToVisual(_arrowFrom, out int fr, out int fc);
            SquareToVisual(_arrowTo, out int tr, out int tc);
            double x1 = fc * cell + cell / 2, y1 = fr * cell + cell / 2;
            double x2 = tc * cell + cell / 2, y2 = tr * cell + cell / 2;

            double dx = x2 - x1, dy = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            double ux = dx / len, uy = dy / len;
            double px = -uy, py = ux;   // unit perpendicular

            const double head = 34, halfW = 19, shaft = 15;
            double sx = x1 + ux * 22, sy = y1 + uy * 22;   // start just out of the from-square
            double bx = x2 - ux * head, by = y2 - uy * head;   // shaft stops at the arrowhead base

            var brush = new SolidColorBrush(Color.FromArgb(0xCC, 0x8F, 0xCB, 0x3F));   // semi-transparent accent green

            ArrowLayer.Children.Add(new Line
            {
                X1 = sx, Y1 = sy, X2 = bx, Y2 = by,
                Stroke = brush, StrokeThickness = shaft,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });

            var headPoly = new Polygon { Fill = brush };
            headPoly.Points.Add(new Point(x2, y2));                            // tip
            headPoly.Points.Add(new Point(bx + px * halfW, by + py * halfW));   // back corner
            headPoly.Points.Add(new Point(bx - px * halfW, by - py * halfW));   // back corner
            ArrowLayer.Children.Add(headPoly);
        }

        // A piece visual sized to one cell, mirroring how the board draws the piece (SVG set or glyph).
        FrameworkElement BuildFlyingPiece(char piece, double cw, double ch)
        {
            var container = new Grid { Width = cw, Height = ch };
            var svg = SvgFor(piece);
            if (svg != null)
            {
                container.Children.Add(new Image { Source = svg, Stretch = Stretch.Uniform, Margin = new Thickness(6) });
            }
            else
            {
                string g = GlyphFor(piece);
                bool white = char.IsUpper(piece);
                var outline = MakeGlyph();
                outline.Text = g;
                outline.Foreground = new SolidColorBrush(white ? WhiteOutline : BlackOutline);
                outline.RenderTransform = new TranslateTransform { X = 0, Y = 2 };
                var fill = MakeGlyph();
                fill.Text = g;
                fill.Foreground = new SolidColorBrush(white ? WhiteFill : BlackFill);
                var pg = new Grid();
                pg.Children.Add(outline);
                pg.Children.Add(fill);
                container.Children.Add(new Viewbox { Child = pg, Stretch = Stretch.Uniform, Margin = new Thickness(8) });
            }
            return container;
        }

        void MaybePlayMoveSound()
        {
            int pieceCount = 0;
            for (int i = 0; i < 64; i++) if (_position.Squares[i] != '.') pieceCount++;

            bool moveChanged = LastMove.HasValue &&
                (!_lastSounded.HasValue || !LastMove.Value.Equals(_lastSounded.Value));

            // Only a live, interactive board (the Play board) makes move sounds — never the
            // home preview, ongoing-game thumbnails, the settings preview or watch boards,
            // which also set LastMove and would otherwise ding on load.
            if ((Interactive || SoundsEnabled) && _soundReady && moveChanged)
            {
                bool capture = _lastPieceCount >= 0 && pieceCount < _lastPieceCount;
                // Like lichess's standard theme: a checking move just plays the move/capture
                // sound (no separate check "ding" — that read as a checkmate/notify sound).
                if (capture) SoundService.Capture();
                else SoundService.Move();
            }

            _lastSounded = LastMove;
            _lastPieceCount = pieceCount;
            _soundReady = true;
        }

        #endregion

        #region input

        bool IsHumanTurn => Interactive && _position.WhiteToMove == PlayerIsWhite;

        // Clicking a square (gamepad A, Enter, or tap) — pick up a piece, then drop it.
        void Cell_Click(object sender, RoutedEventArgs e)
        {
            if (!IsHumanTurn) return;
            int sq = (int)((Button)sender).Tag;

            char piece = _position.PieceAt(sq);
            bool ownPiece = piece != '.' && (char.IsUpper(piece) == PlayerIsWhite);

            if (_selected < 0)
            {
                if (ownPiece) Select(sq);
                return;
            }

            // Trying to complete a move onto this square?
            ChessMove? chosen = null;
            bool needsPromotion = false;
            foreach (var m in _legalFromSelected)
            {
                if (m.To == sq)
                {
                    chosen = m;
                    if (m.Promotion != '\0') needsPromotion = true;
                    break;
                }
            }

            if (chosen.HasValue)
            {
                if (needsPromotion) ShowPromotionPicker(_selected, sq);
                else CommitMove(chosen.Value);
                return;
            }

            // Otherwise re-select (or deselect).
            if (ownPiece) Select(sq);
            else { ClearSelection(); Render(); }
        }

        /// <summary>
        /// Move gamepad focus onto the board. Focusing a real Button is reliable, but the
        /// board may not be laid out the instant it becomes visible, so retry across the
        /// next few layout passes until it sticks.
        /// </summary>
        public void FocusBoard()
        {
            if (!Interactive) return;
            DetachFocusRetry();          // never stack retry handlers across repeated calls
            if (TryFocusCell()) return;
            _focusRetryTries = 0;
            _focusRetryHandler = (s, e) =>
            {
                // Stop once it sticks, if interactivity was revoked, or after a generous budget
                // (a slow cold start can burn many layout passes before the board is ready).
                if (!Interactive || TryFocusCell() || ++_focusRetryTries > 90) DetachFocusRetry();
            };
            LayoutUpdated += _focusRetryHandler;
        }

        void DetachFocusRetry()
        {
            if (_focusRetryHandler != null) { LayoutUpdated -= _focusRetryHandler; _focusRetryHandler = null; }
        }

        bool TryFocusCell()
        {
            if (!Interactive || Visibility != Visibility.Visible || ActualWidth <= 0) return false;
            int sq = _selected >= 0 ? _selected : DefaultFocusSquare();
            var cell = _cells[sq] ?? _cells[DefaultFocusSquare()];
            return cell != null && cell.Focus(FocusState.Programmatic);
        }

        // A sensible square to land on: the side-to-move's king (always present), else a
        // central pawn square.
        int DefaultFocusSquare()
        {
            int k = _position.FindKing(PlayerIsWhite);
            if (k >= 0) return k;
            return PlayerIsWhite ? 12 : 52; // e2 / e7
        }

        void Select(int sq)
        {
            _selected = sq;
            _legalFromSelected.Clear();
            _legalFromSelected.AddRange(Permissive ? _position.PseudoLegalMovesFrom(sq) : _position.LegalMovesFrom(sq));
            Render();
        }

        void ClearSelection()
        {
            _selected = -1;
            _legalFromSelected.Clear();
        }

        /// <summary>Put down a picked-up piece (B button). True if there was one to put down.</summary>
        public bool CancelSelection()
        {
            if (_selected < 0) return false;
            ClearSelection();
            Render();
            return true;
        }

        // While a piece is selected, directional focus moves may not leave the board —
        // programmatic moves (game-over card, page changes) always pass through.
        void OnLosingFocus(UIElement sender, LosingFocusEventArgs e)
        {
            if (_selected < 0 || !Interactive) return;
            if (e.InputDevice != FocusInputDeviceKind.GameController && e.InputDevice != FocusInputDeviceKind.Keyboard) return;
            var d = e.NewFocusedElement as DependencyObject;
            while (d != null)
            {
                if (d == this) return;   // still on the board (cells, promotion picker)
                d = VisualTreeHelper.GetParent(d);
            }
            e.TryCancel();
        }

        void CommitMove(ChessMove move)
        {
            ClearSelection();
            // In standard chess, optimistically reflect the move for a snappy feel;
            // the game stream re-confirms the authoritative position. In variants our
            // engine can't fully judge legality, so we defer entirely to the server.
            if (!Permissive)
            {
                var next = _position.Apply(move);
                if (next != null) { _position = next; LastMove = move; }
            }
            Render();
            MoveRequested?.Invoke(this, move);
        }

        #endregion

        #region promotion

        void ShowPromotionPicker(int from, int to)
        {
            _pendingPromotion = new ChessMove(from, to);
            PromotionButtons.Children.Clear();
            bool white = PlayerIsWhite;
            foreach (char p in new[] { 'q', 'r', 'b', 'n' })
            {
                // Match the rest of the app: no system reveal-focus rectangle. Instead
                // emulate the shared green inset ring — a subtle dark border at rest that
                // switches to a crisp, thick green ring when the button gains gamepad focus.
                var restBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x42, 0x36));
                var focusBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x8F, 0xCB, 0x3F));
                var btn = new Button
                {
                    Width = 110, Height = 130,
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x25, 0x1D)),
                    BorderBrush = restBorder,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    UseSystemFocusVisuals = false,
                    Content = new TextBlock
                    {
                        Text = Glyph(p),
                        FontFamily = new FontFamily("Segoe UI Symbol"),
                        FontSize = 72,
                        Foreground = new SolidColorBrush(white ? Colors.White : Color.FromArgb(0xFF, 0xE8, 0xE4, 0xDC)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                btn.GotFocus += (s, e) =>
                {
                    btn.BorderBrush = focusBorder;
                    btn.BorderThickness = new Thickness(4);
                };
                btn.LostFocus += (s, e) =>
                {
                    btn.BorderBrush = restBorder;
                    btn.BorderThickness = new Thickness(2);
                };
                char promo = p;
                btn.Click += (s, e) =>
                {
                    PromotionOverlay.Visibility = Visibility.Collapsed;
                    CommitMove(new ChessMove(_pendingPromotion.From, _pendingPromotion.To, promo));
                    FocusBoard();
                };
                PromotionButtons.Children.Add(btn);
            }
            PromotionOverlay.Visibility = Visibility.Visible;
            if (PromotionButtons.Children.Count > 0)
                ((Button)PromotionButtons.Children[0]).Focus(FocusState.Programmatic);
        }

        #endregion
    }
}
