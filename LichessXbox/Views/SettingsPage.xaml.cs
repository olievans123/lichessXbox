using LichessXbox.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LichessXbox.Views
{
    public sealed partial class SettingsPage : Page
    {
        bool _loaded;

        public SettingsPage()
        {
            this.InitializeComponent();
            ThemeGrid.ItemsSource = BoardTheme.Presets;
            ThemeGrid.SelectedItem = BoardTheme.Presets.Find(p => p.Name == BoardTheme.CurrentName);
            OutlineToggle.IsOn = BoardTheme.OutlinePieces;
            SoundToggle.IsOn = BoardTheme.UiSounds;
            MoveSoundToggle.IsOn = BoardTheme.MoveSounds;
            PieceGrid.ItemsSource = PieceSets.All;
            PieceGrid.SelectedItem = BoardTheme.PieceSet;   // fires SelectionChanged → seeds the preview
            _loaded = true;
            ThemeGrid.FocusOnLoad();
        }

        void Theme_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BoardTheme.Preset p) BoardTheme.Apply(p.Name);
        }

        // Live-preview the highlighted piece set on the side board without applying it.
        // On Xbox the highlight follows gamepad focus, so this updates as the user browses.
        void PieceSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(PieceGrid.SelectedItem is string set)) return;
            PreviewBoard.PieceSetOverride = set;   // "Native" → Unicode glyphs; else the SVG set
            PreviewLabel.Text = set == PieceSets.Native ? "Native — Unicode glyphs" : set;
            if (set != PieceSets.Native) _ = PieceSets.EnsureAsync(set);   // fetch SVGs so the preview fills in
        }

        async void PieceSet_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is string set)) return;
            // Re-clicking the current set is normally a no-op — unless it hasn't finished
            // downloading (e.g. the default failed offline), in which case let it retry.
            if (set == BoardTheme.PieceSet && PieceSets.IsReady(set)) return;
            if (set == PieceSets.Native)
            {
                BoardTheme.SetPieceSet(set);
                PieceStatus.Visibility = Visibility.Collapsed;
                return;
            }
            PieceStatus.Text = "Downloading the " + set + " set…";
            PieceStatus.Visibility = Visibility.Visible;
            bool ok = await PieceSets.EnsureAsync(set);
            if (ok)
            {
                BoardTheme.SetPieceSet(set);           // fires Changed → every board redraws with the SVG set
                PieceStatus.Visibility = Visibility.Collapsed;
            }
            else
            {
                PieceStatus.Text = "Couldn't download the " + set + " set — check your connection and try again.";
                PieceGrid.SelectedItem = BoardTheme.PieceSet;   // revert the selection marker
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
