using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Estimation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation.Tests
{
    [TestClass]
    public class ResearchReuseTests
    {
        private string _dir;

        [TestInitialize]
        public void Setup()
        {
            ResearchAvailability.ResetAll();
            _dir = Path.Combine(Path.GetTempPath(), "pa-reuse-" + Guid.NewGuid().ToString("N"));
            ResearchNotesCache.Configure(_dir);
        }

        [TestCleanup]
        public void TearDown()
        {
            ResearchNotesCache.Configure(null);
            ResearchAvailability.ResetAll();
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private const string Guide =
            "Star Quest trophy guide. Difficulty: 3/10.\n" +
            "Moon Walker - Visit every moon.\nVoid Slayer - Defeat the final boss on Nightmare.\nSun Chaser - Reach the sun without a map.\n";

        private sealed class Search : ISearchProvider
        {
            public int Calls;
            public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken token)
            {
                Calls++;
                var response = new SearchResponse { Success = true, CreditsUsed = 1 };
                response.Hits.Add(new SearchHit { Url = "https://a.com/star-quest-trophy-guide", Title = "Star Quest trophy guide", Snippet = "s", RawContent = request.IncludeRawContent ? Guide : null });
                return Task.FromResult(response);
            }
        }

        private sealed class Scorer : IEstimationLlmClient
        {
            public int Calls;
            public Task<LlmCallResult> GenerateAsync(string json, CancellationToken t)
            {
                Calls++;
                var ids = ((JArray)JObject.Parse(json)["achievements"]).Select(a => (string)a["id"]).ToList();
                return Task.FromResult(LlmCallResult.Ok(new JObject
                {
                    ["game"] = new JObject { ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new JArray(ids.Select(id => new JObject { ["id"] = id, ["difficulty"] = 3, ["type"] = "boss", ["phase"] = "mid", ["capstone"] = false, ["confidence"] = 0.9 }))
                }.ToString()));
            }
        }

        private sealed class Host : IEstimationHost
        {
            public readonly Dictionary<Guid, EstimationSourceGame> Games = new Dictionary<Guid, EstimationSourceGame>();
            public int Writes;
            public IReadOnlyList<Guid> GetGameIds() => Games.Keys.ToList();
            public EstimationSourceGame LoadSource(Guid id) => Games.TryGetValue(id, out var g) ? g : null;
            public int WriteRows(Guid id, string model, IReadOnlyList<EstimatedValueRow> rows) { Writes++; return rows.Count; }
            public long ClearAll() => 7;
        }

        private static EstimationSourceGame Star()
        {
            var names = new[] { "Moon Walker", "Void Slayer", "Sun Chaser", "Quiet Hour", "Late Arrival", "Odd Job", "Small Victory", "Long Road", "Hidden Door", "Last Light" };
            return new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = "Star Quest", ReleaseYear = 2021,
                Achievements = names.Select((n, i) => new EstimationSourceAchievement { ApiName = "A" + i, DisplayName = n, Description = "Do " + n }).ToList()
            };
        }

        private static (EstimationCoordinator coordinator, Host host, Search search, Scorer scorer) Build(EstimationSourceGame game)
        {
            var host = new Host(); host.Games[game.PlayniteGameId] = game;
            var search = new Search(); var scorer = new Scorer();
            var gatherer = new EvidenceGatherer(search, null, new EvidenceOptions { MonthlyCreditBudget = 0, MaxAchievementQueries = 0 });
            var coordinator = new EstimationCoordinator(host, scorer,
                () => new EstimationRunOptions { Model = "m", UseCalibration = false, EvidenceSource = gatherer });
            return (coordinator, host, search, scorer);
        }

        [TestMethod]
        public void Regenerate_ReusesSavedResearchAndSpendsNoSearchCredits()
        {
            var game = Star();
            var (coordinator, host, search, scorer) = Build(game);

            coordinator.RunGameAsync(game.PlayniteGameId, regenerate: false, cancellationToken: CancellationToken.None).Wait();
            var searchesAfterFirstRun = search.Calls;
            Assert.IsTrue(searchesAfterFirstRun >= 1, "the first run researches");
            Assert.AreEqual(1, host.Writes);

            var again = coordinator.RunGameAsync(game.PlayniteGameId, regenerate: true, cancellationToken: CancellationToken.None).Result;

            Assert.AreEqual(searchesAfterFirstRun, search.Calls, "regenerating spent no extra searches");
            Assert.AreEqual(EstimationRunStatus.Generated, again.Status);
            Assert.AreEqual(2, host.Writes, "the values were still regenerated");
            Assert.AreEqual(2, scorer.Calls, "scoring ran again, on the saved research");
            Assert.IsTrue(again.Message.Contains("Found written evidence for 3 of 10"), "the saved notes were attached: " + again.Message);
        }

        [TestMethod]
        public void Regenerate_StillReusesResearchAfterARestart()
        {
            var game = Star();
            var first = Build(game);
            first.coordinator.RunGameAsync(game.PlayniteGameId, false, CancellationToken.None).Wait();
            var searches = first.search.Calls;

            ResearchNotesCache.Configure(_dir);   // as after restarting Playnite: memory is empty, the files remain
            var second = Build(game);
            second.coordinator.RunGameAsync(game.PlayniteGameId, true, CancellationToken.None).Wait();

            Assert.AreEqual(0, second.search.Calls, "the saved files supplied all the research");
            Assert.IsTrue(searches >= 1);
        }

        [TestMethod]
        public void DeleteAllGeneratedValues_LeavesTheSavedResearchAlone()
        {
            var game = Star();
            var (coordinator, host, search, scorer) = Build(game);
            coordinator.RunGameAsync(game.PlayniteGameId, false, CancellationToken.None).Wait();
            Assert.AreEqual(1, ResearchNotesCache.SavedGameCount());

            Assert.AreEqual(7L, coordinator.ClearAll());

            Assert.AreEqual(1, ResearchNotesCache.SavedGameCount(), "deleting generated values does not delete research");
            var before = search.Calls;
            coordinator.RunGameAsync(game.PlayniteGameId, false, CancellationToken.None).Wait();
            Assert.AreEqual(before, search.Calls, "so generating again is free");
        }

        [TestMethod]
        public void DeleteSavedResearch_RemovesTheFilesAndTheNextRunSearchesAgain()
        {
            var game = Star();
            var (coordinator, host, search, scorer) = Build(game);
            coordinator.RunGameAsync(game.PlayniteGameId, false, CancellationToken.None).Wait();
            var files = Directory.GetFiles(_dir, "*.json");
            Assert.AreEqual(1, files.Length);

            ResearchNotesCache.ClearAll();

            Assert.AreEqual(0, Directory.GetFiles(_dir, "*.json").Length);
            Assert.AreEqual(0, ResearchNotesCache.SavedGameCount());

            var before = search.Calls;
            coordinator.RunGameAsync(game.PlayniteGameId, true, CancellationToken.None).Wait();
            Assert.IsTrue(search.Calls > before, "with the research deleted, it is researched from scratch");
        }

        [TestMethod]
        public void SavedGameCount_CountsGamesInMemoryAndOnDisk()
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid();
            ResearchNotesCache.For(a)["x"] = "note"; ResearchNotesCache.Save(a);
            ResearchNotesCache.For(b)["y"] = "note";            // in memory, not yet saved
            Assert.AreEqual(2, ResearchNotesCache.SavedGameCount());

            ResearchNotesCache.Configure(_dir);                  // memory dropped; only the saved one remains
            Assert.AreEqual(1, ResearchNotesCache.SavedGameCount());
        }
    }
}
