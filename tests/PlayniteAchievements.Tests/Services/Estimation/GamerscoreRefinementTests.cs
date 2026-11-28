using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Estimation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation.Tests
{
    /// <summary>Common Gamerscore values, rare special values, calibration from metadata sources, and research follow-ups.</summary>
    [TestClass]
    public class GamerscoreRefinementTests
    {
        private static readonly HashSet<int> Common = new HashSet<int>(new EstimationSettings().GamerscoreCommonValues);

        private static AchievementScore Score(string name, int difficulty = 2, double confidence = 0.9) =>
            new AchievementScore { ApiName = name, Difficulty = difficulty, Confidence = confidence };

        // ------------------------------------------------------------------ common values

        [TestMethod]
        public void Allocation_UsesCommonValuesAndStaysExact()
        {
            var rnd = new Random(11);
            var off = 0; var total = 0;
            foreach (var n in new[] { 10, 12, 20, 30, 40, 50, 75, 100 })
            {
                for (var run = 0; run < 100; run++)
                {
                    var weights = Enumerable.Range(0, n).Select(i => 0.7 + 0.6 * rnd.NextDouble()).ToList();
                    var milestones = new bool[n];
                    for (var k = 0; k < Math.Max(1, n / 20); k++) { var j = rnd.Next(n); milestones[j] = true; weights[j] *= 2.5; }

                    var values = GamerscoreEstimator.AllocateBlock(weights, milestones, 1000, false, new EstimationSettings());
                    Assert.AreEqual(1000, values.Sum());
                    Assert.IsTrue(values.All(v => v >= 5 && v <= 200 && v % 5 == 0));
                    off += values.Count(v => !Common.Contains(v)); total += n;
                }
            }

            Assert.IsTrue(off / (double)total < 0.01, "off-common share was " + off / (double)total);
        }

        [TestMethod]
        public void Allocation_CanBeSwitchedBackToPlainRounding()
        {
            var weights = Enumerable.Range(0, 30).Select(i => 0.7 + 0.02 * i).ToList();
            var values = GamerscoreEstimator.AllocateBlock(weights, null, 1000, false, new EstimationSettings { GamerscorePreferCommonValues = false });
            Assert.AreEqual(1000, values.Sum());
            Assert.IsTrue(values.Any(v => !Common.Contains(v)), "plain rounding keeps the old behaviour, which uses other multiples of 5");
        }

        [TestMethod]
        public void Allocation_UniformLeftoverStillGoesOnTheMilestone()
        {
            var weights = Enumerable.Repeat(1.0, 90).ToList();
            var milestones = Enumerable.Range(0, 90).Select(i => i == 89).ToList();
            var values = GamerscoreEstimator.AllocateBlock(weights, milestones, 1000, true, new EstimationSettings());
            Assert.AreEqual(1000, values.Sum());
            Assert.AreEqual(89, values.Count(v => v == 10));
        }

        // ------------------------------------------------------------------ special values

        private static List<AchievementScore> Forty() => Enumerable.Range(0, 40).Select(i => Score("a" + i)).ToList();

        private static Dictionary<string, int> Run(List<AchievementScore> scores, GameScoring game) =>
            GamerscoreEstimator.Estimate(new GamerscoreEstimationInput { Scores = scores, Game = game }).ToDictionary(e => e.ApiName, e => e.Value);

        [TestMethod]
        public void Special_FailureInDlcMayBeZeroAndTheBlockStaysExact()
        {
            var scores = Forty();
            scores[3].SpecialKind = SpecialValueKind.Failure; scores[3].SpecialValue = 0; scores[3].Category = "Pack";
            var game = new GameScoring { OverallConfidence = 0.9 };
            game.CategoryKinds["Pack"] = CategoryKind.Dlc;

            var values = Run(scores, game);

            Assert.AreEqual(0, values["a3"]);
            Assert.AreEqual(1000, values.Values.Sum());
        }

        [TestMethod]
        public void Special_FailureInTheBaseGameIsNeverZeroAndIsBalancedToTheExactTotal()
        {
            var scores = Forty();
            scores[3].SpecialKind = SpecialValueKind.Failure; scores[3].SpecialValue = 0; scores[3].Category = "Base";
            var game = new GameScoring { OverallConfidence = 0.9 };
            game.CategoryKinds["Base"] = CategoryKind.Base;

            var values = Run(scores, game);

            Assert.AreEqual(1, values["a3"]);
            Assert.AreEqual(1000, values.Values.Sum(), "a 1 G value is paired with one balancing value so the block is still exact");
            Assert.AreEqual(2, values.Values.Count(v => !Common.Contains(v)), "exactly the special value and its balancer are unusual");
        }

        [TestMethod]
        public void Special_IsRareAndNeverOnCapstonesSeriesOrLowConfidence()
        {
            var scores = Forty();
            foreach (var i in new[] { 3, 7, 9, 11 })
            {
                scores[i].SpecialKind = SpecialValueKind.Joke; scores[i].SpecialValue = 7;
            }

            scores[9].IsCapstone = true;
            scores[11].SeriesId = "wins";
            scores[3].Confidence = 0.5;
            var game = new GameScoring { OverallConfidence = 0.9 };

            var values = Run(scores, game);

            Assert.AreEqual(7, values["a7"], "the one eligible joke is used");
            Assert.AreNotEqual(7, values["a9"]);
            Assert.AreNotEqual(7, values["a11"]);
            Assert.AreNotEqual(7, values["a3"]);
            Assert.AreEqual(1000, values.Values.Sum());
        }

        [TestMethod]
        public void Special_IsIgnoredInLowConfidenceUniformOrDisabledCases()
        {
            var scores = Forty();
            scores[7].SpecialKind = SpecialValueKind.Joke; scores[7].SpecialValue = 7;

            Assert.AreNotEqual(7, Run(scores, new GameScoring { OverallConfidence = 0.4 })["a7"]);
            Assert.AreNotEqual(7, Run(scores, new GameScoring { OverallConfidence = 0.9, IsUniformSet = true })["a7"]);

            var off = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput
            {
                Scores = scores, Game = new GameScoring { OverallConfidence = 0.9 },
                Settings = new EstimationSettings { GamerscoreAllowSpecialValues = false }
            }).ToDictionary(e => e.ApiName, e => e.Value);
            Assert.AreNotEqual(7, off["a7"]);
        }

        [TestMethod]
        public void Special_ParserReadsKindAndValue()
        {
            var json = "{\"game\":{\"profile\":\"short_easy\",\"overallConfidence\":0.9,\"uniformSet\":false}," +
                       "\"achievements\":[{\"id\":\"a0001\",\"difficulty\":1,\"type\":\"optional\",\"phase\":\"early\",\"capstone\":false,\"confidence\":0.9," +
                       "\"specialKind\":\"failure\",\"specialValue\":0}]}";
            var parsed = EstimationResponseParser.Parse(json, new Dictionary<string, string> { { "a0001", "ACH_1" } });

            Assert.AreEqual(SpecialValueKind.Failure, parsed.Scores.Single().SpecialKind);
            Assert.AreEqual(0, parsed.Scores.Single().SpecialValue);
        }

        // ------------------------------------------------------------------ calibration from metadata sources

        [TestMethod]
        public void Calibration_CountsOfficialListsTakenFromXboxAndPlayStationMetadata()
        {
            var steamWithPsn = new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "Steam game", ProviderKey = "Steam", TrophiesFromPlayStationMetadata = true,
                Achievements = Enumerable.Range(0, 8).Select(i => new EstimationSourceAchievement
                    { ApiName = "s" + i, NativeTrophyType = i < 6 ? "bronze" : "gold" }).ToList()
            };
            var steamWithXbox = new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "Other", ProviderKey = "Steam", PointsFromXboxMetadata = true,
                Achievements = Enumerable.Range(0, 20).Select(i => new EstimationSourceAchievement { ApiName = "x" + i, NativePoints = 50 }).ToList()
            };
            var notOfficial = new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "Jolt", ProviderKey = "GameJolt",
                Achievements = Enumerable.Range(0, 8).Select(i => new EstimationSourceAchievement { ApiName = "j" + i, NativeTrophyType = "bronze", NativePoints = 10 }).ToList()
            };

            var profile = CalibrationProfile.FromSources(new[] { steamWithPsn, steamWithXbox, notOfficial });

            Assert.AreEqual(1, profile.Lists.Count, "only the game with official PlayStation grades counts as a trophy list");
            Assert.AreEqual(8, profile.Lists[0].Size);
            Assert.AreEqual(1, profile.GamerscoreRows.Count, "only the game with official Xbox values counts as a Gamerscore list");
            Assert.AreEqual(1000.0, profile.GamerscoreRows[0].AverageTotal, 0.0001);
        }

        [TestMethod]
        public void Calibration_FitFromLibraryNowWorksWithMetadataLists()
        {
            var games = Enumerable.Range(0, 6).Select(i => new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "m" + i, ProviderKey = "Steam", TrophiesFromPlayStationMetadata = true,
                Achievements = Enumerable.Range(0, 11).Select(k => new EstimationSourceAchievement { ApiName = "t" + i + "_" + k, NativeTrophyType = "gold" }).ToList()
            }).ToList();

            Assert.AreEqual(990, CalibrationProfile.FromSources(games).SuggestedTrophyBudget);
        }

        // ------------------------------------------------------------------ research follow-ups

        [TestMethod]
        public void ClearMisses_KeepsFoundNotesAndOnlyForgetsMisses()
        {
            ResearchNotesCache.Configure(null);
            var id = Guid.NewGuid();
            ResearchNotesCache.For(id)["a"] = "found";
            ResearchNotesCache.MissesFor(id).Add("b");

            var changed = ResearchNotesCache.ClearMisses(new[] { id, Guid.NewGuid() });

            Assert.AreEqual(1, changed);
            Assert.AreEqual("found", ResearchNotesCache.For(id)["a"]);
            Assert.AreEqual(0, ResearchNotesCache.MissesFor(id).Count);
            ResearchNotesCache.Configure(null);
        }

        private sealed class NoHits : ISearchProvider
        {
            public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken token)
            {
                var response = new SearchResponse { Success = true, CreditsUsed = 1 };
                response.Hits.Add(new SearchHit { Url = "https://example.com/x", Title = "Unrelated", Snippet = "nothing about it" });
                return Task.FromResult(response);
            }
        }

        [TestMethod]
        public void Gatherer_LogsWhichAchievementsAreStillUncovered()
        {
            var logs = new List<string>();
            var gatherer = new EvidenceGatherer(new NoHits(), null,
                new EvidenceOptions { MonthlyCreditBudget = 0, MaxGameQueries = 1, MaxAchievementQueries = 1 }, logs.Add);
            var context = new EstimationGameContext
            {
                Title = "Star Quest", TargetPlatform = "psn",
                Achievements = new[] { "Moon Walker", "Void Slayer", "Sun Chaser" }
                    .Select((n, i) => new PublicAchievement { ApiName = "A" + i, DisplayName = n, Description = "d" }).ToList()
            };
            var misses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            gatherer.GatherAsync(context, new Dictionary<string, string>(), misses, CancellationToken.None).Wait();

            var line = logs.Single(l => l.Contains("no evidence yet"));
            Assert.IsTrue(line.Contains("3 achievement(s)"));
            Assert.IsTrue(line.Contains("Not searched yet (2"), line);
            Assert.IsTrue(line.Contains("Searched, nothing found (1"), line);
            Assert.AreEqual(1, misses.Count, "one achievement was searched and remembered as a miss");
        }
    }
}
