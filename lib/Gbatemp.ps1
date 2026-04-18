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

# Search the subforum for threads relevant to a serial / game name. Serial is tried
# first (unambiguous, present in many thread titles); falls back to the name.
# Returns the best-scored thread {Title, Url, ThreadId, Slug} or $null.
function Find-TextureThread {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Serial,
        [string]$GameName = $null,
        [int]$NodeId = 549
    )

    $queries = @($Serial)
    if ($GameName) { $queries += $GameName }

    foreach ($q in $queries) {
        Write-Log "Searching GBAtemp for '$q' in node $NodeId..." "INFO"
        $encoded = [System.Web.HttpUtility]::UrlEncode($q)
        # XenForo search: title-only within a specific node, threads only.
        $searchUrl = "https://gbatemp.net/search/?q=$encoded&c[title_only]=1&c[nodes][0]=$NodeId&o=relevance"
        $resp = Invoke-FlareRequest -FlareSolverrUrl $FlareSolverrUrl -Url $searchUrl
        $threads = Get-ThreadsFromHtml -Html $resp.Html

        if ($threads.Count -eq 0) {
            Write-Log "  No matches for '$q'" "DEBUG"
            continue
        }

        $scored = foreach ($t in $threads) {
            $s = 0
            $title = $t.Title
            if ($title -match [regex]::Escape($Serial)) { $s += 100 }
            if ($GameName) {
                $gameTokens = ($GameName.ToLowerInvariant() -replace '[^a-z0-9]+', ' ').Trim() -split '\s+'
                $titleLower = $title.ToLowerInvariant()
                foreach ($tok in $gameTokens) {
                    if ($tok.Length -ge 2 -and $titleLower.Contains($tok)) { $s += 10 }
                }
            }
            if ($title -match '(?i)\b(request|dump|help|wanted|looking for|need)\b') { $s -= 50 }
            if ($title -match '(?i)\b(hd|upscaled|texture|remaster|pack|replacement)\b') { $s += 20 }
            [PSCustomObject]@{ Thread = $t; Score = $s }
        }

        $best = $scored | Sort-Object -Property Score -Descending | Select-Object -First 1
        if ($best -and $best.Score -gt 0) {
            Write-Log "  Selected: $($best.Thread.Title) (score $($best.Score))" "SUCCESS"
            return $best.Thread
        }
    }

    Write-Log "No matching threads found on GBAtemp" "WARN"
    return $null
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
