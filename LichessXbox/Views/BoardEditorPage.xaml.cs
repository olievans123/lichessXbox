using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Helpers;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace LichessXbox.Views
{
    /// <summary>Free board editor — place pieces, get a FEN, send it to analysis.
    /// Renders the same piece set chosen in Settings (SVG when active, glyphs otherwise).</summary>
    public sealed partial class BoardEditorPage : Page
    {
        readonly char[] _squares = new char[64];
        readonly Button[] _cells = new Button[64];
        readonly TextBlock[] _glyph = new TextBlock[64];
        readonly Image[] _img = new Image[64];
        readonly List<(char Piece, TextBlock Glyph, Image Img)> _palette = new List<(char, TextBlock, Image)>();
        readonly Dictionary<string, SvgImageSource> _svgCache = new Dictionary<string, SvgImageSource>();
        char _selected = 'P';
        bool _whiteToMove = true;

        public BoardEditorPage()
        {
            this.InitializeComponent();
            BuildGrid();
            BuildPalette();
            SetStartingPosition();

            // Keep the editor's pieces in sync with the Settings piece set (and theme).
            Action reRender = () => RenderAll();
            BoardTheme.Changed += reRender;
            this.Unloaded += (s, e) => BoardTheme.Changed -= reRender;

            this.Loaded += async (s, e) =>
            {
                UpdateFen();
                (WhitePalette.Children.Count > 0 ? WhitePalette.Children[0] as Button : null)?.Focus(FocusState.Programmatic);
                await EnsurePieceSetAsync();
            };
        }

        async Task EnsurePieceSetAsync()
        {
            string set = BoardTheme.PieceSet;
            if (set == PieceSets.Native || PieceSets.IsReady(set)) return;
            if (await PieceSets.EnsureAsync(set)) RenderAll();
        }

        void BuildGrid()
        {
            for (int i = 0; i < 8; i++)
            {
                EditorGrid.RowDefinitions.Add(new RowDefinition());
                EditorGrid.ColumnDefinitions.Add(new ColumnDefinition());
            }
            for (int row = 0; row < 8; row++)
                for (int col = 0; col < 8; col++)
                {
                    int rank = 7 - row, file = col;
                    int sq = rank * 8 + file;
                    bool isLight = (file + rank) % 2 == 1;
                    var glyph = new TextBlock
                    {
                        FontFamily = new FontFamily("Segoe UI Symbol"),
                        FontSize = 52,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                    var content = new Grid();
                    content.Children.Add(glyph);
                    content.Children.Add(img);
                    var btn = new Button
                    {
                        Background = new SolidColorBrush(isLight ? BoardTheme.Light : BoardTheme.Dark),
                        Style = (Style)Application.Current.Resources["BoardCellButton"],
                        Content = content,
                        Tag = sq,
                    };
                    btn.Click += Cell_Click;
                    Grid.SetRow(btn, row);
                    Grid.SetColumn(btn, col);
                    EditorGrid.Children.Add(btn);
                    _cells[sq] = btn;
                    _glyph[sq] = glyph;
                    _img[sq] = img;
                }
        }

        // Palette colours: black pieces sit on a light tile (like a board square) so they read
        // as pieces, not greyed-out; the SELECTED piece gets a solid green FILL (distinct from
        // the green focus RING, so "armed" vs "cursor" never look the same).
        static readonly Color PaletteDark = Color.FromArgb(0xFF, 0x2A, 0x25, 0x1D);
        static readonly Color PaletteLightTile = Color.FromArgb(0xFF, 0xD8, 0xD2, 0xC4);
        static readonly Color SelectedFill = Color.FromArgb(0xFF, 0x6F, 0xA6, 0x30);
        static readonly Color GreenEdge = Color.FromArgb(0xFF, 0x8F, 0xCB, 0x3F);
        static readonly Color HairlineEdge = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        void BuildPalette()
        {
            foreach (char c in "KQRBNP") WhitePalette.Children.Add(MakePaletteButton(c));
            foreach (char c in "kqrbnp") BlackPalette.Children.Add(MakePaletteButton(c));
            ToolPalette.Children.Add(MakePaletteButton('.')); // eraser
            ToolPalette.Children.Add(new TextBlock
            {
                Text = "Eraser",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xA8, 0xA2, 0x96)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            });
        }

        // A black piece sits on a light tile; white pieces / the eraser on the dark surface.
        static Color IdleBg(char piece) =>
            piece != '.' && !char.IsUpper(piece) ? PaletteLightTile : PaletteDark;

        Button MakePaletteButton(char piece)
        {
            Color glyphColor = piece == '.' ? Color.FromArgb(0xFF, 0x9A, 0x94, 0x88)
                             : char.IsUpper(piece) ? Colors.White : Color.FromArgb(0xFF, 0x26, 0x22, 0x1C);
            var glyph = new TextBlock
            {
                Text = piece == '.' ? "⌫" : PieceGlyph(piece),
                FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = piece == '.' ? 24 : 26,
                Foreground = new SolidColorBrush(glyphColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var img = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(4), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var content = new Grid();
            content.Children.Add(glyph);
            content.Children.Add(img);
            var btn = new Button
            {
                Width = 36, Height = 36,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0),
                Content = content,
                Tag = piece,
            };
            btn.UseSystemFocusVisuals = true;
            btn.FocusVisualPrimaryBrush = new SolidColorBrush(GreenEdge);
            btn.Click += Palette_Click;
            StylePaletteButton(btn, piece);
            if (piece != '.') _palette.Add((piece, glyph, img));
            return btn;
        }

        void Palette_Click(object sender, RoutedEventArgs e)
        {
            _selected = (char)((Button)sender).Tag;
            RefreshPaletteBorders(WhitePalette);
            RefreshPaletteBorders(BlackPalette);
            RefreshPaletteBorders(ToolPalette);
        }

        // Selected = solid green fill; otherwise the piece's idle tile. (Focus is the green
        // ring drawn separately, so the two states never look identical.)
        void StylePaletteButton(Button b, char piece)
        {
            bool sel = piece == _selected;
            b.Background = new SolidColorBrush(sel ? SelectedFill : IdleBg(piece));
            b.BorderBrush = new SolidColorBrush(sel ? GreenEdge : HairlineEdge);
        }

        void RefreshPaletteBorders(Panel panel)
        {
            foreach (var child in panel.Children)
                if (child is Button b && b.Tag is char c) StylePaletteButton(b, c);
        }

        void Cell_Click(object sender, RoutedEventArgs e)
        {
            int sq = (int)((Button)sender).Tag;
            _squares[sq] = _selected;
            RenderCell(sq);
            UpdateFen();
        }

        void SetStartingPosition()
        {
            var start = ChessPosition.Starting();
            Array.Copy(start.Squares, _squares, 64);
            RenderAll();
        }

        // Render a board square as the selected SVG piece, or the Unicode glyph as a fallback.
        void RenderCell(int sq)
        {
            char p = _squares[sq];
            var glyph = _glyph[sq];
            var img = _img[sq];
            if (p == '.')
            {
                glyph.Text = "";
                glyph.Visibility = Visibility.Collapsed;
                img.Visibility = Visibility.Collapsed;
                return;
            }
            var src = PieceSets.SourceFor(BoardTheme.PieceSet, p);
            if (src != null)
            {
                var svg = CachedSvg(src);
                if (!ReferenceEquals(img.Source, svg)) img.Source = svg;
                img.Visibility = Visibility.Visible;
                glyph.Visibility = Visibility.Collapsed;
            }
            else
            {
                glyph.Text = PieceGlyph(p);
                glyph.Foreground = new SolidColorBrush(char.IsUpper(p) ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x16, 0x12));
                glyph.Visibility = Visibility.Visible;
                img.Visibility = Visibility.Collapsed;
            }
        }

        // Decode each SVG once and reuse it, so re-themes don't re-decode and flicker the pieces.
        SvgImageSource CachedSvg(Uri src)
        {
            if (!_svgCache.TryGetValue(src.AbsoluteUri, out var svg))
            {
                svg = new SvgImageSource(src);
                _svgCache[src.AbsoluteUri] = svg;
            }
            return svg;
        }

        void RenderPalette()
        {
            foreach (var (piece, glyph, img) in _palette)
            {
                var src = PieceSets.SourceFor(BoardTheme.PieceSet, piece);
                if (src != null)
                {
                    var svg = CachedSvg(src);
                    if (!ReferenceEquals(img.Source, svg)) img.Source = svg;
                    img.Visibility = Visibility.Visible;
                    glyph.Visibility = Visibility.Collapsed;
                }
                else
                {
                    glyph.Visibility = Visibility.Visible;
                    img.Visibility = Visibility.Collapsed;
                }
            }
        }

        void RenderAll()
        {
            if (_cells[0] == null) return;
            for (int sq = 0; sq < 64; sq++) RenderCell(sq);
            RenderPalette();
            UpdateFen();
        }

        void UpdateFen()
        {
            var pos = new ChessPosition();
            Array.Copy(_squares, pos.Squares, 64);
            pos.WhiteToMove = _whiteToMove;
            // Infer castling rights from pieces on their home squares (a1=0 … h8=63) — always
            // stripping them made even the starting position lose castling once analyzed.
            pos.CastleWK = _squares[4] == 'K' && _squares[7] == 'R';
            pos.CastleWQ = _squares[4] == 'K' && _squares[0] == 'R';
            pos.CastleBK = _squares[60] == 'k' && _squares[63] == 'r';
            pos.CastleBQ = _squares[60] == 'k' && _squares[56] == 'r';
            pos.EnPassant = -1;
            if (FenBox != null) FenBox.Text = pos.ToFen();
        }

        void Side_Toggled(object sender, RoutedEventArgs e)
        {
            _whiteToMove = SideToggle.IsOn;
            UpdateFen();
        }

        void Start_Click(object sender, RoutedEventArgs e) => SetStartingPosition();

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < 64; i++) _squares[i] = '.';
            RenderAll();
        }

        void Analyze_Click(object sender, RoutedEventArgs e)
        {
            // Analysis needs a legal-ish position; the most common foot-gun is the wrong king count.
            int wk = 0, bk = 0;
            for (int i = 0; i < 64; i++) { if (_squares[i] == 'K') wk++; else if (_squares[i] == 'k') bk++; }
            if (wk != 1 || bk != 1)
            {
                EditorStatus.Text = "Add exactly one white king and one black king before analyzing.";
                EditorStatus.Visibility = Visibility.Visible;
                return;
            }
            EditorStatus.Visibility = Visibility.Collapsed;
            ((Window.Current.Content as Frame)?.Content as MainPage)?.OpenAnalysis(FenBox.Text + "|");
        }

        static string PieceGlyph(char piece)
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
    }
}
