<#
    Every Steam library's 'common' folder, default install first, then whatever libraryfolders.vdf
    names. The game is installed to a library, not necessarily the one Steam itself lives in - it is
    on D: here while Steam is on C: - so reading the VDF is what makes this work on a machine whose
    libraries are somewhere unexpected, without hardcoding one drive letter.
#>
function Get-SteamGameRoots {
    $roots = @()
    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "${env:ProgramFiles}\Steam",
                         "${env:LOCALAPPDATA}\Steam")) {
        if (-not $steam) { continue }
        $roots += (Join-Path $steam 'steamapps\common')

        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path $vdf)) { continue }
        try {
            $text = Get-Content $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $library = $m.Groups[1].Value -replace '\\\\', '\'
                $roots += (Join-Path $library 'steamapps\common')
            }
        } catch {
            # A VDF that cannot be read is not worth failing over; the fixed roots are still tried.
        }
    }

    # Last resorts for a machine where nothing above resolved.
    $roots += @(
        'C:\Program Files (x86)\Steam\steamapps\common'
        'D:\SteamLibrary\steamapps\common'
    )

    return ($roots | Where-Object { $_ } | Select-Object -Unique)
}

<#
    Finds the game install, for every script in this repo.

    This repo lives in your source tree, not inside the game folder, so no script can guess the
    game location from its own path any more. Resolution order:

      1. an explicit -GameDir
      2. the SWYG_GAME_DIR environment variable
      3. every Steam library

    Only the full release is considered; a leftover Playtest install is ignored. The two install
    under different folder names, so matching by name - not by whichever library Steam lists first -
    is what keeps the old Playtest from winning over the release.

    Dot-source it and call Resolve-GameDir:

        . (Join-Path $PSScriptRoot '..\GameDir.ps1')
        $GameDir = Resolve-GameDir $GameDir
#>
function Resolve-GameDir {
    param(
        [string]$Explicit,
        [switch]$RequireBepInEx
    )

    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:SWYG_GAME_DIR) { $candidates += $env:SWYG_GAME_DIR }

    $names = @('Scam With Your Friends')
    $roots = Get-SteamGameRoots
    foreach ($name in $names) {
        foreach ($root in $roots) { $candidates += (Join-Path $root $name) }
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (-not (Test-Path (Join-Path $candidate 'Scam With Your Friends_Data\Managed\mscorlib.dll'))) { continue }
        if ($RequireBepInEx -and -not (Test-Path (Join-Path $candidate 'BepInEx\core\BepInEx.dll'))) { continue }
        return $candidate
    }

    $wanted = if ($RequireBepInEx) { 'Scam With Your Friends_Data\Managed, BepInEx\core' }
              else { 'Scam With Your Friends_Data\Managed' }
    $hints = ($candidates | Where-Object { $_ } | Select-Object -Unique) -join "`n  "
    throw @"
Could not find the game install (needs $wanted). Pass -GameDir, or set SWYG_GAME_DIR.

Looked in:
  $hints
"@
}