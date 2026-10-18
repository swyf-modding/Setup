using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ScamWYF.Setup.Patching
{
    /// <summary>
    /// Clones <c>UnsafeOnCompleted</c> into <c>OnCompleted</c> for the awaiters that need it.
    /// </summary>
    /// <remarks>
    /// Why this exists
    /// ---------------
    /// Unity's managed stripping removes mscorlib members AND the game's other assemblies
    /// *consistently with each other*. In the shipped build:
    ///
    ///     INotifyCompletion            - deleted entirely
    ///     ICriticalNotifyCompletion    - extends nothing, has only UnsafeOnCompleted(Action)
    ///
    /// so the stripper also deleted OnCompleted(Action) from every awaiter, since nothing could call it
    /// any more. Putting an unstripped mscorlib back (which BepInEx requires) restores
    /// ICriticalNotifyCompletion : INotifyCompletion. Those awaiters are now missing an interface
    /// method, and Mono fails to build their vtable:
    ///
    ///     TypeLoadException: VTable setup of type
    ///     Cysharp.Threading.Tasks.UniTask`1+Awaiter[T] failed
    ///
    /// which kills every await in the game.
    ///
    /// This finds awaiter types that implement ICriticalNotifyCompletion but have no
    /// OnCompleted(Action), and gives them one by cloning the body of UnsafeOnCompleted. In UniTask
    /// (and in the BCL awaiters) those two methods are implemented identically, so the clone is
    /// faithful rather than a stub.
    ///
    /// The patched copy is written to the Doorstop override folder, so the game's own files in
    /// Managed\ are never modified.
    /// </remarks>
    public sealed class AwaiterUnstripper
    {
        public static readonly string[] DefaultAssemblies = { "UniTask.dll" };

        private const string Critical = "System.Runtime.CompilerServices.ICriticalNotifyCompletion";
        private const string Notify = "System.Runtime.CompilerServices.INotifyCompletion";

        public int Run(GameInstall game, IEnumerable<string> assemblies, string overrideDir, ISetupLog log)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            log = log ?? NullSetupLog.Instance;

            var names = (assemblies ?? DefaultAssemblies).ToList();
            overrideDir = overrideDir ?? game.OverrideFolder;

            if (!Directory.Exists(game.ManagedFolder))
            {
                throw new SetupException("Not found: " + game.ManagedFolder);
            }

            Directory.CreateDirectory(overrideDir);

            var total = 0;
            foreach (var name in names)
            {
                total += UnstripOne(game, name, overrideDir, log);
            }

            return total;
        }

        private static int UnstripOne(GameInstall game, string name, string overrideDir, ISetupLog log)
        {
            var source = Path.Combine(game.ManagedFolder, name);
            if (!File.Exists(source))
            {
                log.Warning("skipping " + name + " - not in Managed");
                return 0;
            }

            using (var assembly = AssemblyDefinition.ReadAssembly(source))
            {
                var patched = 0;

                foreach (var type in assembly.MainModule.GetTypes())
                {
                    var implements = type.Interfaces.Any(i =>
                        i.InterfaceType.FullName == Critical || i.InterfaceType.FullName == Notify);
                    if (!implements) continue;

                    var hasOnCompleted = type.Methods.Any(m => TakesOneArgument(m, "OnCompleted"));
                    if (hasOnCompleted) continue;

                    var unsafeOnCompleted = type.Methods.FirstOrDefault(m => TakesOneArgument(m, "UnsafeOnCompleted"));
                    if (unsafeOnCompleted == null)
                    {
                        log.Warning("  " + type.FullName + ": no UnsafeOnCompleted to clone, skipped");
                        continue;
                    }

                    if (unsafeOnCompleted.Body == null)
                    {
                        log.Warning("  " + type.FullName + ": UnsafeOnCompleted has no body, skipped");
                        continue;
                    }

                    var method = new MethodDefinition("OnCompleted", unsafeOnCompleted.Attributes, unsafeOnCompleted.ReturnType);
                    foreach (var parameter in unsafeOnCompleted.Parameters)
                    {
                        method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameter.ParameterType));
                    }
                    method.ImplAttributes = unsafeOnCompleted.ImplAttributes;
                    method.SemanticsAttributes = unsafeOnCompleted.SemanticsAttributes;
                    type.Methods.Add(method);

                    CloneBody(unsafeOnCompleted, method);

                    log.Success("  + " + type.FullName + "::OnCompleted(Action)");
                    patched++;
                }

                if (patched == 0)
                {
                    log.Warning(name + " - nothing to patch");
                    return 0;
                }

                var destination = Path.Combine(overrideDir, name);
                assembly.Write(destination);
                log.Success(name + " - added " + patched + " method(s), wrote " + destination);
                return patched;
            }
        }

        /// <summary>A method named exactly that, or an explicit interface implementation of it.</summary>
        private static bool TakesOneArgument(MethodDefinition method, string name)
        {
            if (method.Parameters.Count != 1) return false;
            return method.Name == name || method.Name.EndsWith("." + name, StringComparison.Ordinal);
        }

        /// <summary>
        /// Copies the source's IL into the target, repointing branches and exception handlers at the
        /// cloned instructions. This is the faithful twin the remarks describe.
        /// </summary>
        private static void CloneBody(MethodDefinition source, MethodDefinition target)
        {
            var sourceBody = source.Body;
            var targetBody = target.Body;

            targetBody.InitLocals = sourceBody.InitLocals;

            foreach (var variable in sourceBody.Variables)
            {
                targetBody.Variables.Add(new VariableDefinition(variable.VariableType));
            }

            var il = targetBody.GetILProcessor();
            var map = new Dictionary<Instruction, Instruction>();

            // Pass 1 - copy instructions, remembering what mapped to what. Branch operands are
            // placeholders until every instruction exists.
            foreach (var instruction in sourceBody.Instructions)
            {
                var opcode = instruction.OpCode;
                var operand = instruction.Operand;

                Instruction created;
                if (operand == null) created = il.Create(opcode);
                else if (operand is Instruction) created = il.Create(opcode, sourceBody.Instructions[0]);
                else if (operand is Instruction[]) created = il.Create(opcode, Array.Empty<Instruction>());
                else if (operand is VariableDefinition variable) created = il.Create(opcode, targetBody.Variables[variable.Index]);
                else if (operand is ParameterDefinition parameter)
                {
                    created = il.Create(opcode, parameter.Index < 0 ? targetBody.ThisParameter : target.Parameters[parameter.Index]);
                }
                else if (operand is MethodReference method) created = il.Create(opcode, method);
                else if (operand is FieldReference field) created = il.Create(opcode, field);
                else if (operand is TypeReference type) created = il.Create(opcode, type);
                else if (operand is string text) created = il.Create(opcode, text);
                else if (operand is sbyte signedByte) created = il.Create(opcode, signedByte);
                else if (operand is byte unsignedByte) created = il.Create(opcode, unsignedByte);
                else if (operand is int integer) created = il.Create(opcode, integer);
                else if (operand is long longInteger) created = il.Create(opcode, longInteger);
                else if (operand is float single) created = il.Create(opcode, single);
                else if (operand is double doublePrecision) created = il.Create(opcode, doublePrecision);
                else throw new SetupException("Unhandled operand type " + operand.GetType().FullName + " in " + source.FullName);

                il.Append(created);
                map[instruction] = created;
            }

            // Pass 2 - repoint branches at the cloned instructions.
            for (var i = 0; i < sourceBody.Instructions.Count; i++)
            {
                var old = sourceBody.Instructions[i];
                var created = targetBody.Instructions[i];

                if (old.Operand is Instruction branchTarget)
                {
                    created.Operand = map[branchTarget];
                }
                else if (old.Operand is Instruction[] targets)
                {
                    created.Operand = targets.Select(t => map[t]).ToArray();
                }
            }

            foreach (var handler in sourceBody.ExceptionHandlers)
            {
                var cloned = new ExceptionHandler(handler.HandlerType);
                if (handler.TryStart != null) cloned.TryStart = map[handler.TryStart];
                if (handler.TryEnd != null) cloned.TryEnd = map[handler.TryEnd];
                if (handler.HandlerStart != null) cloned.HandlerStart = map[handler.HandlerStart];
                if (handler.HandlerEnd != null) cloned.HandlerEnd = map[handler.HandlerEnd];
                if (handler.FilterStart != null) cloned.FilterStart = map[handler.FilterStart];
                cloned.CatchType = handler.CatchType;
                targetBody.ExceptionHandlers.Add(cloned);
            }

            targetBody.MaxStackSize = sourceBody.MaxStackSize;
        }
    }
}
