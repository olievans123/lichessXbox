using System;
using System.Collections.Generic;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LichessXbox.Views
{
    /// <summary>One chapter of a study (its own PGN game).</summary>
    public sealed class StudyChapter
    {
        public string Name { get; set; }
        public string Pgn { get; set; }
    }

    public sealed partial class StudiesPage : Page
    {
        bool _opening;   // guards against a double-press firing two analysis navigations

        public StudiesPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _opening = false;   // reset when returning (the page instance may be restored via Back)
            UserBox.Focus(FocusState.Programmatic);
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
                if (studies.Count > 0) StudyList.Focus(FocusState.Programmatic);
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
            StatusText.Text = "";
            try
            {
                string pgn = await AppState.Current.Api.GetStudyPgnAsync(study.Id);
                if (string.IsNullOrWhiteSpace(pgn)) { StatusText.Text = "Study has no moves to show."; return; }

                var chapters = ParseChapters(pgn);
                if (chapters.Count <= 1)
                {
                    OpenChapter(chapters.Count == 1 ? chapters[0].Pgn : pgn);
                    return;
                }
                // Multi-chapter study: let the user pick which chapter to open.
                ChaptersTitle.Text = study.Name;
                ChapterList.ItemsSource = chapters;
                StudyList.Visibility = Visibility.Collapsed;
                ChaptersPanel.Visibility = Visibility.Visible;
                ChapterList.Focus(FocusState.Programmatic);
            }
            catch { StatusText.Text = "Couldn't open that study."; }
            finally { Busy.IsActive = false; }
        }

        void Chapter_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is StudyChapter ch) OpenChapter(ch.Pgn);
        }

        void ChaptersBack_Click(object sender, RoutedEventArgs e)
        {
            ChaptersPanel.Visibility = Visibility.Collapsed;
            StudyList.Visibility = Visibility.Visible;
            StudyList.Focus(FocusState.Programmatic);
        }

        void OpenChapter(string pgn)
        {
            if (_opening) return;   // ignore a second press while we're navigating
            _opening = true;
            var (fen, uci) = ChessPosition.PgnToMoves(pgn);
            ((Window.Current.Content as Frame)?.Content as MainPage)?.OpenAnalysis(fen + "|" + uci);
        }

        // A study export concatenates its chapters; each begins with [Event "Study: Chapter"].
        static List<StudyChapter> ParseChapters(string pgn)
        {
            var list = new List<StudyChapter>();
            if (string.IsNullOrWhiteSpace(pgn)) return list;
            int idx = 0;
            while (idx < pgn.Length)
            {
                int next = pgn.IndexOf("\n[Event", idx + 1, StringComparison.OrdinalIgnoreCase);
                string chunk = next > 0 ? pgn.Substring(idx, next - idx) : pgn.Substring(idx);
                list.Add(new StudyChapter { Name = ChapterName(chunk, list.Count + 1), Pgn = chunk });
                if (next < 0) break;
                idx = next + 1;
            }
            return list;
        }

        static string ChapterName(string chunk, int n)
        {
            int e = chunk.IndexOf("[Event \"", StringComparison.OrdinalIgnoreCase);
            if (e >= 0)
            {
                int start = e + 8;
                int end = chunk.IndexOf('"', start);
                if (end > start)
                {
                    string ev = chunk.Substring(start, end - start);
                    int colon = ev.LastIndexOf(": ", StringComparison.Ordinal);   // "Study: Chapter" → "Chapter"
                    return colon >= 0 ? ev.Substring(colon + 2) : ev;
                }
            }
            return "Chapter " + n;
        }
    }
}
