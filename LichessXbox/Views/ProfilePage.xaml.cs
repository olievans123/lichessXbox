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

        async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;
            Busy.IsActive = true;
            SignInButton.IsEnabled = false;
            try
            {
                bool ok = await AppState.Current.Auth.SignInAsync();
                if (ok) await RefreshAsync();
                else
                {
                    StatusText.Text = "Sign-in was cancelled or failed. Please try again.";
                    StatusText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Something went wrong: " + ex.Message;
                StatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                Busy.IsActive = false;
                SignInButton.IsEnabled = true;
            }
        }

        async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await AppState.Current.SignOutAsync();
            await RefreshAsync();
        }
    }
}
