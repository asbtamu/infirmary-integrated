using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace IICMD {

    public class App : Application {

        /* One main timer drives every session's simulation timer, mirroring IISIM's App.Timer_Main */
        public System.Timers.Timer Timer_Main = new ();

        public List<RoomSession> Sessions = new ();

        public WindowMain? Window_Main;

        public override void Initialize () {
            AvaloniaXamlLoader.Load (this);

            Timer_Main.Interval = 10;           // q 10 milliseconds
            Timer_Main.Start ();
        }

        public override void OnFrameworkInitializationCompleted () {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
                Window_Main = new WindowMain (this);
                desktop.MainWindow = Window_Main;

                desktop.ShutdownRequested += (s, e) => Cleanup ();
            }

            base.OnFrameworkInitializationCompleted ();
        }

        public void Cleanup () {
            foreach (RoomSession rs in Sessions)
                rs.Dispose ();

            Sessions.Clear ();
            Timer_Main.Stop ();
        }
    }
}
