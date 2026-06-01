using System;
using System.Collections.Generic;
using System.Text;

namespace LichessXbox.Chess
{
    /// <summary>
    /// A complete, self-contained chess position with legal move generation.
    /// Board is a char[64] with a1 = index 0, h8 = index 63.
    /// Uppercase = white, lowercase = black, '.' = empty.
    ///
    /// This is intentionally a small, readable rules engine — enough to validate
    /// moves, highlight legal targets, detect check / mate / stalemate, and apply
    /// the UCI moves that the Lichess Board API streams back to us. It is not a
    /// search engine.
    /// </summary>
    public sealed class ChessPosition
    {
        public char[] Squares = new char[64];
        public bool WhiteToMove = true;
        public bool CastleWK, CastleWQ, CastleBK, CastleBQ;
        public int EnPassant = -1;     // target square behind a pawn that just advanced two, else -1
        public int HalfmoveClock = 0;
        public int FullmoveNumber = 1;

        public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        public static ChessPosition Starting() => FromFen(StartFen);

        public ChessPosition Clone()
        {
            var c = new ChessPosition();
            Array.Copy(Squares, c.Squares, 64);
            c.WhiteToMove = WhiteToMove;
            c.CastleWK = CastleWK; c.CastleWQ = CastleWQ;
            c.CastleBK = CastleBK; c.CastleBQ = CastleBQ;
            c.EnPassant = EnPassant;
            c.HalfmoveClock = HalfmoveClock;
            c.FullmoveNumber = FullmoveNumber;
            return c;
        }

        // ----------------------------------------------------------------- FEN

        public static ChessPosition FromFen(string fen)
        {
            var p = new ChessPosition();
            for (int i = 0; i < 64; i++) p.Squares[i] = '.';
            if (string.IsNullOrWhiteSpace(fen)) fen = StartFen;

            var parts = fen.Trim().Split(' ');
            // Board (FEN lists rank 8 first)
            int rank = 7, file = 0;
            foreach (char ch in parts[0])
            {
                if (ch == '/') { rank--; file = 0; }
                else if (char.IsDigit(ch)) file += ch - '0';
                else { p.Squares[rank * 8 + file] = ch; file++; }
            }

            p.WhiteToMove = parts.Length < 2 || parts[1] != "b";

            string castle = parts.Length > 2 ? parts[2] : "KQkq";
            p.CastleWK = castle.Contains("K");
            p.CastleWQ = castle.Contains("Q");
            p.CastleBK = castle.Contains("k");
            p.CastleBQ = castle.Contains("q");

            p.EnPassant = (parts.Length > 3 && parts[3] != "-") ? ChessMove.ParseSquare(parts[3]) : -1;
            if (parts.Length > 4 && int.TryParse(parts[4], out int hm)) p.HalfmoveClock = hm;
            if (parts.Length > 5 && int.TryParse(parts[5], out int fm)) p.FullmoveNumber = fm;
            return p;
        }

        public string ToFen()
        {
            var sb = new StringBuilder();
            for (int rank = 7; rank >= 0; rank--)
            {
                int empty = 0;
                for (int file = 0; file < 8; file++)
                {
                    char c = Squares[rank * 8 + file];
                    if (c == '.') empty++;
                    else { if (empty > 0) { sb.Append(empty); empty = 0; } sb.Append(c); }
                }
                if (empty > 0) sb.Append(empty);
                if (rank > 0) sb.Append('/');
            }
            sb.Append(WhiteToMove ? " w " : " b ");
            string castle = (CastleWK ? "K" : "") + (CastleWQ ? "Q" : "") + (CastleBK ? "k" : "") + (CastleBQ ? "q" : "");
            sb.Append(castle.Length == 0 ? "-" : castle);
            sb.Append(' ');
            sb.Append(EnPassant >= 0 ? ChessMove.SquareName(EnPassant) : "-");
            sb.Append(' ').Append(HalfmoveClock).Append(' ').Append(FullmoveNumber);
            return sb.ToString();
        }

