<#
    Finds the full set of "stripper pact" breakages.

    Unity's stripper narrows BCL interfaces and then deletes the now-unreachable
    implementations from the game's own assemblies, consistently. Restoring an unstripped
    corlib widens those interfaces again, and any implementing type that lost a method can
    no longer have its vtable built:

        TypeLoadException: VTable setup of type <X> failed

    This script does it in two precise steps:

      1. diff every interface in the shipped corlib against the unstripped one, and keep the
         ones whose required method set GREW (either new methods, or new base interfaces)
      2. find every type in the game that implements one of those interfaces and does not
         provide the added methods

    Anything it lists will fail to load at runtime. Feed the owning assemblies to
    unstrip-awaiters.ps1, or replace them with unstripped builds.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$Unstripped,
    # Write patched copies into the override folder, adding the missing methods.
    [switch]$Fix,
    # Only touch these assemblies when fixing.
    [string[]]$Assemblies
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'GameDir.ps1')
$GameDir = Resolve-GameDir $GameDir -RequireBepInEx

$managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
if (-not $Unstripped) { $Unstripped = Join-Path $GameDir 'unstripped_corlib' }
Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')

# Resolve base types across assemblies, preferring the override folder (what actually loads).
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($Unstripped)
$resolver.AddSearchDirectory($managed)
$readerParams = New-Object Mono.Cecil.ReaderParameters
$readerParams.AssemblyResolver = $resolver

function Read-Asm($p) { [Mono.Cecil.AssemblyDefinition]::ReadAssembly($p, $readerParams) }

# ---------- step 1: which interfaces gained required methods? ----------

function Index-Interfaces($path) {
    $asm = Read-Asm $path
    $map = @{}
    foreach ($t in $asm.MainModule.GetTypes()) {
        if (-not $t.IsInterface) { continue }
        $map[$t.FullName] = $t
    }
    return $map
}

function Required-Methods($name, $map, [System.Collections.Generic.HashSet[string]]$seen) {
    # NOTE: the leading comma stops PowerShell unrolling the set into the pipeline.
    $out = New-Object 'System.Collections.Generic.HashSet[string]'
    if (-not $map.ContainsKey($name)) { return ,$out }
    if (-not $seen.Add($name)) { return ,$out }
    $t = $map[$name]
    foreach ($m in $t.Methods) { [void]$out.Add("$($m.Name)/$($m.Parameters.Count)") }
    foreach ($i in $t.Interfaces) {
        $inherited = Required-Methods $i.InterfaceType.FullName $map $seen
        foreach ($x in $inherited) { [void]$out.Add($x) }
    }
    return ,$out
}

$corlibs = @('mscorlib','System','System.Core','System.Xml','System.Configuration')
$widened = @{}

foreach ($n in $corlibs) {
    $sp = Join-Path $managed "$n.dll"
    $up = Join-Path $Unstripped "$n.dll"
    if (-not (Test-Path $sp) -or -not (Test-Path $up)) { continue }

    $sMap = Index-Interfaces $sp
    $uMap = Index-Interfaces $up

    foreach ($iface in $uMap.Keys) {
        if (-not $sMap.ContainsKey($iface)) { continue }
        $sReq = Required-Methods $iface $sMap (New-Object 'System.Collections.Generic.HashSet[string]')
        $uReq = Required-Methods $iface $uMap (New-Object 'System.Collections.Generic.HashSet[string]')
        $added = @($uReq | Where-Object { -not $sReq.Contains($_) })
        if ($added.Count -gt 0) { $widened[$iface] = $added }
    }
}

Write-Host "=== interfaces whose contract widened under the unstripped corlib ===" -ForegroundColor Cyan
if ($widened.Count -eq 0) { Write-Host "  (none)" }
foreach ($k in ($widened.Keys | Sort-Object)) {
    Write-Host ("  {0}" -f $k) -ForegroundColor Yellow
    Write-Host ("      now also requires: " + ($widened[$k] -join ', '))
}

if ($widened.Count -eq 0) { return }

# ---------- step 2: who implements them and is now short a method? ----------

