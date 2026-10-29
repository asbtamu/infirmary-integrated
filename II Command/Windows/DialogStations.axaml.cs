/* DialogStations.axaml.cs
 * Infirmary Integrated Command
 *
 * "Which stations?" -- the Add Room picker. Draws the floor plan from StationMap the way
 * OSCEtime's board does (a label ending in CONTROL is a black booth, an empty label is filler,
 * a station is a clickable room) and returns the stations clicked. Stations already on the
 * dashboard are shown but can't be picked twice.
 */

using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace IICMD {

    public partial class DialogStations : Window {

        /* Picked stations; empty on Cancel. Blank is true when "Blank room" was chosen instead. */
        public List<StationMap.Station> Picked { get; } = new ();
        public bool Blank { get; private set; } = false;

        private readonly List<(ToggleButton Button, StationMap.Station Station)> _Toggles = new ();

        public DialogStations () {
            InitializeComponent ();
        }

        public DialogStations (StationMap map, ISet<string> accessionsInUse) {
            InitializeComponent ();

            Grid grid = this.GetControl<Grid> ("gridPlan");
            for (int r = 0; r < map.Rows; r++)
                grid.RowDefinitions.Add (new RowDefinition (GridLength.Auto));
            for (int c = 0; c < map.Cols; c++)
                grid.ColumnDefinitions.Add (new ColumnDefinition (new GridLength (84)));

            foreach (StationMap.Cell cell in map.Cells) {
                Control ctl = Build (cell, map, accessionsInUse);
                Grid.SetRow (ctl, cell.Row);
                Grid.SetColumn (ctl, cell.Col);
                Grid.SetRowSpan (ctl, Math.Max (1, cell.RowSpan));
                Grid.SetColumnSpan (ctl, Math.Max (1, cell.ColSpan));
                grid.Children.Add (ctl);
            }

            this.GetControl<TextBlock> ("txtSource").Text = $"Stations from {map.Source}";
        }

        private Control Build (StationMap.Cell cell, StationMap map, ISet<string> inUse) {
            if (map.Stations.TryGetValue (cell.Text, out StationMap.Station? st)) {
                bool added = inUse.Contains (st.Accession);
                ToggleButton tb = new () {
                    Content = added ? $"{st.Name}\n(added)" : st.Name,
                    IsEnabled = !added,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    MinHeight = 52,
                    Margin = new Thickness (2),
                    FontWeight = FontWeight.Bold,
                };
                tb.IsCheckedChanged += (s, e) =>
                    this.GetControl<Button> ("btnAdd").IsEnabled = _Toggles.Any (t => t.Button.IsChecked == true);
                _Toggles.Add ((tb, st));
                return tb;
            }

            bool control = cell.Text.TrimEnd ().EndsWith ("CONTROL", StringComparison.OrdinalIgnoreCase);
            return new Border {
                Background = control ? Brushes.Black : (cell.Text.Length == 0 ? Brushes.Transparent : Brushes.WhiteSmoke),
                BorderBrush = cell.Text.Length == 0 ? Brushes.Transparent : Brushes.LightGray,
                BorderThickness = new Thickness (1),
                Margin = new Thickness (2),
                MinHeight = 28,
                Child = new TextBlock {
                    Text = cell.Text,
                    Foreground = control ? Brushes.White : Brushes.Gray,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
        }

        private void InitializeComponent () {
            AvaloniaXamlLoader.Load (this);
        }

        private void OnClick_Add (object? s, RoutedEventArgs e) {
            Picked.AddRange (_Toggles.Where (t => t.Button.IsChecked == true).Select (t => t.Station));
            Close ();
        }

        private void OnClick_Blank (object? s, RoutedEventArgs e) {
            Blank = true;
            Close ();
        }

        private void OnClick_Cancel (object? s, RoutedEventArgs e) => Close ();
    }
}
