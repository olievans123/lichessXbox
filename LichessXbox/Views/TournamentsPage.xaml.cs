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
            try
            {
                var tournaments = await AppState.Current.Api.GetTournamentsAsync();
                TournamentList.ItemsSource = tournaments;
                TournamentList.Focus(FocusState.Programmatic);
                if (tournaments == null || tournaments.Count == 0)
                {
                    ListStatus.Text = "No tournaments right now.";
                    ListStatus.Visibility = Visibility.Visible;
                }
                else
                {
                    ListStatus.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                ListStatus.Text = "Couldn't load tournaments.";
                ListStatus.Visibility = Visibility.Visible;
            }
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

            DetailStatus.Text = "Loading standings…";
            DetailStatus.Visibility = Visibility.Visible;
            try
            {
                var (title, players) = await AppState.Current.Api.GetTournamentStandingsAsync(t.Id);
                if (!string.IsNullOrEmpty(title)) DetailTitle.Text = title;
                StandingsList.ItemsSource = players;
                DetailStatus.Text = "";
                DetailStatus.Visibility = Visibility.Collapsed;
            }
            catch
            {
                DetailStatus.Text = "Standings unavailable.";
                DetailStatus.Visibility = Visibility.Visible;
            }
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
