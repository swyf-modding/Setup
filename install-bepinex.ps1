<#
    Installs the mod loader into the game: Doorstop from this repo, BepInEx from GitHub.

    Two sources on purpose. Doorstop is small and vendored, so the loader half works with no
    network. BepInEx is fetched so this repo does not carry a copy of someone else's project; pass
    -BepInExZip to install it from a zip you already have instead.

    Safe to re-run. Existing files are left alone unless -Force is given.

        .\install-bepinex.ps1 -GameDir "C:\...\Scam With Your Friends"
        .\install-bepinex.ps1 -Offline              # doorstop only; BepInEx already present
        .\install-bepinex.ps1 -BepInExZip C:\downloads\BepInEx_win_x64_5.4.23.5.zip
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$BepInExZip,
    [switch]$Offline,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GameDir.ps1')
$GameDir = Resolve-GameDir $GameDir

$vendor = Join-Path $PSScriptRoot 'vendor\doorstop'
$beVersion = '5.4.23.5'
$beUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$beVersion/BepInEx_win_x64_$beVersion.zip"

function Copy-IntoGame($source, $dest, $what) {
    if (-not (Test-Path $source)) { throw "Missing from this repo: $source ($what)" }
    if ((Test-Path $dest) -and -not $Force) {
        Write-Host "  = $what already present, leaving it" -ForegroundColor DarkGray
        return
    }
    Copy-Item $source $dest -Force
    Write-Host "  + $what" -ForegroundColor Green
}

Write-Host "=== installing the loader into $GameDir"

Write-Host "Doorstop (vendored):"
foreach ($file in @('winhttp.dll', '.doorstop_version')) {
    Copy-IntoGame (Join-Path $vendor $file) (Join-Path $GameDir $file) $file
}
Copy-IntoGame (Join-Path $vendor 'doorstop_config.ini') (Join-Path $GameDir 'doorstop_config.ini') `
    'doorstop_config.ini (dll_search_path_override=unstripped_corlib)'

$bepInExDll = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
if ((Test-Path $bepInExDll) -and -not $Force -and -not $BepInExZip) {
    Write-Host "BepInEx:"
    Write-Host "  = already installed, leaving it" -ForegroundColor DarkGray
} elseif ($Offline) {
    throw @"
BepInEx is not installed and -Offline was given.

Get BepInEx $beVersion (win_x64) from https://github.com/BepInEx/BepInEx/releases and extract
BepInEx\core into:
  $GameDir\BepInEx\core
"@
} else {
    Write-Host "BepInEx ${beVersion}:"
    $zip = $BepInExZip
    $temp = $null
    try {
        if (-not $zip) {
            $temp = Join-Path ([IO.Path]::GetTempPath()) "BepInEx_$beVersion.zip"
            Write-Host "  downloading $beUrl" -ForegroundColor DarkGray
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $beUrl -OutFile $temp -UseBasicParsing
            $zip = $temp
        }
        if (-not (Test-Path $zip)) { throw "no such file: $zip" }

        # Only BepInEx\core: the loader half comes from vendor\doorstop above.
        $extract = Join-Path ([IO.Path]::GetTempPath()) ("bepinex_" + [Guid]::NewGuid().ToString('N'))
        try {
            Expand-Archive -Path $zip -DestinationPath $extract -Force
            $core = Join-Path $extract 'BepInEx\core'
            if (-not (Test-Path $core)) { throw "that file is not a BepInEx x64 pack (no BepInEx\core in it)" }

            New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'BepInEx\core') | Out-Null
            Copy-Item (Join-Path $core '*') (Join-Path $GameDir 'BepInEx\core') -Recurse -Force
            Write-Host "  + BepInEx\core" -ForegroundColor Green
        } finally {
            Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
        }
    } catch {
        throw @"
Could not install BepInEx ${beVersion}: $($_.Exception.Message)

Get the pack from
  $beUrl

then either run this again, or unpack it by hand so that
  $GameDir\BepInEx\core\BepInEx.dll
exists. -Offline tells this script to leave BepInEx alone entirely.
"@
    } finally {
        if ($temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
    }
}

New-Item -ItemType Directory -Force -Path (Join-Path $GameDir 'BepInEx\plugins') | Out-Null

Write-Host ""
Write-Host "Loader installed." -ForegroundColor Green
Write-Host "BepInEx is LGPL-2.1, Doorstop is LGPL-2.1; licence texts are in vendor\." -ForegroundColor DarkGray