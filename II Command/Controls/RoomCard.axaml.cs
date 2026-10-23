/* RoomCard.axaml.cs
 * Infirmary Integrated Command
 *
 * One dashboard card = one RoomSession. Vitals on the left half; the current
 * Step, its branch (Progression) triggers, and step controls on the right half.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

        private DialogVitals? _DialogVitals;

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

            InitVitalEditing ();

            RefreshHeader ();
            RefreshStep ();
            RefreshVitals ();
            RefreshBroadcastStatus ();
        }

        /* Inline vitals editing on the card.
         *
         * Double-click a value to type a new one (Enter applies, Esc cancels). With the toolbar
         * toggle on, the mouse wheel over a value nudges it by one (0.1 for temperature).
         * Wheel nudges are debounced: the label shows the pending target in orange immediately,
         * and one apply + one relay post go out ~600 ms after the last notch, matching how
         * II:SIM's Control window batches parameter changes rather than re-baselining the
         * rhythm engine on every tick. */

        private static readonly (string Name, RoomSession.Vital Vital) [] _EditableVitals = {
            ("HR", RoomSession.Vital.HR),
            ("NSBP", RoomSession.Vital.NSBP),
            ("NDBP", RoomSession.Vital.NDBP),
            ("SPO2", RoomSession.Vital.SPO2),
            ("RR", RoomSession.Vital.RR),
            ("ETCO2", RoomSession.Vital.ETCO2),
            ("T", RoomSession.Vital.T),
        };

        private readonly Dictionary<RoomSession.Vital, double> _Pending = new ();
        private readonly Dictionary<string, IBrush?> _LabelBrushes = new ();
        private readonly DispatcherTimer _ApplyTimer = new () { Interval = TimeSpan.FromMilliseconds (600) };
        private static readonly IBrush _PendingBrush = Brushes.DarkOrange;

        private static string Format (RoomSession.Vital v, double value)
            => v == RoomSession.Vital.T ? value.ToString ("0.0", CultureInfo.InvariantCulture) : ((int)value).ToString ();

        private void InitVitalEditing () {
            _ApplyTimer.Tick += (s, e) => {
                _ApplyTimer.Stop ();
                _ = FlushPending ();
            };

            foreach (var (name, vital) in _EditableVitals) {
                TextBlock lbl = this.GetControl<TextBlock> ($"lbl{name}");
                TextBox tb = this.GetControl<TextBox> ($"tb{name}");

                _LabelBrushes [name] = lbl.Foreground;

                lbl.DoubleTapped += (s, e) => BeginEdit (name, vital);
                lbl.PointerWheelChanged += (s, e) => OnVitalWheel (name, vital, e);

                tb.KeyDown += (s, e) => {
                    if (e.Key == Key.Enter) {
                        CommitEdit (name, vital);
                        e.Handled = true;
                    } else if (e.Key == Key.Escape) {
                        EndEdit (name);
                        e.Handled = true;
                    }
                };

                tb.LostFocus += (s, e) => EndEdit (name);
            }
        }

        private void BeginEdit (string name, RoomSession.Vital vital) {
            double? current = Session?.GetSetting (vital);

            if (current is null)
                return;

            TextBlock lbl = this.GetControl<TextBlock> ($"lbl{name}");
            TextBox tb = this.GetControl<TextBox> ($"tb{name}");

            tb.Text = Format (vital, _Pending.TryGetValue (vital, out double pending) ? pending : current.Value);
            lbl.IsVisible = false;
            tb.IsVisible = true;
            tb.Focus ();
            tb.SelectAll ();
        }

        private void CommitEdit (string name, RoomSession.Vital vital) {
            TextBox tb = this.GetControl<TextBox> ($"tb{name}");
            string text = (tb.Text ?? "").Trim ();

            if (double.TryParse (text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || double.TryParse (text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) {
                _Pending [vital] = RoomSession.Clamp (vital, value);
                ShowPending (name, vital);
            }

            EndEdit (name);

            _ApplyTimer.Stop ();
            _ = FlushPending ();                    // Typed values apply immediately
        }

        private void EndEdit (string name) {
            TextBox tb = this.GetControl<TextBox> ($"tb{name}");

            if (!tb.IsVisible)
                return;

            tb.IsVisible = false;
            this.GetControl<TextBlock> ($"lbl{name}").IsVisible = true;
        }

        private void OnVitalWheel (string name, RoomSession.Vital vital, PointerWheelEventArgs e) {
            if (!(Owner?.ScrollAdjustsVitals ?? false))
                return;                             // Toggle off: let the wheel scroll the dashboard as normal

            double? current = Session?.GetSetting (vital);

            if (current is null)
                return;

            e.Handled = true;                       // Don't also scroll the room panel

            double baseValue = _Pending.TryGetValue (vital, out double pending) ? pending : current.Value;
            double step = RoomSession.Step (vital) * (e.Delta.Y > 0 ? 1 : -1);

            _Pending [vital] = RoomSession.Clamp (vital, baseValue + step);
            ShowPending (name, vital);

            _ApplyTimer.Stop ();
            _ApplyTimer.Start ();
        }

        private void ShowPending (string name, RoomSession.Vital vital) {
            TextBlock lbl = this.GetControl<TextBlock> ($"lbl{name}");
            lbl.Text = Format (vital, _Pending [vital]);
            lbl.Foreground = _PendingBrush;
        }

        private async Task FlushPending () {
            if (_Pending.Count == 0)
                return;

            Dictionary<RoomSession.Vital, double> batch = new (_Pending);
            _Pending.Clear ();

            if (Session is not null)
                await Session.ApplyVitalOverrides (batch);

            Dispatcher.UIThread.Post (() => {
                foreach (var (name, _) in _EditableVitals)
                    this.GetControl<TextBlock> ($"lbl{name}").Foreground = _LabelBrushes [name];

                _DialogVitals?.LoadFromRoom ();
            });

            RefreshVitals ();
        }

        public void RefreshHeader () {
            Dispatcher.UIThread.Post (() => {
                if (Session is null)
                    return;

                this.GetControl<TextBox> ("tbRoomName").Text = Session.RoomName;

                TextBlock txt = this.GetControl<TextBlock> ("txtScenarioName");

                if (Session.Scenario is null) {
                    txt.Text = "No scenario";
                    txt.Foreground = Brushes.Firebrick;
                } else {
                    txt.Text = !String.IsNullOrEmpty (Session.Scenario.Name)
                        ? Session.Scenario.Name
                        : Path.GetFileNameWithoutExtension (Session.ScenarioFilePath ?? "");
                    txt.Foreground = Brushes.DimGray;
                }

                if (_DialogVitals is not null)
                    _DialogVitals.Title = $"Vitals — {Session.RoomName}";
            });
        }

        /* Loads (or replaces) this room's scenario; the room's identity and broadcast are untouched */
        public async Task LoadScenario (string path) {
            if (Session is null)
                return;

            bool ok = await Session.LoadScenarioFile (path);

            if (!ok)
                Owner?.ShowStatus ($"Could not load scenario: {Path.GetFileName (path)}", true);

            RefreshHeader ();
            RefreshStep ();
            RefreshVitals ();
            Dispatcher.UIThread.Post (() => _DialogVitals?.LoadFromRoom ());

            if (ok && Session.IsBroadcasting)
                _ = Session.PostCurrentStep ();     // Push the new scenario to the room now, not in 5 s
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        /* Refreshers; RefreshVitals is called by WindowMain's UI timer (~q 1 second) */

        public void RefreshVitals () {
            Physiology? p = Session?.Physiology;

            Dispatcher.UIThread.Post (() => {
                void Set (string name, RoomSession.Vital vital, string text) {
                    if (_Pending.ContainsKey (vital))
                        return;                     // A pending edit owns this label until it flushes

                    this.GetControl<TextBlock> ($"lbl{name}").Text = text;
                }

                if (p is null) {
                    foreach (var (name, vital) in _EditableVitals)
                        Set (name, vital, "--");
                    return;
                }

                Set ("HR", RoomSession.Vital.HR, $"{p.HR}");
                Set ("NSBP", RoomSession.Vital.NSBP, $"{p.NSBP}");
                Set ("NDBP", RoomSession.Vital.NDBP, $"{p.NDBP}");
                Set ("SPO2", RoomSession.Vital.SPO2, $"{p.SPO2}");
                Set ("RR", RoomSession.Vital.RR, $"{p.RR}");
                Set ("ETCO2", RoomSession.Vital.ETCO2, $"{p.ETCO2}");
                Set ("T", RoomSession.Vital.T, $"{p.T:0.0}");
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
                /* "Next" takes the default progression, so it is only meaningful when one exists
                 * -- or when the step has a single unambiguous exit to fall back to. Enabling it
                 * on a branching step with no default set would destroy the loaded scenario. */
                this.GetControl<Button> ("btnNextStep").IsEnabled =
                    !String.IsNullOrEmpty (step.DefaultProgression?.DestinationUUID)
                    || step.Progressions.Count == 1;
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
                    txt.Text = $"Off air — Accession: {Session?.Mirror.Accession}  Password: {Session?.Mirror.PasswordAccess}";
                    txt.Foreground = Brushes.DimGray;
                    brd.BorderBrush = Brushes.Gray;
                    btn.Content = "Broadcast";
                }
            });
        }

        /* Density. Comfortable fits 12 rooms on a 4K screen; Compact fits 12 on 1080p
         * without scrolling (4 columns x 3 rows). All six vitals stay visible either way. */

        private static readonly string [] _VitalControls = { "lblHR", "lblNSBP", "lblBPSlash", "lblNDBP", "lblSPO2", "lblRR", "lblETCO2", "lblT" };

        public void SetCompact (bool compact) {
            Width = compact ? 440 : 520;

            foreach (string name in _VitalControls)
                this.GetControl<TextBlock> (name).FontSize = compact ? 19 : 26;

            foreach (var (name, _) in _EditableVitals)
                this.GetControl<TextBox> ($"tb{name}").FontSize = compact ? 16 : 22;

            this.GetControl<TextBox> ("tbRoomName").FontSize = compact ? 14 : 16;
            this.GetControl<Border> ("brdCard").Margin = compact ? new Thickness (3) : new Thickness (6);
            this.GetControl<Border> ("brdCard").Padding = compact ? new Thickness (6) : new Thickness (8);
        }

        /* Event handlers */

        private void OnStepChanged (object? sender, EventArgs e) {
            RefreshStep ();
            RefreshVitals ();

            /* An open vitals editor is showing the previous step's targets; refresh it */
            Dispatcher.UIThread.Post (() => _DialogVitals?.LoadFromRoom ());
        }

        private async void OnClick_Scenario (object? s, RoutedEventArgs e) {
            if (Owner is null)
                return;

            string? path = await Owner.PickScenarioFile ();

            if (path is not null)
                await LoadScenario (path);
        }

        private async void OnClick_Room (object? s, RoutedEventArgs e) {
            if (Session is null || Owner is null)
                return;

            DialogRoom dlg = new (Session);
            await dlg.ShowDialog (Owner);

            if (dlg.Changed) {
                RefreshHeader ();
                RefreshBroadcastStatus ();
            }
        }

        private void OnClick_Debrief (object? s, RoutedEventArgs e) {
            try {
                string? path = Session?.WriteDebrief ();

                if (path is null) {
                    Owner?.ShowStatus ($"{Session?.RoomName}: no scenario run to report", true);
                    return;
                }

                Process.Start (new ProcessStartInfo (path) { UseShellExecute = true });
                Owner?.ShowStatus ($"Debrief written: {path}");
            } catch (Exception ex) {
                CrashLog.Write ("Debrief", ex, Session?.Log?.FilePath);
                Owner?.ShowStatus ($"Debrief NOT written: {ex.Message}", true);
            }
        }

        private void OnClick_Vitals (object? s, RoutedEventArgs e) {
            if (Session is null || Owner is null)
                return;

            if (_DialogVitals is not null) {
                _DialogVitals.LoadFromRoom ();
                _DialogVitals.Activate ();
                return;
            }

            _DialogVitals = new DialogVitals (Session);
            _DialogVitals.Closed += (s2, e2) => _DialogVitals = null;
            _DialogVitals.Show (Owner);             // Non-modal: several rooms' editors can be open at once
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

        /* Locked for the whole toggle: StartBroadcast marks the room HOST before its first await,
         * so a second click mid-post would read "broadcasting" and stop it again */
        private async Task ToggleBroadcast () {
            Button btn = this.GetControl<Button> ("btnBroadcast");

            if (!btn.IsEnabled)
                return;

            btn.IsEnabled = false;

            try {
                await ToggleBroadcastUnlocked ();
            } finally {
                btn.IsEnabled = true;
            }
        }

        private async Task ToggleBroadcastUnlocked () {
            if (Session is null || Owner is null)
                return;

            if (Session.IsBroadcasting) {
                await Session.StopBroadcast ();
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

                if (resp != Server.ServerResponse.Success) {
                    ShowBroadcastError (resp);
                    return;
                }
            }

            RefreshBroadcastStatus ();
        }

        public void ShowBroadcastError (Server.ServerResponse resp) {
            Dispatcher.UIThread.Post (() => {
                this.GetControl<TextBlock> ("txtBroadcastStatus").Text = resp switch {
                    Server.ServerResponse.ErrorNameResolution => "Error: server inaccessible",
                    Server.ServerResponse.ErrorCredentials => "Error: edit password rejected by server",
                    _ => "Error: server response invalid",
                };
                this.GetControl<TextBlock> ("txtBroadcastStatus").Foreground = Brushes.Red;
            });
        }

        private void OnClick_Remove (object? s, RoutedEventArgs e)
            => _ = Owner?.RemoveRoom (this);

        /* Called on removal (button or layout load): nothing may touch the session after it is disposed */
        public void Detach () {
            _ApplyTimer.Stop ();
            _Pending.Clear ();
            _DialogVitals?.Close ();

            if (Session is not null)
                Session.StepChanged -= OnStepChanged;
        }
    }
}
