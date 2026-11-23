using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Services.Estimation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Estimation.Tests
{
    [TestClass]
    public class EstimationTests
    {
        private static AchievementScore Score(string id, int difficulty = 3, AchievementKind kind = AchievementKind.StoryMilestone,
            GamePhase phase = GamePhase.Mid, bool capstone = false, string series = null, int position = 0, double confidence = 1.0)
        {
            return new AchievementScore
            {
                ApiName = id, Difficulty = difficulty, Kind = kind, Phase = phase, IsCapstone = capstone,
                SeriesId = series, SeriesPosition = position, Confidence = confidence
            };
        }

        private static List<AchievementScore> Spread(int count)
        {
            // A believable mix: mostly easy and mid, a few hard ones, one capstone.
            var list = new List<AchievementScore>();
            for (var i = 0; i < count; i++)
            {
                var t = i / (double)Math.Max(1, count - 1);
                list.Add(Score("ach" + i,
                    difficulty: 1 + (int)Math.Round(t * 4),
                    kind: t < 0.1 ? AchievementKind.Tutorial : t < 0.6 ? AchievementKind.StoryMilestone : t < 0.9 ? AchievementKind.Boss : AchievementKind.Challenge,
                    phase: t < 0.33 ? GamePhase.Early : t < 0.66 ? GamePhase.Mid : GamePhase.Late,
                    capstone: i == count - 1));
            }

            return list;
        }

        private static int Points(IEnumerable<TrophyEstimate> estimates) =>
            estimates.Sum(e => e.Grade == TrophyGrade.Gold ? 90 : e.Grade == TrophyGrade.Silver ? 30 : 15);

        // ------------------------------------------------------------------ guard

        [TestMethod]
        public void Guard_DropsEstimatesThatHaveNativeData()
        {
            var estimates = new[] { "a", "b", "c" };
            var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "B" };
            var writable = NativeWinsGuard.FilterWritable(estimates, e => e, native);
            CollectionAssert.AreEqual(new[] { "a", "c" }, writable);
        }

        [TestMethod]
        public void Guard_ListsEstimatesMadeStaleByNativeArrival()
        {
            var stale = NativeWinsGuard.StaleEstimates(new[] { "a", "b" }, new HashSet<string> { "b", "z" });
            CollectionAssert.AreEqual(new[] { "b" }, stale);
        }

        [TestMethod]
        public void Trophies_NeverGenerateForNativeAchievements()
        {
            var input = new TrophyEstimationInput { Scores = Spread(12) };
            input.NativeGrades["ach3"] = TrophyGrade.Bronze;
            input.NativeGrades["ach11"] = TrophyGrade.Gold;
            input.Targets = new HashSet<string> { "ach3", "ach4", "ach5" };

            var names = TrophyGradeEstimator.Estimate(input).Select(e => e.ApiName).ToList();
            CollectionAssert.DoesNotContain(names, "ach3");
            CollectionAssert.DoesNotContain(names, "ach11");
            CollectionAssert.Contains(names, "ach4");
        }

        [TestMethod]
        public void Gamerscore_NeverGenerateForNativeAchievements()
        {
            var input = new GamerscoreEstimationInput { Scores = Spread(10) };
            input.NativeValues["ach2"] = 50;
            var names = GamerscoreEstimator.Estimate(input).Select(e => e.ApiName).ToList();
            CollectionAssert.DoesNotContain(names, "ach2");
            Assert.AreEqual(9, names.Count);
        }

        // ------------------------------------------------------------------ trophies

        [TestMethod]
        public void Trophies_SmallListLandsNearNativeBudgetAndIsGoldHeavy()
        {
            // A Short Hike: 11 gold + 1 silver = 1,020 points.
            var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput { Scores = Spread(12) });
            var points = Points(estimates);

            Assert.IsTrue(points >= 940 && points <= 1090, "points were " + points);
            Assert.IsTrue(estimates.Count(e => e.Grade == TrophyGrade.Gold) >= 9);
        }

        [TestMethod]
        public void Trophies_HigherValueNeverGetsALowerGradeExceptForAReasonedGold()
        {
            var scores = Spread(40);
            var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput { Scores = scores })
                .ToDictionary(e => e.ApiName);
            var game = new GameScoring();
            var value = scores.ToDictionary(s => s.ApiName, s => ValueScoring.Effective(s, game));

            foreach (var a in scores)
            {
                foreach (var b in scores)
                {
                    if (value[a.ApiName] <= value[b.ApiName] + 1e-9) continue; // only strictly higher value

                    var ga = estimates[a.ApiName];
                    var gb = estimates[b.ApiName];
                    if (ga.Grade >= gb.Grade) continue;

                    // The one allowed inversion: the lower item is gold because it has a stated reason
                    // (gold requires one) while the higher item has none and drops to silver.
                    var allowed = gb.Grade == TrophyGrade.Gold && gb.Reason != "budget" && gb.Reason != "value";
                    Assert.IsTrue(allowed, a.ApiName + " (" + value[a.ApiName] + ", " + ga.Grade + ") vs " + b.ApiName + " (" + value[b.ApiName] + ", " + gb.Grade + ", " + gb.Reason + ")");
                }
            }
        }

        [TestMethod]
        public void Trophies_GoldWithoutAReasonDropsToSilver()
        {
            // High scores everywhere but nothing marks a capstone, ending or hard challenge.
            var scores = Enumerable.Range(0, 80)
                .Select(i => Score("x" + i, difficulty: 4, kind: AchievementKind.Boss, phase: GamePhase.Late))
                .ToList();
            var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput { Scores = scores });
            Assert.AreEqual(0, estimates.Count(e => e.Grade == TrophyGrade.Gold));
        }

        [TestMethod]
        public void Trophies_LargeListIsBronzeDominantWithGoldOnlyForCapstones()
        {
            var scores = Spread(300);
            var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput { Scores = scores });

            Assert.IsTrue(estimates.Count(e => e.Grade == TrophyGrade.Bronze) > estimates.Count / 2);
            Assert.IsTrue(estimates.Count(e => e.Grade == TrophyGrade.Gold) <= 3);
        }

        [TestMethod]
        public void Trophies_UniformSetIsAllBronze()
        {
            var scores = Enumerable.Range(0, 30).Select(i => Score("cat" + i, difficulty: 1)).ToList();
            var input = new TrophyEstimationInput { Scores = scores, Game = new GameScoring { IsUniformSet = true } };
            var estimates = TrophyGradeEstimator.Estimate(input);
            Assert.AreEqual(30, estimates.Count);
            Assert.IsTrue(estimates.All(e => e.Grade == TrophyGrade.Bronze));
        }

        [TestMethod]
        public void Trophies_ValueBasedPresetGivesSmallListsMoreVariety()
        {
            var native = TrophyGradeEstimator.Estimate(new TrophyEstimationInput { Scores = Spread(12) });
            var valueBased = TrophyGradeEstimator.Estimate(new TrophyEstimationInput
            {
                Scores = Spread(12),
                Settings = new EstimationSettings { Preset = BudgetPreset.ValueBased }
            });

            Assert.IsTrue(valueBased.Select(e => e.Grade).Distinct().Count() >= native.Select(e => e.Grade).Distinct().Count());
            Assert.IsTrue(Points(valueBased) <= Points(native));
        }

        [TestMethod]
        public void Trophies_PlatinumIsNeverProduced()
        {
            var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput { Scores = Spread(25) });
            Assert.IsTrue(estimates.All(e => e.TrophyType == "bronze" || e.TrophyType == "silver" || e.TrophyType == "gold"));
        }

        [TestMethod]
        public void Trophies_FillInsIgnoreTheBudgetAndFollowNativeGrades()
        {
            // Hollow Knight style: natives are mostly bronze, fill-ins should not be dragged to ~1,000 points.
            var scores = Spread(40);
            var input = new TrophyEstimationInput { Scores = scores };
            for (var i = 0; i < 25; i++)
            {
                input.NativeGrades[scores[i].ApiName] = scores[i].Difficulty >= 4 ? TrophyGrade.Silver : TrophyGrade.Bronze;
            }

            var estimates = TrophyGradeEstimator.Estimate(input);
            Assert.AreEqual(15, estimates.Count);
            Assert.IsTrue(estimates.All(e => !input.NativeGrades.ContainsKey(e.ApiName)));
            Assert.IsTrue(estimates.All(e => e.Reason.StartsWith("fill-in")));
        }

        [TestMethod]
        public void Trophies_FillInsLearnMappingFromTheOtherPlatform()
        {
            var scores = Spread(30);
            var input = new TrophyEstimationInput { Scores = scores };

            // Native PSN grades follow Xbox rank exactly for the first 20 achievements.
            for (var i = 0; i < 20; i++)
            {
                input.OtherPlatformValues[scores[i].ApiName] = (i + 1) * 5;
                input.NativeGrades[scores[i].ApiName] = i >= 17 ? TrophyGrade.Gold : i >= 10 ? TrophyGrade.Silver : TrophyGrade.Bronze;
            }

            // The ten missing ones also exist on Xbox with high values, so should map high.
            for (var i = 20; i < 30; i++) input.OtherPlatformValues[scores[i].ApiName] = (i + 1) * 5;

            var estimates = TrophyGradeEstimator.Estimate(input);
            Assert.AreEqual(10, estimates.Count);
            Assert.IsTrue(estimates.Count(e => e.Grade >= TrophyGrade.Silver) >= 8);
        }

        // ------------------------------------------------------------------ gamerscore

        [TestMethod]
        public void Gamerscore_BaseBlockSumsToExactly1000WithValidValues()
        {
            var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput { Scores = Spread(100) });
            Assert.AreEqual(100, estimates.Count);
            Assert.AreEqual(1000, estimates.Sum(e => e.Value));
            Assert.IsTrue(estimates.All(e => e.Value % 5 == 0 && e.Value >= 5 && e.Value <= 200));
        }

        [TestMethod]
        public void Gamerscore_ValuesClusterNearAverageWithCapstoneSpike()
        {
            var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput { Scores = Spread(100) });
            var capstone = estimates.Single(e => e.ApiName == "ach99").Value;
            var median = estimates.Select(e => e.Value).OrderBy(v => v).ElementAt(50);

            Assert.IsTrue(median >= 5 && median <= 20, "median was " + median);
            Assert.IsTrue(capstone >= 3 * 10, "capstone was " + capstone);
        }

        [TestMethod]
        public void Gamerscore_SmallListStillGetsTheFullBlock()
        {
            var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput { Scores = Spread(12) });
            Assert.AreEqual(1000, estimates.Sum(e => e.Value));
            Assert.IsTrue(estimates.All(e => e.Value <= 200));
        }

        [TestMethod]
        public void Gamerscore_HugeListSplitsIntoBlocksOf100WorthOneThousandEach()
        {
            // Halo MCC scale.
            var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput { Scores = Spread(700) });
            Assert.AreEqual(700, estimates.Count);

            var blocks = estimates.GroupBy(e => e.Block).ToList();
            Assert.AreEqual(7, blocks.Count);
            foreach (var block in blocks)
            {
                Assert.AreEqual(100, block.Count());
                Assert.AreEqual(1000, block.Sum(e => e.Value));
            }
        }

        [TestMethod]
        public void Gamerscore_UniformSetSplitsEvenlyAndLeftoversGoToMilestones()
        {
            var scores = Enumerable.Range(0, 90).Select(i => Score("hid" + i, difficulty: 1, capstone: i == 89)).ToList();
            var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput
            {
                Scores = scores,
                Game = new GameScoring { IsUniformSet = true }
            });

            Assert.AreEqual(1000, estimates.Sum(e => e.Value));
            var common = estimates.GroupBy(e => e.Value).OrderByDescending(g => g.Count()).First();
            Assert.IsTrue(common.Count() >= 80, "uniform set should be almost entirely one value");
            Assert.IsTrue(estimates.Single(e => e.ApiName == "hid89").Value >= common.Key);
        }

        [TestMethod]
        public void Gamerscore_LadderIsFlatWithABumpAtTheCapstone()
        {
            var scores = new List<AchievementScore>();
            for (var i = 0; i < 20; i++) scores.Add(Score("filler" + i, difficulty: 2));
            var ladder = new[] { 1, 2, 3, 4, 5 };
            foreach (var step in ladder)
            {
                scores.Add(Score("win" + step, difficulty: step, series: "wins", position: step));
            }

            var estimates = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput { Scores = scores })
                .ToDictionary(e => e.ApiName, e => e.Value);

            // Flat: the first four steps sit within one step (5 G) of each other.
            var flat = new[] { estimates["win1"], estimates["win2"], estimates["win3"], estimates["win4"] };
            Assert.IsTrue(flat.Max() - flat.Min() <= 5, string.Join(",", flat));
            Assert.IsTrue(estimates["win5"] > flat.Max());
        }

        [TestMethod]
        public void Gamerscore_FillInsAreAnExtraBlockPricedFromNativeValues()
        {
            var scores = Spread(60);
            var input = new GamerscoreEstimationInput { Scores = scores };
            for (var i = 0; i < 40; i++) input.NativeValues[scores[i].ApiName] = 20;

            var estimates = GamerscoreEstimator.Estimate(input);
            Assert.AreEqual(20, estimates.Count);
            Assert.AreEqual(400, estimates.Sum(e => e.Value)); // 20 achievements at the native average of 20 G
        }

        [TestMethod]
        public void Gamerscore_AllocatorHandlesExtremeWeightsWithoutBreakingRules()
        {
            var weights = new List<double> { 1000, 1, 1, 1, 1, 1 };
            var values = GamerscoreEstimator.AllocateBlock(weights, null, 1000, false, new EstimationSettings());
            Assert.AreEqual(1000, values.Sum());
            Assert.IsTrue(values.All(v => v >= 5 && v <= 200 && v % 5 == 0));
        }

        // ------------------------------------------------------------------ privacy

        [TestMethod]
        public void Privacy_RequestHasNoKeysOutsideTheAllowlist()
        {
            var context = new EstimationGameContext
            {
                Title = "A Short Hike", ReleaseYear = 2019, Genre = "Adventure", TargetPlatform = "psn",
                Achievements = new List<PublicAchievement>
                {
                    new PublicAchievement { ApiName = "ACH_1", DisplayName = "First flight", Description = "Fly for the first time", RarityPercent = 72.34 }
                }
            };

            var requests = EstimationRequestBuilder.Build(context);
            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(0, PrivacyFilter.UnexpectedKeys(requests[0].Json).Count);

            // The real api name never leaves the machine: only an opaque id does.
            Assert.IsFalse(requests[0].Json.Contains("ACH_1"));
            Assert.AreEqual("ACH_1", requests[0].IdMap["a0001"]);
        }

        [TestMethod]
        public void Privacy_RedactsCredentialsAndPersonalDetailsInText()
        {
            Assert.IsTrue(PrivacyFilter.LooksSensitive("contact me at someone@example.com"));
            Assert.IsTrue(PrivacyFilter.LooksSensitive("Bearer abc123def456"));
            Assert.IsTrue(PrivacyFilter.LooksSensitive(@"saved in C:\Users\Lily\Documents"));
            Assert.IsTrue(PrivacyFilter.LooksSensitive("npsso: abcdef"));
            Assert.IsTrue(PrivacyFilter.LooksSensitive("key AIzaSyD0123456789abcdefghijklmnopqrstuv0"));
            Assert.IsFalse(PrivacyFilter.LooksSensitive("Defeat the final boss without taking damage"));
        }

        [TestMethod]
        public void Privacy_BlocksARequestWithUnexpectedFields()
        {
            var json = "{\"task\":\"x\",\"unlockTime\":\"2025-01-01\"}";
            Assert.ThrowsException<InvalidOperationException>(() => PrivacyFilter.AssertSafe(json));
        }

        [TestMethod]
        public void Request_LargeListsAreChunkedWithStableIds()
        {
            var context = new EstimationGameContext
            {
                Title = "Geometry Dash", TargetPlatform = "xbox",
                Achievements = Enumerable.Range(0, 250)
                    .Select(i => new PublicAchievement { ApiName = "g" + i, DisplayName = "Level " + i, Description = "Beat level " + i })
                    .ToList()
            };

            var requests = EstimationRequestBuilder.Build(context, 100);
            Assert.AreEqual(3, requests.Count);
            Assert.AreEqual(100, requests[0].IdMap.Count);
            Assert.AreEqual(50, requests[2].IdMap.Count);
            Assert.AreEqual("g100", requests[1].IdMap["a0101"]);
        }

        [TestMethod]
        public void Calibration_SummarisesNativeListsBySize()
        {
            var lists = new[]
            {
                new NativeTrophyList { Grades = Enumerable.Repeat(TrophyGrade.Gold, 11).Concat(new[] { TrophyGrade.Silver }).ToList() },
                new NativeTrophyList { Grades = Enumerable.Repeat(TrophyGrade.Bronze, 50).ToList() }
            };

            var profile = CalibrationProfile.Build(lists);
            Assert.AreEqual(2, profile.Rows.Count);
            Assert.AreEqual(1020, profile.Rows[0].AveragePoints);
            Assert.AreEqual(1.0, profile.Rows[1].BronzeShare);
        }
    }
}
