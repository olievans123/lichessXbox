using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Xaml.Media;

namespace LichessXbox.Helpers
{
    /// <summary>
    /// User-selectable board appearance. The chess board reads these colours when
    /// it builds and re-themes live when <see cref="Changed"/> fires. Persisted to
    /// local settings so the choice survives restarts.
    /// </summary>
    public static class BoardTheme
    {
        public sealed class Preset
        {
            public string Name { get; set; }
            public Color Light { get; set; }
            public Color Dark { get; set; }
            public Preset(string name, Color light, Color dark) { Name = name; Light = light; Dark = dark; }
            public SolidColorBrush LightBrush => new SolidColorBrush(Light);
            public SolidColorBrush DarkBrush => new SolidColorBrush(Dark);
        }

        public static readonly List<Preset> Presets = new List<Preset>
        {
            new Preset("Green",  C(0xEB,0xEC,0xD0), C(0x73,0x95,0x52)),
            new Preset("Brown",  C(0xF0,0xD9,0xB5), C(0xB5,0x88,0x63)),
            new Preset("Blue",   C(0xDE,0xE3,0xE6), C(0x8C,0xA2,0xAD)),
            new Preset("Slate",  C(0xCC,0xCF,0xD6), C(0x5B,0x73,0x92)),
            new Preset("Purple", C(0xE6,0xDD,0xF0), C(0x8A,0x6F,0xB0)),
            new Preset("Ink",    C(0xC8,0xC4,0xBC), C(0x3E,0x3A,0x33)),
        };

        public static Color Light { get; private set; } = Presets[0].Light;
        public static Color Dark { get; private set; } = Presets[0].Dark;

        /// <summary>True = use outlined glyph piece set; false = solid pieces.</summary>
        public static bool OutlinePieces { get; private set; }

        /// <summary>Play move/capture/check sounds on the board.</summary>
        public static bool MoveSounds { get; private set; } = true;

        public static string CurrentName { get; private set; } = "Green";

        /// <summary>Selected piece set ("Native" = the built-in Unicode glyphs, else a lichess SVG set).
        /// Defaults to lichess's classic set; downloaded on first use, glyphs shown until ready.</summary>
        public static string PieceSet { get; private set; } = PieceSets.Default;

        public static event Action Changed;

        static BoardTheme()
        {
            // When a piece set finishes downloading, treat it like a theme change so every
            // live board redraws and swaps its glyphs for the SVGs (no manual reload needed).
            PieceSets.Ready += _ => Changed?.Invoke();
        }

        public static void Apply(string presetName)
        {
            var p = Presets.Find(x => x.Name == presetName) ?? Presets[0];
            Light = p.Light; Dark = p.Dark; CurrentName = p.Name;
            Save("BoardTheme", p.Name);
            Changed?.Invoke();
        }

        public static void SetOutlinePieces(bool outline)
        {
            OutlinePieces = outline;
            Save("OutlinePieces", outline ? "1" : "0");
            Changed?.Invoke();
        }

        public static void SetMoveSounds(bool on)
        {
            MoveSounds = on;
            Save("MoveSounds", on ? "1" : "0");
        }

        public static void SetPieceSet(string set)
        {
            PieceSet = string.IsNullOrEmpty(set) ? PieceSets.Native : set;
            Save("PieceSet", PieceSet);
            Changed?.Invoke();
        }

        public static void Load()
        {
            try
            {
                var s = ApplicationData.Current.LocalSettings.Values;
                if (s.TryGetValue("BoardTheme", out var name) && name is string n)
                {
                    var p = Presets.Find(x => x.Name == n);
                    if (p != null) { Light = p.Light; Dark = p.Dark; CurrentName = p.Name; }
                }
                if (s.TryGetValue("OutlinePieces", out var o) && o is string os) OutlinePieces = os == "1";
                if (s.TryGetValue("MoveSounds", out var m) && m is string ms) MoveSounds = ms == "1";
                if (s.TryGetValue("PieceSet", out var pv) && pv is string ps && !string.IsNullOrEmpty(ps)) PieceSet = ps;
            }
            catch { /* first run */ }
        }

        static void Save(string key, string value)
        {
            try { ApplicationData.Current.LocalSettings.Values[key] = value; } catch { }
        }

