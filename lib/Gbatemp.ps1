# lib/Gbatemp.ps1
# Search GBAtemp's PCSX2 HD Texture Pack subforum (node 549) via FlareSolverr,
# pick the best-matching thread for a game, and extract external download links
# (MEGA, MediaFire, Google Drive, Archive.org, Yandex) from the first post.

Add-Type -AssemblyName System.Web
. (Join-Path $PSScriptRoot 'Logging.ps1')
. (Join-Path $PSScriptRoot 'Cloudflare.ps1')

# Patterns for each host. Kept as an ordered list so we can report source host.
$script:HostPatterns = @(
    @{ Name = 'MEGA';       Regex = 'https?://mega\.nz/(?:file|folder)/[A-Za-z0-9#!_\-]+' },
    @{ Name = 'Archive';    Regex = 'https?://archive\.org/(?:details|download)/[^\s"''<>]+' },
    @{ Name = 'GDrive';     Regex = 'https?://(?:drive|docs)\.google\.com/(?:file/d/|open\?id=|uc\?[^"'' <>]*id=)[A-Za-z0-9_\-]+' },
    @{ Name = 'MediaFire';  Regex = 'https?://(?:www\.)?mediafire\.com/(?:file|folder)/[A-Za-z0-9_\-/?&=.]+' },
    @{ Name = 'Yandex';     Regex = 'https?://disk\.yandex\.(?:ru|com)/d/[A-Za-z0-9_\-]+' },
    @{ Name = 'GitHub';     Regex = 'https?://github\.com/[^/\s"''<>]+/[^/\s"''<>]+/releases/[^\s"''<>]+' }
)

# Browse the subforum index pages and score thread titles against the query.
# GBAtemp's /search/ endpoint is unreliable (DB overload from browser-level asset
# requests). Browsing the forum listing pages is cheaper and more reliable.
# A persistent FlareSolverr session is used so Cloudflare cookies are shared
# across page fetches, making pages 2+ load much faster.
#
# Returns the best-scored thread {Title, Url, ThreadId, Slug} or $null.
function Find-TextureThread {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Serial,
        [string]$GameName = $null,
        [int]$NodeId = 549,
        [int]$MaxPages = 10
    )

    $session = New-FlareSolverrSession -FlareSolverrUrl $FlareSolverrUrl
    $allThreads = @{}  # keyed by ThreadId to de-dup across pages
    $bestResult = $null
    $bestScore  = 0

    try {
        for ($page = 1; $page -le $MaxPages; $page++) {
            $pageUrl = if ($page -eq 1) {
                "https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.$NodeId/"
            } else {
                "https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.$NodeId/page-$page"
            }

            Write-Log "Browsing GBAtemp forum page $page..." "INFO"
            try {
                $resp = Invoke-FlareRequest -FlareSolverrUrl $FlareSolverrUrl -Url $pageUrl -SessionId $session
            } catch {
                Write-Log "  Page $page fetch failed: $($_.Exception.Message)" "WARN"
                continue
            }

            $pageThreads = Get-ThreadsFromHtml -Html $resp.Html
            Write-Log "  Page $page html=$($resp.Html.Length) threads=$($pageThreads.Count) allSoFar=$($allThreads.Count)" "DEBUG"
            if ($pageThreads.Count -eq 0) {
                Write-Log "  Page $page returned no thread links — stopping" "DEBUG"
                break
            }

            foreach ($t in $pageThreads) {
                if (-not $allThreads.ContainsKey($t.ThreadId)) {
                    $allThreads[$t.ThreadId] = $t
                }
            }
            Write-Log "  Page $page allThreads after add: $($allThreads.Count)" "DEBUG"

            # Score this page's threads.
            foreach ($t in $pageThreads) {
                $s = Score-Thread -Thread $t -Serial $Serial -GameName $GameName
                if ($s -gt $bestScore) {
                    $bestScore  = $s
                    $bestResult = $t
                }
            }

            # If we already have a strong match, no need to browse more pages.
            if ($bestScore -ge 50) {
                Write-Log "  Strong match found on page $page — stopping early" "DEBUG"
                break
            }
        }
    } finally {
        if ($session) { Remove-FlareSolverrSession -FlareSolverrUrl $FlareSolverrUrl -SessionId $session }
    }

    if ($bestResult -and $bestScore -gt 0) {
        Write-Log "Selected thread: $($bestResult.Title) (score $bestScore)" "SUCCESS"
        return $bestResult
    }

    Write-Log "No matching thread found in $($allThreads.Count) forum entries scanned" "WARN"
    return $null
}

