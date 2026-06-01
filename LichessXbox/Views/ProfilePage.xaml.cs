using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LichessXbox.Helpers;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class ProfilePage : Page
    {
        LanCallbackServer _server;
        string _verifier, _state;
        int _pairAttempt;   // guards against stale callbacks when restarting/cancelling

        public ProfilePage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();

        protected override void OnNavigatedFrom(NavigationEventArgs e) => StopPairing();

        async System.Threading.Tasks.Task RefreshAsync()
        {
            if (!AppState.Current.IsSignedIn)
            {
                SignedInPanel.Visibility = Visibility.Collapsed;
                SignedOutCard.Visibility = Visibility.Visible;
                ShowQr();              // start device pairing by default
                return;
            }

            var account = await AppState.Current.EnsureAccountAsync();
            if (account == null)
            {
                await AppState.Current.SignOutAsync();
                SignedInPanel.Visibility = Visibility.Collapsed;
                SignedOutCard.Visibility = Visibility.Visible;
                ShowQr();
                return;
            }

            StopPairing();
            ShowAccount(account);
        }

        async void ShowAccount(LichessAccount a)
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
            NoRatings.Visibility = (ratings.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => SignOutButton.Focus(FocusState.Programmatic));
        }

        // ----------------------------------------------------- QR device pairing

        void ShowQr()
        {
            QrPanel.Visibility = Visibility.Visible;
            ManualPanel.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => ManualToggleButton.Focus(FocusState.Programmatic));
            _ = StartPairingAsync();
        }

        void ShowManual_Click(object sender, RoutedEventArgs e)
        {
            StopPairing();
            QrPanel.Visibility = Visibility.Collapsed;
            ManualPanel.Visibility = Visibility.Visible;
            TokenBox.Focus(FocusState.Programmatic);
        }

        void ShowQr_Click(object sender, RoutedEventArgs e) => ShowQr();

        async Task StartPairingAsync()
        {
            StopPairing();
            int attempt = ++_pairAttempt;

            QrBusy.IsActive = true;
            QrStatus.Text = "Preparing…";

            var server = new LanCallbackServer();
            bool started = await server.StartAsync();
            if (attempt != _pairAttempt) { server.Dispose(); return; }

            if (!started)
            {
                QrBusy.IsActive = false;
                QrStatus.Text = "Couldn't start pairing on this network — use “Enter a token manually”.";
                return;
            }
            _server = server;

            var auth = AppState.Current.Auth.BuildAuthorization(server.RedirectUri);
            _verifier = auth.verifier;
            _state = auth.state;

            // Render the authorize URL as a QR via a public renderer (the data isn't secret).
            string qrUrl = "https://api.qrserver.com/v1/create-qr-code/?size=320x320&margin=10&data="
                           + Uri.EscapeDataString(auth.url);
            QrImage.Source = new BitmapImage(new Uri(qrUrl));
            QrStatus.Text = "Scan, sign in on your phone, and approve…";

            try
            {
                Dictionary<string, string> result = await server.WaitForCallbackAsync();
                if (attempt != _pairAttempt) return;   // superseded (toggled/navigated)

                if (result.TryGetValue("state", out var st) && st != _state)
                {
                    QrStatus.Text = "Security check failed — please try again.";
                    return;
                }
                if (!result.TryGetValue("code", out var code))
                {
                    QrStatus.Text = "No code received — try again.";
                    return;
                }

                QrStatus.Text = "Approved! Finishing sign-in…";
                bool ok = await AppState.Current.Auth.CompleteAuthAsync(code, _verifier, server.RedirectUri);
                if (attempt != _pairAttempt) return;
                if (ok) await RefreshAsync();
                else QrStatus.Text = "Token exchange failed — try again or use manual.";
            }
            catch (TaskCanceledException) { /* pairing stopped */ }
            catch (Exception ex) { QrStatus.Text = "Pairing error: " + ex.Message; }
            finally { if (attempt == _pairAttempt) QrBusy.IsActive = false; }
        }

        void StopPairing()
        {
            _pairAttempt++;            // invalidate any in-flight await
            try { _server?.Dispose(); } catch { }
            _server = null;
        }

        // --------------------------------------------------- manual token fallback

        async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;
            string token = TokenBox.Text?.Trim();
            if (string.IsNullOrEmpty(token)) { ShowAuthError("Enter a token first."); return; }

            Busy.IsActive = true;
            SignInButton.IsEnabled = false;
            try
            {
                bool ok = await AppState.Current.SignInWithTokenAsync(token);
                if (ok) await RefreshAsync();
                else ShowAuthError("That token didn't work. Make sure you copied it fully and it has the board:play scope.");
            }
            catch (Exception ex) { ShowAuthError("Something went wrong: " + ex.Message); }
            finally { Busy.IsActive = false; SignInButton.IsEnabled = true; }
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
