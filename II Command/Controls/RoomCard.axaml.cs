/* RoomCard.axaml.cs
 * Infirmary Integrated Command
 *
 * One dashboard card = one RoomSession. Vitals on the left half; the current
 * Step, its branch (Progression) triggers, and step controls on the right half.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using II;
using II.Server;

namespace IICMD {

    public partial class RoomCard : UserControl {
        public RoomSession? Session;
        public WindowMain? Owner;

        public RoomCard () {
            InitializeComponent ();
        }

        public RoomCard (RoomSession session, WindowMain owner) {
            InitializeComponent ();

            Session = session;
            Owner = owner;

            Session.StepChanged += OnStepChanged;

            this.GetControl<TextBox> ("tbRoomName").Text = Session.RoomName;
            this.GetControl<TextBox> ("tbRoomName").LostFocus += (s, e) => {
                if (Session is not null)
                    Session.RoomName = this.GetControl<TextBox> ("tbRoomName").Text ?? Session.RoomName;
            };

            this.GetControl<TextBlock> ("txtScenarioName").Text =
                Session.Scenario?.Name
                ?? Path.GetFileNameWithoutExtension (Session.ScenarioFilePath ?? "")
                ?? "";

            RefreshStep ();
            RefreshVitals ();
            RefreshBroadcastStatus ();
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        /* Refreshers; RefreshVitals is called by WindowMain's UI timer (~q 1 second) */

        public void RefreshVitals () {
            Physiology? p = Session?.Physiology;

            Dispatcher.UIThread.Post (() => {
                if (p is null) {
                    this.GetControl<TextBlock> ("lblHR").Text = "--";
                    this.GetControl<TextBlock> ("lblBP").Text = "--/--";
                    this.GetControl<TextBlock> ("lblSPO2").Text = "--";
                    this.GetControl<TextBlock> ("lblRR").Text = "--";
                    this.GetControl<TextBlock> ("lblETCO2").Text = "--";
                    this.GetControl<TextBlock> ("lblT").Text = "--";
                    return;
                }

                this.GetControl<TextBlock> ("lblHR").Text = $"{p.HR}";
                this.GetControl<TextBlock> ("lblBP").Text = $"{p.NSBP}/{p.NDBP}";
                this.GetControl<TextBlock> ("lblSPO2").Text = $"{p.SPO2}";
                this.GetControl<TextBlock> ("lblRR").Text = $"{p.RR}";
                this.GetControl<TextBlock> ("lblETCO2").Text = $"{p.ETCO2}";
                this.GetControl<TextBlock> ("lblT").Text = $"{p.T:0.0}";
            });
        }

        public void RefreshStep () {
            Dispatcher.UIThread.Post (() => {
                Scenario.Step? step = Session?.Scenario?.Current;

                TextBlock txtStepName = this.GetControl<TextBlock> ("txtStepName");
                TextBlock txtStepTimer = this.GetControl<TextBlock> ("txtStepTimer");
                StackPanel stack = this.GetControl<StackPanel> ("stackProgressions");

                if (step is null) {
                    txtStepName.Text = "No scenario loaded";
                    txtStepTimer.Text = "";
                    stack.Children.Clear ();
                    return;
                }

                txtStepName.Text = String.IsNullOrEmpty (step.Name) ? "(unnamed step)" : step.Name;

                txtStepTimer.Text = (step.ProgressTimer == -1)
                    ? "Manual progression"
                    : $"Auto-advances in {step.ProgressTimer} sec (default branch)";

                this.GetControl<Button> ("btnPreviousStep").IsEnabled = !String.IsNullOrEmpty (step.DefaultSource);
                this.GetControl<Button> ("btnNextStep").IsEnabled =
                    !String.IsNullOrEmpty (step.DefaultProgression?.UUID) || step.Progressions.Count > 0;
                this.GetControl<Button> ("btnPauseStep").IsEnabled = step.ProgressTimer > 0;
                this.GetControl<Button> ("btnPlayStep").IsEnabled = step.ProgressTimer > 0;

                /* One button per branch; the default branch is marked */
                stack.Children.Clear ();

                foreach (Scenario.Step.Progression prog in step.Progressions) {
                    bool isDefault = prog.UUID == step.DefaultProgression?.UUID;

                    Button btn = new () {
                        Content = String.IsNullOrEmpty (prog.Description)
                            ? (isDefault ? "Advance (default)" : "Advance")
                            : (isDefault ? $"{prog.Description} (default)" : prog.Description),
                    };
                    btn.Classes.Add ("progression");

                    Scenario.Step.Progression capture = prog;
                    btn.Click += (s, e) => _ = Session?.NextStep (capture);

                    stack.Children.Add (btn);
                }

                if (step.Progressions.Count == 0)
                    stack.Children.Add (new TextBlock () {
                        Text = "End of scenario",
                        Foreground = Brushes.DimGray,
                        FontSize = 12,
                    });
            });
        }

        public void RefreshBroadcastStatus () {
            Dispatcher.UIThread.Post (() => {
                TextBlock txt = this.GetControl<TextBlock> ("txtBroadcastStatus");
                Border brd = this.GetControl<Border> ("brdCard");
                Button btn = this.GetControl<Button> ("btnBroadcast");

                if (Session?.IsBroadcasting ?? false) {
                    txt.Text = $"● LIVE — Accession: {Session.Mirror.Accession}  Password: {Session.Mirror.PasswordAccess}";
                    txt.Foreground = Brushes.Green;
                    brd.BorderBrush = Brushes.Green;
                    btn.Content = "Stop";
                } else {
                    txt.Text = "Off air";
                    txt.Foreground = Brushes.DimGray;
                    brd.BorderBrush = Brushes.Gray;
                    btn.Content = "Broadcast";
                }
            });
        }

        /* Event handlers */

        private void OnStepChanged (object? sender, EventArgs e) {
            RefreshStep ();
            RefreshVitals ();
        }

        private void OnClick_NextStep (object? s, RoutedEventArgs e)
            => _ = Session?.NextStep ();               // Default branch

        private void OnClick_PreviousStep (object? s, RoutedEventArgs e)
            => _ = Session?.PreviousStep ();

        private void OnClick_PauseStep (object? s, RoutedEventArgs e)
            => _ = Session?.PauseStepTimer ();

        private void OnClick_PlayStep (object? s, RoutedEventArgs e)
            => _ = Session?.PlayStepTimer ();

        private void OnClick_Broadcast (object? s, RoutedEventArgs e)
            => _ = ToggleBroadcast ();

        private async Task ToggleBroadcast () {
            if (Session is null || Owner is null)
                return;

            if (Session.IsBroadcasting) {
                Session.StopBroadcast ();
            } else {
                if (String.IsNullOrEmpty (Session.Mirror.Accession))
                    Session.Mirror.Accession = Utility.RandomString (8);
                if (String.IsNullOrEmpty (Session.Mirror.PasswordAccess))
                    Session.Mirror.PasswordAccess = Utility.RandomString (8);

                Server.ServerResponse resp = await Session.StartBroadcast (
                    Owner.ServerAddress,
                    Session.Mirror.Accession,
                    Session.Mirror.PasswordAccess,
                    Session.Mirror.PasswordEdit);

                if (resp == Server.ServerResponse.ErrorCredentials || resp == Server.ServerResponse.ErrorNameResolution) {
                    Dispatcher.UIThread.Post (() => {
                        this.GetControl<TextBlock> ("txtBroadcastStatus").Text =
                            (resp == Server.ServerResponse.ErrorNameResolution)
                                ? "Error: server inaccessible"
                                : "Error: server response invalid";
                        this.GetControl<TextBlock> ("txtBroadcastStatus").Foreground = Brushes.Red;
                    });
                    return;
                }
            }

            RefreshBroadcastStatus ();
        }

        private void OnClick_Remove (object? s, RoutedEventArgs e)
            => Owner?.RemoveRoom (this);
    }
}
