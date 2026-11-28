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
            scores[3].SpecialKind = SpecialValueKind.Failure; scores[3].SpecialValue = 0; scores[3].SpecialConfidence = 0.98; scores[3].Category = "Pack";
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
            scores[3].SpecialKind = SpecialValueKind.Failure; scores[3].SpecialValue = 0; scores[3].SpecialConfidence = 0.98; scores[3].Category = "Base";
            var game = new GameScoring { OverallConfidence = 0.9 };
            game.CategoryKinds["Base"] = CategoryKind.Base;

            var values = Run(scores, game);

            Assert.AreEqual(1, values["a3"]);
            Assert.AreEqual(1000, values.Values.Sum(), "a 1 G value is paired with one balancing value so the block is still exact");
            Assert.AreEqual(2, values.Values.Count(v => !Common.Contains(v)), "exactly the special value and its balancer are unusual");
        }

        [TestMethod]
        public void Special_SeveralMayShareABlockButOnlyWhenEachIsNearCertain()
        {
            var scores = Forty();
            foreach (var i in new[] { 3, 7, 9, 11, 13, 15 })
            {
                scores[i].SpecialKind = SpecialValueKind.Joke; scores[i].SpecialValue = 7; scores[i].SpecialConfidence = 0.98;
            }

            scores[9].IsCapstone = true;
            scores[11].SeriesId = "wins";
            scores[3].SpecialConfidence = 0.8;     // not certain enough
            scores[13].Confidence = 0.5;           // the scoring itself is unsure
            var game = new GameScoring { OverallConfidence = 0.9 };

            var values = Run(scores, game);

            Assert.AreEqual(7, values["a7"]);
            Assert.AreEqual(7, values["a15"], "a second certain special shares the block");
            foreach (var name in new[] { "a3", "a9", "a11", "a13" }) Assert.AreNotEqual(7, values[name], name);
            Assert.AreEqual(1000, values.Values.Sum());
        }

        [TestMethod]
        public void Special_NeverExceedsATenthOfTheBlock()
        {
            var scores = Forty();
            for (var i = 0; i < 12; i++)
            {
                scores[i].SpecialKind = SpecialValueKind.Joke; scores[i].SpecialValue = 5; scores[i].SpecialConfidence = 0.99;
            }

            var values = Run(scores, new GameScoring { OverallConfidence = 0.95 });

            Assert.IsTrue(values.Values.Count(v => v == 5) <= 4 + 2, "at most 4 specials plus balancing/ordinary 5s");
            Assert.AreEqual(1000, values.Values.Sum());
        }

        [TestMethod]
        public void Special_MustAgreeWithTheNativePlayStationTrophyWhenOneExists()
        {
            var scores = Forty();
            scores[7].SpecialKind = SpecialValueKind.Failure; scores[7].SpecialValue = 5; scores[7].SpecialConfidence = 0.98;
            scores[9].SpecialKind = SpecialValueKind.Failure; scores[9].SpecialValue = 5; scores[9].SpecialConfidence = 0.98;
            var game = new GameScoring { OverallConfidence = 0.9 };

            var values = GamerscoreEstimator.Estimate(new GamerscoreEstimationInput
            {
                Scores = scores, Game = game,
                OtherPlatformGrades = new Dictionary<string, TrophyGrade> { { "a7", TrophyGrade.Bronze }, { "a9", TrophyGrade.Gold } }
            }).ToDictionary(e => e.ApiName, e => e.Value);

            Assert.AreEqual(5, values["a7"], "a bronze equivalent agrees with a small failure value");
            Assert.IsTrue(values["a9"] > 5, "a gold equivalent means it is not a special case, so it is valued normally");
            Assert.AreEqual(1000, values.Values.Sum());
        }

        [TestMethod]
        public void Special_IsIgnoredInLowConfidenceUniformOrDisabledCases()
        {
            var scores = Forty();
            scores[7].SpecialKind = SpecialValueKind.Joke; scores[7].SpecialValue = 7; scores[7].SpecialConfidence = 0.98;

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
                       "\"specialKind\":\"failure\",\"specialValue\":0,\"specialConfidence\":0.97}]}";
            var parsed = EstimationResponseParser.Parse(json, new Dictionary<string, string> { { "a0001", "ACH_1" } });

            Assert.AreEqual(SpecialValueKind.Failure, parsed.Scores.Single().SpecialKind);
            Assert.AreEqual(0, parsed.Scores.Single().SpecialValue);
            Assert.AreEqual(0.97, parsed.Scores.Single().SpecialConfidence, 0.0001);
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

        // ------------------------------------------------------------------ evidence reporting and retry variety

        [TestMethod]
        public void TitleVariants_AcceptTheBaseTitleOfAnEditionOrSubtitle()
        {
            var variants = EvidenceGatherer.TitleVariants("Ori and the Blind Forest: Definitive Edition");
            Assert.IsTrue(variants.Contains("ori and the blind forest"));
            Assert.IsTrue(variants.Contains("ori and the blind forest definitive edition"));
            Assert.IsFalse(EvidenceGatherer.TitleVariants("Ori and the Blind Forest").Contains("ori"));
            Assert.AreEqual(0, EvidenceGatherer.TitleVariants("  ").Count);
        }

        [TestMethod]
        public void RetryRounds_UseDifferentlyWordedQueries()
        {
            var context = new EstimationGameContext { Title = "Nightmare Kart" };
            var a = new PublicAchievement { ApiName = "A", DisplayName = "Drifting off", Description = "Drift for a very long time without stopping once" };

            var queries = Enumerable.Range(0, 3).Select(r => EvidenceGatherer.AchievementQuery(context, a, r)).ToList();

            Assert.AreEqual(3, queries.Distinct().Count());
            Assert.IsTrue(queries[0].Contains("Drifting off") && queries[0].Contains("Drift for a very long time"), "the first search carries the name and the description");
            Assert.IsTrue(queries[2].Contains("Drift for a very long time") && !queries[2].Contains("Drifting off"), "the last wording drops the name in case guides spell it differently");
            Assert.AreEqual(queries[0], EvidenceGatherer.AchievementQuery(context, a, 0));
            Assert.AreEqual(queries[0], EvidenceGatherer.AchievementQuery(context, a, 3), "the wording cycles");
        }

        [TestMethod]
        public void ClearMisses_StartsANewRetryRound()
        {
            ResearchNotesCache.Configure(null);
            var id = Guid.NewGuid();
            ResearchNotesCache.MissesFor(id).Add("b");
            ResearchNotesCache.ClearMisses(new[] { id });
            Assert.AreEqual("1", ResearchNotesCache.For(id)[ResearchNotesCache.RetryRoundKey]);
            ResearchNotesCache.MissesFor(id).Add("c");
            ResearchNotesCache.ClearMisses(new[] { id });
            Assert.AreEqual("2", ResearchNotesCache.For(id)[ResearchNotesCache.RetryRoundKey]);
            ResearchNotesCache.Configure(null);
        }

        [TestMethod]
        public void EvidenceSummary_SaysHowManyAreCoveredWhatIsMissingAndTheSources()
        {
            var summary = new EstimationGameSummary { EvidenceTotal = 25, EvidenceFound = 17 };
            summary.EvidenceMissingNames.AddRange(new[] { "Drifting off", "Bullseye!" });
            summary.EvidenceSources["gamefaqs.com"] = 9;
            summary.EvidenceSources["steamcommunity.com"] = 12;

            var text = summary.DescribeEvidence();

            Assert.IsTrue(text.StartsWith("Evidence for 17 of 25 achievements; missing 2: Drifting off; Bullseye!."), text);
            Assert.IsTrue(text.Contains("Sources: steamcommunity.com (12), gamefaqs.com (9)."), text);
            Assert.IsNull(new EstimationGameSummary().DescribeEvidence());

            var dialog = summary.DescribeEvidence(0, 5, includeNames: false);
            Assert.IsTrue(dialog.StartsWith("Evidence for 17 of 25 achievements; 2 without evidence."), dialog);
            Assert.IsFalse(dialog.Contains("Drifting off") || dialog.Contains("Bullseye"), "the dialog shows counts only, never achievement names");
        }

        [TestMethod]
        public void EvidenceSourceReader_ReadsDomainTagsFromNotes()
        {
            var domains = EvidenceSourceReader.Domains("[gamefaqs.com] first … [steamcommunity.com] second … [gamefaqs.com] again [not a domain]").ToList();
            CollectionAssert.AreEqual(new[] { "gamefaqs.com", "steamcommunity.com" }, domains);
        }

        // ------------------------------------------------------------------ retry only regenerates on new evidence

        private sealed class CountingScorer : IEstimationLlmClient
        {
            public int Calls;
            public Task<LlmCallResult> GenerateAsync(string json, CancellationToken t)
            {
                Calls++;
                var ids = ((Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(json)["achievements"]).Select(a => (string)a["id"]).ToList();
                return Task.FromResult(LlmCallResult.Ok(new Newtonsoft.Json.Linq.JObject
                {
                    ["game"] = new Newtonsoft.Json.Linq.JObject { ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new Newtonsoft.Json.Linq.JArray(ids.Select(id => new Newtonsoft.Json.Linq.JObject
                        { ["id"] = id, ["difficulty"] = 3, ["type"] = "boss", ["phase"] = "mid", ["capstone"] = false, ["confidence"] = 0.9 }))
                }.ToString()));
            }
        }

        private sealed class FixedEvidence : IEvidenceSource
        {
            public int NewFromTargeted;
            public bool Unavailable;
            public Task<EvidenceResult> GatherAsync(EstimationGameContext c, IDictionary<string, string> n, ISet<string> m, CancellationToken t)
            {
                var r = new EvidenceResult { FromTargetedSearches = NewFromTargeted, Unavailable = Unavailable, Reason = Unavailable ? "no credits" : null };
                r.NotesByApiName["A0"] = "[a.com] earlier note"; // found in an earlier run, so not new
                if (NewFromTargeted > 0) r.NotesByApiName["A1"] = "[b.com] fresh note";
                return Task.FromResult(r);
            }
        }

        private sealed class OneGameHost : IEstimationHost
        {
            public EstimationSourceGame Game; public int Writes;
            public IReadOnlyList<Guid> GetGameIds() => new[] { Game.PlayniteGameId };
            public EstimationSourceGame LoadSource(Guid id) => Game;
            public int WriteRows(Guid id, string model, IReadOnlyList<PlayniteAchievements.Services.Database.EstimatedValueRow> rows) { Writes++; return rows.Count; }
            public long ClearAll() => 0;
        }

        private static EstimationSourceGame TwoAchievementGame() => new EstimationSourceGame
        {
            PlayniteGameId = Guid.NewGuid(), Title = "Star Quest",
            Achievements = new[] { "Moon Walker", "Void Slayer" }.Select((n, i) => new EstimationSourceAchievement { ApiName = "A" + i, DisplayName = n, Description = "Do " + n }).ToList()
        };

        [TestMethod]
        public void Retry_WithNoNewEvidenceMakesNoGeminiRequestAndChangesNothing()
        {
            ResearchNotesCache.Configure(null);
            var game = TwoAchievementGame(); var host = new OneGameHost { Game = game }; var scorer = new CountingScorer();
            var coordinator = new EstimationCoordinator(host, scorer,
                () => new EstimationRunOptions { Model = "m", UseCalibration = false, EvidenceSource = new FixedEvidence(), OnlyRegenerateWithNewEvidence = true });

            var summary = coordinator.RunGameAsync(game.PlayniteGameId, true, CancellationToken.None).Result;

            Assert.AreEqual(EstimationRunStatus.Skipped, summary.Status);
            Assert.AreEqual(0, scorer.Calls, "no Gemini request");
            Assert.AreEqual(0, host.Writes, "existing values are left exactly as they are");
            Assert.IsTrue(summary.Message.Contains("No new evidence"), summary.Message);
            ResearchNotesCache.Configure(null);
        }

        [TestMethod]
        public void Retry_WhenSearchIsUnavailableAlsoChangesNothing()
        {
            ResearchNotesCache.Configure(null);
            var game = TwoAchievementGame(); var host = new OneGameHost { Game = game }; var scorer = new CountingScorer();
            var coordinator = new EstimationCoordinator(host, scorer,
                () => new EstimationRunOptions { Model = "m", UseCalibration = false, EvidenceSource = new FixedEvidence { Unavailable = true }, OnlyRegenerateWithNewEvidence = true });

            var summary = coordinator.RunGameAsync(game.PlayniteGameId, true, CancellationToken.None).Result;

            Assert.AreEqual(EstimationRunStatus.Skipped, summary.Status);
            Assert.AreEqual(0, scorer.Calls);
            Assert.AreEqual(0, host.Writes);
            ResearchNotesCache.Configure(null);
        }

        [TestMethod]
        public void Retry_WithNewEvidenceRegeneratesAsUsual()
        {
            ResearchNotesCache.Configure(null);
            var game = TwoAchievementGame(); var host = new OneGameHost { Game = game }; var scorer = new CountingScorer();
            var coordinator = new EstimationCoordinator(host, scorer,
                () => new EstimationRunOptions { Model = "m", UseCalibration = false, EvidenceSource = new FixedEvidence { NewFromTargeted = 1 }, OnlyRegenerateWithNewEvidence = true });

            var summary = coordinator.RunGameAsync(game.PlayniteGameId, true, CancellationToken.None).Result;

            Assert.AreEqual(EstimationRunStatus.Generated, summary.Status);
            Assert.IsTrue(scorer.Calls >= 1);
            Assert.AreEqual(1, host.Writes);
            Assert.AreEqual(2, summary.EvidenceFound);
            ResearchNotesCache.Configure(null);
        }
    }
}
