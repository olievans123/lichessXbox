using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LichessXbox.Helpers;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace LichessXbox.Views
{
    public sealed partial class ProfilePage : Page
    {
        LanCallbackServer _server;
        string _verifier, _state;
        int _pairAttempt;   // guards against stale callbacks when restarting/cancelling
        readonly DispatcherTimer _qrExpiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };

        public ProfilePage()
        {
            this.InitializeComponent();
            _qrExpiry.Tick += (s, e) =>
            {
                _qrExpiry.Stop();
                QrBusy.IsActive = false;
                QrStatus.Text = "This code expired — press “New code” for a fresh one.";
            };
            // The QR is rendered by a web service; if that fetch fails, say so instead of
            // leaving a blank white square.
            QrImage.ImageFailed += (s, e) =>
                QrStatus.Text = "Couldn't load the QR code — check the connection, or use “Enter a token”.";
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e) => await RefreshAsync();

        protected override void OnNavigatedFrom(NavigationEventArgs e) => StopPairing();

        async System.Threading.Tasks.Task RefreshAsync()
        {
            // The signed-in view lives in its own ScrollViewer; keep it fully collapsed (not just
            // its content) in every other state, so it can't overlay the sign-in card and swallow
            // gamepad focus — an empty ScrollViewer is still a focus stop on Xbox.
            SignedInScroller.Visibility = Visibility.Collapsed;
            if (!AppState.Current.IsSignedIn)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
                SignedInPanel.Visibility = Visibility.Collapsed;
                SignedOutCard.Visibility = Visibility.Visible;
                ShowQr();              // start device pairing by default
                return;
            }

            // Communicate progress while the account fetch is in flight so the page isn't blank.
            SignedInPanel.Visibility = Visibility.Collapsed;
            SignedOutCard.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingPanel.Visibility = Visibility.Visible;

            // Offline with a stored token must degrade to the sign-in card, not throw out of the
            // async void OnNavigatedTo and pop the global error dialog.
            LichessAccount account = null;
            try { account = await AppState.Current.EnsureAccountAsync(); } catch { }

            LoadingPanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;

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
            LoadingPanel.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
            SignedOutCard.Visibility = Visibility.Collapsed;
            SignedInScroller.Visibility = Visibility.Visible;
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
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => RefreshQrButton.Focus(FocusState.Programmatic));
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
                QrStatus.Text = "Couldn't start pairing on this network — use “Enter a token”.";
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
            QrStatus.Text = "Waiting for you to approve on your phone…";
            QrBusy.IsActive = false;   // the code is ready to scan — no spinner while we wait
            _qrExpiry.Start();

            try
            {
                Dictionary<string, string> result = await server.WaitForCallbackAsync();
                if (attempt != _pairAttempt) return;   // superseded (toggled/navigated)
                _qrExpiry.Stop();

                // Require the state to be present AND match the one we sent (CSRF / mix-up guard).
                // We always send a state, so a missing one is as suspect as a wrong one.
                if (!result.TryGetValue("state", out var st) || st != _state)
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
                QrBusy.IsActive = true;
                bool ok = await AppState.Current.Auth.CompleteAuthAsync(code, _verifier, server.RedirectUri);
                if (attempt != _pairAttempt) return;
                if (ok) await RefreshAsync();
                else QrStatus.Text = "Token exchange failed — try again or use manual.";
            }
            catch (TaskCanceledException) { /* pairing stopped */ }
            catch (Exception) { QrStatus.Text = "Pairing didn't complete — press “New code” to try again, or use “Enter a token”."; }
            finally { if (attempt == _pairAttempt) QrBusy.IsActive = false; }
        }

        void StopPairing()
        {
            _pairAttempt++;            // invalidate any in-flight await
            _qrExpiry.Stop();
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
            Busy.Visibility = Visibility.Visible;
            SignInButton.IsEnabled = false;
            bool failed = false;
            try
            {
                bool ok = await AppState.Current.SignInWithTokenAsync(token);
                if (ok) await RefreshAsync();
                else { ShowAuthError("That token didn't work. Make sure you copied it fully and it has the board:play scope."); failed = true; }
            }
            catch (Exception ex) { ShowAuthError("Something went wrong: " + ex.Message); failed = true; }
            finally
            {
                Busy.IsActive = false;
                Busy.Visibility = Visibility.Collapsed;
                SignInButton.IsEnabled = true;
                if (failed) SignInButton.Focus(FocusState.Programmatic);
            }
        }

        void ShowAuthError(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        // Fill the token box from the system clipboard, so the user doesn't have to type it.
        async void PasteToken_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    string text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text)) { TokenBox.Text = text.Trim(); StatusText.Visibility = Visibility.Collapsed; }
                }
            }
            catch { /* clipboard unavailable */ }
        }

        async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            await AppState.Current.SignOutAsync();
            await RefreshAsync();
        }

    }
}
