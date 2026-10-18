using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScamWYF.Setup
{
    /// <summary>How the game was asked to start.</summary>
    public enum LaunchMethod
    {
        /// <summary>Through Steam, which is how the game is normally played.</summary>
        Steam,

        /// <summary>Through a Proton build directly, for Lutris, Heroic or a bare terminal.</summary>
        Direct,

        /// <summary>The game's own executable, on Windows.</summary>
        Native
    }

    /// <summary>
    /// Starting the game on Linux, and the one Wine setting that makes mods load there.
    /// </summary>
    /// <remarks>
    /// The game is a Windows build, so on Linux it runs under Proton. BepInEx and Doorstop are
    /// therefore the Windows flavour - but Wine prefers its own builtin `winhttp.dll` over the copy
    /// Doorstop installs in the game folder, and without an override Doorstop is never loaded. The
    /// game then starts completely unmodded with no error anywhere, which is exactly the failure that
    /// is hard to notice and hard to explain. Everything in this class exists to make that impossible:
    /// either the override is in place, or the tool says so and prints the launch option to paste.
    /// </remarks>
    public sealed class ProtonIntegration
    {
        // `n` alone is mis-stable: on current Proton builds the game then crashes a minute or so
        // into startup inside Unity's window code (observed across several Proton versions). With
        // `native,builtin` Wine keeps a builtin fallback for the proxy's imports and the game is
        // stable, so the expected value and the check both require the pair.
        private static readonly Regex OverridePattern =
            new Regex("\"winhttp\"\\s*=\\s*\"(native|n)\\s*,\\s*(builtin|b)\"", RegexOptions.IgnoreCase);

        public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>Is Wine already told to prefer the game's own winhttp.dll?</summary>
        public bool DllOverrideIsSet(GameInstall game)
        {
            var userReg = UserRegPath(game);
            if (userReg == null || !File.Exists(userReg)) return false;

            try
            {
                return OverridePattern.IsMatch(File.ReadAllText(userReg));
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

        /// <summary>
        /// Puts the winhttp override in the prefix's registry if a prefix exists.
        /// </summary>
        /// <remarks>
        /// Best effort, and the Steam launch option is the authoritative path - this just saves the
        /// user from having to set it. Touching `user.reg` is only safe when a prefix is already
        /// there and Wine is not running, so both are checked; the file is backed up first, and
        /// anything unexpected is reported rather than guessed at.
        ///
        /// Returns true when the override is in place afterwards.
        /// </remarks>
        public bool EnsureDllOverride(GameInstall game, ISetupLog log)
        {
            log = log ?? NullSetupLog.Instance;

            if (DllOverrideIsSet(game)) return true;

            var userReg = UserRegPath(game);
            if (userReg == null)
            {
                log.Detail("No Proton prefix yet - the game has not been launched on Linux.");
                log.Info("Set this game's Steam launch option, then launch it:");
                log.Info("  " + GameInstall.ProtonLaunchOption);
                return false;
            }

            try
            {
                var text = File.ReadAllText(userReg);
                File.Copy(userReg, userReg + ".swyf-backup", true);

                // Wine's user.reg uses double backslashes in section headers, so the header we
                // look for and the one we write must both use them. A single-backslash header is not
                // a section Wine recognises, which is why the search and the write use the same form.
                var sectionHeader = @"[Software\\Wine\\DllOverrides]";
                var line = "\"winhttp\"=\"native,builtin\"";

                var idx = text.IndexOf(sectionHeader, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    // Append at the end of the file instead of inserting into the existing section.
                    // Inserting before the section's #time= line confused Wine's parser and crashed
                    // the game during startup on at least one install.
                    if (!text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
                    text += "\n" + sectionHeader + " 1000000000\n#time=1d00000000\n" + line + "\n";
                }
                else
                {
                    if (!text.EndsWith("\n", StringComparison.Ordinal)) text += "\n";
                    text += "\n" + sectionHeader + " 1000000000\n#time=1d00000000\n" + line + "\n";
                }

                File.WriteAllText(userReg, text);
                log.Success("  + winhttp=n,b in " + userReg);
                return true;
            }
            catch (Exception ex)
            {
                log.Warning("Could not write the winhttp override (" + ex.Message + ").");
                log.Info("Set this game's Steam launch option instead:");
                log.Info("  " + GameInstall.ProtonLaunchOption);
                return false;
            }
        }

        /// <summary>
        /// Starts the game.
        /// </summary>
        /// <remarks>
        /// Steam first on Linux, because that keeps overlays, the Steam runtime and the user's chosen
        /// Proton intact - and because it is how the game is actually played. A direct Proton launch
        /// is the fallback and covers Lutris, Heroic and a terminal.
        /// </remarks>
        public LaunchMethod Launch(GameInstall game, LaunchMethod preferred, ISetupLog log, string gameArgs = null)
        {
            log = log ?? NullSetupLog.Instance;

            if (!game.Exists) throw new SetupException("Not found: " + game.Path);

            // The game's Whisper (voice-to-text) feature crashes under Proton: the CPU compatibility
            // check reports false even on a CPU that supports AVX2, and the native init then faults.
            // Passing -whisper-cpu forces the CPU code path, which avoids the crash. On Windows the
            // native detection works, so the flag is only added there where it is needed.
            if (IsLinux && string.IsNullOrWhiteSpace(gameArgs)) gameArgs = "-whisper-cpu";

            if (!IsLinux)
            {
                StartExe("cmd.exe", "/c start \"\" \"" + game.Executable + "\"", game.Path, null);
                log.Success("Started " + game.Executable);
                return LaunchMethod.Native;
            }

            if (preferred == LaunchMethod.Direct) return LaunchDirect(game, log, gameArgs);

            var steam = FindExecutable("steam");
            if (steam != null)
            {
                var steamArgs = "-applaunch " + SteamEnvironment.GameAppId;
                if (!string.IsNullOrWhiteSpace(gameArgs)) steamArgs += " " + gameArgs;
                StartExe(steam, steamArgs, game.Path, null);
                log.Success("Asked Steam to start the game (" + SteamEnvironment.GameAppId + ").");
                WarnIfOverrideMissing(game, log);
                return LaunchMethod.Steam;
            }

            var flatpak = FindExecutable("flatpak");
            if (flatpak != null)
            {
                var flatpakArgs = "run com.valvesoftware.Steam -applaunch " + SteamEnvironment.GameAppId;
                if (!string.IsNullOrWhiteSpace(gameArgs)) flatpakArgs += " " + gameArgs;
                StartExe(flatpak, flatpakArgs, game.Path, null);
                log.Success("Asked the Steam Flatpak to start the game.");
                WarnIfOverrideMissing(game, log);
                return LaunchMethod.Steam;
            }

            log.Warning("Steam was not found on PATH; trying a direct Proton launch instead.");
            return LaunchDirect(game, log, gameArgs);
        }

        private LaunchMethod LaunchDirect(GameInstall game, ISetupLog log, string gameArgs = null)
        {
            var proton = game.Steam.ProtonBuilds.FirstOrDefault();
            if (proton == null)
            {
                throw new SetupException(
                    "No Proton build found under any Steam library, so the game cannot be started directly." + Environment.NewLine +
                    "Install Proton for this game in Steam, or start it from Steam itself.");
            }

            var protonScript = Path.Combine(proton, "proton");
            if (!File.Exists(protonScript))
            {
                throw new SetupException("Proton build looks wrong (no 'proton' script): " + proton);
            }

            var python = FindExecutable("python3") ?? FindExecutable("python");
            if (python == null)
            {
                throw new SetupException("A direct Proton launch needs python3 on PATH. Start the game from Steam instead.");
            }

            var compatData = game.Steam.CompatDataDir
                ?? Path.Combine(game.Steam.Libraries.FirstOrDefault() ?? game.Path, "..", "..", "compatdata", SteamEnvironment.GameAppId.ToString());
            Directory.CreateDirectory(compatData);

            var environment = new Dictionary<string, string>
            {
                ["STEAM_COMPAT_DATA_PATH"] = compatData,
                ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = game.Steam.PrimarySteamRoot ?? string.Empty,
                ["WINEDLLOVERRIDES"] = "winhttp=n,b",
                ["SteamAppId"] = SteamEnvironment.GameAppId.ToString()
            };

            var protonArgs = "\"" + protonScript + "\" run \"" + game.Executable + "\"";
            if (!string.IsNullOrWhiteSpace(gameArgs)) protonArgs += " " + gameArgs;
            StartExe(python, protonArgs, game.Path, environment);
            log.Success("Started the game through " + Path.GetFileName(proton) + " with the winhttp override.");
            return LaunchMethod.Direct;
        }

        private void WarnIfOverrideMissing(GameInstall game, ISetupLog log)
        {
            if (DllOverrideIsSet(game)) return;

            log.Info(string.Empty);
            log.Warning("The winhttp override is not set in this prefix yet, so BepInEx will not load.");
            log.Info("Set this game's Steam launch option, then launch it:");
            log.Info("  " + GameInstall.ProtonLaunchOption);
        }

        private static string UserRegPath(GameInstall game)
        {
            var prefix = game.Steam.PrefixDir;
            return prefix == null ? null : Path.Combine(prefix, "user.reg");
        }

        private static void StartExe(string file, string arguments, string workingDirectory, IDictionary<string, string> environment)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            };

            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    if (string.IsNullOrEmpty(pair.Value)) continue;
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }

            using (Process.Start(startInfo))
            {
                // Detached: the tool's job is to ask Steam or Proton to start the game, not to babysit
                // it. Disposing the handle here does not stop the game.
            }
        }

        /// <summary>The full path of an executable on PATH, or null.</summary>
        public static string FindExecutable(string name)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path)) return null;

            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing over.
                }
            }

            return null;
        }
    }
}
