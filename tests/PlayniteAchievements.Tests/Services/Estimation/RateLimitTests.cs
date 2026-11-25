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
    public class RateLimitTests
    {
        private const string Key = "AIzaSyRateLimitTestKey0123456789abcdef";

        private sealed class Handler : HttpMessageHandler
        {
            public readonly Queue<Func<HttpResponseMessage>> Responses = new Queue<Func<HttpResponseMessage>>();
            public int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                if (Responses.Count == 0) throw new InvalidOperationException("unexpected request");
                return Task.FromResult(Responses.Dequeue()());
            }
        }

        private sealed class Rig
        {
            public readonly Handler Http = new Handler();
            public readonly List<TimeSpan> Waits = new List<TimeSpan>();
            public readonly List<string> Log = new List<string>();
            public DateTime Now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
            public GeminiClientOptions Options = new GeminiClientOptions { ApiKey = () => Key, MinRequestInterval = TimeSpan.FromSeconds(8), ResearchMinRequestInterval = TimeSpan.FromSeconds(15) };

            public GeminiClient Client() => new GeminiClient(new HttpClient(Http), Options, Log.Add,
                (span, token) => { Waits.Add(span); Now += span; return Task.FromResult(0); }, () => Now);
        }

        private static string Request() => EstimationRequestBuilder.Build(new EstimationGameContext
        {
            Title = "T", TargetPlatform = "psn",
            Achievements = new List<PublicAchievement> { new PublicAchievement { ApiName = "a", DisplayName = "n", Description = "d" } }
        })[0].Json;

        private static HttpResponseMessage Limit(string quotaId, string limit = "5", string retryDelay = "34s", string message = "You exceeded your current quota.")
        {
            var body = new JObject
            {
                ["error"] = new JObject
                {
                    ["code"] = 429, ["status"] = "RESOURCE_EXHAUSTED", ["message"] = message,
                    ["details"] = new JArray(
                        new JObject { ["@type"] = "type.googleapis.com/google.rpc.QuotaFailure", ["violations"] = new JArray(new JObject { ["quotaId"] = quotaId, ["quotaValue"] = limit }) },
                        retryDelay == null ? new JObject() : new JObject { ["@type"] = "type.googleapis.com/google.rpc.RetryInfo", ["retryDelay"] = retryDelay })
                }
            };
            return new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent(body.ToString()) };
        }

        private static HttpResponseMessage Ok() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JObject { ["candidates"] = new JArray(new JObject { ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = "{}" }) } }) }.ToString())
        };

        [TestMethod]
        public void RateLimit_WaitsForTheDelayGeminiNamesInTheBody()
        {
            var rig = new Rig();
            rig.Http.Responses.Enqueue(() => Limit("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5", "34s"));
            rig.Http.Responses.Enqueue(Ok);

            var result = rig.Client().GenerateAsync(Request(), CancellationToken.None).Result;

            Assert.IsTrue(result.Success);
            Assert.IsTrue(rig.Waits.Any(w => w >= TimeSpan.FromSeconds(34) && w < TimeSpan.FromSeconds(36)),
                "waited " + string.Join(",", rig.Waits) + " instead of the 34 seconds Gemini asked for");
        }

        [TestMethod]
        public void RateLimit_FallsBackToTheRetryInMessageWhenThereIsNoDelayField()
        {
            var rig = new Rig();
            rig.Http.Responses.Enqueue(() => Limit("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5", null, "Quota exceeded. Please retry in 12.5s."));
            rig.Http.Responses.Enqueue(Ok);

            rig.Client().GenerateAsync(Request(), CancellationToken.None).Wait();

            Assert.IsTrue(rig.Waits.Any(w => w >= TimeSpan.FromSeconds(12.5) && w < TimeSpan.FromSeconds(15)));
        }

        [TestMethod]
        public void RateLimit_ADailyQuotaIsNotRetriedAndSaysSo()
        {
            var rig = new Rig();
            rig.Http.Responses.Enqueue(() => Limit("GenerateRequestsPerDayPerProjectPerModel-FreeTier", "20", "3600s"));

            var result = rig.Client().GenerateAsync(Request(), CancellationToken.None).Result;

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.DailyQuotaExhausted);
            Assert.AreEqual(1, rig.Http.Calls, "retrying a used-up daily quota cannot help");
            Assert.IsTrue(result.Error.Contains("daily") && result.Error.Contains("tomorrow") && result.Error.Contains("Research each achievement"));
        }

        [TestMethod]
        public void RateLimit_TheLogNamesTheQuotaAndTheLimitWithoutTheKey()
        {
            var rig = new Rig();
            rig.Http.Responses.Enqueue(() => Limit("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5", "10s", "key " + Key + " exceeded"));
            rig.Http.Responses.Enqueue(Ok);

            rig.Client().GenerateAsync(Request(), CancellationToken.None).Wait();

            Assert.IsTrue(rig.Log.Any(l => l.Contains("GenerateRequestsPerMinutePerProjectPerModel-FreeTier") && l.Contains("limit 5") && l.Contains("retry in 10s")));
            Assert.IsFalse(rig.Log.Any(l => l.Contains(Key)));
        }

        [TestMethod]
        public void RateLimit_ThePerMinuteMessageSuggestsWhatToChange()
        {
            var rig = new Rig();
            for (var i = 0; i < 6; i++) rig.Http.Responses.Enqueue(() => Limit("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5", "1s"));

            var result = rig.Client().GenerateAsync(Request(), CancellationToken.None).Result;

            Assert.AreEqual(LlmFailureKind.Transient, result.Failure);
            Assert.AreEqual(6, rig.Http.Calls);
            Assert.IsTrue(result.Error.Contains("Seconds between requests") && result.Error.Contains("limit 5"));
        }

        [TestMethod]
        public void Pacing_SlowsDownAfterARateLimitThenEasesOffAgain()
        {
            var rig = new Rig();
            rig.Options.MinRequestInterval = TimeSpan.FromSeconds(1);
            rig.Http.Responses.Enqueue(() => Limit("GenerateRequestsPerMinutePerProjectPerModel-FreeTier", "5", "1s"));
            rig.Http.Responses.Enqueue(Ok);
            for (var i = 0; i < 12; i++) rig.Http.Responses.Enqueue(Ok);
            var client = rig.Client();

            client.GenerateAsync(Request(), CancellationToken.None).Wait();          // 429 then success: slows to 15s
            Assert.IsTrue(rig.Log.Any(l => l.Contains("slowing down to one request every 15s")));

            rig.Waits.Clear();
            client.GenerateAsync(Request(), CancellationToken.None).Wait();
            Assert.IsTrue(rig.Waits.Any(w => w >= TimeSpan.FromSeconds(8)), "the next request is spaced out, not fired at the normal gap");

            for (var i = 0; i < 8; i++) client.GenerateAsync(Request(), CancellationToken.None).Wait();
            Assert.IsTrue(rig.Log.Any(l => l.Contains("back to normal pacing")), "it eases off as requests succeed");
        }

        [TestMethod]
        public void Pacing_ResearchRequestsAreSpacedFurtherApartThanPlainOnes()
        {
            var rig = new Rig();
            rig.Options.MinRequestInterval = TimeSpan.FromSeconds(8);
            rig.Options.ResearchMinRequestInterval = TimeSpan.FromSeconds(20);
            for (var i = 0; i < 4; i++) rig.Http.Responses.Enqueue(Ok);
            var client = rig.Client();
            var research = EstimationRequestBuilder.BuildResearch(new EstimationGameContext
            {
                Title = "T", Achievements = new List<PublicAchievement> { new PublicAchievement { ApiName = "a", DisplayName = "n", Description = "d" } }
            }, 25)[0].Json;

            client.ResearchAsync(research, CancellationToken.None).Wait();
            rig.Waits.Clear();
            client.ResearchAsync(research, CancellationToken.None).Wait();
            Assert.IsTrue(rig.Waits.Contains(TimeSpan.FromSeconds(20)), "research waits the wider interval");

            rig.Waits.Clear();
            client.GenerateAsync(Request(), CancellationToken.None).Wait();
            client.GenerateAsync(Request(), CancellationToken.None).Wait();
            Assert.IsTrue(rig.Waits.Contains(TimeSpan.FromSeconds(8)), "plain requests keep the normal interval");
        }

        // ---------------------------------------------------------------- the session cache

        private sealed class Scorer : IEstimationLlmClient
        {
            public Task<LlmCallResult> GenerateAsync(string json, CancellationToken t)
            {
                var ids = ((JArray)JObject.Parse(json)["achievements"]).Select(a => (string)a["id"]).ToList();
                return Task.FromResult(LlmCallResult.Ok(new JObject
                {
                    ["game"] = new JObject { ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new JArray(ids.Select(id => new JObject { ["id"] = id, ["difficulty"] = 3, ["type"] = "boss", ["phase"] = "mid", ["capstone"] = false, ["confidence"] = 0.9 }))
                }.ToString()));
            }
        }

        private sealed class FlakyResearcher : IEstimationResearchClient
        {
            public int Calls;
            public int FailOnCall = int.MaxValue;
            public readonly List<int> Sizes = new List<int>();

            public Task<LlmCallResult> ResearchAsync(string json, CancellationToken t)
            {
                Calls++;
                var ids = ((JArray)JObject.Parse(json)["achievements"]).Select(a => (string)a["id"]).ToList();
                Sizes.Add(ids.Count);
                if (Calls == FailOnCall) return Task.FromResult(LlmCallResult.Fail(LlmFailureKind.Transient, "rate limit"));
                var result = LlmCallResult.Ok(string.Join("\n", ids.Select(id => id + " | Needs the described action in the game. | Moderate. | boss")));
                result.SearchQueries = 4;
                return Task.FromResult(result);
            }
        }

        private static EstimationGameContext Game(int count) => new EstimationGameContext
        {
            Title = "T", TargetPlatform = "psn",
            Achievements = Enumerable.Range(0, count).Select(i => new PublicAchievement { ApiName = "A" + i, DisplayName = "n" + i, Description = "d" }).ToList()
        };

        [TestMethod]
        public void Cache_AnInterruptedResearchRunDoesNotRepayForFinishedBatches()
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var researcher = new FlakyResearcher { FailOnCall = 3 };

            var first = LlmScoringService.ScoreAsync(new Scorer(), Game(75), CancellationToken.None, 100, researcher, 25, cache).Result;
            Assert.IsFalse(first.Succeeded, "the third batch hit a rate limit");
            Assert.AreEqual(50, cache.Count, "the two finished batches were kept");

            researcher.Calls = 0; researcher.FailOnCall = int.MaxValue; researcher.Sizes.Clear();
            var second = LlmScoringService.ScoreAsync(new Scorer(), Game(75), CancellationToken.None, 100, researcher, 25, cache).Result;

            Assert.IsTrue(second.Succeeded);
            CollectionAssert.AreEqual(new[] { 25 }, researcher.Sizes, "only the unfinished batch is researched again");
            Assert.AreEqual(75, second.ResearchedCount);
        }

        [TestMethod]
        public void Cache_EverythingCachedMeansNoResearchRequestsAtAll()
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var researcher = new FlakyResearcher();
            LlmScoringService.ScoreAsync(new Scorer(), Game(30), CancellationToken.None, 100, researcher, 25, cache).Wait();

            researcher.Calls = 0;
            var again = LlmScoringService.ScoreAsync(new Scorer(), Game(30), CancellationToken.None, 100, researcher, 25, cache).Result;

            Assert.AreEqual(0, researcher.Calls);
            Assert.AreEqual(30, again.ResearchedCount);
        }

        [TestMethod]
        public void Cache_IsPerGameAndClearedOnRegenerate()
        {
            var game = Guid.NewGuid();
            ResearchNotesCache.For(game)["A0"] = "note";
            Assert.AreEqual(1, ResearchNotesCache.For(game).Count);
            Assert.AreEqual(0, ResearchNotesCache.For(Guid.NewGuid()).Count);

            ResearchNotesCache.Clear(game);
            Assert.AreEqual(0, ResearchNotesCache.For(game).Count);
        }
    }
}
