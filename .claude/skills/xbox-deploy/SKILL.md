---
name: xbox-deploy
description: Remotely build, install, launch, and screenshot the LichessXbox app on the dev Xbox via Windows Device Portal — no Windows PC or manual Safari sideload. Use when iterating on LichessXbox and you want to deploy a change and see it running, verify a fix on-device, or screenshot a page to critique its UX.
---

# Remote Xbox deploy & iterate loop

Lets you run the full **build → install → launch → screenshot → fix → redeploy**
loop on the dev Xbox without a Windows machine. The console's Windows Device Portal
(REST API over HTTPS) does the installing; CI builds the package.

## The loop

```bash
cd ~/LichessXbox
# 1. commit + push your change; wait for the green CI run (see BUILD.md / gh run watch)
# 2. install the latest green artifact and launch it:
.claude/skills/xbox-deploy/deploy.sh install
# 3. look at what's on screen:
.claude/skills/xbox-deploy/deploy.sh shot /tmp/x.png   # then Read /tmp/x.png
```

`install` downloads the newest successful `LichessXbox-sideload` artifact, stops the
running app, deploys the bundle + dependencies, and launches it. Individual verbs:
`launch`, `shot [path]`, `stop`, `uninstall`, `state`.

Override the console address with `XBOX=host:port` (default `192.168.1.64:11443`).

## What you CAN and CANNOT do remotely

- **CAN:** install/uninstall, launch, screenshot any current screen, read
  `LocalState/error.log` (via the portal filesystem) when it crashes.
- **CANNOT:** inject gamepad input. You see whatever screen is up, but you can't
  press buttons or move focus. To reach a screen (open a study, start a game,
  go to Tournaments) the **user must navigate there**, then you screenshot and
  critique. Pages reachable from a cold launch (Home + wherever it opens) are
  fully autonomous.

## Hard-won gotchas (all handled by deploy.sh)

- **CSRF:** every non-GET to the portal 403s without a token. Do one GET to seed a
  cookie jar, then send the cookie's CSRF value back as `X-CSRF-Token`.
- **The reliable loop = CONSOLE uninstall → ONE clean remote install.** This is
  the big one. On this console ANY remote re-deploy lands in the **"Not ready yet"
  (0x80270300)** limbo (a blank green tile; every launch returns HTTP 400) — this
  is true *both* when deploying over an existing install *and* after a remote
  uninstall (WDP DELETE only half-deregisters; the box has two registered users,
  `DefaultAccount` + `UserMgr1`). A single `deploy.sh install` over a *truly absent*
  app (uninstalled from the console UI: **My games & apps → Apps → Online Chess →
  Manage → Uninstall**) launches first try. So the iteration loop is: **(1)** ask
  the user to uninstall from the console, **(2)** `deploy.sh install` once. There is
  no working remote-only update path on this box — don't try to deploy over a live
  install or remote-uninstall; both produce the limbo and waste a cycle.
- **Stop before redeploy:** deploying over the *running* app also fails outright
  with `0x80070005 "Access is denied"`. The script force-stops first regardless.
- **Launch needs an empty body:** `POST /api/taskmanager/app` returns **HTTP 411**
  without one — send `-d ''`.
- **Finalizing delay:** right after install, launch returns 400 for ~30–60s while
  the app registers. Retry through it (the script loops).
- **Dependencies:** install the x64 `VCLibs`, `NET.Native.Framework 2.2`, and
  `NET.Native.Runtime 2.2` `.appx` alongside the `.msixbundle` or first launch fails.

## Package identity (stable)

- PackageFullName: `Lichess.Xbox.Chess_1.0.0.0_x64__7mhbhha5y68ey`
- AppUserModelId:  `Lichess.Xbox.Chess_7mhbhha5y68ey!App`
- CI repo / artifact: `olievans123/lichessXbox` / `LichessXbox-sideload`

## Key endpoints (Windows Device Portal)

| Action | Method | Path |
|---|---|---|
| seed CSRF cookie | GET | `/api/os/info` |
| install | POST | `/api/app/packagemanager/package?package=<bundle>` (multipart: bundle + deps) |
| deploy/uninstall result | GET | `/api/app/packagemanager/state` |
| list packages | GET | `/api/app/packagemanager/packages` |
| uninstall | DELETE | `/api/app/packagemanager/package?package=<PackageFullName>` |
| force-stop | DELETE | `/api/taskmanager/app?package=<b64 PFN>&forcestop=yes` |
| launch | POST | `/api/taskmanager/app?appid=<b64 AppId>&package=<b64 PFN>` (body `-d ''`) |
| screenshot | GET | `/ext/screenshot?download=true` |