        // ------------------------------------------------------------- helpers

        static bool IsWhite(char p) => p != '.' && char.IsUpper(p);
        static bool IsBlack(char p) => p != '.' && char.IsLower(p);
        bool IsOwn(char p) => WhiteToMove ? IsWhite(p) : IsBlack(p);
        bool IsEnemy(char p) => WhiteToMove ? IsBlack(p) : IsWhite(p);

        public int FindKing(bool white)
        {
            char k = white ? 'K' : 'k';
            for (int i = 0; i < 64; i++) if (Squares[i] == k) return i;
            return -1;
        }

        public bool IsInCheck(bool white)
        {
            int king = FindKing(white);
            return king >= 0 && IsSquareAttacked(king, !white);
        }

        /// <summary>Is <paramref name="sq"/> attacked by a piece of the given colour?</summary>
        public bool IsSquareAttacked(int sq, bool byWhite)
        {
            int f = ChessMove.FileOf(sq), r = ChessMove.RankOf(sq);

            // Pawns
            int pawnDir = byWhite ? -1 : 1; // attackers sit one rank toward their own side
            char pawn = byWhite ? 'P' : 'p';
            foreach (int df in new[] { -1, 1 })
            {
                int af = f + df, ar = r + pawnDir;
                if (InBounds(af, ar) && Squares[ar * 8 + af] == pawn) return true;
            }

            // Knights
            char knight = byWhite ? 'N' : 'n';
            foreach (var (df, dr) in KnightDeltas)
                if (InBounds(f + df, r + dr) && Squares[(r + dr) * 8 + (f + df)] == knight) return true;

            // King adjacency
            char king = byWhite ? 'K' : 'k';
            for (int df = -1; df <= 1; df++)
                for (int dr = -1; dr <= 1; dr++)
                {
                    if (df == 0 && dr == 0) continue;
                    if (InBounds(f + df, r + dr) && Squares[(r + dr) * 8 + (f + df)] == king) return true;
                }

            // Sliding: rook/queen orthogonal
            char rook = byWhite ? 'R' : 'r', queen = byWhite ? 'Q' : 'q', bishop = byWhite ? 'B' : 'b';
            if (SlideHits(f, r, Orthogonal, rook, queen)) return true;
            if (SlideHits(f, r, Diagonal, bishop, queen)) return true;
            return false;
        }

        bool SlideHits(int f, int r, (int, int)[] dirs, char a, char b)
        {
            foreach (var (df, dr) in dirs)
            {
                int cf = f + df, cr = r + dr;
                while (InBounds(cf, cr))
                {
                    char c = Squares[cr * 8 + cf];
                    if (c != '.')
                    {
                        if (c == a || c == b) return true;
                        break;
                    }
                    cf += df; cr += dr;
                }
            }
            return false;
        }

        static bool InBounds(int f, int r) => f >= 0 && f < 8 && r >= 0 && r < 8;

        static readonly (int, int)[] KnightDeltas =
            { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) };
        static readonly (int, int)[] Orthogonal = { (1, 0), (-1, 0), (0, 1), (0, -1) };
        static readonly (int, int)[] Diagonal = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
        static readonly (int, int)[] AllEight =
            { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };

        // --------------------------------------------------- move generation

        public List<ChessMove> GenerateLegalMoves()
        {
            var pseudo = GeneratePseudoLegal();
            var legal = new List<ChessMove>(pseudo.Count);
            bool side = WhiteToMove;
            foreach (var m in pseudo)
            {
                var next = ApplyMoveInternal(m);
                if (!next.IsInCheck(side)) legal.Add(m);
            }
            return legal;
        }

        public List<ChessMove> LegalMovesFrom(int from)
        {
            var list = new List<ChessMove>();
            foreach (var m in GenerateLegalMoves()) if (m.From == from) list.Add(m);
            return list;
        }

