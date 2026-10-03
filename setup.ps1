<#
    Sets the game up for modding, from a clean install, in one command.

    1. installs the loader      (Doorstop from vendor\, BepInEx from GitHub)
    2. seeds the corlib override (the unstripped BCL from vendor\corlib)
    3. restores the stripped awaiter methods
    4. patches the remaining vtable breakages
    5. verifies

    No mods are built here: AI-Backend and Mod-Handler each have their own
    build.ps1. Safe to re-run - steps 1 and 2 leave existing files alone, steps 3 and 4 rewrite
    their own output. Steps 3 and 4 only ever write to unstripped_corlib\; Managed\ is untouched.

      .\setup.ps1
      .\setup.ps1 -GameDir "C:\...\steamapps\common\Scam With Your Friends"
      .\setup.ps1 -Offline        # do not download BepInEx
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$BepInExZip,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GameDir.ps1')
$GameDir = Resolve-GameDir $GameDir

$override = Join-Path $GameDir 'unstripped_corlib'
$managed  = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'

Write-Host "Game: $GameDir"
Write-Host ""

# ---------------------------------------------------------------- 1. loader

$installArgs = @{ GameDir = $GameDir }
if ($BepInExZip) { $installArgs.BepInExZip = $BepInExZip }
if ($Offline)    { $installArgs.Offline = $true }
& (Join-Path $PSScriptRoot 'install-bepinex.ps1') @installArgs

# ---------------------------------------------------------------- 2. corlib

Write-Host ""
Write-Host "=== seeding the unstripped corlib override ===" -ForegroundColor Cyan

# These are the assemblies BepInEx and Harmony need that the game's stripped Managed\ does not
# have. The game's own copies of everything else stay where they are, so this is a small folder on
# purpose - see vendor\README.md for why.
$corlibSrc = Join-Path $PSScriptRoot 'vendor\corlib'
if (-not (Test-Path $corlibSrc)) { throw "Missing from this repo: $corlibSrc" }

New-Item -ItemType Directory -Force -Path $override | Out-Null
$seeded = 0
foreach ($dll in Get-ChildItem $corlibSrc -Filter *.dll) {
    $dest = Join-Path $override $dll.Name
    if (Test-Path $dest) { continue }
    Copy-Item $dll.FullName $dest
    $seeded++
}
if ($seeded -gt 0) {
    Write-Host "  + seeded $seeded assembly/assemblies into $override" -ForegroundColor Green
} else {
    Write-Host "  = override already seeded, leaving it" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 3 and 4. patches

Write-Host ""
Write-Host "=== restoring stripped awaiter methods ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'tools\unstrip-awaiters.ps1') -GameDir $GameDir

Write-Host ""
Write-Host "=== patching remaining vtable breakages ===" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'tools\find-vtable-breaks.ps1') -GameDir $GameDir -Fix

# ---------------------------------------------------------------- 5. verify

Write-Host ""
Write-Host "=== verifying ===" -ForegroundColor Cyan

$problems = @()

if (-not (Test-Path (Join-Path $override 'mscorlib.dll'))) {
    $problems += "no mscorlib in the override folder - BepInEx cannot load"
}

$config = Join-Path $GameDir 'doorstop_config.ini'
if (-not (Test-Path $config)) {
    $problems += "no doorstop_config.ini in the game root"
} elseif ((Get-Content $config -Raw) -notmatch 'dll_search_path_override\s*=\s*unstripped_corlib') {
    $problems += "doorstop_config.ini does not set dll_search_path_override=unstripped_corlib"
}

# 6>&1 captures the tool's Write-Host lines (the information stream) as well as its output, so the
# verdict below is read from what the tool actually said rather than from an empty capture.
$report = & (Join-Path $PSScriptRoot 'tools\find-vtable-breaks.ps1') -GameDir $GameDir 6>&1
$verdict = $report | Select-String 'No vtable breakages|breakage\(s\)'
if ($verdict) { Write-Host $verdict }

if (($report | Out-String) -notmatch 'No vtable breakages remain') {
    $problems += "vtable breakages remain - run tools\find-vtable-breaks.ps1 and read its output"
}

# Managed\ must be exactly as the game shipped it.
$managedCount = (Get-ChildItem $managed -Filter *.dll).Count
if ($managedCount -lt 1) { $problems += "no assemblies in Managed\" }

Write-Host ""
if ($problems.Count -eq 0) {
    Write-Host "Setup complete. The game should load with BepInEx." -ForegroundColor Green
    Write-Host "Next: build the mods, then launch the game once to create their config files." -ForegroundColor Green
    Write-Host ""
    Write-Host "  AI-Backend\build.ps1    -> your own LLM instead of the hosted backend" -ForegroundColor Green
    Write-Host "  Mod-Handler\build.ps1   -> in-game mod list" -ForegroundColor Green
    Write-Host ""
    Write-Host "Re-run this after any game update." -ForegroundColor DarkGray
    exit 0
}

Write-Host "Setup finished with problems:" -ForegroundColor Yellow
foreach ($problem in $problems) { Write-Host "  - $problem" -ForegroundColor Yellow }
exit 1