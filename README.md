# ps2-texture-grabber

Automation that downloads HD texture packs for PS2 games from
[GBAtemp's PCSX2 HD Texture Pack subforum](https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.549/)
and configures PCSX2 to load them.

Given a game name (e.g. `"God of War"`), the tool resolves the PCSX2 serial ID,
searches the forum, downloads the pack, installs it to the PCSX2 `textures` folder,
and enables the three INI flags required for texture replacement to take effect.

The actual download runs in a detached background worker so it survives the
invoking shell (or AI agent) terminating.

## Usage

**The download never blocks.** `dlps2tex "Game"` resolves the game and its download
links, spawns a worker, prints a **Job ID** and returns — typically in seconds. The
worker downloads, extracts, installs and configures on its own. Poll it with `--status`;
there is no flag to wait, and nothing to wait for.

```cmd
:: Download a pack -- returns a job id immediately
dlps2tex "Dragon Ball Z Budokai Tenkaichi 3"

:: A serial works too -- use the one dlrom's [HANDOFF] line gives you,
:: so the textures match the exact version installed
dlps2tex "SLUS-21569"

:: Check a background job's status
dlps2tex --status <jobId>
dlps2tex --status <jobId> --json    :: machine-readable

:: List texture packs you already have installed locally
dlps2tex --list
dlps2tex --list --json

:: Reclaim disk: abandoned job working dirs and rebuildable caches
dlps2tex --clean
dlps2tex --clean --dry-run          :: preview only
dlps2tex --clean --all              :: also clear finished job records
```

A spawn looks like this, and the shell prompt comes straight back:

```
Download job spawned.  It will continue in the background.
  Job ID:   e5d3ccc09480
  Log:      ...\data\jobs\e5d3ccc09480.log
  Check:    ps2tex --status e5d3ccc09480
```

Add `--json` to a download to get the job id, serial, resolved links and log path as JSON
instead of scraping that block.

Interactive mode lets you disambiguate multiple regional matches. It is the one mode that
expects a human at the keyboard — never use it from a script or an agent:

```cmd
dlps2tex --query "Final Fantasy X" --interactive
```

> `dlrom` uses the same job model (`dlrom --status <jobId>`, `dlrom --list`,
> `dlrom --clean`), so a job id — and a housekeeping command — means the same thing in both
> tools. See [dl-scripts](https://github.com/Saupernova13/dl-scripts).

## Housekeeping — `--clean`

Each job stages its download in `data/jobs/{id}/` (the archive plus its extracted copy).
The worker deletes that directory on success *and* on failure, but a worker that is killed
outright — reboot, task manager, power cut — never gets the chance, and texture packs are
large. `--clean` sweeps up after it, along with the caches:

| Category | What goes |
|---|---|
| `work` | Orphaned `data/jobs/{id}/` working dirs — the downloaded archive and extracted copy. |
| `cache` | Everything under `data/cache/`: the GameIndex parse, the Archive.org index, gbatemp thread lists, wiki lookups, and the HTML saved from threads that yielded no links. All rebuild on demand. |
| `job` | **Only with `--all`:** the `data/jobs/*.json` and `.log` history. |

Installed texture packs are **never** touched — they live in PCSX2's `textures\` folder,
not here.

Nothing belonging to a live job is removed. `JobState` carries no pid, so "live" means
status `pending`/`running` **and** touched within the last 2 hours; a job stuck at
"running" since a crash months ago is treated as dead, so its working dir does not become
permanently unreclaimable. Skips are reported:

```
[DEBUG] Job 4c0a75bb3dcb says 'running' but has not moved since 2026-06-21T21:05:04Z - treating as dead.
[INFO]  Keeping working dir for job a1b2c3d4e5f6 - it is still running.
```

## Prerequisites

- **7-Zip** (`winget install 7zip.7zip`) — required to extract `.7z` and `.rar`
  archives. `.zip` archives work without it via built-in extraction.
- **Edge or Chrome** — used by Playwright to bypass GBAtemp's Cloudflare challenge.
  Both are typically pre-installed on Windows 11. If neither is found, the tool
  falls back to its bundled Chromium (Cloudflare bypass may be less reliable).
- **PCSX2-Qt** installed via EmuDeck (paths default to the EmuDeck layout; override
  with `--textures-path`, `--gamesettings-path`, `--game-index` if needed).

## Configuration

Copy `.settings.example` to `.settings` and edit any defaults you want to override.
`.settings` is gitignored. Command-line parameters always win over `.settings`.

## How `--list` works

Reads the serial IDs present as subfolders of your `textures/` directory, looks
each one up in PCSX2's bundled `GameIndex.yaml`, and prints the game names along
with PNG count, on-disk size, and whether the matching `gamesettings` INI has
texture replacement enabled. The scan is live — every invocation walks the
folder, so the row count reflects exactly how many packs are installed at that
moment. No network access.

Add `--json` for machine-readable output:

```cmd
dlps2tex --list --json
```

## Dynamic resolution

Name→serial lookups use a two-tier strategy:

1. **Local first** — PCSX2's `GameIndex.yaml` for names, and existing
   `gamesettings/{SERIAL}_*.ini` for CRCs. Fast, no network.
2. **wiki.pcsx2.net fallback** — if the local source comes up empty (unknown
   title, or game never booted in PCSX2), the tool queries
   [wiki.pcsx2.net](https://wiki.pcsx2.net/) via the MediaWiki opensearch API
   and parses the per-region infobox for serials/CRCs. Results are cached under
   `data/cache/wiki/` for 7 days. Wiki-sourced results are tagged `[WIKI]` in
   the log output.

## Archive.org shortcut

Before opening a browser, the tool checks an Archive.org index for a pre-packaged
download. When a match is found the Playwright browser is skipped entirely,
making those downloads significantly faster.

## Background jobs

Download jobs run as a detached subprocess. The process is fully independent of
the invoking shell — closing the parent window, AI agent timeout, or session end
will not stop the download. Per-job state is written to `data/jobs/<jobId>.json`
and logs to `data/jobs/<jobId>.log`.

### Checking progress

Poll job status at any time:

```cmd
:: Human-readable summary with progress bar and log tail
dlps2tex --status <jobId>

:: JSON for scripts / AI agents
dlps2tex --status <jobId> --json
```

The JSON job-state shape:

| Field | Type | Description |
|---|---|---|
| `id` | string | Job identifier |
| `status` | string | `pending` \| `running` \| `complete` \| `failed` |
| `step` | string | `pending` \| `downloading` \| `extracting` \| `installing` \| `configuring` \| `complete` \| `failed` |
| `progress` | int (0–100) | Percentage through the current step |
| `bytesDownloaded` | long | Bytes received so far (downloading step) |
| `totalBytes` | long | Total archive size when known |
| `currentLink` | string | Host currently being tried (MEGA, MediaFire, etc.) |
| `servedBy` | string | Host that ultimately delivered the file |
| `lastUpdate` | ISO-8601 | Timestamp of the most recent state change |
| `message` | string | Human-readable status message |
| `query`, `serial`, `gameName` | string | Identifying metadata |
| `threadUrl`, `downloadLinks` | | Input context |
| `createdAt`, `startedAt`, `completedAt` | ISO-8601 | Lifecycle timestamps |

Progress writes are throttled to ~1-second intervals during downloads, so you
can safely poll every few seconds without thrashing disk.
