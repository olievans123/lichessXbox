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

        string _authVerifier;
        string _authState;

        // Launch the Lichess login inside our own WebView (the system broker never shows
        // its UI on Xbox). We intercept the loopback redirect to capture the auth code.
        void SignIn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;
            var auth = AppState.Current.Auth.BuildAuthorization();
            _authVerifier = auth.verifier;
            _authState = auth.state;
            AuthWeb.NavigationStarting += AuthWeb_NavigationStarting;
            AuthOverlay.Visibility = Visibility.Visible;
            AuthWeb.Navigate(new Uri(auth.url));
        }

        async void AuthWeb_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs e)
        {
            string uri = e.Uri?.ToString() ?? "";
            if (!uri.StartsWith(LichessAuthService.RedirectUri, StringComparison.OrdinalIgnoreCase))
                return; // still navigating the login/consent pages

            // Our redirect — stop it from actually loading and read the result.
            e.Cancel = true;
            CloseAuth();

            var q = ParseQuery(uri);
            if (q.TryGetValue("error", out var err))
            {
                ShowAuthError("Lichess declined sign-in: " + err);
                return;
            }
            if (!q.TryGetValue("code", out var code))
            {
                ShowAuthError("Sign-in failed: no authorization code returned.");
                return;
            }
            if (q.TryGetValue("state", out var st) && st != _authState)
            {
                ShowAuthError("Sign-in failed: security state mismatch.");
                return;
            }

            Busy.IsActive = true;
            try
            {
                bool ok = await AppState.Current.Auth.CompleteAuthAsync(code, _authVerifier);
                if (ok) await RefreshAsync();
                else ShowAuthError("Token exchange failed. Please try again.");
            }
            catch (Exception ex)
            {
                ShowAuthError("Something went wrong: " + ex.Message);
            }
            finally { Busy.IsActive = false; }
        }

        void CancelAuth_Click(object sender, RoutedEventArgs e) => CloseAuth();

        void CloseAuth()
        {
            AuthWeb.NavigationStarting -= AuthWeb_NavigationStarting;
            AuthOverlay.Visibility = Visibility.Collapsed;
            try { AuthWeb.Navigate(new Uri("about:blank")); } catch { }
        }

        void ShowAuthError(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        static System.Collections.Generic.Dictionary<string, string> ParseQuery(string uri)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            int q = uri.IndexOf('?');
            if (q < 0) return d;
            foreach (var pair in uri.Substring(q + 1).Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) d[kv[0]] = Uri.UnescapeDataString(kv[1]);
            }
            return d;
        }

        async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await AppState.Current.SignOutAsync();
            await RefreshAsync();
        }
    }
}
