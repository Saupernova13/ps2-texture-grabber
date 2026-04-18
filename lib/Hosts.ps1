# lib/Hosts.ps1
# Per-host download dispatchers. Each Invoke-*Download function takes a URL and
# a target file path; on failure it throws so the caller can fall through to the
# next-ranked link.

. (Join-Path $PSScriptRoot 'Logging.ps1')

function Get-DispatcherForHost {
    param([string]$HostName)
    switch ($HostName) {
        'MEGA'      { return 'Invoke-MegaDownload' }
        'Archive'   { return 'Invoke-DirectDownload' }
        'GitHub'    { return 'Invoke-DirectDownload' }
        'GDrive'    { return 'Invoke-GDriveDownload' }
        'MediaFire' { return 'Invoke-MediaFireDownload' }
        'Yandex'    { return 'Invoke-YandexDownload' }
        default     { return 'Invoke-DirectDownload' }
    }
}

function Invoke-DirectDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$OutFile,
        [scriptblock]$ProgressCallback = $null,
        [string]$HostName = 'HTTP'
    )
    Write-Log "Direct HTTP download: $Url" "INFO"
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # archive.org /details/ links need a browser redirect to /download/; normalise.
    $final = $Url
    if ($Url -match '^https?://archive\.org/details/(.+)$') {
        $final = "https://archive.org/download/" + $Matches[1]
        Write-Log "  Archive details->download: $final" "DEBUG"
    }

    # Stream with HttpWebRequest so we can report byte progress to the worker.
    try {
        $req = [System.Net.HttpWebRequest]::Create($final)
        $req.AllowAutoRedirect = $true
        $req.UserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/1.0'
        $req.Timeout = 60000
        $req.ReadWriteTimeout = 120000
        $resp = $req.GetResponse()
        $total = [long]$resp.ContentLength
        $src = $resp.GetResponseStream()
        $dst = [System.IO.File]::Open($OutFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
        try {
            $buf = New-Object byte[] 81920
            $read = 0; $last = [Environment]::TickCount; $lastPct = -1
            while (($n = $src.Read($buf, 0, $buf.Length)) -gt 0) {
                $dst.Write($buf, 0, $n)
                $read += $n
                if ($total -gt 0 -and $ProgressCallback) {
                    $pct = [int](($read * 100) / $total)
                    $now = [Environment]::TickCount
                    if ($pct -ne $lastPct -and ($now - $last) -ge 1000) {
                        try { & $ProgressCallback $read $total $pct $HostName } catch {}
                        $last = $now; $lastPct = $pct
                    }
                }
            }
            if ($ProgressCallback -and $total -gt 0) {
                try { & $ProgressCallback $read $total 100 $HostName } catch {}
            }
        } finally {
            $dst.Close(); $src.Close(); $resp.Close()
        }
    } catch {
        throw "Direct download failed for $final : $($_.Exception.Message)"
    }

    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        throw "Direct download produced empty file for $final"
    }
}

