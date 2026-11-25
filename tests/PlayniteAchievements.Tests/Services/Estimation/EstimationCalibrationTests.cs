using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
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
    public class EstimationCalibrationTests
    {
        private static EstimationSourceGame PsnGame(string title, string provider, params string[] grades)
        {
            return new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = title, ProviderKey = provider,
                Achievements = grades.Select((g, i) => new EstimationSourceAchievement
                {
                    ApiName = title + i, DisplayName = "A" + i, Description = "d", NativeTrophyType = g
                }).ToList()
            };
        }

        private static string[] Mix(int bronze, int silver, int gold) =>
            Enumerable.Repeat("bronze", bronze).Concat(Enumerable.Repeat("silver", silver)).Concat(Enumerable.Repeat("gold", gold)).ToArray();

        // ------------------------------------------------------------------ which lists count

        [TestMethod]
        public void FromSources_CountsOnlyCompleteGenuinePsnLists()
        {
            var good = PsnGame("Good", "PSN", Mix(5, 3, 2));
            var partial = PsnGame("Partial", "PSN", Mix(5, 3, 2));
            partial.Achievements[0].NativeTrophyType = null;                       // one trophy has no native grade
            var foreign = PsnGame("Foreign", "PSN", Mix(5, 3, 2));
            foreign.Achievements[1].NativeTrophyType = "win_condition";             // the field means something else
            var gameJolt = PsnGame("Jolt", "GameJolt", Mix(5, 3, 2));               // not a PSN-style budget
            var steam = PsnGame("Steam", "Steam", Mix(5, 3, 2));
            var tiny = PsnGame("Tiny", "PSN", "gold", "gold");                      // too small to say anything

            var profile = CalibrationProfile.FromSources(new[] { good, partial, foreign, gameJolt, steam, tiny });

            Assert.AreEqual(1, profile.Lists.Count);
            Assert.AreEqual(10, profile.Lists[0].Size);
            Assert.AreEqual(5 * 15 + 3 * 30 + 2 * 90, profile.Lists[0].Points);
        }

        [TestMethod]
        public void FromSources_IgnoresPlatinumRowsAndEmulatorProvidersCount()
        {
            var game = PsnGame("Emu", "RPCS3", Mix(4, 2, 1).Concat(new[] { "platinum" }).ToArray());
            var profile = CalibrationProfile.FromSources(new[] { game });

            Assert.AreEqual(1, profile.Lists.Count);
            Assert.AreEqual(7, profile.Lists[0].Size, "the platinum is not part of the list");
        }

        // ------------------------------------------------------------------ what it summarises

        [TestMethod]
        public void FromSources_SummarisesGradeMixBySize()
        {
            var profile = CalibrationProfile.FromSources(new[]
            {
                PsnGame("a", "PSN", Mix(2, 4, 6)),     // 12 trophies
                PsnGame("b", "PSN", Mix(0, 0, 12)),    // 12 trophies
                PsnGame("c", "PSN", Mix(30, 0, 0))     // 30 trophies
            });

            Assert.AreEqual(2, profile.Rows.Count);
            Assert.AreEqual("1-12", profile.Rows[0].SizeBucket);
            Assert.AreEqual(2, profile.Rows[0].Lists);
            Assert.AreEqual(0.75, profile.Rows[0].GoldShare, 0.0001);
            Assert.AreEqual("13-30", profile.Rows[1].SizeBucket);
            Assert.AreEqual(1.0, profile.Rows[1].BronzeShare, 0.0001);
        }

        [TestMethod]
        public void FromSources_MeasuresHowDlcAndUpdatePacksSplitPoints()
        {
            var game = PsnGame("dlc", "PSN", Mix(8, 0, 0).Concat(Mix(0, 0, 2)).ToArray());
            for (var i = 8; i < 10; i++) game.Achievements[i].CategoryKind = "DLC";
            var plain = PsnGame("plain", "PSN", Mix(5, 2, 1));

            var profile = CalibrationProfile.FromSources(new[] { game, plain });

            Assert.IsNotNull(profile.Extras);
            Assert.AreEqual(1, profile.Extras.Lists, "only lists that have DLC or update categories count");
            Assert.AreEqual(180.0 / (8 * 15 + 180), profile.Extras.AveragePointShare, 0.001);
            Assert.AreEqual(0.2, profile.Extras.AverageTrophyShare, 0.0001);
        }

        [TestMethod]
        public void FromSources_SummarisesGamerscoreFromCompleteXboxLists()
        {
            var xbox = new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "x", ProviderKey = "Xbox", PointsAreGamerscore = true,
                Achievements = Enumerable.Range(0, 20).Select(i => new EstimationSourceAchievement
                {
                    ApiName = "x" + i, NativePoints = i == 19 ? 400 : 30   // 19 x 30 + 400 = 970; one clear spike
                }).ToList()
            };
            var incomplete = new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "y", ProviderKey = "Xbox", PointsAreGamerscore = true,
                Achievements = Enumerable.Range(0, 20).Select(i => new EstimationSourceAchievement { ApiName = "y" + i, NativePoints = i == 0 ? (int?)null : 50 }).ToList()
            };

            var profile = CalibrationProfile.FromSources(new[] { xbox, incomplete });

            Assert.AreEqual(1, profile.GamerscoreRows.Count);
            var row = profile.GamerscoreRows[0];
            Assert.AreEqual("1-25", row.SizeBucket);
            Assert.AreEqual(970.0, row.AverageTotal, 0.0001);
            Assert.AreEqual(30.0, row.MedianValue, 0.0001);
            Assert.AreEqual(1.0 / 20, row.SpikeShare, 0.0001);
        }

        [TestMethod]
        public void ClosestLists_PicksTheNearestSizesAndSuggestedBudgetNeedsEnoughLists()
        {
            var games = new[] { 8, 12, 20, 40, 80 }.Select(n => PsnGame("g" + n, "PSN", Mix(n, 0, 0))).ToList();
            var profile = CalibrationProfile.FromSources(games);

            CollectionAssert.AreEqual(new[] { 12, 8, 20 }, profile.ClosestLists(11, 3).Select(l => l.Size).ToList());
            Assert.IsNull(profile.SuggestedTrophyBudget, "five lists fall in 5-45 only if all do; here too few are in range");

            var many = Enumerable.Range(0, 6).Select(i => PsnGame("m" + i, "PSN", Mix(0, 0, 11))).ToList();   // 990 points each
            Assert.AreEqual(990, CalibrationProfile.FromSources(many).SuggestedTrophyBudget);
        }

        // ------------------------------------------------------------------ the request

        [TestMethod]
        public void Request_CarriesCalibrationWithoutAnyLibraryTitle()
        {
            var profile = CalibrationProfile.FromSources(new[]
            {
                PsnGame("Secret Owned Game Alpha", "PSN", Mix(5, 3, 2)),
                PsnGame("Secret Owned Game Beta", "PSN", Mix(10, 5, 5))
            });
            var context = new EstimationGameContext
            {
                Title = "Target", TargetPlatform = "psn", Calibration = profile,
                Achievements = Enumerable.Range(0, 12).Select(i => new PublicAchievement { ApiName = "t" + i, DisplayName = "n" + i, Description = "d" }).ToList()
            };

            var json = EstimationRequestBuilder.Build(context)[0].Json;
            var calibration = (JObject)JObject.Parse(json)["calibration"];

            Assert.IsNotNull(calibration["bySize"]);
            Assert.AreEqual(2, ((JArray)calibration["closestLists"]).Count);
            Assert.AreEqual(10, (int)((JArray)calibration["closestLists"])[0]["size"], "the closest real list to 12 trophies is the 10-trophy one");
            Assert.IsFalse(json.Contains("Secret Owned Game"), "titles from the user's library must never be sent");
            Assert.AreEqual(0, PrivacyFilter.UnexpectedKeys(json).Count);
        }

        [TestMethod]
        public void Request_OmitsCalibrationWhenThereIsNone()
        {
            var context = new EstimationGameContext
            {
                Title = "Target", TargetPlatform = "psn", Calibration = new CalibrationProfile(),
                Achievements = new List<PublicAchievement> { new PublicAchievement { ApiName = "a", DisplayName = "n" } }
            };

            Assert.IsNull(JObject.Parse(EstimationRequestBuilder.Build(context)[0].Json)["calibration"]);
        }

        // ------------------------------------------------------------------ the coordinator

        private sealed class CountingHost : IEstimationHost
        {
            public readonly Dictionary<Guid, EstimationSourceGame> Games = new Dictionary<Guid, EstimationSourceGame>();
            public int Loads;
            public bool ThrowOnLoad;

            public IReadOnlyList<Guid> GetGameIds() => Games.Keys.ToList();
            public EstimationSourceGame LoadSource(Guid id)
            {
                Loads++;
                if (ThrowOnLoad) throw new InvalidOperationException("library unreadable");
                return Games.TryGetValue(id, out var g) ? g : null;
            }
            public int WriteRows(Guid id, string model, IReadOnlyList<EstimatedValueRow> rows) => rows.Count;
            public long ClearAll() => 0;
        }

        private sealed class RecordingModel : IEstimationLlmClient
        {
            public readonly List<string> Requests = new List<string>();

            public Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken token)
            {
                Requests.Add(requestJson);
                var ids = ((JArray)JObject.Parse(requestJson)["achievements"]).Select(a => (string)a["id"]).ToList();
                var answer = new JObject
                {
                    ["game"] = new JObject { ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new JArray(ids.Select(id => new JObject
                    {
                        ["id"] = id, ["difficulty"] = 3, ["type"] = "story_milestone", ["phase"] = "mid", ["capstone"] = false, ["confidence"] = 0.8
                    }))
                };
                return Task.FromResult(LlmCallResult.Ok(answer.ToString()));
            }
        }

        private static EstimationSourceGame Target(string title)
        {
            return new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = title, ProviderKey = "Steam",
                Achievements = Enumerable.Range(0, 12).Select(i => new EstimationSourceAchievement { ApiName = title + i, DisplayName = "A" + i, Description = "d" }).ToList()
            };
        }

        [TestMethod]
        public void Coordinator_SendsCalibrationFromTheLibraryAndComputesItOncePerRun()
        {
            var host = new CountingHost();
            foreach (var g in new[] { PsnGame("n1", "PSN", Mix(5, 3, 2)), PsnGame("n2", "PSN", Mix(8, 2, 2)) }) host.Games[g.PlayniteGameId] = g;
            var t1 = Target("t1"); var t2 = Target("t2");
            host.Games[t1.PlayniteGameId] = t1; host.Games[t2.PlayniteGameId] = t2;
            var model = new RecordingModel();
            var coordinator = new EstimationCoordinator(host, model, () => new EstimationRunOptions { Model = "m" });

            coordinator.RunGamesAsync(new[] { t1.PlayniteGameId, t2.PlayniteGameId }, false, null, CancellationToken.None).Wait();

            Assert.AreEqual(2, model.Requests.Count);
            Assert.IsTrue(model.Requests.All(r => JObject.Parse(r)["calibration"] != null));
            Assert.AreEqual(4 + 2, host.Loads, "four games read once for calibration, then the two targets");
        }

        [TestMethod]
        public void Coordinator_AnUnreadableLibraryJustMeansNoCalibration()
        {
            var host = new CountingHost { ThrowOnLoad = true };
            var target = Target("t");
            host.Games[target.PlayniteGameId] = target;
            var coordinator = new EstimationCoordinator(host, new RecordingModel(), () => new EstimationRunOptions());

            Assert.IsNull(coordinator.EnsureCalibration());
        }

        [TestMethod]
        public void Coordinator_CalibrationCanBeSwitchedOff()
        {
            var host = new CountingHost();
            var native = PsnGame("n1", "PSN", Mix(5, 3, 2)); host.Games[native.PlayniteGameId] = native;
            var target = Target("t"); host.Games[target.PlayniteGameId] = target;
            var model = new RecordingModel();
            var coordinator = new EstimationCoordinator(host, model, () => new EstimationRunOptions { UseCalibration = false });

            coordinator.RunGameAsync(target.PlayniteGameId, false, CancellationToken.None).Wait();

            Assert.IsNull(JObject.Parse(model.Requests[0])["calibration"]);
            Assert.AreEqual(1, host.Loads, "the library is not read at all");
        }
    }
}
