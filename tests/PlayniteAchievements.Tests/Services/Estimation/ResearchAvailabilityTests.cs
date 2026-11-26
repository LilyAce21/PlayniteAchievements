using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Services.Database;
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
    public class ResearchAvailabilityTests
    {
        private const string Key = "AIzaSyAvailabilityKey0123456789abcdefgh";

        private sealed class Http : HttpMessageHandler
        {
            public readonly Queue<Func<HttpResponseMessage>> Responses = new Queue<Func<HttpResponseMessage>>();
            public readonly List<string> Urls = new List<string>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Urls.Add(request.RequestUri.ToString());
                if (Responses.Count == 0) throw new InvalidOperationException("unexpected request to " + request.RequestUri);
                return Task.FromResult(Responses.Dequeue()());
            }
        }

        private sealed class Rig
        {
            public readonly Http Handler = new Http();
            public readonly List<TimeSpan> Waits = new List<TimeSpan>();
            public readonly List<string> Log = new List<string>();
            public DateTime Now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            public readonly string Model = "model-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            public GeminiClientOptions Options;

            public Rig() { Options = new GeminiClientOptions { ApiKey = () => Key, Model = Model, MinRequestInterval = TimeSpan.FromSeconds(1), ResearchMinRequestInterval = TimeSpan.FromSeconds(1) }; }

            public GeminiClient Client() => new GeminiClient(new HttpClient(Handler), Options, Log.Add,
                (span, token) => { Waits.Add(span); Now += span; return Task.FromResult(0); }, () => Now);
        }

        [TestInitialize]
        public void Reset() { ResearchAvailability.ResetAll(); ResearchAvailability.UtcNow = () => DateTime.UtcNow; }

        [TestCleanup]
        public void Restore() { ResearchAvailability.ResetAll(); ResearchAvailability.UtcNow = () => DateTime.UtcNow; }

        private static string ResearchRequest() => EstimationRequestBuilder.BuildResearch(new EstimationGameContext
        {
            Title = "T", Achievements = new List<PublicAchievement> { new PublicAchievement { ApiName = "a", DisplayName = "n", Description = "d" } }
        }, 25)[0].Json;

        private static string PlainRequest() => EstimationRequestBuilder.Build(new EstimationGameContext
        {
            Title = "T", TargetPlatform = "psn", Achievements = new List<PublicAchievement> { new PublicAchievement { ApiName = "a", DisplayName = "n", Description = "d" } }
        })[0].Json;

        private static HttpResponseMessage Status(int code, string body = "{}") => new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) };

        private static HttpResponseMessage OkText(string text) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JObject { ["candidates"] = new JArray(new JObject { ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = text }) } }) }.ToString())
        };

        private static string QuotaBody(string quotaId, string value) => new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = 429, ["status"] = "RESOURCE_EXHAUSTED", ["message"] = "Quota exceeded.",
                ["details"] = new JArray(new JObject { ["violations"] = new JArray(new JObject { ["quotaId"] = quotaId, ["quotaValue"] = value }) })
            }
        }.ToString();

        // ---------------------------------------------------------------- recognising a refusal

        [TestMethod]
        public void TwoUnexplainedRateLimitsInARowWithNoEarlierSuccessMeanResearchIsUnavailable()
        {
            var rig = new Rig();
            rig.Handler.Responses.Enqueue(() => Status(429));
            rig.Handler.Responses.Enqueue(() => Status(429));
            var client = rig.Client();

            var result = client.ResearchAsync(ResearchRequest(), CancellationToken.None).Result;

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.ResearchUnavailable);
            Assert.AreEqual(2, rig.Handler.Urls.Count, "it gives up after two, instead of six minutes of retries");
            Assert.IsTrue(result.Error.Contains("paid-only") && result.Error.Contains("Research model"));
            Assert.IsTrue(rig.Log.Any(l => l.Contains("web research is unavailable")));

            // Asked again, it does not even send the request.
            var again = client.ResearchAsync(ResearchRequest(), CancellationToken.None).Result;
            Assert.IsTrue(again.ResearchUnavailable);
            Assert.AreEqual(2, rig.Handler.Urls.Count, "no further request while research is known to be refused");
        }

        [TestMethod]
        public void AnExplicitZeroLimitMeansUnavailableAtOnce()
        {
            var rig = new Rig();
            rig.Handler.Responses.Enqueue(() => Status(429, QuotaBody("GenerateContentSearchGroundingFreeTier", "0")));

            var result = rig.Client().ResearchAsync(ResearchRequest(), CancellationToken.None).Result;

            Assert.IsTrue(result.ResearchUnavailable);
            Assert.AreEqual(1, rig.Handler.Urls.Count);
        }

        [TestMethod]
        public void ALimitOfZeroInTheMessageAlsoCounts()
        {
            var rig = new Rig();
            rig.Handler.Responses.Enqueue(() => Status(429, new JObject { ["error"] = new JObject { ["message"] = "Quota exceeded for metric: grounding_requests, limit: 0" } }.ToString()));

            var result = rig.Client().ResearchAsync(ResearchRequest(), CancellationToken.None).Result;

            Assert.IsTrue(result.ResearchUnavailable);
            Assert.AreEqual(1, rig.Handler.Urls.Count);
            Assert.IsTrue(rig.Log.Any(l => l.Contains("Quota exceeded for metric")), "Google's own words reach the log when there are no quota fields");
        }

        [TestMethod]
        public void ANotFoundOrForbiddenAnswerMeansUnavailableAtOnce()
        {
            foreach (var code in new[] { 404, 403 })
            {
                ResearchAvailability.ResetAll();
                var rig = new Rig();
                rig.Handler.Responses.Enqueue(() => Status(code));

                var result = rig.Client().ResearchAsync(ResearchRequest(), CancellationToken.None).Result;

                Assert.IsTrue(result.ResearchUnavailable, "HTTP " + code);
                Assert.IsTrue(result.Error.Contains(rig.Model));
                Assert.AreEqual(1, rig.Handler.Urls.Count);
            }
        }

        [TestMethod]
        public void ARateLimitAfterAnEarlierSuccessIsStillJustARateLimit()
        {
            var rig = new Rig();
            rig.Handler.Responses.Enqueue(() => OkText("a0001 | works | fine | boss"));
            for (var i = 0; i < 3; i++) rig.Handler.Responses.Enqueue(() => Status(429, QuotaBody("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5")));
            var client = rig.Client();

            Assert.IsTrue(client.ResearchAsync(ResearchRequest(), CancellationToken.None).Result.Success);
            var result = client.ResearchAsync(ResearchRequest(), CancellationToken.None).Result;

            Assert.IsFalse(result.Success);
            Assert.IsFalse(result.ResearchUnavailable, "research has worked, so this is a rate limit, not a refusal");
            Assert.AreEqual(4, rig.Handler.Urls.Count, "one success, then the three research attempts");
        }

        [TestMethod]
        public void TheMemoryExpiresAndOneSuccessClearsIt()
        {
            var rig = new Rig();
            ResearchAvailability.UtcNow = () => rig.Now;
            ResearchAvailability.MarkUnavailable(rig.Model, "refused earlier");

            Assert.IsTrue(ResearchAvailability.IsUnavailable(rig.Model, out var reason));
            Assert.AreEqual("refused earlier", reason);

            rig.Now += TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1);
            Assert.IsFalse(ResearchAvailability.IsUnavailable(rig.Model, out _), "it is tried again after a few hours");

            ResearchAvailability.MarkUnavailable(rig.Model, "again");
            ResearchAvailability.MarkWorked(rig.Model);
            Assert.IsFalse(ResearchAvailability.IsUnavailable(rig.Model, out _));
        }

        // ---------------------------------------------------------------- the research model

        [TestMethod]
        public void TheResearchModelIsUsedForResearchOnlyAndTrackedSeparately()
        {
            var rig = new Rig();
            rig.Options.ResearchModel = "gemini-2.5-flash-lite";
            rig.Handler.Responses.Enqueue(() => OkText("a0001 | does the thing | easy | boss"));
            rig.Handler.Responses.Enqueue(() => OkText("{}"));
            var client = rig.Client();

            client.ResearchAsync(ResearchRequest(), CancellationToken.None).Wait();
            client.GenerateAsync(PlainRequest(), CancellationToken.None).Wait();

            Assert.IsTrue(rig.Handler.Urls[0].Contains("/models/gemini-2.5-flash-lite:generateContent"));
            Assert.IsTrue(rig.Handler.Urls[1].Contains("/models/" + rig.Model + ":generateContent"));
        }

        [TestMethod]
        public void RefusalOfTheResearchModelDoesNotBlockTheMainModel()
        {
            var rig = new Rig();
            rig.Options.ResearchModel = "gemini-2.5-flash-lite";
            rig.Handler.Responses.Enqueue(() => Status(404));
            rig.Handler.Responses.Enqueue(() => OkText("{}"));
            var client = rig.Client();

            Assert.IsTrue(client.ResearchAsync(ResearchRequest(), CancellationToken.None).Result.ResearchUnavailable);
            Assert.IsTrue(client.GenerateAsync(PlainRequest(), CancellationToken.None).Result.Success);
        }

        // ---------------------------------------------------------------- pacing is separate

        [TestMethod]
        public void AResearchRateLimitDoesNotSlowPlainRequests()
        {
            var rig = new Rig();
            rig.Handler.Responses.Enqueue(() => OkText("a0001 | works | fine | boss"));        // research has worked before
            rig.Handler.Responses.Enqueue(() => Status(429, QuotaBody("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5")));
            rig.Handler.Responses.Enqueue(() => OkText("a0001 | works | fine | boss"));
            rig.Handler.Responses.Enqueue(() => OkText("{}"));
            var client = rig.Client();

            client.ResearchAsync(ResearchRequest(), CancellationToken.None).Wait();
            client.ResearchAsync(ResearchRequest(), CancellationToken.None).Wait();            // 429 then success: research slows to 15s
            Assert.IsTrue(rig.Log.Any(l => l.Contains("research: slowing down")));

            rig.Waits.Clear();
            client.GenerateAsync(PlainRequest(), CancellationToken.None).Wait();
            Assert.IsFalse(rig.Waits.Any(w => w >= TimeSpan.FromSeconds(10)), "plain scoring keeps its normal pace");
        }

        // ---------------------------------------------------------------- the scoring flow carries on

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

        private sealed class Researcher : IEstimationResearchClient
        {
            public int Calls;
            public int UnavailableFromCall = 1;
            public Task<LlmCallResult> ResearchAsync(string json, CancellationToken t)
            {
                Calls++;
                if (Calls >= UnavailableFromCall)
                {
                    var refused = LlmCallResult.Fail(LlmFailureKind.Transient, "Google Search research is not available for this model on this key.");
                    refused.ResearchUnavailable = true;
                    return Task.FromResult(refused);
                }

                var ids = ((JArray)JObject.Parse(json)["achievements"]).Select(a => (string)a["id"]).ToList();
                var ok = LlmCallResult.Ok(string.Join("\n", ids.Select(id => id + " | Needs the described action in the game. | Moderate. | boss")));
                ok.SearchQueries = 3;
                return Task.FromResult(ok);
            }
        }

        private static EstimationGameContext Game(int count) => new EstimationGameContext
        {
            Title = "T", TargetPlatform = "psn",
            Achievements = Enumerable.Range(0, count).Select(i => new PublicAchievement { ApiName = "A" + i, DisplayName = "n" + i, Description = "d" }).ToList()
        };

        [TestMethod]
        public void Scoring_CarriesOnWithoutResearchWhenItIsUnavailableAndDoesNotPenaliseConfidence()
        {
            var scorer = new Scorer();
            var outcome = LlmScoringService.ScoreAsync(scorer, Game(30), CancellationToken.None, 100, new Researcher(), 25).Result;

            Assert.IsTrue(outcome.Succeeded, "the game is not lost");
            Assert.AreEqual(1, scorer.Calls);
            Assert.AreEqual(30, outcome.Scores.Count);
            Assert.IsTrue(outcome.Scores.All(s => !s.Researched && s.Confidence > 0.8), "nothing was missed, so nothing is penalised");
            Assert.IsNotNull(outcome.ResearchSkippedReason);
            Assert.IsTrue(outcome.Notes.Any(n => n.StartsWith("Scored without web research")));
            Assert.AreEqual(0, outcome.ResearchedCount);
        }

        [TestMethod]
        public void Scoring_IfResearchBecomesUnavailablePartWayTheEarlierNotesAreKept()
        {
            var outcome = LlmScoringService.ScoreAsync(new Scorer(), Game(60), CancellationToken.None, 100, new Researcher { UnavailableFromCall = 2 }, 25).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(25, outcome.ResearchedCount, "the first batch was researched");
            Assert.IsTrue(outcome.Scores.Where(s => !s.Researched).All(s => s.Confidence <= LlmScoringService.UnresearchedConfidenceCap));
            Assert.IsNotNull(outcome.ResearchSkippedReason);
        }

        private sealed class Host : IEstimationHost
        {
            public readonly Dictionary<Guid, EstimationSourceGame> Games = new Dictionary<Guid, EstimationSourceGame>();
            public IReadOnlyList<Guid> GetGameIds() => Games.Keys.ToList();
            public EstimationSourceGame LoadSource(Guid id) => Games.TryGetValue(id, out var g) ? g : null;
            public int WriteRows(Guid id, string model, IReadOnlyList<EstimatedValueRow> rows) => rows.Count;
            public long ClearAll() => 0;
        }

        private sealed class Both : IEstimationLlmClient, IEstimationResearchClient
        {
            private readonly Scorer _scorer = new Scorer();
            private readonly Researcher _researcher = new Researcher();
            public Task<LlmCallResult> GenerateAsync(string json, CancellationToken t) => _scorer.GenerateAsync(json, t);
            public Task<LlmCallResult> ResearchAsync(string json, CancellationToken t) => _researcher.ResearchAsync(json, t);
        }

        private static EstimationSourceGame Source(string title) => new EstimationSourceGame
        {
            PlayniteGameId = Guid.NewGuid(), Title = title,
            Achievements = Enumerable.Range(0, 12).Select(i => new EstimationSourceAchievement { ApiName = title + i, DisplayName = "n" + i, Description = "d" }).ToList()
        };

        [TestMethod]
        public void Library_CountsGamesScoredWithoutResearchAndDoesNotStop()
        {
            var host = new Host();
            foreach (var g in new[] { Source("a"), Source("b"), Source("c") }) host.Games[g.PlayniteGameId] = g;
            var coordinator = new EstimationCoordinator(host, new Both(), () => new EstimationRunOptions { Model = "m", UseCalibration = false });

            var summary = coordinator.RunLibraryAsync(false, null, CancellationToken.None).Result;

            Assert.AreEqual(3, summary.Generated);
            Assert.AreEqual(3, summary.WithoutResearch);
            Assert.IsFalse(summary.StoppedEarly);
            Assert.AreEqual(0, summary.Failed);
            Assert.IsNotNull(summary.ResearchSkippedReason);
            Assert.IsTrue(summary.Results.All(r => r.ResearchSkippedReason != null && r.Status == EstimationRunStatus.Generated));
        }

        [TestMethod]
        public void Regenerate_TriesResearchAfreshByForgettingEarlierRefusals()
        {
            ResearchAvailability.MarkUnavailable("some-model", "refused earlier");
            var host = new Host();
            var game = Source("a"); host.Games[game.PlayniteGameId] = game;
            var coordinator = new EstimationCoordinator(host, new Both(), () => new EstimationRunOptions { Model = "m", UseCalibration = false });

            coordinator.RunGameAsync(game.PlayniteGameId, regenerate: false, cancellationToken: CancellationToken.None).Wait();
            Assert.IsTrue(ResearchAvailability.IsUnavailable("some-model", out _), "a normal run leaves the memory alone");

            coordinator.RunGameAsync(game.PlayniteGameId, regenerate: true, cancellationToken: CancellationToken.None).Wait();
            Assert.IsFalse(ResearchAvailability.IsUnavailable("some-model", out _), "regenerating forgets it");
        }
    }
}
