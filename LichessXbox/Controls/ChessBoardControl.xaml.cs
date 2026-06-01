using System;
using System.Collections.Generic;
using LichessXbox.Chess;
using LichessXbox.Helpers;
using LichessXbox.Services;
using Windows.System;
using Windows.UI;
using Windows.Gaming.Input;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Shapes;

namespace LichessXbox.Controls
{
    /// <summary>
    /// A self-contained, gamepad-first chess board.
    ///
    /// • Renders any <see cref="ChessPosition"/> with last-move, check, selection and
    ///   legal-target highlights.
    /// • D-pad / left-stick / arrows move a focus cursor; A (or Enter) picks up and
    ///   drops a piece. B / Esc cancels a selection.
    /// • Raises <see cref="MoveRequested"/> when the human completes a legal move.
    ///
    /// Set <see cref="Interactive"/> = false for watch-only screens (TV / replays).
    /// </summary>
    public sealed partial class ChessBoardControl : UserControl
    {
        // Per-square visuals, indexed 0..63 (a1 = 0).
        readonly Border[] _squareBg = new Border[64];
        readonly Border[] _highlight = new Border[64];
        readonly Ellipse[] _legalDot = new Ellipse[64];
        readonly Border[] _captureRing = new Border[64];
        readonly Viewbox[] _pieceHost = new Viewbox[64];
        readonly TextBlock[] _pieceFill = new TextBlock[64];
        readonly TextBlock[] _pieceOutline = new TextBlock[64];
        readonly Border _cursor;

        ChessPosition _position = ChessPosition.Starting();
        readonly List<ChessMove> _legalFromSelected = new List<ChessMove>();
        int _selected = -1;
        int _cursorRow = 4, _cursorCol = 4;
        ChessMove _pendingPromotion;

        // Sound bookkeeping: play a sound whenever the highlighted last move changes.
        ChessMove? _lastSounded;
        int _lastPieceCount = -1;
        bool _soundReady;

        public event EventHandler<ChessMove> MoveRequested;

        public ChessBoardControl()
        {
            this.InitializeComponent();
            BuildBoard();

            _cursor = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8F, 0xCB, 0x3F)),
                BorderThickness = new Thickness(5),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            BoardGrid.Children.Add(_cursor);

            this.KeyDown += OnKeyDown;
            this.GotFocus += (s, e) => { if (Interactive) ShowCursor(); };
            this.LostFocus += (s, e) => _cursor.Visibility = Visibility.Collapsed;
            PrimeGamepadSubsystem();
            _padTimer.Tick += OnGamepadTick;
            this.Loaded += (s, e) => { Render(); _padTimer.Start(); };

