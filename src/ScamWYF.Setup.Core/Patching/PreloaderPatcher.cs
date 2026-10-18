using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ScamWYF.Setup.Patching
{
    /// <summary>
    /// Rewrites BepInEx's PlatformUtils.SetPlatform to drop the GetPEKind call.
    /// </summary>
    /// <remarks>
    /// Why this exists
    /// ---------------
    /// This game ships with Unity managed stripping ON, so its mscorlib.dll is missing
    /// System.Reflection.Module.GetPEKind. BepInEx's preloader calls that method from
    /// BepInEx.Preloader.PlatformUtils.SetPlatform(), so BepInEx dies before it loads anything:
    ///
    ///     MissingMethodException: Method not found:
    ///     void System.Reflection.Module.GetPEKind(PortableExecutableKinds&amp;, ImageFileMachine&amp;)
    ///
    /// The usual workaround is Doorstop's dll_search_path_override pointing at an unstripped corlib.
    /// That works, but it swaps the whole BCL out from under the game.
    ///
    /// The call is only there to detect an ARM CPU on Windows. On an x64 build it can never do
    /// anything useful, so this rewrites that one method to:
    ///
    ///     PlatformHelper.Current = Platform.Windows | (IntPtr.Size == 8 ? Platform.Bits64 : 0);
    ///
    /// which is what SetPlatform would have computed anyway, and leaves the game's own runtime
    /// completely untouched. Only useful for diagnosis; <see cref="Revert"/> undoes it.
    ///
    /// Re-run this after updating BepInEx.
    /// </remarks>
    public sealed class PreloaderPatcher
    {
        private const int PlatformWindows = 0x25; // MonoMod.Utils.Platform.Windows
        private const int PlatformBits64 = 0x02;  // MonoMod.Utils.Platform.Bits64

        public void Run(string coreDir, bool revert, ISetupLog log)
        {
            log = log ?? NullSetupLog.Instance;
            if (string.IsNullOrWhiteSpace(coreDir)) throw new ArgumentException("a BepInEx core folder is required", nameof(coreDir));

            coreDir = Path.GetFullPath(coreDir);
            var preloader = Path.Combine(coreDir, "BepInEx.Preloader.dll");
            var backup = preloader + ".orig";

            if (!File.Exists(preloader)) throw new SetupException("Not found: " + preloader);

            if (revert)
            {
                if (!File.Exists(backup)) throw new SetupException("No backup at " + backup);
                File.Copy(backup, preloader, true);
                log.Warning("Reverted " + preloader + " from backup.");
                return;
            }

            // Work from the pristine copy so this is idempotent.
            if (File.Exists(backup))
            {
                File.Copy(backup, preloader, true);
            }
            else
            {
                File.Copy(preloader, backup, true);
                log.Info("Backed up original to " + backup);
            }

            var readerParameters = new ReaderParameters { ReadWrite = true };
            using (var assembly = AssemblyDefinition.ReadAssembly(preloader, readerParameters))
            {
                var type = assembly.MainModule.GetTypes().FirstOrDefault(t => t.FullName == "BepInEx.Preloader.PlatformUtils");
                if (type == null) throw new SetupException("BepInEx.Preloader.PlatformUtils not found - BepInEx layout changed.");

                var method = type.Methods.FirstOrDefault(m => m.Name == "SetPlatform" && m.Parameters.Count == 0);
                if (method == null) throw new SetupException("PlatformUtils.SetPlatform() not found - BepInEx layout changed.");

                var body = method.Body;

                // Sanity check: only patch if it really does call GetPEKind.
                var usesGetPEKind = body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference reference && reference.Name == "GetPEKind");

                if (!usesGetPEKind)
                {
                    log.Warning("SetPlatform() does not call GetPEKind; nothing to patch.");
                    return;
                }

                // Reuse the existing reference to PlatformHelper.set_Current from the original body.
                var setCurrent = body.Instructions
                    .Select(instruction => instruction.Operand as MethodReference)
                    .LastOrDefault(reference => reference != null && reference.Name == "set_Current");

                if (setCurrent == null) throw new SetupException("Could not find the PlatformHelper.set_Current reference.");

                var getIntPtrSize = assembly.MainModule.ImportReference(typeof(IntPtr).GetProperty("Size").GetGetMethod());

                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();

                //     PlatformHelper.Current = Platform.Windows | (IntPtr.Size == 8 ? Platform.Bits64 : 0)
                var callSet = il.Create(OpCodes.Call, setCurrent);

                il.Append(il.Create(OpCodes.Ldc_I4, PlatformWindows));
                il.Append(il.Create(OpCodes.Call, getIntPtrSize));
                il.Append(il.Create(OpCodes.Ldc_I4_8));
                il.Append(il.Create(OpCodes.Bne_Un, callSet));
                il.Append(il.Create(OpCodes.Ldc_I4, PlatformBits64));
                il.Append(il.Create(OpCodes.Or));
                il.Append(callSet);
                il.Append(il.Create(OpCodes.Ret));

                body.MaxStackSize = 8;
                body.InitLocals = false;

                assembly.Write();
                log.Success("Patched PlatformUtils.SetPlatform() in " + preloader);
            }
        }
    }
}
