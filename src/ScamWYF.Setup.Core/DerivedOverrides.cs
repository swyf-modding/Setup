using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ScamWYF.Setup
{
    /// <summary>
    /// Tracks which files in `unstripped_corlib\` were derived from the game's own assemblies, and
    /// which game assembly each one was derived from, so that a game update can be told apart from
    /// a healthy install.
    /// </summary>
    /// <remarks>
    /// The override folder holds two different kinds of file and they age differently.
    ///
    /// The BCL assemblies come from the vendored payload. They track the Unity version and are
    /// replaced only by seeding a new payload.
    ///
    /// The rest - UniTask, Newtonsoft.Json, Unity.InputSystem and whatever else gains a vtable
    /// stub - are patched copies of the game's own assemblies. A game update replaces the originals
    /// in `Managed\` and leaves these behind, and because Doorstop searches the override folder
    /// first, the game then loads the previous build's copy of its own code. Unity serialises
    /// InputSystem settings into globalgamemanagers, so the symptom is the engine failing to read
    /// its own data: "globalgamemanagers.assets is corrupted", a native crash, and no managed stack
    /// to explain it.
    ///
    /// Nothing detected that, because being set up was a file-existence check and the file still
    /// existed. Recording what each derived copy was made from turns it into a question that can be
    /// answered: hash the source again and see whether it moved.
    ///
    /// Two plain-text files rather than JSON, matching `.doorstop_version` next door:
    ///
    ///   .seeded        one payload file name per line
    ///   .derived-from  "sha256 *name", the hash being of the Managed source when it was derived
    ///
    /// Both live in the override folder, so deleting that folder resets everything consistently.
    /// </remarks>
    public static class DerivedOverrides
    {
        /// <summary>Names seeded from the payload. These are not derived from anything.</summary>
        public const string SeededListName = ".seeded";

        /// <summary>Derived file names and the hash of the Managed assembly each came from.</summary>
        public const string ManifestName = ".derived-from";

        /// <summary>
        /// Records which override files came from the payload. Written on every seed, including a
        /// seed that copied nothing, because the point is to describe the folder as it now stands
        /// rather than to log what this particular run did.
        /// </summary>
        public static void RecordSeeded(GameInstall game, IEnumerable<string> names)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (names == null) throw new ArgumentNullException(nameof(names));

            var ordered = names
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            WriteLines(Path.Combine(game.OverrideFolder, SeededListName), ordered);
        }

        /// <summary>The payload-seeded names, or empty when nothing has recorded them yet.</summary>
        public static IReadOnlyCollection<string> Seeded(GameInstall game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));

            var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in ReadLines(Path.Combine(game.OverrideFolder, SeededListName)))
            {
                seeded.Add(line);
            }
            return seeded;
        }

        /// <summary>
        /// Notes that the override copy of <paramref name="name"/> was derived from the Managed
        /// assembly as it is right now. Call it after writing the patched copy, not before.
        /// </summary>
        public static void Record(GameInstall game, string name)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            var source = Path.Combine(game.ManagedFolder, name);
            if (!File.Exists(source)) return;

            var entries = new Dictionary<string, string>(Manifest(game), StringComparer.OrdinalIgnoreCase);
            entries[name] = Hash(source);

            WriteLines(
                Path.Combine(game.OverrideFolder, ManifestName),
                entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                       .Select(e => e.Value + " *" + e.Key));
        }

        /// <summary>Derived file name to the hash of the Managed assembly it came from.</summary>
        public static IReadOnlyDictionary<string, string> Manifest(GameInstall game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));

            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in ReadLines(Path.Combine(game.OverrideFolder, ManifestName)))
            {
                // "<hash> *<name>", the sha256sum shape. Split on the first space only: an
                // assembly file name may contain one, a hash may not.
                var separator = line.IndexOf(" *", StringComparison.Ordinal);
                if (separator <= 0 || separator + 2 >= line.Length) continue;

                var hash = line.Substring(0, separator).Trim();
                var name = line.Substring(separator + 2).Trim();
                if (hash.Length > 0 && name.Length > 0) entries[name] = hash;
            }
            return entries;
        }

        /// <summary>
        /// Override files that are patched copies of a game assembly: present in both folders, and
        /// not seeded from the payload.
        /// </summary>
        /// <remarks>
        /// Empty when nothing has recorded the seeded list, which is every install set up before
        /// this existed. That is deliberate: without knowing which files came from the payload,
        /// every BCL assembly would look derived and a Unity update would report mscorlib as stale
        /// in a way no re-run could clear. Seeding records the list, so one setup run fixes it.
        /// </remarks>
        public static IReadOnlyList<string> Derived(GameInstall game)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));

            var seeded = Seeded(game);
            if (seeded.Count == 0) return Array.Empty<string>();
            if (!Directory.Exists(game.OverrideFolder) || !Directory.Exists(game.ManagedFolder))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(game.OverrideFolder, "*.dll")
                .Select(Path.GetFileName)
                .Where(name => !seeded.Contains(name))
                .Where(name => File.Exists(Path.Combine(game.ManagedFolder, name)))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Derived copies whose Managed source has changed since they were made, which is what a
        /// game update looks like from here. A derived copy with no recorded source counts as
        /// stale: it predates this bookkeeping, and re-deriving it is cheap and always correct.
        /// </summary>
        public static IReadOnlyList<string> Stale(GameInstall game)
        {
            var manifest = Manifest(game);
            var stale = new List<string>();

            foreach (var name in Derived(game))
            {
                string recorded;
                if (!manifest.TryGetValue(name, out recorded))
                {
                    stale.Add(name);
                    continue;
                }

                if (!string.Equals(recorded, Hash(Path.Combine(game.ManagedFolder, name)), StringComparison.OrdinalIgnoreCase))
                {
                    stale.Add(name);
                }
            }

            return stale;
        }

        /// <summary>
        /// Notes every derived copy currently in the override folder against the game as it is
        /// now. Called at the end of a patch run, when each one has either just been written from
        /// the current <c>Managed\</c> or was already current.
        /// </summary>
        /// <remarks>
        /// Recording only the files a run rewrote is not enough. An assembly that needs no stub
        /// this time is still a derived copy, and leaving it out of the manifest would make it
        /// look unaccounted for - reported as stale on every later check, by a run that would
        /// then find nothing to do about it.
        /// </remarks>
        public static void RecordAll(GameInstall game)
        {
            foreach (var name in Derived(game)) Record(game, name);
        }

        /// <summary>
        /// Whether this bookkeeping applies to a patch step writing to <paramref name="folder"/>.
        /// </summary>
        /// <remarks>
        /// The patch steps can be pointed at a folder other than the install's own, which the
        /// tooling uses to patch into a scratch directory. The manifest describes the install, so
        /// a run that writes somewhere else must not touch it.
        /// </remarks>
        public static bool Tracks(GameInstall game, string folder)
        {
            if (game == null || string.IsNullOrEmpty(folder)) return false;
            return string.Equals(
                Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(game.OverrideFolder).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Deletes derived copies built from an older game build, so the next patch step starts
        /// from the assembly the game actually ships.
        /// </summary>
        /// <remarks>
        /// Deleting rather than overwriting, because the patched copy may not need patching at
        /// all against the new build: the stub it was given could have been added upstream. In
        /// that case the right end state is no override copy, letting the game load its own.
        /// </remarks>
        public static IReadOnlyList<string> DiscardStale(GameInstall game, ISetupLog log)
        {
            log = log ?? NullSetupLog.Instance;
            var discarded = new List<string>();

            foreach (var name in Stale(game))
            {
                try
                {
                    File.Delete(Path.Combine(game.OverrideFolder, name));
                    log.Detail("  ~ " + name + " was built from an older game version, re-deriving it");
                    discarded.Add(name);
                }
                catch (IOException ex)
                {
                    log.Warning("  could not replace the stale " + name + ": " + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    log.Warning("  could not replace the stale " + name + ": " + ex.Message);
                }
            }

            return discarded;
        }

        /// <summary>SHA-256 of a file, lowercase hex. Empty when it cannot be read.</summary>
        public static string Hash(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    var digest = sha.ComputeHash(stream);
                    var text = new StringBuilder(digest.Length * 2);
                    foreach (var b in digest) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return text.ToString();
                }
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

        private static IEnumerable<string> ReadLines(string path)
        {
            string[] lines;
            try
            {
                if (!File.Exists(path)) return Array.Empty<string>();
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }

            return lines.Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#');
        }

        private static void WriteLines(string path, IEnumerable<string> lines)
        {
            var text = new StringBuilder();
            text.Append("# Written by swyf-setup. Delete unstripped_corlib to start over.\n");
            foreach (var line in lines) text.Append(line).Append('\n');

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // Bookkeeping only. A folder that cannot be written to has a louder problem than
                // this, and failing the setup over it would be the wrong trade.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
