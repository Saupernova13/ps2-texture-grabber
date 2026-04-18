# worker/Invoke-Download.ps1
# Detached worker process. Reads a job file, performs download + extract +
# install + INI flag flip, writes status back to the job file.
#
# Designed to be launched via Start-Process (no console attached) so it outlives
# its parent shell / AI agent session. All output goes to data/jobs/<id>.log.

param(
    [Parameter(Mandatory=$true)][string]$JobFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web

# Find repo root relative to this script (worker/Invoke-Download.ps1 -> ..)
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)

. (Join-Path $RepoRoot 'lib\Logging.ps1')
. (Join-Path $RepoRoot 'lib\PCSX2.ps1')
. (Join-Path $RepoRoot 'lib\Hosts.ps1')

function Save-JobState {
    param([hashtable]$State, [string]$Path)
    ($State | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Load-JobState {
    param([string]$Path)
    $obj = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $h = @{}
    foreach ($p in $obj.PSObject.Properties) { $h[$p.Name] = $p.Value }
    return $h
}

function Extract-Archive {
    param([string]$ArchivePath, [string]$OutDir)
    if (-not (Test-Path -LiteralPath $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }
    $ext = [System.IO.Path]::GetExtension($ArchivePath).ToLowerInvariant()
    Write-Log "Extracting ($ext): $ArchivePath -> $OutDir" "INFO" $script:LogFile

    if ($ext -eq '.zip') {
        Expand-Archive -LiteralPath $ArchivePath -DestinationPath $OutDir -Force
        return
    }

    $sevenZip = Get-Command '7z.exe' -ErrorAction SilentlyContinue
    if (-not $sevenZip) {
        foreach ($c in @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe")) {
            if (Test-Path $c) { $sevenZip = @{ Source = $c }; break }
        }
    }
    if (-not $sevenZip) {
        throw "7-Zip not found and archive is $ext. Install via: winget install 7zip.7zip"
    }
    $p = Start-Process -FilePath $sevenZip.Source `
        -ArgumentList @('x', "-o$OutDir", '-y', $ArchivePath) `
        -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "7z.exe exited with code $($p.ExitCode)" }
}

# Locate the folder within $ExtractedDir that should map to
# textures/{SERIAL}/replacements/. Priority:
#   1) A folder named 'replacements' (any depth) — use its content.
#   2) Longest common ancestor of all PNG files.
function Find-TextureRoot {
    param([string]$ExtractedDir)

    $replacements = Get-ChildItem -Path $ExtractedDir -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ieq 'replacements' } |
        Sort-Object { $_.FullName.Length } | Select-Object -First 1
    if ($replacements) { return $replacements.FullName }

    $pngs = Get-ChildItem -Path $ExtractedDir -Filter *.png -Recurse -File -ErrorAction SilentlyContinue
    if (-not $pngs) { throw "No PNG files found in extracted archive" }

    $dirs = $pngs | ForEach-Object { Split-Path -Parent $_.FullName } | Select-Object -Unique
    if ($dirs.Count -eq 1) { return $dirs[0] }

    # Longest common path prefix of directory paths.
    $split = $dirs | ForEach-Object { $_ -split '[\\/]' }
    $min = ($split | ForEach-Object { $_.Count } | Measure-Object -Minimum).Minimum
    $common = @()
    for ($i = 0; $i -lt $min; $i++) {
        $seg = $split[0][$i]
        $all = $true
        foreach ($s in $split) { if ($s[$i] -ne $seg) { $all = $false; break } }
        if ($all) { $common += $seg } else { break }
    }
    return ($common -join '\')
}

function Install-TextureFiles {
    param(
        [string]$SourceRoot,
        [string]$TargetRoot
    )
    if (-not (Test-Path -LiteralPath $TargetRoot)) {
        New-Item -ItemType Directory -Path $TargetRoot -Force | Out-Null
    }
    $copied = 0
    $files = Get-ChildItem -Path $SourceRoot -File -Recurse -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        # Preserve folder structure relative to $SourceRoot.
        $rel = $f.FullName.Substring($SourceRoot.Length).TrimStart('\','/')
        $dst = Join-Path $TargetRoot $rel
        $dstDir = Split-Path -Parent $dst
        if (-not (Test-Path -LiteralPath $dstDir)) {
            New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
        $copied++
    }
    Write-Log "Copied $copied file(s) to $TargetRoot" "SUCCESS" $script:LogFile
    return $copied
}

# ============================== main ==============================

$script:JobFile = $JobFile
$state = Load-JobState -Path $JobFile
$script:LogFile = $state.logFile
if (-not $script:LogFile) {
    $script:LogFile = [System.IO.Path]::ChangeExtension($JobFile, '.log')
    $state.logFile = $script:LogFile
}

Write-Log "=== Worker started for job $($state.id) ===" "INFO" $script:LogFile
Write-Log "Query:  $($state.query)" "INFO" $script:LogFile
Write-Log "Serial: $($state.serial)" "INFO" $script:LogFile

$state.status = 'running'
$state.startedAt = (Get-Date).ToString('o')
Save-JobState -State $state -Path $JobFile

try {
    $jobDir = Join-Path (Split-Path -Parent $JobFile) $state.id
    if (-not (Test-Path -LiteralPath $jobDir)) {
        New-Item -ItemType Directory -Path $jobDir -Force | Out-Null
    }

    # 1. Download. Pick archive extension from the final URL if we can infer it;
    # otherwise start as .bin and rename after we know the extension.
    $archivePath = Join-Path $jobDir 'archive.bin'
    $links = @($state.downloadLinks)
    if ($links.Count -eq 0) { throw "Job has no download links" }

    # Re-shape links (they came through JSON as PSCustomObject).
    $linkObjects = foreach ($l in $links) {
        if ($l -is [hashtable] -or $l -is [System.Collections.IDictionary]) {
            [PSCustomObject]@{ Host = $l.Host; Url = $l.Url }
        } else {
            [PSCustomObject]@{ Host = $l.Host; Url = $l.Url }
        }
    }

    $servedBy = Invoke-HostDownload -Links $linkObjects -OutFile $archivePath
    $state.servedBy = $servedBy

    # Rename archive to actual extension when discoverable.
    $resolved = Get-Item -LiteralPath $archivePath
    $urlForHost = ($linkObjects | Where-Object { $_.Host -eq $servedBy } | Select-Object -First 1).Url
    if ($urlForHost -match '\.(zip|7z|rar|tar\.gz|tgz)(\?|$)') {
        $ext = $Matches[1]
        $renamed = [System.IO.Path]::ChangeExtension($resolved.FullName, ".$ext")
        if ($renamed -ne $resolved.FullName) {
            Move-Item -LiteralPath $resolved.FullName -Destination $renamed -Force
            $archivePath = $renamed
        }
    } else {
        # Sniff file header for zip/7z.
        $bytes = [byte[]]::new(6)
        $fs = [System.IO.File]::OpenRead($archivePath)
        try { [void]$fs.Read($bytes, 0, 6) } finally { $fs.Close() }
        $sig = ($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join ''
        if     ($sig.StartsWith('504B03')) { $ext = 'zip' }
        elseif ($sig.StartsWith('377ABCAF'))  { $ext = '7z' }
        elseif ($sig.StartsWith('526172'))    { $ext = 'rar' }
        else { $ext = 'zip' }
        $renamed = [System.IO.Path]::ChangeExtension($archivePath, ".$ext")
        if ($renamed -ne $archivePath) {
            Move-Item -LiteralPath $archivePath -Destination $renamed -Force
            $archivePath = $renamed
        }
    }

    # 2. Extract.
    $extractDir = Join-Path $jobDir 'extracted'
    Extract-Archive -ArchivePath $archivePath -OutDir $extractDir

    # 3. Find texture root within extracted tree.
    $textureRoot = Find-TextureRoot -ExtractedDir $extractDir
    Write-Log "Texture source root: $textureRoot" "INFO" $script:LogFile

    # 4. Install into textures/{SERIAL}/replacements/.
    $targetRoot = Join-Path $state.texturesPath (Join-Path $state.serial 'replacements')
    $copied = Install-TextureFiles -SourceRoot $textureRoot -TargetRoot $targetRoot

    # 5. Flip INI flags.
    $crc = Resolve-GameCrc -Serial $state.serial -GamesettingsPath $state.gamesettingsPath
    $iniPath = Get-IniPath -Serial $state.serial -Crc $crc -GamesettingsPath $state.gamesettingsPath
    [void](Set-TextureIni -IniPath $iniPath)
    Write-Log "INI at: $iniPath" "INFO" $script:LogFile

    $state.status = 'complete'
    $state.completedAt = (Get-Date).ToString('o')
    $state.message = "Installed $copied texture file(s) for $($state.serial) via $servedBy"
    Save-JobState -State $state -Path $JobFile
    Write-Log "=== Job complete ===" "SUCCESS" $script:LogFile

} catch {
    $state.status = 'failed'
    $state.completedAt = (Get-Date).ToString('o')
    $state.message = $_.Exception.Message
    Save-JobState -State $state -Path $JobFile
    Write-Log "=== Job failed: $($_.Exception.Message) ===" "ERROR" $script:LogFile
    Write-Log $_.ScriptStackTrace "DEBUG" $script:LogFile
    exit 1
}