Write-Host ""
Write-Host "=== types that will fail vtable setup ===" -ForegroundColor Cyan

# Prefer the override folder's copy of an assembly, since that is what actually loads.
$scan = @{}
foreach ($d in Get-ChildItem $managed -Filter *.dll) { $scan[$d.Name] = $d.FullName }
foreach ($d in Get-ChildItem $Unstripped -Filter *.dll) { $scan[$d.Name] = $d.FullName }

# Rewrites an interface-method signature type into the implementing module, replacing the
# interface's generic parameters with the concrete arguments. Returns $null if it cannot.
function Import-Substituted($typeRef, $map, $module) {
    if ($null -eq $typeRef) { return $null }

    if ($typeRef -is [Mono.Cecil.GenericParameter]) {
        if ($map.ContainsKey($typeRef.Name)) { return $module.ImportReference($map[$typeRef.Name]) }
        return $null
    }

    if ($typeRef -is [Mono.Cecil.GenericInstanceType]) {
        $elem = $module.ImportReference($typeRef.ElementType)
        $gi = New-Object Mono.Cecil.GenericInstanceType($elem)
        foreach ($arg in $typeRef.GenericArguments) {
            $s = Import-Substituted $arg $map $module
            if (-not $s) { return $null }
            $gi.GenericArguments.Add($s)
        }
        return $gi
    }

    if ($typeRef -is [Mono.Cecil.ByReferenceType]) {
        $inner = Import-Substituted $typeRef.ElementType $map $module
        if (-not $inner) { return $null }
        return (New-Object Mono.Cecil.ByReferenceType($inner))
    }

    if ($typeRef -is [Mono.Cecil.ArrayType]) {
        $inner = Import-Substituted $typeRef.ElementType $map $module
        if (-not $inner) { return $null }
        return (New-Object Mono.Cecil.ArrayType($inner, $typeRef.Rank))
    }

    try { return $module.ImportReference($typeRef) } catch { return $null }
}

# Walks up the base chain inside this module. A method inherited from a base class satisfies
# the interface just fine, so without this we report a pile of false positives.
function Provides-Method($type, $mName, $mArgs) {
    $cur = $type
    $guard = 0
    while ($cur -and $guard -lt 32) {
        $guard++
        foreach ($m in $cur.Methods) {
            if ($m.Parameters.Count -ne $mArgs) { continue }
            if ($m.Name -eq $mName -or $m.Name -like "*.$mName") { return $true }
        }
        if (-not $cur.BaseType) { return $false }
        # Keep walking across assembly boundaries - every class bottoms out at System.Object,
        # so bailing out at the first external base would mark everything as satisfied.
        try { $next = $cur.BaseType.Resolve() } catch { $next = $null }
        if (-not $next) { return $false }   # unresolvable: report it rather than hide it
        $cur = $next
    }
    return $false
}

