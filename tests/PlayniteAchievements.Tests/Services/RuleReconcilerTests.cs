using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Rules;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class RuleReconcilerTests
    {
        private const string Field = RuleFields.CompletionStatus;
        private const string Completed = "completed";
        private const string Playing = "playing";
        private const string Abandoned = "abandoned";

        private static RuleClaim Claim(string written, string prior) =>
            new RuleClaim { Field = Field, Written = written, Prior = prior };

        [TestMethod]
        public void Decide_RuleApplies_NoClaim_WritesAndClaimsWithPrior()
        {
            var decision = RuleReconciler.Decide(Field, Playing, Completed, null, pinned: false);

            Assert.IsTrue(decision.Write);
            Assert.AreEqual(Completed, decision.Value);
            Assert.AreEqual(Completed, decision.Claim.Written);
            Assert.AreEqual(Playing, decision.Claim.Prior);
            Assert.IsTrue(decision.StateChanged);
        }

        [TestMethod]
        public void Decide_RuleApplies_ValueAlreadyThere_TakesNoOwnership()
        {
            var decision = RuleReconciler.Decide(Field, Completed, Completed, null, pinned: false);

            Assert.IsTrue(decision.IsNoOp);
            Assert.IsNull(decision.Claim);
        }

        [TestMethod]
        public void Decide_RuleApplies_OwnUntouchedValue_KeepsClaim()
        {
            var claim = Claim(Completed, Playing);
            var decision = RuleReconciler.Decide(Field, Completed, Completed, claim, pinned: false);

            Assert.IsTrue(decision.IsNoOp);
            Assert.AreSame(claim, decision.Claim);
        }

        [TestMethod]
        public void Decide_RuleTargetMoved_FollowsAndKeepsOriginalPrior()
        {
            var decision = RuleReconciler.Decide(Field, Completed, Abandoned, Claim(Completed, Playing), pinned: false);

            Assert.IsTrue(decision.Write);
            Assert.AreEqual(Abandoned, decision.Value);
            Assert.AreEqual(Abandoned, decision.Claim.Written);
            Assert.AreEqual(Playing, decision.Claim.Prior);
        }

        [TestMethod]
        public void Decide_ValueChangedOutsideRule_DropsClaimAndPins()
        {
            var decision = RuleReconciler.Decide(Field, Abandoned, Completed, Claim(Completed, Playing), pinned: false);

            Assert.IsFalse(decision.Write);
            Assert.IsNull(decision.Claim);
            Assert.IsTrue(decision.Pinned);
            Assert.IsTrue(decision.StateChanged);
        }

        [TestMethod]
        public void Decide_Pinned_RuleApplies_LeavesValueAlone()
        {
            var decision = RuleReconciler.Decide(Field, Playing, Completed, null, pinned: true);

            Assert.IsTrue(decision.IsNoOp);
            Assert.IsTrue(decision.Pinned);
        }

        [TestMethod]
        public void Decide_RuleReleases_OwnUntouchedValue_RestoresPrior()
        {
            var decision = RuleReconciler.Decide(Field, Completed, null, Claim(Completed, Playing), pinned: false);

            Assert.IsTrue(decision.Write);
            Assert.AreEqual(Playing, decision.Value);
            Assert.IsNull(decision.Claim);
            Assert.IsTrue(decision.StateChanged);
        }

        [TestMethod]
        public void Decide_RuleReleases_PriorWasUnset_RestoresUnset()
        {
            var decision = RuleReconciler.Decide(
                RuleFields.ExcludedFromRefreshes,
                RuleFields.True,
                null,
                new RuleClaim { Field = RuleFields.ExcludedFromRefreshes, Written = RuleFields.True },
                pinned: false);

            Assert.IsTrue(decision.Write);
            Assert.IsNull(decision.Value);
            Assert.IsNull(decision.Claim);
        }

        [TestMethod]
        public void Decide_RuleReleases_ValueChangedSince_LeavesValue()
        {
            var decision = RuleReconciler.Decide(Field, Abandoned, null, Claim(Completed, Playing), pinned: false);

            Assert.IsFalse(decision.Write);
            Assert.IsNull(decision.Claim);
            Assert.IsTrue(decision.StateChanged);
        }

        [TestMethod]
        public void Decide_RuleReleases_ClearsPin()
        {
            var decision = RuleReconciler.Decide(Field, Playing, null, null, pinned: true);

            Assert.IsFalse(decision.Write);
            Assert.IsFalse(decision.Pinned);
            Assert.IsTrue(decision.StateChanged);
        }

        [TestMethod]
        public void Decide_NoRuleNoState_IsNoOp()
        {
            Assert.IsTrue(RuleReconciler.Decide(Field, Playing, null, null, pinned: false).IsNoOp);
        }

        [TestMethod]
        public void Decide_ComparesStatusIdsIgnoringCase()
        {
            var id = Guid.NewGuid().ToString("D");
            var decision = RuleReconciler.Decide(Field, id.ToUpperInvariant(), id, null, pinned: false);

            Assert.IsTrue(decision.IsNoOp);
        }

        [TestMethod]
        public void RecordUserWrite_DropsClaimAndPinsField()
        {
            var data = new GameCustomDataFile
            {
                ExcludedFromRefreshes = true,
                RuleClaims = new List<RuleClaim>
                {
                    new RuleClaim { Field = RuleFields.ExcludedFromRefreshes, Written = RuleFields.True },
                    new RuleClaim { Field = RuleFields.CompletionStatus, Written = Completed }
                }
            };

            RuleReconciler.RecordUserWrite(data, RuleFields.ExcludedFromRefreshes);

            Assert.IsNull(RuleReconciler.FindClaim(data, RuleFields.ExcludedFromRefreshes));
            Assert.IsNotNull(RuleReconciler.FindClaim(data, RuleFields.CompletionStatus));
            Assert.IsTrue(RuleReconciler.IsPinned(data, RuleFields.ExcludedFromRefreshes));
        }

        [TestMethod]
        public void UserIncludesRuleExcludedGame_RuleStaysOffUntilItReleases()
        {
            var data = new GameCustomDataFile();
            Apply(data, match: true);
            Assert.AreEqual(true, data.ExcludedFromRefreshes);

            // The user includes the game by hand.
            data.ExcludedFromRefreshes = null;
            RuleReconciler.RecordUserWrite(data, RuleFields.ExcludedFromRefreshes);

            Apply(data, match: true);
            Assert.IsNull(data.ExcludedFromRefreshes);

            // The game stops matching, which ends the override; a later match excludes again.
            Apply(data, match: false);
            Assert.IsFalse(RuleReconciler.IsPinned(data, RuleFields.ExcludedFromRefreshes));
            Apply(data, match: true);
            Assert.AreEqual(true, data.ExcludedFromRefreshes);
        }

        [TestMethod]
        public void UserExclusion_SurvivesRuleMatchingAndReleasing()
        {
            var data = new GameCustomDataFile { ExcludedFromRefreshes = true };

            Apply(data, match: true);
            Apply(data, match: false);

            Assert.AreEqual(true, data.ExcludedFromRefreshes);
            Assert.IsNull(data.RuleClaims?.Find(claim => claim != null));
        }

        [TestMethod]
        public void NormalizeInternal_KeepsRuleClaimsAndPins()
        {
            var gameId = Guid.NewGuid();
            var data = new GameCustomDataFile
            {
                RuleClaims = new List<RuleClaim>
                {
                    new RuleClaim { Field = RuleFields.CompletionStatus, Written = Completed, Prior = Playing },
                    new RuleClaim { Field = " " }
                },
                UserPinnedFields = new List<string> { RuleFields.ExcludedFromSummaries, RuleFields.ExcludedFromSummaries }
            };

            var normalized = GameCustomDataNormalizer.NormalizeInternal(data, gameId);

            Assert.AreEqual(1, normalized.RuleClaims.Count);
            Assert.AreEqual(Playing, normalized.RuleClaims[0].Prior);
            CollectionAssert.AreEqual(new[] { RuleFields.ExcludedFromSummaries }, normalized.UserPinnedFields);
            Assert.IsTrue(GameCustomDataNormalizer.HasInternalData(normalized));
            Assert.IsFalse(GameCustomDataNormalizer.HasVisibleCustomization(normalized));
        }

        [TestMethod]
        public void NormalizeInternal_DropsEmptyRuleState()
        {
            var normalized = GameCustomDataNormalizer.NormalizeInternal(
                new GameCustomDataFile { RuleClaims = new List<RuleClaim>(), UserPinnedFields = new List<string>() },
                Guid.NewGuid());

            Assert.IsNull(normalized.RuleClaims);
            Assert.IsNull(normalized.UserPinnedFields);
            Assert.IsFalse(GameCustomDataNormalizer.HasInternalData(normalized));
        }

        [TestMethod]
        public void HasConditions_SortOnlyPreset_IsFalse()
        {
            Assert.IsFalse(FilterExclusionRuleService.HasConditions(new FilterPresetSettings()));
            Assert.IsFalse(FilterExclusionRuleService.HasConditions(null));
        }

        [TestMethod]
        public void HasConditions_CompletionStatusFilter_IsTrue()
        {
            var settings = new FilterPresetSettings
            {
                CompletionStatuses = new IdItemFilterItemProperties { Ids = new List<Guid> { Guid.NewGuid() } }
            };

            Assert.IsTrue(FilterExclusionRuleService.HasConditions(settings));
        }

        private static void Apply(GameCustomDataFile data, bool match)
        {
            var field = RuleFields.ExcludedFromRefreshes;
            var decision = RuleReconciler.Decide(
                field,
                RuleReconciler.ToFlagText(data.ExcludedFromRefreshes),
                match ? RuleFields.True : null,
                RuleReconciler.FindClaim(data, field),
                RuleReconciler.IsPinned(data, field));
            if (decision.Write)
            {
                data.ExcludedFromRefreshes = RuleReconciler.FromFlagText(decision.Value);
            }

            RuleReconciler.ApplyState(data, field, decision);
        }
    }
}
