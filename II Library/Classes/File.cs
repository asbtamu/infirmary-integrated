/* File.cs
 * Infirmary Integrated
 * By Ibi Keller (Tanjera), (c) 2023
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace II {
    public static class File {
        public static void Init () {
            if (!Directory.Exists (GetCacheDir ()))
                Directory.CreateDirectory (GetCacheDir ());

            if (!Directory.Exists (GetAppDataDir ()))
                Directory.CreateDirectory (GetAppDataDir ());

            ClearCache ();
        }

        public static string GetAppDataDir () {
            return Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.ApplicationData), GetOSStyling ("Infirmary Integrated"));
        }

        public static string GetConfigPath () {
            return Path.Combine (GetAppDataDir (), GetOSStyling ("config.cfg"));
        }

        public static string GetCacheDir () {
            return Path.Combine (Path.GetTempPath (), GetOSStyling ("Infirmary Integrated"));
        }

        public static void ClearCache () {
            string [] files = Directory.GetFiles (GetCacheDir ());

            foreach (string f in files)
                System.IO.File.Delete (f);
        }

        public static string GetOSStyling (string input) {
            if (String.IsNullOrWhiteSpace (input))
                return input;

            OperatingSystem os = Environment.OSVersion;
            if (os.Platform == PlatformID.Win32NT) {
                StringBuilder sb = new ();

                for (int i = 0; i < input.Length; i++) {
                    if (i == 0 || input [i - 1] == ' ')
                        sb.Append (input [i].ToString ().ToUpperInvariant ());
                    else
                        sb.Append (input [i].ToString ().ToLowerInvariant ());
                }
                return sb.ToString ();
            } else {
                return input.ToLowerInvariant ();                // e.g. "marana"
            }
        }
    }
}