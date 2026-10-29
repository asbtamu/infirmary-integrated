/* StationMap.cs
 * Infirmary Integrated Command
 *
 * The lab's room PCs ("stations") and the floor plan the Add Room picker draws them on.
 * The plan uses OSCEtime's board format (engine/campus_layout.py): a rows x cols grid of
 * [row, col, rowspan, colspan, text] cells, so a floor plan baked for OSCEtime pastes in as-is.
 *
 * Each station carries the fixed credentials its room PC's launcher was made with. Read from
 * stations.json on the update share first, so both control PCs share one copy and credentials
 * stay out of the source repository; a stations.json beside the exe is the fallback.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IICMD {

    public class StationMap {
        public record Cell (int Row, int Col, int RowSpan, int ColSpan, string Text);
        public record Station (string Key, string Name, string Accession, string PasswordAccess, string PasswordEdit);

        public string Source { get; init; } = "";
        public int Rows { get; init; }
        public int Cols { get; init; }
        public List<Cell> Cells { get; init; } = new ();
        public Dictionary<string, Station> Stations { get; init; } = new (StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> SearchPaths () => new [] {
            Path.Combine (II.Updater.Share, "stations.json"),
            Path.Combine (AppContext.BaseDirectory, "stations.json"),
        };

        /* First readable file wins; null when there is none (Add Room then makes a blank room) */
        public static StationMap? Load () {
            foreach (string path in SearchPaths ()) {
                try {
                    if (File.Exists (path))
                        return Parse (File.ReadAllText (path), path);
                } catch (Exception ex) {
                    II.CrashLog.Write ("StationMap", ex, path);
                }
            }
            return null;
        }

        public static StationMap Parse (string json, string source) {
            using JsonDocument doc = JsonDocument.Parse (json);
            JsonElement root = doc.RootElement;

            List<Cell> cells = root.GetProperty ("cells").EnumerateArray ()
                .Select (c => new Cell (c [0].GetInt32 (), c [1].GetInt32 (), c [2].GetInt32 (), c [3].GetInt32 (), c [4].GetString () ?? ""))
                .ToList ();

            Dictionary<string, Station> stations = new (StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty p in root.GetProperty ("stations").EnumerateObject ()) {
                string Get (string name) => p.Value.TryGetProperty (name, out JsonElement v) ? v.GetString () ?? "" : "";
                stations [p.Name] = new Station (p.Name,
                    Get ("name") is { Length: > 0 } n ? n : p.Name,
                    Get ("accession"), Get ("passwordAccess"), Get ("passwordEdit"));
            }

            return new StationMap {
                Source = source,
                Rows = root.GetProperty ("rows").GetInt32 (),
                Cols = root.GetProperty ("cols").GetInt32 (),
                Cells = cells,
                Stations = stations,
            };
        }
    }
}
