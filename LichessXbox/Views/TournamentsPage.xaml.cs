using System;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    public sealed partial class TournamentsPage : Page
    {
        string _selectedId;

        public TournamentsPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            Busy.IsActive = true;
            try { TournamentList.ItemsSource = await AppState.Current.Api.GetTournamentsAsync(); }
            catch { }
            finally { Busy.IsActive = false; }
        }

        async void Tournament_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is TournamentItem t)) return;
            _selectedId = t.Id;
            DetailTitle.Text = t.Name;
            JoinButton.Visibility = AppState.Current.IsSignedIn && t.Group != "Finished"
                ? Visibility.Visible : Visibility.Collapsed;
            StandingsList.ItemsSource = null;

            try
            {
                var (title, players) = await AppState.Current.Api.GetTournamentStandingsAsync(t.Id);
                if (!string.IsNullOrEmpty(title)) DetailTitle.Text = title;
                StandingsList.ItemsSource = players;
            }
            catch { }
        }

        async void Join_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedId)) return;
            bool ok = await AppState.Current.Api.JoinTournamentAsync(_selectedId);
            JoinButton.Content = ok ? "Joined — open Play to be paired" : "Join failed";
            JoinButton.IsEnabled = !ok;
        }
    }
}
