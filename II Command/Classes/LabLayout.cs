/* LabLayout.cs
 * Infirmary Integrated Command
 *
 * Saved lab configuration: server address plus one entry per room
 * (name, scenario file, accession, passwords) so a class setup is repeatable.
 * Serialized as JSON.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace IICMD {

    public class LabLayout {
        public string ServerAddress { get; set; } = II.Server.Mirror.DefaultServer;
        public List<RoomDefinition> Rooms { get; set; } = new ();

        public class RoomDefinition {
            public string? Name { get; set; }
            public string? ScenarioFile { get; set; }
            public string? Accession { get; set; }
            public string? PasswordAccess { get; set; }
            public string? PasswordEdit { get; set; }
        }

        public static async Task Save (LabLayout layout, string filepath) {
            string json = JsonSerializer.Serialize (layout, new JsonSerializerOptions () { WriteIndented = true });
            await File.WriteAllTextAsync (filepath, json);
        }

        public static async Task<LabLayout?> Load (string filepath) {
            if (!File.Exists (filepath))
                return null;

            try {
                string json = await File.ReadAllTextAsync (filepath);
                return JsonSerializer.Deserialize<LabLayout> (json);
            } catch {
                return null;
            }
        }
    }
}
