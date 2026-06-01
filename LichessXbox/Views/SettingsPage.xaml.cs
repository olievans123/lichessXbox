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
            _loaded = true;
            OutlineToggle.FocusOnLoad();
        }

        void Theme_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BoardTheme.Preset p) BoardTheme.Apply(p.Name);
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
