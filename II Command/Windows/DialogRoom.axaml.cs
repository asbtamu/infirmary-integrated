/* DialogRoom.axaml.cs
 * Infirmary Integrated Command
 *
 * Per-room identity: name, accession, access password, edit password. Credentials are
 * fixed per physical room and only changeable while the room is off air.
 */

using System;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace IICMD {

    public partial class DialogRoom : Window {
        public RoomSession? Session;
        public bool Changed { get; private set; } = false;

        public DialogRoom () {
            InitializeComponent ();
        }

        public DialogRoom (RoomSession session) {
            InitializeComponent ();

            Session = session;

            this.GetControl<TextBox> ("tbName").Text = session.RoomName;
            this.GetControl<TextBox> ("tbAccession").Text = session.Mirror.Accession;
            this.GetControl<TextBox> ("tbPasswordAccess").Text = session.Mirror.PasswordAccess;
            this.GetControl<TextBox> ("tbPasswordEdit").Text = session.Mirror.PasswordEdit;

            if (session.IsBroadcasting) {
                this.GetControl<TextBox> ("tbAccession").IsEnabled = false;
                this.GetControl<TextBox> ("tbPasswordAccess").IsEnabled = false;
                this.GetControl<TextBox> ("tbPasswordEdit").IsEnabled = false;
                this.GetControl<TextBlock> ("txtNote").Text = "This room is LIVE. Stop broadcasting to change its credentials; the name can still be changed.";
            }
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        private void OnClick_OK (object? s, RoutedEventArgs e) {
            if (Session is null) {
                Close ();
                return;
            }

            string name = (this.GetControl<TextBox> ("tbName").Text ?? "").Trim ();
            if (name.Length > 0 && name != Session.RoomName) {
                Session.RoomName = name;
                Changed = true;
            }

            if (!Session.IsBroadcasting) {
                string accession = this.GetControl<TextBox> ("tbAccession").Text ?? "";
                string pwAccess = this.GetControl<TextBox> ("tbPasswordAccess").Text ?? "";
                string pwEdit = this.GetControl<TextBox> ("tbPasswordEdit").Text ?? "";

                bool differs = accession.Trim ().ToUpperInvariant () != Session.Mirror.Accession
                    || pwAccess.Trim () != Session.Mirror.PasswordAccess
                    || pwEdit.Trim () != Session.Mirror.PasswordEdit;

                if (differs) {
                    if (!Session.SetCredentials (accession, pwAccess, pwEdit, out string? error)) {
                        this.GetControl<TextBlock> ("txtNote").Text = error;
                        return;                                 // Keep the dialog open to fix it
                    }

                    Changed = true;
                }
            }

            Close ();
        }

        private void OnClick_Cancel (object? s, RoutedEventArgs e) => Close ();
    }
}