function Invoke-MegaDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$OutFile
    )
    Write-Log "MEGA download: $Url" "INFO"
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # Find mega-get. MEGAcmd installs to %ProgramFiles%\MEGAcmd or %LocalAppData%\MEGAcmd.
    $mega = Get-Command 'mega-get.bat' -ErrorAction SilentlyContinue
    if (-not $mega) { $mega = Get-Command 'mega-get' -ErrorAction SilentlyContinue }
    if (-not $mega) {
        $candidates = @(
            "$env:ProgramFiles\MEGAcmd\mega-get.bat",
            "$env:LocalAppData\MEGAcmd\mega-get.bat"
        )
        foreach ($c in $candidates) { if (Test-Path $c) { $mega = @{ Source = $c }; break } }
    }
    if (-not $mega) {
        throw "MEGAcmd not found. Install it: winget install MEGA.MEGAcmd"
    }
    $megaPath = $mega.Source

    # mega-get downloads to a folder; point it at the dir.
    # Use Start-Process so we don't tie up the pipeline.
    $p = Start-Process -FilePath $megaPath -ArgumentList @($Url, $dir) `
        -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "mega-get exited with code $($p.ExitCode)"
    }

    # mega-get names the file after the MEGA filename; pick the newest file in dir
    # that didn't exist before. Caller can rename if needed.
    $newest = Get-ChildItem -Path $dir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $newest) { throw "MEGA download produced no file in $dir" }
    if ($newest.FullName -ne $OutFile) {
        Move-Item -LiteralPath $newest.FullName -Destination $OutFile -Force
    }
}

function Invoke-GDriveDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$OutFile
    )
    Write-Log "Google Drive download: $Url" "INFO"
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # Extract file ID from common URL shapes.
    $fileId = $null
    if ($Url -match 'drive\.google\.com/file/d/([A-Za-z0-9_\-]+)') { $fileId = $Matches[1] }
    elseif ($Url -match '[?&]id=([A-Za-z0-9_\-]+)')               { $fileId = $Matches[1] }
    if (-not $fileId) { throw "Could not extract Google Drive file ID from $Url" }

    # Prefer gdown if available — handles large-file virus warnings for us.
    $gdown = Get-Command 'gdown' -ErrorAction SilentlyContinue
    if ($gdown) {
        $p = Start-Process -FilePath $gdown.Source `
            -ArgumentList @('--id', $fileId, '-O', $OutFile) `
            -NoNewWindow -Wait -PassThru
        if ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $OutFile)) { return }
        Write-Log "gdown failed (exit $($p.ExitCode)); falling back to HTTP" "WARN"
    }

    # Fallback: confirmation-token dance for large files.
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $first = Invoke-WebRequest -Uri "https://drive.google.com/uc?export=download&id=$fileId" -WebSession $session -UseBasicParsing
    $finalUrl = "https://drive.google.com/uc?export=download&id=$fileId"

    # Look for the confirm token on the virus-scan warning page.
    $tok = [regex]::Match($first.Content, 'confirm=([0-9A-Za-z_\-]+)')
    if ($tok.Success) {
        $finalUrl = "https://drive.google.com/uc?export=download&confirm=$($tok.Groups[1].Value)&id=$fileId"
    }

    Invoke-WebRequest -Uri $finalUrl -WebSession $session -OutFile $OutFile -UseBasicParsing
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        throw "Google Drive download produced empty file (may be quota-limited or needs gdown)"
    }
}

function Invoke-MediaFireDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$OutFile
    )
    Write-Log "MediaFire download: $Url" "INFO"
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # MediaFire serves an HTML page with a 'Download file' anchor that has the real CDN URL.
    $page = Invoke-WebRequest -Uri $Url -UseBasicParsing -Headers @{
        'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Edge/120.0'
    }

    $m = [regex]::Match($page.Content, 'aria-label="Download file"\s+href="([^"]+)"')
    if (-not $m.Success) { $m = [regex]::Match($page.Content, 'href="(https?://download[0-9]+\.mediafire\.com/[^"]+)"') }
    if (-not $m.Success) {
        throw "Could not find MediaFire direct URL on $Url"
    }
    $direct = $m.Groups[1].Value
    Write-Log "  MediaFire direct URL: $direct" "DEBUG"

    Invoke-WebRequest -Uri $direct -OutFile $OutFile -UseBasicParsing
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        throw "MediaFire download produced empty file"
    }
}

function Invoke-YandexDownload {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$OutFile
    )
    Write-Log "Yandex.Disk download: $Url" "INFO"
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    # Yandex's public API resolves a public_key (the disk.yandex link) to a short-lived direct URL.
    $apiUrl = "https://cloud-api.yandex.net/v1/disk/public/resources/download?public_key=$([System.Web.HttpUtility]::UrlEncode($Url))"
    $resp = Invoke-RestMethod -Uri $apiUrl -ErrorAction Stop
    if (-not $resp.href) { throw "Yandex API did not return a download href" }

    Invoke-WebRequest -Uri $resp.href -OutFile $OutFile -UseBasicParsing
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        throw "Yandex download produced empty file"
    }
}

# Try each link in order until one succeeds. Returns the host that served the file.
# Optional $ProgressCallback: invoked as `& cb bytesDownloaded totalBytes pct hostName`
# whenever bytes flow. Only Invoke-DirectDownload currently reports progress; other
# dispatchers finish atomically from the caller's point of view.
function Invoke-HostDownload {
    param(
        [Parameter(Mandatory=$true)][object[]]$Links,
        [Parameter(Mandatory=$true)][string]$OutFile,
        [scriptblock]$ProgressCallback = $null
    )
    $errors = New-Object System.Collections.ArrayList
    foreach ($link in $Links) {
        $dispatcher = Get-DispatcherForHost -HostName $link.Host
        try {
            Write-Log "Trying $($link.Host): $($link.Url)" "INFO"
            if ($dispatcher -eq 'Invoke-DirectDownload') {
                & $dispatcher -Url $link.Url -OutFile $OutFile `
                    -ProgressCallback $ProgressCallback -HostName $link.Host
            } else {
                & $dispatcher -Url $link.Url -OutFile $OutFile
            }
            Write-Log "Download succeeded via $($link.Host)" "SUCCESS"
            return $link.Host
        } catch {
            $msg = "$($link.Host) failed: $($_.Exception.Message)"
            Write-Log $msg "WARN"
            [void]$errors.Add($msg)
        }
    }
    throw "All download hosts failed:`n  " + ($errors -join "`n  ")
}
