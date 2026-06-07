using System;
using System.Collections.Generic;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace LichessXbox.Views
{
    public sealed partial class GamesPage : Page
    {
        public GamesPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            if (!AppState.Current.IsSignedIn)
            {
                SignInPrompt.Visibility = Visibility.Visible;
                GamesContent.Visibility = Visibility.Collapsed;
                GoSignInButton.Focus(FocusState.Programmatic);
                return;
            }
            SignInPrompt.Visibility = Visibility.Collapsed;
            GamesContent.Visibility = Visibility.Visible;
            await ReloadAsync();
        }

        async void Retry_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

        async System.Threading.Tasks.Task ReloadAsync()
        {
            var account = await AppState.Current.EnsureAccountAsync();
            if (account == null) return;

            Busy.Visibility = Visibility.Visible;
            Busy.IsActive = true;
            try
            {
                var games = await AppState.Current.Api.GetUserGamesAsync(account.Username, 25);
                GamesList.ItemsSource = games;
                bool any = games != null && games.Count > 0;
                NoGames.Text = "No games yet.";
                NoGames.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                RetryGamesButton.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                if (any) GamesList.Focus(FocusState.Programmatic);
                else RetryGamesButton.Focus(FocusState.Programmatic);
            }
            catch
            {
                NoGames.Text = "Couldn't load your games.";
                NoGames.Visibility = Visibility.Visible;
                RetryGamesButton.Visibility = Visibility.Visible;
                RetryGamesButton.Focus(FocusState.Programmatic);
            }
            finally { Busy.IsActive = false; Busy.Visibility = Visibility.Collapsed; }
        }

        void GoSignIn_Click(object sender, RoutedEventArgs e)
        {
            ((Window.Current.Content as Frame)?.Content as MainPage)?.NavigateTo("profile");
        }

        void Game_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is GameSummary g)) return;
            var shell = (Window.Current.Content as Frame)?.Content as MainPage;
            shell?.OpenAnalysis((g.InitialFen ?? "startpos") + "|" + (g.Moves ?? "") + "|" + (g.WhiteName ?? "White") + "|" + (g.BlackName ?? "Black"));
        }
    }
}
