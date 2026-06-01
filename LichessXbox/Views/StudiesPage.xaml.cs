using System;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LichessXbox.Views
{
    public sealed partial class StudiesPage : Page
    {
        public StudiesPage()
        {
            this.InitializeComponent();
        }

        async void Load_Click(object sender, RoutedEventArgs e)
        {
            string user = UserBox.Text?.Trim();
            if (string.IsNullOrEmpty(user)) return;
            Busy.IsActive = true;
            StatusText.Text = "";
            StudyList.ItemsSource = null;
            try
            {
                var studies = await AppState.Current.Api.GetStudiesByUserAsync(user);
                StudyList.ItemsSource = studies;
                if (studies.Count == 0) StatusText.Text = $"No public studies found for {user}.";
            }
            catch
            {
                StatusText.Text = "Couldn't load studies. Check the username.";
            }
            finally { Busy.IsActive = false; }
        }

        async void Study_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is StudyItem study)) return;
            Busy.IsActive = true;
            try
            {
                string pgn = await AppState.Current.Api.GetStudyPgnAsync(study.Id);
                if (string.IsNullOrWhiteSpace(pgn)) { StatusText.Text = "Study has no moves to show."; return; }

                // Use the first chapter (chapters are separated by a blank line).
                string firstChapter = FirstChapter(pgn);
                var (fen, uci) = ChessPosition.PgnToMoves(firstChapter);

                var shell = (Window.Current.Content as Frame)?.Content as MainPage;
                shell?.OpenAnalysis(fen + "|" + uci);
            }
            catch { StatusText.Text = "Couldn't open that study."; }
            finally { Busy.IsActive = false; }
        }

        static string FirstChapter(string pgn)
        {
            // A study export concatenates chapters; each begins with [Event ...].
            int second = pgn.IndexOf("\n[Event", 1, StringComparison.OrdinalIgnoreCase);
            return second > 0 ? pgn.Substring(0, second) : pgn;
        }
    }
}
