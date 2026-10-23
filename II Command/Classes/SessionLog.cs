/* SessionLog.cs
 * Infirmary Integrated Command
 *
 * Per-room record of one scenario run: step changes (and what caused them), instructor
 * vitals overrides, step-timer pause/resume, broadcast on/off, and a vitals sample every
 * few seconds. Each entry is appended to a CSV as it happens, so a crash loses nothing
 * already written; WriteReport() turns the run into a one-page HTML debrief.
 *
 * Contains no student identity: rooms only. Once someone pairs a log with a roster it is an
 * education record (FERPA) and belongs in an approved system, not a shared folder or the relay.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace IICMD {

    public class SessionLog {

        public record Vitals (int HR, int NSBP, int NDBP, int SPO2, int RR, int ETCO2, double T);

        public record Entry (DateTime Wall, double Elapsed, string Event, string? Step, string? Detail, Vitals? Vitals);

        public static string DefaultFolder { get; } = Path.Combine (
            Environment.GetFolderPath (Environment.SpecialFolder.MyDocuments),
            "Infirmary Integrated", "Session Logs");

        public string RoomName { get; }
        public string ScenarioName { get; }
        public string FilePath { get; }
        public DateTime Started { get; }
        public List<Entry> Entries { get; } = new ();

        private readonly Stopwatch _Clock = Stopwatch.StartNew ();
        private readonly object _Lock = new ();
        private bool _WriteFailed = false;

        private const string Header = "wall_time,elapsed_s,event,step,detail,hr,nsbp,ndbp,spo2,rr,etco2,temp_c";

        public SessionLog (string roomName, string scenarioName, string? folder = null) {
            RoomName = roomName;
            ScenarioName = scenarioName;
            Started = DateTime.Now;

            string dir = Path.Combine (folder ?? DefaultFolder, Started.ToString ("yyyy-MM-dd"));
            string stem = $"{SanitizeFilename (roomName)}_{Started:HHmmss}";
            FilePath = Path.Combine (dir, $"{stem}.csv");

            /* Two rooms with the same name loaded in the same second must not share a file */
            for (int i = 2; File.Exists (FilePath); i++)
                FilePath = Path.Combine (dir, $"{stem}_{i}.csv");

            Append (Header);
        }

        public void Add (string evt, string? step = null, string? detail = null, Vitals? vitals = null) {
            Entry e = new (DateTime.Now, Math.Round (_Clock.Elapsed.TotalSeconds, 1), evt, step, detail, vitals);

            lock (_Lock)
                Entries.Add (e);

            Append (ToCsv (e));
        }

        public static string ToCsv (Entry e) {
            Vitals? v = e.Vitals;
            string [] fields = {
                e.Wall.ToString ("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                e.Elapsed.ToString ("0.0", CultureInfo.InvariantCulture),
                e.Event, e.Step ?? "", e.Detail ?? "",
                v?.HR.ToString () ?? "", v?.NSBP.ToString () ?? "", v?.NDBP.ToString () ?? "",
                v?.SPO2.ToString () ?? "", v?.RR.ToString () ?? "", v?.ETCO2.ToString () ?? "",
                v?.T.ToString ("0.0", CultureInfo.InvariantCulture) ?? "",
            };
            return String.Join (",", fields.Select (CsvField));
        }

        /* RFC 4180 quoting; a leading = + - @ is prefixed so Excel can't run a step name as a formula */
        public static string CsvField (string s) {
            if (s.Length > 0 && "=+-@".Contains (s [0]) && !double.TryParse (s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                s = "'" + s;

            return s.IndexOfAny (new [] { ',', '"', '\n', '\r' }) >= 0
                ? $"\"{s.Replace ("\"", "\"\"")}\""
                : s;
        }

        private void Append (string line) {
            lock (_Lock) {
                try {
                    Directory.CreateDirectory (Path.GetDirectoryName (FilePath)!);
                    File.AppendAllText (FilePath, line + Environment.NewLine);
                } catch (Exception ex) {
                    /* Logged once: a full disk or locked file must not take the room down, or spam the crash log */
                    if (!_WriteFailed)
                        II.CrashLog.Write ("SessionLog", ex, FilePath);
                    _WriteFailed = true;
                }
            }
        }

        public static string SanitizeFilename (string name) {
            foreach (char c in Path.GetInvalidFileNameChars ())
                name = name.Replace (c, '_');

            name = name.Trim ();

            return String.IsNullOrEmpty (name) ? "Room" : name;
        }

        /* Debrief report: path taken, authored steps never reached, vitals over time, full event list.
         * Written next to the CSV. shortcut: built from this run's in-memory entries, so a log from
         * an earlier app run can't be re-reported; add a CSV reader if that's ever needed. */

        public string WriteReport (IEnumerable<string> authoredSteps) {
            string path = Path.ChangeExtension (FilePath, ".html");
            File.WriteAllText (path, BuildReport (authoredSteps));
            return path;
        }

        public string BuildReport (IEnumerable<string> authoredSteps) {
            List<Entry> entries;
            lock (_Lock)
                entries = Entries.ToList ();

            static string H (string? s) => WebUtility.HtmlEncode (s ?? "");
            static string Clock (double s) => TimeSpan.FromSeconds (s).ToString (@"h\:mm\:ss");

            List<Entry> path = entries.Where (e => e.Event is "session_start" or "step").ToList ();
            HashSet<string> visited = new (path.Select (e => e.Step ?? ""));
            List<string> notVisited = authoredSteps.Distinct ().Where (s => !visited.Contains (s)).ToList ();
            double duration = entries.Count > 0 ? entries [^1].Elapsed : 0;

            StringBuilder sb = new ();
            sb.AppendLine ("<!doctype html><html><head><meta charset=\"utf-8\">");
            sb.AppendLine ($"<title>Debrief — {H (RoomName)}</title>");
            sb.AppendLine ("<style>body{font:14px system-ui,sans-serif;margin:24px;max-width:1000px;color:#222}"
                + "h1{font-size:20px}h2{font-size:16px;margin-top:28px}table{border-collapse:collapse;width:100%}"
                + "td,th{border-bottom:1px solid #ddd;padding:4px 8px;text-align:left;vertical-align:top}"
                + "th{background:#f4f4f4}.muted{color:#666}svg{width:100%;height:auto;border:1px solid #ddd}"
                + "@media print{h2{break-after:avoid}}</style></head><body>");

            sb.AppendLine ($"<h1>Debrief — {H (RoomName)}</h1>");
            sb.AppendLine ($"<p>Scenario: <b>{H (ScenarioName)}</b><br>Started {Started:yyyy-MM-dd HH:mm:ss} · "
                + $"Duration {Clock (duration)}<br><span class=\"muted\">Elapsed times are wall-clock seconds since the "
                + "scenario was loaded. Steps reflect instructor choices and step timers, not observed learner actions.</span></p>");

            sb.AppendLine ("<h2>Path taken</h2><table><tr><th>Time</th><th>Step</th><th>How</th></tr>");
            foreach (Entry e in path)
                sb.AppendLine ($"<tr><td>{Clock (e.Elapsed)}</td><td>{H (e.Step)}</td><td>{H (e.Detail)}</td></tr>");
            sb.AppendLine ("</table>");

            sb.AppendLine ("<h2>Authored steps not reached</h2>");
            sb.AppendLine (notVisited.Count == 0
                ? "<p class=\"muted\">None — every step was visited.</p>"
                : "<ul>" + String.Concat (notVisited.Select (s => $"<li>{H (s)}</li>")) + "</ul>");

            sb.AppendLine ("<h2>Vitals</h2>");
            sb.AppendLine (VitalsChart (entries, duration));

            sb.AppendLine ("<h2>All events</h2><table><tr><th>Time</th><th>Event</th><th>Step</th><th>Detail</th></tr>");
            foreach (Entry e in entries.Where (e => e.Event != "vitals"))
                sb.AppendLine ($"<tr><td>{Clock (e.Elapsed)}</td><td>{H (e.Event)}</td><td>{H (e.Step)}</td><td>{H (e.Detail)}</td></tr>");
            sb.AppendLine ("</table>");

            sb.AppendLine ($"<p class=\"muted\">Source data: {H (Path.GetFileName (FilePath))}</p></body></html>");
            return sb.ToString ();
        }

        private static string VitalsChart (List<Entry> entries, double duration) {
            List<Entry> samples = entries.Where (e => e.Vitals is not null).ToList ();

            if (samples.Count < 2 || duration <= 0)
                return "<p class=\"muted\">Not enough vitals samples to chart.</p>";

            const int W = 960, Ht = 320, L = 40, R = 10, T = 10, B = 30;
            double X (double s) => L + (s / duration) * (W - L - R);
            double Y (double v) => T + (1 - Math.Clamp (v, 0, 200) / 200) * (Ht - T - B);   // One shared 0-200 axis
            string F (double d) => d.ToString ("0.#", CultureInfo.InvariantCulture);

            (string Name, string Color, Func<Vitals, double> Get) [] series = {
                ("HR", "#2e7d32", v => v.HR),
                ("SBP", "#c62828", v => v.NSBP),
                ("DBP", "#ef9a9a", v => v.NDBP),
                ("SpO2", "#1565c0", v => v.SPO2),
                ("RR", "#b8860b", v => v.RR),
                ("EtCO2", "#757575", v => v.ETCO2),
            };

            StringBuilder sb = new ();
            sb.AppendLine ($"<svg viewBox=\"0 0 {W} {Ht}\" role=\"img\" aria-label=\"Vitals over time\" font-size=\"11\">");

            for (int v = 0; v <= 200; v += 50)
                sb.AppendLine ($"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{F (Y (v))}\" y2=\"{F (Y (v))}\" stroke=\"#eee\"/>"
                    + $"<text x=\"{L - 6}\" y=\"{F (Y (v) + 4)}\" text-anchor=\"end\" fill=\"#666\">{v}</text>");

            foreach (Entry e in entries.Where (e => e.Event == "step"))
                sb.AppendLine ($"<line x1=\"{F (X (e.Elapsed))}\" x2=\"{F (X (e.Elapsed))}\" y1=\"{T}\" y2=\"{Ht - B}\" "
                    + $"stroke=\"#999\" stroke-dasharray=\"3 3\"><title>{WebUtility.HtmlEncode (e.Step ?? "")}</title></line>");

            foreach (var (name, color, get) in series) {
                string pts = String.Join (" ", samples.Select (e => $"{F (X (e.Elapsed))},{F (Y (get (e.Vitals!)))}"));
                sb.AppendLine ($"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.8\" points=\"{pts}\"><title>{name}</title></polyline>");
            }

            sb.AppendLine ($"<text x=\"{L}\" y=\"{Ht - 8}\" fill=\"#666\">0:00:00</text>"
                + $"<text x=\"{W - R}\" y=\"{Ht - 8}\" text-anchor=\"end\" fill=\"#666\">{TimeSpan.FromSeconds (duration):h\\:mm\\:ss}</text>");
            sb.AppendLine ("</svg>");

            sb.AppendLine ("<p>" + String.Join (" · ", series.Select (s => $"<span style=\"color:{s.Color}\">■</span> {s.Name}"))
                + " <span class=\"muted\">— dashed lines are step changes (hover for name)</span></p>");

            return sb.ToString ();
        }
    }
}
