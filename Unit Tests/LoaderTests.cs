using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using II;

namespace Tests {

    [TestClass]
    public class LoaderTests {

        [TestMethod]
        public async Task Physiology_BlankLineDoesNotEndLoad () {
            Physiology p = new (new II.Timer ());
            await p.Load ("HR:91\n\nRR:19\n");

            Assert.AreEqual (91, p.VS_Settings.HR);
            Assert.AreEqual (19, p.VS_Settings.RR);         // Was dropped: the loop stopped at the blank line
        }

        [TestMethod]
        public async Task Scenario_BlankLineDoesNotEndLoad () {
            Scenario s = new (new II.Timer (), false);
            await s.Load ("Name:Before\n\nAuthor:After\n");

            Assert.AreEqual ("Before", s.Name);
            Assert.AreEqual ("After", s.Author);
        }

        [TestMethod]
        public void WindowSettings_ShortLineFallsBackToDefaults () {
            II.Settings.Window w = II.Settings.Window.Load ("10,20");

            Assert.IsNull (w.X);
            Assert.IsNull (w.WindowState);
        }
    }
}
