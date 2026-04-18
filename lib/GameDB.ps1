# lib/GameDB.ps1
# Parse PCSX2's GameIndex.yaml into a {serial, name, nameEn, region} lookup
# and resolve a user-supplied game name string to a serial ID.
#
# Why regex instead of ConvertFrom-Yaml: PowerShell 5.1 has no built-in YAML
# parser, and the user's GameIndex.yaml has a predictable flat structure.
# Avoids a PSGallery module dependency.

. (Join-Path $PSScriptRoot 'Logging.ps1')

function Import-PS2GameDB {
    param(
        [Parameter(Mandatory=$true)][string]$GameIndexPath,
        [Parameter(Mandatory=$true)][string]$CachePath
    )

    $src = Get-Item -LiteralPath $GameIndexPath -ErrorAction Stop
    if (Test-Path -LiteralPath $CachePath) {
        $cache = Get-Item -LiteralPath $CachePath
        if ($cache.LastWriteTime -ge $src.LastWriteTime) {
            Write-Log "Loading GameDB from cache: $CachePath" "DEBUG"
            try {
                return Get-Content -LiteralPath $CachePath -Raw | ConvertFrom-Json
            } catch {
                Write-Log "Cache read failed, rebuilding: $($_.Exception.Message)" "WARN"
            }
        }
    }

    Write-Log "Parsing GameIndex.yaml (this is done once and cached)..." "INFO"

    $entries = New-Object System.Collections.ArrayList
    $current = $null
    # PS2 serial prefix: 4 letters, dash, 5 digits (SLUS, SLES, SCUS, SCES, SLPS, SCPS, SLPM, SCPM, etc.)
    $serialPattern = '^([A-Z]{4}-\d{5}):\s*$'
    $fieldPattern  = '^\s{2}([a-zA-Z\-]+):\s*"?([^"]*?)"?\s*$'

    foreach ($line in [System.IO.File]::ReadLines($src.FullName)) {
        if ($line -match $serialPattern) {
            if ($current) { [void]$entries.Add([PSCustomObject]$current) }
            $current = @{
                serial  = $Matches[1]
                name    = $null
                nameEn  = $null
                region  = $null
            }
        } elseif ($current -and ($line -match $fieldPattern)) {
            switch ($Matches[1]) {
                'name'    { $current.name   = $Matches[2] }
                'name-en' { $current.nameEn = $Matches[2] }
                'region'  { $current.region = $Matches[2] }
            }
        }
    }
    if ($current) { [void]$entries.Add([PSCustomObject]$current) }

    Write-Log "Parsed $($entries.Count) GameDB entries" "SUCCESS"

    $cacheDir = Split-Path -Parent $CachePath
    if (-not (Test-Path -LiteralPath $cacheDir)) {
        New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    }
    $entries | ConvertTo-Json -Compress -Depth 3 | Set-Content -LiteralPath $CachePath -Encoding UTF8
    Write-Log "Cached GameDB to $CachePath" "DEBUG"

    return $entries
}

function Resolve-PS2Serial {
    param(
        [Parameter(Mandatory=$true)][object[]]$GameDB,
        [Parameter(Mandatory=$true)][string]$Query,
        [switch]$Interactive
    )

    # If user pasted a serial directly, accept it.
    if ($Query -match '^[A-Z]{4}-\d{5}$') {
        $entry = $GameDB | Where-Object { $_.serial -eq $Query } | Select-Object -First 1
        if ($entry) { return $entry }
        Write-Log "Serial '$Query' not found in GameDB" "WARN"
        return $null
    }

    # Normalize: lowercase, collapse all non-alphanumerics to single spaces, trim.
    # This makes "Dragon Ball Z - Budokai" and "Dragon Ball Z Budokai" equivalent.
    $normalize = {
        param($s)
        if (-not $s) { return '' }
        ($s.ToLowerInvariant() -replace '[^a-z0-9]+', ' ').Trim()
    }
    $qn = & $normalize $Query
    $qTokens = $qn -split '\s+' | Where-Object { $_ }

    $scored = foreach ($e in $GameDB) {
        $nameScore = 0
        $candidates = @($e.name, $e.nameEn) | Where-Object { $_ }
        foreach ($c in $candidates) {
            $cn = & $normalize $c
            if ($cn -eq $qn)                      { $nameScore = [Math]::Max($nameScore, 1000) }
            elseif ($cn.StartsWith("$qn "))       { $nameScore = [Math]::Max($nameScore, 700) }
            elseif ($cn.Contains(" $qn "))        { $nameScore = [Math]::Max($nameScore, 500) }
            elseif ($cn.Contains($qn))            { $nameScore = [Math]::Max($nameScore, 300) }
            else {
                # Token-set match: award partial if all query tokens appear as whole words
                $cTokens = $cn -split '\s+'
                $matched = $qTokens | Where-Object { $cTokens -contains $_ }
                if ($matched.Count -eq $qTokens.Count -and $qTokens.Count -gt 0) {
                    $nameScore = [Math]::Max($nameScore, 200)
                }
            }
        }
        if ($nameScore -gt 0) {
            # Region preference applies only when the name actually matched.
            $regionBonus = switch ($e.region) {
                'NTSC-U' { 30 }
                'PAL'    { 20 }
                'NTSC-J' { 10 }
                default  { 0 }
            }
            [PSCustomObject]@{ Entry = $e; Score = ($nameScore + $regionBonus) }
        }
    }

    $ranked = @($scored | Sort-Object -Property Score -Descending)
    if ($ranked.Count -eq 0) {
        Write-Log "No GameDB match for query: '$Query'" "ERROR"
        return $null
    }

    if ($Interactive -and $ranked.Count -gt 1 -and $ranked[0].Score -lt 1000) {
        Write-Host ""
        Write-Host "Multiple matches for '$Query':" -ForegroundColor Cyan
        $top = $ranked | Select-Object -First 10
        for ($i = 0; $i -lt $top.Count; $i++) {
            $e = $top[$i].Entry
            $display = if ($e.nameEn) { "$($e.nameEn) / $($e.name)" } else { $e.name }
            Write-Host ("  [{0}] {1} ({2}, {3})" -f ($i + 1), $display, $e.serial, $e.region)
        }
        Write-Host "Select [1-$($top.Count)] or 0 to cancel: " -NoNewline -ForegroundColor Cyan
        $sel = [int](Read-Host)
        if ($sel -lt 1 -or $sel -gt $top.Count) { return $null }
        return $top[$sel - 1].Entry
    }

    $best = $ranked[0]
    $entry = $best.Entry
    $displayName = if ($entry.nameEn) { $entry.nameEn } else { $entry.name }
    Write-Log "Resolved '$Query' -> $($entry.serial) ($displayName, $($entry.region))" "SUCCESS"
    if ($ranked.Count -gt 1 -and $best.Score -lt 1000) {
        Write-Log "  (ambiguous: $($ranked.Count) matches; use -Interactive to pick)" "WARN"
    }
    return $entry
}

function Get-GameNameBySerial {
    param(
        [Parameter(Mandatory=$true)][object[]]$GameDB,
        [Parameter(Mandatory=$true)][string]$Serial
    )
    $entry = $GameDB | Where-Object { $_.serial -eq $Serial } | Select-Object -First 1
    if (-not $entry) { return $null }
    if ($entry.nameEn) { return $entry.nameEn }
    return $entry.name
}
