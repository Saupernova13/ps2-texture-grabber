# lib/Wiki.ps1
# Authoritative fallback for serial <-> name <-> CRC lookups via wiki.pcsx2.net.
#
# Two query paths, both over FlareSolverr (uniform HTTP handling, future-proof
# against Cloudflare):
#   1. Serial -> page: GET https://wiki.pcsx2.net/{SERIAL}. The wiki treats
#      serials like SLUS-20336 as redirects to the canonical game page.
#   2. Name   -> page: opensearch API, then fetch the top result.
#
# Game pages expose per-region blocks listing Serial numbers and CRCs; we parse
# them into {Serial, Crc, Region} tuples. Pages are cached under
# data/cache/wiki/ for 7 days.

Add-Type -AssemblyName System.Web
. (Join-Path $PSScriptRoot 'Logging.ps1')
. (Join-Path $PSScriptRoot 'Cloudflare.ps1')

$script:WikiCacheTtlDays = 7

function Get-WikiCacheDir {
    param([string]$RepoRoot)
    if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
    $d = Join-Path $RepoRoot 'data\cache\wiki'
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    return $d
}

function Get-WikiCachedHtml {
    param([string]$Key, [string]$RepoRoot)
    $safe = ($Key -replace '[^A-Za-z0-9_\-\.]', '_')
    $path = Join-Path (Get-WikiCacheDir -RepoRoot $RepoRoot) "$safe.html"
    if (Test-Path -LiteralPath $path) {
        $age = (Get-Date) - (Get-Item -LiteralPath $path).LastWriteTime
        if ($age.TotalDays -lt $script:WikiCacheTtlDays) {
            return (Get-Content -LiteralPath $path -Raw)
        }
    }
    return $null
}

function Set-WikiCachedHtml {
    param([string]$Key, [string]$Html, [string]$RepoRoot)
    $safe = ($Key -replace '[^A-Za-z0-9_\-\.]', '_')
    $path = Join-Path (Get-WikiCacheDir -RepoRoot $RepoRoot) "$safe.html"
    Set-Content -LiteralPath $path -Value $Html -Encoding UTF8
}

# Fetch a wiki URL through FlareSolverr (cached). Returns the HTML string.
function Get-WikiPage {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Url,
        [string]$CacheKey = $null,
        [string]$RepoRoot
    )
    if (-not $CacheKey) { $CacheKey = $Url }
    $cached = Get-WikiCachedHtml -Key $CacheKey -RepoRoot $RepoRoot
    if ($cached) {
        Write-Log "Wiki cache hit: $CacheKey" "DEBUG"
        return $cached
    }
    $resp = Invoke-FlareRequest -FlareSolverrUrl $FlareSolverrUrl -Url $Url
    if ($resp.Html) {
        Set-WikiCachedHtml -Key $CacheKey -Html $resp.Html -RepoRoot $RepoRoot
    }
    return $resp.Html
}

# Opensearch API returns a 4-element JSON array: [query, [titles], [descs], [urls]].
# Returns array of {Title, Url} or empty array.
function Invoke-WikiOpenSearch {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Query,
        [int]$Limit = 5,
        [string]$RepoRoot
    )
    $enc = [System.Web.HttpUtility]::UrlEncode($Query)
    $apiUrl = "https://wiki.pcsx2.net/api.php?action=opensearch&search=$enc&limit=$Limit&format=json"
    $key = "opensearch_$($Query)_$Limit"
    Write-Log "Wiki opensearch: $Query" "DEBUG"
    $raw = Get-WikiPage -FlareSolverrUrl $FlareSolverrUrl -Url $apiUrl -CacheKey $key -RepoRoot $RepoRoot

    # FlareSolverr wraps JSON responses in <html><body><pre>JSON</pre></body></html>.
    $json = $raw
    $m = [regex]::Match($raw, '<pre[^>]*>(.*?)</pre>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) { $json = [System.Web.HttpUtility]::HtmlDecode($m.Groups[1].Value) }

    try {
        $arr = $json | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Log "Wiki opensearch returned non-JSON for '$Query'" "WARN"
        return @()
    }
    if (-not $arr -or $arr.Count -lt 4) { return @() }

    $titles = $arr[1]
    $urls   = $arr[3]
    $out = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $titles.Count; $i++) {
        [void]$out.Add([PSCustomObject]@{
            Title = $titles[$i]
            Url   = $urls[$i]
        })
    }
    return ,$out
}

