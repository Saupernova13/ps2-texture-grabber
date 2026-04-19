$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
. (Join-Path $RepoRoot 'lib\Logging.ps1')
. (Join-Path $RepoRoot 'lib\GameDB.ps1')
. (Join-Path $RepoRoot 'lib\PCSX2.ps1')
. (Join-Path $RepoRoot 'lib\Cloudflare.ps1')
. (Join-Path $RepoRoot 'lib\Gbatemp.ps1')

$url = 'http://localhost:8191/v1'
Write-Host "Calling Find-TextureThread directly..."
$thread = Find-TextureThread -FlareSolverrUrl $url -Serial 'SLUS-21087' -GameName 'Mortal Kombat - Shaolin Monks'
if ($thread) {
    Write-Host "FOUND: $($thread.Title) -> $($thread.Url)"
} else {
    Write-Host "NOT FOUND"
}
