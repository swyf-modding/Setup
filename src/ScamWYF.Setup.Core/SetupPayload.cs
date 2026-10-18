using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ScamWYF.Setup
{
    /// <summary>
    /// The binaries this tool seeds into a game: the unstripped corlib and the vendored Doorstop.
    /// </summary>
    /// <remarks>
    /// These are about 12 MB. They cannot be fetched at run time - the corlib comes from a Unity
    /// editor install and Doorstop is vendored - so they have to travel with the tool. Two sources,
    /// one shape:
    ///
    ///   * <see cref="FromDirectory"/> reads a `vendor\` folder, which is what the repository and CI
    ///     have, and what a developer wants while iterating on the corlib.
    ///   * <see cref="Embedded"/> reads the same files compiled into this assembly, which is what lets
    ///     the published launcher be one file and the CLI be one binary.
    ///
    /// Nothing else in the tool knows which it got.
    /// </remarks>
    public abstract class SetupPayload
    {
        /// <summary>The eight unstripped BCL assemblies, by name.</summary>
        public static readonly string[] CorlibAssemblyNames =
        {
            "mscorlib.dll",
            "System.dll",
            "System.Core.dll",
            "System.Configuration.dll",
            "System.Numerics.dll",
            "System.Security.dll",
            "System.Xml.dll",
            "Mono.Security.dll"
        };

        /// <summary>Doorstop's three files. The licence text travels beside them in the repo, not here.</summary>
        public static readonly string[] DoorstopFileNames =
        {
            "winhttp.dll",
            ".doorstop_version",
            "doorstop_config.ini"
        };

        private const string CorlibResourcePrefix = "payload/corlib/";
        private const string DoorstopResourcePrefix = "payload/doorstop/";

        /// <summary>A short description for the log, such as the folder or `embedded`.</summary>
        public abstract string Describe { get; }

        public abstract bool IsEmbedded { get; }

        /// <summary>Corlib assemblies actually available, by file name.</summary>
        public abstract IReadOnlyList<string> Corlib { get; }

        /// <summary>Doorstop files actually available, by file name.</summary>
        public abstract IReadOnlyList<string> Doorstop { get; }

        public abstract Stream OpenCorlib(string name);

        public abstract Stream OpenDoorstop(string name);

        public static SetupPayload FromDirectory(string vendorRoot)
        {
            if (string.IsNullOrWhiteSpace(vendorRoot)) throw new ArgumentException("a vendor folder is required", nameof(vendorRoot));
            return new DirectoryPayload(GameInstall.ExpandPath(vendorRoot));
        }

        public static SetupPayload Embedded()
        {
            return new EmbeddedPayload();
        }

        /// <summary>
        /// The payload to use, without being told.
        /// </summary>
        /// <remarks>
        /// Order: <c>SWYG_SETUP_PAYLOAD</c>, then a `vendor\` folder found by walking up from the
        /// running assembly, then the embedded copy. The walk is what makes `dotnet run` from the
        /// repository use the real `vendor\` folder without any configuration, while a published
        /// binary falls through to the embedded one.
        /// </remarks>
        public static SetupPayload ResolveDefault()
        {
            var fromEnvironment = GameInstall.ExpandPath(Environment.GetEnvironmentVariable("SWYG_SETUP_PAYLOAD"));
            if (!string.IsNullOrWhiteSpace(fromEnvironment) && LooksLikeVendor(fromEnvironment))
            {
                return FromDirectory(fromEnvironment);
            }

            var directory = FindVendorUpwards(AppContext.BaseDirectory);
            if (directory != null) return FromDirectory(directory);

            return Embedded();
        }

        private static bool LooksLikeVendor(string root)
        {
            return Directory.Exists(Path.Combine(root, "corlib")) && Directory.Exists(Path.Combine(root, "doorstop"));
        }

        private static string FindVendorUpwards(string start)
        {
            if (string.IsNullOrWhiteSpace(start)) return null;

            var current = new DirectoryInfo(start);
            for (var depth = 0; current != null && depth < 10; depth++)
            {
                var candidate = Path.Combine(current.FullName, "vendor");
                if (LooksLikeVendor(candidate)) return candidate;

                // A Setup checkout has src\ and vendor\ side by side, and the CLI runs out of
                // src\ScamWYF.Setup.Cli\bin\...\ so walking up finds it from there.
                current = current.Parent;
            }

            return null;
        }

        private sealed class DirectoryPayload : SetupPayload
        {
            private readonly string _corlib;
            private readonly string _doorstop;

            public DirectoryPayload(string vendorRoot)
            {
                _corlib = Path.Combine(vendorRoot, "corlib");
                _doorstop = Path.Combine(vendorRoot, "doorstop");

                if (!Directory.Exists(_corlib)) throw new SetupException("Payload is missing corlib: " + _corlib);
                if (!Directory.Exists(_doorstop)) throw new SetupException("Payload is missing doorstop: " + _doorstop);

                Corlib = List(_corlib, "*.dll");
                Doorstop = DoorstopFileNames.Where(n => File.Exists(Path.Combine(_doorstop, n))).ToList();
            }

            public override string Describe => _corlib;
            public override bool IsEmbedded => false;
            public override IReadOnlyList<string> Corlib { get; }
            public override IReadOnlyList<string> Doorstop { get; }

            public override Stream OpenCorlib(string name) => File.OpenRead(Path.Combine(_corlib, name));
            public override Stream OpenDoorstop(string name) => File.OpenRead(Path.Combine(_doorstop, name));

            private static IReadOnlyList<string> List(string directory, string pattern)
            {
                return Directory.EnumerateFiles(directory, pattern)
                    .Select(Path.GetFileName)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private sealed class EmbeddedPayload : SetupPayload
        {
            private readonly Assembly _assembly = typeof(SetupPayload).Assembly;

            public override string Describe => "embedded";
            public override bool IsEmbedded => true;
            public override IReadOnlyList<string> Corlib { get; } = CorlibAssemblyNames;
            public override IReadOnlyList<string> Doorstop { get; } = DoorstopFileNames;

            public override Stream OpenCorlib(string name) => Open(CorlibResourcePrefix + name);

            public override Stream OpenDoorstop(string name) => Open(DoorstopResourcePrefix + name);

            private Stream Open(string resource)
            {
                var stream = _assembly.GetManifestResourceStream(resource);
                if (stream == null)
                {
                    throw new SetupException(
                        "The embedded payload is incomplete: no resource named '" + resource + "'." + Environment.NewLine +
                        "This is a broken build - the payload is compiled in and cannot be fetched.");
                }
                return stream;
            }
        }
    }
}
