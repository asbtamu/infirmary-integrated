using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using IICMD;

namespace Tests {

    [TestClass]
    public class StationMapTests {

        [TestMethod]
        public void ExampleFile_ParsesPlanAndStations () {
            string path = @"..\..\..\..\II Command\stations.example.json";
            StationMap map = StationMap.Parse (File.ReadAllText (path), path);

            Assert.AreEqual (7, map.Cols);
            Assert.AreEqual (4, map.Stations.Count);
            Assert.AreEqual ("HOUSIM1", map.Stations ["sim1"].Accession);       // Lookup ignores case, as cell text varies
            Assert.IsTrue (map.Cells.Exists (c => c.Text == "SIM4"));            // Every station appears on the plan
            Assert.IsTrue (map.Cells.TrueForAll (c => c.Row + c.RowSpan <= map.Rows && c.Col + c.ColSpan <= map.Cols));
        }
    }
}
