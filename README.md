# ♞ Lichess for Xbox

A beautiful, **native Xbox** chess client built on **UWP + WinUI**, integrated with
[lichess.org](https://lichess.org). Play rated and casual games with your controller,
watch Lichess TV live, solve puzzles, and track your ratings — all in a 10‑foot,
gamepad‑first interface.

> Built to be opened in **Visual Studio on Windows** and deployed to an **Xbox in
> Developer Mode**. It also runs as a normal UWP app on a Windows PC, which is the
> fastest way to iterate.

---

## ✨ Features

| | |
|---|---|
| **Play online** | OAuth login, public seeks across Bullet / Blitz / Rapid / Classical, **play vs Stockfish (levels 1–8)**, **challenge any user**, **incoming-challenge inbox** (accept/decline), live clocks, resign, full game streaming via the Lichess **Board API**. |
| **Variants** | **All 8 variants** — Chess960, Crazyhouse, King of the Hill, Three-check, Antichess, Atomic, Horde, Racing Kings — selectable for quick pairing, the computer, and challenges. |
| **Analysis** | Free exploration board with move navigation + flip, FEN import, **cloud engine eval + eval bar**, **opening explorer** (names & stats), and **endgame tablebase**. Replay any of your games here in one click. |
| **Watch** | All 8 Lichess TV channels with live switching, plus live streamers and broadcasts. |
| **Puzzles** | Daily puzzle (signed out) or your personalized next puzzle (signed in), with solution validation and an auto‑playing opponent. |
| **Games & account** | Your recent games with one-click replay-into-analysis, a rating graph over time, and your following list. Ratings per time control, Patron status, secure sign-out on Profile. |
| **Tournaments** | Browse live / upcoming / finished arenas, view standings, and **join** with one click. |
| **Studies** | Browse any member's public studies and open a chapter on the analysis board. |
| **In-game extras** | Offer/accept draws, request takebacks, **rematch**, plus correspondence (daily) games. |
| **Tools** | A board editor (place pieces → analyze), a coordinate trainer (timed), and Settings: **6 board themes** (live), outline piece set, and UI sounds. |
| **Gamepad‑first board** | A custom chess board control: D‑pad / left‑stick to move a focus cursor, **A** to pick up and drop a piece, **B** to cancel, on‑board promotion picker, last‑move / check / legal‑target highlights, smooth focus visuals. |

The whole UI is tuned for a TV: large type, controller focus animations, a warm
"tournament" palette inspired by the Lichess board.

---

## 🧱 Architecture

```
LichessXbox/
├─ App.xaml(.cs)            App bootstrap, full-screen TV bounds, dark theme
├─ MainPage.xaml(.cs)       NavigationView shell (Home / Play / TV / Puzzles / Profile)
├─ Styles/Theme.xaml        Palette, type ramp, focusable tile/button styles
├─ Chess/                   Self-contained rules engine (no external chess lib)
│  ├─ ChessMove.cs          0..63 squares, UCI parsing
│  └─ ChessPosition.cs      FEN, legal move generation, check/mate, SAN matcher
├─ Controls/
│  └─ ChessBoardControl     The gamepad-driven board (renders any ChessPosition)
├─ Services/
│  ├─ Pkce.cs               RFC 7636 PKCE helpers
│  ├─ LichessAuthService    OAuth2 + PKCE via WebAuthenticationBroker, token vault
│  ├─ LichessApiService     REST + NDJSON streaming (events, board, TV, puzzles)
│  └─ AppState.cs           Process-wide auth/account/api singleton
├─ Models/LichessModels.cs  DTOs (account, presets, puzzle, players)
└─ Views/                   Home, Login, Play, Tv, Puzzles, Profile pages
```

**No external chess engine** — `ChessPosition` does its own legal move generation,
check/checkmate/stalemate detection, FEN round‑tripping, and a SAN matcher used to
replay puzzle PGNs. The only NuGet dependencies are `Newtonsoft.Json`,
`Microsoft.UI.Xaml` (WinUI 2), and the UWP platform package.

---

## 🔑 Lichess OAuth — no app registration required

Lichess is an **open OAuth2 provider**: a public client needs **no client secret and
no pre‑registered app**. This project uses the **Authorization Code flow with PKCE**
through the UWP `WebAuthenticationBroker` (which works on Xbox). The redirect URI is
the broker's per‑install callback URI, which Lichess accepts for public clients.

Scopes requested: `board:play`, `puzzle:read`, `preference:read`.

The bearer token is stored in the Windows **PasswordVault**, so you stay signed in
between sessions. Sign‑out revokes the token server‑side.

If you ever want a distinct client id, change `LichessAuthService.ClientId`.

---

## 🛠 Prerequisites

- **Windows 10 (1809+) or Windows 11**
- **Visual Studio 2022** with the **Universal Windows Platform development** workload
- **Windows 10/11 SDK 10.0.22621.0** (or adjust `TargetPlatformVersion` in the
  `.csproj` to an SDK you have installed)

## ▶️ Build & run on a PC (fastest loop)

1. Open `LichessXbox.sln` in Visual Studio.
2. If prompted about a signing certificate: open `Package.appxmanifest` →
   **Packaging** → **Choose Certificate…** → **Create…** to generate a test cert
   (VS usually does this automatically on first build).
3. Set the configuration to **x64 / Debug** and target **Local Machine**.
4. Press **F5**. Sign in from the **Profile** tab, then play.

> A wired or virtual gamepad works on the PC too; keyboard arrows + Enter also drive
> the board, so you can validate the controller UX without a console.

## 🎮 Deploy to an Xbox (Developer Mode)

1. On the Xbox, install **Dev Mode Activation** from the Store and activate Developer
   Mode (one‑time, requires a Microsoft Partner Center developer account).
2. On the console, open **Dev Home → Remote Access** and note the console IP, then set
   a username/password for pairing.
3. In Visual Studio, set the configuration to **x64** (Xbox is x64) and the target to
   **Remote Machine**.
4. In **Project → Properties → Debug**, enter the Xbox IP under *Remote machine* and
   choose *Universal (Unencrypted Protocol)* authentication; pair when prompted.
5. Press **F5** to deploy and launch on the console.

The app declares the `Windows.Xbox` device family, draws into the full TV‑safe area,
and is entirely controller‑navigable.

---

## 🎯 Controls

| Action | Gamepad | Keyboard |
|---|---|---|
| Move board cursor | D‑pad / Left stick | Arrow keys |
| Pick up / drop piece | **A** | Enter / Space |
| Cancel selection | **B** | Esc |
| Navigate menus | D‑pad / Left stick | Tab / Arrows |
| Activate item | **A** | Enter |

Promotions show an on‑board picker (Q / R / B / N).

---

## 🖼 Replacing the placeholder art

`Assets/*.png` are solid‑green placeholders so the project builds immediately. Drop in
real tile/splash/logo art at the same filenames and sizes (see
`Package.appxmanifest`). Visual Studio's **Asset Generator** (double‑click the
manifest → *Visual Assets*) can produce every scaled variant from a single source
image.

