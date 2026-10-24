using System;
using System.IO;

namespace ScamWYF.Setup
{
    /// <summary>
    /// Seeds `unstripped_corlib\` with the unstripped BCL Doorstop redirects to.
    /// </summary>
    /// <remarks>
    /// These are the assemblies BepInEx and Harmony need that the game's stripped `Managed\` does not
    /// have. The game's own copies of everything else stay where they are, so this is a small folder
    /// on purpose - see vendor/README.md.
    ///
    /// Existing files are left alone, so a re-run never overwrites a patched copy that a later step
    /// wrote there.
    /// </remarks>
    public sealed class CorlibSeeder
    {
        public int Seed(GameInstall game, SetupPayload payload, ISetupLog log)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            log = log ?? NullSetupLog.Instance;

            log.Heading("seeding the unstripped corlib override");

            Directory.CreateDirectory(game.OverrideFolder);

            var seeded = 0;
            foreach (var name in payload.Corlib)
            {
                var destination = Path.Combine(game.OverrideFolder, name);
                if (File.Exists(destination)) continue;

                using (var source = payload.OpenCorlib(name))
                using (var file = File.Create(destination))
                {
                    source.CopyTo(file);
                }

                seeded++;
            }

            if (seeded > 0)
            {
                log.Success("  + seeded " + seeded + " assembly/assemblies into " + game.OverrideFolder);
            }
            else
            {
                log.Detail("  = override already seeded, leaving it");
            }

            // Which names came from the payload, recorded every run and not only when something
            // was copied. Everything else in the folder is a patched copy of a game assembly, and
            // telling the two apart is what lets a game update be spotted later.
            DerivedOverrides.RecordSeeded(game, payload.Corlib);

            return seeded;
        }
    }
}