        static Color C(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    }

    /// <summary>
    /// Open-source lichess piece sets (lichess-org/lila/public/piece). Each set is 12
    /// SVGs (wK..bP); we download them on first use into local storage and render them
    /// via SvgImageSource. "Native" = the built-in Unicode glyphs (always available).
    /// </summary>
    public static class PieceSets
    {
        public const string Native = "Native";
        /// <summary>The set used by default — lichess's classic Colin M.L. Burnett pieces.</summary>
        public const string Default = "cburnett";
        static readonly string[] Codes = { "wK", "wQ", "wR", "wB", "wN", "wP", "bK", "bQ", "bR", "bB", "bN", "bP" };

        /// <summary>Native first, then the lichess sets.</summary>
        public static readonly string[] All =
        {
            Native, "cburnett", "merida", "alpha", "maestro", "fresca", "cardinal", "staunty",
            "governor", "dubrovny", "gioco", "icpieces", "tatiana", "california", "pixel", "mono",
            "letter", "shapes", "chessnut", "companion", "leipzig", "fantasy", "spatial", "riohacha",
            "celtic", "chess7", "kosal", "pirouetti", "reillycraig", "horsey", "anarcandy", "caliente",
            "cooke", "disguised", "firi", "kiwen-suwi", "monarchy", "mpchess", "rhosgfx",
            "shahi-ivory-brown", "totoy", "xkcd",
        };

        // Native is handled by the explicit checks below, so it is never stored here.
        // ConcurrentDictionary + a download semaphore keep the startup warm-up, each board's
        // own load, and the Settings picker from racing on shared state. _http is created once.
        static readonly ConcurrentDictionary<string, byte> _ready = new ConcurrentDictionary<string, byte>();
        static readonly SemaphoreSlim _downloadGate = new SemaphoreSlim(1, 1);
        static readonly HttpClient _http = new HttpClient();

        /// <summary>Raised (with the set name) once a set's SVGs are cached, so live boards can redraw.</summary>
        public static event Action<string> Ready;

        public static bool IsReady(string set) => string.IsNullOrEmpty(set) || set == Native || _ready.ContainsKey(set);

        /// <summary>Local URI for a piece's SVG, or null to fall back to the Unicode glyph.</summary>
        public static Uri SourceFor(string set, char piece)
        {
            if (string.IsNullOrEmpty(set) || set == Native || !_ready.ContainsKey(set)) return null;
            string code = (char.IsUpper(piece) ? "w" : "b") + char.ToUpperInvariant(piece);
            return new Uri($"ms-appdata:///local/piece/{set}/{code}.svg");
        }

        /// <summary>
        /// Ensure a set's 12 SVGs are cached locally (downloading any that are missing).
        /// Returns true once the set is usable. Safe to call repeatedly; cheap if cached.
        /// </summary>
        public static async Task<bool> EnsureAsync(string set)
        {
            if (string.IsNullOrEmpty(set) || set == Native) return true;
            if (_ready.ContainsKey(set)) return true;

            // Serialize downloads so the startup warm-up and a board's own load don't both
            // fetch (and collide writing) the same 12 files. Whoever waits re-checks and exits.
            await _downloadGate.WaitAsync();
            bool fresh = false;
            try
            {
                if (_ready.ContainsKey(set)) return true;   // another caller finished while we waited
                var root = await ApplicationData.Current.LocalFolder.CreateFolderAsync("piece", CreationCollisionOption.OpenIfExists);
                var dir = await root.CreateFolderAsync(set, CreationCollisionOption.OpenIfExists);
                foreach (var code in Codes)
                {
                    if (await dir.TryGetItemAsync(code + ".svg") != null) continue;   // already cached
                    var url = $"https://raw.githubusercontent.com/lichess-org/lila/master/public/piece/{set}/{code}.svg";
                    var bytes = await _http.GetByteArrayAsync(url);
                    var file = await dir.CreateFileAsync(code + ".svg", CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(file, bytes);
                }
                _ready[set] = 0;
                fresh = true;
            }
            catch { return false; }
            finally { _downloadGate.Release(); }

            if (fresh) Ready?.Invoke(set);   // outside the gate; UI-thread continuation → safe for boards to redraw
            return true;
        }
    }
}
