using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class LocalCustomSchemaCacheTests
    {
        [TestMethod]
        public void CustomSchemaDoesNotReplaceSharedSteamRowsAfterClear()
        {
            var native = new List<SchemaAchievement>();
            for (var i = 0; i < 100; i++) native.Add(new SchemaAchievement { Name = "a" + i });
            var cached = new SchemaAndPercentages
            {
                Achievements = native,
                GlobalPercentages = new Dictionary<string, double> { ["a0"] = 25 },
                SchemaHash = 123
            };
            var overridden = cached.WithAchievements(new List<SchemaAchievement>
            {
                new SchemaAchievement { Name = "FIND_ALL_KITTIES" }
            });

            Assert.AreNotSame(cached, overridden);
            Assert.AreEqual(1, overridden.Achievements.Count);
            // A refresh after Clear, or another game's refresh, receives this cache object.
            Assert.AreSame(native, cached.Achievements);
            Assert.AreEqual(100, cached.Achievements.Count);
            Assert.AreEqual("a99", cached.Achievements[99].Name);
            Assert.AreSame(cached.GlobalPercentages, overridden.GlobalPercentages);
            Assert.AreSame(cached.Groups, overridden.Groups);
            Assert.AreEqual(cached.SchemaHash, overridden.SchemaHash);
        }

        [TestMethod]
        public void MultipleGameOverridesRemainIndependent()
        {
            var cached = new SchemaAndPercentages { Achievements = new List<SchemaAchievement>() };
            var first = cached.WithAchievements(new List<SchemaAchievement> { new SchemaAchievement { Name = "first" } });
            var second = cached.WithAchievements(new List<SchemaAchievement> { new SchemaAchievement { Name = "second" } });
            Assert.AreEqual("first", first.Achievements[0].Name);
            Assert.AreEqual("second", second.Achievements[0].Name);
            Assert.AreEqual(0, cached.Achievements.Count);
        }
    }
}
