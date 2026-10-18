using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;

namespace ScamWYF.Setup
{
    /// <summary>How to install the loader, and where BepInEx comes from.</summary>
    public sealed class InstallOptions
    {
        /// <summary>Install BepInEx from this zip instead of downloading it.</summary>
        public string BepInExZip { get; set; }

        /// <summary>Do not download BepInEx. Fail if it is not already installed.</summary>
        public bool Offline { get; set; }

        /// <summary>Replace files that are already present.</summary>
        public bool Force { get; set; }

        public string BepInExVersion { get; set; } = GameInstall.BepInExVersion;
    }

    /// <summary>
    /// Installs the mod loader into the game: Doorstop from the payload, BepInEx from GitHub.
    /// </summary>
    /// <remarks>
    /// Two sources on purpose. Doorstop is small and vendored, so the loader half works with no
    /// network. BepInEx is fetched so this repository does not carry a copy of someone else's project.
    ///
    /// Safe to re-run. Existing files are left alone unless <see cref="InstallOptions.Force"/> is set.
    /// </remarks>
    public sealed class LoaderInstaller
    {
        public static string BepInExUrl(string version)
        {
            return "https://github.com/BepInEx/BepInEx/releases/download/v" + version +
                   "/BepInEx_win_x64_" + version + ".zip";
        }

        public void Install(GameInstall game, SetupPayload payload, InstallOptions options, ISetupLog log)
        {
            if (game == null) throw new ArgumentNullException(nameof(game));
            if (options == null) options = new InstallOptions();
            log = log ?? NullSetupLog.Instance;

            Directory.CreateDirectory(game.Path);

            log.Info("=== installing the loader into " + game.Path);
            InstallDoorstop(game, payload, options, log);
            InstallBepInEx(game, options, log);

            Directory.CreateDirectory(game.PluginsFolder);
        }

        private static void InstallDoorstop(GameInstall game, SetupPayload payload, InstallOptions options, ISetupLog log)
        {
            log.Info("Doorstop (vendored):");

            foreach (var name in SetupPayload.DoorstopFileNames)
            {
                if (!payload.Doorstop.Contains(name, StringComparer.Ordinal))
                {
                    throw new SetupException("The payload is missing Doorstop's " + name + " (" + payload.Describe + ")");
                }

                var what = name == "doorstop_config.ini"
                    ? "doorstop_config.ini (dll_search_path_override=unstripped_corlib)"
                    : name;

                using (var source = payload.OpenDoorstop(name))
                {
                    CopyIntoGame(source, Path.Combine(game.Path, name), what, options.Force, log);
                }
            }
        }

        private void InstallBepInEx(GameInstall game, InstallOptions options, ISetupLog log)
        {
            var bepInExDll = Path.Combine(game.BepInExCore, "BepInEx.dll");

            if (File.Exists(bepInExDll) && !options.Force && string.IsNullOrWhiteSpace(options.BepInExZip))
            {
                log.Info("BepInEx:");
                log.Detail("  = already installed, leaving it");
                return;
            }

            if (options.Offline && string.IsNullOrWhiteSpace(options.BepInExZip))
            {
                throw new SetupException(
                    "BepInEx is not installed and --offline was given." + Environment.NewLine + Environment.NewLine +
                    "Get BepInEx " + options.BepInExVersion + " (win_x64) from https://github.com/BepInEx/BepInEx/releases" +
                    " and extract BepInEx/core into:" + Environment.NewLine +
                    "  " + game.BepInExCore);
            }

            log.Info("BepInEx " + options.BepInExVersion + ":");

            var zip = options.BepInExZip;
            var downloaded = false;
            var extractDir = (string)null;

            try
            {
                if (string.IsNullOrWhiteSpace(zip))
                {
                    zip = Path.Combine(Path.GetTempPath(), "BepInEx_" + options.BepInExVersion + "_" +
                        Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
                    Download(options.BepInExVersion, zip, log);
                    downloaded = true;
                }

                if (!File.Exists(zip)) throw new SetupException("No such file: " + zip);

                extractDir = Path.Combine(Path.GetTempPath(), "bepinex_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractDir);
                ExtractZipSafely(zip, extractDir);

                var core = FindBepInExCore(extractDir);
                if (core == null)
                {
                    throw new SetupException("That file is not a BepInEx x64 pack (no BepInEx/core in it): " + zip);
                }

                CopyTree(core, game.BepInExCore);
                log.Success("  + BepInEx/core");
            }
            catch (SetupException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SetupException(
                    "Could not install BepInEx " + options.BepInExVersion + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                    "Get the pack from" + Environment.NewLine +
                    "  " + BepInExUrl(options.BepInExVersion) + Environment.NewLine + Environment.NewLine +
                    "then either run this again, or unpack it by hand so that" + Environment.NewLine +
                    "  " + Path.Combine(game.BepInExCore, "BepInEx.dll") + Environment.NewLine +
                    "exists. --offline tells this tool to leave BepInEx alone entirely.", ex);
            }
            finally
            {
                if (extractDir != null) TryDeleteDirectory(extractDir);
                if (downloaded && zip != null) TryDeleteFile(zip);
            }
        }

        private static void Download(string version, string destination, ISetupLog log)
        {
            var url = BepInExUrl(version);
            log.Detail("  downloading " + url);

            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new SetupException("GitHub returned " + (int)response.StatusCode + " " + response.ReasonPhrase + " for " + url);
                    }

                    using (var content = response.Content.ReadAsStream())
                    using (var file = File.Create(destination))
                    {
                        content.CopyTo(file);
                    }
                }
            }

            log.Detail("  downloaded " + new FileInfo(destination).Length / 1024 + " KB");
        }

        private static void CopyIntoGame(Stream source, string destination, string what, bool force, ISetupLog log)
        {
            if (File.Exists(destination) && !force)
            {
                log.Detail("  = " + what + " already present, leaving it");
                return;
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var file = File.Create(destination))
            {
                source.CopyTo(file);
            }

            log.Success("  + " + what);
        }

        /// <summary>Extracts a zip, refusing any entry that would land outside the destination.</summary>
        private static void ExtractZipSafely(string zip, string destination)
        {
            var root = Path.GetFullPath(destination);
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
                    if (!target.StartsWith(rootWithSeparator, StringComparison.Ordinal))
                    {
                        throw new SetupException("The zip entry '" + entry.FullName + "' escapes the extraction folder (zip-slip).");
                    }

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(target, true);
                }
            }
        }

        private static string FindBepInExCore(string root)
        {
            // The pack names it BepInEx/core. Case is not guaranteed across archives, so the parent
            // folder name is compared rather than building the path literally.
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetFileName(directory), "core", StringComparison.OrdinalIgnoreCase)) continue;
                var parent = Path.GetDirectoryName(directory);
                if (parent != null && string.Equals(Path.GetFileName(parent), "BepInEx", StringComparison.OrdinalIgnoreCase))
                {
                    return directory;
                }
            }

            return null;
        }

        private static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
