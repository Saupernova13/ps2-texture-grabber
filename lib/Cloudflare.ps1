# lib/Cloudflare.ps1
# Thin wrapper over FlareSolverr, used to bypass GBAtemp's Cloudflare challenge.
#
# FlareSolverr is a separate local service (https://github.com/FlareSolverr/FlareSolverr)
# running a headless Chromium. It exposes a JSON-over-HTTP API on :8191 that accepts
# a URL, clears the challenge, and returns the page HTML. We don't auto-install it
# here — Docker vs. Windows-binary is a user preference.

. (Join-Path $PSScriptRoot 'Logging.ps1')

function Test-FlareSolverr {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl
    )
    try {
        # The /v1 endpoint only accepts POST; probe the root.
        $root = $FlareSolverrUrl -replace '/v1/?$', '/'
        $resp = Invoke-WebRequest -Uri $root -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        return $resp.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Invoke-FlareRequest {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$Url,
        [int]$MaxTimeoutMs = 60000,
        [string]$SessionId = $null
    )

    $payload = @{
        cmd        = "request.get"
        url        = $Url
        maxTimeout = $MaxTimeoutMs
    }
    if ($SessionId) { $payload.session = $SessionId }

    $body = $payload | ConvertTo-Json -Compress
    Write-Log "FlareSolverr -> GET $Url" "DEBUG"

    try {
        $resp = Invoke-RestMethod -Uri $FlareSolverrUrl `
            -Method POST `
            -ContentType 'application/json' `
            -Body $body `
            -TimeoutSec (($MaxTimeoutMs / 1000) + 10) `
            -ErrorAction Stop
    } catch {
        Write-Log "FlareSolverr request failed: $($_.Exception.Message)" "ERROR"
        Write-Log "  Is FlareSolverr running at $FlareSolverrUrl ? See README prerequisites." "ERROR"
        throw
    }

    if ($resp.status -ne 'ok') {
        $msg = if ($resp.message) { $resp.message } else { 'unknown FlareSolverr error' }
        throw "FlareSolverr returned status '$($resp.status)': $msg"
    }

    if (-not $resp.solution -or -not $resp.solution.response) {
        throw "FlareSolverr response missing solution.response"
    }

    return [PSCustomObject]@{
        Url        = $resp.solution.url
        Html       = $resp.solution.response
        Cookies    = $resp.solution.cookies
        UserAgent  = $resp.solution.userAgent
        StatusCode = $resp.solution.status
    }
}

# Create a named FlareSolverr browser session. Returns the session ID or $null on failure.
# Sessions persist Cloudflare cookies across requests, making subsequent page loads
# much lighter (no challenge re-solve, fewer parallel connections to the target host).
function New-FlareSolverrSession {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [string]$SessionId = $null
    )
    if (-not $SessionId) { $SessionId = "ps2tex-$([Guid]::NewGuid().ToString('N').Substring(0,8))" }
    try {
        $body = (@{ cmd = 'sessions.create'; session = $SessionId } | ConvertTo-Json -Compress)
        $r = Invoke-RestMethod -Uri $FlareSolverrUrl -Method POST -ContentType 'application/json' `
            -Body $body -TimeoutSec 30 -ErrorAction Stop
        if ($r.status -eq 'ok') { return $SessionId }
    } catch {
        Write-Log "Could not create FlareSolverr session: $($_.Exception.Message)" "WARN"
    }
    return $null
}

# Destroy a named FlareSolverr session (best-effort, never throws).
function Remove-FlareSolverrSession {
    param(
        [Parameter(Mandatory=$true)][string]$FlareSolverrUrl,
        [Parameter(Mandatory=$true)][string]$SessionId
    )
    try {
        $body = (@{ cmd = 'sessions.destroy'; session = $SessionId } | ConvertTo-Json -Compress)
        Invoke-RestMethod -Uri $FlareSolverrUrl -Method POST -ContentType 'application/json' `
            -Body $body -TimeoutSec 15 -ErrorAction Stop | Out-Null
    } catch {}
}