$found = 0
$fixedAsm = 0
foreach ($name in ($scan.Keys | Sort-Object)) {
    if ($name -eq 'mscorlib.dll') { continue }
    try { $asm = Read-Asm $scan[$name] } catch { continue }

    $bad = New-Object System.Collections.ArrayList
    $todo = New-Object System.Collections.ArrayList

    foreach ($t in $asm.MainModule.GetTypes()) {
        # NB: PowerShell gives -and and -or equal precedence, so keep this a single test.
        if ($t.IsInterface) { continue }
        foreach ($i in $t.Interfaces) {
            $iface = $i.InterfaceType.FullName
            $lt = $iface.IndexOf('<'); if ($lt -ge 0) { $iface = $iface.Substring(0, $lt) }
            if (-not $widened.ContainsKey($iface)) { continue }

            foreach ($need in $widened[$iface]) {
                $parts = $need -split '/'
                $mName = $parts[0]; $mArgs = [int]$parts[1]
                if (Provides-Method $t $mName $mArgs) { continue }
                [void]$bad.Add("$($t.FullName)  needs  $need   (from $iface)")
                [void]$todo.Add([pscustomobject]@{ Type = $t; Name = $mName; Args = $mArgs; Iface = $i.InterfaceType })
            }
        }
    }

    if ($bad.Count -eq 0) { $asm.Dispose(); continue }

    Write-Host ""
    Write-Host ("  {0}  [{1}]" -f $name, $(if ($scan[$name] -like "*unstripped_corlib*") { 'override copy' } else { 'Managed copy' })) -ForegroundColor Red
    $bad | Sort-Object -Unique | ForEach-Object { Write-Host "      $_" }
    $found += $bad.Count

    if (-not $Fix -or ($Assemblies -and $name -notin $Assemblies)) { $asm.Dispose(); continue }

    # These methods were stripped precisely because nothing reachable calls them, so a body
    # that throws is safe - and far safer than returning a bogus value if one ever does.
    $ctor = $null
    foreach ($m in $todo) {
        $iface = $m.Iface.Resolve()
        $sig = $null
        if ($iface) { $sig = $iface.Methods | Where-Object { $_.Name -eq $m.Name -and $_.Parameters.Count -eq $m.Args } | Select-Object -First 1 }
        if (-not $sig) { Write-Warning "      cannot resolve signature for $($m.Name); skipped"; continue }

        # The interface method's signature is written in terms of the interface's own type
        # parameters (IBufferWriter<T>.GetSpan returns Span<T>). Substitute the actual
        # arguments from the implemented interface before importing, or the import fails.
        $map = @{}
        if ($m.Iface -is [Mono.Cecil.GenericInstanceType]) {
            $def = $m.Iface.Resolve()
            if ($def) {
                for ($gi = 0; $gi -lt $def.GenericParameters.Count -and $gi -lt $m.Iface.GenericArguments.Count; $gi++) {
                    $map[$def.GenericParameters[$gi].Name] = $m.Iface.GenericArguments[$gi]
                }
            }
        }

        $retType = Import-Substituted $sig.ReturnType $map $asm.MainModule
        if (-not $retType) { Write-Warning "      cannot build signature for $($m.Name); skipped"; continue }
        # -bor yields a plain int in PowerShell, so cast it back to the enum.
        $attrs = [Mono.Cecil.MethodAttributes]( `
                 [int][Mono.Cecil.MethodAttributes]::Public -bor `
                 [int][Mono.Cecil.MethodAttributes]::Final -bor `
                 [int][Mono.Cecil.MethodAttributes]::Virtual -bor `
                 [int][Mono.Cecil.MethodAttributes]::HideBySig -bor `
                 [int][Mono.Cecil.MethodAttributes]::NewSlot)
        $nm = New-Object Mono.Cecil.MethodDefinition($m.Name, $attrs, $retType)
        $sigFailed = $false
        foreach ($p in $sig.Parameters) {
            $pt = Import-Substituted $p.ParameterType $map $asm.MainModule
            if (-not $pt) { $sigFailed = $true; break }
            $nm.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition($p.Name, $p.Attributes, $pt)))
        }
        if ($sigFailed) { Write-Warning "      cannot build parameters for $($m.Name); skipped"; continue }

        if (-not $ctor) {
            $ctor = $asm.MainModule.ImportReference([System.NotImplementedException].GetConstructor([Type]::EmptyTypes))
        }
        $il = $nm.Body.GetILProcessor()
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Newobj, $ctor))
        $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Throw))
        $nm.Body.MaxStackSize = 1

        $m.Type.Methods.Add($nm)
        Write-Host "      + $($m.Type.FullName)::$($m.Name) (throwing stub)" -ForegroundColor Green
    }

    $dest = Join-Path $Unstripped $name
    $asm.Write($dest)
    $asm.Dispose()
    Write-Host "      wrote $dest" -ForegroundColor Green
    $fixedAsm++
}

Write-Host ""
if ($found -eq 0) {
    Write-Host "No vtable breakages remain." -ForegroundColor Green
} elseif ($Fix) {
    Write-Host "Patched $fixedAsm assembly/assemblies. Re-run without -Fix to confirm." -ForegroundColor Cyan
} else {
    Write-Host "$found breakage(s). Re-run with -Fix (optionally -Assemblies a.dll,b.dll) to patch them." -ForegroundColor Red
}
