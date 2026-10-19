using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using II;

namespace Tests {

    /* One test per audit fix whose failure is silent in class: wrong timing, lost values, garbled numbers */
    [TestClass]
    public class Regression {

        [TestMethod]
        public void IsNewerVersion_UnequalLengths () {
            Assert.IsTrue (II.Utility.IsNewerVersion ("2.8", "2.8.1"));      // Used to throw IndexOutOfRange
            Assert.IsFalse (II.Utility.IsNewerVersion ("2.8.1", "2.8"));
            Assert.IsFalse (II.Utility.IsNewerVersion ("2.8", "2.8.0"));
        }

        [TestMethod]
        public void DateTime_RoundTrip_AnyCulture () {
            CultureInfo saved = CultureInfo.CurrentCulture;

            try {
                DateTime when = new (2026, 10, 19, 14, 5, 9);

                foreach (string culture in new [] { "en-US", "de-DE", "th-TH", "ar-SA" }) {
                    CultureInfo.CurrentCulture = new CultureInfo (culture);

                    string? text = II.Utility.DateTime_ToString (when);
                    Assert.AreEqual ("2026/10/19 14:05:09", text, culture);
                    Assert.AreEqual (when, II.Utility.DateTime_FromString (text), culture);
                }
            } finally {
                CultureInfo.CurrentCulture = saved;
            }
        }

        [TestMethod]
        public void DateTime_FromString_BadInput_DoesNotThrow () {
            Assert.AreEqual (DateTime.MinValue, II.Utility.DateTime_FromString (""));
            Assert.AreEqual (DateTime.MinValue, II.Utility.DateTime_FromString (null));
            Assert.AreEqual (DateTime.MinValue, II.Utility.DateTime_FromString ("<b>Warning</b>"));
            Assert.AreEqual (new DateTime (2026, 1, 2, 3, 4, 5), II.Utility.DateTime_FromString ("2026.01.02 03:04:05"));
        }

        [TestMethod]
        public void RandomString_IsAlphanumeric () {
            string s = II.Utility.RandomString (64);
            Assert.AreEqual (64, s.Length);
            StringAssert.Matches (s, new System.Text.RegularExpressions.Regex ("^[A-Z0-9]+$"));
        }

        /* A step timer paused 1/4 of the way in must, after a long pause, still wait out the rest */
        [TestMethod]
        public async Task Timer_PauseDoesNotCountTowardInterval () {
            II.Timer t = new ();
            int ticks = 0;
            t.Tick += (s, e) => ticks++;

            await t.ResetStart (400);
            Thread.Sleep (100);
            await t.Pause ();
            Thread.Sleep (600);                 // Paused longer than the whole interval
            await t.Unpause ();

            t.Process ();
            Assert.AreEqual (0, ticks, "Fired immediately on resume: pause time was counted as elapsed");

            Thread.Sleep (450);                 // ~300 ms of running time remained
            t.Process ();
            Assert.AreEqual (1, ticks, "Did not fire once the remaining running time passed");
        }

        [TestMethod]
        public async Task Timer_ResetWhilePaused_StaysPaused () {
            II.Timer t = new ();
            int ticks = 0;
            t.Tick += (s, e) => ticks++;

            await t.ResetStart (50);
            await t.Pause ();
            await t.Reset ();
            Thread.Sleep (120);

            t.Process ();
            Assert.AreEqual (0, ticks);
        }

        /* One unparseable value used to abort the whole block, silently loading default vitals */
        [TestMethod]
        public async Task Physiology_Load_SkipsOnlyTheBadKey () {
            Physiology p = new (null);

            await p.Load ("Updated:\nHR:not-a-number\nNSBP:142\nRR:22\n");

            Assert.AreEqual (142, p.VS_Settings.NSBP);
            Assert.AreEqual (22, p.VS_Settings.RR);
        }

        [TestMethod]
        public async Task Physiology_SaveLoad_KeepsDecimals () {
            CultureInfo saved = CultureInfo.CurrentCulture;

            try {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;      // What each app's Main now sets
                Physiology a = new (null);
                a.VS_Settings.T = 37.5;

                Physiology b = new (null);
                await b.Load (a.Save ());

                Assert.AreEqual (37.5, b.VS_Settings.T, 0.001);
            } finally {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
