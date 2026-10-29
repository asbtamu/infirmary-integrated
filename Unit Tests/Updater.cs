using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using II;

namespace Tests {

    [TestClass]
    public class UpdaterTests {
        private string _Dir = "", _Share = "", _Install = "";

        [TestInitialize]
        public void Setup () {
            _Dir = Path.Combine (Path.GetTempPath (), "ii-updater-" + Guid.NewGuid ().ToString ("N"));
            _Share = Path.Combine (_Dir, "share");
            _Install = Path.Combine (_Dir, "app");
            Directory.CreateDirectory (_Share);
            Directory.CreateDirectory (_Install);

            string src = Path.Combine (_Dir, "build");
            Directory.CreateDirectory (src);
            System.IO.File.WriteAllText (Path.Combine (src, "app.exe"), "new build");
            ZipFile.CreateFromDirectory (src, Path.Combine (_Share, "App_2.0.0.zip"));
        }

        [TestCleanup]
        public void Cleanup () => Directory.Delete (_Dir, true);

        private void WriteManifest (string version, string? sha = null) {
            sha ??= Convert.ToHexString (SHA256.HashData (System.IO.File.ReadAllBytes (Path.Combine (_Share, "App_2.0.0.zip"))));
            System.IO.File.WriteAllText (Path.Combine (_Share, "manifest.json"),
                $"{{ \"sim\": {{ \"version\": \"{version}\", \"package\": \"App_2.0.0.zip\", \"sha256\": \"{sha}\" }} }}");
        }

        private string? Prepare (string current = "1.0.0")
            => Updater.Prepare ("sim", current, _Share, _Install, "app.exe", new [] { "--accession", "A%B" });

        [TestMethod]
        public void NewerBuild_IsStagedBesideInstall () {
            WriteManifest ("2.0.0");
            string? script = Prepare ();

            Assert.IsNotNull (script);
            Assert.AreEqual ("new build", System.IO.File.ReadAllText (Path.Combine (_Install + ".new", "app.exe")));

            string cmd = System.IO.File.ReadAllText (script);
            StringAssert.Contains (cmd, $"set \"INSTALL={_Install}\"");
            StringAssert.Contains (cmd, "\"--accession\" \"A%%B\"");     // % doubled for cmd
        }

        [TestMethod]
        public void SameOrOlder_DoesNothing () {
            WriteManifest ("1.0.0");
            Assert.IsNull (Prepare ());
            Assert.IsNull (Prepare ("3.0.0"));
            Assert.IsFalse (Directory.Exists (_Install + ".new"));
        }

        [TestMethod]
        public void HashMismatch_InstallsNothing () {
            WriteManifest ("2.0.0", new string ('0', 64));
            Assert.IsNull (Prepare ());
            Assert.IsFalse (Directory.Exists (_Install + ".new"));
        }

        [TestMethod]
        public void NoUpdatesFile_Pins () {
            WriteManifest ("2.0.0");
            System.IO.File.WriteAllText (Path.Combine (_Install, "no-updates"), "");
            Assert.IsNull (Prepare ());
        }

        [TestMethod]
        public void MissingShare_IsNormalLaunch ()
            => Assert.IsNull (Updater.Prepare ("sim", "1.0.0", Path.Combine (_Dir, "nope"), _Install, "app.exe", Array.Empty<string> ()));

        [TestMethod]
        public void Manifest_RejectsPathsOutOfShare () {
            Assert.IsNull (Updater.ReadManifest ("{\"sim\":{\"version\":\"9.0.0\",\"package\":\"..\\\\x.zip\",\"sha256\":\"00\"}}", "sim"));
            Assert.IsNull (Updater.ReadManifest ("{\"command\":{}}", "sim"));
        }
    }
}
