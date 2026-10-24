using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ScamWYF.Setup.Patching;

namespace ScamWYF.Setup
{
    /// <summary>
    /// The verification pass: everything that has to be true for the game to load with BepInEx.
    /// </summary>
    /// <remarks>
    /// The setup run's own test. It re-runs the vtable scan rather than trusting the fix pass, because
    /// "I wrote the patch" and "the patch is what loads" are different claims - for instance when an
    /// assembly the scan could not read is the one that needed patching.
    /// </remarks>
    public sealed class SetupVerifier
    {
        public IReadOnlyList<string> Verify(GameInstall game, ISetupLog log)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            log = log ?? NullSetupLog.Instance;

            var problems = new List<string>();

            if (!File.Exists(Path.Combine(game.OverrideFolder, "mscorlib.dll")))
            {
                problems.Add("no mscorlib in the override folder - BepInEx cannot load");
            }

            if (!File.Exists(game.DoorstopConfig))
            {
                problems.Add("no doorstop_config.ini in the game root");
            }
            else if (!Regex.IsMatch(SafeRead(game.DoorstopConfig), "dll_search_path_override\\s*=\\s*unstripped_corlib"))
            {
                problems.Add("doorstop_config.ini does not set dll_search_path_override=unstripped_corlib");
            }

            if (!Directory.Exists(game.ManagedFolder) || !game.ManagedAssemblies().Any())
            {
                problems.Add("no assemblies in " + GameInstall.DataFolderName + "/Managed");
            }

            var stale = game.StaleOverrides;
            if (stale.Count > 0)
            {
                problems.Add("built from an older game version, re-run setup: " + string.Join(", ", stale));
            }

            if (game.HasGame && Directory.Exists(game.OverrideFolder))
            {
                var scan = new VtableBreakFixer().Run(game, fix: false, assemblies: null, unstripped: null, log: log);
                if (!scan.Clean)
                {
                    problems.Add("vtable breakages remain - run `swyf-setup vtable --fix` and read its output");
                }
            }

            return problems;
        }

        private static string SafeRead(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }
}
