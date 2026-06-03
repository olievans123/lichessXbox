using System;
using System.Collections.Generic;
using LichessXbox.Helpers;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LichessXbox.Services
{
    /// <summary>Plays the board sound effects (respecting the user's setting).</summary>
    public static class SoundService
    {
        static readonly Dictionary<string, MediaPlayer> _players = new Dictionary<string, MediaPlayer>();

        public static void Move() => Play("move");
        public static void Capture() => Play("capture");
        /// <summary>The lichess game-over "dong" (GenericNotify) — checkmate, resign, time-out, draw.</summary>
        public static void GameEnd() => Play("notify");

        static void Play(string name)
        {
            if (!BoardTheme.MoveSounds) return;
            try
            {
                if (!_players.TryGetValue(name, out var mp))
                {
                    mp = new MediaPlayer
                    {
                        Source = MediaSource.CreateFromUri(new Uri($"ms-appx:///Assets/Sounds/{name}.mp3")),
                        AutoPlay = false,
                        Volume = 0.6,
                    };
                    _players[name] = mp;
                }
                if (mp.PlaybackSession != null) mp.PlaybackSession.Position = TimeSpan.Zero;
                mp.Play();
            }
            catch { /* audio not critical */ }
        }
    }
}
