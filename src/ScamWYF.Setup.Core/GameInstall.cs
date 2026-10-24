using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScamWYF.Setup
{
    /// <summary>
    /// A game install on disk, and what is in it.
    /// </summary>
    /// <remarks>
    /// Probing rather than assuming, because the interesting case is a partly-installed game: a player
    /// who has BepInEx but not the corlib override gets a crash they cannot explain, and the fix is
    /// exactly this tool. So each piece is reported separately instead of as one installed / not
    /// installed answer.
    ///
    /// All paths are built with <see cref="System.IO.Path.Combine(string, string)"/> rather than the
    /// `\` literals the Windows-only Switchboard used, so this is correct on Linux too.
    /// </remarks>
    public sealed class GameInstall
    {
        public const string FolderName = "Scam With Your Friends";
        public const string DataFolderName = "Scam With Your Friends_Data";
        public const string ExecutableName = "Scam With Your Friends.exe";
        public const string BepInExVersion = "5.4.23.5";

        /// <summary>
        /// The Steam launch option that makes Wine prefer the game's own winhttp.dll over its
        /// builtin copy. Must be <c>n,b</c> and not <c>n</c> alone: with only <c>n</c> the game
        /// crashes in Unity's window code a minute into startup on current Proton builds.
        /// </summary>
        public const string ProtonLaunchOption = "WINEDLLOVERRIDES=\"winhttp=n,b\" %command% -whisper-cpu";

        private GameInstall(string path, SteamEnvironment steam)
        {
            Path = path;
            Steam = steam;
        }

        public string Path { get; }

        public SteamEnvironment Steam { get; }

        public string DataFolder => System.IO.Path.Combine(Path, DataFolderName);
        public string ManagedFolder => System.IO.Path.Combine(DataFolder, "Managed");
        public string BepInExRoot => System.IO.Path.Combine(Path, "BepInEx");
        public string BepInExCore => System.IO.Path.Combine(BepInExRoot, "core");
        public string PluginsFolder => System.IO.Path.Combine(BepInExRoot, "plugins");
        public string ConfigFolder => System.IO.Path.Combine(BepInExRoot, "config");
        public string OverrideFolder => System.IO.Path.Combine(Path, "unstripped_corlib");
        public string DoorstopConfig => System.IO.Path.Combine(Path, "doorstop_config.ini");
        public string LogFile => System.IO.Path.Combine(BepInExRoot, "LogOutput.log");
        public string Executable => System.IO.Path.Combine(Path, ExecutableName);

        /// <summary>Everything that has to be true for the game to load with BepInEx.</summary>
        public bool HasGame => File.Exists(System.IO.Path.Combine(ManagedFolder, "mscorlib.dll"));

        /// <summary>Doorstop is installed. The loader half, which starts everything.</summary>
        public bool HasDoorstop => File.Exists(System.IO.Path.Combine(Path, "winhttp.dll"));

        /// <summary>BepInEx is installed.</summary>
        public bool HasBepInEx => File.Exists(System.IO.Path.Combine(BepInExCore, "BepInEx.dll"));

        /// <summary>Doorstop is pointed at the corlib override, which is what makes it usable.</summary>
        public bool HasCorlibOverride => File.Exists(System.IO.Path.Combine(OverrideFolder, "mscorlib.dll"));

        /// <summary>
        /// Patched copies in the override folder that were built from an older build of the game.
        /// </summary>
        /// <remarks>
        /// Doorstop searches the override folder before <c>Managed\</c>, so one of these shadows
        /// the assembly the updated game shipped. The failure is not a managed exception: Unity
        /// serialises InputSystem settings into globalgamemanagers, so a stale
        /// <c>Unity.InputSystem.dll</c> makes the engine misread its own data and crash on startup
        /// reporting that the file is corrupted. Verifying the install through Steam does not help,
        /// because the file is fine and the reader is wrong.
        ///
        /// Re-running setup fixes it. Empty on an install set up before this was recorded; see
        /// <see cref="DerivedOverrides.Derived"/>.
        /// </remarks>
        public IReadOnlyList<string> StaleOverrides => DerivedOverrides.Stale(this);

        /// <summary>No patched copy in the override folder predates the installed game.</summary>
        public bool CorlibOverrideIsCurrent => StaleOverrides.Count == 0;

        public bool DoorstopPointsAtOverride
        {
            get
            {
                try
                {
                    return File.Exists(DoorstopConfig) &&
                        Regex.IsMatch(File.ReadAllText(DoorstopConfig),
                            "dll_search_path_override\\s*=\\s*unstripped_corlib");
                }
                catch (IOException)
                {
                    return false;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        public bool Exists => Directory.Exists(Path);

        /// <summary>Setup has everything it needs to work.</summary>
        public bool IsReady => HasGame && HasDoorstop && HasBepInEx && HasCorlibOverride &&
            DoorstopPointsAtOverride && CorlibOverrideIsCurrent;

        /// <summary>
        /// The install to work on.
        /// </summary>
        /// <remarks>
        /// Resolution order: an explicit path, then <c>SWYG_GAME_DIR</c>, then every Steam library.
        /// An explicit path means exactly that path and is returned even when it is not an install -
        /// the caller shows that folder and says what is missing, which is answerable. Substituting a
        /// Steam location that happened to exist would mean quietly working on a game the user did
        /// not ask about.
        ///
        /// This does not throw when nothing is found: the CLI and the GUI both need to report where
        /// they looked, which an exception cannot do. <see cref="ResolveOrThrow"/> is the variant for
        /// callers that would rather fail.
        /// </remarks>
        public static GameInstall Resolve(string explicitPath, SteamEnvironment steam = null)
        {
            steam = steam ?? SteamEnvironment.Detect();

            var expanded = ExpandPath(explicitPath);
            if (!string.IsNullOrWhiteSpace(expanded)) return new GameInstall(expanded, steam);

            var environment = ExpandPath(Environment.GetEnvironmentVariable("SWYG_GAME_DIR"));
            if (!string.IsNullOrWhiteSpace(environment)) return new GameInstall(environment, steam);

            var tried = new List<string>();
            foreach (var candidate in Candidates(steam))
            {
                var install = new GameInstall(candidate, steam);
                if (install.HasGame || install.HasBepInEx || install.HasDoorstop) return install;
                tried.Add(candidate);
            }

            // Nothing anywhere. Return the first place looked so the caller can show it.
            return new GameInstall(tried.FirstOrDefault() ?? string.Empty, steam);
        }

        /// <summary>The install to work on, or an exception naming everywhere that was tried.</summary>
        public static GameInstall ResolveOrThrow(string explicitPath, bool requireBepInEx = false, SteamEnvironment steam = null)
        {
            steam = steam ?? SteamEnvironment.Detect();
            var install = Resolve(explicitPath, steam);

            var ok = install.HasGame && (!requireBepInEx || install.HasBepInEx);
            if (ok) return install;

            var wanted = requireBepInEx ? "Managed and BepInEx\\core" : "Scam With Your Friends_Data/Managed";
            var hints = string.Join(Environment.NewLine + "  ",
                new[] { install.Path }.Concat(Candidates(steam)).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());

            throw new SetupException(
                "Could not find the game install (needs " + wanted + ")." + Environment.NewLine +
                "Pass --game-dir, or set SWYG_GAME_DIR." + Environment.NewLine + Environment.NewLine +
                "Looked in:" + Environment.NewLine + "  " + hints);
        }

        public static IEnumerable<string> Candidates(SteamEnvironment steam)
        {
            foreach (var library in steam.Libraries)
            {
                yield return System.IO.Path.Combine(library, FolderName);
            }
        }

        public IEnumerable<string> ManagedAssemblies()
        {
            if (!Directory.Exists(ManagedFolder)) return Array.Empty<string>();
            try
            {
                return Directory.EnumerateFiles(ManagedFolder, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>A one-line summary for a status bar or the console.</summary>
        public string Summary()
        {
            if (!Exists) return "game folder not found";
            if (!HasGame) return "not a game install (no " + DataFolderName + "/Managed)";

            var missing = new List<string>();
            if (!HasDoorstop) missing.Add("loader");
            if (!DoorstopPointsAtOverride) missing.Add("doorstop config");
            if (!HasBepInEx) missing.Add("BepInEx");
            if (!HasCorlibOverride) missing.Add("corlib override");

            if (missing.Count > 0) return "needs setup: " + string.Join(", ", missing);

            // Everything is present but some of it is older than the game. Said separately from
            // the missing-pieces list, because "the game updated" is a different situation from
            // "this was never set up" and the player reaches it without changing anything.
            var stale = StaleOverrides;
            return stale.Count == 0
                ? "ready"
                : "needs setup: the game updated (" + string.Join(", ", stale) + " predate it)";
        }

        /// <summary>Expands a leading ~ and makes a path absolute. Returns null for empty input.</summary>
        public static string ExpandPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            {
                var home = Environment.GetEnvironmentVariable("HOME")
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                {
                    path = path.Length == 1 ? home : System.IO.Path.Combine(home, path.Substring(2));
                }
            }

            try
            {
                return System.IO.Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }
    }
}
