using System;
using LichessXbox.Helpers;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class LoginPage : Page
    {
        // Optional page tag to return to after a successful sign-in.
        string _returnTo;

        public LoginPage()
        {
            this.InitializeComponent();
            this.SignInButton.FocusOnLoad();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _returnTo = e.Parameter as string;
        }

        async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;
            Busy.IsActive = true;
            SignInButton.IsEnabled = false;
            try
            {
                bool ok = await AppState.Current.Auth.SignInAsync();
                if (ok)
                {
                    await AppState.Current.EnsureAccountAsync();
                    var shell = (Window.Current.Content as Frame)?.Content as MainPage;
                    shell?.NavigateTo(_returnTo ?? "profile");
                }
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
    }
}