# Normalize a string for fuzzy title scoring (same rules used in GameDB).
function ConvertTo-WikiNormalized {
    param([string]$Text)
    return (($Text.ToLowerInvariant() -replace '[^a-z0-9]+', ' ').Trim())
}

# Score an opensearch result against the query.
function Get-WikiResultScore {
    param([string]$Title, [string]$Query)
    $t = ConvertTo-WikiNormalized -Text $Title
    $q = ConvertTo-WikiNormalized -Text $Query
    if (-not $t -or -not $q) { return 0 }
    $score = 0
    if ($t -eq $q) { $score += 1000 }
    if ($t.StartsWith($q)) { $score += 300 }
    $qTokens = $q -split '\s+' | Where-Object { $_.Length -ge 2 }
    if ($qTokens.Count -gt 0) {
        $all = $true
        foreach ($tok in $qTokens) {
            if (-not ($t -match "\b$([regex]::Escape($tok))\b")) { $all = $false; break }
        }
        if ($all) { $score += 500 }
    }
    return $score
}

# Find the wiki game page for a serial or game name.
# Returns {Title, Url, Html} or $null.
function Find-WikiGamePage {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Query,
        [string]$RepoRoot
    )

    # Path 1: serial -> direct redirect.
    if ($Query -match '^[A-Z]{4}-\d{5}$') {
        $url = "https://wiki.pcsx2.net/$Query"
        Write-Log "Wiki serial redirect: $Query" "DEBUG"
        try {
            $html = Get-WikiPage -FlareSolverrUrl $FlareSolverrUrl -Url $url -CacheKey "serial_$Query" -RepoRoot $RepoRoot
            # Reject "no page" / "create the page" stubs.
            if ($html -match 'There is currently no text in this page' -or
                $html -match 'Wiki does not have an article') {
                Write-Log "Wiki has no page for serial $Query" "DEBUG"
            } else {
                $titleMatch = [regex]::Match($html, '<h1[^>]*class="firstHeading"[^>]*>([^<]+)</h1>')
                $title = if ($titleMatch.Success) { [System.Web.HttpUtility]::HtmlDecode($titleMatch.Groups[1].Value.Trim()) } else { $Query }
                return [PSCustomObject]@{ Title = $title; Url = $url; Html = $html }
            }
        } catch {
            Write-Log "Wiki serial fetch failed: $($_.Exception.Message)" "WARN"
        }
    }

    # Path 2: opensearch API.
    $results = Invoke-WikiOpenSearch -FlareSolverrUrl $FlareSolverrUrl -Query $Query -Limit 5 -RepoRoot $RepoRoot
    if (-not $results -or $results.Count -eq 0) {
        Write-Log "Wiki opensearch returned no results for '$Query'" "WARN"
        return $null
    }

    $scored = foreach ($r in $results) {
        [PSCustomObject]@{ Result = $r; Score = (Get-WikiResultScore -Title $r.Title -Query $Query) }
    }
    $best = $scored | Sort-Object -Property Score -Descending | Select-Object -First 1
    if (-not $best -or $best.Score -le 0) {
        Write-Log "Wiki opensearch: no result scored positively for '$Query'" "WARN"
        return $null
    }

    $r = $best.Result
    try {
        $html = Get-WikiPage -FlareSolverrUrl $FlareSolverrUrl -Url $r.Url -CacheKey "page_$($r.Title)" -RepoRoot $RepoRoot
    } catch {
        Write-Log "Wiki page fetch failed for $($r.Url): $($_.Exception.Message)" "ERROR"
        return $null
    }
    return [PSCustomObject]@{ Title = $r.Title; Url = $r.Url; Html = $html }
}

