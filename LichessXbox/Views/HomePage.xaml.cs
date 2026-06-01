using System;
using LichessXbox.Chess;
using LichessXbox.Helpers;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
            // A handsome opening position from the Italian Game for the preview.
            PreviewBoard.Position = ChessPosition.FromFen(
                "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3");
            PreviewBoard.LastMove = ChessMove.FromUci("f1c4");
            this.PlayButton.FocusOnLoad();
            this.Loaded += HomePage_Loaded;
        }

        async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            var account = await AppState.Current.EnsureAccountAsync();
            GreetingText.Text = account != null ? $"Welcome back, {account.Username}" : "Welcome";
        }

        MainPage Shell => (Window.Current.Content as Frame)?.Content as MainPage;

        void Play_Click(object sender, RoutedEventArgs e) => Shell?.NavigateTo("play");
        void Tv_Click(object sender, RoutedEventArgs e) => Shell?.NavigateTo("watch");
        void Puzzles_Click(object sender, RoutedEventArgs e) => Shell?.NavigateTo("puzzles");
    }
}
