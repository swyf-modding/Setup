using System;
using System.Collections.Generic;
using ScamWYF.Setup.Patching;

namespace ScamWYF.Setup
{
    /// <summary>How much of the setup to do, and where the pieces come from.</summary>
    public sealed class SetupOptions
    {
        public InstallOptions Install { get; set; } = new InstallOptions();

        /// <summary>Overwrite loader files that are already present.</summary>
        public bool Force
        {
            get => Install.Force;
            set => Install.Force = value;
        }

        public bool FixAwaiterMethods { get; set; } = true;

        public bool FixVtableBreaks { get; set; } = true;

        /// <summary>Assemblies for the awaiter clone. Defaults to UniTask.dll.</summary>
        public IReadOnlyList<string> AwaiterAssemblies { get; set; }

        /// <summary>Restrict the vtable fix to these assemblies.</summary>
        public IReadOnlyList<string> VtableAssemblies { get; set; }
    }

    /// <summary>What the run ended up with. Empty <see cref="Problems"/> means ready to play.</summary>
    public sealed class SetupResult
    {
        public SetupResult(IReadOnlyList<string> problems)
        {
            Problems = problems ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> Problems { get; }

        public bool Ok => Problems.Count == 0;
    }

    /// <summary>
    /// Sets the game up for modding, from a clean install, in one call.
    /// </summary>
    /// <remarks>
    ///   1. installs the loader      (Doorstop from the payload, BepInEx from GitHub)
    ///   2. seeds the corlib override (the unstripped BCL from the payload)
    ///   3. restores the stripped awaiter methods
    ///   4. patches the remaining vtable breakages
    ///   5. verifies
    ///
    /// No mods are built here. Safe to re-run: steps 1 and 2 leave existing files alone, steps 3 and 4
    /// rewrite only their own output, and Managed\ is never written to.
    /// </remarks>
    public sealed class SetupRunner
    {
        public SetupResult Run(GameInstall game, SetupPayload payload, SetupOptions options = null, ISetupLog log = null)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (payload == null) payload = SetupPayload.ResolveDefault();
            options = options ?? new SetupOptions();
            log = log ?? NullSetupLog.Instance;

            log.Info("Game: " + game.Path);
            log.Info(string.Empty);

            // 1. loader
            new LoaderInstaller().Install(game, payload, options.Install, log);

            // 2. corlib
            new CorlibSeeder().Seed(game, payload, log);

            // 3 and 4. patches
            if (options.FixAwaiterMethods)
            {
                log.Heading("restoring stripped awaiter methods");
                new AwaiterUnstripper().Run(game, options.AwaiterAssemblies ?? AwaiterUnstripper.DefaultAssemblies, null, log);
            }

            if (options.FixVtableBreaks)
            {
                log.Heading("patching remaining vtable breakages");
                new VtableBreakFixer().Run(game, fix: true, assemblies: options.VtableAssemblies, unstripped: null, log: log);
            }

            // 5. verify
            log.Heading("verifying");
            var problems = new SetupVerifier().Verify(game, log);

            log.Info(string.Empty);
            if (problems.Count == 0)
            {
                log.Success("Setup complete. The game should load with BepInEx.");
                log.Success("Next: build the mods, then launch the game once to create their config files.");
                log.Detail("Re-run this after any game update.");
            }
            else
            {
                log.Warning("Setup finished with problems:");
                foreach (var problem in problems) log.Warning("  - " + problem);
            }

            return new SetupResult(problems);
        }
    }
}