# Parse a wiki game page for {Serial, Crc, Region} tuples.
#
# Real wiki infobox structure (observed on Shadow_of_the_Colossus, God_of_War):
#   <td><b>Serial numbers:</b></td><td>SCUS-97399</td>
#   <td><b>Release date:</b></td>...
#   <td><b>CRCs:</b></td><td>D6385328<br>D7BF2F2D</td>
# Each region sub-table appears after a region marker like "NTSC-U" in bold.
#
# Strategy: for each "CRCs:" label in the HTML, walk backwards to find (a) the
# nearest preceding serial and (b) the nearest preceding region marker; extract
# all 8-hex-char tokens from the <td> following the label.
function Get-WikiRegionData {
    param([Parameter(Mandatory=$true)][string]$PageHtml)

    $out = New-Object System.Collections.ArrayList

    $html = $PageHtml -replace '(?is)<script[^>]*>.*?</script>', ''
    $html = $html -replace '(?is)<style[^>]*>.*?</style>', ''

    # Match a CRCs: label and capture the following <td>...</td>.
    $crcBlocks = [regex]::Matches($html, '(?is)CRCs?:\s*</b>\s*</td>\s*<td[^>]*>(.*?)</td>')
    if ($crcBlocks.Count -eq 0) {
        # No CRCs on the page — still record serials with null CRC so callers
        # at least get the name/serial mapping.
        $serials = [regex]::Matches($html, '\b([A-Z]{4}-\d{5})\b') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        foreach ($s in $serials) {
            [void]$out.Add([PSCustomObject]@{ Serial = $s; Crc = $null; Region = 'Unknown' })
        }
        return ,$out
    }

    foreach ($blk in $crcBlocks) {
        $tdContent = $blk.Groups[1].Value
        $crcs = [regex]::Matches($tdContent, '\b([0-9A-Fa-f]{8})\b') |
            ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() } |
            Select-Object -Unique
        if (-not $crcs -or $crcs.Count -eq 0) { continue }

        # Context window: up to 4000 chars before the CRC label.
        $ctxStart = [Math]::Max(0, $blk.Index - 4000)
        $ctx = $html.Substring($ctxStart, $blk.Index - $ctxStart)

        # Nearest preceding serial (last one in the context window).
        $serialMatches = [regex]::Matches($ctx, '\b([A-Z]{4}-\d{5})\b')
        $serial = if ($serialMatches.Count -gt 0) {
            $serialMatches[$serialMatches.Count - 1].Groups[1].Value
        } else { $null }

        # Region marker: last occurrence of a canonical region label in the context.
        $regionMatches = [regex]::Matches($ctx, '(?i)(NTSC-U|NTSC-J|NTSC-K|NTSC-C|NTSC-A|PAL)')
        $region = if ($regionMatches.Count -gt 0) {
            $regionMatches[$regionMatches.Count - 1].Value.ToUpperInvariant()
        } else {
            # Derive from serial prefix.
            if ($serial) {
                $r = Get-RegionFromSerial -Serial $serial
                if ($r) { $r } else { 'Unknown' }
            } else { 'Unknown' }
        }

        if (-not $serial) { continue }

        foreach ($c in $crcs) {
            [void]$out.Add([PSCustomObject]@{ Serial = $serial; Crc = $c; Region = $region })
        }
    }

    # Also capture serials with no CRC (for name->serial fallback).
    $allSerials = [regex]::Matches($html, '\b([A-Z]{4}-\d{5})\b') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
    $knownSerials = @{}
    foreach ($r in $out) { $knownSerials[$r.Serial] = $true }
    foreach ($s in $allSerials) {
        if (-not $knownSerials.ContainsKey($s)) {
            $region = Get-RegionFromSerial -Serial $s
            if (-not $region) { $region = 'Unknown' }
            [void]$out.Add([PSCustomObject]@{ Serial = $s; Crc = $null; Region = $region })
        }
    }

    # De-dup.
    $seen = @{}
    $dedup = New-Object System.Collections.ArrayList
    foreach ($r in $out) {
        $k = "$($r.Serial)|$($r.Crc)|$($r.Region)"
        if (-not $seen.ContainsKey($k)) { $seen[$k] = $true; [void]$dedup.Add($r) }
    }
    return ,$dedup
}

