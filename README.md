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

```cmd
:: Download a pack
dlps2tex "Dragon Ball Z Budokai Tenkaichi 3"

:: List texture packs you already have installed locally
dlps2tex --list

:: Check a background job's status
dlps2tex --status <jobId>
```

Interactive mode lets you disambiguate multiple regional matches:

```cmd
dlps2tex --query "Final Fantasy X" --interactive
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
