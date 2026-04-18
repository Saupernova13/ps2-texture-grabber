# ps2-texture-grabber

PowerShell automation that downloads HD texture packs for PS2 games from
[GBAtemp's PCSX2 HD Texture Pack subforum](https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.549/)
and configures PCSX2 to load them.

Given a game name (e.g. `"God of War"`), the script resolves the PCSX2 serial ID,
searches the forum, downloads the pack, installs it to the PCSX2 `textures` folder,
and enables the three INI flags required for texture replacement to take effect.

The actual download runs in a detached background worker so it survives the
invoking shell (or AI agent) terminating.

## Usage

```powershell
# Download a pack
.\Add-Texture.ps1 -Query "Dragon Ball Z Budokai Tenkaichi 3"

# List texture packs you already have installed locally
.\Add-Texture.ps1 -List

# Check a background job's status
.\Add-Texture.ps1 -Status <jobId>
```

Interactive mode lets you disambiguate multiple regional matches:

```powershell
.\Add-Texture.ps1 -Query "Final Fantasy X" -Interactive
```

## Prerequisites

- **PowerShell 5.1+** (Windows default) or PowerShell 7.
- **PCSX2-Qt** installed via EmuDeck (paths default to the EmuDeck layout; override
  with `-TexturesPath`, `-GamesettingsPath`, `-GameIndexPath` if needed).
- **FlareSolverr** running locally on `http://localhost:8191` — bypasses GBAtemp's
  Cloudflare challenge. The script will prompt you to install it on first run if
  it isn't reachable. See <https://github.com/FlareSolverr/FlareSolverr>.
- **MEGAcmd** — required for MEGA-hosted packs (~50% of threads). No winget
  package exists; install from the official MEGA site:
  - <https://mega.io/cmd> — direct Windows installer (`MEGAcmdSetup64.exe`)
  - <https://github.com/meganz/MEGAcmd/releases> — GitHub mirror
- **7-Zip** (`winget install 7zip.7zip`) — required to extract `.7z` / `.rar`
  archives. `.zip` works without it.

## Configuration

Copy `.settings.example` to `.settings` and edit any defaults you want to override.
`.settings` is gitignored. Command-line parameters always win over `.settings`.

## How `-List` works

Reads the serial IDs present as subfolders of your `textures/` directory, looks
each one up in PCSX2's bundled `GameIndex.yaml`, and prints the game names along
with PNG count, on-disk size, and whether the matching `gamesettings` INI has
texture replacement enabled. The scan is live — every invocation walks the
folder, so the row count reflects exactly how many packs are installed at that
moment. No network access.

Add `-Json` for machine-readable output:

```powershell
.\Add-Texture.ps1 -List -Json | ConvertFrom-Json
```

## Dynamic resolution

Name→serial and serial→CRC lookups use a two-tier strategy:

1. **Local first** — PCSX2's `GameIndex.yaml` for names, and existing
   `gamesettings/{SERIAL}_*.ini` for CRCs. Fast, no network.
2. **wiki.pcsx2.net fallback** — if the local source comes up empty (unknown
   title, or game never booted in PCSX2 so no INI exists), the script queries
   [wiki.pcsx2.net](https://wiki.pcsx2.net/) via the MediaWiki opensearch API
   and parses the per-region infobox for serials/CRCs. Results are cached under
   `data/cache/wiki/` for 7 days. Wiki-sourced results are tagged `[WIKI]` in
   the log output.

The wiki fallback requires FlareSolverr to be reachable.

## Background jobs

Download jobs run as a hidden, detached `powershell.exe` process launched via
`Start-Process -WindowStyle Hidden`. The process is fully independent of the
invoking shell — closing the parent window, AI agent timeout, or session end will
not stop the download. Per-job state is written to `data/jobs/<jobId>.json` and
logs to `data/jobs/<jobId>.log`.

### Checking progress

Poll job status at any time:

```powershell
# Human-readable summary with progress bar and log tail
.\Add-Texture.ps1 -Status <jobId>

# JSON for scripts / AI agents
.\Add-Texture.ps1 -Status <jobId> -Json
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
