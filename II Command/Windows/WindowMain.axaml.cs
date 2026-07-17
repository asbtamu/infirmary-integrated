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

        private readonly List<RoomCard> _Cards = new ();
        private readonly DispatcherTimer _UITimer = new ();

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
            };
            _UITimer.Start ();

            Closed += (s, e) => {
                _UITimer.Stop ();
                Instance?.Cleanup ();
            };
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        /* Room management */

        private async Task<RoomCard?> AddRoom (string scenarioFile, string? roomName = null,
                string? accession = null, string? passwordAccess = null, string? passwordEdit = null) {
            if (Instance is null)
                return null;

            RoomSession session = new (Instance.Timer_Main);

            if (!await session.LoadScenarioFile (scenarioFile)) {
                session.Dispose ();
                return null;
            }

            session.RoomName = roomName ?? $"Room {Instance.Sessions.Count + 1}";
            session.Mirror.Accession = accession ?? Utility.RandomString (8);
            session.Mirror.PasswordAccess = passwordAccess ?? Utility.RandomString (8);
            session.Mirror.PasswordEdit = passwordEdit ?? "";

            Instance.Sessions.Add (session);

            RoomCard card = new (session, this);
            _Cards.Add (card);
            this.GetControl<WrapPanel> ("panelRooms").Children.Add (card);

            return card;
        }

        public void RemoveRoom (RoomCard card) {
            if (card.Session is not null) {
                Instance?.Sessions.Remove (card.Session);
                card.Session.Dispose ();
            }

            _Cards.Remove (card);
            this.GetControl<WrapPanel> ("panelRooms").Children.Remove (card);
        }

        /* Toolbar actions */

        private async void OnClick_AddRoom (object? s, RoutedEventArgs e) {
            if (!StorageProvider.CanOpen)
                return;

            var files = await StorageProvider.OpenFilePickerAsync (new FilePickerOpenOptions () {
                Title = "Select a scenario file",
                AllowMultiple = true,
                FileTypeFilter = new [] {
                    new FilePickerFileType ("Infirmary Integrated Simulations") {
                        Patterns = new [] { "*.ii" },
                        MimeTypes = new [] { "text/plain" }
                    }
                },
            });

            foreach (IStorageFile file in files) {
                string? path = file.TryGetLocalPath ();
                if (path is not null)
                    await AddRoom (path);
            }
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
            if (layout is null)
                return;

            /* Clear existing rooms, then populate from the layout */
            foreach (RoomCard card in _Cards.ToList ())
                RemoveRoom (card);

            this.GetControl<TextBox> ("tbServerAddress").Text = layout.ServerAddress;

            foreach (LabLayout.RoomDefinition room in layout.Rooms) {
                if (!String.IsNullOrEmpty (room.ScenarioFile))
                    await AddRoom (room.ScenarioFile, room.Name, room.Accession, room.PasswordAccess, room.PasswordEdit);
            }
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
                Rooms = Instance.Sessions.Select (rs => new LabLayout.RoomDefinition () {
                    Name = rs.RoomName,
                    ScenarioFile = rs.ScenarioFilePath,
                    Accession = rs.Mirror.Accession,
                    PasswordAccess = rs.Mirror.PasswordAccess,
                    PasswordEdit = rs.Mirror.PasswordEdit,
                }).ToList (),
            };

            await LabLayout.Save (layout, path);
        }

        private async void OnClick_BroadcastAll (object? s, RoutedEventArgs e) {
            foreach (RoomCard card in _Cards) {
                if (card.Session is not null && !card.Session.IsBroadcasting) {
                    await card.Session.StartBroadcast (
                        ServerAddress,
                        card.Session.Mirror.Accession,
                        card.Session.Mirror.PasswordAccess,
                        card.Session.Mirror.PasswordEdit);

                    card.RefreshBroadcastStatus ();
                }
            }
        }
    }
}
