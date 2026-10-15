using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Services.Psn;

namespace PlayniteAchievements.Services.Tests.Psn
{
    [TestClass]
    public class PsnSharedIdStoreTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            _dir = Path.Combine(Path.GetTempPath(), "psn-shared-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch (Exception)
            {
            }
        }

        // These match the Achievements app's tests case for case: the two must agree on keys.
        [TestMethod]
        public void NormalizesNamesTheSameWayAsTheAchievementsApp()
        {
            Assert.AreEqual("clairobscurexpedition33", PsnSharedIdStore.NormalizeName("Clair Obscur: Expedition 33"));
            Assert.AreEqual("hollowknight", PsnSharedIdStore.NormalizeName("Hollow Knight\u2122"));
            Assert.AreEqual("pokemon", PsnSharedIdStore.NormalizeName("Pok\u00e9mon"));
            Assert.AreEqual(string.Empty, PsnSharedIdStore.NormalizeName("  "));
            Assert.AreEqual(string.Empty, PsnSharedIdStore.NormalizeName(null));
        }

        [TestMethod]
        public void BuildsKeysFromSteamIdAndName()
        {
            CollectionAssert.AreEqual(new[] { "steam:1678", "name:celeste" }, PsnSharedIdStore.KeysFor(1678, "Celeste").ToArray());
            CollectionAssert.AreEqual(new[] { "name:celeste" }, PsnSharedIdStore.KeysFor(0, "Celeste").ToArray());
            Assert.AreEqual(0, PsnSharedIdStore.KeysFor(0, null).Count);
        }

        [TestMethod]
        public void RemembersAndLooksUpBySteamIdOrName()
        {
            var store = new PsnSharedIdStore(_dir, () => 1000);
            Assert.AreEqual(0, store.Lookup(1678, "Celeste").Count);

            Assert.IsTrue(store.Remember(1678, "Celeste", new[] { "npwr14979_00", "bogus" }));
            CollectionAssert.AreEqual(new[] { "NPWR14979_00" }, store.Lookup(1678, null).ToArray());
            CollectionAssert.AreEqual(new[] { "NPWR14979_00" }, store.Lookup(0, "CELESTE").ToArray());
        }

        [TestMethod]
        public void SteamIdWinsOverNameWhenBothExist()
        {
            var store = new PsnSharedIdStore(_dir);
            store.Remember(1678, "Celeste", new[] { "NPWR14979_00" });
            store.Remember(0, "Celeste", new[] { "NPWR00001_00" });
            CollectionAssert.AreEqual(new[] { "NPWR14979_00" }, store.Lookup(1678, "Celeste").ToArray());
            CollectionAssert.AreEqual(new[] { "NPWR00001_00" }, store.Lookup(0, "Celeste").ToArray());
        }

        [TestMethod]
        public void SavesNothingWithoutAValidIdOrAKey()
        {
            var store = new PsnSharedIdStore(_dir);
            Assert.IsFalse(store.Remember(0, "X", new[] { "bogus" }));
            Assert.IsFalse(store.Remember(0, null, new[] { "NPWR00002_00" }));
            Assert.IsFalse(File.Exists(store.FilePath));
        }

        [TestMethod]
        public void WritesMergeSoAnotherAppsGamesSurvive()
        {
            var first = new PsnSharedIdStore(_dir);
            var second = new PsnSharedIdStore(_dir);
            first.Remember(999, "Other Game", new[] { "NPWR00003_00" });
            second.Remember(5, "Third", new[] { "NPWR00004_00" });
            CollectionAssert.AreEqual(new[] { "NPWR00003_00" }, second.Lookup(999, null).ToArray());
            CollectionAssert.AreEqual(new[] { "NPWR00004_00" }, first.Lookup(5, null).ToArray());
        }

        [TestMethod]
        public void ReadsTheFileTheAchievementsAppWrites()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(
                Path.Combine(_dir, PsnSharedIdStore.FileName),
                "{\n  \"version\": 1,\n  \"games\": {\n    \"steam:1678\": { \"name\": \"Celeste\", \"npwrIds\": [\"NPWR14979_00\"], \"updatedAt\": 1760400000000 }\n  }\n}");
            var store = new PsnSharedIdStore(_dir);
            CollectionAssert.AreEqual(new[] { "NPWR14979_00" }, store.Lookup(1678, null).ToArray());
        }

        [TestMethod]
        public void ACorruptFileReadsAsEmptyAndTheNextWriteReplacesIt()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, PsnSharedIdStore.FileName);
            File.WriteAllText(path, "{not json");
            var store = new PsnSharedIdStore(_dir);
            Assert.AreEqual(0, store.Lookup(5, "Third").Count);
            Assert.IsTrue(store.Remember(5, "Third", new[] { "NPWR00004_00" }));
            CollectionAssert.AreEqual(new[] { "NPWR00004_00" }, store.Lookup(5, null).ToArray());

            var data = JObject.Parse(File.ReadAllText(path));
            Assert.AreEqual(1, (int)data["version"]);
            Assert.IsNotNull(data["games"]["steam:5"]["updatedAt"]);
        }

        [TestMethod]
        public void SeesChangesAnotherProcessMakesToTheFile()
        {
            var reader = new PsnSharedIdStore(_dir);
            Assert.AreEqual(0, reader.Lookup(5, null).Count);
            new PsnSharedIdStore(_dir).Remember(5, "Third", new[] { "NPWR00004_00" });
            CollectionAssert.AreEqual(new[] { "NPWR00004_00" }, reader.Lookup(5, null).ToArray());
        }
    }
}
