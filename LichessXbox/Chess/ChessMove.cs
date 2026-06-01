using System;

namespace LichessXbox.Chess
{
    /// <summary>
    /// A single move expressed with 0..63 square indices (a1 = 0, h8 = 63).
    /// Promotion is the lower-case piece letter ('q','r','b','n') or '\0'.
    /// </summary>
    public struct ChessMove : IEquatable<ChessMove>
    {
        public int From;
        public int To;
        public char Promotion;

        public ChessMove(int from, int to, char promotion = '\0')
        {
            From = from;
            To = to;
            Promotion = char.ToLowerInvariant(promotion);
        }

        public static int Square(int file, int rank) => rank * 8 + file;
        public static int FileOf(int sq) => sq % 8;
        public static int RankOf(int sq) => sq / 8;

        public static string SquareName(int sq) =>
            ((char)('a' + FileOf(sq))).ToString() + (RankOf(sq) + 1);

        /// <summary>UCI long algebraic, e.g. "e2e4" or "e7e8q".</summary>
        public string ToUci()
        {
            var s = SquareName(From) + SquareName(To);
            return Promotion == '\0' ? s : s + Promotion;
        }

        public static ChessMove FromUci(string uci)
        {
            if (string.IsNullOrEmpty(uci) || uci.Length < 4)
                return new ChessMove(-1, -1);
            int from = ParseSquare(uci.Substring(0, 2));
            int to = ParseSquare(uci.Substring(2, 2));
            char promo = uci.Length >= 5 ? uci[4] : '\0';
            return new ChessMove(from, to, promo);
        }

        public static int ParseSquare(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return -1;
            int file = s[0] - 'a';
            int rank = s[1] - '1';
            if (file < 0 || file > 7 || rank < 0 || rank > 7) return -1;
            return Square(file, rank);
        }

        public bool Equals(ChessMove other) =>
            From == other.From && To == other.To && Promotion == other.Promotion;

        public override bool Equals(object obj) => obj is ChessMove m && Equals(m);
        public override int GetHashCode() => (From << 12) ^ (To << 4) ^ Promotion;
        public override string ToString() => ToUci();
    }
}
