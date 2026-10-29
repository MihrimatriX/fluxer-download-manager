# Fluxer

Fluxer is a download manager for Windows. It splits HTTP downloads across several connections, handles BitTorrent, pulls video from sites that yt-dlp supports, and picks up downloads from Chromium browsers and Firefox through its extension. The UI is a native Avalonia app.

## What it does

- Multi-connection HTTP downloads with resume. When a link expires, Fluxer marks the download as needing a new link and lets you refresh it from the page.
- BitTorrent through MonoTorrent: magnet links, `.torrent` files, DHT, peer exchange, local peer discovery, UPnP/NAT-PMP and encryption settings.
- Video downloads through yt-dlp. On first use Fluxer fetches yt-dlp, deno and ffmpeg from their official GitHub releases and checks each one against its SHA-256 sum.
- Browser integration for Chrome, Edge, Brave, Opera, Vivaldi and Firefox (128 or later). The extension hands downloads to Fluxer, can collect every file or image on a page, and shows a small card with a 3-second countdown before it takes a download, so you can leave it in the browser instead (hover the card or press its pause button to stop the countdown). Its popup pauses or resumes everything in Fluxer with one button, can pause capturing for an hour, and takes a typed or pasted link.
- Speed profiles (Unlimited, Balanced, Background), each with its own download, upload and concurrency limits, plus a weekly 7 × 24 schedule that switches between them.
- A night queue that starts at a time you choose (01:00 by default).
- A compact main window: commands in the title bar, state and file-type filters in one row, a small detail panel under the list, and a command palette (Ctrl+K) that reaches every action, filter, speed profile and settings page.
- A tray window with live speed, a mini graph, running downloads, a box for pasting links and one button that pauses or resumes everything.
- Deleting is undoable for 6 seconds, and files stay on disk unless you pick "Delete with files…" from the context menu.
- Optional clipboard monitoring for copied file links and magnets, and a watch folder that adds any `.torrent` saved into it.
- Torrent RSS feeds: new items in RSS or Atom feeds are added on a timer, with per-feed include/exclude filters (regex). Items already in a feed when you add it are skipped.
- Finished downloads are handed to your antivirus through Windows Attachment Services, the way browsers do.
- Editable categories (extensions, sites, folder) and your own ones, tags, and named queues with their own times, days, concurrency and when-done action.
- Per-site settings (login, connection cap, User-Agent, Referer, speed limit) and smart speed: a slower profile while you work in another program, a hold on low battery, and a rolling traffic quota.
- Batch link patterns (`file_[001-120].jpg`), a warning for links already in the list, a site crawler that lists the files a site links to, and a floating drop target.
- Finished archives can be unpacked (zip, rar, 7z, tar, split sets, a password list), and a program or webhook can run when a download finishes.
- Statistics: today, week, month and all time, the last 24 hours and a 12-month heatmap.
- Remote access: a REST API, aria2-compatible JSON-RPC (AriaNg and "send to aria2" extensions work) and a web page for the phone, all behind an API key.
- JavaScript plugins that react to downloads, each with the permissions you approved.
- Torrent extras: a subscribed tracker list, a peer block list (ipfilter.dat, .p2p, CIDR), recheck and move data, a piece map, and Torznab search (Jackett, Prowlarr).
- Portable mode, list and settings export/import, and daily or weekly database backups.
- English and Turkish UI.

## Install

1. Download `Fluxer-win-Setup.exe` from the [latest release](../../releases/latest) and run it. It installs per user under `%LOCALAPPDATA%\Fluxer` without admin rights and adds a Start menu entry.
2. Load the browser extension:
   - Open `chrome://extensions` (Edge: `edge://extensions`) and turn on Developer mode.
   - Click "Load unpacked" and pick `%LOCALAPPDATA%\Fluxer\current\extension`.
   - The extension ID should be `dckkomckipcaglacbgfmbgmapfckpkja`. The manifest carries a fixed key, so the ID is the same on every machine.
   - Firefox: install `fluxer-extension-firefox-<version>.xpi` from the release page when it is there (signed by Mozilla). Without it, open `about:debugging` → This Firefox → "Load Temporary Add-on…" and pick `manifest.json` in `%LOCALAPPDATA%\Fluxer\current\extension-firefox`; Firefox drops a temporary add-on when it restarts.
3. Download a file in the browser and it lands in Fluxer. Hold Alt while clicking to keep a download in the browser.

Each time Fluxer starts, it writes or repairs its native messaging registrations (Chrome, Edge, Brave, Chromium and Firefox; Opera and Vivaldi read Chrome's) and the `magnet:` and `.torrent` associations under `HKCU`. You can check their state in Settings → Browser integration.

Windows 10 version 1809 or later, x64.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+N | New download (multi-line paste works) |
| Ctrl+V | Add links from the clipboard |
| Ctrl+O | Open a .torrent file |
| Ctrl+F | Search |
| Space / Delete / Enter | Pause or resume / delete / open file |
| Ctrl+Shift+P / Ctrl+Shift+R | Pause all / resume all |
| Ctrl+↑ / Ctrl+↓ | Move the selected rows in the queue |
| Ctrl+K | Command palette: every action, filter, speed profile and settings page |
| Ctrl+I | Toggle the details panel |
| Ctrl+, | Settings |

Closing the window sends Fluxer to the tray. Quit from the tray menu. Settings save as soon as you change them.

## Updates

Installed copies update themselves from this repository's GitHub releases. Fluxer checks at start and every few hours, downloads new versions in the background and installs them when you exit (or right away from Settings → Advanced). Turn on "Beta versions" there to also get pre-releases, or point "Update source" at another folder or URL that hosts a Velopack feed.

Signing the installer and publishing the extension to the Chrome Web Store and Edge Add-ons are described in [docs/RELEASING.md](docs/RELEASING.md).

## Building from source

You need the .NET SDK 10.0.401 or newer and Node 22. Building the installer also needs the Visual Studio C++ tools, because the native messaging host is compiled with NativeAOT.

```powershell
dotnet build                                              # whole solution
dotnet test --project tests/Fluxer.Core.Tests             # unit tests
dotnet test --project tests/Fluxer.Integration.Tests      # integration tests against local Kestrel servers
cd extension; npm ci; npm test; npm run build             # extension -> extension/dist (+ dist-firefox)
$env:FLUXER_INSTANCE="dev"; dotnet run --project src/Fluxer.App   # separate profile next to an installed copy
dotnet run --project src/Fluxer.Cli -- get <url> -c 8     # engine console tool
pwsh build/pack.ps1                                       # installer -> artifacts/releases
```

`build/pack.ps1` takes its version from `Directory.Build.props` unless you pass `-Version`.

## Releases

GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) runs the tests and builds the installer on every push, and attaches the result to the run as an artifact. Pushing a tag such as `v0.8.0` also publishes a GitHub release with the installer and the update feed files:

```powershell
git tag v0.8.0
git push origin v0.8.0
```

## Project layout

| Path | Contents |
|---|---|
| `src/Fluxer.Core` | Download engine: HTTP segments, torrents, yt-dlp, queue, SQLite storage |
| `src/Fluxer.App` | Avalonia desktop app |
| `src/Fluxer.Ipc` | Message contracts shared by the app and the native host |
| `src/Fluxer.NativeHost` | Native messaging host (NativeAOT) that relays browser messages to the app over a named pipe |
| `src/Fluxer.Cli` | Console front end for the engine |
| `extension` | Manifest V3 browser extension (TypeScript, Vite) |
| `tests` | Unit and integration tests |

Design notes are in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and release history in [CHANGELOG.md](CHANGELOG.md), both in Turkish.
