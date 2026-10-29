using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using II;
using II.Server;

namespace Tests {

    /* Drives II Server/mirror_relay.py through II's real client code (Mirror.PostStep / GetStep), so the
     * relay is proven against what II actually sends, '+'-as-space quirk included. Needs Python on PATH. */
    [TestClass]
    public class MirrorRelayTests {
        private static Process? _Relay;
        private static string _Url = "";

        [ClassInitialize]
        public static async Task Start (TestContext _) {
            int port;
            using (TcpListener l = new (IPAddress.Loopback, 0)) { l.Start (); port = ((IPEndPoint)l.LocalEndpoint).Port; }

            string script = Path.GetFullPath (@"..\..\..\..\II Server\mirror_relay.py");
            try {
                _Relay = Process.Start (new ProcessStartInfo ("python", $"-I \"{script}\" --bind 127.0.0.1 --port {port}") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                });
            } catch (System.ComponentModel.Win32Exception) {
                return;                                     // No Python: tests report Inconclusive
            }

            _Url = $"http://127.0.0.1:{port}/";
            for (int i = 0; i < 50; i++) {                  // Wait for the listener
                try { using TcpClient c = new ("127.0.0.1", port); return; } catch { await Task.Delay (100); }
            }
        }

        [ClassCleanup]
        public static void Stop () => _Relay?.Kill ();

        private static Mirror Host (string editPw = "EDIT1") => new (new II.Timer ()) {
            Status = Mirror.Statuses.HOST, ServerAddress = _Url, Accession = $"T{Guid.NewGuid ():N}".Substring (0, 12).ToUpperInvariant (),
            PasswordAccess = "ACCESS1", PasswordEdit = editPw,
        };

        private static async Task<Scenario.Step> StepWithHR (int hr) {
            Scenario.Step step = new (new II.Timer ()) { Physiology = new Physiology (new II.Timer ()) };
            await step.Physiology.Load ($"HR:{hr}\nNSBP:131\nT:37.4\n");
            step.Physiology.Updated = DateTime.UtcNow;
            return step;
        }

        [TestMethod]
        public async Task PostThenGet_RoundTripsThroughRelay () {
            if (_Relay is null) Assert.Inconclusive ("Python not available");

            Mirror host = Host ();
            Server server = new (new II.Timer ());
            Assert.AreEqual (Server.ServerResponse.Success, await host.PostStep (await StepWithHR (123), server));
            Assert.AreEqual (Server.ServerResponse.Success, await host.PostStep (await StepWithHR (124), server));   // ENTRY_UPDATED

            Assert.AreEqual ((true, (int?)null), await Server.Get_MirrorReadAge (host));    // Nobody has read it yet

            Mirror client = new (new II.Timer ()) {
                Status = Mirror.Statuses.CLIENT, ServerAddress = _Url, Accession = host.Accession, PasswordAccess = "ACCESS1",
            };
            Scenario.Step received = new (new II.Timer ());
            Assert.AreEqual (Server.ServerResponse.Success, await client.GetStep (received, server));
            Assert.AreEqual (124, received.Physiology?.VS_Settings.HR);
            Assert.AreEqual (131, received.Physiology?.VS_Settings.NSBP);

            var (supported, age) = await Server.Get_MirrorReadAge (host);
            Assert.IsTrue (supported);
            Assert.IsTrue (age is >= 0 and <= 2, $"age {age}");
        }

        /* http.server rejects a request line over 64 KB (414); a full step must stay far below it */
        [TestMethod]
        public void FullStep_FitsRelayRequestLine () {
            Scenario.Step step = new (new II.Timer ()) { Physiology = new Physiology (new II.Timer ()) };
            int length = Encryption.EncryptAES (step.Save ()).Length + 300;      // + accession, hashes, date, path
            Console.WriteLine ($"post request line ~{length} bytes");
            Assert.IsTrue (length < 16 * 1024, $"step post is {length} bytes; the relay's limit is 65536");
        }

        [TestMethod]
        public async Task WrongPasswords_AreRefused () {
            if (_Relay is null) Assert.Inconclusive ("Python not available");

            Mirror host = Host ();
            Server server = new (new II.Timer ());
            await host.PostStep (await StepWithHR (80), server);

            Mirror intruder = Host ("WRONG");
            intruder.Accession = host.Accession;
            Assert.AreEqual (Server.ServerResponse.ErrorCredentials, await intruder.PostStep (await StepWithHR (30), server));
            Assert.AreEqual ((true, (int?)null), await Server.Get_MirrorReadAge (intruder));

            Mirror client = new (new II.Timer ()) {
                Status = Mirror.Statuses.CLIENT, ServerAddress = _Url, Accession = host.Accession, PasswordAccess = "NOPE",
            };
            Assert.AreEqual (Server.ServerResponse.ErrorCredentials, await client.GetStep (new Scenario.Step (new II.Timer ()), server));
        }
    }
}
