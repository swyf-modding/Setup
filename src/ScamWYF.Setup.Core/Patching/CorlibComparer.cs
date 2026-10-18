using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace ScamWYF.Setup.Patching
{
    /// <summary>
    /// Diffs the shipped BCL against an unstripped one: missing types, members and forwarders.
    /// </summary>
    /// <remarks>
    /// A diagnostic. It answers "is the unstripped corlib actually a superset of the shipped one",
    /// which is the first thing to check when a new Unity version moves the goalposts, and it prints
    /// what the problem assemblies need that the unstripped corlib does not provide.
    /// </remarks>
    public sealed class CorlibComparer
    {
        private static readonly string[] CorlibNames =
        {
            "mscorlib", "System", "System.Core", "System.Xml", "System.Configuration", "System.Runtime.Serialization"
        };

        private static readonly string[] ProblemAssemblies = { "System.Text.Json", "UniTask", "Newtonsoft.Json" };

        public void Run(string managed, string unstripped, ISetupLog log)
        {
            if (string.IsNullOrWhiteSpace(managed)) throw new ArgumentException("managed folder required", nameof(managed));
            if (string.IsNullOrWhiteSpace(unstripped)) throw new ArgumentException("unstripped folder required", nameof(unstripped));
            log = log ?? NullSetupLog.Instance;

            if (!Directory.Exists(managed)) throw new SetupException("Not found: " + managed);
            if (!Directory.Exists(unstripped)) throw new SetupException("Not found: " + unstripped);

            log.Info("Indexing corlibs...");

            var shipped = new Dictionary<string, CorlibIndex>(StringComparer.OrdinalIgnoreCase);
            var unstrippedIndex = new Dictionary<string, CorlibIndex>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in CorlibNames)
            {
                var shippedPath = Path.Combine(managed, name + ".dll");
                var unstrippedPath = Path.Combine(unstripped, name + ".dll");
                if (File.Exists(shippedPath)) shipped[name] = CorlibIndex.Build(shippedPath);
                if (File.Exists(unstrippedPath)) unstrippedIndex[name] = CorlibIndex.Build(unstrippedPath);
            }

            log.Info("game corlibs:      " + string.Join(", ", shipped.Keys));
            log.Info("unstripped corlibs:" + string.Join(", ", unstrippedIndex.Keys));

            // ---- 1. Is the unstripped corlib actually a superset of the shipped one? ----
            foreach (var name in shipped.Keys)
            {
                if (!unstrippedIndex.TryGetValue(name, out var reference)) continue;

                var game = shipped[name];
                var missingTypes = new List<string>();
                var missingMembers = new List<string>();

                foreach (var typeName in game.Types.Keys)
                {
                    if (!reference.Types.TryGetValue(typeName, out var referenceMembers))
                    {
                        if (!reference.Exported.ContainsKey(typeName)) missingTypes.Add(typeName);
                        continue;
                    }

                    foreach (var member in game.Types[typeName])
                    {
                        if (!referenceMembers.Contains(member)) missingMembers.Add(typeName + " :: " + member);
                    }
                }

                log.Info(string.Empty);
                log.Warning("=== " + name + " : present in SHIPPED but absent from UNSTRIPPED ===");
                log.Info("    types missing  : " + missingTypes.Count);
                log.Info("    members missing: " + missingMembers.Count);
                foreach (var type in missingTypes.Take(25)) log.Info("      TYPE   " + type);
                foreach (var member in missingMembers.Take(40)) log.Info("      MEMBER " + member);
            }

            // ---- 2. What do the problem assemblies actually need? ----
            foreach (var target in ProblemAssemblies)
            {
                var path = Path.Combine(managed, target + ".dll");
                if (!File.Exists(path)) continue;

                var unresolved = new List<string>();
                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    foreach (var reference in assembly.MainModule.GetMemberReferences())
                    {
                        var declaringType = reference.DeclaringType;
                        var scope = declaringType?.Scope;
                        if (scope == null) continue;

                        var scopeName = scope.Name;
                        if (!unstrippedIndex.TryGetValue(scopeName, out var provider)) continue;

                        var typeName = StripGeneric(declaringType.FullName);
                        if (!provider.Types.TryGetValue(typeName, out var members))
                        {
                            if (!provider.Exported.ContainsKey(typeName)) unresolved.Add(scopeName + " : TYPE MISSING  " + typeName);
                            continue;
                        }

                        var key = MemberKey(reference);
                        if (!members.Contains(key)) unresolved.Add(scopeName + " : MEMBER MISSING " + typeName + " :: " + key);
                    }
                }

                log.Info(string.Empty);
                log.Error("=== " + target + " needs these, unstripped corlib does NOT provide them ===");
                if (unresolved.Count == 0)
                {
                    log.Success("    (nothing missing)");
                }
                else
                {
                    foreach (var line in unresolved.Distinct().OrderBy(x => x, StringComparer.Ordinal)) log.Error("    " + line);
                }
            }
        }

        private static string StripGeneric(string name)
        {
            var marker = name.IndexOf('<');
            return marker >= 0 ? name.Substring(0, marker) : name;
        }

        private static string MemberKey(MemberReference reference)
        {
            return reference is MethodReference method
                ? method.Name + "/" + method.Parameters.Count
                : reference.Name + "/F";
        }

        private sealed class CorlibIndex
        {
            public Dictionary<string, HashSet<string>> Types { get; private set; }
            public Dictionary<string, bool> Exported { get; private set; }

            public static CorlibIndex Build(string path)
            {
                var index = new CorlibIndex
                {
                    Types = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                    Exported = new Dictionary<string, bool>(StringComparer.Ordinal)
                };

                using (var assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    foreach (var type in assembly.MainModule.GetTypes())
                    {
                        var members = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var method in type.Methods) members.Add(method.Name + "/" + method.Parameters.Count);
                        foreach (var field in type.Fields) members.Add(field.Name + "/F");
                        index.Types[type.FullName] = members;
                    }

                    foreach (var exported in assembly.MainModule.ExportedTypes)
                    {
                        index.Exported[exported.FullName] = true;
                    }
                }

                return index;
            }
        }
    }
}
