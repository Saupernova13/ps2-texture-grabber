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
- **MEGAcmd** (`winget install MEGA.MEGAcmd`) — required for MEGA-hosted packs
  (~50% of threads).
- **7-Zip** (`winget install 7zip.7zip`) — required to extract `.7z` / `.rar`
  archives. `.zip` works without it.

## Configuration

Copy `.settings.example` to `.settings` and edit any defaults you want to override.
`.settings` is gitignored. Command-line parameters always win over `.settings`.

## How `-List` works

Reads the serial IDs present as subfolders of your `textures/` directory, looks
each one up in PCSX2's bundled `GameIndex.yaml`, and prints the game names along
with PNG count, on-disk size, and whether the matching `gamesettings` INI has
texture replacement enabled. No network access.

## Background jobs

Download jobs run as a hidden, detached `powershell.exe` process launched via
`Start-Process -WindowStyle Hidden`. The process is fully independent of the
invoking shell — closing the parent window, AI agent timeout, or session end will
not stop the download. Per-job state is written to `data/jobs/<jobId>.json` and
logs to `data/jobs/<jobId>.log`.
