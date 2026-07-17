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
