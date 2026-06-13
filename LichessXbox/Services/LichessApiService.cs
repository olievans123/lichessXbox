using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using LichessXbox.Models;
using Newtonsoft.Json.Linq;

namespace LichessXbox.Services
{
    /// <summary>
    /// Thin async wrapper over the Lichess HTTP + NDJSON streaming API.
    /// All authenticated calls use the bearer token from <see cref="LichessAuthService"/>.
    /// </summary>
    public sealed class LichessApiService
    {
        const string Base = "https://lichess.org";
        readonly HttpClient _http;
        readonly LichessAuthService _auth;

        public LichessApiService(LichessAuthService auth)
        {
            _auth = auth;
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("LichessXbox/1.0");
        }

        HttpRequestMessage Build(HttpMethod method, string path, HttpContent content = null)
        {
            var req = new HttpRequestMessage(method, Base + path);
            if (_auth.IsAuthenticated)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
            req.Headers.Accept.ParseAdd("application/json");
            if (content != null) req.Content = content;
            return req;
        }

        // The shared HttpClient uses an infinite timeout so the long-lived NDJSON streams aren't
        // cut off. Plain request/response calls must therefore impose their OWN deadline, or a
        // stalled socket (e.g. a move POST mid-game) hangs forever with no error. The default
        // completion option buffers the whole response before SendAsync returns, so the body is
        // already in memory by the time we dispose the token source — safe for these short calls.
        async Task<HttpResponseMessage> SendBufferedAsync(HttpRequestMessage req, CancellationToken ct = default, int timeoutSeconds = 15)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
                return await _http.SendAsync(req, linked.Token);
        }

        // ----------------------------------------------------------- account

        public async Task<LichessAccount> GetAccountAsync()
        {
            using (var req = Build(HttpMethod.Get, "/api/account"))
            using (var resp = await SendBufferedAsync(req))
            {
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync();
                var o = JObject.Parse(json);
                var perfs = o["perfs"];
                return new LichessAccount
                {
                    Id = o.Value<string>("id"),
                    Username = o.Value<string>("username"),
                    Title = o.Value<string>("title"),
                    Patron = o.Value<bool?>("patron") ?? false,
                    BulletRating = perfs?["bullet"]?.Value<int?>("rating"),
                    BlitzRating = perfs?["blitz"]?.Value<int?>("rating"),
                    RapidRating = perfs?["rapid"]?.Value<int?>("rating"),
                    ClassicalRating = perfs?["classical"]?.Value<int?>("rating"),
                    PlayTimeSeconds = o["playTime"]?.Value<long?>("total"),
                };
            }
        }

