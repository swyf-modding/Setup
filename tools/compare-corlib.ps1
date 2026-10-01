param(
    [string]$Cecil,
    [string]$Managed,
    [string]$Unstripped
)

$ErrorActionPreference = 'Stop'
Add-Type -Path $Cecil

function Load($p) { [Mono.Cecil.AssemblyDefinition]::ReadAssembly($p) }

# Build a lookup of what an assembly actually provides.
function Index($path) {
    $asm = Load $path
    $types = @{}
    $exported = @{}
    foreach ($t in $asm.MainModule.GetTypes()) {
        $members = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($m in $t.Methods) {
            [void]$members.Add("$($m.Name)/$($m.Parameters.Count)")
        }
        foreach ($f in $t.Fields) { [void]$members.Add("$($f.Name)/F") }
        $types[$t.FullName] = $members
    }
    foreach ($e in $asm.MainModule.ExportedTypes) { $exported[$e.FullName] = $true }
    return @{ Types = $types; Exported = $exported; Name = $asm.Name.Name }
}

$corlibNames = @('mscorlib', 'System', 'System.Core', 'System.Xml', 'System.Configuration', 'System.Runtime.Serialization')

Write-Host "Indexing corlibs..." -ForegroundColor Cyan
$game = @{}
$dev = @{}
foreach ($n in $corlibNames) {
    $gp = Join-Path $Managed "$n.dll"
    $dp = Join-Path $Unstripped "$n.dll"
    if (Test-Path $gp) { $game[$n] = Index $gp }
    if (Test-Path $dp) { $dev[$n]  = Index $dp }
}
Write-Host ("game corlibs:      " + ($game.Keys -join ', '))
Write-Host ("unstripped corlibs:" + ($dev.Keys -join ', '))

# ---- 1. Is the unstripped corlib actually a superset of the shipped one? ----
foreach ($n in $game.Keys) {
    if (-not $dev.ContainsKey($n)) { continue }
    $missingTypes = New-Object System.Collections.ArrayList
    $missingMembers = New-Object System.Collections.ArrayList
    foreach ($tn in $game[$n].Types.Keys) {
        if (-not $dev[$n].Types.ContainsKey($tn)) {
            if (-not $dev[$n].Exported.ContainsKey($tn)) { [void]$missingTypes.Add($tn) }
            continue
        }
        foreach ($mem in $game[$n].Types[$tn]) {
            if (-not $dev[$n].Types[$tn].Contains($mem)) { [void]$missingMembers.Add("$tn :: $mem") }
        }
    }
    Write-Host ""
    Write-Host "=== $n : present in SHIPPED but absent from UNSTRIPPED ===" -ForegroundColor Yellow
    Write-Host ("    types missing  : {0}" -f $missingTypes.Count)
    Write-Host ("    members missing: {0}" -f $missingMembers.Count)
    $missingTypes   | Select-Object -First 25 | ForEach-Object { Write-Host "      TYPE   $_" }
    $missingMembers | Select-Object -First 40 | ForEach-Object { Write-Host "      MEMBER $_" }
}

# ---- 2. What do the three problem assemblies actually need? ----
foreach ($target in @('System.Text.Json', 'UniTask', 'Newtonsoft.Json')) {
    $p = Join-Path $Managed "$target.dll"
    if (-not (Test-Path $p)) { continue }
    $asm = Load $p
    $unresolved = New-Object System.Collections.ArrayList
    foreach ($mr in $asm.MainModule.GetMemberReferences()) {
        $dt = $mr.DeclaringType
        if (-not $dt) { continue }
        $scope = $dt.Scope
        if (-not $scope) { continue }
        $sn = $scope.Name
        if (-not $dev.ContainsKey($sn)) { continue }

        # strip generic instantiation to get the open type name
        $tn = $dt.FullName
        $tick = $tn.IndexOf('<')
        if ($tick -ge 0) { $tn = $tn.Substring(0, $tick) }

        if (-not $dev[$sn].Types.ContainsKey($tn)) {
            if (-not $dev[$sn].Exported.ContainsKey($tn)) { [void]$unresolved.Add("$sn : TYPE MISSING  $tn") }
            continue
        }
        $key = if ($mr -is [Mono.Cecil.MethodReference]) { "$($mr.Name)/$($mr.Parameters.Count)" } else { "$($mr.Name)/F" }
        if (-not $dev[$sn].Types[$tn].Contains($key)) { [void]$unresolved.Add("$sn : MEMBER MISSING $tn :: $key") }
    }
    Write-Host ""
    Write-Host "=== $target needs these, unstripped corlib does NOT provide them ===" -ForegroundColor Red
    if ($unresolved.Count -eq 0) {
        Write-Host "    (nothing missing)" -ForegroundColor Green
    } else {
        $unresolved | Sort-Object -Unique | ForEach-Object { Write-Host "    $_" }
    }
}
