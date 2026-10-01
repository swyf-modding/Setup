<#
    Why this exists
    ---------------
    Unity's managed stripping removes mscorlib members AND the game's other assemblies
    *consistently with each other*. In the shipped build:

        INotifyCompletion            - deleted entirely
        ICriticalNotifyCompletion    - extends nothing, has only UnsafeOnCompleted(Action)

    so the stripper also deleted OnCompleted(Action) from every awaiter, since nothing
    could call it any more.

    Putting an unstripped mscorlib back (which BepInEx requires) restores
    ICriticalNotifyCompletion : INotifyCompletion. Those awaiters are now missing an
    interface method, and Mono fails to build their vtable:

        TypeLoadException: Generic type definition failed to init, due to:
        VTable setup of type Cysharp.Threading.Tasks.UniTask`1+Awaiter[T] failed

    which kills every await in the game.

    This script finds awaiter types that implement ICriticalNotifyCompletion but have no
    OnCompleted(Action), and gives them one by cloning the body of UnsafeOnCompleted. In
    UniTask (and in the BCL awaiters) those two methods are implemented identically, so
    the clone is faithful rather than a stub.

    The patched copy is written to the Doorstop override folder, so the game's own files
    in Managed\ are never modified.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string[]]$Assemblies = @('UniTask.dll'),
    [string]$OverrideDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'GameDir.ps1')
$GameDir = Resolve-GameDir $GameDir

$managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
if (-not $OverrideDir) { $OverrideDir = Join-Path $GameDir 'unstripped_corlib' }
$cecilPath = Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll'

foreach ($p in @($managed, $cecilPath)) { if (-not (Test-Path $p)) { throw "Not found: $p" } }
New-Item -ItemType Directory -Force -Path $OverrideDir | Out-Null
Add-Type -Path $cecilPath

$CRITICAL = 'System.Runtime.CompilerServices.ICriticalNotifyCompletion'
$NOTIFY   = 'System.Runtime.CompilerServices.INotifyCompletion'

function Clone-MethodBody {
    param($Source, $Target)

    $sb = $Source.Body
    $tb = $Target.Body
    $tb.InitLocals = $sb.InitLocals

    foreach ($v in $sb.Variables) {
        $tb.Variables.Add((New-Object Mono.Cecil.Cil.VariableDefinition($v.VariableType)))
    }

    $il = $tb.GetILProcessor()
    $map = @{}

    # pass 1 - copy instructions, remembering what mapped to what
    foreach ($ins in $sb.Instructions) {
        $new =
            if ($null -eq $ins.Operand) { $il.Create($ins.OpCode) }
            elseif ($ins.Operand -is [Mono.Cecil.Cil.Instruction]) { $il.Create($ins.OpCode, $sb.Instructions[0]) } # fixed in pass 2
            elseif ($ins.Operand -is [Mono.Cecil.Cil.Instruction[]]) { $il.Create($ins.OpCode, [Mono.Cecil.Cil.Instruction[]]@()) }
            elseif ($ins.Operand -is [Mono.Cecil.Cil.VariableDefinition]) { $il.Create($ins.OpCode, $tb.Variables[$ins.Operand.Index]) }
            elseif ($ins.Operand -is [Mono.Cecil.ParameterDefinition]) {
                # parameter 0 of the source maps to parameter 0 of the target, etc.
                if ($ins.Operand.Index -lt 0) { $il.Create($ins.OpCode, $Target.Body.ThisParameter) }
                else { $il.Create($ins.OpCode, $Target.Parameters[$ins.Operand.Index]) }
            }
            elseif ($ins.Operand -is [Mono.Cecil.MethodReference]) { $il.Create($ins.OpCode, $ins.Operand) }
            elseif ($ins.Operand -is [Mono.Cecil.FieldReference])  { $il.Create($ins.OpCode, $ins.Operand) }
            elseif ($ins.Operand -is [Mono.Cecil.TypeReference])   { $il.Create($ins.OpCode, $ins.Operand) }
            elseif ($ins.Operand -is [string])  { $il.Create($ins.OpCode, [string]$ins.Operand) }
            elseif ($ins.Operand -is [sbyte])   { $il.Create($ins.OpCode, [sbyte]$ins.Operand) }
            elseif ($ins.Operand -is [byte])    { $il.Create($ins.OpCode, [byte]$ins.Operand) }
            elseif ($ins.Operand -is [int])     { $il.Create($ins.OpCode, [int]$ins.Operand) }
            elseif ($ins.Operand -is [long])    { $il.Create($ins.OpCode, [long]$ins.Operand) }
            elseif ($ins.Operand -is [single])  { $il.Create($ins.OpCode, [single]$ins.Operand) }
            elseif ($ins.Operand -is [double])  { $il.Create($ins.OpCode, [double]$ins.Operand) }
            else { throw "Unhandled operand type $($ins.Operand.GetType().FullName) in $($Source.FullName)" }

        $il.Append($new)
        $map[$ins] = $new
    }

    # pass 2 - repoint branches at the cloned instructions
    for ($i = 0; $i -lt $sb.Instructions.Count; $i++) {
        $old = $sb.Instructions[$i]
        $new = $tb.Instructions[$i]
        if ($old.Operand -is [Mono.Cecil.Cil.Instruction]) {
            $new.Operand = $map[$old.Operand]
        } elseif ($old.Operand -is [Mono.Cecil.Cil.Instruction[]]) {
            $new.Operand = [Mono.Cecil.Cil.Instruction[]]($old.Operand | ForEach-Object { $map[$_] })
        }
    }

    foreach ($h in $sb.ExceptionHandlers) {
        $nh = New-Object Mono.Cecil.Cil.ExceptionHandler($h.HandlerType)
        if ($h.TryStart)     { $nh.TryStart     = $map[$h.TryStart] }
        if ($h.TryEnd)       { $nh.TryEnd       = $map[$h.TryEnd] }
        if ($h.HandlerStart) { $nh.HandlerStart = $map[$h.HandlerStart] }
        if ($h.HandlerEnd)   { $nh.HandlerEnd   = $map[$h.HandlerEnd] }
        if ($h.FilterStart)  { $nh.FilterStart  = $map[$h.FilterStart] }
        if ($h.CatchType)    { $nh.CatchType    = $h.CatchType }
        $tb.ExceptionHandlers.Add($nh)
    }

    $tb.MaxStackSize = $sb.MaxStackSize
}

foreach ($name in $Assemblies) {
    $src = Join-Path $managed $name
    if (-not (Test-Path $src)) { Write-Warning "skipping $name - not in Managed"; continue }

    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($src)
    try {
        $patched = 0
        foreach ($t in $asm.MainModule.GetTypes()) {
            $implements = $false
            foreach ($i in $t.Interfaces) {
                if ($i.InterfaceType.FullName -eq $CRITICAL -or $i.InterfaceType.FullName -eq $NOTIFY) { $implements = $true }
            }
            if (-not $implements) { continue }

            $hasOn = $t.Methods | Where-Object { $_.Name -match '(^|\.)OnCompleted$' -and $_.Parameters.Count -eq 1 }
            if ($hasOn) { continue }

            $unsafe = $t.Methods | Where-Object { $_.Name -match '(^|\.)UnsafeOnCompleted$' -and $_.Parameters.Count -eq 1 } | Select-Object -First 1
            if (-not $unsafe) { Write-Warning "  $($t.FullName): no UnsafeOnCompleted to clone, skipped"; continue }

            $m = New-Object Mono.Cecil.MethodDefinition('OnCompleted', $unsafe.Attributes, $unsafe.ReturnType)
            foreach ($p in $unsafe.Parameters) {
                $m.Parameters.Add((New-Object Mono.Cecil.ParameterDefinition($p.Name, $p.Attributes, $p.ParameterType)))
            }
            $m.ImplAttributes = $unsafe.ImplAttributes
            $m.SemanticsAttributes = $unsafe.SemanticsAttributes
            $t.Methods.Add($m)

            Clone-MethodBody -Source $unsafe -Target $m

            Write-Host "  + $($t.FullName)::OnCompleted(Action)"
            $patched++
        }

        if ($patched -eq 0) {
            Write-Host "$name - nothing to patch" -ForegroundColor Yellow
            continue
        }

        $dest = Join-Path $OverrideDir $name
        $asm.Write($dest)
        Write-Host "$name - added $patched method(s), wrote $dest" -ForegroundColor Green
    }
    finally { $asm.Dispose() }
}
