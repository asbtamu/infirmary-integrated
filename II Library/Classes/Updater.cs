/* Updater.cs
 * Infirmary Integrated
 *
 * Institutional deployment: startup updates from a file share (OSCEtime's model, see its UPDATER.md).
 *
 * Staff copy a published build (tools\Publish-Update.ps1) onto the share by hand. Each app calls
 * TryUpdate() first thing in Main, before any window exists -- so a room PC never restarts mid-
 * scenario, and nobody in the room has to click anything. Unreachable share, nothing newer, a bad
 * hash, a read-only install: all silently fall through to a normal launch, and are logged to
 * %LOCALAPPDATA%\Infirmary Integrated\updates\updater.log.
 *
 * Share layout:   manifest.json   { "sim": { "version": "2.8.2", "package": "II-SIM_2.8.2.zip", "sha256": "..." },
 *                                   "command": { ... } }
 *                 II-SIM_2.8.2.zip, II-Command_0.1.1.zip
 *
 * Swap: the zip is verified and extracted to "<install>.new" beside the install folder, then a .cmd
 * (written to LOCALAPPDATA, never TEMP) waits for this process to exit, renames "<install>" to
 * "<install>.bak", renames ".new" into place and relaunches with the same arguments. Renames on one
 * volume are all-or-nothing; if the second fails the backup is renamed back. Rollback by hand: swap
 * the ".bak" folder back in. A file named "no-updates" in the install folder pins that machine.
 *
 * Trust: the hash proves the copy is intact, not who published it. Write access to the share is
 * code execution on every station, so the share must be read-only to the station accounts.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace II {

    public static class Updater {
        public const string DefaultShare = @"H:\Sim Techs\updates\ii";

        public static string WorkDir { get; } = Path.Combine (
            Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData), "Infirmary Integrated", "updates");

        /* II_UPDATE_SHARE overrides for testing against a local folder */
        public static string Share => Environment.GetEnvironmentVariable ("II_UPDATE_SHARE") ?? DefaultShare;

        public record Package (string Version, string File, string Sha256);

        /* Returns true when an update has been staged and the swap launched: the caller must exit at once. */
        public static bool TryUpdate (string product, string [] args) {
            try {
                string exe = Environment.ProcessPath ?? "";
                string install = AppContext.BaseDirectory.TrimEnd ('\\', '/');

                /* A build run from the source tree has no install to replace */
                if (install.Contains ($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || !OperatingSystem.IsWindows ())
                    return false;

                string share = Share;
                string current = System.Reflection.Assembly.GetEntryAssembly ()?.GetName ().Version?.ToString (3) ?? "0.0.0";

                /* A disconnected mapped drive can stall for a long time; the room must still come up */
                Task<string?> check = Task.Run (() => Prepare (product, current, share, install, Path.GetFileName (exe), args));
                string? script = check.Wait (TimeSpan.FromMinutes (3)) ? check.Result : null;

                if (script is null)
                    return false;

                Process.Start (new ProcessStartInfo (script) {
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = WorkDir,             // Not the install folder: a cwd inside it blocks the rename
                });
                Log ($"{product}: swap launched, exiting");
                return true;
            } catch (Exception ex) {
                Log ($"{product}: update skipped: {ex.Message}");
                return false;
            }
        }

        /* Everything up to (not including) launching the swap. Returns the swap script's path, or null
         * to launch normally. Public for the unit tests; never touches the running install. */
        public static string? Prepare (string product, string currentVersion, string share, string install,
                string exeName, string [] args) {
            string manifestPath = Path.Combine (share, "manifest.json");

            if (System.IO.File.Exists (Path.Combine (install, "no-updates"))) {
                Log ($"{product}: pinned (no-updates file), skipping");
                return null;
            }

            if (!System.IO.File.Exists (manifestPath))
                return null;                                // Share not mapped or nothing published: normal

            Package? pkg = ReadManifest (System.IO.File.ReadAllText (manifestPath), product);

            if (pkg is null || !Utility.IsNewerVersion (currentVersion, pkg.Version))
                return null;

            Log ($"{product}: {currentVersion} -> {pkg.Version} offered");

            /* Batch quoting can't carry a double quote, and a failure after exit means no relaunch */
            if (args.Any (a => a.Contains ('"'))) {
                Log ($"{product}: an argument contains a double quote; skipping update");
                return null;
            }

            string staging = install + ".new";

            if (Directory.Exists (staging))
                Directory.Delete (staging, true);

            try {
                Directory.CreateDirectory (staging);        // Doubles as the writability check for the install's parent
            } catch (Exception ex) {
                Log ($"{product}: cannot write beside {install} ({ex.Message}); install somewhere this account can write");
                return null;
            }

            Directory.CreateDirectory (WorkDir);
            string zip = Path.Combine (WorkDir, Path.GetFileName (pkg.File));
            System.IO.File.Copy (Path.Combine (share, pkg.File), zip, true);

            if (!String.Equals (Sha256 (zip), pkg.Sha256, StringComparison.OrdinalIgnoreCase)) {
                Log ($"{product}: SHA-256 MISMATCH for {pkg.File}; nothing installed");
                System.IO.File.Delete (zip);
                Directory.Delete (staging, true);
                return null;
            }

            ZipFile.ExtractToDirectory (zip, staging);      // Throws on entries that escape the folder
            System.IO.File.Delete (zip);

            if (!System.IO.File.Exists (Path.Combine (staging, exeName))) {
                Log ($"{product}: package has no {exeName}; nothing installed");
                Directory.Delete (staging, true);
                return null;
            }

            string script = Path.Combine (WorkDir, $"apply_update_{product}.cmd");
            System.IO.File.WriteAllText (script, SwapScript (install, staging, exeName, args, Path.Combine (WorkDir, "updater.log")));
            return script;
        }

        public static Package? ReadManifest (string json, string product) {
            using JsonDocument doc = JsonDocument.Parse (json);

            if (!doc.RootElement.TryGetProperty (product, out JsonElement e))
                return null;

            string? Get (string name) => e.TryGetProperty (name, out JsonElement v) ? v.GetString () : null;
            string? version = Get ("version"), file = Get ("package"), sha = Get ("sha256");

            /* The package name comes off a share: it must not walk out of it */
            if (version is null || file is null || sha is null || file != Path.GetFileName (file))
                return null;

            return new Package (version, file, sha);
        }

        /* Paths are baked in with set "VAR=..." rather than passed as arguments: cmd mangles more than two
         * quote characters on a /c line. Uses only move/rmdir/ping, no PowerShell (see OSCEtime's
         * Defender notes). Does not delete itself; the next update overwrites it. */
        public static string SwapScript (string install, string staging, string exeName, string [] args, string log) {
            string Esc (string s) => s.Replace ("%", "%%");
            string argLine = String.Concat (args.Select (a => $" \"{Esc (a)}\""));
            string bak = install + ".bak";

            return String.Join ("\r\n", new [] {
                "@echo off",
                $"set \"INSTALL={Esc (install)}\"",
                $"set \"NEW={Esc (staging)}\"",
                $"set \"BAK={Esc (bak)}\"",
                $"set \"LOG={Esc (log)}\"",
                "set /a TRIES=0",
                "if exist \"%BAK%\" rmdir /s /q \"%BAK%\"",
                ":wait",
                "move \"%INSTALL%\" \"%BAK%\" >nul 2>&1 && goto swap",
                "set /a TRIES+=1",
                "if %TRIES% geq 60 goto giveup",
                "ping -n 3 127.0.0.1 >nul",
                "goto wait",
                ":swap",
                "move \"%NEW%\" \"%INSTALL%\" >nul 2>&1 && goto done",
                "move \"%BAK%\" \"%INSTALL%\" >nul 2>&1",
                ">>\"%LOG%\" echo %date% %time% swap: could not place new build, previous restored",
                "goto launch",
                ":done",
                ">>\"%LOG%\" echo %date% %time% swap: replaced",
                "goto launch",
                ":giveup",
                ">>\"%LOG%\" echo %date% %time% swap: install folder still locked after 2 min, left unchanged",
                ":launch",
                $"start \"\" \"%INSTALL%\\{Esc (exeName)}\"{argLine}",
                "",
            });
        }

        private static string Sha256 (string path) {
            using FileStream fs = System.IO.File.OpenRead (path);
            return Convert.ToHexString (SHA256.HashData (fs));
        }

        private static void Log (string line) {
            try {
                Directory.CreateDirectory (WorkDir);
                System.IO.File.AppendAllText (Path.Combine (WorkDir, "updater.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            } catch { }
        }
    }
}
