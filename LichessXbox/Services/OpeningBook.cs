using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LichessXbox.Chess;
using Windows.Storage;

namespace LichessXbox.Services
{
    public sealed class BookMove
    {
        public string San;
        public string Uci;
        public string Name;       // the opening the move leads to
        public string ChildKey;   // internal: position key after the move (for label resolution)
    }

    /// <summary>
    /// Offline opening book built from lichess's chess-openings list. The lines are replayed with
    /// OUR engine so the position keys always match the live board, and it needs no network — the
    /// online explorer host (explorer.lichess.ovh) isn't reachable from the Xbox.
    ///
    /// Given a position it can name the opening and list the named continuations from it.
    /// </summary>
    public sealed class OpeningBook
    {
        static OpeningBook _instance;
        public static OpeningBook Instance => _instance ?? (_instance = new OpeningBook());

        readonly Dictionary<string, string> _name = new Dictionary<string, string>();
        readonly Dictionary<string, List<BookMove>> _moves = new Dictionary<string, List<BookMove>>();
        Task _loadTask;
        bool _loaded;

        public bool IsLoaded => _loaded;

        /// <summary>Load + parse once (idempotent); safe to await from every position refresh.</summary>
        public Task EnsureLoadedAsync() => _loadTask ?? (_loadTask = LoadAsync());

        async Task LoadAsync()
        {
            try
            {
                var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/openings.tsv"));
                string text = await FileIO.ReadTextAsync(file);
                await Task.Run(() => Parse(text));   // CPU-bound replay off the UI thread
            }
            catch { /* book stays empty — the explorer just shows "unavailable" */ }
            _loaded = true;
        }

        void Parse(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = line.Split('\t');
                if (cols.Length < 3) continue;
                string name = cols[1];
                string pgn = cols[2];

                var pos = ChessPosition.Starting();
                string prevKey = Key(pos);
                foreach (var tok in pgn.Split(' '))
                {
                    if (tok.Length == 0 || tok.EndsWith(".")) continue;   // skip move numbers like "1."
                    ChessMove? mv;
                    try { mv = pos.ParseSan(tok); } catch { mv = null; }
                    if (mv == null) break;
                    ChessPosition next;
                    try { next = pos.Apply(mv.Value); } catch { next = null; }
                    if (next == null) break;
                    string childKey = Key(next);
                    AddMove(prevKey, tok, mv.Value.ToUci(), name, childKey);
                    pos = next;
                    prevKey = childKey;
                }
                // The position at the end of this line IS this opening (first writer wins).
                if (!_name.ContainsKey(prevKey)) _name[prevKey] = name;
            }

            // Relabel each continuation with the opening its resulting position is named for, when
            // known (more accurate than the whole line's name).
            foreach (var list in _moves.Values)
                foreach (var m in list)
                    if (m.ChildKey != null && _name.TryGetValue(m.ChildKey, out var childName))
                        m.Name = childName;
        }

        void AddMove(string parentKey, string san, string uci, string name, string childKey)
        {
            if (!_moves.TryGetValue(parentKey, out var list)) { list = new List<BookMove>(); _moves[parentKey] = list; }
            foreach (var m in list) if (m.Uci == uci) return;   // dedup transpositions
            list.Add(new BookMove { San = san, Uci = uci, Name = name, ChildKey = childKey });
        }

        // Position key = board + side-to-move + castling (omit en-passant/clocks so matching is
        // robust against differing ep conventions).
        static string Key(ChessPosition p)
        {
            var f = p.ToFen().Split(' ');
            return f.Length >= 3 ? f[0] + " " + f[1] + " " + f[2] : f[0];
        }

        public string Name(ChessPosition p) =>
            _loaded && _name.TryGetValue(Key(p), out var n) ? n : null;

        public IReadOnlyList<BookMove> Moves(ChessPosition p) =>
            _loaded && _moves.TryGetValue(Key(p), out var m) ? m : null;
    }
}
