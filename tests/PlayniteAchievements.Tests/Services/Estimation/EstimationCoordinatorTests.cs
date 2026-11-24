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
    public class EstimationCoordinatorTests
    {
        private sealed class FakeHost : IEstimationHost
        {
            public readonly Dictionary<Guid, EstimationSourceGame> Games = new Dictionary<Guid, EstimationSourceGame>();
            public readonly List<Guid> Written = new List<Guid>();
            public long Cleared;

            public IReadOnlyList<Guid> GetGameIds() => Games.Keys.ToList();
            public EstimationSourceGame LoadSource(Guid id) => Games.TryGetValue(id, out var g) ? g : null;
            public int WriteRows(Guid id, string model, IReadOnlyList<EstimatedValueRow> rows) { Written.Add(id); return rows.Count; }
            public long ClearAll() => Cleared;
        }

        private sealed class ScriptedClient : IEstimationLlmClient
        {
            public int Calls;
            public Func<int, LlmCallResult> Failure = _ => null;

            public Task<LlmCallResult> GenerateAsync(string requestJson, CancellationToken token)
            {
                Calls++;
                var failure = Failure(Calls);
                if (failure != null) return Task.FromResult(failure);

                var ids = ((JArray)JObject.Parse(requestJson)["achievements"]).Select(a => (string)a["id"]).ToList();
                var answer = new JObject
                {
                    ["game"] = new JObject { ["profile"] = "short_easy", ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new JArray(ids.Select(id => new JObject
                    {
                        ["id"] = id, ["difficulty"] = 3, ["type"] = "story_milestone", ["phase"] = "mid",
                        ["capstone"] = false, ["seriesId"] = null, ["seriesPosition"] = 0, ["confidence"] = 0.8
                    }))
                };
                return Task.FromResult(LlmCallResult.Ok(answer.ToString()));
            }
        }

        private static EstimationSourceGame Game(string title, int count = 12)
        {
            return new EstimationSourceGame
            {
                PlayniteGameId = Guid.NewGuid(), Title = title,
                Achievements = Enumerable.Range(0, count)
                    .Select(i => new EstimationSourceAchievement { ApiName = title + i, DisplayName = "A" + i, Description = "d" + i })
                    .ToList()
            };
        }

        private static (EstimationCoordinator, FakeHost, ScriptedClient) Build(params EstimationSourceGame[] games)
        {
            var host = new FakeHost();
            foreach (var g in games) host.Games[g.PlayniteGameId] = g;
            var client = new ScriptedClient();
            return (new EstimationCoordinator(host, client, () => new EstimationRunOptions { Model = "m" }), host, client);
        }

        [TestMethod]
        public void Library_GeneratesAndStoresForEveryGame()
        {
            var (coordinator, host, client) = Build(Game("a"), Game("b"), Game("c"));
            var summary = coordinator.RunLibraryAsync(false, null, CancellationToken.None).Result;

            Assert.AreEqual(3, summary.Generated);
            Assert.AreEqual(3, host.Written.Count);
            Assert.AreEqual(3 * 12, summary.ValuesStored); // the fake host counts one row (trophy and Gamerscore) per achievement
            Assert.IsFalse(summary.StoppedEarly);
            Assert.AreEqual(3, client.Calls);
        }

        [TestMethod]
        public void Library_KeepsEarlierResultsUnlessAskedToRegenerate()
        {
            var game = Game("a");
            foreach (var a in game.Achievements) { a.HasEstimatedTrophy = true; a.HasEstimatedPoints = true; }
            var (coordinator, host, client) = Build(game);

            var kept = coordinator.RunLibraryAsync(false, null, CancellationToken.None).Result;
            Assert.AreEqual(1, kept.Skipped);
            Assert.AreEqual(0, client.Calls, "no request is made for a game that is already covered");
            Assert.AreEqual(0, host.Written.Count);

            var regenerated = coordinator.RunLibraryAsync(true, null, CancellationToken.None).Result;
            Assert.AreEqual(1, regenerated.Generated);
            Assert.AreEqual(1, client.Calls);
        }

        [TestMethod]
        public void Library_RegeneratesAWholeKindWhenSomeTargetsAreNew()
        {
            var game = Game("a", 10);
            for (var i = 0; i < 8; i++) game.Achievements[i].HasEstimatedTrophy = true; // two achievements are new
            foreach (var a in game.Achievements) a.HasEstimatedPoints = true;
            var (coordinator, host, client) = Build(game);

            var summary = coordinator.RunLibraryAsync(false, null, CancellationToken.None).Result;

            Assert.AreEqual(1, summary.Generated);
            Assert.AreEqual(10, summary.ValuesStored, "all ten trophy grades are regenerated together; Gamerscore is kept");
        }

        [TestMethod]
        public void Library_StopsEarlyOnAProblemEveryGameWouldShare()
        {
            var (coordinator, host, client) = Build(Game("a"), Game("b"), Game("c"));
            client.Failure = call => call >= 2 ? LlmCallResult.Fail(LlmFailureKind.Transient, "quota used up") : null;

            var summary = coordinator.RunLibraryAsync(false, null, CancellationToken.None).Result;

            Assert.IsTrue(summary.StoppedEarly);
            Assert.AreEqual("quota used up", summary.StopReason);
            Assert.AreEqual(1, summary.Generated, "the finished game is kept");
            Assert.AreEqual(1, summary.Failed);
            Assert.AreEqual(2, client.Calls, "the third game is never tried");
            Assert.AreEqual(1, host.Written.Count);
        }

        [TestMethod]
        public void Library_OneGamesRefusedContentDoesNotStopTheRun()
        {
            var (coordinator, host, client) = Build(Game("a"), Game("b"), Game("c"));
            client.Failure = call => call == 2 ? LlmCallResult.Fail(LlmFailureKind.Content, "blocked") : null;

            var summary = coordinator.RunLibraryAsync(false, null, CancellationToken.None).Result;

            Assert.IsFalse(summary.StoppedEarly);
            Assert.AreEqual(2, summary.Generated);
            Assert.AreEqual(1, summary.Failed);
            Assert.AreEqual(3, client.Calls);
        }

        [TestMethod]
        public void Game_WithoutAchievementDataIsSkipped()
        {
            var (coordinator, host, client) = Build();
            var summary = coordinator.RunGameAsync(Guid.NewGuid(), false, CancellationToken.None).Result;

            Assert.AreEqual(EstimationRunStatus.Skipped, summary.Status);
            Assert.AreEqual(0, client.Calls);
        }

        [TestMethod]
        public void Library_ReportsProgressForEachGame()
        {
            var (coordinator, host, client) = Build(Game("a"), Game("b"));
            var seen = new List<string>();
            var progress = new SyncProgress(s => seen.Add(s.Title));

            coordinator.RunLibraryAsync(false, progress, CancellationToken.None).Wait();

            CollectionAssert.AreEquivalent(new[] { "a", "b" }, seen);
        }

        [TestMethod]
        public void ClearAll_ReturnsTheNumberOfDeletedRows()
        {
            var (coordinator, host, client) = Build();
            host.Cleared = 42;
            Assert.AreEqual(42L, coordinator.ClearAll());
        }

        private sealed class SyncProgress : IProgress<EstimationGameSummary>
        {
            private readonly Action<EstimationGameSummary> _action;
            public SyncProgress(Action<EstimationGameSummary> action) { _action = action; }
            public void Report(EstimationGameSummary value) => _action(value);
        }
    }
}
