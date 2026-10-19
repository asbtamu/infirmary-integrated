/* CrashLog.cs
 * Infirmary Integrated
 *
 * Institutional deployment: crash logging.
 *
 * Upstream has no file logging and no global exception handler, so an unhandled
 * exception terminates the process with no trace -- the window never appears (or
 * vanishes), and the only record is a .NET Runtime entry in the Windows Event Log.
 * That is not something a lab instructor can act on mid-class.
 *
 * Install() hooks the two process-wide failure paths and appends to
 * %APPDATA%\Infirmary Integrated\crash.log (GetAppDataDir on other platforms).
 * Writing is best-effort and never throws: a logger that crashes the crash is worse
 * than no logger.
 */

using System;
using System.Text;
using System.Threading.Tasks;

namespace II {

    public static class CrashLog {
        private static readonly object _Lock = new ();
        private static string _AppName = "Infirmary Integrated";

        /* Keeps a single log from growing without bound on a machine that crashes repeatedly */
        private const long MaxBytes = 1024 * 1024;

        public static string GetLogPath ()
            => System.IO.Path.Combine (File.GetAppDataDir (), "crash.log");

        /* Call once at startup, before the UI framework starts. */
        public static void Install (string appName) {
            _AppName = appName;

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Write ("UnhandledException", e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (s, e) => {
                Write ("UnobservedTaskException", e.Exception);
                e.SetObserved ();                       // Prevents a second, fatal rethrow
            };
        }

        /* Also usable directly from a catch block for a non-fatal problem worth recording. */
        public static void Write (string source, Exception? ex, string? note = null) {
            try {
                string dir = File.GetAppDataDir ();

                if (!System.IO.Directory.Exists (dir))
                    System.IO.Directory.CreateDirectory (dir);

                string path = GetLogPath ();

                StringBuilder sb = new ();
                sb.AppendLine ();
                sb.AppendLine ("================================================================");
                sb.AppendLine ($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ({DateTime.UtcNow:HH:mm:ss} UTC)");
                sb.AppendLine ($"Application : {_AppName}");
                sb.AppendLine ($"Machine     : {Environment.MachineName}");
                sb.AppendLine ($"OS          : {Environment.OSVersion}");
                sb.AppendLine ($"Runtime     : {Environment.Version}");
                sb.AppendLine ($"Source      : {source}");

                if (!String.IsNullOrEmpty (note))
                    sb.AppendLine ($"Note        : {note}");

                if (ex is null) {
                    sb.AppendLine ("Exception   : (none supplied)");
                } else {
                    for (Exception? e = ex; e is not null; e = e.InnerException) {
                        sb.AppendLine ();
                        sb.AppendLine ($"{e.GetType ().FullName}: {e.Message}");
                        sb.AppendLine (e.StackTrace ?? "  (no stack trace)");
                    }
                }

                lock (_Lock) {
                    /* Rotate rather than truncate, so the failure that started it is not lost */
                    try {
                        System.IO.FileInfo fi = new (path);

                        if (fi.Exists && fi.Length > MaxBytes) {
                            string old = path + ".1";

                            if (System.IO.File.Exists (old))
                                System.IO.File.Delete (old);

                            System.IO.File.Move (path, old);
                        }
                    } catch { }

                    System.IO.File.AppendAllText (path, sb.ToString ());
                }
            } catch {
                /* Deliberately silent -- never let logging mask or worsen the original fault */
            }
        }
    }
}
