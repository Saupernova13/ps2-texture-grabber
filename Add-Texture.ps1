# Add-Texture.ps1
# Download HD texture packs for PS2 games from GBAtemp's PCSX2 HD Texture Pack
# subforum and configure PCSX2 to load them.
#
# Usage:
#   .\Add-Texture.ps1 -Query "Dragon Ball Z Budokai Tenkaichi 3"
#   .\Add-Texture.ps1 -List
#   .\Add-Texture.ps1 -Status <jobId>

param(
    [Parameter(Mandatory=$false)][string]$Query,
    [Parameter(Mandatory=$false)][switch]$List,
    [Parameter(Mandatory=$false)][string]$Status,
    [Parameter(Mandatory=$false)][switch]$Interactive,
    [Parameter(Mandatory=$false)][string]$TexturesPath,
    [Parameter(Mandatory=$false)][string]$GamesettingsPath,
    [Parameter(Mandatory=$false)][string]$GameIndexPath,
    [Parameter(Mandatory=$false)][string]$FlareSolverrUrl,
    [Parameter(Mandatory=$false)][int]$NodeId = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web

$script:RepoRoot = Split-Path -Parent $PSCommandPath
. (Join-Path $RepoRoot 'lib\Logging.ps1')

# Load .settings (command-line args win).
$settingsFile = Join-Path $RepoRoot '.settings'
$settings = Import-Settings -SettingsFile $settingsFile

if (-not $TexturesPath)      { $TexturesPath      = $settings['TexturesPath'] }
if (-not $GamesettingsPath)  { $GamesettingsPath  = $settings['GamesettingsPath'] }
if (-not $GameIndexPath)     { $GameIndexPath     = $settings['GameIndexPath'] }
if (-not $FlareSolverrUrl)   { $FlareSolverrUrl   = $settings['FlareSolverrUrl'] }
if ($NodeId -eq 0) {
    if ($settings['GbatempNodeId']) { $NodeId = [int]$settings['GbatempNodeId'] } else { $NodeId = 549 }
}
if (-not $TexturesPath)     { $TexturesPath     = "$env:APPDATA\EmuDeck\Emulators\PCSX2-Qt\textures" }
if (-not $GamesettingsPath) { $GamesettingsPath = "$env:APPDATA\EmuDeck\Emulators\PCSX2-Qt\gamesettings" }
if (-not $GameIndexPath)    { $GameIndexPath    = "$env:APPDATA\EmuDeck\Emulators\PCSX2-Qt\resources\GameIndex.yaml" }
if (-not $FlareSolverrUrl)  { $FlareSolverrUrl  = 'http://localhost:8191/v1' }

$cachePath = Join-Path $RepoRoot 'data\cache\gamedb.json'

# ---------- -Status ----------
if ($Status) {
    . (Join-Path $RepoRoot 'lib\Jobs.ps1')
    [void](Get-JobStatus -RepoRoot $RepoRoot -JobId $Status)
    return
}

# ---------- -List ----------
if ($List) {
    . (Join-Path $RepoRoot 'lib\GameDB.ps1')
    . (Join-Path $RepoRoot 'lib\PCSX2.ps1')

    Write-Log "Scanning installed texture packs at $TexturesPath" "INFO"
    $db = Import-PS2GameDB -GameIndexPath $GameIndexPath -CachePath $cachePath
    $packs = Get-InstalledTexturePacks -TexturesPath $TexturesPath -GamesettingsPath $GamesettingsPath -GameDB $db

    if (-not $packs -or $packs.Count -eq 0) {
        Write-Log "No texture packs found under $TexturesPath" "WARN"
        return
    }

    Write-Host ""
    Write-Host "Installed PS2 texture packs:" -ForegroundColor Cyan
    Write-Host ""
    $packs | Format-Table -AutoSize GameName, Serial, TextureCount, SizeMB, IniConfigured
    Write-Host "Total: $($packs.Count) pack(s)" -ForegroundColor Green
    return
}

# ---------- download ----------
if (-not $Query) {
    Write-Log "Missing -Query. Try: .\Add-Texture.ps1 -Query 'God of War'  or  -List  or  -Status <id>" "ERROR"
    exit 1
}

. (Join-Path $RepoRoot 'lib\GameDB.ps1')
. (Join-Path $RepoRoot 'lib\PCSX2.ps1')
. (Join-Path $RepoRoot 'lib\Cloudflare.ps1')
. (Join-Path $RepoRoot 'lib\Gbatemp.ps1')
. (Join-Path $RepoRoot 'lib\Jobs.ps1')

Write-Log "Starting texture grabber" "INFO"
Write-Log "  Query:        $Query"         "INFO"
Write-Log "  Textures:     $TexturesPath"  "DEBUG"
Write-Log "  Gamesettings: $GamesettingsPath" "DEBUG"
Write-Log "  FlareSolverr: $FlareSolverrUrl"  "DEBUG"

# 1. Resolve name -> serial.
$db = Import-PS2GameDB -GameIndexPath $GameIndexPath -CachePath $cachePath
$entry = Resolve-PS2Serial -GameDB $db -Query $Query -Interactive:$Interactive
if (-not $entry) { Write-Log "Could not resolve '$Query' to a PS2 serial" "ERROR"; exit 1 }

$gameName = if ($entry.nameEn) { $entry.nameEn } else { $entry.name }

# 2. Check FlareSolverr is reachable.
if (-not (Test-FlareSolverr -FlareSolverrUrl $FlareSolverrUrl)) {
    Write-Log "FlareSolverr is not reachable at $FlareSolverrUrl" "ERROR"
    Write-Log "Install/start it (see https://github.com/FlareSolverr/FlareSolverr) and retry." "ERROR"
    Write-Log "Quick Docker: docker run -d -p 8191:8191 ghcr.io/flaresolverr/flaresolverr:latest" "INFO"
    exit 1
}
Write-Log "FlareSolverr reachable" "DEBUG"

# 3. Find the thread on GBAtemp.
$thread = Find-TextureThread -FlareSolverrUrl $FlareSolverrUrl -Serial $entry.serial -GameName $gameName -NodeId $NodeId
if (-not $thread) {
    Write-Log "No matching thread for $($entry.serial) '$gameName'" "ERROR"
    Write-Log "Try browsing manually: https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.$NodeId/" "INFO"
    exit 1
}

# 4. Extract download links from the thread's first post.
$links = Get-DownloadLinks -FlareSolverrUrl $FlareSolverrUrl -ThreadUrl $thread.Url
if (-not $links -or $links.Count -eq 0) {
    Write-Log "No usable download links found in thread: $($thread.Url)" "ERROR"
    exit 1
}

# 5. Write job file and spawn detached worker.
$job = @{
    query            = $Query
    serial           = $entry.serial
    gameName         = $gameName
    region           = $entry.region
    threadUrl        = $thread.Url
    threadTitle      = $thread.Title
    downloadLinks    = @($links)
    texturesPath     = $TexturesPath
    gamesettingsPath = $GamesettingsPath
    flareSolverrUrl  = $FlareSolverrUrl
}

$res = Start-DownloadJob -RepoRoot $RepoRoot -Job $job

Write-Host ""
Write-Host "Download job spawned. It will continue in the background." -ForegroundColor Green
Write-Host "  Job ID:   $($res.JobId)" -ForegroundColor Yellow
Write-Host "  Log:      $($res.LogFile)"
Write-Host "  Check:    .\Add-Texture.ps1 -Status $($res.JobId)"
Write-Host ""

return [PSCustomObject]@{
    JobId   = $res.JobId
    Serial  = $entry.serial
    Game    = $gameName
    Thread  = $thread.Url
    Links   = $links
    LogFile = $res.LogFile
}
