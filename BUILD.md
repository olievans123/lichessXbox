# Building & deploying without a Windows machine

You can't compile UWP natively on macOS — the toolchain is Windows-only. The trick:
**build on a free GitHub-hosted Windows runner**, then **deploy to the Xbox from Safari**
via the console's Device Portal. No local Windows required.

---

## 1. Push to GitHub (one-time)

`gh` (the GitHub CLI) is the quickest path. Auth is interactive (opens a browser), so
these run in *your* terminal:

```bash
brew install gh                 # if not already installed
gh auth login                   # choose GitHub.com → HTTPS → login via browser
cd /Users/patevans/LichessXbox
gh repo create lichess-xbox --private --source=. --remote=origin --push
```

That push triggers the build automatically.

**No-CLI alternative:** create an empty repo at github.com, then:

```bash
cd /Users/patevans/LichessXbox
git remote add origin https://github.com/<your-username>/lichess-xbox.git
git push -u origin HEAD
```

---

## 2. Watch the build

```bash
gh run watch                    # live status, or use the repo's Actions tab
```

The workflow (`.github/workflows/build-uwp.yml`):
- compiles `Release | x64` on `windows-latest`
- generates a self-signed cert matching the manifest publisher (`CN=LichessXbox`)
- publishes the **LichessXbox-sideload** artifact (`.msixbundle` + Dependencies + `.cer`)

> The **first run will likely fail** — nothing has ever been compiled. Grab the errors:
> ```bash
> gh run view --log-failed
> ```
> Send them back and we fix → commit → push → it rebuilds.

---

## 3. Deploy to the Xbox (from your Mac)

1. On the Xbox: install **Dev Mode Activation** from the Store and activate Developer Mode.
2. Dev Home → enable **Remote Access**; note the console IP.
3. In **Safari**, open `https://<xbox-ip>:11443` (accept the cert warning) and sign in.
4. **My games & apps → Add**: upload the `.msixbundle`, add the **Dependencies** packages,
   and select the **`.cer`** as the certificate. Install, then launch.

---

## Optional: offline local engine

The analysis board's **Local** engine toggle loads `stockfish.js`. For offline use, drop a
real `stockfish.js` into `LichessXbox/Assets/engine/` before pushing; otherwise it loads
from a CDN at runtime.

## Optional: ARM64

The workflow builds `x64`. Newer Xbox models are ARM — if x64 won't deploy, add `ARM64` to
`AppxBundlePlatforms` / the build matrix (ask and I'll update the workflow).
