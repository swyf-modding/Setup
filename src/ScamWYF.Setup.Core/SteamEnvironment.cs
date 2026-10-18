using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScamWYF.Setup
{
    /// <summary>
    /// What Steam looks like on this machine: where its libraries are, and where this game's Proton
    /// prefix lives.
    /// </summary>
    /// <remarks>
    /// The paths are read rather than assumed. A game is installed to a library, not necessarily the
    /// one Steam itself lives in, and on Linux the default root moves between distributions,
    /// Flatpak, Snap and Steam's own compatibility symlinks. On Windows the fixed entries are only a
    /// last resort. Every candidate is validated by the caller, so listing one that does not exist
    /// costs nothing.
    /// </remarks>
    public sealed class SteamEnvironment
    {
        public const int GameAppId = 4954910;

        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        private SteamEnvironment(
            IReadOnlyList<string> steamRoots,
            IReadOnlyList<string> libraries,
            string primarySteamRoot,
            string compatDataDir,
            string prefixDir,
            IReadOnlyList<string> protonBuilds)
        {
            SteamRoots = steamRoots;
            Libraries = libraries;
            PrimarySteamRoot = primarySteamRoot;
            CompatDataDir = compatDataDir;
            PrefixDir = prefixDir;
            ProtonBuilds = protonBuilds;
        }

        /// <summary>Steam root folders that exist, most likely first.</summary>
        public IReadOnlyList<string> SteamRoots { get; }

        /// <summary>Every `<library>/steamapps/common` folder, deduplicated.</summary>
        public IReadOnlyList<string> Libraries { get; }

        public string PrimarySteamRoot { get; }

        /// <summary>`<library>/steamapps/compatdata/4954910`, or null when no library has one.</summary>
        public string CompatDataDir { get; }

        /// <summary>The Proton prefix, or null when the game has never been launched under Proton.</summary>
        public string PrefixDir { get; }

        /// <summary>Proton builds found under the libraries, newest-looking names first.</summary>
        public IReadOnlyList<string> ProtonBuilds { get; }

        public static SteamEnvironment Detect()
        {
            // ~/.steam/steam and ~/.steam/root are symlinks into ~/.local/share/Steam, so resolving
            // each root before deduplicating is what keeps one Steam from being listed three times.
            var roots = CandidateSteamRoots()
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(Normalize)
                .Where(Directory.Exists)
                .Select(Canonicalize)
                .Distinct(PathComparer)
                .ToList();

            var libraries = new List<string>();
            foreach (var root in roots)
            {
                AddLibrary(libraries, Path.Combine(root, "steamapps", "common"));

                var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                foreach (var library in ReadLibraryFolders(vdf))
                {
                    AddLibrary(libraries, Path.Combine(library, "steamapps", "common"));
                }
            }

            // Last resorts, Windows-only. Kept for the machine where nothing above resolved, exactly
            // as the Windows script did. Guarded on the platform because a `C:\...` path on Linux is
            // not a location, it is a string that will never exist.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                AddLibrary(libraries, @"C:\Program Files (x86)\Steam\steamapps\common");
                AddLibrary(libraries, @"D:\SteamLibrary\steamapps\common");
            }

            var compatData = FindCompatData(roots, libraries);
            var prefix = compatData == null ? null : Path.Combine(compatData, "pfx");
            if (prefix != null && !Directory.Exists(prefix)) prefix = null;

            var protonBuilds = libraries
                .SelectMany(common => SafeDirectories(common))
                .Where(dir => Path.GetFileName(dir).StartsWith("Proton", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SteamEnvironment(roots, libraries, roots.FirstOrDefault(), compatData, prefix, protonBuilds);
        }

        private static IEnumerable<string> CandidateSteamRoots()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
                yield return Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
                yield return Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "Steam");
                yield break;
            }

            var home = Environment.GetEnvironmentVariable("HOME");
            var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            // ~/.steam/steam and ~/.steam/root are usually symlinks into ~/.local/share/Steam, but
            // both spellings are used by different installs and either may be the only one present.
            yield return Combine(home, ".steam", "steam");
            yield return Combine(home, ".steam", "root");
            yield return Combine(home, ".local", "share", "Steam");
            yield return Combine(home, ".local", "share", "steam");
            if (!string.IsNullOrWhiteSpace(xdgData)) yield return Path.Combine(xdgData, "Steam");

            // Flatpak and Snap keep their own HOME, and are common on Steam Deck-adjacent desktops.
            yield return Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            yield return Combine(home, "snap", "steam", "common", ".local", "share", "Steam");

            // macOS, for completeness. The game is not sold there, but the probe should not lie.
            yield return Combine(home, "Library", "Application Support", "Steam");
        }

        private static IEnumerable<string> ReadLibraryFolders(string vdf)
        {
            if (!File.Exists(vdf)) yield break;

            string text;
            try
            {
                text = File.ReadAllText(vdf);
            }
            catch (IOException)
            {
                // A VDF that cannot be read is not worth failing over; the fixed roots are still tried.
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var library = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(library)) yield return library;
            }
        }

        private static string FindCompatData(IReadOnlyList<string> roots, IReadOnlyList<string> libraries)
        {
            var steamApps = new List<string>();
            foreach (var root in roots) steamApps.Add(Path.Combine(root, "steamapps"));
            foreach (var common in libraries) steamApps.Add(Path.GetDirectoryName(common));

            var candidates = steamApps
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Select(dir => Path.Combine(dir, "compatdata", GameAppId.ToString()))
                .Distinct(PathComparer)
                .ToList();

            // Prefer one that actually has a prefix: a stale compatdata folder without pfx is not
            // proof the game has been launched.
            return candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "pfx")))
                ?? candidates.FirstOrDefault(Directory.Exists);
        }

        private static void AddLibrary(ICollection<string> libraries, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Normalize(path);
            if (!libraries.Contains(full, PathComparer)) libraries.Add(full);
        }

        private static IEnumerable<string> SafeDirectories(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return Array.Empty<string>();
            try
            {
                return Directory.EnumerateDirectories(path).ToList();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static string Combine(string first, params string[] rest)
        {
            if (string.IsNullOrWhiteSpace(first)) return null;
            var path = first;
            foreach (var part in rest) path = Path.Combine(path, part);
            return path;
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>Resolves a symlink to its real folder, so two spellings of one Steam collapse.</summary>
        private static string Canonicalize(string path)
        {
            try
            {
                var info = new DirectoryInfo(path);
                var target = info.ResolveLinkTarget(true);
                return (target ?? info).FullName;
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
