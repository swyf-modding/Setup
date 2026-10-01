param(
    [string]$Cecil,
    [string]$Managed,
    [string]$ScanDir
)

$ErrorActionPreference = 'Stop'
Add-Type -Path $Cecil

function Index($path) {
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    $types = @{}
    foreach ($t in $asm.MainModule.GetTypes()) {
        $members = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($m in $t.Methods) { [void]$members.Add("$($m.Name)/$($m.Parameters.Count)") }
        foreach ($f in $t.Fields)  { [void]$members.Add("$($f.Name)/F") }
        $types[$t.FullName] = $members
    }
    $exp = @{}
    foreach ($e in $asm.MainModule.ExportedTypes) { $exp[$e.FullName] = $true }
    return @{ Types = $types; Exported = $exp }
}

$bclNames = @('mscorlib','System','System.Core','System.Xml','System.Configuration',
              'System.Runtime.Serialization','System.Data','System.Drawing','System.Numerics')

$bcl = @{}
foreach ($n in $bclNames) {
    $p = Join-Path $Managed "$n.dll"
    if (Test-Path $p) { $bcl[$n] = Index $p }
}

$all = @{}
foreach ($dll in Get-ChildItem $ScanDir -Filter *.dll) {
    if ($dll.Name -eq 'Mono.Cecil.Pdb.dll' -or $dll.Name -eq 'Mono.Cecil.Mdb.dll') { continue }
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll.FullName)
    $missing = New-Object System.Collections.ArrayList

    foreach ($tr in $asm.MainModule.GetTypeReferences()) {
        $scope = $tr.Scope
        if (-not $scope -or -not $bcl.ContainsKey($scope.Name)) { continue }
        $tn = $tr.FullName
        if ($bcl[$scope.Name].Types.ContainsKey($tn)) { continue }
        if ($bcl[$scope.Name].Exported.ContainsKey($tn)) { continue }
        [void]$missing.Add("TYPE   $($scope.Name) :: $tn")
    }

    foreach ($mr in $asm.MainModule.GetMemberReferences()) {
        $dt = $mr.DeclaringType
        if (-not $dt -or -not $dt.Scope) { continue }
        $sn = $dt.Scope.Name
        if (-not $bcl.ContainsKey($sn)) { continue }
        $tn = $dt.FullName
        $lt = $tn.IndexOf('<')
        if ($lt -ge 0) { $tn = $tn.Substring(0, $lt) }
        if (-not $bcl[$sn].Types.ContainsKey($tn)) { continue }  # already reported as missing type
        $key = if ($mr -is [Mono.Cecil.MethodReference]) { "$($mr.Name)/$($mr.Parameters.Count)" } else { "$($mr.Name)/F" }
        if ($bcl[$sn].Types[$tn].Contains($key)) { continue }
        [void]$missing.Add("MEMBER $sn :: $tn :: $key")
    }

    if ($missing.Count -gt 0) {
        $u = $missing | Sort-Object -Unique
        $all[$dll.Name] = $u
    }
}

$total = 0
foreach ($k in ($all.Keys | Sort-Object)) {
    Write-Host ""
    Write-Host "=== $k  ($($all[$k].Count) missing) ===" -ForegroundColor Red
    $all[$k] | ForEach-Object { Write-Host "    $_" }
    $total += $all[$k].Count
}
Write-Host ""
Write-Host "TOTAL MISSING REFERENCES: $total" -ForegroundColor Cyan
