using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Recording;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class NotificationSnapshotStoreTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Guid GameA = Guid.NewGuid();
        private static readonly Guid GameB = Guid.NewGuid();

        private static NotificationSnapshot Snap(Guid game, string name, string api, DateTime at, byte marker = 1)
        {
            return new NotificationSnapshot
            {
                GameId = game,
                NameKey = NotificationSnapshotStore.NormalizeKey(name),
                ApiKey = NotificationSnapshotStore.NormalizeKey(api),
                CapturedUtc = at,
                Png = new[] { marker }
            };
        }

        [TestMethod]
        public void NormalizeKey_IgnoresCaseSpacingAndPunctuation()
        {
            Assert.AreEqual("dontpanic", NotificationSnapshotStore.NormalizeKey("  Don't  PANIC! "));
            Assert.AreEqual(string.Empty, NotificationSnapshotStore.NormalizeKey("   "));
            Assert.AreEqual(string.Empty, NotificationSnapshotStore.NormalizeKey(null));
        }

        [TestMethod]
        public void MatchesByAchievementName_EvenWhenTheTimesAreFarApart()
        {
            var now = T0;
            var store = new NotificationSnapshotStore(utcNow: () => now);
            store.Add(Snap(GameA, "First Blood", "ACH_1", T0.AddSeconds(-90), 1));
            store.Add(Snap(GameA, "Collector", "ACH_2", T0.AddSeconds(-10), 2));

            var taken = store.TakeBest(GameA, "first blood", null, T0);

            Assert.IsNotNull(taken);
            Assert.AreEqual(1, taken.Png[0]);
        }

        [TestMethod]
        public void MatchesByInternalName_WhenTheDisplayNamesDiffer()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(Snap(GameA, "Erste Blut", "ACH_1", T0.AddSeconds(-5), 7));

            var taken = store.TakeBest(GameA, "First Blood", "ach_1", T0);

            Assert.IsNotNull(taken);
            Assert.AreEqual(7, taken.Png[0]);
        }

        [TestMethod]
        public void SnapshotIsUsedOnlyOnce()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(Snap(GameA, "First Blood", "", T0));

            Assert.IsNotNull(store.TakeBest(GameA, "First Blood", null, T0));
            Assert.IsNull(store.TakeBest(GameA, "First Blood", null, T0));
            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public void NeverHandsOutAnotherGamesSnapshot()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(Snap(GameB, "First Blood", "", T0));

            Assert.IsNull(store.TakeBest(GameA, "First Blood", null, T0));
            Assert.AreEqual(1, store.Count);
        }

        [TestMethod]
        public void FallsBackToTime_OnlyWhenExactlyOneSnapshotIsClose()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(Snap(GameA, "Something Else", "", T0.AddSeconds(-4), 9));

            var taken = store.TakeBest(GameA, "Unrelated Name", null, T0);

            Assert.IsNotNull(taken);
            Assert.AreEqual(9, taken.Png[0]);
        }

        [TestMethod]
        public void TimeFallback_RefusesToGuessBetweenTwoCloseSnapshots()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(Snap(GameA, "One", "", T0.AddSeconds(-3)));
            store.Add(Snap(GameA, "Two", "", T0.AddSeconds(-5)));

            Assert.IsNull(store.TakeBest(GameA, "Unrelated Name", null, T0));
            Assert.AreEqual(2, store.Count);
        }

        [TestMethod]
        public void TimeFallback_IgnoresSnapshotsOutsideTheTolerance()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(Snap(GameA, "Something Else", "", T0.AddSeconds(-60)));

            Assert.IsNull(store.TakeBest(GameA, "Unrelated Name", null, T0));
        }

        [TestMethod]
        public void ExpiredSnapshotsAreDroppedAndNeverReturned()
        {
            var now = T0;
            var store = new NotificationSnapshotStore(lifetime: TimeSpan.FromMinutes(4), utcNow: () => now);
            store.Add(Snap(GameA, "First Blood", "", T0));

            now = T0.AddMinutes(5);

            Assert.IsNull(store.TakeBest(GameA, "First Blood", null, now));
            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public void TheStoreIsCapped_OldestFirst()
        {
            var store = new NotificationSnapshotStore(maxEntries: 3, utcNow: () => T0);
            for (var i = 0; i < 5; i++)
            {
                store.Add(Snap(GameA, "Ach " + i, "", T0.AddSeconds(-i), (byte)i));
            }

            Assert.AreEqual(3, store.Count);
            Assert.IsNull(store.TakeBest(GameA, "Ach 0", null, T0));
            Assert.IsNotNull(store.TakeBest(GameA, "Ach 4", null, T0));
        }

        [TestMethod]
        public void EmptySnapshotsAreIgnored()
        {
            var store = new NotificationSnapshotStore(utcNow: () => T0);
            store.Add(new NotificationSnapshot { GameId = GameA, NameKey = "x", Png = new byte[0] });
            store.Add(null);

            Assert.AreEqual(0, store.Count);
        }
    }
}