        /// <summary>
        /// Pseudo-legal moves from a square (not filtered for leaving the king in
        /// check). Used as the input hint set in variants where check rules differ
        /// (Atomic, Antichess, Racing Kings…) and the server is the arbiter.
        /// </summary>
        public List<ChessMove> PseudoLegalMovesFrom(int from)
        {
            var list = new List<ChessMove>();
            foreach (var m in GeneratePseudoLegal()) if (m.From == from) list.Add(m);
            return list;
        }

        List<ChessMove> GeneratePseudoLegal()
        {
            var moves = new List<ChessMove>(48);
            for (int sq = 0; sq < 64; sq++)
            {
                char piece = Squares[sq];
                if (piece == '.' || !IsOwn(piece)) continue;
                char up = char.ToUpperInvariant(piece);
                int f = ChessMove.FileOf(sq), r = ChessMove.RankOf(sq);
                switch (up)
                {
                    case 'P': PawnMoves(sq, f, r, moves); break;
                    case 'N': StepMoves(sq, f, r, KnightDeltas, moves); break;
                    case 'B': SlideMoves(sq, f, r, Diagonal, moves); break;
                    case 'R': SlideMoves(sq, f, r, Orthogonal, moves); break;
                    case 'Q': SlideMoves(sq, f, r, AllEight, moves); break;
                    case 'K': StepMoves(sq, f, r, AllEight, moves); CastleMoves(sq, moves); break;
                }
            }
            return moves;
        }

        void StepMoves(int from, int f, int r, (int, int)[] deltas, List<ChessMove> moves)
        {
            foreach (var (df, dr) in deltas)
            {
                int nf = f + df, nr = r + dr;
                if (!InBounds(nf, nr)) continue;
                int to = nr * 8 + nf;
                char t = Squares[to];
                if (t == '.' || IsEnemy(t)) moves.Add(new ChessMove(from, to));
            }
        }

        void SlideMoves(int from, int f, int r, (int, int)[] dirs, List<ChessMove> moves)
        {
            foreach (var (df, dr) in dirs)
            {
                int nf = f + df, nr = r + dr;
                while (InBounds(nf, nr))
                {
                    int to = nr * 8 + nf;
                    char t = Squares[to];
                    if (t == '.') moves.Add(new ChessMove(from, to));
                    else { if (IsEnemy(t)) moves.Add(new ChessMove(from, to)); break; }
                    nf += df; nr += dr;
                }
            }
        }

        void PawnMoves(int from, int f, int r, List<ChessMove> moves)
        {
            int dir = WhiteToMove ? 1 : -1;
            int startRank = WhiteToMove ? 1 : 6;
            int promoRank = WhiteToMove ? 7 : 0;

            // Single / double push
            int oneR = r + dir;
            if (InBounds(f, oneR) && Squares[oneR * 8 + f] == '.')
            {
                AddPawnMove(from, oneR * 8 + f, oneR == promoRank, moves);
                int twoR = r + 2 * dir;
                if (r == startRank && Squares[twoR * 8 + f] == '.')
                    moves.Add(new ChessMove(from, twoR * 8 + f));
            }
            // Captures + en passant
            foreach (int df in new[] { -1, 1 })
            {
                int nf = f + df, nr = r + dir;
                if (!InBounds(nf, nr)) continue;
                int to = nr * 8 + nf;
                char t = Squares[to];
                if (t != '.' && IsEnemy(t)) AddPawnMove(from, to, nr == promoRank, moves);
                else if (to == EnPassant) moves.Add(new ChessMove(from, to));
            }
        }

        static void AddPawnMove(int from, int to, bool promo, List<ChessMove> moves)
        {
            if (promo)
            {
                moves.Add(new ChessMove(from, to, 'q'));
                moves.Add(new ChessMove(from, to, 'r'));
                moves.Add(new ChessMove(from, to, 'b'));
                moves.Add(new ChessMove(from, to, 'n'));
            }
            else moves.Add(new ChessMove(from, to));
        }