# Resolve a preferred region from a serial prefix.
function Get-RegionFromSerial {
    param([string]$Serial)
    switch -Regex ($Serial) {
        '^(SLUS|SCUS|PUSA)' { return 'NTSC-U' }
        '^(SLES|SCES|SLED|SCED|PAPX)' { return 'PAL' }
        '^(SLPS|SCPS|SLPM|SCPM|PAPX|SLKA|SCKA)' { return 'NTSC-J' }
        '^(SCKA|SLKA)' { return 'NTSC-K' }
        default { return $null }
    }
}

# Look up CRC for a serial via the wiki. Returns CRC (uppercase hex) or $null.
function Get-WikiCrcForSerial {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Serial,
        [string]$RepoRoot
    )

    $page = Find-WikiGamePage -FlareSolverrUrl $FlareSolverrUrl -Query $Serial -RepoRoot $RepoRoot
    if (-not $page) { return $null }

    $rows = Get-WikiRegionData -PageHtml $page.Html
    if (-not $rows -or $rows.Count -eq 0) { return $null }

    # Exact serial match first.
    $matched = $rows | Where-Object { $_.Serial -eq $Serial -and $_.Crc }
    if ($matched) {
        $crc = ($matched | Select-Object -First 1).Crc
        Write-Log "Wiki CRC for $Serial : $crc (from $($page.Title))" "SUCCESS"
        return $crc
    }

    # Fallback: any CRC in the serial's preferred region.
    $region = Get-RegionFromSerial -Serial $Serial
    if ($region) {
        $matched = $rows | Where-Object { $_.Region -eq $region -and $_.Crc } | Select-Object -First 1
        if ($matched) {
            Write-Log "Wiki CRC for $Serial (via region $region): $($matched.Crc)" "SUCCESS"
            return $matched.Crc
        }
    }

    # Last resort: any CRC on the page.
    $any = $rows | Where-Object { $_.Crc } | Select-Object -First 1
    if ($any) {
        Write-Log "Wiki CRC for $Serial (first available): $($any.Crc)" "WARN"
        return $any.Crc
    }
    return $null
}

# Look up serial for a game name via the wiki. Returns serial string or $null.
function Get-WikiSerialForName {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Name,
        [string]$PreferredRegion = 'NTSC-U',
        [string]$RepoRoot
    )

    $page = Find-WikiGamePage -FlareSolverrUrl $FlareSolverrUrl -Query $Name -RepoRoot $RepoRoot
    if (-not $page) { return $null }

    $rows = Get-WikiRegionData -PageHtml $page.Html
    if (-not $rows -or $rows.Count -eq 0) { return $null }

    $preferred = $rows | Where-Object { $_.Region -eq $PreferredRegion } | Select-Object -First 1
    if ($preferred) {
        Write-Log "Wiki serial for '$Name' ($PreferredRegion): $($preferred.Serial)" "SUCCESS"
        return $preferred.Serial
    }
    # Region priority fallback.
    foreach ($r in @('NTSC-U', 'PAL', 'NTSC-J', 'Unknown')) {
        $hit = $rows | Where-Object { $_.Region -eq $r } | Select-Object -First 1
        if ($hit) {
            Write-Log "Wiki serial for '$Name' ($r): $($hit.Serial)" "SUCCESS"
            return $hit.Serial
        }
    }
    $first = $rows | Select-Object -First 1
    if ($first) { return $first.Serial }
    return $null
}
