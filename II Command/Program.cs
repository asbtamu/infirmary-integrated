/* Infirmary Integrated Command
 * Multi-session instructor dashboard: hosts and controls concurrent
 * mirror-broadcast simulation sessions from one screen.
 *
 * Institutional deployment addition; not part of upstream Infirmary Integrated.
 */

using Avalonia;

using System;

namespace IICMD {

    internal class Program {

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        public static void Main (string [] args) {
            II.CrashLog.Install ("II:CMD");                 // Institutional deployment: log unhandled exceptions

            /* Save files, config and the relay are parsed and written with the current culture: on a PC
             * set to e.g. German, "37.5" reads back as 375. Fix the format for every thread, this one included. */
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

            if (II.Updater.TryUpdate ("command", args))     // Newer build on the share: swap and relaunch
                return;

            BuildAvaloniaApp ()
            .StartWithClassicDesktopLifetime (args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp ()
            => AppBuilder.Configure<App> ()
                .UsePlatformDetect ()
                .LogToTrace ();
    }
}
