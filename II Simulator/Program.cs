using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using System;

namespace IISIM {

    internal class Program {

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        public static void Main (string [] args) {
            II.CrashLog.Install ("II:SIM");                 // Institutional deployment: log unhandled exceptions

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