# lib/Jobs.ps1
# Job spawning and status inspection for detached download workers.
#
# A "job" is a JSON file under data/jobs/<id>.json that captures everything
# the worker needs. We launch the worker via Start-Process as a completely
# independent powershell.exe so the download survives the caller exiting.

. (Join-Path $PSScriptRoot 'Logging.ps1')

function New-JobId {
    return [Guid]::NewGuid().ToString('N').Substring(0, 12)
}

# Worker-side helper: update progress in a job state hashtable and persist.
# Any omitted parameter is left untouched. Always stamps lastUpdate.
function Update-JobProgress {
    param(
        [Parameter(Mandatory=$true)][hashtable]$State,
        [Parameter(Mandatory=$true)][string]$JobFile,
        [string]$Step,
        [Nullable[int]]$Progress,
        [string]$Message,
        [Nullable[long]]$BytesDownloaded,
        [Nullable[long]]$TotalBytes,
        [string]$CurrentLink
    )
    if ($Step)            { $State.step = $Step }
    if ($PSBoundParameters.ContainsKey('Progress'))       { $State.progress = [int]$Progress }
    if ($Message)         { $State.message = $Message }
    if ($PSBoundParameters.ContainsKey('BytesDownloaded')){ $State.bytesDownloaded = [long]$BytesDownloaded }
    if ($PSBoundParameters.ContainsKey('TotalBytes'))     { $State.totalBytes = [long]$TotalBytes }
    if ($CurrentLink)     { $State.currentLink = $CurrentLink }
    $State.lastUpdate = (Get-Date).ToString('o')

    try {
        ($State | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $JobFile -Encoding UTF8
    } catch {
        # Never throw from progress update; it's best-effort.
    }
}

function Start-DownloadJob {
    param(
        [Parameter(Mandatory=$true)][string]$RepoRoot,
        [Parameter(Mandatory=$true)][hashtable]$Job
    )

    $jobsDir = Join-Path $RepoRoot 'data\jobs'
    if (-not (Test-Path -LiteralPath $jobsDir)) {
        New-Item -ItemType Directory -Path $jobsDir -Force | Out-Null
    }

    if (-not $Job.id)        { $Job.id = New-JobId }
    if (-not $Job.status)    { $Job.status = 'pending' }
    if (-not $Job.createdAt) { $Job.createdAt = (Get-Date).ToString('o') }

    $jobFile = Join-Path $jobsDir "$($Job.id).json"
    $logFile = Join-Path $jobsDir "$($Job.id).log"
    $Job.jobFile = $jobFile
    $Job.logFile = $logFile

    ($Job | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $jobFile -Encoding UTF8
    Write-Log "Job file: $jobFile" "DEBUG"

    $worker = Join-Path $RepoRoot 'worker\Invoke-Download.ps1'
    if (-not (Test-Path -LiteralPath $worker)) {
        throw "Worker script not found: $worker"
    }

    $args = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-WindowStyle', 'Hidden',
        '-File', $worker,
        '-JobFile', $jobFile
    )

    # Start-Process without -Wait and WindowStyle Hidden creates a fully detached
    # process. Closing the parent shell does not terminate it.
    $proc = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList $args `
        -WindowStyle Hidden `
        -PassThru

    Write-Log "Spawned worker (PID $($proc.Id)) for job $($Job.id)" "SUCCESS"
    return [PSCustomObject]@{
        JobId   = $Job.id
        JobFile = $jobFile
        LogFile = $logFile
        Pid     = $proc.Id
    }
}

# Emit the raw job state as JSON to stdout. For AI agents / scripts that want
# to poll without regex-parsing the human-readable status output.
function Write-JobStatusJson {
    param(
        [Parameter(Mandatory=$true)][string]$RepoRoot,
        [Parameter(Mandatory=$true)][string]$JobId
    )
    $jobFile = Join-Path $RepoRoot "data\jobs\$JobId.json"
    if (-not (Test-Path -LiteralPath $jobFile)) {
        @{ error = "Unknown job: $JobId"; jobId = $JobId } | ConvertTo-Json
        return
    }
    Get-Content -LiteralPath $jobFile -Raw
}

function Get-JobStatus {
    param(
        [Parameter(Mandatory=$true)][string]$RepoRoot,
        [Parameter(Mandatory=$true)][string]$JobId
    )

    $jobFile = Join-Path $RepoRoot "data\jobs\$JobId.json"
    $logFile = Join-Path $RepoRoot "data\jobs\$JobId.log"

    if (-not (Test-Path -LiteralPath $jobFile)) {
        throw "Unknown job: $JobId (no $jobFile)"
    }

    $state = Get-Content -LiteralPath $jobFile -Raw | ConvertFrom-Json

    Write-Host ""
    Write-Host "Job $JobId" -ForegroundColor Yellow
    Write-Host "  Status:     $($state.status)" -ForegroundColor Cyan
    if ($state.step)         { Write-Host "  Step:       $($state.step)" -ForegroundColor Cyan }
    if ($null -ne $state.progress) {
        $bar = ('#' * [int]([Math]::Floor($state.progress / 5))).PadRight(20, '.')
        Write-Host "  Progress:   [$bar] $($state.progress)%" -ForegroundColor Cyan
    }
    Write-Host "  Query:      $($state.query)"
    Write-Host "  Serial:     $($state.serial)"
    Write-Host "  Game:       $($state.gameName)"
    Write-Host "  Created:    $($state.createdAt)"
    if ($state.startedAt)    { Write-Host "  Started:    $($state.startedAt)" }
    if ($state.lastUpdate)   { Write-Host "  Last upd:   $($state.lastUpdate)" }
    if ($state.completedAt)  { Write-Host "  Completed:  $($state.completedAt)" }
    if ($state.currentLink)  { Write-Host "  Link:       $($state.currentLink)" }
    if ($state.servedBy)     { Write-Host "  Served by:  $($state.servedBy)" }
    if ($state.totalBytes -and $state.totalBytes -gt 0) {
        $dl = [Math]::Round($state.bytesDownloaded / 1MB, 2)
        $tot = [Math]::Round($state.totalBytes / 1MB, 2)
        Write-Host "  Bytes:      $dl / $tot MB"
    }
    if ($state.message)      { Write-Host "  Message:    $($state.message)" }
    Write-Host ""

    if (Test-Path -LiteralPath $logFile) {
        Write-Host "--- tail $logFile ---" -ForegroundColor Gray
        Get-Content -LiteralPath $logFile -Tail 20 | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
    }
    return $state
}
