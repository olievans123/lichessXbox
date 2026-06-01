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
            await LoadListsAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e) => _streamCts?.Cancel();

        async Task LoadListsAsync()
        {
            try
            {
                var channels = await AppState.Current.Api.GetTvChannelsAsync();
                ChannelList.ItemsSource = channels;
            }
            catch { }
            try { StreamerList.ItemsSource = await AppState.Current.Api.GetLiveStreamersAsync(); } catch { }
            try { SimulList.ItemsSource = await AppState.Current.Api.GetSimulsAsync(); } catch { }
            try { BroadcastList.ItemsSource = await AppState.Current.Api.GetBroadcastsAsync(); } catch { }
        }

        void Channel_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is TvChannel ch) StartChannel(ch.Key, ch.Name);
        }

        void StartChannel(string key, string title)
        {
            _streamCts?.Cancel();
            _streamCts = new CancellationTokenSource();
            var ct = _streamCts.Token;
            _currentChannel = key;
            HeaderText.Text = title;
            _ = RunAsync(key, ct);
        }

        async Task RunAsync(string key, CancellationToken ct)
        {
            try
            {
                if (key == "best") await AppState.Current.Api.StreamTvFeedAsync(OnFrame, ct);
                else await AppState.Current.Api.StreamTvChannelAsync(key, OnFrame, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Watch stream ended: " + ex.Message); }
        }

        void OnFrame(JObject frame)
        {
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
            Board.LastMove = string.IsNullOrEmpty(lastMoveUci) ? (ChessMove?)null : ChessMove.FromUci(lastMoveUci);
            Board.Position = ChessPosition.FromFen(fen);
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
