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

            var account = await AppState.Current.EnsureAccountAsync();
            if (account == null) return;

            Busy.IsActive = true;
            try
            {
                var games = await AppState.Current.Api.GetUserGamesAsync(account.Username, 25);
                GamesList.ItemsSource = games;
                NoGames.Visibility = (games == null || games.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
                GamesList.Focus(FocusState.Programmatic);
            }
            catch
            {
                NoGames.Visibility = Visibility.Visible;
            }
            finally { Busy.IsActive = false; }

            await LoadRatingAsync(account.Username);
            await LoadFollowingAsync();
        }

        async System.Threading.Tasks.Task LoadRatingAsync(string username)
        {
            try
            {
                var points = await AppState.Current.Api.GetRatingHistoryAsync(username, "Blitz");
                DrawGraph(points);
            }
            catch { RatingRange.Text = "No rating history yet."; }
        }

        async System.Threading.Tasks.Task LoadFollowingAsync()
        {
            try
            {
                var following = await AppState.Current.Api.GetFollowingAsync();
                FollowingList.ItemsSource = following;
                NoFollowing.Visibility = following.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        void DrawGraph(List<(int x, int y)> points)
        {
            GraphCanvas.Children.Clear();
            if (points == null || points.Count < 2) { RatingRange.Text = "No rating history yet."; return; }

            double w = 260, h = 150, pad = 10;
            // Lay out after the canvas has a size; fall back to fixed dims.
            if (GraphCanvas.ActualWidth > 10) w = GraphCanvas.ActualWidth;
            if (GraphCanvas.ActualHeight > 10) h = GraphCanvas.ActualHeight;

            int minR = int.MaxValue, maxR = int.MinValue;
            foreach (var p in points) { if (p.y < minR) minR = p.y; if (p.y > maxR) maxR = p.y; }
            if (maxR == minR) { maxR += 1; minR -= 1; }

            int n = points.Count;
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x8F, 0xCB, 0x3F)),
                StrokeThickness = 3,
            };
            var pts = new PointCollection();
            for (int i = 0; i < n; i++)
            {
                double x = pad + (w - 2 * pad) * i / (n - 1);
                double y = pad + (h - 2 * pad) * (1 - (double)(points[i].y - minR) / (maxR - minR));
                pts.Add(new Point(x, y));
            }
            line.Points = pts;
            GraphCanvas.Children.Add(line);

            RatingRange.Text = $"{minR} – {maxR}  ·  {n} games tracked";
        }

        void GoSignIn_Click(object sender, RoutedEventArgs e)
        {
            ((Window.Current.Content as Frame)?.Content as MainPage)?.NavigateTo("profile");
        }

        void Game_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is GameSummary g)) return;
            var shell = (Window.Current.Content as Frame)?.Content as MainPage;
            shell?.OpenAnalysis((g.InitialFen ?? "startpos") + "|" + (g.Moves ?? ""));
        }
    }
}