        void CastleMoves(int from, List<ChessMove> moves)
        {
            if (WhiteToMove && from == 4)
            {
                if (CastleWK && Squares[5] == '.' && Squares[6] == '.' &&
                    !IsSquareAttacked(4, false) && !IsSquareAttacked(5, false) && !IsSquareAttacked(6, false))
                    moves.Add(new ChessMove(4, 6));
                if (CastleWQ && Squares[3] == '.' && Squares[2] == '.' && Squares[1] == '.' &&
                    !IsSquareAttacked(4, false) && !IsSquareAttacked(3, false) && !IsSquareAttacked(2, false))
                    moves.Add(new ChessMove(4, 2));
            }
            else if (!WhiteToMove && from == 60)
            {
                if (CastleBK && Squares[61] == '.' && Squares[62] == '.' &&
                    !IsSquareAttacked(60, true) && !IsSquareAttacked(61, true) && !IsSquareAttacked(62, true))
                    moves.Add(new ChessMove(60, 62));
                if (CastleBQ && Squares[59] == '.' && Squares[58] == '.' && Squares[57] == '.' &&
                    !IsSquareAttacked(60, true) && !IsSquareAttacked(59, true) && !IsSquareAttacked(58, true))
                    moves.Add(new ChessMove(60, 58));
            }
        }

        // --------------------------------------------------------- apply move

        /// <summary>Applies a move without legality filtering; returns the new position.</summary>
        ChessPosition ApplyMoveInternal(ChessMove m)
        {
            var n = Clone();
            char piece = n.Squares[m.From];
            char up = char.ToUpperInvariant(piece);
            bool white = IsWhite(piece);
            int newEp = -1;

            bool isPawn = up == 'P';
            bool isCapture = n.Squares[m.To] != '.';

            // En passant capture removes the pawn behind the target square
            if (isPawn && m.To == EnPassant && EnPassant >= 0)
            {
                int capR = ChessMove.RankOf(m.To) + (white ? -1 : 1);
                n.Squares[capR * 8 + ChessMove.FileOf(m.To)] = '.';
                isCapture = true;
            }

            // Move the piece
            n.Squares[m.To] = piece;
            n.Squares[m.From] = '.';

            // Promotion
            if (isPawn && m.Promotion != '\0')
                n.Squares[m.To] = white ? char.ToUpperInvariant(m.Promotion) : char.ToLowerInvariant(m.Promotion);

            // Castling: move the rook too
            if (up == 'K' && Math.Abs(ChessMove.FileOf(m.To) - ChessMove.FileOf(m.From)) == 2)
            {
                if (m.To == 6) { n.Squares[5] = n.Squares[7]; n.Squares[7] = '.'; }
                else if (m.To == 2) { n.Squares[3] = n.Squares[0]; n.Squares[0] = '.'; }
                else if (m.To == 62) { n.Squares[61] = n.Squares[63]; n.Squares[63] = '.'; }
                else if (m.To == 58) { n.Squares[59] = n.Squares[56]; n.Squares[56] = '.'; }
            }

            // Double pawn push sets en-passant target
            if (isPawn && Math.Abs(ChessMove.RankOf(m.To) - ChessMove.RankOf(m.From)) == 2)
                newEp = (ChessMove.RankOf(m.From) + (white ? 1 : -1)) * 8 + ChessMove.FileOf(m.From);

            // Update castling rights
            if (up == 'K') { if (white) { n.CastleWK = n.CastleWQ = false; } else { n.CastleBK = n.CastleBQ = false; } }
            if (m.From == 0 || m.To == 0) n.CastleWQ = false;
            if (m.From == 7 || m.To == 7) n.CastleWK = false;
            if (m.From == 56 || m.To == 56) n.CastleBQ = false;
            if (m.From == 63 || m.To == 63) n.CastleBK = false;

            n.EnPassant = newEp;
            n.HalfmoveClock = (isPawn || isCapture) ? 0 : HalfmoveClock + 1;
            if (!white) n.FullmoveNumber = FullmoveNumber + 1;
            n.WhiteToMove = !WhiteToMove;
            return n;
        }

