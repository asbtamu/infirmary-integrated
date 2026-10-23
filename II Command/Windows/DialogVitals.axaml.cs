/* DialogVitals.axaml.cs
 * Infirmary Integrated Command
 *
 * Non-modal per-room editor for the vitals an instructor actually reaches for mid-sim:
 * cardiac rhythm, HR, BP, SpO2, RR, EtCO2, temperature. Apply pushes through
 * RoomSession.ApplyVitals (which mirrors II:SIM's parameter-apply path) and posts to
 * the relay immediately. Non-modal so several rooms' editors can stay open at once.
 */

using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using II;

namespace IICMD {

    public partial class DialogVitals : Window {
        public RoomSession? Session;

        private static readonly Cardiac_Rhythms.Values [] _Rhythms
            = Enum.GetValues<Cardiac_Rhythms.Values> ();

        public DialogVitals () {
            InitializeComponent ();
        }

        public DialogVitals (RoomSession session) {
            InitializeComponent ();

            Session = session;

            Title = $"Vitals — {session.RoomName}";
            this.GetControl<TextBlock> ("txtRoom").Text = session.RoomName;

            ComboBox combo = this.GetControl<ComboBox> ("comboRhythm");
            combo.ItemsSource = _Rhythms.Select (r => Utility.UnderscoreToSpace (r.ToString ())).ToList ();

            LoadFromRoom ();
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        /* Populates the fields from the room's *settings* (targets), not its momentary actuals */

        public void LoadFromRoom () {
            Physiology? p = Session?.Physiology;
            TextBlock status = this.GetControl<TextBlock> ("txtStatus");

            if (p is null) {
                status.Text = "No scenario loaded in this room.";
                this.GetControl<Button> ("btnApply").IsEnabled = false;
                return;
            }

            this.GetControl<ComboBox> ("comboRhythm").SelectedIndex = Array.IndexOf (_Rhythms, p.Cardiac_Rhythm.Value);
            this.GetControl<NumericUpDown> ("numHR").Value = p.VS_Settings.HR;
            this.GetControl<NumericUpDown> ("numNSBP").Value = p.VS_Settings.NSBP;
            this.GetControl<NumericUpDown> ("numNDBP").Value = p.VS_Settings.NDBP;
            this.GetControl<NumericUpDown> ("numSPO2").Value = p.VS_Settings.SPO2;
            this.GetControl<NumericUpDown> ("numRR").Value = p.VS_Settings.RR;
            this.GetControl<NumericUpDown> ("numETCO2").Value = p.VS_Settings.ETCO2;
            this.GetControl<NumericUpDown> ("numT").Value = (decimal)p.VS_Settings.T;

            this.GetControl<Button> ("btnApply").IsEnabled = true;
            status.Text = $"Step: {Session?.Scenario?.Current?.Name ?? "(unnamed)"}";
            status.Foreground = Brushes.DimGray;
        }

        private async Task Apply () {
            if (Session is null || Session.Physiology is null)
                return;

            int Int (string name) => (int)(this.GetControl<NumericUpDown> (name).Value ?? 0);

            int rhythmIndex = this.GetControl<ComboBox> ("comboRhythm").SelectedIndex;
            Cardiac_Rhythms.Values rhythm = (rhythmIndex >= 0 && rhythmIndex < _Rhythms.Length)
                ? _Rhythms [rhythmIndex]
                : Session.Physiology.Cardiac_Rhythm.Value;

            await Session.ApplyVitals (
                Int ("numHR"),
                Int ("numNSBP"), Int ("numNDBP"),
                Int ("numSPO2"),
                (double)(this.GetControl<NumericUpDown> ("numT").Value ?? 37),
                rhythm,
                Int ("numRR"),
                Int ("numETCO2"));

            TextBlock status = this.GetControl<TextBlock> ("txtStatus");
            status.Text = $"Applied {DateTime.Now:HH:mm:ss} — rooms update within ~5 s";
            status.Foreground = Brushes.Green;
        }

        private void OnClick_Apply (object? s, RoutedEventArgs e) => _ = Apply ();

        private void OnClick_Reload (object? s, RoutedEventArgs e) => LoadFromRoom ();

        private void OnClick_Close (object? s, RoutedEventArgs e) => Close ();
    }
}
