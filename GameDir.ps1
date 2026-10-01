<#
    Finds the game install, for every script in this repo.

    This repo lives in your source tree, not inside the game folder, so no script can guess the
    game location from its own path any more. Resolution order:

      1. an explicit -GameDir
      2. the SWYG_GAME_DIR environment variable
      3. the usual Steam locations

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

    $names = @('Scam With Your Friends', 'Scam With Your Friends Playtest')
    $roots = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common"
        "${env:ProgramFiles}\Steam\steamapps\common"
        "${env:HOME}\.local\share\Steam\steamapps\common"
        "${env:HOME}\.steam\steam\steamapps\common"
    )
    foreach ($root in $roots) {
        foreach ($name in $names) { $candidates += (Join-Path $root $name) }
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