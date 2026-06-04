using System;
using LichessXbox.Chess;
using Windows.UI.Xaml.Controls;

namespace LichessXbox.Services
{
    /// <summary>
    /// Drives a local Stockfish (stockfish.js) running inside a hidden WebView, so
    /// analysis works for any position — including ones the cloud hasn't seen.
    ///
    /// Pure UCI over a tiny JS bridge: we send "position fen … / go depth …" and parse
    /// the "info … score … pv …" lines back into a White-relative eval and a SAN line.
    /// Bundle a real <c>stockfish.js</c> in <c>Assets/engine/</c> for offline use; the
    /// host page falls back to a CDN copy when it's missing.
    /// </summary>
    public sealed class LocalEngine
    {
        readonly WebView _web;
        bool _ready;
        ChessPosition _context = ChessPosition.Starting();

        /// <summary>Fires with (depth, evalText, pvSan, bestMoveUci) as analysis deepens.</summary>
        public event Action<int, string, string, string> Info;
        public event Action<bool> ReadyChanged;

        public bool IsReady => _ready;

        public LocalEngine(WebView web)
        {
            _web = web;
            _web.ScriptNotify += OnScriptNotify;
            _web.NavigationFailed += (s, e) => System.Diagnostics.Debug.WriteLine("Engine page failed: " + e.WebErrorStatus);
            _web.Navigate(new Uri("ms-appx-web:///Assets/engine/engine.html"));
        }

        async void Send(string cmd)
        {
            try { await _web.InvokeScriptAsync("send", new[] { cmd }); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Engine send failed: " + ex.Message); }
        }

        /// <summary>Analyze a position to a fixed depth. Cancels any running search first.</summary>
        public void Analyze(ChessPosition pos, int depth = 18)
        {
            _context = pos;
            if (!_ready) return;
            Send("stop");
            Send("position fen " + pos.ToFen());
            Send("go depth " + depth);
        }

        public void Stop() { if (_ready) Send("stop"); }

        /// <summary>
        /// Fully tear the engine down: halt the search, stop listening for notifications,
        /// and navigate the WebView to a blank page so the Stockfish WASM heap is released.
        /// Without this the asm.js heap stays resident — costly under Xbox's hard memory cap.
        /// </summary>
        public void Shutdown()
        {
            try
            {
                if (_ready) Send("stop");
                _ready = false;
                _web.ScriptNotify -= OnScriptNotify;
                _web.Navigate(new Uri("about:blank"));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Engine shutdown failed: " + ex.Message); }
        }

        void OnScriptNotify(object sender, NotifyEventArgs e)
        {
            string line = e.Value ?? "";
            if (line.StartsWith("engineready"))
            {
                Send("uci");
                Send("isready");
                Send("setoption name MultiPV value 1");
            }
            else if (line.StartsWith("readyok") || line.StartsWith("uciok"))
            {
                if (!_ready) { _ready = true; ReadyChanged?.Invoke(true); }
            }
            else if (line.StartsWith("info ") && line.Contains(" pv "))
            {
                ParseInfo(line);
            }
        }

        void ParseInfo(string line)
        {
            var tok = line.Split(' ');
            int depth = 0;
            int scoreCp = 0, scoreMate = 0;
            bool isMate = false, haveScore = false;
            string pvUci = "";

            for (int i = 0; i < tok.Length; i++)
            {
                switch (tok[i])
                {
                    case "depth":
                        if (i + 1 < tok.Length) int.TryParse(tok[i + 1], out depth);
                        break;
                    case "score":
                        if (i + 2 < tok.Length)
                        {
                            haveScore = true;
                            if (tok[i + 1] == "mate") { isMate = true; int.TryParse(tok[i + 2], out scoreMate); }
                            else int.TryParse(tok[i + 2], out scoreCp);
                        }
                        break;
                    case "pv":
                        pvUci = string.Join(" ", tok, i + 1, tok.Length - (i + 1));
                        i = tok.Length;
                        break;
                }
            }

            if (!haveScore) return;

            // UCI scores are from the side to move; orient to White.
            bool whiteToMove = _context.WhiteToMove;
            string evalText;
            if (isMate)
            {
                int m = whiteToMove ? scoreMate : -scoreMate;
                evalText = (m >= 0 ? "#" : "#-") + Math.Abs(m);
            }
            else
            {
                double cp = whiteToMove ? scoreCp : -scoreCp;
                evalText = (cp >= 0 ? "+" : "") + (cp / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }

            string pvSan = string.IsNullOrWhiteSpace(pvUci) ? "" : _context.LineToSan(pvUci.Split(' '));
            string bestUci = pvUci ?? "";
            int sp = bestUci.IndexOf(' ');
            if (sp > 0) bestUci = bestUci.Substring(0, sp);   // first PV move = the best move
            Info?.Invoke(depth, evalText, pvSan, bestUci);
        }
    }
}
