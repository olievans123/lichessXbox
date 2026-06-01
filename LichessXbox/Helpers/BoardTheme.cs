using System;
using System.Collections.Generic;
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

        public static event Action Changed;

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
            }
            catch { /* first run */ }
        }

        static void Save(string key, string value)
        {
            try { ApplicationData.Current.LocalSettings.Values[key] = value; } catch { }
        }

        static Color C(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    }
}