            Action reTheme = () => ReTheme();
            BoardTheme.Changed += reTheme;
            this.Unloaded += (s, e) => { BoardTheme.Changed -= reTheme; _padTimer.Stop(); };
        }

        #region public properties

        public ChessPosition Position
        {
            get => _position;
            set { _position = value ?? ChessPosition.Starting(); ClearSelection(); Render(); }
        }

        public static readonly DependencyProperty WhiteAtBottomProperty =
            DependencyProperty.Register(nameof(WhiteAtBottom), typeof(bool), typeof(ChessBoardControl),
                new PropertyMetadata(true, (d, e) => ((ChessBoardControl)d).Render()));
        public bool WhiteAtBottom
        {
            get => (bool)GetValue(WhiteAtBottomProperty);
            set => SetValue(WhiteAtBottomProperty, value);
        }

        public static readonly DependencyProperty InteractiveProperty =
            DependencyProperty.Register(nameof(Interactive), typeof(bool), typeof(ChessBoardControl),
                new PropertyMetadata(false));
        public bool Interactive
        {
            get => (bool)GetValue(InteractiveProperty);
            set => SetValue(InteractiveProperty, value);
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

        #endregion

        #region geometry

        // Map a board square (0..63) to a visual (row, col) honouring orientation.
        void SquareToVisual(int sq, out int row, out int col)
        {
            int file = ChessMove.FileOf(sq), rank = ChessMove.RankOf(sq);
            if (WhiteAtBottom) { row = 7 - rank; col = file; }
            else { row = rank; col = 7 - file; }
        }

        int VisualToSquare(int row, int col)
        {
            int file, rank;
            if (WhiteAtBottom) { rank = 7 - row; file = col; }
            else { rank = row; file = 7 - col; }
            return rank * 8 + file;
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

                var cell = new Grid();

                var bg = new Border { Background = new SolidColorBrush(isLight ? light : dark) };
                cell.Children.Add(bg);
                _squareBg[sq] = bg;

                var hl = new Border { Background = new SolidColorBrush(Color.FromArgb(0x88, 0xF2, 0xD2, 0x6B)), Visibility = Visibility.Collapsed };
                cell.Children.Add(hl);
                _highlight[sq] = hl;

                var ring = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                    BorderThickness = new Thickness(6),
                    CornerRadius = new CornerRadius(90),
                    Margin = new Thickness(4),
                    Visibility = Visibility.Collapsed
                };
                cell.Children.Add(ring);
                _captureRing[sq] = ring;

                var dot = new Ellipse
                {
                    Width = 26, Height = 26,
                    Fill = new SolidColorBrush(Color.FromArgb(0x66, 0x14, 0x14, 0x14)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                cell.Children.Add(dot);
                _legalDot[sq] = dot;

                // Piece = outline glyph behind a fill glyph for crisp readability on any square.
                var outline = MakeGlyph(scale: 1.0);
                var fill = MakeGlyph(scale: 1.0);
                var pieceGrid = new Grid();
                pieceGrid.Children.Add(outline);
                pieceGrid.Children.Add(fill);
                var host = new Viewbox { Child = pieceGrid, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
                cell.Children.Add(host);
                _pieceHost[sq] = host;
                _pieceFill[sq] = fill;
                _pieceOutline[sq] = outline;

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
                    };
                    coord.Text = rank == 0 ? ((char)('a' + file)).ToString() : (rank + 1).ToString();
                    coord.HorizontalAlignment = rank == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    coord.VerticalAlignment = rank == 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    cell.Children.Add(coord);
                }

                Grid.SetRow(cell, 0); Grid.SetColumn(cell, 0); // positioned in Render()
                cell.Tag = sq;
                _cellHost[sq] = cell;
                BoardGrid.Children.Add(cell);
            }
        }

        readonly Grid[] _cellHost = new Grid[64];

        static TextBlock MakeGlyph(double scale)
        {
            return new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = 100,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
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

        /// <summary>Re-colour the squares when the user changes the board theme.</summary>
        void ReTheme()
        {
            if (_squareBg[0] == null) return;
            for (int sq = 0; sq < 64; sq++)
            {
                int file = ChessMove.FileOf(sq), rank = ChessMove.RankOf(sq);
                bool isLight = (file + rank) % 2 == 1;
                var color = isLight ? BoardTheme.Light : BoardTheme.Dark;
                if (_squareBg[sq].Background is SolidColorBrush b) b.Color = color;
                else _squareBg[sq].Background = new SolidColorBrush(color);
            }
            Render();
        }

        void Render()
        {
            if (_squareBg[0] == null) return;

            int checkSquare = -1;
            if (_position.IsInCheck(_position.WhiteToMove))
                checkSquare = _position.FindKing(_position.WhiteToMove);

            var whiteFill = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xF6);
            var whiteOutline = Color.FromArgb(0xFF, 0x33, 0x2E, 0x26);
            var blackFill = Color.FromArgb(0xFF, 0x26, 0x22, 0x1C);
            var blackOutline = Color.FromArgb(0xFF, 0xB8, 0xB2, 0xA6);

            for (int sq = 0; sq < 64; sq++)
            {
                // Position the cell in the grid per orientation.
                SquareToVisual(sq, out int row, out int col);
                Grid.SetRow(_cellHost[sq], row);
                Grid.SetColumn(_cellHost[sq], col);

                char piece = _position.PieceAt(sq);
                bool empty = piece == '.';
                _pieceHost[sq].Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                if (!empty)
                {
                    string g = GlyphFor(piece);
                    bool white = char.IsUpper(piece);
                    _pieceFill[sq].Text = g;
                    _pieceOutline[sq].Text = g;
                    _pieceFill[sq].Foreground = new SolidColorBrush(white ? whiteFill : blackFill);
                    _pieceOutline[sq].Foreground = new SolidColorBrush(white ? whiteOutline : blackOutline);
                    // Nudge the outline so it reads as a thin border.
                    _pieceOutline[sq].RenderTransform = new TranslateTransform { X = 0, Y = 2 };
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

            MaybePlayMoveSound();
            PositionCursor();
        }

        void MaybePlayMoveSound()
        {
            int pieceCount = 0;
            for (int i = 0; i < 64; i++) if (_position.Squares[i] != '.') pieceCount++;

            bool moveChanged = LastMove.HasValue &&
                (!_lastSounded.HasValue || !LastMove.Value.Equals(_lastSounded.Value));

            if (_soundReady && moveChanged)
            {
                bool capture = _lastPieceCount >= 0 && pieceCount < _lastPieceCount;
                if (_position.IsInCheck(_position.WhiteToMove)) SoundService.Check();
                else if (capture) SoundService.Capture();
                else SoundService.Move();
            }

            _lastSounded = LastMove;
            _lastPieceCount = pieceCount;
            _soundReady = true;
        }

        #endregion

        #region input

        bool IsHumanTurn => Interactive && _position.WhiteToMove == PlayerIsWhite;

        void ShowCursor()
        {
            if (PromotionOverlay.Visibility == Visibility.Visible) return;
            _cursor.Visibility = Visibility.Visible;
            PositionCursor();
        }

        void PositionCursor()
        {
            Grid.SetRow(_cursor, _cursorRow);
            Grid.SetColumn(_cursor, _cursorCol);
        }

        // Routed key path: fires when the board UserControl itself holds focus.
        void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (PromotionOverlay.Visibility == Visibility.Visible) return; // buttons handle it
            if (!Interactive) return;
            e.Handled = HandleKey(e.Key);
        }

        // ---- Hardware gamepad fallback --------------------------------------------
        // The routed KeyDown above only fires while the board UserControl holds XAML
        // focus, and on Xbox that focus can silently fail to land on first show — the
        // "doesn't focus / can't make any moves" symptom. So we ALSO read the physical
        // controller directly (Windows.Gaming.Input, focus-independent) on a timer and
        // drive the board when no other control owns input. NOTE: CoreWindow.KeyDown does
        // NOT reliably deliver gamepad buttons on Xbox, which is why earlier fallbacks via
        // key events did not help — reading the Gamepad directly does.
        readonly DispatcherTimer _padTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        GamepadButtons _prevButtons;
        double _prevLX, _prevLY;
        bool _padPrimed;

        /// <summary>Temporary: show a tiny input-state readout on interactive boards. Set false to hide.</summary>
        public static bool InputDebug = true;

        // Subscribing once ensures the gamepad subsystem starts tracking controllers,
        // so Gamepad.Gamepads is reliably populated when we poll it.
        static bool _gamepadPrimed;
        static void PrimeGamepadSubsystem()
        {
            if (_gamepadPrimed) return;
            _gamepadPrimed = true;
            Gamepad.GamepadAdded += (s, g) => { };
            Gamepad.GamepadRemoved += (s, g) => { };
        }

        void OnGamepadTick(object sender, object e)
        {
            if (InputDebug) UpdateDiag();

            if (!Interactive || ActualWidth <= 0 || ActualHeight <= 0 ||
                PromotionOverlay.Visibility == Visibility.Visible) { _padPrimed = false; return; }

            var pads = Gamepad.Gamepads;
            if (pads.Count == 0) { _padPrimed = false; return; }

            // If XAML focus is already on the board, the routed handler runs; if it's on
            // another real control (Resign, a list, nav), let that control own the input.
            var focused = FocusManager.GetFocusedElement();
            if (ReferenceEquals(focused, this) || focused is Control) { _padPrimed = false; return; }

            GamepadReading r;
            try { r = pads[0].GetCurrentReading(); } catch { return; }
            var b = r.Buttons;
            double lx = r.LeftThumbstickX, ly = r.LeftThumbstickY;

            // First active frame: record a baseline so a held button doesn't auto-fire.
            if (!_padPrimed) { _prevButtons = b; _prevLX = lx; _prevLY = ly; _padPrimed = true; ShowCursor(); return; }

            const double T = 0.5;
            GamepadButtons pressed = b & ~_prevButtons;   // rising edges only
            bool up    = (pressed & GamepadButtons.DPadUp) != 0    || (ly >  T && _prevLY <=  T);
            bool down  = (pressed & GamepadButtons.DPadDown) != 0  || (ly < -T && _prevLY >= -T);
            bool left  = (pressed & GamepadButtons.DPadLeft) != 0  || (lx < -T && _prevLX >= -T);
            bool right = (pressed & GamepadButtons.DPadRight) != 0 || (lx >  T && _prevLX <=  T);

            bool acted = false;
            ShowCursor();
            if (up)         { MoveCursor(-1, 0); acted = true; }
            else if (down)  { MoveCursor(1, 0);  acted = true; }
            else if (left)  { MoveCursor(0, -1); acted = true; }
            else if (right) { MoveCursor(0, 1);  acted = true; }

            if ((pressed & GamepadButtons.A) != 0)      { Activate(); acted = true; }
            else if ((pressed & GamepadButtons.B) != 0) { if (_selected >= 0) { ClearSelection(); Render(); } acted = true; }

            _prevButtons = b; _prevLX = lx; _prevLY = ly;

            // Once we've handled something, try to claim real focus so the normal routed
            // path (and edge-to-side-panel navigation) take over from here.
            if (acted) Focus(FocusState.Programmatic);
        }

        // Tiny on-board readout so an input failure can be diagnosed from a screenshot:
        // pad = controllers seen, int = Interactive, w = laid-out width, foc = focused
        // element type, sel = selected square. Hidden unless the board is interactive.
        void UpdateDiag()
        {
            if (DiagPanel == null) return;
            if (!Interactive) { DiagPanel.Visibility = Visibility.Collapsed; return; }
            object f = FocusManager.GetFocusedElement();
            string fn = f == null ? "null" : f.GetType().Name;
            int pads = Gamepad.Gamepads.Count;
            DiagText.Text = $"pad:{pads} int:1 w:{(int)ActualWidth} foc:{fn} sel:{_selected}";
            DiagPanel.Visibility = Visibility.Visible;
        }

        // Shared key logic. Returns true if the press was consumed; false lets the key
        // bubble so gamepad XY-focus can leave the board at its edges.
        bool HandleKey(VirtualKey key)
        {
            switch (key)
            {
                case VirtualKey.Up:
                case VirtualKey.GamepadDPadUp:
                case VirtualKey.GamepadLeftThumbstickUp:
                    return MoveCursor(-1, 0);
                case VirtualKey.Down:
                case VirtualKey.GamepadDPadDown:
                case VirtualKey.GamepadLeftThumbstickDown:
                    return MoveCursor(1, 0);
                case VirtualKey.Left:
                case VirtualKey.GamepadDPadLeft:
                case VirtualKey.GamepadLeftThumbstickLeft:
                    return MoveCursor(0, -1);
                case VirtualKey.Right:
                case VirtualKey.GamepadDPadRight:
                case VirtualKey.GamepadLeftThumbstickRight:
                    return MoveCursor(0, 1);
                case VirtualKey.Enter:
                case VirtualKey.Space:
                case VirtualKey.GamepadA:
                    Activate(); return true;
                case VirtualKey.Escape:
                case VirtualKey.GamepadB:
                    if (_selected >= 0) { ClearSelection(); Render(); return true; }
                    return false;
                default:
                    return false;
            }
        }

        // Returns true if the cursor actually moved. At a board edge it returns false and
        // leaves the key unhandled, so gamepad XY-focus can leave the board for side-panel
        // buttons (Resign, nav, etc.) instead of being trapped on the board.
        bool MoveCursor(int dRow, int dCol)
        {
            ShowCursor();
            int nr = Clamp(_cursorRow + dRow), nc = Clamp(_cursorCol + dCol);
            if (nr == _cursorRow && nc == _cursorCol) return false;
            _cursorRow = nr; _cursorCol = nc;
            PositionCursor();
            return true;
        }

        int _focusTries;

        /// <summary>
        /// Give the board gamepad focus. Focus() can silently fail if the board has just
        /// become visible and isn't laid out yet, so retry on the next layout passes until
        /// it sticks (the symptom otherwise: no move-cursor and you can't make moves).
        /// </summary>
        public void FocusBoard()
        {
            _focusTries = 0;
            if (TryFocus()) return;
            EventHandler<object> h = null;
            h = (s, e) =>
            {
                if (TryFocus() || ++_focusTries > 20) LayoutUpdated -= h;
            };
            LayoutUpdated += h;
        }

        bool TryFocus()
        {
            // Not displayed yet (collapsed parent / pre-layout) — wait for the next pass.
            if (Visibility != Visibility.Visible || ActualWidth <= 0) return false;
            bool ok = Focus(FocusState.Programmatic);
            // Show the move cursor the moment we're live, even if Focus() didn't take —
            // the window-level key fallback still drives moves in that case.
            if (Interactive) ShowCursor();
            return ok;
        }

        static int Clamp(int v) => v < 0 ? 0 : (v > 7 ? 7 : v);

        void Activate()
        {
            if (!IsHumanTurn) return;
            int sq = VisualToSquare(_cursorRow, _cursorCol);
            char piece = _position.PieceAt(sq);
            bool ownPiece = piece != '.' && (char.IsUpper(piece) == PlayerIsWhite);

            if (_selected < 0)
            {
                if (ownPiece) Select(sq);
                return;
            }

            // Trying to complete a move onto the cursor square?
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
                var btn = new Button
                {
                    Width = 110, Height = 130,
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x25, 0x1D)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8F, 0xCB, 0x3F)),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(12),
                    UseSystemFocusVisuals = false,
                    FocusVisualPrimaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x8F, 0xCB, 0x3F)),
                    FocusVisualSecondaryBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
                    Content = new TextBlock
                    {
                        Text = Glyph(p),
                        FontFamily = new FontFamily("Segoe UI Symbol"),
                        FontSize = 72,
                        Foreground = new SolidColorBrush(white ? Colors.White : Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                };
                char promo = p;
                btn.Click += (s, e) =>
                {
                    PromotionOverlay.Visibility = Visibility.Collapsed;
                    CommitMove(new ChessMove(_pendingPromotion.From, _pendingPromotion.To, promo));
                    this.Focus(FocusState.Programmatic);
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
