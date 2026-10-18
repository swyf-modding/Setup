using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ScamWYF.Setup;
using ScamWYF.Setup.Patching;

namespace ScamWYF.Setup.Cli
{
    /// <summary>
    /// `swyf-setup` - the command-line front-end for the setup library.
    /// </summary>
    /// <remarks>
    /// This is the whole tool for someone who only wants the BepInEx system and not the launcher:
    /// one self-contained binary that installs and repairs an install on Windows and Linux alike.
    /// </remarks>
    public static class Program
    {
        public static int Main(string[] args)
        {
            var log = new ConsoleSetupLog();

            try
            {
                var commandLine = CommandLine.Parse(args);

                if (commandLine.Has("help") || commandLine.Has("h") || commandLine.Command == "help")
                {
                    PrintHelp();
                    return 0;
                }

                if (commandLine.Has("version") || commandLine.Command == "version")
                {
                    Console.WriteLine(VersionString());
                    return 0;
                }

                switch (commandLine.Command)
                {
                    case null:
                    case "":
                    case "install":
                    case "setup":
                    case "repair":
                        return Install(commandLine, log);
                    case "verify":
                        return Verify(commandLine, log);
                    case "play":
                        return Play(commandLine, log);
                    case "doctor":
                        return Doctor(commandLine, log);
                    case "unstrip":
                        return Unstrip(commandLine, log);
                    case "vtable":
                        return Vtable(commandLine, log);
                    case "scan":
                        return Scan(commandLine, log);
                    case "compare":
                        return Compare(commandLine, log);
                    case "patch-preloader":
                        return PatchPreloader(commandLine, log, revert: false);
                    case "revert-preloader":
                        return PatchPreloader(commandLine, log, revert: true);
                    case "payload":
                        return Payload(commandLine, log);
                    default:
                        Console.Error.WriteLine("Unknown command: " + commandLine.Command);
                        PrintHelp();
                        return 2;
                }
            }
            catch (SetupException ex)
            {
                log.Error(string.Empty);
                log.Error(ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                log.Error(string.Empty);
                log.Error("Unexpected failure: " + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------------ commands

        private static int Install(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            var payload = ResolvePayload(commandLine, log);

            var options = new SetupOptions
            {
                Install = new InstallOptions
                {
                    BepInExZip = commandLine.Get("bepinex-zip"),
                    Offline = commandLine.Has("offline"),
                    Force = commandLine.Has("force"),
                    BepInExVersion = commandLine.Get("bepinex-version") ?? GameInstall.BepInExVersion
                },
                FixAwaiterMethods = !commandLine.Has("no-awaiters"),
                FixVtableBreaks = !commandLine.Has("no-fix")
            };

            if (commandLine.Get("assemblies") is string list && !string.IsNullOrWhiteSpace(list))
            {
                options.VtableAssemblies = Split(list);
            }

            var result = new SetupRunner().Run(game, payload, options, log);

            if (result.Ok && SteamHint(commandLine, game, log)) { }

            return result.Ok ? 0 : 1;
        }

        private static int Verify(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            log.Info("Verifying " + game.Path);
            var problems = new SetupVerifier().Verify(game, log);

            log.Info(string.Empty);
            if (problems.Count == 0)
            {
                log.Success("Ready: the game should load with BepInEx.");
                return 0;
            }

            log.Warning("Problems:");
            foreach (var problem in problems) log.Warning("  - " + problem);
            return 1;
        }

        private static int Play(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            var proton = new ProtonIntegration();

            if (proton.IsLinux && !commandLine.Has("no-override"))
            {
                proton.EnsureDllOverride(game, log);
            }

            var method = commandLine.Has("direct") ? LaunchMethod.Direct : LaunchMethod.Steam;
            proton.Launch(game, method, log);
            return 0;
        }

        private static int Doctor(CommandLine commandLine, ISetupLog log)
        {
            var steam = SteamEnvironment.Detect();
            var game = GameInstall.Resolve(commandLine.Get("game-dir"), steam);

            log.Info(VersionString());
            log.Info("os:            " + Environment.OSVersion.Platform + " " + Environment.OSVersion.VersionString);
            log.Info("runtime:       " + Environment.Version);
            log.Info("home:          " + (Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            log.Info(string.Empty);
            log.Info("steam roots:");
            if (steam.SteamRoots.Count == 0) log.Warning("  (none found)");
            foreach (var root in steam.SteamRoots) log.Info("  " + root);

            log.Info("steam libraries:");
            if (steam.Libraries.Count == 0) log.Warning("  (none found)");
            foreach (var library in steam.Libraries) log.Info("  " + library);

            log.Info("compatdata:    " + (steam.CompatDataDir ?? "(none)"));
            log.Info("prefix:        " + (steam.PrefixDir ?? "(none - the game has not been launched under Proton)"));
            log.Info("proton builds: " + (steam.ProtonBuilds.Count == 0 ? "(none)" : string.Join(", ", steam.ProtonBuilds.Select(System.IO.Path.GetFileName))));
            log.Info(string.Empty);
            log.Info("game:          " + (string.IsNullOrWhiteSpace(game.Path) ? "(not found)" : game.Path));

            try
            {
                PrintProbe(log, "Game install", game.HasGame, GameInstall.DataFolderName + "/Managed");
                PrintProbe(log, "Loader (Doorstop)", game.HasDoorstop, "winhttp.dll");
                PrintProbe(log, "Corlib override", game.HasCorlibOverride, "unstripped_corlib/mscorlib.dll");
                PrintProbe(log, "Override wired up", game.DoorstopPointsAtOverride, "dll_search_path_override");
                PrintProbe(log, "BepInEx", game.HasBepInEx, "BepInEx/core/BepInEx.dll");

                if (OperatingSystem.IsLinux())
                {
                    var proton = new ProtonIntegration();
                    PrintProbe(log, "Proton winhttp override", proton.DllOverrideIsSet(game), GameInstall.ProtonLaunchOption);
                }
            }
            catch (Exception ex)
            {
                log.Warning("  probe failed: " + ex.Message);
            }

            log.Info(string.Empty);
            log.Info("summary:       " + game.Summary());

            try
            {
                var payload = ResolvePayload(commandLine, log);
                log.Info("payload:       " + payload.Describe + " (" + payload.Corlib.Count + " corlib, " + payload.Doorstop.Count + " doorstop)");
            }
            catch (SetupException ex)
            {
                log.Warning("payload:       " + ex.Message);
            }

            return 0;
        }

        private static int Unstrip(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            var assemblies = commandLine.Get("assemblies") is string list && !string.IsNullOrWhiteSpace(list)
                ? (IEnumerable<string>)Split(list)
                : AwaiterUnstripper.DefaultAssemblies;

            log.Heading("restoring stripped awaiter methods");
            var patched = new AwaiterUnstripper().Run(game, assemblies, commandLine.Get("override-dir"), log);
            log.Info(string.Empty);
            log.Info("Patched " + patched + " method(s).");
            return 0;
        }

        private static int Vtable(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            var assemblies = commandLine.Get("assemblies") is string list && !string.IsNullOrWhiteSpace(list)
                ? (IReadOnlyList<string>)Split(list)
                : null;

            var result = new VtableBreakFixer().Run(
                game,
                fix: commandLine.Has("fix"),
                assemblies: assemblies,
                unstripped: commandLine.Get("unstripped"),
                log: log);

            return result.Clean ? 0 : 1;
        }

        private static int Scan(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            var scanDir = commandLine.Get("dir") ?? game.BepInExCore;
            new MissingApiScanner().Run(commandLine.Get("managed") ?? game.ManagedFolder, scanDir, log);
            return 0;
        }

        private static int Compare(CommandLine commandLine, ISetupLog log)
        {
            var game = ResolveGame(commandLine, requireBepInEx: false);
            new CorlibComparer().Run(
                commandLine.Get("managed") ?? game.ManagedFolder,
                commandLine.Get("unstripped") ?? game.OverrideFolder,
                log);
            return 0;
        }

        private static int PatchPreloader(CommandLine commandLine, ISetupLog log, bool revert)
        {
            var game = ResolveGame(commandLine, requireBepInEx: !revert);
            var coreDir = commandLine.Get("core-dir") ?? game.BepInExCore;
            new PreloaderPatcher().Run(coreDir, revert, log);
            return 0;
        }

        private static int Payload(CommandLine commandLine, ISetupLog log)
        {
            var payload = ResolvePayload(commandLine, log);
            log.Info("payload:  " + payload.Describe);
            log.Info("embedded: " + payload.IsEmbedded);
            log.Info("corlib:");
            foreach (var name in payload.Corlib) log.Info("  " + name);
            log.Info("doorstop:");
            foreach (var name in payload.Doorstop) log.Info("  " + name);
            return 0;
        }

        // ------------------------------------------------------------------ helpers

        private static GameInstall ResolveGame(CommandLine commandLine, bool requireBepInEx)
        {
            return GameInstall.ResolveOrThrow(commandLine.Get("game-dir"), requireBepInEx);
        }

        private static SetupPayload ResolvePayload(CommandLine commandLine, ISetupLog log)
        {
            var explicitPayload = commandLine.Get("payload-dir");
            if (!string.IsNullOrWhiteSpace(explicitPayload))
            {
                var payload = SetupPayload.FromDirectory(explicitPayload);
                log.Detail("payload: " + payload.Describe);
                return payload;
            }

            var resolved = SetupPayload.ResolveDefault();
            if (resolved.IsEmbedded) log.Detail("payload: embedded");
            else log.Detail("payload: " + resolved.Describe);
            return resolved;
        }

        private static bool SteamHint(CommandLine commandLine, GameInstall game, ISetupLog log)
        {
            // On Linux the loader is installed but Wine will not load it until it is told to prefer
            // the game's own winhttp.dll. Saying so here is the difference between "done" and a game
            // that silently starts unmodded.
            if (!OperatingSystem.IsLinux()) return false;

            log.Info(string.Empty);
            if (game.Steam.PrefixDir != null)
            {
                log.Detail("Proton prefix found: " + game.Steam.PrefixDir);
                log.Detail("If mods do not load, set this game's Steam launch option to:");
            }
            else
            {
                log.Detail("Launch the game once under Proton, then set this Steam launch option:");
            }

            log.Info("  " + GameInstall.ProtonLaunchOption);
            return true;
        }

        private static void PrintProbe(ISetupLog log, string name, bool ok, string detail)
        {
            var mark = ok ? "[ok]  " : "[..]  ";
            if (ok) log.Success("  " + mark + name);
            else log.Warning("  " + mark + name + "  (" + detail + ")");
        }

        private static List<string> Split(string value)
        {
            return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        private static string VersionString()
        {
            var assembly = typeof(Program).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var name = assembly.GetName().Name;
            return name + " " + (informational ?? assembly.GetName().Version?.ToString() ?? "0.0.0");
        }

        private static void PrintHelp()
        {
            Console.WriteLine(VersionString());
            Console.WriteLine();
            Console.WriteLine("Install or repair the BepInEx system in a Scam With Your Friends install.");
            Console.WriteLine("On Linux the game runs under Proton; the loader is the Windows one, so Wine has to");
            Console.WriteLine("be told to prefer the game's winhttp.dll. The tool prints the launch option to use.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  swyf-setup [install] [options]        install or repair (the default)");
            Console.WriteLine("  swyf-setup verify [options]           check an install without changing it");
            Console.WriteLine("  swyf-setup play [--direct]            start the game (Steam by default, Proton directly with --direct)");
            Console.WriteLine("  swyf-setup doctor [options]           show where things are and what is missing");
            Console.WriteLine("  swyf-setup unstrip [options]          restore stripped awaiter methods only");
            Console.WriteLine("  swyf-setup vtable [--fix]             scan (and optionally patch) vtable breakages");
            Console.WriteLine("  swyf-setup scan [--dir <path>]        list BCL references the stripped corlib lacks");
            Console.WriteLine("  swyf-setup compare [options]          diff the shipped BCL against the override");
            Console.WriteLine("  swyf-setup patch-preloader            diagnostic: drop GetPEKind from BepInEx");
            Console.WriteLine("  swyf-setup revert-preloader           undo patch-preloader");
            Console.WriteLine("  swyf-setup payload                    show which payload binaries are in use");
            Console.WriteLine();
            Console.WriteLine("Common options:");
            Console.WriteLine("  --game-dir <path>      the install to use (default: SWYG_GAME_DIR, then Steam)");
            Console.WriteLine("  --payload-dir <path>   a vendor folder to take Doorstop and corlib from");
            Console.WriteLine("  --offline              do not download BepInEx");
            Console.WriteLine("  --bepinex-zip <path>   install BepInEx from a zip you already have");
            Console.WriteLine("  --force                replace files that are already present");
            Console.WriteLine("  --assemblies a.dll,..  restrict a patch step to these assemblies");
            Console.WriteLine("  --no-awaiters          skip the awaiter clone step");
            Console.WriteLine("  --no-fix               scan for vtable breakages but do not patch them");
            Console.WriteLine();
            Console.WriteLine("Environment:");
            Console.WriteLine("  SWYG_GAME_DIR          a game folder to use when --game-dir is not given");
            Console.WriteLine("  SWYG_SETUP_PAYLOAD     a vendor folder to use instead of the embedded payload");
        }
    }
}
