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
            SoundToggle.IsOn = ElementSoundPlayer.State == ElementSoundPlayerState.On;
            MoveSoundToggle.IsOn = BoardTheme.MoveSounds;
            PieceGrid.ItemsSource = PieceSets.All;
            PieceGrid.SelectedItem = BoardTheme.PieceSet;
            _loaded = true;
            ThemeGrid.FocusOnLoad();
        }

        void Theme_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BoardTheme.Preset p) BoardTheme.Apply(p.Name);
        }

        async void PieceSet_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is string set) || set == BoardTheme.PieceSet) return;
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
