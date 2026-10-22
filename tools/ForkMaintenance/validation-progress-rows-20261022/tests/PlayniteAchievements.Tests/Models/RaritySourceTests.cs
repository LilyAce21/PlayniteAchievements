using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Tests.Models
{
    /// <summary>
    /// Covers the per-game rarity source: what a refresh records, how a write merges with the
    /// stored value, and what custom achievements contribute.
    /// </summary>
    [TestClass]
    public class RaritySourceTests
    {
        [TestMethod]
        public void ResolveRefreshed_KeepsDeclaredTierForAnAllCommonSet()
        {
            // A Minecraft tab of task advancements: every tier Common, no percent, but real data.
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "a", Rarity = RarityTier.Common },
                new AchievementDetail { ApiName = "b", Rarity = RarityTier.Common }
            };

            Assert.AreEqual(RaritySource.Tier, RaritySourceResolver.ResolveRefreshed(RaritySource.Tier, achievements));
        }

        [TestMethod]
        public void ResolveRefreshed_IsNoneWhenTheProviderDeclaredNothingAndSentNoPercent()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "a" },
                new AchievementDetail { ApiName = "b" }
            };

            Assert.AreEqual(RaritySource.None, RaritySourceResolver.ResolveRefreshed(null, achievements));
            Assert.AreEqual(RaritySource.None, RaritySourceResolver.ResolveRefreshed(null, null));
        }

        [TestMethod]
        public void ResolveRefreshed_AnyPercentMakesItPercent()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "a" },
                new AchievementDetail { ApiName = "b", GlobalPercentUnlocked = 42.0 }
            };

            Assert.AreEqual(RaritySource.Percent, RaritySourceResolver.ResolveRefreshed(null, achievements));
            Assert.AreEqual(RaritySource.Percent, RaritySourceResolver.ResolveRefreshed(RaritySource.Tier, achievements));
        }

        [TestMethod]
        public void ResolveRaritySourceToWrite_KeepsStoredWhenIncomingIsMissingOrNone()
        {
            // A re-save of loaded data or a legacy import carries no source.
            Assert.AreEqual(RaritySource.Percent, SqlNadoCacheBehavior.ResolveRaritySourceToWrite(null, "Percent"));

            // A refresh whose rarity source was unavailable says None; the stored achievements keep
            // their rarity, so the game keeps saying it has some.
            Assert.AreEqual(RaritySource.Percent, SqlNadoCacheBehavior.ResolveRaritySourceToWrite(RaritySource.None, "Percent"));
            Assert.AreEqual(RaritySource.Tier, SqlNadoCacheBehavior.ResolveRaritySourceToWrite(RaritySource.None, "Tier"));
        }

        [TestMethod]
        public void ResolveRaritySourceToWrite_TakesIncomingOtherwise()
        {
            Assert.AreEqual(RaritySource.Tier, SqlNadoCacheBehavior.ResolveRaritySourceToWrite(RaritySource.Tier, "Percent"));
            Assert.AreEqual(RaritySource.None, SqlNadoCacheBehavior.ResolveRaritySourceToWrite(RaritySource.None, null));
            Assert.AreEqual(RaritySource.None, SqlNadoCacheBehavior.ResolveRaritySourceToWrite(RaritySource.None, "None"));
            Assert.IsNull(SqlNadoCacheBehavior.ResolveRaritySourceToWrite(null, null));
        }

        [TestMethod]
        public void ParseRaritySource_RejectsUnknownValues()
        {
            Assert.AreEqual(RaritySource.Tier, SqlNadoCacheBehavior.ParseRaritySource(" tier "));
            Assert.IsNull(SqlNadoCacheBehavior.ParseRaritySource(null));
            Assert.IsNull(SqlNadoCacheBehavior.ParseRaritySource("Guessed"));
            Assert.IsNull(SqlNadoCacheBehavior.ParseRaritySource("7"));
        }

        [TestMethod]
        public void CustomDefinitions_ResolveFromPercentThenTier()
        {
            Assert.IsNull(CustomAchievementProjectionService.ResolveRaritySource(new[]
            {
                new CustomAchievementDefinition { DisplayName = "plain" }
            }));

            Assert.AreEqual(RaritySource.Tier, CustomAchievementProjectionService.ResolveRaritySource(new[]
            {
                new CustomAchievementDefinition { DisplayName = "plain" },
                new CustomAchievementDefinition { DisplayName = "tiered", Rarity = "Common" }
            }));

            Assert.AreEqual(RaritySource.Percent, CustomAchievementProjectionService.ResolveRaritySource(new[]
            {
                new CustomAchievementDefinition { DisplayName = "tiered", Rarity = "Rare" },
                new CustomAchievementDefinition { DisplayName = "measured", GlobalPercentUnlocked = 3.5 }
            }));
        }

        [TestMethod]
        public void CombineRaritySource_PrefersTheStrongerSource()
        {
            Assert.AreEqual(RaritySource.Tier, CustomAchievementProjectionService.CombineRaritySource(RaritySource.None, RaritySource.Tier));
            Assert.AreEqual(RaritySource.Percent, CustomAchievementProjectionService.CombineRaritySource(RaritySource.Percent, RaritySource.Tier));
            Assert.AreEqual(RaritySource.None, CustomAchievementProjectionService.CombineRaritySource(RaritySource.None, null));
            Assert.AreEqual(RaritySource.Tier, CustomAchievementProjectionService.CombineRaritySource(null, RaritySource.Tier));
            Assert.IsNull(CustomAchievementProjectionService.CombineRaritySource(null, null));
        }
    }
}
