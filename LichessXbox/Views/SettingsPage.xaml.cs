using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using LichessXbox.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace LichessXbox.Views
{
    /// <summary>A piece set in the picker. Holds the lazily-loaded thumbnail; the glyph
    /// shows until the thumbnail (or for "Native", always) is ready.</summary>
    public sealed class PieceSetItem : INotifyPropertyChanged
    {
        public string Name { get; }
        public PieceSetItem(string name) { Name = name; }

        ImageSource _preview;
        public ImageSource Preview
        {
            get => _preview;
            set { _preview = value; Raise(nameof(Preview)); Raise(nameof(GlyphVisibility)); }
        }

        public Visibility GlyphVisibility => _preview == null ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed partial class SettingsPage : Page
    {
        bool _loaded;
        const int CollapsedCount = 7;   // Native + 6 sets shown before "Show all"
        readonly ObservableCollection<PieceSetItem> _pieceItems = new ObservableCollection<PieceSetItem>();

        public SettingsPage()
        {
            this.InitializeComponent();
            ThemeGrid.ItemsSource = BoardTheme.Presets;
            ThemeGrid.SelectedItem = BoardTheme.Presets.Find(p => p.Name == BoardTheme.CurrentName);
            OutlineToggle.IsOn = BoardTheme.OutlinePieces;
            SoundToggle.IsOn = BoardTheme.UiSounds;
            MoveSoundToggle.IsOn = BoardTheme.MoveSounds;

            PieceGrid.ItemsSource = _pieceItems;
            PopulatePieces(false);
            SelectCurrentPiece();
            _loaded = true;
            ThemeGrid.FocusOnLoad();
        }

        // Fill the picker with either the first few sets (collapsed) or all of them.
        void PopulatePieces(bool all)
        {
            var names = all ? PieceSets.All.ToList() : PieceSets.All.Take(CollapsedCount).ToList();
            // Always include the applied set so its green selection is visible even when collapsed.
            if (!names.Contains(BoardTheme.PieceSet)) names.Insert(1, BoardTheme.PieceSet);

            _pieceItems.Clear();
            foreach (var n in names)
            {
                var item = new PieceSetItem(n);
                _pieceItems.Add(item);
                _ = LoadPreviewAsync(item);
            }
            ShowAllButton.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        }

        async Task LoadPreviewAsync(PieceSetItem item)
        {
            if (item.Name == PieceSets.Native) return;   // glyph only
            if (await PieceSets.EnsurePreviewAsync(item.Name))
            {
                var uri = PieceSets.PreviewUriFor(item.Name);
                if (uri != null) item.Preview = new SvgImageSource(uri);
            }
        }

        void SelectCurrentPiece()
        {
            var match = _pieceItems.FirstOrDefault(i => i.Name == BoardTheme.PieceSet);
            if (match != null) PieceGrid.SelectedItem = match;
        }

        void ShowAll_Click(object sender, RoutedEventArgs e)
        {
            // Keep whatever the user was previewing, and move focus into the grid since the
            // Show-all button is about to collapse (otherwise gamepad focus is stranded).
            string highlighted = (PieceGrid.SelectedItem as PieceSetItem)?.Name ?? BoardTheme.PieceSet;
            PopulatePieces(true);
            var keep = _pieceItems.FirstOrDefault(i => i.Name == highlighted)
                       ?? _pieceItems.FirstOrDefault(i => i.Name == BoardTheme.PieceSet);
            if (keep != null) PieceGrid.SelectedItem = keep;
            PieceGrid.Focus(FocusState.Programmatic);
        }

        void Theme_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BoardTheme.Preset p) BoardTheme.Apply(p.Name);
        }

        // Live-preview the highlighted piece set on the side board without applying it.
        // On Xbox the highlight follows gamepad focus, so this updates as the user browses.
        void PieceSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(PieceGrid.SelectedItem is PieceSetItem item)) return;
            string set = item.Name;
            PreviewBoard.PieceSetOverride = set;   // "Native" → Unicode glyphs; else the SVG set
            PreviewLabel.Text = set == PieceSets.Native ? "Native — Unicode glyphs" : set;

            if (set != PieceSets.Native && !PieceSets.IsReady(set))
                _ = PreviewDownloadAsync(set);   // show the veil while the full set downloads
            else
                ShowPreviewVeil(false);
        }

        async Task PreviewDownloadAsync(string set)
        {
            ShowPreviewVeil(true);
            await PieceSets.EnsureAsync(set);
            // Ready → BoardTheme.Changed → the preview re-renders with the SVGs. Only clear the
            // veil if this is still the set being previewed (the user may have moved on).
            if (PreviewBoard.PieceSetOverride == set) ShowPreviewVeil(false);
        }

        void ShowPreviewVeil(bool on)
        {
            PreviewVeil.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            PreviewBusy.IsActive = on;
        }

        async void PieceSet_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is PieceSetItem item)) return;
            string set = item.Name;
            // Re-clicking the current set is a no-op unless it hasn't finished downloading yet.
            if (set == BoardTheme.PieceSet && PieceSets.IsReady(set)) return;

            if (set == PieceSets.Native)
            {
                BoardTheme.SetPieceSet(set);
                PieceStatus.Visibility = Visibility.Collapsed;
                ShowPreviewVeil(false);
                return;
            }

            PieceStatus.Text = "Downloading the " + set + " set…";
            PieceStatus.Visibility = Visibility.Visible;
            ShowPreviewVeil(true);
            bool ok = await PieceSets.EnsureAsync(set);
            ShowPreviewVeil(false);
            if (ok)
            {
                BoardTheme.SetPieceSet(set);           // fires Changed → every board redraws with the SVG set
                PieceStatus.Visibility = Visibility.Collapsed;
            }
            else
            {
                PieceStatus.Text = "Couldn't download the " + set + " set — check your connection and try again.";
                SelectCurrentPiece();   // revert the selection marker
            }
        }

        void Outline_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loaded) BoardTheme.SetOutlinePieces(OutlineToggle.IsOn);
        }

        void Sound_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            BoardTheme.SetUiSounds(SoundToggle.IsOn);
            ElementSoundPlayer.State = SoundToggle.IsOn ? ElementSoundPlayerState.On : ElementSoundPlayerState.Off;
        }

        void MoveSound_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loaded) BoardTheme.SetMoveSounds(MoveSoundToggle.IsOn);
        }

        void Editor_Click(object sender, RoutedEventArgs e)
        {
            var shell = (Window.Current.Content as Frame)?.Content as MainPage;
            shell?.NavigateTo("editor");
        }

        void Coords_Click(object sender, RoutedEventArgs e)
        {
            var shell = (Window.Current.Content as Frame)?.Content as MainPage;
            shell?.NavigateTo("coords");
        }
    }
}
