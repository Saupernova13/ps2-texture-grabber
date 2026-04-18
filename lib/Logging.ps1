# lib/Logging.ps1
# Shared logging + settings loader, modeled on anime-grabber / game-grabber.

function Write-Log {
    param(
        [string]$Message,
        [string]$Level = "INFO",
        [string]$LogFile = $null,
        [string]$Step = $null
    )
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $stepTag = if ($Step) { "[$Step] " } else { "" }
    $line = "[$timestamp] [$Level] $stepTag$Message"
    $color = switch ($Level) {
        "INFO"    { "Cyan" }
        "SUCCESS" { "Green" }
        "WARN"    { "Yellow" }
        "ERROR"   { "Red" }
        "DEBUG"   { "Gray" }
        default   { "White" }
    }
    Write-Host $line -ForegroundColor $color
    if ($LogFile) {
        try {
            Add-Content -Path $LogFile -Value $line -ErrorAction Stop
        } catch {
            # swallow — logging must not throw
        }
    }
}

function Import-Settings {
    param(
        [string]$SettingsFile
    )
    $settings = @{}
    if (-not (Test-Path $SettingsFile)) { return $settings }
    Get-Content $SettingsFile | Where-Object { $_ -notmatch '^\s*#' -and $_ -match '=' } | ForEach-Object {
        $key, $value = $_ -split '=', 2
        $settings[$key.Trim()] = $value.Trim()
    }
    return $settings
}
