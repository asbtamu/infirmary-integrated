using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using IICMD;

namespace Tests {

    [TestClass]
    public class SessionLogTests {

        [TestMethod]
        public void CsvField_QuotesAndDefusesFormulas () {
            Assert.AreEqual ("plain", SessionLog.CsvField ("plain"));
            Assert.AreEqual ("\"a, b\"", SessionLog.CsvField ("a, b"));
            Assert.AreEqual ("\"say \"\"hi\"\"\"", SessionLog.CsvField ("say \"hi\""));
            Assert.AreEqual ("'=HYPERLINK(1)", SessionLog.CsvField ("=HYPERLINK(1)"));
            Assert.AreEqual ("-5", SessionLog.CsvField ("-5"));
        }

        [TestMethod]
        public void LogWritesCsvAndReport () {
            string dir = Path.Combine (Path.GetTempPath (), "ii-sessionlog-" + Guid.NewGuid ().ToString ("N"));

            try {
                SessionLog log = new ("Room 1", "Chest <pain>", dir);
                SessionLog.Vitals v = new (80, 120, 80, 98, 16, 38, 37.0);

                log.Add ("session_start", "Baseline", "Loaded x.ii", v);
                log.Add ("vitals", "Baseline", null, v);
                log.Add ("step", "VF arrest", "instructor: branch \"Gave wrong drug\"", v with { HR = 180 });
                log.Add ("vitals_override", "VF arrest", "HR 180→60", v with { HR = 60 });

                string [] lines = File.ReadAllLines (log.FilePath);
                Assert.AreEqual (5, lines.Length);                          // Header + 4 entries
                StringAssert.StartsWith (lines [0], "wall_time,elapsed_s,event");
                StringAssert.Contains (lines [3], "\"instructor: branch \"\"Gave wrong drug\"\"\"");

                string html = File.ReadAllText (log.WriteReport (new [] { "Baseline", "VF arrest", "ROSC" }));
                StringAssert.Contains (html, "Chest &lt;pain&gt;");          // Encoded, not injected
                StringAssert.Contains (html, "<li>ROSC</li>");               // Authored but not reached
                Assert.IsFalse (html.Contains ("<li>Baseline</li>"));
                StringAssert.Contains (html, "Not enough vitals samples");    // Run lasted < 0.1 s: no chart, no divide-by-zero
            } finally {
                if (Directory.Exists (dir))
                    Directory.Delete (dir, true);
            }
        }
    }
}
