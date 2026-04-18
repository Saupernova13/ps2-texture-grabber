# lib/PCSX2.ps1
# PCSX2-specific operations: CRC lookup, INI editing, texture folder install,
# and enumeration of locally installed texture packs.

. (Join-Path $PSScriptRoot 'Logging.ps1')

# Resolve the CRC for a serial.
# Priority:
#   1. Existing gamesettings/{SERIAL}_*.ini — fastest, no network.
#   2. Wiki lookup (optional) — handles games the user has never booted.
#   3. $null → caller writes unscoped "{SERIAL}.ini" (PCSX2 honours it as a
#      per-serial fallback that applies to all dumps of the game).
function Resolve-GameCrc {
    param(
        [Parameter(Mandatory=$true)][string]$Serial,
        [Parameter(Mandatory=$true)][string]$GamesettingsPath,
        [string]$FlareSolverrUrl = $null,
        [string]$RepoRoot = $null
    )
    $pattern = Join-Path $GamesettingsPath ("{0}_*.ini" -f $Serial)
    $match = Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($match) {
        # Filename format: {SERIAL}_{CRC}.ini
        if ($match.BaseName -match '^[A-Z]{4}-\d{5}_([0-9A-Fa-f]{8})$') {
            return $Matches[1].ToUpperInvariant()
        }
    }

    # Wiki fallback.
    if ($FlareSolverrUrl) {
        try {
            $wikiLib = Join-Path $PSScriptRoot 'Wiki.ps1'
            if (Test-Path -LiteralPath $wikiLib) {
                . $wikiLib
                if (Test-FlareSolverr -FlareSolverrUrl $FlareSolverrUrl) {
                    Write-Log "[WIKI] Looking up CRC for $Serial on wiki.pcsx2.net..." "INFO"
                    $crc = Get-WikiCrcForSerial -FlareSolverrUrl $FlareSolverrUrl -Serial $Serial -RepoRoot $RepoRoot
                    if ($crc) { return $crc.ToUpperInvariant() }
                }
            }
        } catch {
            Write-Log "Wiki CRC lookup failed: $($_.Exception.Message)" "WARN"
        }
    }

    return $null
}

# Compute the absolute INI path for a {Serial, CRC} pair.
# CRC may be $null, in which case the unscoped form is used.
function Get-IniPath {
    param(
        [Parameter(Mandatory=$true)][string]$Serial,
        [string]$Crc,
        [Parameter(Mandatory=$true)][string]$GamesettingsPath
    )
    if ($Crc) {
        return Join-Path $GamesettingsPath ("{0}_{1}.ini" -f $Serial, $Crc.ToUpperInvariant())
    }
    return Join-Path $GamesettingsPath ("{0}.ini" -f $Serial)
}

