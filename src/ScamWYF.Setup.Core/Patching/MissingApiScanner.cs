using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace ScamWYF.Setup.Patching
{
    /// <summary>
    /// Lists every BCL member or type an assembly needs that the game's stripped corlib lacks.
    /// </summary>
    /// <remarks>
    /// A diagnostic, and the one to run on <c>BepInEx\core</c> before trusting a new BepInEx version:
    /// it says exactly how much of the BCL a build expects that this game's stripping removed.
    /// </remarks>
    public sealed class MissingApiScanner
    {
        private static readonly string[] BclNames =
        {
            "mscorlib", "System", "System.Core", "System.Xml", "System.Configuration",
            "System.Runtime.Serialization", "System.Data", "System.Drawing", "System.Numerics"
        };

        /// <summary>Returns the total number of missing references found.</summary>
        public int Run(string managed, string scanDir, ISetupLog log)
        {
            if (string.IsNullOrWhiteSpace(managed)) throw new ArgumentException("managed folder required", nameof(managed));
            if (string.IsNullOrWhiteSpace(scanDir)) throw new ArgumentException("scan folder required", nameof(scanDir));
            log = log ?? NullSetupLog.Instance;

            if (!Directory.Exists(managed)) throw new SetupException("Not found: " + managed);
            if (!Directory.Exists(scanDir)) throw new SetupException("Not found: " + scanDir);

            var bcl = new Dictionary<string, CorlibIndex>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in BclNames)
            {
                var path = Path.Combine(managed, name + ".dll");
                if (File.Exists(path)) bcl[name] = CorlibIndex.Build(path);
            }

            var results = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(scanDir, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("Mono.Cecil.Pdb.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("Mono.Cecil.Mdb.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var missing = new List<string>();

                using (var assembly = AssemblyDefinition.ReadAssembly(file))
                {
                    foreach (var reference in assembly.MainModule.GetTypeReferences())
                    {
                        var scope = reference.Scope;
                        if (scope == null || !bcl.TryGetValue(scope.Name, out var provider)) continue;

                        var typeName = reference.FullName;
                        if (provider.Types.ContainsKey(typeName)) continue;
                        if (provider.Exported.ContainsKey(typeName)) continue;

                        missing.Add("TYPE   " + scope.Name + " :: " + typeName);
                    }

                    foreach (var reference in assembly.MainModule.GetMemberReferences())
                    {
                        var declaringType = reference.DeclaringType;
                        var scope = declaringType?.Scope;
                        if (scope == null || !bcl.TryGetValue(scope.Name, out var provider)) continue;

                        var typeName = StripGeneric(declaringType.FullName);
                        if (!provider.Types.TryGetValue(typeName, out var members)) continue; // already reported as a missing type

                        var key = reference is MethodReference method
                            ? method.Name + "/" + method.Parameters.Count
                            : reference.Name + "/F";

                        if (members.Contains(key)) continue;

                        missing.Add("MEMBER " + scope.Name + " :: " + typeName + " :: " + key);
                    }
                }

                if (missing.Count > 0)
                {
                    results[fileName] = missing.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                }
            }

            var total = 0;
            foreach (var name in results.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                log.Info(string.Empty);
                log.Error("=== " + name + "  (" + results[name].Count + " missing) ===");
                foreach (var line in results[name]) log.Error("    " + line);
                total += results[name].Count;
            }

            log.Info(string.Empty);
            log.Info("TOTAL MISSING REFERENCES: " + total);
            return total;
        }

        private static string StripGeneric(string name)
        {
            var marker = name.IndexOf('<');
            return marker >= 0 ? name.Substring(0, marker) : name;
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