        /// <summary>Apply a legal move and return the resulting position (null if illegal).</summary>
        public ChessPosition Apply(ChessMove m)
        {
            foreach (var legal in GenerateLegalMoves())
                if (legal.Equals(m)) return ApplyMoveInternal(m);
            // Allow promotion shorthand where caller omitted the piece (defaults to queen)
            if (m.Promotion == '\0')
            {
                var q = new ChessMove(m.From, m.To, 'q');
                foreach (var legal in GenerateLegalMoves())
                    if (legal.Equals(q)) return ApplyMoveInternal(q);
            }
            return null;
        }

        public ChessPosition ApplyUci(string uci) => Apply(ChessMove.FromUci(uci));

        /// <summary>
        /// Tolerant apply for replaying server move lists in any variant. Handles
        /// Crazyhouse drops ("Q@e4") and falls back to a pseudo-legal apply when a
        /// move isn't legal under standard rules (Atomic/Antichess/etc.). Never
        /// throws; returns the current position unchanged if it can't interpret.
        /// </summary>
        public ChessPosition ApplyUciLoose(string uci)
        {
            if (string.IsNullOrWhiteSpace(uci)) return this;

            // Crazyhouse drop, e.g. "P@e4" / "Q@h7".
            int at = uci.IndexOf('@');
            if (at == 1)
            {
                int sq = ChessMove.ParseSquare(uci.Substring(at + 1, 2));
                if (sq < 0) return this;
                var n = Clone();
                char p = uci[0];
                n.Squares[sq] = WhiteToMove ? char.ToUpperInvariant(p) : char.ToLowerInvariant(p);
                n.EnPassant = -1;
                if (!WhiteToMove) n.FullmoveNumber = FullmoveNumber + 1;
                n.WhiteToMove = !WhiteToMove;
                return n;
            }

            var m = ChessMove.FromUci(uci);
            if (m.From < 0 || Squares[m.From] == '.') return this;
            var legal = Apply(m);
            if (legal != null) return legal;
            // Not legal under standard rules — apply the raw piece move anyway.
            return ApplyMoveInternal(m);
        }

        // --------------------------------------------------------- outcomes

        public bool HasLegalMoves() => GenerateLegalMoves().Count > 0;
        public bool IsCheckmate() => IsInCheck(WhiteToMove) && !HasLegalMoves();
        public bool IsStalemate() => !IsInCheck(WhiteToMove) && !HasLegalMoves();

        public char PieceAt(int sq) => Squares[sq];

        // ----------------------------------------------------------- SAN

