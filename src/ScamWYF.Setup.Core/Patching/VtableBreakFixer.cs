using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ScamWYF.Setup.Patching
{
    /// <summary>What a vtable scan found, and whether a fix pass wrote anything.</summary>
    public sealed class VtableBreakResult
    {
        public VtableBreakResult(int breakages, int patchedAssemblies, IReadOnlyList<string> details)
        {
            Breakages = breakages;
            PatchedAssemblies = patchedAssemblies;
            Details = details ?? Array.Empty<string>();
        }

        /// <summary>Types that cannot build a vtable against the unstripped corlib.</summary>
        public int Breakages { get; }

        public int PatchedAssemblies { get; }

        public IReadOnlyList<string> Details { get; }

        public bool Clean => Breakages == 0;
    }

    /// <summary>
    /// Finds every type that cannot build a vtable against the unstripped corlib, and optionally
    /// patches it.
    /// </summary>
    /// <remarks>
    /// The full set of "stripper pact" breakages. Unity's stripper narrows BCL interfaces and then
    /// deletes the now-unreachable implementations from the game's own assemblies, consistently.
    /// Restoring an unstripped corlib widens those interfaces again, and any implementing type that
    /// lost a method can no longer have its vtable built:
    ///
    ///     TypeLoadException: VTable setup of type &lt;X&gt; failed
    ///
    /// Done in two precise steps:
    ///
    ///   1. diff every interface in the shipped corlib against the unstripped one, and keep the ones
    ///      whose required method set GREW (new methods, or new base interfaces)
    ///   2. find every type in the game that implements one of those and does not provide the added
    ///      methods
    ///
    /// With <c>fix</c> the missing methods are added as bodies that throw NotImplementedException.
    /// That is safe precisely because the stripper only removes what nothing reachable calls: the
    /// method exists to satisfy vtable layout, and if one ever does fire you get a clear exception
    /// naming it rather than silent corruption from a faked return value.
    /// </remarks>
    public sealed class VtableBreakFixer
    {
        private static readonly string[] Corlibs = { "mscorlib", "System", "System.Core", "System.Xml", "System.Configuration" };

        public VtableBreakResult Run(
            GameInstall game,
            bool fix,
            IEnumerable<string> assemblies,
            string unstripped,
            ISetupLog log)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            log = log ?? NullSetupLog.Instance;

            var managed = game.ManagedFolder;
            if (!Directory.Exists(managed)) throw new SetupException("Not found: " + managed);

            unstripped = unstripped ?? game.OverrideFolder;
            if (!Directory.Exists(unstripped)) throw new SetupException("Not found: " + unstripped);

            var only = assemblies == null ? null : new HashSet<string>(assemblies, StringComparer.OrdinalIgnoreCase);

            // A patched copy left over from a previous game build is thrown away before anything
            // reads it. Patching it again would find nothing to do - it is already patched - so it
            // would survive every re-run while shadowing the assembly the updated game shipped.
            // Only when fixing: a scan is meant to report the install, not change it.
            var tracked = DerivedOverrides.Tracks(game, unstripped);
            if (fix && tracked) DerivedOverrides.DiscardStale(game, log);

            // Prefer the override folder's copy of an assembly, since that is what actually loads.
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(unstripped);
            resolver.AddSearchDirectory(managed);

            var readerParameters = new ReaderParameters { AssemblyResolver = resolver };

            var widened = FindWidenedInterfaces(managed, unstripped, readerParameters, log);

            log.Heading("types that will fail vtable setup");
            if (widened.Count == 0)
            {
                log.Success("No vtable breakages remain.");
                return new VtableBreakResult(0, 0, Array.Empty<string>());
            }

            var scan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(managed, "*.dll")) scan[Path.GetFileName(file)] = file;
            foreach (var file in Directory.EnumerateFiles(unstripped, "*.dll")) scan[Path.GetFileName(file)] = file;

            var breakages = 0;
            var patchedAssemblies = 0;
            var details = new List<string>();

            foreach (var name in scan.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(name, "mscorlib.dll", StringComparison.OrdinalIgnoreCase)) continue;

                AssemblyDefinition assembly;
                try
                {
                    assembly = AssemblyDefinition.ReadAssembly(scan[name], readerParameters);
                }
                catch (Exception)
                {
                    // An assembly that cannot be read is not a vtable breakage; skip it the way the
                    // PowerShell tool did rather than failing the whole scan.
                    continue;
                }

                using (assembly)
                {
                    var bad = new List<string>();
                    var todo = new List<PendingMethod>();

                    foreach (var type in assembly.MainModule.GetTypes())
                    {
                        if (type.IsInterface) continue;

                        foreach (var implementation in type.Interfaces)
                        {
                            var interfaceName = implementation.InterfaceType.FullName;
                            var genericMarker = interfaceName.IndexOf('<');
                            if (genericMarker >= 0) interfaceName = interfaceName.Substring(0, genericMarker);

                            if (!widened.TryGetValue(interfaceName, out var required)) continue;

                            foreach (var need in required)
                            {
                                var parts = need.Split('/');
                                var methodName = parts[0];
                                var argumentCount = int.Parse(parts[1]);

                                if (ProvidesMethod(type, methodName, argumentCount)) continue;

                                bad.Add(type.FullName + "  needs  " + need + "   (from " + interfaceName + ")");
                                todo.Add(new PendingMethod(type, methodName, argumentCount, implementation.InterfaceType));
                            }
                        }
                    }

                    if (bad.Count == 0) continue;

                    var origin = scan[name].StartsWith(unstripped, StringComparison.Ordinal) ? "override copy" : "Managed copy";
                    log.Info(string.Empty);
                    log.Error("  " + name + "  [" + origin + "]");
                    foreach (var line in bad.Distinct().OrderBy(x => x, StringComparer.Ordinal))
                    {
                        log.Error("      " + line);
                        details.Add(name + ": " + line);
                    }

                    breakages += bad.Count;

                    if (!fix) continue;
                    if (only != null && !only.Contains(name)) continue;

                    foreach (var pending in todo)
                    {
                        if (AddThrowingStub(assembly, pending, log)) { }
                    }

                    var destination = Path.Combine(unstripped, name);
                    assembly.Write(destination);
                    log.Success("      wrote " + destination);
                    patchedAssemblies++;
                }
            }

            // Every derived copy now standing is either one this run wrote from the current
            // Managed\, or one that was already current. Recording the lot is what lets the next
            // game update be spotted, rather than inferred from a crash.
            if (fix && tracked) DerivedOverrides.RecordAll(game);

            log.Info(string.Empty);
            if (breakages == 0)
            {
                log.Success("No vtable breakages remain.");
            }
            else if (fix)
            {
                log.Info("Patched " + patchedAssemblies + " assembly/assemblies. Re-run without --fix to confirm.");
            }
            else
            {
                log.Error(breakages + " breakage(s). Re-run with --fix (optionally --assemblies a.dll,b.dll) to patch them.");
            }

            return new VtableBreakResult(breakages, patchedAssemblies, details);
        }

        /// <summary>Step 1: which interfaces gained required methods under the unstripped corlib?</summary>
        private static Dictionary<string, List<string>> FindWidenedInterfaces(
            string managed,
            string unstripped,
            ReaderParameters readerParameters,
            ISetupLog log)
        {
            var widened = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            log.Heading("interfaces whose contract widened under the unstripped corlib");
            var any = false;

            foreach (var corlib in Corlibs)
            {
                var shippedPath = Path.Combine(managed, corlib + ".dll");
                var unstrippedPath = Path.Combine(unstripped, corlib + ".dll");
                if (!File.Exists(shippedPath) || !File.Exists(unstrippedPath)) continue;

                Dictionary<string, TypeDefinition> shipped;
                Dictionary<string, TypeDefinition> unstrippedMap;
                try
                {
                    shipped = IndexInterfaces(shippedPath, readerParameters);
                    unstrippedMap = IndexInterfaces(unstrippedPath, readerParameters);
                }
                catch (Exception ex)
                {
                    // A corlib that cannot be read is a problem, but it is not this scan's job to
                    // diagnose it, and failing here would hide every other result. Say so and move on.
                    log.Warning("  could not read " + corlib + ".dll (" + ex.Message + "); skipped");
                    continue;
                }

                foreach (var iface in unstrippedMap.Keys)
                {
                    if (!shipped.ContainsKey(iface)) continue;

                    var shippedRequired = RequiredMethods(iface, shipped, new HashSet<string>(StringComparer.Ordinal));
                    var unstrippedRequired = RequiredMethods(iface, unstrippedMap, new HashSet<string>(StringComparer.Ordinal));

                    var added = unstrippedRequired.Where(m => !shippedRequired.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList();
                    if (added.Count == 0) continue;

                    widened[iface] = added;
                    any = true;
                    log.Warning("  " + iface);
                    log.Info("      now also requires: " + string.Join(", ", added));
                }
            }

            if (!any) log.Info("  (none)");

            return widened;
        }

        private static Dictionary<string, TypeDefinition> IndexInterfaces(string path, ReaderParameters readerParameters)
        {
            var map = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);

            using (var assembly = AssemblyDefinition.ReadAssembly(path, readerParameters))
            {
                foreach (var type in assembly.MainModule.GetTypes())
                {
                    if (!type.IsInterface) continue;
                    map[type.FullName] = type;
                }
            }

            return map;
        }

        private static HashSet<string> RequiredMethods(string name, Dictionary<string, TypeDefinition> map, HashSet<string> seen)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (!map.TryGetValue(name, out var type)) return result;
            if (!seen.Add(name)) return result;

            foreach (var method in type.Methods)
            {
                result.Add(method.Name + "/" + method.Parameters.Count);
            }

            foreach (var inherited in type.Interfaces)
            {
                foreach (var method in RequiredMethods(inherited.InterfaceType.FullName, map, seen))
                {
                    result.Add(method);
                }
            }

            return result;
        }

        /// <summary>
        /// Walks up the base chain. A method inherited from a base class satisfies the interface just
        /// fine, so without this we report a pile of false positives.
        /// </summary>
        private static bool ProvidesMethod(TypeDefinition type, string name, int argumentCount)
        {
            var current = type;
            var guard = 0;

            while (current != null && guard < 32)
            {
                guard++;

                foreach (var method in current.Methods)
                {
                    if (method.Parameters.Count != argumentCount) continue;
                    if (method.Name == name || method.Name.EndsWith("." + name, StringComparison.Ordinal)) return true;
                }

                if (current.BaseType == null) return false;

                // Keep walking across assembly boundaries - every class bottoms out at System.Object,
                // so bailing out at the first external base would mark everything as satisfied.
                try
                {
                    current = current.BaseType.Resolve();
                }
                catch (Exception)
                {
                    current = null;
                }

                // Unresolvable: report it rather than hide it.
                if (current == null) return false;
            }

            return false;
        }

        private static bool AddThrowingStub(AssemblyDefinition assembly, PendingMethod pending, ISetupLog log)
        {
            var interfaceType = pending.InterfaceType;
            MethodDefinition signature = null;

            try
            {
                var resolved = interfaceType.Resolve();
                if (resolved != null)
                {
                    signature = resolved.Methods.FirstOrDefault(m =>
                        m.Name == pending.Name && m.Parameters.Count == pending.ArgumentCount);
                }
            }
            catch (Exception)
            {
                signature = null;
            }

            if (signature == null)
            {
                log.Warning("      cannot resolve signature for " + pending.Name + "; skipped");
                return false;
            }

            // The interface method's signature is written in terms of the interface's own type
            // parameters (IBufferWriter<T>.GetSpan returns Span<T>). Substitute the actual arguments
            // from the implemented interface before importing, or the import fails.
            var substitutes = new Dictionary<string, TypeReference>(StringComparer.Ordinal);
            if (interfaceType is GenericInstanceType generic)
            {
                var definition = generic.Resolve();
                if (definition != null)
                {
                    for (var i = 0; i < definition.GenericParameters.Count && i < generic.GenericArguments.Count; i++)
                    {
                        substitutes[definition.GenericParameters[i].Name] = generic.GenericArguments[i];
                    }
                }
            }

            var module = assembly.MainModule;
            var returnType = ImportSubstituted(signature.ReturnType, substitutes, module);
            if (returnType == null)
            {
                log.Warning("      cannot build signature for " + pending.Name + "; skipped");
                return false;
            }

            const MethodAttributes attributes = MethodAttributes.Public | MethodAttributes.Final |
                                                MethodAttributes.Virtual | MethodAttributes.HideBySig |
                                                MethodAttributes.NewSlot;

            var method = new MethodDefinition(pending.Name, attributes, returnType);

            foreach (var parameter in signature.Parameters)
            {
                var parameterType = ImportSubstituted(parameter.ParameterType, substitutes, module);
                if (parameterType == null)
                {
                    log.Warning("      cannot build parameters for " + pending.Name + "; skipped");
                    return false;
                }

                method.Parameters.Add(new ParameterDefinition(parameter.Name, parameter.Attributes, parameterType));
            }

            var constructor = module.ImportReference(typeof(NotImplementedException).GetConstructor(Type.EmptyTypes));
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Newobj, constructor));
            il.Append(il.Create(OpCodes.Throw));
            method.Body.MaxStackSize = 1;

            pending.Type.Methods.Add(method);
            log.Success("      + " + pending.Type.FullName + "::" + pending.Name + " (throwing stub)");
            return true;
        }

        private static TypeReference ImportSubstituted(TypeReference type, Dictionary<string, TypeReference> substitutes, ModuleDefinition module)
        {
            if (type == null) return null;

            if (type is GenericParameter genericParameter)
            {
                if (substitutes.TryGetValue(genericParameter.Name, out var replacement)) return module.ImportReference(replacement);
                return null;
            }

            if (type is GenericInstanceType genericInstance)
            {
                var element = module.ImportReference(genericInstance.ElementType);
                var imported = new GenericInstanceType(element);
                foreach (var argument in genericInstance.GenericArguments)
                {
                    var substituted = ImportSubstituted(argument, substitutes, module);
                    if (substituted == null) return null;
                    imported.GenericArguments.Add(substituted);
                }
                return imported;
            }

            if (type is ByReferenceType byReference)
            {
                var inner = ImportSubstituted(byReference.ElementType, substitutes, module);
                return inner == null ? null : new ByReferenceType(inner);
            }

            if (type is ArrayType array)
            {
                var inner = ImportSubstituted(array.ElementType, substitutes, module);
                return inner == null ? null : new ArrayType(inner, array.Rank);
            }

            try
            {
                return module.ImportReference(type);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private sealed class PendingMethod
        {
            public PendingMethod(TypeDefinition type, string name, int argumentCount, TypeReference interfaceType)
            {
                Type = type;
                Name = name;
                ArgumentCount = argumentCount;
                InterfaceType = interfaceType;
            }

            public TypeDefinition Type { get; }
            public string Name { get; }
            public int ArgumentCount { get; }
            public TypeReference InterfaceType { get; }
        }
    }
}
