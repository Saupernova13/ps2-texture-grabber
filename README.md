# ps2-texture-grabber

[![CI](https://github.com/Saupernova13/ps2-texture-grabber/actions/workflows/ci.yml/badge.svg)](https://github.com/Saupernova13/ps2-texture-grabber/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Saupernova13/ps2-texture-grabber?sort=semver)](https://github.com/Saupernova13/ps2-texture-grabber/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

One command to give a PS2 game HD textures.

`dlps2tex "God of War"` resolves the game to its PCSX2 serial, finds a texture
pack for it, downloads it, installs it into PCSX2's `textures` folder, and writes
the INI flags that make PCSX2 actually load it. No manual unzipping, no hunting
for the right serial, no editing config by hand.

Packs come from [GBAtemp's PCSX2 HD Texture Pack subforum](https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.549/)
and an Archive.org index, and can be hosted on MEGA, Google Drive, MediaFire,
Yandex Disk, GitHub releases or plain HTTP — all handled for you.

---

## Install

1. Download the latest **`ps2tex-vX.Y.Z-win-x64.zip`** from the
   [releases page](https://github.com/Saupernova13/ps2-texture-grabber/releases/latest)
   and unzip it anywhere, e.g. `C:\Tools\ps2tex`.
2. Add that folder to your `PATH` so `dlps2tex` works from any terminal.
3. Check it:
   ```cmd
   dlps2tex --version
   ```

No .NET installation is needed — the runtime is bundled. Keep `dlps2tex.cmd` and
`ps2tex.exe` in the same folder; the launcher looks for the exe beside itself.

> Prefer building it yourself? See [Building from source](#building-from-source).

### Prerequisites

| | Why |
|---|---|
| **Windows 10/11 (x64)** | The tool writes into PCSX2's `%APPDATA%` layout. |
| **PCSX2-Qt** | The target. Paths default to the EmuDeck layout and are overridable. |
| **7-Zip** — `winget install 7zip.7zip` | Needed for `.7z` and `.rar` packs. `.zip` works without it. |
| **Edge or Chrome** | Used to read GBAtemp past its Cloudflare challenge. Pre-installed on Windows 11; the bundled Chromium is a less reliable fallback. |

---

## Usage

**Nothing here blocks on a download.** A query resolves the game and its links,
spawns a detached background worker, prints a **Job ID** and returns — usually in
seconds. The worker downloads, extracts, installs and configures on its own, and
survives the terminal (or the agent) that started it closing. There is
deliberately no `--wait` flag.

```cmd
:: Download and install a pack -- returns a job id immediately
dlps2tex "Dragon Ball Z Budokai Tenkaichi 3"

:: A serial works too, and is exact -- no chance of matching the wrong region
dlps2tex "SLUS-21569"

:: How is that download going?
dlps2tex --status <jobId>
dlps2tex --status <jobId> --json     :: machine-readable

:: What do I already have installed?
dlps2tex --list
dlps2tex --list --json

:: Reclaim disk
dlps2tex --clean
dlps2tex --clean --dry-run           :: preview, delete nothing
dlps2tex --clean --all               :: also clear finished job records

dlps2tex --version
```

A spawn looks like this, and your prompt comes straight back:

```
Download job spawned.  It will continue in the background.
  Job ID:   e5d3ccc09480
  Log:      ...\data\jobs\e5d3ccc09480.log
  Check:    ps2tex --status e5d3ccc09480
```

That block **is** success — the download is underway. Add `--json` to get the job
id, serial, resolved links and log path as JSON instead of parsing it.

### Command reference

| Command | What it does |
|---|---|
| `dlps2tex "<game>"` | Resolve, download, install, configure. Prints a job id. |
| `dlps2tex "<SERIAL>"` | Same, but pinned to an exact serial (e.g. `SLES-50916`). |
| `dlps2tex --list [--json]` | Texture packs already installed, with size and whether the INI is enabled. Local only, no network. |
| `dlps2tex --status <jobId> [--json]` | Progress, current step, and the tail of the job log. |
| `dlps2tex --clean [--all] [--dry-run] [--json]` | Reclaim working dirs and caches. See [Housekeeping](#housekeeping--clean). |
| `dlps2tex --version` | Which build this is. Quote it in bug reports. |
| `dlps2tex --interactive "<game>"` | Pick between regional matches yourself. Needs a human — never use from a script. |

Path overrides, if PCSX2 is not where the defaults expect:
`--textures-path`, `--gamesettings-path`, `--game-index`, `--node-id`.

> `dlps2tex` is the friendly launcher; `ps2tex.exe` is the tool. Everything above
> works against the exe directly too, but the game name then needs `--query`:
> `ps2tex --query "God of War"`.

### Chaining from a ROM download

[`dlrom`](https://github.com/Saupernova13/dl-scripts) prints a `[HANDOFF]` line
carrying the serial of the exact version it installed:

```
[HANDOFF] platform=ps2 serial=SLUS-21569 title="..." texturecmd=dlps2tex "SLUS-21569"
```

Feeding that serial in guarantees the textures match the dump you have — base vs
FES, USA vs PAL. Both tools share the same job model, so a job id and `--clean`
mean the same thing in each.

---

## Configuration

**Usually none needed.** On a standard EmuDeck install every path is derived from
`%APPDATA%` at runtime.

To override, copy `.settings.example` to `.settings` **next to `ps2tex.exe`** and
edit it. Command-line arguments always win over the file, and `.settings` is
gitignored so it never carries your paths into the repo.

| Key | Default |
|---|---|
| `TexturesPath` | `%APPDATA%\EmuDeck\Emulators\PCSX2-Qt\textures` |
| `GamesettingsPath` | `%APPDATA%\EmuDeck\Emulators\PCSX2-Qt\gamesettings` |
| `GameIndexPath` | `%APPDATA%\EmuDeck\Emulators\PCSX2-Qt\resources\GameIndex.yaml` |
| `GbatempNodeId` | `549` (the PCSX2 HD Texture Pack subforum) |

Installed packs land in `<TexturesPath>\{SERIAL}\replacements\`, and
`LoadTextureReplacements` is set in `gamesettings\{SERIAL}_{CRC}.ini`.

---

## How it finds a pack

1. **Name → serial.** PCSX2's own `GameIndex.yaml` (parsed once, then cached).
   Serials you already have installed are preferred, so `"The Sims 2"` resolves to
   *your* PAL copy rather than defaulting to the US one. If the name is unknown,
   [wiki.pcsx2.net](https://wiki.pcsx2.net/) is consulted as a fallback.
2. **Archive.org index** is checked first — a hit here skips the browser entirely
   and is much faster.
3. **GBAtemp** otherwise: the subforum is searched, candidate threads are scored
   against the serial and game name, and the highest-scoring thread is opened.
   If it yields no links, the next candidate is tried.
4. **Download** through whichever host the thread used, then extract, install and
   write the INI flags.

---

## Housekeeping — `--clean`

Each job stages its download in `data/jobs/{id}/` — the archive plus its extracted
copy, which for a texture pack is large. The worker deletes that directory on
success *and* on failure, but a worker killed outright (reboot, task manager,
power cut) never gets the chance. `--clean` sweeps up after it:

| Category | What goes |
|---|---|
| `work` | Orphaned `data/jobs/{id}/` working dirs. |
| `cache` | Everything under `data/cache/`: the GameIndex parse, the Archive.org index, gbatemp thread lists, wiki lookups, and HTML saved from threads that yielded no links. All rebuild on demand. |
| `job` | **Only with `--all`:** the `data/jobs/*.json` and `.log` history. |

**Installed texture packs are never touched** — they live in PCSX2's `textures\`
folder, not here.

Nothing belonging to a live job is removed either. `JobState` carries no pid, so
"live" means status `pending`/`running` **and** touched within the last 2 hours;
a job stuck at "running" since a crash months ago is treated as dead so its
working dir does not become permanently unreclaimable. Skips are reported:

```
[DEBUG] Job 4c0a75bb3dcb says 'running' but has not moved since 2026-06-21T21:05:04Z - treating as dead.
[INFO]  Keeping working dir for job a1b2c3d4e5f6 - it is still running.
```

---

## Background jobs

Jobs run as a fully detached subprocess: closing the parent window, an agent
timing out, or the session ending will not stop the download. State lives in
`data/jobs/<jobId>.json`, output in `data/jobs/<jobId>.log`.

Progress writes are throttled to roughly one per second, so polling every few
seconds is cheap.

| Field | Type | Description |
|---|---|---|
| `id` | string | Job identifier |
| `status` | string | `pending` \| `running` \| `complete` \| `failed` |
| `step` | string | `pending` \| `downloading` \| `extracting` \| `installing` \| `configuring` \| `complete` \| `failed` |
| `progress` | int (0–100) | Percentage through the current step |
| `bytesDownloaded` / `totalBytes` | long | Byte counts for the download step |
| `currentLink` | string | Host currently being tried |
| `servedBy` | string | Host that ultimately delivered the file |
| `message` | string | Human-readable status |
| `query`, `serial`, `gameName`, `region` | string | Resolved identity |
| `threadUrl`, `downloadLinks` | | Where it came from |
| `createdAt`, `startedAt`, `lastUpdate`, `completedAt` | ISO-8601 | Lifecycle |

---

## Troubleshooting

**"No matching texture pack found"** — no pack exists for that serial, or the
title resolved to the wrong region. Try the serial directly
(`dlps2tex "SLES-50916"`), or `--interactive` to choose between matches.

**"No download links found in any post"** — the thread uses a file host that is
not recognised yet. The full thread HTML is saved to
`data/cache/missing-links/`; please
[open an issue](https://github.com/Saupernova13/ps2-texture-grabber/issues/new?template=missing_links.yml)
with the link so the pattern can be added.

**A job sits at the same percentage** — large packs on slow hosts are simply slow.
`dlps2tex --status <jobId>` shows the log tail; the log is intentionally quiet
during the transfer, with progress going to the JSON instead.

**Textures don't appear in-game** — check `dlps2tex --list` says `INI OK? True`
for that serial, and confirm PCSX2 is loading the same `gamesettings` folder the
tool wrote to.

**Cloudflare / GBAtemp failures** — make sure Edge or Chrome is installed. The
Archive.org path still works without a browser.

**Something is behaving oddly after an interrupted run** — `dlps2tex --clean` and
retry; a half-written cache is the usual cause.

---

## Building from source

Requires the **.NET 10 SDK**.

```powershell
git clone https://github.com/Saupernova13/ps2-texture-grabber
cd ps2-texture-grabber
dotnet publish Ps2TextureGrabber.csproj -c Release -r win-x64 --self-contained -o bin\publish
.\shim\dlps2tex.cmd --version
```

The launcher finds `bin\publish\ps2tex.exe` on its own, so a checkout is usable
without copying anything around.

See [CONTRIBUTING.md](CONTRIBUTING.md) for repo layout, conventions and the
release process.

---

## Licence

[MIT](LICENSE).

This tool automates downloads from third-party community sites. Use it only for
content you are entitled to.
