<#
    Why this exists
    ---------------
    This game ships with Unity managed stripping ON, so its mscorlib.dll is missing
    System.Reflection.Module.GetPEKind. BepInEx's preloader calls that method from
    BepInEx.Preloader.PlatformUtils.SetPlatform(), so BepInEx dies before it loads
    anything:

        MissingMethodException: Method not found:
        void System.Reflection.Module.GetPEKind(PortableExecutableKinds&, ImageFileMachine&)

    The usual workaround is Doorstop's dll_search_path_override pointing at an
    unstripped corlib. That works, but it swaps the whole BCL out from under the game.

    The call is only there to detect an ARM CPU on Windows. On an x64 Windows build it
    can never do anything useful. So instead we rewrite that one method to:

        PlatformHelper.Current = Platform.Windows | (IntPtr.Size == 8 ? Platform.Bits64 : 0);

    which is what SetPlatform would have computed anyway, and leaves the game's own
    runtime completely untouched.

    Re-run this after updating BepInEx.
#>
[CmdletBinding()]
param(
    [string]$CoreDir,
    [switch]$Revert
)

$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'GameDir.ps1')
if (-not $CoreDir) { $CoreDir = Join-Path (Resolve-GameDir $null -RequireBepInEx) 'BepInEx\core' }

$CoreDir    = (Resolve-Path $CoreDir).Path
$preloader  = Join-Path $CoreDir 'BepInEx.Preloader.dll'
$backup     = "$preloader.orig"
$cecil      = Join-Path $CoreDir 'Mono.Cecil.dll'

if (-not (Test-Path $preloader)) { throw "Not found: $preloader" }
if (-not (Test-Path $cecil))     { throw "Not found: $cecil" }

if ($Revert) {
    if (-not (Test-Path $backup)) { throw "No backup at $backup" }
    Copy-Item $backup $preloader -Force
    Write-Host "Reverted $preloader from backup." -ForegroundColor Yellow
    return
}

# Work from the pristine copy so the script is idempotent.
if (Test-Path $backup) {
    Copy-Item $backup $preloader -Force
} else {
    Copy-Item $preloader $backup -Force
    Write-Host "Backed up original to $backup"
}

Add-Type -Path $cecil

$Platform_Windows = 0x25   # MonoMod.Utils.Platform.Windows
$Platform_Bits64  = 0x02   # MonoMod.Utils.Platform.Bits64

$readParams = New-Object Mono.Cecil.ReaderParameters
$readParams.ReadWrite = $true
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($preloader, $readParams)

try {
    $type = $asm.MainModule.GetTypes() | Where-Object { $_.FullName -eq 'BepInEx.Preloader.PlatformUtils' }
    if (-not $type) { throw "BepInEx.Preloader.PlatformUtils not found - BepInEx layout changed." }

    $method = $type.Methods | Where-Object { $_.Name -eq 'SetPlatform' -and $_.Parameters.Count -eq 0 }
    if (-not $method) { throw "PlatformUtils.SetPlatform() not found - BepInEx layout changed." }

    $body = $method.Body

    # Sanity check: only patch if it really does call GetPEKind.
    $usesGetPEKind = $false
    foreach ($ins in $body.Instructions) {
        if ($ins.Operand -is [Mono.Cecil.MethodReference] -and $ins.Operand.Name -eq 'GetPEKind') { $usesGetPEKind = $true }
    }
    if (-not $usesGetPEKind) {
        Write-Host "SetPlatform() does not call GetPEKind; nothing to patch." -ForegroundColor Yellow
        return
    }

    # Reuse the existing reference to PlatformHelper.set_Current from the original body.
    $setCurrent = $null
    foreach ($ins in $body.Instructions) {
        if ($ins.Operand -is [Mono.Cecil.MethodReference] -and $ins.Operand.Name -eq 'set_Current') {
            $setCurrent = $ins.Operand
        }
    }
    if (-not $setCurrent) { throw "Could not find the PlatformHelper.set_Current reference." }

    $getIntPtrSize = $asm.MainModule.ImportReference([IntPtr].GetProperty('Size').GetGetMethod())

    $body.Instructions.Clear()
    $body.Variables.Clear()
    $body.ExceptionHandlers.Clear()
    $il = $body.GetILProcessor()

    #     PlatformHelper.Current = Platform.Windows | (IntPtr.Size == 8 ? Platform.Bits64 : 0)
    $callSet = $il.Create([Mono.Cecil.Cil.OpCodes]::Call, $setCurrent)

    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4, $Platform_Windows))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Call, $getIntPtrSize))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4_8))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Bne_Un, $callSet))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ldc_I4, $Platform_Bits64))
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Or))
    $il.Append($callSet)
    $il.Append($il.Create([Mono.Cecil.Cil.OpCodes]::Ret))

    $body.MaxStackSize = 8
    $body.InitLocals = $false

    $asm.Write()
    Write-Host "Patched PlatformUtils.SetPlatform() in $preloader" -ForegroundColor Green
}
finally {
    $asm.Dispose()
}
