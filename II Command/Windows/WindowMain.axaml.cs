/* WindowMain.axaml.cs
 * Infirmary Integrated Command
 *
 * The dashboard: a wrap-grid of RoomCards (one per concurrent session), a toolbar
 * for adding rooms, saving/loading lab layouts, and broadcasting all rooms at once.
 * A 1-second UI timer refreshes every card's vitals.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using II;
using II.Server;

namespace IICMD {

    public partial class WindowMain : Window {
        public App? Instance;

        public string ServerAddress {
            get => this.GetControl<TextBox> ("tbServerAddress").Text ?? Mirror.DefaultServer;
        }

        public bool IsCompact { get => this.GetControl<CheckBox> ("chkCompact").IsChecked ?? false; }

        /* Off by default: a wheel over a card inside the scrolling room panel must not change a live room */
        public bool ScrollAdjustsVitals { get => this.GetControl<CheckBox> ("chkScrollVitals").IsChecked ?? false; }

        private readonly List<RoomCard> _Cards = new ();
        private readonly DispatcherTimer _UITimer = new ();

        /* Path to the II:SIM executable on room machines; used to generate launcher scripts */
        private string? _SimulatorPath;

        /* Seconds between periodic re-posts of each broadcasting session (see _UITimer) */
        private const int RepostSeconds = 5;

        private int _TickCount = 0;

        public WindowMain () {
            InitializeComponent ();
        }

        public WindowMain (App? app) {
            InitializeComponent ();

            Instance = app;

            this.GetControl<TextBox> ("tbServerAddress").Text = Mirror.DefaultServer;

            _UITimer.Interval = TimeSpan.FromSeconds (1);
            _UITimer.Tick += (s, e) => {
                foreach (RoomCard card in _Cards)
                    card.RefreshVitals ();

                /* Re-post every broadcasting session periodically, rather than only on step
                 * changes. Two reasons:
                 *
                 *   1. A client that joins mid-session consumes the current post during its
                 *      connection check (DialogMirrorReceive/MirrorReceiveArguments call
                 *      GetStep against a throwaway Step, which advances Mirror.PatientUpdated).
                 *      Its first live tick then sees nothing newer and applies nothing, so the
                 *      monitor holds default vitals until the *next* post. Without a periodic
                 *      re-post that means until the instructor happens to change a step.
                 *
                 *   2. Physiology that drifts within a step now reaches the rooms too. */

                if (++_TickCount % RepostSeconds != 0)
                    return;

                foreach (RoomCard card in _Cards) {
                    card.Session?.LogVitalsSample ();

                    if (card.Session?.IsBroadcasting ?? false)
                        _ = card.Session.PostCurrentStep ();
                }
            };
            _UITimer.Start ();

            Closing += OnClosing;

            Closed += (s, e) => {
                _UITimer.Stop ();
                Instance?.Cleanup ();
            };
        }

        /* Hold the close until every LIVE room has posted its resting patient (see
         * RoomSession.StopBroadcast); otherwise the process exits mid-post and each room's
         * end state stays on the relay. */
        private bool _ScrubbedForClose = false;

        private async void OnClosing (object? s, WindowClosingEventArgs e) {
            if (_ScrubbedForClose || !_Cards.Any (c => c.Session?.IsBroadcasting ?? false))
                return;

            e.Cancel = true;
            ShowStatus ("Taking rooms off air…");

            await StopBroadcasts (_Cards.Select (c => c.Session));

            _ScrubbedForClose = true;
            Close ();
        }

        /* Bounded so a dead relay can't hang the dashboard: StopBroadcast's HTTP timeout is far longer */
        private static async Task StopBroadcasts (IEnumerable<RoomSession?> sessions) {
            Task all = Task.WhenAll (sessions.Where (rs => rs is not null).Select (rs => rs!.StopBroadcast ()));
            await Task.WhenAny (all, Task.Delay (TimeSpan.FromSeconds (5)));
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        /* Room management */

        /* Rooms are permanent (name + credentials, saved in the layout); scenarios are per class.
         * A room is created with or without a scenario, and a scenario that fails to load leaves
         * an empty room rather than no room -- the room PC's launcher still points at it. */

        private async Task<RoomCard?> AddRoom (string? scenarioFile = null, string? roomName = null,
                string? accession = null, string? passwordAccess = null, string? passwordEdit = null) {
            if (Instance is null)
                return null;

            RoomSession session = new (Instance.Timer_Main);

            /* Count+1 alone repeats a name after a removal (1,2,3 -> remove 2 -> add = a second "Room 3") */
            int n = Instance.Sessions.Count + 1;
            while (Instance.Sessions.Any (rs => rs.RoomName == $"Room {n}"))
                n++;
            session.RoomName = roomName ?? $"Room {n}";
            session.Mirror.Accession = accession ?? $"ROOM{n:00}{Utility.RandomString (4)}";
            session.Mirror.PasswordAccess = passwordAccess ?? Utility.RandomString (8);
            session.Mirror.PasswordEdit = passwordEdit ?? Utility.RandomString (8);

            if (!String.IsNullOrEmpty (scenarioFile) && !await session.LoadScenarioFile (scenarioFile))
                ShowStatus ($"Could not load scenario for {session.RoomName}: {System.IO.Path.GetFileName (scenarioFile)}", true);

            Instance.Sessions.Add (session);

            RoomCard card = new (session, this);
            card.SetCompact (IsCompact);
            _Cards.Add (card);
            this.GetControl<WrapPanel> ("panelRooms").Children.Add (card);

            return card;
        }

        public async Task RemoveRoom (RoomCard card) {
            card.Detach ();

            if (card.Session is not null) {
                await StopBroadcasts (new [] { card.Session });
                Instance?.Sessions.Remove (card.Session);
                card.Session.Dispose ();
            }

            _Cards.Remove (card);
            this.GetControl<WrapPanel> ("panelRooms").Children.Remove (card);
        }

        /* Toolbar actions */

        /* "Which stations?": picked stations are added with their fixed credentials and go live at
         * once, so their room PCs (already waiting at the launcher) connect without another click */
        private async void OnClick_AddRoom (object? s, RoutedEventArgs e) {
            StationMap? map = StationMap.Load ();

            if (map is null) {
                await AddRoom ();
                return;
            }

            HashSet<string> inUse = new (Instance?.Sessions.Select (rs => rs.Mirror.Accession) ?? Enumerable.Empty<string> (),
                StringComparer.OrdinalIgnoreCase);

            DialogStations dlg = new (map, inUse);
            await dlg.ShowDialog (this);

            if (dlg.Blank) {
                await AddRoom ();
                return;
            }

            foreach (StationMap.Station st in dlg.Picked) {
                RoomCard? card = await AddRoom (null, st.Name, st.Accession, st.PasswordAccess, st.PasswordEdit);

                if (card is not null)
                    await Broadcast (card);
            }
        }

        /* Shared by the per-card "Scenario…" button */
        public async Task<string?> PickScenarioFile () {
            if (!StorageProvider.CanOpen)
                return null;

            var files = await StorageProvider.OpenFilePickerAsync (new FilePickerOpenOptions () {
                Title = "Select a scenario file",
                AllowMultiple = false,
                FileTypeFilter = new [] {
                    new FilePickerFileType ("Infirmary Integrated Simulations") {
                        Patterns = new [] { "*.ii" },
                        MimeTypes = new [] { "text/plain" }
                    }
                },
            });

            return files.FirstOrDefault ()?.TryGetLocalPath ();
        }

        private async void OnClick_LoadLayout (object? s, RoutedEventArgs e) {
            if (!StorageProvider.CanOpen)
                return;

            var files = await StorageProvider.OpenFilePickerAsync (new FilePickerOpenOptions () {
                Title = "Load lab layout",
                AllowMultiple = false,
                FileTypeFilter = new [] {
                    new FilePickerFileType ("II Command Lab Layout") {
                        Patterns = new [] { "*.iicmd" },
                        MimeTypes = new [] { "application/json" }
                    }
                },
            });

            string? path = files.FirstOrDefault ()?.TryGetLocalPath ();
            if (path is null)
                return;

            LabLayout? layout = await LabLayout.Load (path);
            if (layout is null) {
                ShowStatus ($"Could not read layout: {System.IO.Path.GetFileName (path)}", true);
                return;
            }

            /* Clear existing rooms, then populate from the layout */
            await StopBroadcasts (_Cards.Select (c => c.Session));
            foreach (RoomCard card in _Cards.ToList ())
                await RemoveRoom (card);

            this.GetControl<TextBox> ("tbServerAddress").Text = layout.ServerAddress;
            _SimulatorPath = layout.SimulatorPath;

            foreach (LabLayout.RoomDefinition room in layout.Rooms)
                await AddRoom (room.ScenarioFile, room.Name, room.Accession, room.PasswordAccess, room.PasswordEdit);
        }

        private async void OnClick_SaveLayout (object? s, RoutedEventArgs e) {
            if (!StorageProvider.CanSave)
                return;

            IStorageFile? file = await StorageProvider.SaveFilePickerAsync (new FilePickerSaveOptions () {
                Title = "Save lab layout",
                SuggestedFileName = "lab layout",
                DefaultExtension = "iicmd",
                FileTypeChoices = new [] {
                    new FilePickerFileType ("II Command Lab Layout") {
                        Patterns = new [] { "*.iicmd" },
                        MimeTypes = new [] { "application/json" }
                    }
                },
            });

            string? path = file?.TryGetLocalPath ();
            if (path is null || Instance is null)
                return;

            LabLayout layout = new () {
                ServerAddress = ServerAddress,
                SimulatorPath = _SimulatorPath,
                Rooms = Instance.Sessions.Select (rs => new LabLayout.RoomDefinition () {
                    Name = rs.RoomName,
                    ScenarioFile = rs.ScenarioFilePath,
                    Accession = rs.Mirror.Accession,
                    PasswordAccess = rs.Mirror.PasswordAccess,
                    PasswordEdit = rs.Mirror.PasswordEdit,
                }).ToList (),
            };

            try {
                await LabLayout.Save (layout, path);
                ShowStatus ($"Layout saved: {System.IO.Path.GetFileName (path)}");
            } catch (Exception ex) {
                CrashLog.Write ("SaveLayout", ex, path);
                ShowStatus ($"Layout NOT saved: {ex.Message}", true);
            }
        }

        /* Toolbar status line, for outcomes the instructor must see (failed saves/exports/broadcasts) */
        public void ShowStatus (string text, bool isError = false) {
            Dispatcher.UIThread.Post (() => {
                TextBlock txt = this.GetControl<TextBlock> ("txtStatus");
                txt.Text = text;
                txt.Foreground = isError ? Avalonia.Media.Brushes.Red : Avalonia.Media.Brushes.DimGray;
            });
        }

        private void OnClick_Compact (object? s, RoutedEventArgs e) {
            bool compact = IsCompact;

            foreach (RoomCard card in _Cards)
                card.SetCompact (compact);
        }

        /* Writes one .bat per room that launches II:SIM straight into that room's mirror
         * session, fullscreen on the patient monitor. Put each on its room machine's desktop. */

        private async void OnClick_ExportLaunchers (object? s, RoutedEventArgs e) {
            if (Instance is null || Instance.Sessions.Count == 0 || !StorageProvider.CanOpen)
                return;

            if (String.IsNullOrEmpty (_SimulatorPath) || !System.IO.File.Exists (_SimulatorPath)) {
                var exes = await StorageProvider.OpenFilePickerAsync (new FilePickerOpenOptions () {
                    Title = "Locate the II:SIM executable as installed on room machines",
                    AllowMultiple = false,
                });

                _SimulatorPath = exes.FirstOrDefault ()?.TryGetLocalPath ();

                if (String.IsNullOrEmpty (_SimulatorPath))
                    return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync (new FolderPickerOpenOptions () {
                Title = "Choose a folder for the room launcher scripts",
                AllowMultiple = false,
            });

            string? dir = folders.FirstOrDefault ()?.TryGetLocalPath ();
            if (String.IsNullOrEmpty (dir))
                return;

            try {
                await WriteLaunchers (dir);
                ShowStatus ($"Exported {Instance.Sessions.Count} room launcher(s) to {dir}");
            } catch (Exception ex) {
                CrashLog.Write ("ExportLaunchers", ex, dir);
                ShowStatus ($"Launchers NOT exported: {ex.Message}", true);
            }
        }

        private async Task WriteLaunchers (string dir) {
            if (Instance is null || _SimulatorPath is null)
                return;

            /* Two rooms can share a name; never let one launcher silently overwrite another */
            HashSet<string> used = new (StringComparer.OrdinalIgnoreCase);

            foreach (RoomSession rs in Instance.Sessions) {
                StringBuilder sb = new ();

                sb.AppendLine ("@echo off");
                sb.AppendLine ($"rem Infirmary Integrated room launcher: {rs.RoomName}");
                sb.AppendLine ($"rem Scenario: {rs.Scenario?.Name ?? System.IO.Path.GetFileName (rs.ScenarioFilePath ?? "")}");
                sb.AppendLine ($"rem Generated by II Command {DateTime.Now:yyyy-MM-dd HH:mm}");
                sb.AppendLine ();

                sb.Append ($"start \"\" {BatArg (_SimulatorPath)} --receive --accession {BatArg (rs.Mirror.Accession)}");

                if (!String.IsNullOrEmpty (rs.Mirror.PasswordAccess))
                    sb.Append ($" --password {BatArg (rs.Mirror.PasswordAccess)}");

                if (!String.IsNullOrEmpty (ServerAddress))
                    sb.Append ($" --server {BatArg (ServerAddress)}");

                sb.AppendLine (" --monitor --fullscreen");

                string name = SessionLog.SanitizeFilename (rs.RoomName);
                for (int i = 2; !used.Add (name); i++)
                    name = $"{SessionLog.SanitizeFilename (rs.RoomName)}_{i}";

                await LabLayout.WriteAtomic (System.IO.Path.Combine (dir, $"{name}.bat"), sb.ToString ());
            }
        }

        /* Quoted so & | ^ and spaces are literal; % is still expanded inside quotes, so doubled.
         * A " cannot be escaped in a cmd argument at all (e.g. a hand-edited layout), so refuse it. */
        private static string BatArg (string value) {
            if (value.Contains ('"'))
                throw new InvalidOperationException ($"Value contains a double quote, which a launcher can't carry: {value}");

            return $"\"{value.Replace ("%", "%%")}\"";
        }

        private async void OnClick_BroadcastAll (object? s, RoutedEventArgs e) {
            foreach (RoomCard card in _Cards.ToList ())
                await Broadcast (card);
        }

        private async Task Broadcast (RoomCard card) {
            if (card.Session is null || card.Session.IsBroadcasting)
                return;

            Server.ServerResponse resp = await card.Session.StartBroadcast (
                ServerAddress,
                card.Session.Mirror.Accession,
                card.Session.Mirror.PasswordAccess,
                card.Session.Mirror.PasswordEdit);

            if (resp == Server.ServerResponse.Success)
                card.RefreshBroadcastStatus ();
            else
                card.ShowBroadcastError (resp);
        }
    }
}
