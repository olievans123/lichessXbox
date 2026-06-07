using System.Collections.Generic;

namespace LichessXbox.Models
{
    /// <summary>Lichess account (subset of /api/account).</summary>
    public class LichessAccount
    {
        public string Id;
        public string Username;
        public string Title;
        public int? BulletRating;
        public int? BlitzRating;
        public int? RapidRating;
        public int? ClassicalRating;
        public bool Patron;
        public string CountryFlag;
        public long? PlayTimeSeconds;
        public string DisplayName => string.IsNullOrEmpty(Title) ? Username : Title + " " + Username;
    }

    /// <summary>
    /// A challenge / matchmaking preset shown on the Play screen.
    /// Bound members are properties (UWP {Binding} ignores fields).
    /// </summary>
    public class TimeControlPreset
    {
        public string Label { get; set; }
        public int ClockLimitSeconds { get; set; }   // initial clock
        public int ClockIncrementSeconds { get; set; }
        public string Glyph { get; set; }            // small emoji/glyph for the tile
        public bool Rated { get; set; }
        public int Days { get; set; }                 // >0 = correspondence (days per move), else real-time
        public bool IsCorrespondence => Days > 0;
        public string RatedLabel => Rated ? "Rated" : "Casual";

        public TimeControlPreset(string label, int limit, int inc, string glyph, bool rated = true, int days = 0)
        {
            Label = label; ClockLimitSeconds = limit; ClockIncrementSeconds = inc; Glyph = glyph; Rated = rated; Days = days;
        }

        // Online quick-pairing goes through the Lichess Board API seek, which only matches Rapid,
        // Classical and Correspondence — Lichess blocks Bullet/Blitz for third-party apps. So these
        // are the full set of time controls that can actually be paired online.
        public static List<TimeControlPreset> Defaults => new List<TimeControlPreset>
        {
            new TimeControlPreset("Rapid 10+0", 600, 0, "🐎"),
            new TimeControlPreset("Rapid 10+5", 600, 5, "🐎"),
            new TimeControlPreset("Rapid 15+10", 900, 10, "🐎"),
            new TimeControlPreset("Classical 30+0", 1800, 0, "🏛"),
            new TimeControlPreset("Classical 30+20", 1800, 20, "🏛"),
            new TimeControlPreset("Casual 10+0", 600, 0, "🎲", false),
            new TimeControlPreset("Daily 1", 0, 0, "📅", true, 1),
            new TimeControlPreset("Daily 3", 0, 0, "📅", true, 3),
            new TimeControlPreset("Daily 7", 0, 0, "📅", true, 7),
        };

        // Challenging a specific player (the /api/challenge endpoint) allows every speed, so a
        // friend game CAN be Bullet or Blitz. Casual, so it's easy to accept.
        public static List<TimeControlPreset> ChallengeClocks => new List<TimeControlPreset>
        {
            new TimeControlPreset("Bullet 1+0", 60, 0, "🚀", false),
            new TimeControlPreset("Bullet 2+1", 120, 1, "🚀", false),
            new TimeControlPreset("Blitz 3+2", 180, 2, "⚡", false),
            new TimeControlPreset("Blitz 5+3", 300, 3, "⚡", false),
            new TimeControlPreset("Rapid 10+0", 600, 0, "🐎", false),
            new TimeControlPreset("Classical 30+20", 1800, 20, "🏛", false),
        };
    }

    /// <summary>An incoming challenge from another player (shown in the lobby inbox).</summary>
    public class IncomingChallenge
    {
        public string Id { get; set; }
        public string ChallengerName { get; set; }
        public string Description { get; set; }  // e.g. "Blitz · 5+3 · rated"
    }

    /// <summary>A Lichess chess variant. Key matches the API (e.g. "kingOfTheHill").</summary>
    public class ChessVariant
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Glyph { get; set; }
        public ChessVariant(string key, string name, string glyph) { Key = key; Name = name; Glyph = glyph; }