        /// <summary>
        /// Resolve a SAN move (e.g. "Nf3", "exd5", "O-O", "e8=Q+") against the
        /// current position by matching it to a generated legal move. Returns null
        /// if it can't be matched. This is a matcher, not a full SAN parser — it is
        /// only used to replay the puzzle PGN that Lichess gives us.
        /// </summary>
        public ChessMove? ParseSan(string san)
        {
            if (string.IsNullOrWhiteSpace(san)) return null;
            string s = san.Trim().TrimEnd('+', '#', '!', '?');

            var legal = GenerateLegalMoves();

            // Castling
            if (s == "O-O" || s == "0-0")
            {
                int from = WhiteToMove ? 4 : 60, to = WhiteToMove ? 6 : 62;
                return Match(legal, from, to, '\0');
            }
            if (s == "O-O-O" || s == "0-0-0")
            {
                int from = WhiteToMove ? 4 : 60, to = WhiteToMove ? 2 : 58;
                return Match(legal, from, to, '\0');
            }

            char promo = '\0';
            int eq = s.IndexOf('=');
            if (eq >= 0 && eq + 1 < s.Length) { promo = char.ToLowerInvariant(s[eq + 1]); s = s.Substring(0, eq); }
            else if (s.Length >= 2 && "QRBNqrbn".IndexOf(s[s.Length - 1]) >= 0 && char.IsDigit(s[s.Length - 2]) == false)
            {
                // (no trailing promotion without '='; ignore)
            }

            s = s.Replace("x", "");

            char pieceType = 'P';
            int idx = 0;
            if (s.Length > 0 && "KQRBN".IndexOf(s[0]) >= 0) { pieceType = s[0]; idx = 1; }

            // Destination is the last two characters.
            if (s.Length < 2) return null;
            int to2 = ChessMove.ParseSquare(s.Substring(s.Length - 2));
            if (to2 < 0) return null;

            // Anything between the piece letter and the destination is disambiguation.
            string disamb = s.Substring(idx, s.Length - 2 - idx);
            int dFile = -1, dRank = -1;
            foreach (char c in disamb)
            {
                if (c >= 'a' && c <= 'h') dFile = c - 'a';
                else if (c >= '1' && c <= '8') dRank = c - '1';
            }

            foreach (var m in legal)
            {
                if (m.To != to2) continue;
                char p = char.ToUpperInvariant(Squares[m.From]);
                if (p != pieceType) continue;
                if (dFile >= 0 && ChessMove.FileOf(m.From) != dFile) continue;
                if (dRank >= 0 && ChessMove.RankOf(m.From) != dRank) continue;
                if (promo != '\0' && m.Promotion != promo) continue;
                if (promo == '\0' && m.Promotion != '\0' && m.Promotion != 'q') continue;
                return m;
            }
            return null;
        }

        /// <summary>
        /// Parse a single PGN game/chapter into a starting FEN ("startpos" if none)
        /// and a space-separated UCI move list, for replay on the analysis board.
        /// Strips tags, comments, variations, NAGs, move numbers and results.
        /// </summary>
        public static (string fen, string uci) PgnToMoves(string pgn)
        {
            if (string.IsNullOrWhiteSpace(pgn)) return ("startpos", "");

            // Pull an explicit start FEN if the chapter has one.
            string fen = "startpos";
            foreach (var line in pgn.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("[FEN", StringComparison.OrdinalIgnoreCase))
                {
                    int q1 = l.IndexOf('"'), q2 = l.LastIndexOf('"');
                    if (q1 >= 0 && q2 > q1) fen = l.Substring(q1 + 1, q2 - q1 - 1);
                }
            }

            // Strip tag lines.
            var sb = new StringBuilder();
            foreach (var line in pgn.Split('\n'))
                if (!line.TrimStart().StartsWith("[")) sb.Append(line).Append(' ');
            string body = sb.ToString();

            // Remove comments {...} and nested variations (...).
            body = StripDelimited(body, '{', '}');
            body = StripDelimited(body, '(', ')');

