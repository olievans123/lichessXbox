using System;
using System.Threading;
using System.Threading.Tasks;
using LichessXbox.Chess;
using LichessXbox.Models;
using LichessXbox.Services;
using Newtonsoft.Json.Linq;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LichessXbox.Views
{
    /// <summary>
    /// Watch hub: pick any of the Lichess TV channels and stream it live, plus
    /// browse live streamers and broadcasts.
    /// </summary>
    public sealed partial class WatchPage : Page
    {
        CancellationTokenSource _streamCts;
        int _streamAttempt;   // guards OnFrame against late frames from a channel we've switched away from
        bool _orientationWhite = true;
        string _whiteName = "—", _blackName = "—", _whiteRating = "", _blackRating = "";
        string _currentChannel = "best";

        public WatchPage()
        {
            this.InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            StartChannel(_currentChannel, "Lichess TV");
            // The board is non-interactive (watch only), so it can't take focus — the list does.
            await LoadListsAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) => _streamCts?.Cancel();

        async Task LoadListsAsync()
        {
            Busy.IsActive = true;
            try
            {
                try
                {
                    var channels = await AppState.Current.Api.GetTvChannelsAsync();
                    ChannelList.ItemsSource = channels;
                    SetEmptyState(ChannelList, ChannelsEmpty);
                }
                catch { ShowEmptyState(ChannelsEmpty); }
                try { StreamerList.ItemsSource = await AppState.Current.Api.GetLiveStreamersAsync(); SetEmptyState(StreamerList, StreamersEmpty); } catch { ShowEmptyState(StreamersEmpty); }
                try { SimulList.ItemsSource = await AppState.Current.Api.GetSimulsAsync(); SetEmptyState(SimulList, SimulsEmpty); } catch { ShowEmptyState(SimulsEmpty); }
                try { BroadcastList.ItemsSource = await AppState.Current.Api.GetBroadcastsAsync(); SetEmptyState(BroadcastList, BroadcastsEmpty); } catch { ShowEmptyState(BroadcastsEmpty); }
            }
            finally { Busy.IsActive = false; }

            // Land gamepad focus on the first non-empty list so the page is never a dead-end.
            Control target =
                ChannelList.Items.Count > 0 ? (Control)ChannelList :
                StreamerList.Items.Count > 0 ? StreamerList :
                SimulList.Items.Count > 0 ? SimulList :
                BroadcastList.Items.Count > 0 ? BroadcastList : null;
            target?.Focus(Windows.UI.Xaml.FocusState.Programmatic);
        }

        // Streamer/simul/broadcast cards are informational; they're click-enabled only so a
        // gamepad can move focus onto them (and pull the list into view). No action on press.
        void InfoCard_ItemClick(object sender, ItemClickEventArgs e) { }

        static void SetEmptyState(ListView list, TextBlock empty)
        {
            empty.Visibility = list.Items.Count == 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
        }

        static void ShowEmptyState(TextBlock empty) => empty.Visibility = Windows.UI.Xaml.Visibility.Visible;

        void Channel_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TvChannel ch) StartChannel(ch.Key, ch.Name);
        }

        void StartChannel(string key, string title)
        {
            _streamCts?.Cancel();
            _streamCts = new CancellationTokenSource();
            int attempt = ++_streamAttempt;   // late frames from the old stream are stamped with a stale id
            var ct = _streamCts.Token;
            _currentChannel = key;
            HeaderText.Text = title;
            // Clear the previous channel's game so it doesn't linger until the first frame arrives.
            TopName.Text = ""; BottomName.Text = ""; TopRating.Text = ""; BottomRating.Text = "";
            TopClock.Text = "--:--"; BottomClock.Text = "--:--";
            Board.LastMove = null;
            Board.Position = ChessPosition.Starting();   // neutral board until the first frame lands
            _ = RunAsync(key, attempt, ct);
        }

        async Task RunAsync(string key, int attempt, CancellationToken ct)
        {
            // Keep the stream alive across transient drops (sleep/resume, Wi-Fi blips): when it
            // ends without being cancelled, reconnect after a backoff. Stops the moment the user
            // switches channels (attempt changes) or leaves the page (ct cancelled).
            void Frame(JObject f) => OnFrame(f, attempt);
            int delayMs = 1000;
            while (!ct.IsCancellationRequested && attempt == _streamAttempt)
            {
                try
                {
                    if (key == "best") await AppState.Current.Api.StreamTvFeedAsync(Frame, ct);
                    else await AppState.Current.Api.StreamTvChannelAsync(key, Frame, ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Watch stream dropped: " + ex.Message); }

                if (ct.IsCancellationRequested || attempt != _streamAttempt) return;
                try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { return; }
                delayMs = Math.Min(delayMs * 2, 15000);   // exponential backoff, capped at 15s
            }
        }

        void OnFrame(JObject frame, int attempt)
        {
            if (attempt != _streamAttempt) return;   // a late frame from a channel we've since left
            string t = frame.Value<string>("t");
            var d = frame["d"];
            if (d == null) return;

            if (t == "featured")
            {
                _orientationWhite = (d.Value<string>("orientation") ?? "white") == "white";
                var players = d["players"] as JArray;
                if (players != null)
                {
                    foreach (var p in players)
                    {
                        string color = p.Value<string>("color");
                        string name = p["user"]?.Value<string>("name") ?? "Anonymous";
                        string title = p["user"]?.Value<string>("title");
                        if (!string.IsNullOrEmpty(title)) name = title + " " + name;
                        string rating = p.Value<int?>("rating")?.ToString() ?? "";
                        if (color == "white") { _whiteName = name; _whiteRating = rating; }
                        else { _blackName = name; _blackRating = rating; }
                    }
                }
                Board.WhiteAtBottom = _orientationWhite;
                ApplyBoard(d.Value<string>("fen"), null);
                ApplyPlayers();
                ApplyClocks(d["players"] as JArray);
            }
            else if (t == "fen")
            {
                ApplyBoard(d.Value<string>("fen"), d.Value<string>("lm"));
                long? wc = d.Value<long?>("wc"); long? bc = d.Value<long?>("bc");
                if (wc.HasValue) SetClock(true, wc.Value * 1000);
                if (bc.HasValue) SetClock(false, bc.Value * 1000);
            }
        }

        void ApplyBoard(string fen, string lastMoveUci)
        {
            if (string.IsNullOrEmpty(fen)) return;
            if (!fen.Contains(" ")) fen = fen + " w - - 0 1";
            try
            {
                // TV frames are untrusted: a malformed fen/lm must be ignored, not throw out to the
                // global handler and pop an error dialog over the board. (Play/Analysis already guard.)
                Board.LastMove = string.IsNullOrEmpty(lastMoveUci) ? (ChessMove?)null : ChessMove.FromUci(lastMoveUci);
                Board.Position = ChessPosition.FromFen(fen);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Bad TV frame ignored: " + ex.Message); }
        }

        void ApplyPlayers()
        {
            if (_orientationWhite)
            {
                BottomName.Text = _whiteName; BottomRating.Text = _whiteRating;
                TopName.Text = _blackName; TopRating.Text = _blackRating;
            }
            else
            {
                BottomName.Text = _blackName; BottomRating.Text = _blackRating;
                TopName.Text = _whiteName; TopRating.Text = _whiteRating;
            }
        }

        void ApplyClocks(JArray players)
        {
            if (players == null) return;
            foreach (var p in players)
            {
                string color = p.Value<string>("color");
                long? seconds = p.Value<long?>("seconds");
                if (seconds.HasValue) SetClock(color == "white", seconds.Value * 1000);
            }
        }

        void SetClock(bool white, long ms)
        {
            bool bottom = white == _orientationWhite;
            string text = Format(ms);
            if (bottom) BottomClock.Text = text; else TopClock.Text = text;
        }

        static string Format(long ms)
        {
            if (ms <= 0) return "0:00";
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return string.Format("{0}:{1:00}", (int)ts.TotalMinutes, ts.Seconds);
        }
    }
}
