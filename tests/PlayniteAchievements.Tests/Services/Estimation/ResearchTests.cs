using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Services.Estimation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation.Tests
{
    [TestClass]
    public class ResearchTests
    {
        private static string Id(int index) => "a" + (index + 1).ToString("D4");

        private static EstimationGameContext Context(int count) => new EstimationGameContext
        {
            Title = "Test Game", ReleaseYear = 2020, Genre = "RPG", TargetPlatform = "psn",
            Achievements = Enumerable.Range(0, count)
                .Select(i => new PublicAchievement { ApiName = "ACH_" + i, DisplayName = "Name " + i, Description = "Desc " + i, RarityPercent = 12.5 })
                .ToList()
        };

        // ---------------------------------------------------------------- parser

        [TestMethod]
        public void Parser_ReadsLinesInSeveralFormatsAndIgnoresTheRest()
        {
            var map = new Dictionary<string, string> { ["a0001"] = "X1", ["a0002"] = "X2", ["a0003"] = "X3", ["a0004"] = "X4", ["a0005"] = "X5" };
            var text = string.Join("\n", new[]
            {
                "Here is my research:",
                "a0001 | Defeat the final boss in the last area. | Hard, about 20 tries. | boss",
                "- a0002 | Collect all 50 shards across the map. | Long, a grind. | collectible",
                "**a0003** | Finish the tutorial level. | Trivial. | tutorial",
                "a0004 | no sources found, but it sounds like a long grind | grind",
                "a0099 | An id that does not exist in the request at all. | n/a | n/a",
                "a0005 | short"
            });

            var notes = ResearchResponseParser.Parse(text, map);

            CollectionAssert.AreEquivalent(new[] { "a0001", "a0002", "a0003" }, notes.Keys.ToList());
            Assert.IsTrue(notes["a0001"].Contains("final boss"));
        }

        [TestMethod]
        public void Parser_CapsLengthRemovesCredentialsAndKeepsTheFirstLinePerId()
        {
            var map = new Dictionary<string, string> { ["a0001"] = "X1" };
            var text = "a0001 | " + new string('x', 2000) + "\n" +
                       "a0001 | A second, different line for the same id that must be ignored. | easy | tutorial";

            var note = ResearchResponseParser.Parse(text, map)["a0001"];
            Assert.IsTrue(note.Length <= ResearchResponseParser.MaxNoteLength + 1);

            var leaky = ResearchResponseParser.Parse("a0001 | Contact someone@example.com or use token=abcdef123 to unlock it. | easy | optional", map)["a0001"];
            Assert.IsFalse(leaky.Contains("someone@example.com") || leaky.Contains("abcdef123"));
        }

        [TestMethod]
        public void Parser_EmptyOrNullGivesNoNotes()
        {
            Assert.AreEqual(0, ResearchResponseParser.Parse(null, new Dictionary<string, string>()).Count);
            Assert.AreEqual(0, ResearchResponseParser.Parse("nothing useful here", new Dictionary<string, string> { ["a0001"] = "x" }).Count);
        }

        // ---------------------------------------------------------------- requests

        [TestMethod]
        public void BuildResearch_UsesTheSameIdsBatchesAndLeavesRarityOut()
        {
            var context = Context(60);
            var research = EstimationRequestBuilder.BuildResearch(context, 25);
            var scoring = EstimationRequestBuilder.Build(context, 100);

            Assert.AreEqual(3, research.Count);
            Assert.AreEqual(25, research[0].IdMap.Count);
            Assert.AreEqual(10, research[2].IdMap.Count);
            Assert.AreEqual(scoring[0].IdMap["a0030"], research[1].IdMap["a0030"], "an achievement keeps its id across both steps");

            Assert.IsFalse(research.Any(r => r.Json.Contains("rarityPercent")), "rarity must not colour the research");
            Assert.IsTrue(scoring[0].Json.Contains("rarityPercent"));
            Assert.IsTrue(research.All(r => PrivacyFilter.UnexpectedKeys(r.Json).Count == 0));
            Assert.IsFalse(research.Any(r => r.Json.Contains("ACH_")), "real api names are never sent");
            Assert.AreEqual("research_achievements", (string)JObject.Parse(research[0].Json)["task"]);
        }

        [TestMethod]
        public void BuildResearch_CanTargetOnlySomeAchievementsForARetry()
        {
            var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ACH_3", "ACH_40" };
            var requests = EstimationRequestBuilder.BuildResearch(Context(60), 25, only);

            Assert.AreEqual(1, requests.Count);
            CollectionAssert.AreEquivalent(new[] { "a0004", "a0041" }, requests[0].IdMap.Keys.ToList());
        }

        [TestMethod]
        public void Build_AttachesNotesToTheirAchievementsOnly()
        {
            var notes = new Dictionary<string, string> { ["a0002"] = "Needs the true ending route." };
            var request = EstimationRequestBuilder.Build(Context(3), 100, notes)[0];
            var achievements = (JArray)JObject.Parse(request.Json)["achievements"];

            Assert.IsNull(achievements[0]["notes"]);
            Assert.AreEqual("Needs the true ending route.", (string)achievements[1]["notes"]);
            Assert.AreEqual(0, PrivacyFilter.UnexpectedKeys(request.Json).Count);
        }

        // ---------------------------------------------------------------- the scoring flow

        private sealed class FakeScorer : IEstimationLlmClient
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
                        ["id"] = id, ["difficulty"] = 3, ["type"] = "story_milestone", ["phase"] = "mid", ["capstone"] = false, ["confidence"] = 0.9
                    }))
                };
                return Task.FromResult(LlmCallResult.Ok(answer.ToString()));
            }
        }

        private sealed class FakeResearcher : IEstimationResearchClient
        {
            public readonly List<string> Requests = new List<string>();
            public Func<IList<string>, string> Skip = ids => null;        // returns an id to leave out
            public int FailFromCall = int.MaxValue;
            public int QueriesPerCall = 6;

            public Task<LlmCallResult> ResearchAsync(string requestJson, CancellationToken token)
            {
                Requests.Add(requestJson);
                if (Requests.Count >= FailFromCall) return Task.FromResult(LlmCallResult.Fail(LlmFailureKind.Transient, "rate limit"));

                var ids = ((JArray)JObject.Parse(requestJson)["achievements"]).Select(a => (string)a["id"]).ToList();
                var skip = Skip(ids);
                var text = string.Join("\n", ids.Where(id => id != skip).Select(id => id + " | Requires clearing the stage named in the description. | Moderate effort. | story milestone"));
                var result = LlmCallResult.Ok(text);
                result.SearchQueries = QueriesPerCall;
                result.Sources = 9;
                return Task.FromResult(result);
            }
        }

        [TestMethod]
        public void Scoring_ResearchesEveryAchievementThenScoresWithTheNotes()
        {
            var scorer = new FakeScorer(); var researcher = new FakeResearcher();
            var outcome = LlmScoringService.ScoreAsync(scorer, Context(60), CancellationToken.None, 100, researcher, 25).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(3, researcher.Requests.Count, "60 achievements in batches of 25");
            Assert.AreEqual(60, outcome.ResearchedCount);
            Assert.AreEqual(18, outcome.SearchQueryCount);
            Assert.AreEqual(27, outcome.SourceCount);
            Assert.IsTrue(outcome.Scores.All(s => s.Researched && s.Confidence > 0.5));

            var scored = (JArray)JObject.Parse(scorer.Requests[0])["achievements"];
            Assert.IsTrue(scored.All(a => !string.IsNullOrWhiteSpace((string)a["notes"])), "every achievement carries its research");
            Assert.IsTrue(outcome.Notes[0].StartsWith("Researched 60 of 60"));
        }

        [TestMethod]
        public void Scoring_RetriesAnAchievementTheModelSkipped()
        {
            var researcher = new FakeResearcher();
            var skipped = false;
            researcher.Skip = ids => { if (skipped) return null; skipped = true; return ids[2]; };

            var outcome = LlmScoringService.ScoreAsync(new FakeScorer(), Context(10), CancellationToken.None, 100, researcher, 25).Result;

            Assert.AreEqual(2, researcher.Requests.Count, "one batch, then one retry for the skipped achievement");
            Assert.AreEqual(1, ((JArray)JObject.Parse(researcher.Requests[1])["achievements"]).Count);
            Assert.AreEqual(10, outcome.ResearchedCount);
        }

        [TestMethod]
        public void Scoring_AnAchievementThatStaysUnresearchedIsTrustedLess()
        {
            var researcher = new FakeResearcher { Skip = ids => ids[0] };   // skipped on the batch and again on the retry

            var outcome = LlmScoringService.ScoreAsync(new FakeScorer(), Context(10), CancellationToken.None, 100, researcher, 25).Result;

            var unresearched = outcome.Scores.Single(s => !s.Researched);
            Assert.AreEqual("ACH_0", unresearched.ApiName);
            Assert.IsTrue(unresearched.Confidence <= LlmScoringService.UnresearchedConfidenceCap);
            Assert.AreEqual(9, outcome.ResearchedCount);
        }

        [TestMethod]
        public void Scoring_AFailedResearchCallGeneratesNothing()
        {
            var researcher = new FakeResearcher { FailFromCall = 2 };
            var scorer = new FakeScorer();

            var outcome = LlmScoringService.ScoreAsync(scorer, Context(60), CancellationToken.None, 100, researcher, 25).Result;

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual("rate limit", outcome.Error);
            Assert.AreEqual(0, outcome.Scores.Count);
            Assert.AreEqual(0, scorer.Requests.Count, "scoring is never attempted after a research failure");
        }

        [TestMethod]
        public void Scoring_WarnsWhenNoWebSearchesWereReported()
        {
            var researcher = new FakeResearcher { QueriesPerCall = 0 };
            var outcome = LlmScoringService.ScoreAsync(new FakeScorer(), Context(5), CancellationToken.None, 100, researcher, 25).Result;

            Assert.IsTrue(outcome.Notes.Any(n => n.Contains("no web searches")));
        }

        [TestMethod]
        public void Scoring_WithoutAResearchClientBehavesAsBefore()
        {
            var outcome = LlmScoringService.ScoreAsync(new FakeScorer(), Context(5), CancellationToken.None).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.IsTrue(outcome.Scores.All(s => !s.Researched && s.Confidence > 0.5), "nothing is penalised when research is simply off");
        }

        // ---------------------------------------------------------------- the runner's switch

        private sealed class BothClients : IEstimationLlmClient, IEstimationResearchClient
        {
            public int ResearchCalls;
            private readonly FakeScorer _scorer = new FakeScorer();
            private readonly FakeResearcher _researcher = new FakeResearcher();
            public Task<LlmCallResult> GenerateAsync(string json, CancellationToken t) => _scorer.GenerateAsync(json, t);
            public Task<LlmCallResult> ResearchAsync(string json, CancellationToken t) { ResearchCalls++; return _researcher.ResearchAsync(json, t); }
        }

        private static EstimationSourceGame Game(int count) => new EstimationSourceGame
        {
            PlayniteGameId = Guid.NewGuid(), Title = "T",
            Achievements = Enumerable.Range(0, count).Select(i => new EstimationSourceAchievement { ApiName = "A" + i, DisplayName = "n" + i, Description = "d" + i }).ToList()
        };

        [TestMethod]
        public void Runner_ResearchesOnlyWhenSwitchedOnAndReportsItInTheMessage()
        {
            var client = new BothClients();
            var on = EstimationRunner.RunAsync(Game(30), client, new EstimationRunOptions { UseResearch = true }, CancellationToken.None).Result;
            Assert.IsTrue(client.ResearchCalls >= 2);
            Assert.IsTrue(on.Message.Contains("Researched 30 of 30"));

            var offClient = new BothClients();
            EstimationRunner.RunAsync(Game(30), offClient, new EstimationRunOptions { UseResearch = false }, CancellationToken.None).Wait();
            Assert.AreEqual(0, offClient.ResearchCalls);
        }

        // ---------------------------------------------------------------- the Gemini request

        private sealed class CaptureHandler : HttpMessageHandler
        {
            public readonly List<string> Bodies = new List<string>();
            public readonly Queue<string> Replies = new Queue<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Replies.Dequeue()) };
            }
        }

        [TestMethod]
        public void Gemini_ResearchTurnsOnGoogleSearchAndReadsTheSearchCounts()
        {
            var handler = new CaptureHandler();
            handler.Replies.Enqueue(new JObject
            {
                ["candidates"] = new JArray(new JObject
                {
                    ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = "a0001 | Does a thing in the game. | moderate | boss" }) },
                    ["finishReason"] = "STOP",
                    ["groundingMetadata"] = new JObject
                    {
                        ["webSearchQueries"] = new JArray("q1", "q2", "q3"),
                        ["groundingChunks"] = new JArray(new JObject(), new JObject())
                    }
                })
            }.ToString());
            var log = new List<string>();
            var client = new GeminiClient(new HttpClient(handler), new GeminiClientOptions { ApiKey = () => "KEY" }, log.Add, (s, t) => Task.FromResult(0));

            var result = client.ResearchAsync(EstimationRequestBuilder.BuildResearch(Context(3), 25)[0].Json, CancellationToken.None).Result;

            Assert.IsTrue(result.Success);
            Assert.AreEqual(3, result.SearchQueries);
            Assert.AreEqual(2, result.Sources);

            var body = JObject.Parse(handler.Bodies[0]);
            Assert.IsNotNull(body["tools"][0]["google_search"], "the search tool is on");
            Assert.IsNull(body["generationConfig"]["responseMimeType"], "no strict JSON in the research step");
            Assert.IsNull(body["generationConfig"]["responseSchema"]);
            Assert.IsTrue(((string)body["systemInstruction"]["parts"][0]["text"]).Contains("EVERY achievement"));
            Assert.IsTrue(log.Any(l => l.Contains("3 web search")), "the log proves searches ran, with counts only");
            Assert.IsFalse(log.Any(l => l.Contains("KEY")));
        }

        [TestMethod]
        public void Gemini_ScoringRequestsStillUseStrictJsonAndNoSearchTool()
        {
            var handler = new CaptureHandler();
            handler.Replies.Enqueue(new JObject { ["candidates"] = new JArray(new JObject { ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = "{}" }) } }) }.ToString());
            var client = new GeminiClient(new HttpClient(handler), new GeminiClientOptions { ApiKey = () => "KEY" }, null, (s, t) => Task.FromResult(0));

            client.GenerateAsync(EstimationRequestBuilder.Build(Context(3))[0].Json, CancellationToken.None).Wait();

            var body = JObject.Parse(handler.Bodies[0]);
            Assert.IsNull(body["tools"]);
            Assert.AreEqual("application/json", (string)body["generationConfig"]["responseMimeType"]);
        }
    }
}