            var pos = fen == "startpos" ? Starting() : FromFen(fen);
            var uci = new StringBuilder();
            foreach (var tokRaw in body.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string tok = tokRaw.Trim();
                if (tok.Length == 0 || tok[0] == '$') continue;
                if (tok.Contains(".")) tok = tok.Substring(tok.LastIndexOf('.') + 1);
                if (tok.Length == 0) continue;
                if (tok == "1-0" || tok == "0-1" || tok == "1/2-1/2" || tok == "*") continue;
                var mv = pos.ParseSan(tok);
                if (mv == null) continue;
                var next = pos.Apply(mv.Value);
                if (next == null) break;
                uci.Append(mv.Value.ToUci()).Append(' ');
                pos = next;
            }
            return (fen, uci.ToString().Trim());
        }

        static string StripDelimited(string s, char open, char close)
        {
            var sb = new StringBuilder();
            int depth = 0;
            foreach (char c in s)
            {
                if (c == open) depth++;
                else if (c == close) { if (depth > 0) depth--; }
                else if (depth == 0) sb.Append(c);
            }
            return sb.ToString();
        }

        static ChessMove? Match(List<ChessMove> legal, int from, int to, char promo)
        {
            foreach (var m in legal)
                if (m.From == from && m.To == to && m.Promotion == promo) return m;
            return null;
        }

        /// <summary>Render a legal move in Standard Algebraic Notation (e.g. "Nbd7", "exd6", "O-O", "e8=Q#").</summary>
        public string ToSan(ChessMove m)
        {
            char piece = Squares[m.From];
            if (piece == '.') return m.ToUci();
            char up = char.ToUpperInvariant(piece);

            string san;
            if (up == 'K' && Math.Abs(ChessMove.FileOf(m.To) - ChessMove.FileOf(m.From)) == 2)
            {
                san = m.To > m.From ? "O-O" : "O-O-O";
            }
            else
            {
                bool isPawn = up == 'P';
                bool capture = Squares[m.To] != '.' || (isPawn && m.To == EnPassant);
                var sb = new StringBuilder();

                if (isPawn)
                {
                    if (capture) sb.Append((char)('a' + ChessMove.FileOf(m.From)));
                }
                else
                {
                    sb.Append(up);
                    // Disambiguate against other same-type pieces that can also reach m.To.
                    bool ambFile = false, ambRank = false, any = false;
                    foreach (var other in GenerateLegalMoves())
                    {
                        if (other.From == m.From || other.To != m.To) continue;
                        if (char.ToUpperInvariant(Squares[other.From]) != up) continue;
                        any = true;
                        if (ChessMove.FileOf(other.From) == ChessMove.FileOf(m.From)) ambFile = true;
                        if (ChessMove.RankOf(other.From) == ChessMove.RankOf(m.From)) ambRank = true;
                    }
                    if (any)
                    {
                        if (!ambFile) sb.Append((char)('a' + ChessMove.FileOf(m.From)));
                        else if (!ambRank) sb.Append((char)('1' + ChessMove.RankOf(m.From)));
                        else { sb.Append((char)('a' + ChessMove.FileOf(m.From))); sb.Append((char)('1' + ChessMove.RankOf(m.From))); }
                    }
                }

                if (capture) sb.Append('x');
                sb.Append(ChessMove.SquareName(m.To));
                if (m.Promotion != '\0') sb.Append('=').Append(char.ToUpperInvariant(m.Promotion));
                san = sb.ToString();
            }

            var after = ApplyMoveInternal(m);
            if (after.IsCheckmate()) san += "#";
            else if (after.IsInCheck(after.WhiteToMove)) san += "+";
            return san;
        }

        /// <summary>Convert a UCI line (e.g. cloud-eval PV) into a SAN string from this position.</summary>
        public string LineToSan(IEnumerable<string> uciMoves, int max = 6)
        {
            var sb = new StringBuilder();
            var pos = this;
            int shown = 0;
            foreach (var uci in uciMoves)
            {
                if (shown >= max) break;
                var mv = ChessMove.FromUci(uci);
                // Match against legal moves so promotion/exactness is right.
                ChessMove? legal = null;
                foreach (var lm in pos.GenerateLegalMoves())
                    if (lm.From == mv.From && lm.To == mv.To && (mv.Promotion == '\0' || lm.Promotion == mv.Promotion)) { legal = lm; break; }
                if (legal == null) break;
                if (pos.WhiteToMove) sb.Append(pos.FullmoveNumber).Append(". ");
                else if (shown == 0) sb.Append(pos.FullmoveNumber).Append("... ");
                sb.Append(pos.ToSan(legal.Value)).Append(' ');
                pos = pos.ApplyMoveInternal(legal.Value);
                shown++;
            }
            return sb.ToString().Trim();
        }
    }
}
