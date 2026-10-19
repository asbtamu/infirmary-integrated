/* Utility.cs
 * Infirmary Integrated
 * By Ibi Keller (Tanjera), (c) 2023
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace II {
    public static class Utility {
        public static bool IsNewerVersion (string current, string comparison) {
            string [] curSplit = current.Split ('.'),
                compSplit = comparison.Split ('.');

            /* Missing parts count as 0 ("2.8" == "2.8.0"); non-numeric parts as -1 */
            static int Part (string [] parts, int i)
                => i >= parts.Length ? 0 : int.TryParse (parts [i], out int n) ? n : -1;

            for (int i = 0; i < compSplit.Length; i++) {
                if (Part (curSplit, i) < Part (compSplit, i))
                    return true;
                else if (Part (curSplit, i) > Part (compSplit, i))
                    return false;
            }

            return false;
        }

        /* Wire/file format for save files and the mirror relay. Invariant: in a culture's own
         * calendar "yyyy" can be 2569 (th-TH) or a Hijri year, and "/" can become "." */
        private const string DateTimeFormat = "yyyy/MM/dd HH:mm:ss";

        public static string? DateTime_ToString (DateTime? dt)
            => dt?.ToString (DateTimeFormat, CultureInfo.InvariantCulture);

        /* Empty or malformed input (e.g. "Updated:" saved from a null date) gives MinValue rather
         * than throwing, which used to abort the whole surrounding file load */
        public static DateTime DateTime_FromString (string? str)
            => DateTime.TryParseExact (str?.Trim (), _DateTimeFormatsRead, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dt) ? dt : DateTime.MinValue;

        /* Older saves were written in the PC's culture; the old positional parser ignored the separators */
        private static readonly string [] _DateTimeFormatsRead = {
            DateTimeFormat, "yyyy.MM.dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd H:mm:ss"
        };

        /* Accession keys and passwords for mirror sessions: a cryptographic source, not System.Random */
        public static string RandomString (int length)
            => RandomNumberGenerator.GetString ("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", length);

        public static string WrapString (string? input) {
            input ??= "";
            return input.Replace (" ", Environment.NewLine);
        }

        public static string UnderscoreToSpace (string str) {
            return str.Replace ('_', ' ');
        }

        public static string Indent (int indent) {
            string space = "    ";
            string output = "";

            for (int i = 0; i < indent; i++)
                output += space;

            return output;
        }
    }
}