# Ensure [EmuCore/GS] contains LoadTextureReplacements=true,
# LoadTextureReplacementsAsync=true, PrecacheTextureReplacements=true.
# Preserves all other sections and keys. Idempotent.
function Set-TextureIni {
    param(
        [Parameter(Mandatory=$true)][string]$IniPath
    )

    $required = @{
        'LoadTextureReplacements'         = 'true'
        'LoadTextureReplacementsAsync'    = 'true'
        'PrecacheTextureReplacements'     = 'true'
    }

    $lines = @()
    if (Test-Path -LiteralPath $IniPath) {
        $lines = Get-Content -LiteralPath $IniPath
    } else {
        $dir = Split-Path -Parent $IniPath
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }

    # Walk sections, collect section->lines map preserving order.
    $sectionOrder = New-Object System.Collections.ArrayList
    $sectionLines = @{}
    $current = ''
    [void]$sectionOrder.Add($current)
    $sectionLines[$current] = New-Object System.Collections.ArrayList

    foreach ($line in $lines) {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') {
            $current = $Matches[1]
            if (-not $sectionLines.ContainsKey($current)) {
                [void]$sectionOrder.Add($current)
                $sectionLines[$current] = New-Object System.Collections.ArrayList
            }
        } else {
            [void]$sectionLines[$current].Add($line)
        }
    }

    # Ensure [EmuCore/GS] exists.
    if (-not $sectionLines.ContainsKey('EmuCore/GS')) {
        [void]$sectionOrder.Add('EmuCore/GS')
        $sectionLines['EmuCore/GS'] = New-Object System.Collections.ArrayList
    }

    # In [EmuCore/GS], set each required key. Match "Key = value" with optional whitespace.
    $gsLines = $sectionLines['EmuCore/GS']
    foreach ($key in $required.Keys) {
        $desired = "$key = $($required[$key])"
        $found = $false
        for ($i = 0; $i -lt $gsLines.Count; $i++) {
            if ($gsLines[$i] -match "^\s*$([regex]::Escape($key))\s*=") {
                $gsLines[$i] = $desired
                $found = $true
                break
            }
        }
        if (-not $found) {
            [void]$gsLines.Add($desired)
        }
    }

    # Rebuild file content.
    $out = New-Object System.Collections.ArrayList
    foreach ($sec in $sectionOrder) {
        if ($sec -ne '') {
            if ($out.Count -gt 0 -and $out[$out.Count - 1] -ne '') { [void]$out.Add('') }
            [void]$out.Add("[$sec]")
        }
        foreach ($l in $sectionLines[$sec]) { [void]$out.Add($l) }
    }

    # Trim trailing blank lines, then add a single trailing newline.
    while ($out.Count -gt 0 -and $out[$out.Count - 1] -eq '') {
        $out.RemoveAt($out.Count - 1)
    }

    $existing = if (Test-Path -LiteralPath $IniPath) { (Get-Content -LiteralPath $IniPath -Raw) } else { $null }
    $new = ($out -join "`r`n") + "`r`n"
    if ($existing -eq $new) {
        Write-Log "INI already configured: $IniPath" "DEBUG"
        return $false
    }

    Set-Content -LiteralPath $IniPath -Value $new -NoNewline -Encoding UTF8
    Write-Log "Wrote texture replacement flags to $IniPath" "SUCCESS"
    return $true
}

# Enumerate locally installed texture packs for -List.
# Walks subfolders of $TexturesPath (each a serial ID), resolves the game name
# via GameDB, counts PNGs, and reports whether the matching INI enables textures.
function Get-InstalledTexturePacks {
    param(
        [Parameter(Mandatory=$true)][string]$TexturesPath,
        [Parameter(Mandatory=$true)][string]$GamesettingsPath,
        [Parameter(Mandatory=$true)][object[]]$GameDB
    )

    if (-not (Test-Path -LiteralPath $TexturesPath)) {
        Write-Log "Textures path not found: $TexturesPath" "ERROR"
        return @()
    }

    $folders = Get-ChildItem -Path $TexturesPath -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^[A-Z]{4}-\d{5}$' }

    $results = foreach ($f in $folders) {
        $serial = $f.Name
        $gameName = Get-GameNameBySerial -GameDB $GameDB -Serial $serial
        if (-not $gameName) { $gameName = "(unknown: $serial)" }

        $replacements = Join-Path $f.FullName 'replacements'
        $pngCount = 0
        $size = 0
        if (Test-Path -LiteralPath $replacements) {
            $pngs = Get-ChildItem -Path $replacements -Filter *.png -Recurse -File -ErrorAction SilentlyContinue
            $pngCount = @($pngs).Count
            if ($pngCount -gt 0) {
                $size = ($pngs | Measure-Object -Property Length -Sum).Sum
            }
        }

        $iniConfigured = $false
        $iniFiles = Get-ChildItem -Path (Join-Path $GamesettingsPath "$serial*.ini") -File -ErrorAction SilentlyContinue
        foreach ($ini in $iniFiles) {
            $content = Get-Content -LiteralPath $ini.FullName -Raw
            if ($content -match '(?m)^\s*LoadTextureReplacements\s*=\s*true') {
                $iniConfigured = $true
                break
            }
        }

        [PSCustomObject]@{
            Serial        = $serial
            GameName      = $gameName
            TextureCount  = $pngCount
            SizeMB        = [Math]::Round(($size / 1MB), 2)
            IniConfigured = $iniConfigured
        }
    }

    return ,@($results | Sort-Object GameName)
}