        /// <summary>The signed-in user's in-progress games (/api/account/playing).</summary>
        public async Task<List<OngoingGame>> GetOngoingGamesAsync()
        {
            var list = new List<OngoingGame>();
            using (var req = Build(HttpMethod.Get, "/api/account/playing"))
            using (var resp = await SendBufferedAsync(req))
            {
                if (!resp.IsSuccessStatusCode) return list;
                var o = JObject.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var g in o["nowPlaying"] as JArray ?? new JArray())
                {
                    // Board/move endpoints take the short 8-char gameId, not the 12-char fullId.
                    string id = g.Value<string>("gameId");
                    if (string.IsNullOrEmpty(id))
                    {
                        var fid = g.Value<string>("fullId");
                        id = (fid != null && fid.Length >= 8) ? fid.Substring(0, 8) : fid;
                    }
                    if (string.IsNullOrEmpty(id)) continue;
                    bool myTurn = g.Value<bool?>("isMyTurn") ?? false;
                    string speed = g.Value<string>("speed") ?? "";
                    if (speed.Length > 0) speed = char.ToUpperInvariant(speed[0]) + speed.Substring(1);
                    list.Add(new OngoingGame
                    {
                        GameId = id,
                        OpponentName = g["opponent"]?.Value<string>("username") ?? "Opponent",
                        IsMyTurn = myTurn,
                        Fen = g.Value<string>("fen"),
                        WhiteAtBottom = (g.Value<string>("color") ?? "white") == "white",
                        TypeText = speed.Length > 0 ? speed : "Game",
                        TurnText = myTurn ? "Your move" : "Their move",
                    });
                }
            }
            return list;
        }

        // --------------------------------------------------- generic NDJSON

        /// <summary>
        /// Opens an NDJSON stream and invokes <paramref name="onObject"/> for every
        /// JSON line until the server closes it or the token is cancelled. Blank
        /// keep-alive lines are ignored.
        /// </summary>
        public async Task StreamNdjsonAsync(string path, Action<JObject> onObject, CancellationToken ct)
        {
            using (var req = Build(HttpMethod.Get, path))
            using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if ((int)resp.StatusCode == 429)
                {
                    // Rate-limited: honor Lichess's "wait a full minute" guidance instead of letting
                    // the consumer's reconnect loop re-open this stream every ~1s and escalate the ban.
                    // Wait here (Retry-After, floored at 60s), then return so the loop reconnects late.
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                    if (wait < TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
                    await Task.Delay(wait, ct);
                    return;
                }
                resp.EnsureSuccessStatusCode();
                using (var stream = await resp.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream))
                {
                    // NOTE: never touch reader.EndOfStream here — its getter does a *synchronous*
                    // blocking read, which freezes the UI thread on an idle long-lived stream.
                    // ReadLineAsync() is genuinely async and returns null at end of stream.
                    string line;
                    while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                    {
                        // The loop condition checks the token BEFORE the await, so a line that
                        // completed during cancellation would otherwise still be delivered —
                        // re-arming page machinery that was just torn down.
                        if (ct.IsCancellationRequested) return;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        JObject obj = null;
                        try { obj = JObject.Parse(line); }
                        catch { continue; }
                        onObject(obj);
                    }
                }
            }
        }

        // ----------------------------------------------------- board / play

        /// <summary>Stream of incoming account events (gameStart, gameFinish, challenges).</summary>
        public Task StreamEventsAsync(Action<JObject> onEvent, CancellationToken ct) =>
            StreamNdjsonAsync("/api/stream/event", onEvent, ct);

        /// <summary>Full state stream for one board game.</summary>
        public Task StreamBoardGameAsync(string gameId, Action<JObject> onState, CancellationToken ct) =>
            StreamNdjsonAsync($"/api/board/game/stream/{gameId}", onState, ct);

        public async Task<bool> MakeBoardMoveAsync(string gameId, string uci)
        {
            using (var req = Build(HttpMethod.Post, $"/api/board/game/{gameId}/move/{uci}"))
            using (var resp = await SendBufferedAsync(req))
                return resp.IsSuccessStatusCode;
        }

        public async Task ResignAsync(string gameId)
        {
            using (var req = Build(HttpMethod.Post, $"/api/board/game/{gameId}/resign"))
            using (await SendBufferedAsync(req)) { }
        }

        public async Task AbortAsync(string gameId)
        {
            using (var req = Build(HttpMethod.Post, $"/api/board/game/{gameId}/abort"))
            using (await SendBufferedAsync(req)) { }
        }

        public async Task OfferDrawAsync(string gameId, bool accept)
        {
            using (var req = Build(HttpMethod.Post, $"/api/board/game/{gameId}/draw/{(accept ? "yes" : "no")}"))
            using (await SendBufferedAsync(req)) { }
        }

        public async Task TakebackAsync(string gameId, bool accept)
        {
            using (var req = Build(HttpMethod.Post, $"/api/board/game/{gameId}/takeback/{(accept ? "yes" : "no")}"))
            using (await SendBufferedAsync(req)) { }
        }

        public async Task ClaimVictoryAsync(string gameId)
        {
            using (var req = Build(HttpMethod.Post, $"/api/board/game/{gameId}/claim-victory"))
            using (await SendBufferedAsync(req)) { }
        }

        /// <summary>
        /// Open a public seek. The HTTP request intentionally stays open while
        /// Lichess matches us; cancel the token to withdraw. The actual game id
        /// arrives via <see cref="StreamEventsAsync"/> as a gameStart event.
        /// </summary>
        public async Task CreateSeekAsync(TimeControlPreset preset, CancellationToken ct, string variant = "standard")
        {
            var form = new Dictionary<string, string>
            {
                ["rated"] = preset.Rated ? "true" : "false",
                ["variant"] = variant,
            };
            if (preset.IsCorrespondence)
            {
                form["days"] = preset.Days.ToString();
            }
            else
            {
                form["time"] = (preset.ClockLimitSeconds / 60.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                form["increment"] = preset.ClockIncrementSeconds.ToString();
            }
            using (var content = new FormUrlEncodedContent(form))
            using (var req = Build(HttpMethod.Post, "/api/board/seek", content))
            {
                try
                {
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                    {
                        // Surface a refusal (e.g. an unsupported time control) instead of silently
                        // bouncing back to the lobby with no explanation.
                        if (!resp.IsSuccessStatusCode)
                        {
                            string body = "";
                            try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                            throw new InvalidOperationException(ExtractApiError(body, (int)resp.StatusCode));
                        }
                        using (var stream = await resp.Content.ReadAsStreamAsync())
                        using (var reader = new StreamReader(stream))
                        {
                            // Drain until matched or cancelled. Use async ReadLineAsync only —
                            // reader.EndOfStream blocks the UI thread synchronously.
                            while (!ct.IsCancellationRequested && await reader.ReadLineAsync() != null) { }
                        }
                    }
                }
                catch (OperationCanceledException) { /* withdrawn */ }
            }
        }

        // Pull lichess's {"error":"…"} message out of a failed response body.
        static string ExtractApiError(string body, int status)
        {
            try
            {
                var o = JObject.Parse(body);
                var msg = o.Value<string>("error");
                if (!string.IsNullOrWhiteSpace(msg)) return msg;
            }
            catch { }
            return $"Lichess wouldn't start that game (HTTP {status}).";
        }

        // --------------------------------------------------- computer & friends

        /// <summary>
        /// Challenge the Lichess Stockfish AI at the given level (1–8). The game is
        /// created immediately; we return its id so the caller can open it.
        /// A clock of null creates an unlimited (no-clock) game.
        /// </summary>
        public async Task<string> CreateAiChallengeAsync(int level, TimeControlPreset clock, string variant = "standard")
        {
            var form = new Dictionary<string, string> { ["level"] = level.ToString(), ["variant"] = variant };
            if (clock != null)
            {
                form["clock.limit"] = clock.ClockLimitSeconds.ToString();
                form["clock.increment"] = clock.ClockIncrementSeconds.ToString();
            }
            using (var content = new FormUrlEncodedContent(form))
            using (var req = Build(HttpMethod.Post, "/api/challenge/ai", content))
            using (var resp = await SendBufferedAsync(req))
            {
                if (!resp.IsSuccessStatusCode) return null;
                var o = JObject.Parse(await resp.Content.ReadAsStringAsync());
                return o.Value<string>("id");
            }
        }

        /// <summary>Challenge a specific user. The game starts (gameStart event) once they accept.</summary>
        public async Task<bool> ChallengeUserAsync(string username, TimeControlPreset clock, string variant = "standard")
        {
            var form = new Dictionary<string, string>
            {
                ["rated"] = clock != null && clock.Rated ? "true" : "false",
                ["variant"] = variant,
            };
            // Honour correspondence (days-per-turn) instead of silently forcing a real-time clock.
            if (clock != null && clock.IsCorrespondence)
            {
                form["days"] = clock.Days.ToString();
            }
            else
            {
                form["clock.limit"] = (clock?.ClockLimitSeconds ?? 300).ToString();
                form["clock.increment"] = (clock?.ClockIncrementSeconds ?? 3).ToString();
            }
            using (var content = new FormUrlEncodedContent(form))
            using (var req = Build(HttpMethod.Post, $"/api/challenge/{Uri.EscapeDataString(username)}", content))
            using (var resp = await SendBufferedAsync(req))
                return resp.IsSuccessStatusCode;
        }

        /// <summary>Accept an incoming challenge. False when it can't be accepted (expired/withdrawn).</summary>
        public async Task<bool> AcceptChallengeAsync(string id)
        {
            using (var req = Build(HttpMethod.Post, $"/api/challenge/{id}/accept"))
            using (var resp = await SendBufferedAsync(req))
                return resp.IsSuccessStatusCode;
        }

        public async Task DeclineChallengeAsync(string id)
        {
            using (var req = Build(HttpMethod.Post, $"/api/challenge/{id}/decline"))
            using (await SendBufferedAsync(req)) { }
        }

        /// <summary>Incoming challenges (the "in" list of /api/challenge) for the app-wide gutter tab.</summary>
        public async Task<System.Collections.Generic.List<IncomingChallenge>> GetIncomingChallengesAsync()
        {
            var list = new System.Collections.Generic.List<IncomingChallenge>();
            using (var req = Build(HttpMethod.Get, "/api/challenge"))
            using (var resp = await SendBufferedAsync(req))
            {
                if (!resp.IsSuccessStatusCode) return list;
                var o = JObject.Parse(await resp.Content.ReadAsStringAsync());
                foreach (var c in o["in"] as JArray ?? new JArray())
                {
                    string id = c.Value<string>("id");
                    if (string.IsNullOrEmpty(id)) continue;
                    string name = c["challenger"]?.Value<string>("name") ?? "Someone";
                    string speed = c.Value<string>("speed") ?? c["variant"]?.Value<string>("name") ?? "Challenge";
                    string tc = c["timeControl"]?.Value<string>("show");
                    bool rated = c.Value<bool?>("rated") ?? false;
                    string desc = char.ToUpperInvariant(speed[0]) + speed.Substring(1);
                    if (!string.IsNullOrEmpty(tc)) desc += " · " + tc;
                    desc += rated ? " · rated" : " · casual";
                    list.Add(new IncomingChallenge { Id = id, ChallengerName = name, Description = desc });
                }
            }
            return list;
        }

        // ------------------------------------------------------------ puzzles

        public Task<PuzzleInfo> GetDailyPuzzleAsync() => GetPuzzleAsync("/api/puzzle/daily");
        public Task<PuzzleInfo> GetNextPuzzleAsync() => GetPuzzleAsync("/api/puzzle/next");
        public Task<PuzzleInfo> GetThemedPuzzleAsync(string angle) =>
            GetPuzzleAsync("/api/puzzle/next?angle=" + Uri.EscapeDataString(angle));

        async Task<PuzzleInfo> GetPuzzleAsync(string path)
        {
            // Fetch puzzles ANONYMOUSLY (no auth header). Authenticated /api/puzzle/next can
            // return the same puzzle repeatedly in practice (it tracks per-user "seen" state,
            // and there is no public endpoint to record a solve), so "Next" would appear stuck.
            // Anonymous requests have no per-user history and reliably return a fresh random puzzle.
            using (var req = new HttpRequestMessage(HttpMethod.Get, Base + path))
            {
                req.Headers.Accept.ParseAdd("application/json");
                using (var resp = await SendBufferedAsync(req))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    var o = JObject.Parse(await resp.Content.ReadAsStringAsync());
                    var puzzle = o["puzzle"];
                    var game = o["game"];
                    if (puzzle == null) return null;

                    var info = new PuzzleInfo
                    {
                        Id = puzzle.Value<string>("id"),
                        Rating = puzzle.Value<int?>("rating") ?? 0,
                        InitialPly = puzzle.Value<int?>("initialPly") ?? 0,
                        Pgn = game?.Value<string>("pgn"),
                    };
                    foreach (var t in puzzle["themes"] ?? new JArray()) info.Themes.Add(t.ToString());
                    foreach (var s in puzzle["solution"] ?? new JArray()) info.Solution.Add(s.ToString());
                    return info;
                }
            }
        }

        // ---------------------------------------------------------- analysis

        async Task<string> GetAbsoluteAsync(string url, CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Accept.ParseAdd("application/json");
                if (_auth.IsAuthenticated && url.StartsWith(Base))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
                using (var resp = await SendBufferedAsync(req, ct))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsStringAsync();
                }
            }
        }

        /// <summary>
        /// Cloud engine evaluation for a FEN. Lichess cloud-eval scores are already from
        /// White's perspective (unlike raw UCI), so they're used as-is — re-orienting by the
        /// side to move flipped the sign on every black-to-move position. Null if not cached.
        /// </summary>
        public async Task<CloudEval> GetCloudEvalAsync(string fen)
        {
            string json = await GetAbsoluteAsync(Base + "/api/cloud-eval?fen=" + Uri.EscapeDataString(fen) + "&multiPv=1");
            if (json == null) return null;
            var o = JObject.Parse(json);
            var pv = (o["pvs"] as JArray)?.FirstOrDefault();
            if (pv == null) return null;

            int? cp = pv.Value<int?>("cp");
            int? mate = pv.Value<int?>("mate");
            string moves = pv.Value<string>("moves") ?? "";
            string bestUci = moves.Split(' ').FirstOrDefault();

            var eval = new CloudEval
            {
                Depth = o.Value<int?>("depth") ?? 0,
                BestMoveUci = bestUci,
                PvUci = moves,
            };

            if (mate.HasValue)
            {
                int m = mate.Value;
                eval.EvalText = (m >= 0 ? "#" : "#-") + Math.Abs(m);
                eval.WhiteAdvantage = m >= 0 ? 1 : -1;
            }
            else if (cp.HasValue)
            {
                double whiteCp = cp.Value;
                eval.EvalText = (whiteCp >= 0 ? "+" : "") + (whiteCp / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                eval.WhiteAdvantage = 2.0 / (1.0 + Math.Exp(-0.004 * whiteCp)) - 1.0;
            }
            return eval;
        }

        /// <summary>Opening explorer over Lichess games for a FEN.</summary>
        public async Task<ExplorerResult> GetExplorerAsync(string fen)
        {
            // The opening explorer moved to explorer.lichess.ORG and now requires an OAuth2 token
            // (the spec marks it `security: OAuth2`; anonymous requests get an nginx 401). We send
            // the signed-in user's bearer token. Own timeout (the shared client is infinite) and
            // THROW on failure so the caller can fall back to the offline book.
            string url = "https://explorer.lichess.org/masters?fen=" + Uri.EscapeDataString(fen) + "&moves=12&topGames=0";
            string json;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Accept.ParseAdd("application/json");
                if (_auth.IsAuthenticated)
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.Token);
                using (var resp = await _http.SendAsync(req, cts.Token))
                {
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException("explorer HTTP " + (int)resp.StatusCode);
                    json = await resp.Content.ReadAsStringAsync();
                }
            }
            var o = JObject.Parse(json);
            var result = new ExplorerResult { OpeningName = o["opening"]?.Value<string>("name") };
            foreach (var m in o["moves"] as JArray ?? new JArray())
            {
                long w = m.Value<long?>("white") ?? 0, d = m.Value<long?>("draws") ?? 0, b = m.Value<long?>("black") ?? 0;
                long total = w + d + b;
                if (total == 0) total = 1;
                result.Moves.Add(new ExplorerMoveRow
                {
                    San = m.Value<string>("san"),
                    Uci = m.Value<string>("uci"),
                    Total = w + d + b,
                    Stats = $"{(w + d + b):N0} games",
                    WhitePct = 100.0 * w / total,
                    DrawPct = 100.0 * d / total,
                    BlackPct = 100.0 * b / total,
                });
            }
            return result;
        }

        /// <summary>7-piece tablebase lookup (perfect endgame play).</summary>
        public async Task<TablebaseResult> GetTablebaseAsync(string fen)
        {
            string json = await GetAbsoluteAsync("https://tablebase.lichess.org/standard?fen=" + Uri.EscapeDataString(fen));
            if (json == null) return null;
            var o = JObject.Parse(json);
            var res = new TablebaseResult { Summary = DescribeTb(o.Value<string>("category"), o.Value<int?>("dtz")) };
            foreach (var m in o["moves"] as JArray ?? new JArray())
            {
                res.Moves.Add(new TablebaseRow
                {
                    San = m.Value<string>("san"),
                    Uci = m.Value<string>("uci"),
                    // A move's category is reported from the side to move AFTER it (the mover's
                    // opponent), so invert it — "Win" in the list must mean THIS move wins.
                    Outcome = DescribeTb(InvertTb(m.Value<string>("category")), m.Value<int?>("dtz")),
                });
            }
            return res;
        }

        static string InvertTb(string category)
        {
            switch (category)
            {
                case "win": return "loss";
                case "loss": return "win";
                case "cursed-win": return "blessed-loss";
                case "blessed-loss": return "cursed-win";
                case "maybe-win": return "maybe-loss";
                case "maybe-loss": return "maybe-win";
                default: return category;   // draw / unknown stay as-is
            }
        }

        static string DescribeTb(string category, int? dtz)
        {
            switch (category)
            {
                case "win": return dtz.HasValue ? $"Win · DTZ {Math.Abs(dtz.Value)}" : "Win";
                case "loss": return dtz.HasValue ? $"Loss · DTZ {Math.Abs(dtz.Value)}" : "Loss";
                case "draw": return "Draw";
                case "cursed-win": return "Win (50-move)";
                case "blessed-loss": return "Loss (50-move)";
                default: return category ?? "";
            }
        }

        // ----------------------------------------------------- account / games

        /// <summary>Recent finished games for a user (NDJSON), newest first.</summary>
        public async Task<System.Collections.Generic.List<GameSummary>> GetUserGamesAsync(string username, int max = 20)
        {
            var list = new System.Collections.Generic.List<GameSummary>();
            using (var req = Build(HttpMethod.Get, $"/api/games/user/{Uri.EscapeDataString(username)}?max={max}&moves=true&sort=dateDesc&lastFen=true"))
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("application/x-ndjson");
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) return list;
                    var raw = new System.Collections.Generic.List<string>();
                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                            if (!string.IsNullOrWhiteSpace(line)) raw.Add(line);
                    }
                    // Parse + replay SAN to the final position off the UI thread (this is the
                    // expensive part — legal-move generation per ply across all games).
                    await Task.Run(() =>
                    {
                        foreach (var line in raw)
                        {
                            JObject g;
                            try { g = JObject.Parse(line); } catch { continue; }
                            list.Add(ParseGameSummary(g, username));
                        }
                    });
                }
            }
            return list;
        }

        static GameSummary ParseGameSummary(JObject g, string me)
        {
            var whiteP = g["players"]?["white"];
            var blackP = g["players"]?["black"];
            var white = whiteP?["user"];
            var black = blackP?["user"];
            string whiteName = white?.Value<string>("name") ?? "Anonymous";
            string blackName = black?.Value<string>("name") ?? "Anonymous";
            bool iAmWhite = string.Equals(whiteName, me, StringComparison.OrdinalIgnoreCase);
            string opp = iAmWhite ? blackName : whiteName;
            int? myRating = iAmWhite ? whiteP?.Value<int?>("rating") : blackP?.Value<int?>("rating");
            int? oppRating = iAmWhite ? blackP?.Value<int?>("rating") : whiteP?.Value<int?>("rating");
            string winner = g.Value<string>("winner");
            string speed = g.Value<string>("speed") ?? "game";
            string result;
            int outcome;
            if (winner == null) { result = "Draw · ½-½"; outcome = 2; }
            else
            {
                bool iWon = (winner == "white") == iAmWhite;
                outcome = iWon ? 0 : 1;
                result = (iWon ? "Win · " : "Loss · ") + (winner == "white" ? "1-0" : "0-1");
            }
            if (myRating.HasValue) result += "  ·  " + myRating.Value;
            string initialFen = g.Value<string>("initialFen");
            string moves = g.Value<string>("moves") ?? "";
            string speedCap = char.ToUpperInvariant(speed[0]) + speed.Substring(1);
            return new GameSummary
            {
                Id = g.Value<string>("id"),
                Headline = $"vs {opp}",
                OpponentRating = oppRating.HasValue ? oppRating.Value.ToString() : "",
                ResultText = result,
                TimeControlText = FormatTimeControl(g, speedCap),
                DateText = "",
                Moves = moves,
                InitialFen = initialFen,
                // Prefer the server's final position (exact for variants); replay SAN as fallback.
                FinalFen = g.Value<string>("lastFen") ?? ComputeFinalFen(initialFen, moves),
                PlayerWhite = iAmWhite,
                Outcome = outcome,
                WhiteName = whiteName,
                BlackName = blackName,
            };
        }

        // Human-readable time control: "Blitz · 5+3", "Rapid · 10+0", "Correspondence · 3d", or just the speed.
        static string FormatTimeControl(JObject g, string speedCap)
        {
            var clock = g["clock"];
            if (clock != null)
            {
                int init = clock.Value<int?>("initial") ?? 0;
                int incr = clock.Value<int?>("increment") ?? 0;
                string mins = init == 15 ? "¼" : init == 30 ? "½" : init == 45 ? "¾"
                    : init % 60 == 0 ? (init / 60).ToString()
                    : (init / 60.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                return $"{speedCap} · {mins}+{incr}";
            }
            int days = g.Value<int?>("daysPerTurn") ?? 0;
            return days > 0 ? $"{speedCap} · {days}d" : speedCap;
        }

        // Replay SAN moves from the start to get the final position, for the board thumbnail.
        static string ComputeFinalFen(string initialFen, string sanMoves)
        {
            try
            {
                var pos = LichessXbox.Chess.ChessPosition.FromFen(
                    string.IsNullOrEmpty(initialFen) ? LichessXbox.Chess.ChessPosition.StartFen : initialFen);
                if (!string.IsNullOrWhiteSpace(sanMoves))
                {
                    foreach (var tok in sanMoves.Split(' '))
                    {
                        if (string.IsNullOrWhiteSpace(tok)) continue;
                        var mv = pos.ParseSan(tok);
                        if (mv == null) { try { mv = LichessXbox.Chess.ChessMove.FromUci(tok); } catch { mv = null; } }
                        if (mv != null) { var next = pos.Apply(mv.Value); if (next != null) pos = next; }
                    }
                }
                return pos.ToFen();
            }
            catch { return initialFen; }
        }

        /// <summary>The player's rating and rating change for a finished game (null if unrated/unavailable).</summary>
        public async Task<(int rating, int diff)?> GetGameRatingChangeAsync(string gameId, bool amWhite)
        {
            if (string.IsNullOrEmpty(gameId)) return null;
            try
            {
                using (var req = Build(HttpMethod.Get, $"/game/export/{gameId}?moves=false&clocks=false&evals=false&opening=false"))
                {
                    req.Headers.Accept.Clear();
                    req.Headers.Accept.ParseAdd("application/json");
                    using (var resp = await SendBufferedAsync(req))
                    {
                        if (!resp.IsSuccessStatusCode) return null;
                        var o = JObject.Parse(await resp.Content.ReadAsStringAsync());
                        var p = o["players"]?[amWhite ? "white" : "black"];
                        int? rating = p?.Value<int?>("rating");
                        if (rating == null) return null;
                        int diff = p.Value<int?>("ratingDiff") ?? 0;
                        return (rating.Value, diff);
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>Rating history for a user; returns points (x = day index, y = rating) for one perf.</summary>
        public async Task<System.Collections.Generic.List<(int x, int y)>> GetRatingHistoryAsync(string username, string perf = "Blitz")
        {
            var points = new System.Collections.Generic.List<(int, int)>();
            string json = await GetAbsoluteAsync(Base + $"/api/user/{username}/rating-history");
            if (json == null) return points;
            var arr = JArray.Parse(json);
            JToken series = arr.FirstOrDefault(s => string.Equals(s.Value<string>("name"), perf, StringComparison.OrdinalIgnoreCase))
                            ?? arr.FirstOrDefault();
            if (series == null) return points;
            int i = 0;
            foreach (var p in series["points"] as JArray ?? new JArray())
                points.Add((i++, (p as JArray)?[3]?.Value<int>() ?? 0));
            return points;
        }

        public async Task<System.Collections.Generic.List<string>> GetFollowingAsync()
        {
            var list = new System.Collections.Generic.List<string>();
            using (var req = Build(HttpMethod.Get, "/api/rel/following"))
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("application/x-ndjson");
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) return list;
                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try { list.Add(JObject.Parse(line).Value<string>("username")); } catch { }
                        }
                    }
                }
            }
            return list;
        }

        // -------------------------------------------------------- tournaments

        public async Task<System.Collections.Generic.List<TournamentItem>> GetTournamentsAsync()
        {
            var list = new System.Collections.Generic.List<TournamentItem>();
            string json = await GetAbsoluteAsync(Base + "/api/tournament");
            if (json == null) return list;
            var o = JObject.Parse(json);
            AddTournaments(list, o["started"] as JArray, "In progress");
            AddTournaments(list, o["created"] as JArray, "Upcoming");
            AddTournaments(list, o["finished"] as JArray, "Finished");
            return list;
        }

        static void AddTournaments(System.Collections.Generic.List<TournamentItem> list, JArray arr, string group)
        {
            if (arr == null) return;
            foreach (var t in arr)
            {
                int limit = t["clock"]?.Value<int?>("limit") ?? 0;
                int inc = t["clock"]?.Value<int?>("increment") ?? 0;
                string perf = t["perf"]?.Value<string>("name") ?? t.Value<string>("system") ?? "Arena";
                int players = t.Value<int?>("nbPlayers") ?? 0;
                list.Add(new TournamentItem
                {
                    Id = t.Value<string>("id"),
                    Name = t.Value<string>("fullName") ?? "Arena",
                    Info = $"{perf} · {limit / 60}+{inc} · {players} players",
                    Group = group,
                    // Board API plays Rapid/Classical only → estimated game time (limit + 40·inc) ≥ 480s.
                    Playable = (limit + 40 * inc) >= 480,
                });
            }
        }

        public async Task<(string title, System.Collections.Generic.List<TournamentPlayer> players)> GetTournamentStandingsAsync(string id)
        {
            var players = new System.Collections.Generic.List<TournamentPlayer>();
            string json = await GetAbsoluteAsync(Base + $"/api/tournament/{id}");
            if (json == null) return (null, players);
            var o = JObject.Parse(json);
            string title = o.Value<string>("fullName");
            foreach (var p in o["standing"]?["players"] as JArray ?? new JArray())
            {
                players.Add(new TournamentPlayer
                {
                    RankText = "#" + (p.Value<int?>("rank") ?? 0),
                    Name = p.Value<string>("name"),
                    Score = p.Value<int?>("score") ?? 0,
                    Rating = p.Value<int?>("rating") ?? 0,
                });
            }
            return (title, players);
        }

        /// <summary>Join an arena. Returns null on success, else a human-readable reason.</summary>
        public async Task<string> JoinTournamentAsync(string id)
        {
            // pairMeAsap: pair us even though we're not "connected to the tournament page".
            // An API client has no tournament page open, so without this lichess treats the
            // player as absent and NEVER pairs them. Re-sending join (idempotent; it also
            // unpauses) re-asserts it for the next round.
            using (var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["pairMeAsap"] = "true" }))
            using (var req = Build(HttpMethod.Post, $"/api/tournament/{id}/join", content))
            using (var resp = await SendBufferedAsync(req))
            {
                if (resp.IsSuccessStatusCode) return null;
                // 401/403 = the token can't join arenas. Re-pairing by QR grants the scope,
                // but a pasted personal token keeps the scopes it was CREATED with — signing
                // out and back in with the same token will never fix it.
                if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    return "This sign-in can't join arenas. Re-pair by QR on the Profile page, or paste a new token created with the tournament:write scope.";
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                return ExtractApiError(body, (int)resp.StatusCode);
            }
        }

        /// <summary>Leave (pause out of) an arena. Returns null on success, else a reason.</summary>
        public async Task<string> WithdrawTournamentAsync(string id)
        {
            using (var req = Build(HttpMethod.Post, $"/api/tournament/{id}/withdraw"))
            using (var resp = await SendBufferedAsync(req))
            {
                if (resp.IsSuccessStatusCode) return null;
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                return ExtractApiError(body, (int)resp.StatusCode);
            }
        }

        // -------------------------------------------------------------- studies

        public async Task<System.Collections.Generic.List<StudyItem>> GetStudiesByUserAsync(string username)
        {
            var list = new System.Collections.Generic.List<StudyItem>();
            // Public endpoint — fetch ANONYMOUSLY. The app's OAuth token lacks the study:read scope,
            // and lichess rejects (401) token-bearing study requests without it, even for public data.
            using (var req = new HttpRequestMessage(HttpMethod.Get, Base + $"/api/study/by/{Uri.EscapeDataString(username)}"))
            {
                req.Headers.Accept.ParseAdd("application/x-ndjson");
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) return list;
                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            JObject s;
                            try { s = JObject.Parse(line); } catch { continue; }
                            list.Add(new StudyItem
                            {
                                Id = s.Value<string>("id"),
                                Name = s.Value<string>("name"),
                                Owner = username,
                            });
                        }
                    }
                }
            }
            // A public study can still have PGN export disabled by its owner: the export
            // endpoint 403s (even anonymously) while the study stays listed and viewable on
            // the site. HEAD doesn't discriminate (204 either way), so probe with a
            // headers-only GET and mark view-only studies before the user clicks into them.
            using (var gate = new SemaphoreSlim(6))
            {
                var probes = new List<Task>();
                foreach (var s in list) probes.Add(ProbeStudyExportableAsync(s, gate));
                try { await Task.WhenAll(probes); } catch { }
            }
            return list;
        }

        async Task ProbeStudyExportableAsync(StudyItem s, SemaphoreSlim gate)
        {
            await gate.WaitAsync();
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, Base + $"/api/study/{s.Id}.pgn"))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    s.Exportable = (int)resp.StatusCode != 403;
            }
            catch { /* leave Exportable=true — the click path reports the real error */ }
            finally { gate.Release(); }
        }

        /// <summary>Export a whole study as PGN (chapters separated by blank lines).</summary>
        public async Task<string> GetStudyPgnAsync(string studyId)
        {
            // Public studies export fine ANONYMOUSLY — and lichess rejects a token-bearing
            // request when the token lacks the study:read scope (e.g. a personal token created
            // before that scope was ticked), even for public data. So go anonymous first, and
            // only retry with the token for studies the anonymous request can't see (private).
            string pgn = await FetchStudyPgnAsync(studyId, false);
            if (pgn == null && _auth.IsAuthenticated) pgn = await FetchStudyPgnAsync(studyId, true);
            return pgn;
        }

        /// <summary>HTTP status of the most recent study export attempt — surfaced in the
        /// Studies page error so a failure on the console tells us WHAT lichess said.</summary>
        public int LastStudyExportStatus { get; private set; }

        async Task<string> FetchStudyPgnAsync(string studyId, bool withToken)
        {
            var req = withToken
                ? Build(HttpMethod.Get, $"/api/study/{studyId}.pgn")
                : new HttpRequestMessage(HttpMethod.Get, Base + $"/api/study/{studyId}.pgn");
            using (req)
            {
                req.Headers.Accept.Clear();
                req.Headers.Accept.ParseAdd("application/x-chess-pgn");
                using (var resp = await SendBufferedAsync(req, default, 25))
                {
                    LastStudyExportStatus = (int)resp.StatusCode;
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsStringAsync();
                }
            }
        }

        // ---------------------------------------------------------------- TV

        /// <summary>Stream the current Lichess TV featured game (best ongoing game).</summary>
        public Task StreamTvFeedAsync(Action<JObject> onFrame, CancellationToken ct) =>
            StreamNdjsonAsync("/api/tv/feed", onFrame, ct);

        /// <summary>Stream a specific TV channel (e.g. "blitz", "bullet", "bot").</summary>
        public Task StreamTvChannelAsync(string channelKey, Action<JObject> onFrame, CancellationToken ct) =>
            StreamNdjsonAsync($"/api/tv/{channelKey}/feed", onFrame, ct);

        public async Task<System.Collections.Generic.List<TvChannel>> GetTvChannelsAsync()
        {
            var list = new System.Collections.Generic.List<TvChannel>();
            string json = await GetAbsoluteAsync(Base + "/api/tv/channels");
            if (json == null) return list;
            var o = JObject.Parse(json);
            var glyphs = new System.Collections.Generic.Dictionary<string, string>
            {
                ["bullet"] = "🚀", ["blitz"] = "⚡", ["rapid"] = "🐎", ["classical"] = "🏛",
                ["bot"] = "🤖", ["computer"] = "💻", ["ultraBullet"] = "💥", ["best"] = "👑",
            };
            foreach (var prop in o.Properties())
            {
                string key = prop.Name;
                var v = prop.Value;
                string featured = v["user"]?.Value<string>("name");
                int? rating = v.Value<int?>("rating");
                list.Add(new TvChannel
                {
                    Key = key,
                    Name = char.ToUpperInvariant(key[0]) + key.Substring(1),
                    Featured = featured != null ? $"{featured}{(rating.HasValue ? " (" + rating + ")" : "")}" : "",
                    Glyph = glyphs.TryGetValue(key, out var gl) ? gl : "♟",
                });
            }
            return list;
        }

        public async Task<System.Collections.Generic.List<LiveStreamer>> GetLiveStreamersAsync()
        {
            var list = new System.Collections.Generic.List<LiveStreamer>();
            string json = await GetAbsoluteAsync(Base + "/api/streamer/live");
            if (json == null) return list;
            foreach (var s in JArray.Parse(json))
            {
                var stream = s["stream"];
                list.Add(new LiveStreamer
                {
                    Name = s.Value<string>("name") ?? s["stream"]?.Value<string>("status"),
                    Status = stream?.Value<string>("status") ?? "",
                    Url = "https://lichess.org/streamer/" + s.Value<string>("id"),
                });
            }
            return list;
        }

        public async Task<System.Collections.Generic.List<SimulItem>> GetSimulsAsync()
        {
            var list = new System.Collections.Generic.List<SimulItem>();
            string json = await GetAbsoluteAsync(Base + "/api/simul");
            if (json == null) return list;
            var o = JObject.Parse(json);
            AddSimuls(list, o["started"] as JArray, "in progress");
            AddSimuls(list, o["created"] as JArray, "open");
            AddSimuls(list, o["pending"] as JArray, "pending");
            return list;
        }

        static void AddSimuls(System.Collections.Generic.List<SimulItem> list, JArray arr, string group)
        {
            if (arr == null) return;
            foreach (var s in arr)
            {
                string host = s["host"]?.Value<string>("name") ?? s["host"]?.Value<string>("id") ?? "host";
                int applicants = s.Value<int?>("nbApplicants") ?? 0;
                int pairings = s.Value<int?>("nbPairings") ?? 0;
                int players = pairings > 0 ? pairings : applicants;
                list.Add(new SimulItem
                {
                    Name = s.Value<string>("name") ?? "Simul",
                    Info = $"by {host} · {players} players · {group}",
                });
            }
        }

        public async Task<System.Collections.Generic.List<BroadcastItem>> GetBroadcastsAsync()
        {
            var list = new System.Collections.Generic.List<BroadcastItem>();
            string json = await GetAbsoluteAsync(Base + "/api/broadcast/top");
            if (json == null) return list;
            JToken root = JToken.Parse(json);
            JToken active = root is JObject obj ? (obj["active"] ?? obj["past"]) : root;
            foreach (var b in active as JArray ?? new JArray())
            {
                var tour = b["tour"] ?? b;
                list.Add(new BroadcastItem
                {
                    Name = tour?.Value<string>("name") ?? "Broadcast",
                    Description = tour?.Value<string>("description") ?? "",
                    Url = "https://lichess.org/broadcast",
                });
            }
            return list;
        }
    }
}
