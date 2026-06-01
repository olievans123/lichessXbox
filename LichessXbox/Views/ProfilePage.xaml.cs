using System;
using System.Collections.Generic;
using LichessXbox.Helpers;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class ProfilePage : Page
    {
        public ProfilePage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();

        async System.Threading.Tasks.Task RefreshAsync()
        {
            if (!AppState.Current.IsSignedIn)
            {
                SignedInPanel.Visibility = Visibility.Collapsed;
                SignedOutCard.Visibility = Visibility.Visible;
                SignInButton.FocusOnLoad();
                return;
            }

            var account = await AppState.Current.EnsureAccountAsync();
            if (account == null)
            {
                // Token invalid / expired — fall back to signed-out.
                await AppState.Current.SignOutAsync();
                SignedInPanel.Visibility = Visibility.Collapsed;
                SignedOutCard.Visibility = Visibility.Visible;
                return;
            }

            ShowAccount(account);
        }

        void ShowAccount(LichessAccount a)
        {
            SignedOutCard.Visibility = Visibility.Collapsed;
            SignedInPanel.Visibility = Visibility.Visible;

            UsernameText.Text = a.DisplayName;
            AvatarInitial.Text = string.IsNullOrEmpty(a.Username) ? "?" : a.Username.Substring(0, 1).ToUpperInvariant();
            SubtitleText.Text = a.Patron ? "Lichess Patron ♥" : "Lichess member";

            var ratings = new List<RatingTile>();
            if (a.BulletRating.HasValue) ratings.Add(new RatingTile("Bullet", a.BulletRating.Value));
            if (a.BlitzRating.HasValue) ratings.Add(new RatingTile("Blitz", a.BlitzRating.Value));
            if (a.RapidRating.HasValue) ratings.Add(new RatingTile("Rapid", a.RapidRating.Value));
            if (a.ClassicalRating.HasValue) ratings.Add(new RatingTile("Classical", a.ClassicalRating.Value));
            RatingsGrid.ItemsSource = ratings;
        }

        // Personal-token sign-in: no WebView/broker (both fail on Xbox). The user pastes a
        // token created on lichess.org; we verify it by fetching the account.
        async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;
            string token = TokenBox.Text?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                ShowAuthError("Enter a token first.");
                return;
            }

            Busy.IsActive = true;
            SignInButton.IsEnabled = false;
            try
            {
                bool ok = await AppState.Current.SignInWithTokenAsync(token);
                if (ok) await RefreshAsync();
                else ShowAuthError("That token didn't work. Make sure you copied it fully and it has the board:play scope.");
            }
            catch (Exception ex)
            {
                ShowAuthError("Something went wrong: " + ex.Message);
            }
            finally
            {
                Busy.IsActive = false;
                SignInButton.IsEnabled = true;
            }
        }

        void ShowAuthError(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await AppState.Current.SignOutAsync();
            await RefreshAsync();
        }
    }
}