        public static System.Collections.Generic.List<ChessVariant> All => new System.Collections.Generic.List<ChessVariant>
        {
            new ChessVariant("standard", "Standard", "♟"),
            new ChessVariant("chess960", "Chess960", "🎲"),
            new ChessVariant("crazyhouse", "Crazyhouse", "🏠"),
            new ChessVariant("kingOfTheHill", "King of the Hill", "⛰"),
            new ChessVariant("threeCheck", "Three-check", "✓"),
            new ChessVariant("antichess", "Antichess", "🔄"),
            new ChessVariant("atomic", "Atomic", "💥"),
            new ChessVariant("horde", "Horde", "⚔"),
            new ChessVariant("racingKings", "Racing Kings", "🏁"),
        };
    }

    /// <summary>Stockfish difficulty levels offered by Lichess (/api/challenge/ai).</summary>
    public class AiLevel
    {
        public int Level { get; set; }
        public string Label => "Level " + Level;
        public AiLevel(int level) { Level = level; }
    }

    /// <summary>One rating tile on the Profile screen (public props for {Binding}).</summary>
    public class RatingTile
    {
        public string Mode { get; set; }
        public int Value { get; set; }
        public RatingTile(string mode, int value) { Mode = mode; Value = value; }
    }

    /// <summary>A puzzle from /api/puzzle/* — PGN plus the solution line.</summary>
    public class PuzzleInfo
    {
        public string Id;
        public int Rating;
        public List<string> Themes = new List<string>();
        public string Pgn;             // moves of the game up to the puzzle
        public List<string> Solution = new List<string>(); // UCI moves
        public int InitialPly;         // ply count before the puzzle position
        public string Fen;             // computed starting FEN for the puzzle
        public bool PlayerIsWhite;     // side the solver plays
    }

    /// <summary>One side's live clock / player info inside a game.</summary>
    public class GamePlayer
    {
        public string Name;
        public int Rating;
        public string Title;
        public int ClockMs;
        public string Display => string.IsNullOrEmpty(Title) ? Name : Title + " " + Name;
    }

    // ===================== Analysis =====================

    /// <summary>Cloud engine evaluation of a position (/api/cloud-eval).</summary>
    public class CloudEval
    {
        public string EvalText { get; set; }   // "+0.42" or "#3"
        public int Depth { get; set; }
        public string BestLineSan { get; set; }
        public string BestMoveUci { get; set; }
        public string PvUci { get; set; }       // raw principal variation (space-separated UCI)
        public double WhiteAdvantage { get; set; } // -1..1 for the eval bar
    }

    /// <summary>One candidate move row in the opening explorer.</summary>
    public class ExplorerMoveRow
    {
        public string San { get; set; }
        public string Uci { get; set; }
        public long Total { get; set; }
        public string Stats { get; set; }       // "12,304 games · avg 2243"
        public double WhitePct { get; set; }
        public double DrawPct { get; set; }
        public double BlackPct { get; set; }

        // Pixel widths for a 160px stacked result bar (UWP {Binding} can't do math).
        public double BarWhite => WhitePct * 1.6;
        public double BarDraw => DrawPct * 1.6;
        public double BarBlack => BlackPct * 1.6;
    }

    public class ExplorerResult
    {
        public string OpeningName { get; set; }
        public List<ExplorerMoveRow> Moves { get; set; } = new List<ExplorerMoveRow>();
    }

    /// <summary>One tablebase move row (perfect-play endgame).</summary>
    public class TablebaseRow
    {
        public string San { get; set; }
        public string Uci { get; set; }
        public string Outcome { get; set; }     // "Win in 12", "Draw", "Loss in 7"
    }

    public class TablebaseResult
    {
        public string Summary { get; set; }     // "White wins · DTZ 17"
        public List<TablebaseRow> Moves { get; set; } = new List<TablebaseRow>();
    }

    // ===================== Account / history =====================

    /// <summary>A finished game in the user's history (for the Games list + replay).</summary>
    public class GameSummary
    {
        public string Id { get; set; }
        public string Headline { get; set; }    // "vs DrNykterstein  (2843)" — opponent + their rating
        public string ResultText { get; set; }  // "Win · 1-0  ·  1502" — result + your rating
        public string TimeControlText { get; set; }  // "Blitz · 5+3"
        public string DateText { get; set; }
        public string Moves { get; set; }        // space-separated SAN moves
        public string InitialFen { get; set; }
        public string FinalFen { get; set; }      // position after the last move (for the board thumbnail)
        public bool PlayerWhite { get; set; }     // orient the thumbnail to the player's side
        public int Outcome { get; set; }          // 0 = win, 1 = loss, 2 = draw (for the result colour)
        public string WhiteName { get; set; }     // player names (for the analysis-board header)
        public string BlackName { get; set; }
    }

    // ===================== Watch =====================

    /// <summary>A Lichess TV channel (/api/tv/channels) e.g. Bullet, Blitz, Bot, Computer.</summary>
    public class TvChannel
    {
        public string Key { get; set; }          // url segment, e.g. "blitz"
        public string Name { get; set; }         // "Blitz"
        public string Featured { get; set; }     // current player headline
        public string Glyph { get; set; }
    }

    /// <summary>A live streamer (/api/streamer/live).</summary>
    public class LiveStreamer
    {
        public string Name { get; set; }
        public string Status { get; set; }
        public string Url { get; set; }
    }

    /// <summary>A broadcast / relay tournament (/api/broadcast).</summary>
    public class BroadcastItem
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Url { get; set; }
    }

    /// <summary>A simultaneous exhibition (/api/simul).</summary>
    public class SimulItem
    {
        public string Name { get; set; }
        public string Info { get; set; }     // "by DrNykterstein · 24 players · in progress"
    }

    // ===================== Tournaments =====================

    public class TournamentItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Info { get; set; }     // "Blitz · 3+2 · 142 players"
        public string Group { get; set; }    // "In progress" / "Upcoming" / "Finished"
    }

    public class TournamentPlayer
    {
        public string RankText { get; set; }
        public string Name { get; set; }
        public int Score { get; set; }
        public int Rating { get; set; }
        public string ScoreText => Score + " pts";
        public string RatingText => Rating > 0 ? Rating.ToString() : "";
    }

    // ===================== Studies =====================

    public class StudyItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Owner { get; set; }
    }

    /// <summary>Lichess TV / broadcast featured game snapshot.</summary>
    public class TvGameSnapshot
    {
        public string Fen;
        public GamePlayer White = new GamePlayer();
        public GamePlayer Black = new GamePlayer();
        public string LastMoveUci;
        public bool Orientation = true; // true = white at bottom
    }

    /// <summary>One of the signed-in user's in-progress games (/api/account/playing).
    /// Bound members are properties (UWP {Binding} ignores fields).</summary>
    public class OngoingGame
    {
        public string GameId { get; set; }
        public string OpponentName { get; set; }
        public bool IsMyTurn { get; set; }
        public string Fen { get; set; }
        public bool WhiteAtBottom { get; set; }   // true = the user plays white
        public string TypeText { get; set; }       // e.g. "Rapid", "Correspondence"
        public string TurnText { get; set; }       // "Your move" / "Their move"
    }
}