---

## 🗺 Road to full Lichess parity

Tracking toward feature parity with the official Lichess app, milestone by milestone.

- [x] **M0 — Foundation**: native shell, chess engine, gamepad board, OAuth, online play, TV, puzzles, profile.
- [x] **M1 — Play (humans & AI)**: vs Computer (Stockfish 1–8), challenge a user, incoming-challenge inbox.
- [x] **M2 — Variants**: Chess960, Crazyhouse, King of the Hill, Three-check, Antichess, Atomic, Horde, Racing Kings — selectable across quick pairing / AI / challenges, played server-validated. *(See limitations below.)*
- [x] **M3 — Analysis**: explorable board, move nav + flip, FEN import, game replay, **cloud eval** + eval bar (`/api/cloud-eval`), **opening explorer** (`explorer.lichess.org`), **tablebase** (`tablebase.lichess.org`).
- [x] **M4 — Watch**: all 8 TV channels (live switch), live streamers, broadcasts.
- [x] **M7 — Account & social**: game history with one-click replay into analysis, rating graph, following list.
- [x] **M1.5 — Play extras**: rematch, draw offers, takebacks, correspondence (daily) seeks, claim-victory.
- [x] **M5 — Puzzles+**: Training / Streak / Themed modes (streak counter, auto-advance, theme picker). *(Storm/Racer aren't in the public API; Streak is the supported timed mode.)*
- [x] **M6 — Compete**: Arena tournaments — browse live/upcoming/finished, standings, join. *(Swiss/simuls pending.)*
- [x] **M8 — Studies**: browse a member's studies, open a chapter on the analysis board (PGN→moves).
- [x] **M9 — Tools & polish**: board editor (→ analysis), coordinate trainer, board themes (6 presets, live), outline piece set, UI sounds.
- [x] **M3.5 — Local engine**: a hidden WebView runs `stockfish.js` and feeds the Analysis board; toggle **Cloud ⇄ Local** for any-position eval + PV. Drop a real `stockfish.js` into `Assets/engine/` for offline; otherwise it loads from a CDN.
- [x] **Simuls**: live/open/pending simultaneous exhibitions listed on the Watch page.
- [x] **Move sounds**: synthesized move / capture / check audio, toggleable in Settings.
- [ ] **True long tail**: Swiss tournaments & Storm/Racer (no public API), richer piece-set art, push notifications, full preferences sync.

### Variant limitations (M2)
Variants are **playable and server-validated**: the board offers pseudo-legal move
hints and Lichess is the arbiter, so illegal inputs are simply rejected. Two inputs
aren't fully wired on the board yet and are tracked for a follow-up: **Crazyhouse
piece drops** (no pocket UI) and **Chess960 castling** (king-onto-rook input). Normal
moves in every variant work today.

## 🚧 Notes & next steps

- The board renders pieces with Unicode glyphs (`Segoe UI Symbol`) using a two‑layer
  fill+outline for crisp contrast on any square. Swap in SVG/PNG piece sets in
  `ChessBoardControl` for a custom look.
- Move list shows UCI; adding SAN display is a small extension (`ChessPosition`
  already has the matcher; the reverse — generating SAN — is the only missing half).
- Possible additions: challenge a specific user / the AI (`/api/challenge/*`),
  rematches, board flip button, broadcasts, opening explorer, sound effects, and
  per‑move clock increments shown on the move.
- All network calls are tolerant of stream drops; reconnect logic can be hardened for
  flaky console networks.

---

Made with ☕ and ♞. Lichess is a free/libre project — please consider becoming a
[Patron](https://lichess.org/patron).