# Score a thread against a serial and game name. Pure function, no side effects.
function Score-Thread {
    param(
        [Parameter(Mandatory=$true)][object]$Thread,
        [string]$Serial,
        [string]$GameName
    )
    $s = 0
    $title = $Thread.Title

    if ($Serial -and $title -match [regex]::Escape($Serial)) { $s += 100 }

    if ($GameName) {
        $gameTokens = ($GameName.ToLowerInvariant() -replace '[^a-z0-9]+', ' ').Trim() -split '\s+' |
            Where-Object { $_.Length -ge 2 }
        $titleLower = $title.ToLowerInvariant()
        foreach ($tok in $gameTokens) {
            if ($titleLower.Contains($tok)) { $s += 10 }
        }
    }

    if ($title -match '(?i)\b(request|dump|help|wanted|looking for|need|question)\b') { $s -= 50 }
    if ($title -match '(?i)\b(hd|upscaled|texture|remaster|pack|replacement|4k|2k)\b') { $s += 20 }

    return $s
}

function Get-ThreadsFromHtml {
    param([string]$Html)

    # XenForo thread links: href="/threads/{slug}.{id}/" title="..." class="..."
    # Title is sometimes in the anchor text, sometimes in a title attribute on the wrapping div.
    # We key on the href pattern and pull the inner text up to the closing </a>.
    $pattern = 'href="/threads/([A-Za-z0-9\-_.]+)\.(\d+)/"[^>]*>([^<]+)</a>'
    $matches = [regex]::Matches($Html, $pattern)

    $seen = @{}
    $threads = New-Object System.Collections.ArrayList
    foreach ($m in $matches) {
        $id = $m.Groups[2].Value
        if ($seen.ContainsKey($id)) { continue }
        $seen[$id] = $true
        $title = [System.Web.HttpUtility]::HtmlDecode($m.Groups[3].Value.Trim())
        # Some matches are pagination / last-post links rather than the title anchor —
        # filter obvious pagination / member-profile noise.
        if ($title -match '^\s*$' -or $title -match '^(Page \d+|#\d+|Last)$') { continue }
        [void]$threads.Add([PSCustomObject]@{
            Title    = $title
            ThreadId = $id
            Slug     = $m.Groups[1].Value
            Url      = "https://gbatemp.net/threads/$($m.Groups[1].Value).$id/"
        })
    }
    return ,$threads
}

# Fetch a thread page and return ranked download links from the first post.
# Result: array of {Host, Url} sorted by host reliability.
function Get-DownloadLinks {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$ThreadUrl
    )

    Write-Log "Fetching thread: $ThreadUrl" "INFO"
    $resp = Invoke-FlareRequest -FlareSolverrUrl $FlareSolverrUrl -Url $ThreadUrl
    $html = $resp.Html

    # Isolate the first post body. XenForo uses <article class="message message--post ..."
    # with a nested <div class="bbWrapper">. Grab the first bbWrapper on the page.
    $firstPost = $null
    $m = [regex]::Match($html, '<div class="bbWrapper">(.+?)</div>\s*(?:<(?:div|aside|footer))', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if ($m.Success) {
        $firstPost = $m.Groups[1].Value
    } else {
        Write-Log "Could not isolate first post; scanning full page" "WARN"
        $firstPost = $html
    }

    $hosts = New-Object System.Collections.ArrayList
    $seen = @{}
    foreach ($hp in $script:HostPatterns) {
        $hits = [regex]::Matches($firstPost, $hp.Regex)
        foreach ($h in $hits) {
            $u = $h.Value.TrimEnd('.', ',', ')', ']', '"', "'")
            if ($seen.ContainsKey($u)) { continue }
            $seen[$u] = $true
            [void]$hosts.Add([PSCustomObject]@{
                Host = $hp.Name
                Url  = $u
            })
        }
    }

    if ($hosts.Count -eq 0) {
        Write-Log "No download links found in OP" "WARN"
    } else {
        Write-Log "Found $($hosts.Count) download link(s): $(($hosts | ForEach-Object { $_.Host }) -join ', ')" "SUCCESS"
    }

    return ,$hosts
}
