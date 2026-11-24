using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Estimation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation.Tests
{
    [TestClass]
    public class EstimationRunnerTests
    {
        /// <summary>A stand-in for the model: answers every id in the request, scoring by position.</summary>
        private sealed class FakeModel : IEstimationLlmClient
        {
            public readonly List<string> Requests = new List<string>();
            public LlmCallResult Failure;

            public Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken cancellationToken)
            {
                Requests.Add(requestJson);
                if (Failure != null) return Task.FromResult(Failure);

                var ids = ((JArray)JObject.Parse(requestJson)["achievements"]).Select(a => (string)a["id"]).ToList();
                var answer = new JObject
                {
                    ["game"] = new JObject { ["profile"] = "long_hard", ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new JArray(ids.Select((id, i) =>
                    {
                        var t = i / (double)Math.Max(1, ids.Count - 1);
                        return new JObject
                        {
                            ["id"] = id,
                            ["difficulty"] = 1 + (int)Math.Round(t * 4),
                            ["type"] = t < 0.1 ? "tutorial" : t < 0.7 ? "story_milestone" : "boss",
                            ["phase"] = t < 0.33 ? "early" : t < 0.66 ? "mid" : "late",
                            ["capstone"] = i == ids.Count - 1,
                            ["seriesId"] = null, ["seriesPosition"] = 0, ["confidence"] = 0.85
                        };
                    }))
                };
                return Task.FromResult(LlmCallResult.Ok(answer.ToString()));
            }
        }

        private static EstimationSourceGame Game(int count, bool gamerscore = false)
        {
            return new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "Test", PointsAreGamerscore = gamerscore,
                Achievements = Enumerable.Range(0, count)
                    .Select(i => new EstimationSourceAchievement { ApiName = "ACH_" + i, DisplayName = "Step " + i, Description = "Do " + i })
                    .ToList()
            };
        }

        private static EstimationRunResult Run(EstimationSourceGame game, FakeModel model, EstimationRunOptions options = null) =>
            EstimationRunner.RunAsync(game, model, options ?? new EstimationRunOptions(), CancellationToken.None).Result;

        // ------------------------------------------------------------------ planner

        [TestMethod]
        public void Planner_ExcludesPlatinumFromEverything()
        {
            var game = Game(5);
            game.Achievements[0].NativeTrophyType = "platinum";
            var plan = EstimationPlanner.Plan(game, new EstimationRunOptions());

            Assert.AreEqual(4, plan.Achievements.Count);
            Assert.IsFalse(plan.TrophyTargets.Contains("ACH_0") || plan.PointsTargets.Contains("ACH_0"));
            Assert.IsFalse(plan.NativeGrades.ContainsKey("ACH_0"));
        }

        [TestMethod]
        public void Planner_NeverTargetsASlotThatHasNativeData()
        {
            var game = Game(6, gamerscore: true);
            game.Achievements[1].NativeTrophyType = "gold";
            game.Achievements[2].NativeTrophyType = "silver";
            game.Achievements[3].NativePoints = 20;
            game.Achievements[4].NativePoints = 0; // a native zero still occupies the slot
            var plan = EstimationPlanner.Plan(game, new EstimationRunOptions());

            CollectionAssert.AreEquivalent(new[] { "ACH_0", "ACH_3", "ACH_4", "ACH_5" }, plan.TrophyTargets.ToList());
            CollectionAssert.AreEquivalent(new[] { "ACH_0", "ACH_1", "ACH_2", "ACH_5" }, plan.PointsTargets.ToList());
        }

        [TestMethod]
        public void Planner_LeavesAlonePointsThatAreNotGamerscore()
        {
            var game = Game(4, gamerscore: false);
            game.Achievements[0].NativePoints = 25; // for example RetroAchievements points
            var plan = EstimationPlanner.Plan(game, new EstimationRunOptions());

            Assert.AreEqual(0, plan.PointsTargets.Count);
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("not Xbox Gamerscore")));
            Assert.AreEqual(4, plan.TrophyTargets.Count, "trophies are still generated");
        }

        [TestMethod]
        public void Planner_LeavesAloneATrophyFieldThatHoldsAnotherProvidersValues()
        {
            var game = Game(4);
            game.Achievements[0].NativeTrophyType = "win_condition";
            var plan = EstimationPlanner.Plan(game, new EstimationRunOptions());

            Assert.AreEqual(0, plan.TrophyTargets.Count);
            Assert.AreEqual(0, plan.NativeGrades.Count, "a foreign value is not a grade anchor");
            Assert.AreEqual(4, plan.PointsTargets.Count);
        }

        // ------------------------------------------------------------------ runner

        [TestMethod]
        public void Runner_GeneratesBothForAGameWithNoNativeData()
        {
            var model = new FakeModel();
            var result = Run(Game(100), model);

            Assert.AreEqual(EstimationRunStatus.Generated, result.Status);
            Assert.AreEqual(100, result.Rows.Count);
            Assert.AreEqual(1000, result.Rows.Sum(r => r.Points.Value));
            Assert.IsTrue(result.Rows.All(r => r.TrophyType == "bronze" || r.TrophyType == "silver" || r.TrophyType == "gold"));
            Assert.IsTrue(result.Rows.All(r => r.Confidence.HasValue));
        }

        [TestMethod]
        public void Runner_FillsOnlyTheMissingAchievementsOfAPartialList()
        {
            // Hollow Knight on PlayStation: 25 native trophies, 15 missing.
            var game = Game(40);
            for (var i = 0; i < 25; i++) game.Achievements[i].NativeTrophyType = i % 5 == 0 ? "silver" : "bronze";
            var model = new FakeModel();

            var result = Run(game, model);

            Assert.AreEqual(EstimationRunStatus.Generated, result.Status);
            var trophyRows = result.Rows.Where(r => r.TrophyType != null).ToList();
            Assert.AreEqual(15, trophyRows.Count);
            Assert.IsTrue(trophyRows.All(r => int.Parse(r.ApiName.Substring(4)) >= 25), "native achievements get nothing");

            // The native grades travel to the model as anchors, and nothing else about the player does.
            var request = JObject.Parse(model.Requests[0]);
            Assert.AreEqual(25, ((JArray)request["anchors"]).Count(a => (string)a["platform"] == "psn"));
            Assert.AreEqual(0, PrivacyFilter.UnexpectedKeys(model.Requests[0]).Count);
            Assert.IsFalse(model.Requests[0].Contains("ACH_"));
        }

        [TestMethod]
        public void Runner_NeverReturnsAValueForANativeSlot()
        {
            var game = Game(30, gamerscore: true);
            for (var i = 0; i < 10; i++) { game.Achievements[i].NativePoints = 20; game.Achievements[i].NativeTrophyType = "bronze"; }

            var result = Run(game, new FakeModel());

            foreach (var row in result.Rows)
            {
                var index = int.Parse(row.ApiName.Substring(4));
                Assert.IsTrue(index >= 10, "row for native achievement " + row.ApiName);
            }
        }

        [TestMethod]
        public void Runner_SkipsWithoutCallingTheModelWhenEverythingIsNative()
        {
            var game = Game(5, gamerscore: true);
            foreach (var a in game.Achievements) { a.NativePoints = 10; a.NativeTrophyType = "bronze"; }
            var model = new FakeModel();

            var result = Run(game, model);

            Assert.AreEqual(EstimationRunStatus.Skipped, result.Status);
            Assert.AreEqual(0, model.Requests.Count);
            Assert.AreEqual(0, result.Rows.Count);
        }

        [TestMethod]
        public void Runner_AFailedModelCallGeneratesNothing()
        {
            var model = new FakeModel { Failure = LlmCallResult.Fail(LlmFailureKind.Transient, "rate limit") };
            var result = Run(Game(10), model);

            Assert.AreEqual(EstimationRunStatus.Failed, result.Status);
            Assert.AreEqual(0, result.Rows.Count);
            Assert.AreEqual("rate limit", result.Message);
        }

        [TestMethod]
        public void Runner_RespectsTheGenerateSwitches()
        {
            var options = new EstimationRunOptions { GenerateGamerscore = false };
            var result = Run(Game(20), new FakeModel(), options);

            Assert.IsTrue(result.Rows.All(r => r.TrophyType != null && !r.Points.HasValue));
        }

        // ------------------------------------------------------------------ obfuscation and config

        [TestMethod]
        public void Obfuscator_RoundTripsAndHidesTheKey()
        {
            var key = "AIzaSyExampleKey1234567890";
            var stored = SecretObfuscator.Obfuscate(key);

            Assert.IsFalse(stored.Contains(key));
            Assert.IsTrue(stored.StartsWith("obf1:"));
            Assert.AreEqual(key, SecretObfuscator.Reveal(stored));
            Assert.AreEqual(key, SecretObfuscator.Reveal("  " + key + " "), "a plain key pasted into the file still works");
            Assert.AreEqual(string.Empty, SecretObfuscator.Reveal("obf1:not-base64!!"));
            Assert.AreEqual(string.Empty, SecretObfuscator.Obfuscate("   "));
        }

        [TestMethod]
        public void Config_StoresTheKeyObfuscatedAndReadsItBack()
        {
            var settings = new PersistedSettings();
            Assert.IsFalse(settings.EstimatedValuesEnabled, "off by default");
            Assert.IsFalse(EstimatedValuesConfig.HasApiKey(settings));

            EstimatedValuesConfig.SetApiKey(settings, "my-secret-key-value");
            Assert.IsFalse(settings.EstimatedValuesGeminiKey.Contains("my-secret-key-value"));
            Assert.AreEqual("my-secret-key-value", EstimatedValuesConfig.GetApiKey(settings));

            var client = EstimatedValuesConfig.ToClientOptions(() => settings);
            Assert.AreEqual("my-secret-key-value", client.ApiKey());
            Assert.AreEqual("gemini-3.5-flash-lite", client.Model);
        }

        [TestMethod]
        public void Config_ClampsTheBudgetToASaneRange()
        {
            var settings = new PersistedSettings { EstimatedValuesTrophyBudget = 99999, EstimatedValuesBudgetPreset = BudgetPreset.ValueBased };
            var estimation = EstimatedValuesConfig.ToEstimationSettings(settings);

            Assert.AreEqual(5000, estimation.TrophyBudget);
            Assert.AreEqual(BudgetPreset.ValueBased, estimation.Preset);
        }
    }
}
