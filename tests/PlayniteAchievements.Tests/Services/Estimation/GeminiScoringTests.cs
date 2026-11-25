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
    public class GeminiScoringTests
    {
        private const string Key = "AIzaSyTestKeyTestKeyTestKeyTestKey12345";

        private sealed class FakeHandler : HttpMessageHandler
        {
            public readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>();
            public readonly List<string> Urls = new List<string>();
            public readonly List<string> Bodies = new List<string>();
            public readonly List<string> KeyHeaders = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Urls.Add(request.RequestUri.ToString());
                Bodies.Add(await request.Content.ReadAsStringAsync());
                KeyHeaders.Add(request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.First() : null);
                if (Responses.Count == 0) throw new InvalidOperationException("unexpected request");
                return Responses.Dequeue()(request);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new HttpResponseMessage(status) { Content = new StringContent(body) };

        private static HttpResponseMessage Candidate(string text, string finish = "STOP") =>
            Json(HttpStatusCode.OK, new JObject
            {
                ["candidates"] = new JArray(new JObject
                {
                    ["content"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = text }) },
                    ["finishReason"] = finish
                })
            }.ToString());

        private static string ModelAnswer(IEnumerable<string> ids, double overall = 0.9, bool uniform = false, int difficulty = 3)
        {
            return new JObject
            {
                ["game"] = new JObject { ["profile"] = "short_easy", ["overallConfidence"] = overall, ["uniformSet"] = uniform },
                ["achievements"] = new JArray(ids.Select(id => new JObject
                {
                    ["id"] = id, ["difficulty"] = difficulty, ["type"] = "story_milestone", ["phase"] = "mid",
                    ["capstone"] = false, ["seriesId"] = null, ["seriesPosition"] = 0, ["confidence"] = 0.8
                }))
            }.ToString();
        }

        private static EstimationGameContext Context(int count)
        {
            return new EstimationGameContext
            {
                Title = "Test Game", TargetPlatform = "psn",
                Achievements = Enumerable.Range(0, count)
                    .Select(i => new PublicAchievement { ApiName = "ACH_SECRET_" + i, DisplayName = "Step " + i, Description = "Do thing " + i })
                    .ToList()
            };
        }

        private static IEnumerable<string> IdsFor(int from, int count) => Enumerable.Range(from, count).Select(i => "a" + (i + 1).ToString("D4"));

        private sealed class Harness
        {
            public FakeHandler Handler = new FakeHandler();
            public List<TimeSpan> Delays = new List<TimeSpan>();
            public DateTime Now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            public GeminiClientOptions Options = new GeminiClientOptions { ApiKey = () => Key, MinRequestInterval = TimeSpan.FromSeconds(8) };
            public List<string> Log = new List<string>();

            public GeminiClient Client()
            {
                return new GeminiClient(new HttpClient(Handler), Options, Log.Add,
                    (span, token) => { Delays.Add(span); Now += span; return Task.FromResult(0); },
                    () => Now);
            }
        }

        // -------------------------------------------------------------- the client

        [TestMethod]
        public void Client_SendsKeyInHeaderNeverInUrlAndSendsOnlyOpaqueIds()
        {
            var h = new Harness();
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(0, 3))));

            var outcome = LlmScoringService.ScoreAsync(h.Client(), Context(3), CancellationToken.None).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(Key, h.Handler.KeyHeaders[0]);
            Assert.IsFalse(h.Handler.Urls[0].Contains(Key) || h.Handler.Urls[0].Contains("key="));
            Assert.IsTrue(h.Handler.Urls[0].EndsWith("/models/gemini-3.5-flash-lite:generateContent"));
            Assert.IsFalse(h.Handler.Bodies[0].Contains("ACH_SECRET"), "real api names must not be sent");
            Assert.IsFalse(h.Handler.Bodies[0].Contains(Key));
            Assert.IsFalse(h.Log.Any(line => line.Contains(Key)));
            CollectionAssert.AreEqual(new[] { "ACH_SECRET_0", "ACH_SECRET_1", "ACH_SECRET_2" }, outcome.Scores.Select(s => s.ApiName).ToList());
        }

        [TestMethod]
        public void Client_RetriesRateLimitsHonouringRetryAfter()
        {
            var h = new Harness();
            h.Handler.Responses.Enqueue(_ =>
            {
                var response = Json((HttpStatusCode)429, "{\"error\":{\"message\":\"quota\"}}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            });
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(0, 2))));

            var result = h.Client().GenerateAsync(EstimationRequestBuilder.Build(Context(2))[0].Json, CancellationToken.None).Result;

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, h.Handler.Urls.Count);
            Assert.IsTrue(h.Delays.Any(d => d >= TimeSpan.FromSeconds(7) && d < TimeSpan.FromSeconds(9)), "Retry-After should set the wait (plus a small margin)");
        }

        [TestMethod]
        public void Client_BacksOffExponentiallyOnServerTroubleThenGivesUpAsTransient()
        {
            var h = new Harness();
            for (var i = 0; i < 6; i++) h.Handler.Responses.Enqueue(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
            var client = h.Client();

            var result = client.GenerateAsync(EstimationRequestBuilder.Build(Context(2))[0].Json, CancellationToken.None).Result;

            Assert.IsFalse(result.Success);
            Assert.AreEqual(LlmFailureKind.Transient, result.Failure);
            Assert.AreEqual(6, h.Handler.Urls.Count);

            // Waits double each attempt (plus under half a second of jitter), up to the cap.
            var first = client.BackoffDelay(1, null);
            var second = client.BackoffDelay(2, null);
            var third = client.BackoffDelay(3, null);
            Assert.IsTrue(first >= TimeSpan.FromSeconds(2) && first < TimeSpan.FromSeconds(2.6));
            Assert.IsTrue(second >= TimeSpan.FromSeconds(4) && second < TimeSpan.FromSeconds(4.6));
            Assert.IsTrue(third >= TimeSpan.FromSeconds(8) && third < TimeSpan.FromSeconds(8.6));
            Assert.IsTrue(client.BackoffDelay(20, null) <= TimeSpan.FromSeconds(120));
        }

        [TestMethod]
        public void Client_DoesNotRetryABadKeyAndNeverEchoesIt()
        {
            var h = new Harness();
            h.Handler.Responses.Enqueue(_ => Json(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"API key not valid: " + Key + "\"}}"));

            var result = h.Client().GenerateAsync(EstimationRequestBuilder.Build(Context(2))[0].Json, CancellationToken.None).Result;

            Assert.AreEqual(LlmFailureKind.Permanent, result.Failure);
            Assert.AreEqual(1, h.Handler.Urls.Count);
            Assert.IsFalse(result.Error.Contains(Key));
            Assert.IsTrue(result.Error.Contains("[key]") || !result.Error.Contains("AIza"));
        }

        [TestMethod]
        public void Client_ExplainsAModelThatIsNotAvailable()
        {
            var h = new Harness();
            h.Options.Model = "gemini-2.5-flash";
            h.Handler.Responses.Enqueue(_ => Json(HttpStatusCode.NotFound, "{}"));

            var result = h.Client().GenerateAsync(EstimationRequestBuilder.Build(Context(2))[0].Json, CancellationToken.None).Result;

            Assert.AreEqual(LlmFailureKind.Permanent, result.Failure);
            Assert.IsTrue(result.Error.Contains("gemini-2.5-flash") && result.Error.Contains("gemini-3.5-flash-lite"));
        }

        [TestMethod]
        public void Client_RefusesWithoutAKeyOrWithAnInvalidModelBeforeAnyRequest()
        {
            var h = new Harness();
            h.Options.ApiKey = () => "  ";
            var json = EstimationRequestBuilder.Build(Context(1))[0].Json;
            Assert.AreEqual(LlmFailureKind.Permanent, h.Client().GenerateAsync(json, CancellationToken.None).Result.Failure);

            h.Options.ApiKey = () => Key;
            h.Options.Model = "../other?x=1";
            Assert.AreEqual(LlmFailureKind.Permanent, h.Client().GenerateAsync(json, CancellationToken.None).Result.Failure);
            Assert.AreEqual(0, h.Handler.Urls.Count);
        }

        [TestMethod]
        public void Client_BlocksARequestWithFieldsOutsideTheAllowlistBeforeSending()
        {
            var h = new Harness();
            var result = h.Client().GenerateAsync("{\"task\":\"x\",\"unlockedAt\":\"2025-01-01\",\"npsso\":\"abc\"}", CancellationToken.None).Result;

            Assert.AreEqual(LlmFailureKind.Permanent, result.Failure);
            Assert.AreEqual(0, h.Handler.Urls.Count, "nothing may be sent");
        }

        [TestMethod]
        public void Client_PacesRequestsForFreeTierLimits()
        {
            var h = new Harness();
            h.Options.MinRequestInterval = TimeSpan.FromSeconds(10);
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(0, 1))));
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(0, 1))));
            var client = h.Client();
            var json = EstimationRequestBuilder.Build(Context(1))[0].Json;

            client.GenerateAsync(json, CancellationToken.None).Wait();
            client.GenerateAsync(json, CancellationToken.None).Wait();

            Assert.IsTrue(h.Delays.Any(d => d == TimeSpan.FromSeconds(10)), "second call should wait out the interval");
        }

        // -------------------------------------------------------------- the orchestrator

        [TestMethod]
        public void Scoring_LargeListsAreChunkedAndCombined()
        {
            var h = new Harness();
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(0, 100))));
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(100, 100))));
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(200, 50))));

            var outcome = LlmScoringService.ScoreAsync(h.Client(), Context(250), CancellationToken.None).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual(3, h.Handler.Urls.Count);
            Assert.AreEqual(250, outcome.Scores.Count);
            Assert.AreEqual(0, outcome.FallbackApiNames.Count);
        }

        [TestMethod]
        public void Scoring_AnUnusableAnswerFallsBackToStructureWithLowConfidence()
        {
            var h = new Harness();
            h.Handler.Responses.Enqueue(_ => Candidate("sorry, I cannot help with that"));

            var outcome = LlmScoringService.ScoreAsync(h.Client(), Context(8), CancellationToken.None).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.IsTrue(outcome.UsedFallbackForAll);
            Assert.IsTrue(outcome.Game.OverallConfidence <= 0.25);
            Assert.IsTrue(outcome.Scores.All(s => s.Confidence <= 0.25));
        }

        [TestMethod]
        public void Scoring_MissingAndUnknownIdsAreHandled()
        {
            var h = new Harness();
            var answer = JObject.Parse(ModelAnswer(IdsFor(0, 4)));
            ((JArray)answer["achievements"]).RemoveAt(3);                              // one achievement missing
            ((JArray)answer["achievements"]).Add(new JObject { ["id"] = "a9999", ["difficulty"] = 3 }); // invented id
            h.Handler.Responses.Enqueue(_ => Candidate(answer.ToString()));

            var outcome = LlmScoringService.ScoreAsync(h.Client(), Context(4), CancellationToken.None).Result;

            Assert.AreEqual(4, outcome.Scores.Count);
            CollectionAssert.AreEqual(new[] { "ACH_SECRET_3" }, outcome.FallbackApiNames);
            Assert.IsFalse(outcome.Scores.Any(s => s.ApiName == "a9999"));
        }

        [TestMethod]
        public void Scoring_ATruncatedAnswerIsNotTrustedHalfParsed()
        {
            var h = new Harness();
            h.Handler.Responses.Enqueue(_ => Candidate(ModelAnswer(IdsFor(0, 3)), "MAX_TOKENS"));

            var outcome = LlmScoringService.ScoreAsync(h.Client(), Context(3), CancellationToken.None).Result;

            Assert.IsTrue(outcome.UsedFallbackForAll);
        }

        [TestMethod]
        public void Scoring_ANetworkOrQuotaFailureGeneratesNothing()
        {
            var h = new Harness();
            for (var i = 0; i < 6; i++) h.Handler.Responses.Enqueue(_ => Json((HttpStatusCode)429, "{}"));

            var outcome = LlmScoringService.ScoreAsync(h.Client(), Context(5), CancellationToken.None).Result;

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(0, outcome.Scores.Count);
            Assert.IsTrue(outcome.Error.Contains("rate limit"));
        }

        // -------------------------------------------------------------- parser and fallback

        [TestMethod]
        public void Parser_ClampsAndRepairsInsteadOfTrusting()
        {
            var json = "```json\n" + new JObject
            {
                ["game"] = new JObject { ["overallConfidence"] = 7, ["uniformSet"] = "true" },
                ["achievements"] = new JArray(new JObject
                {
                    ["id"] = "a0001", ["difficulty"] = 9, ["type"] = "Secret", ["phase"] = "LATE",
                    ["capstone"] = "true", ["seriesId"] = "null", ["seriesPosition"] = -4, ["confidence"] = 3
                })
            } + "\n```";

            var parsed = EstimationResponseParser.Parse(json, new Dictionary<string, string> { ["a0001"] = "X" });

            Assert.IsTrue(parsed.IsUsable);
            var score = parsed.Scores.Single();
            Assert.AreEqual(5, score.Difficulty);
            Assert.AreEqual(AchievementKind.TrueEnding, score.Kind);
            Assert.AreEqual(GamePhase.Late, score.Phase);
            Assert.IsTrue(score.IsCapstone);
            Assert.IsNull(score.SeriesId);
            Assert.AreEqual(0, score.SeriesPosition);
            Assert.AreEqual(1.0, score.Confidence);
            Assert.AreEqual(1.0, parsed.Game.OverallConfidence);
            Assert.IsTrue(parsed.Game.IsUniformSet);
        }

        [TestMethod]
        public void Fallback_FindsLaddersFromNumbersInNamesAndMarksAFinalCapstone()
        {
            var list = new List<PublicAchievement>();
            foreach (var n in new[] { 10, 100, 50 }) list.Add(new PublicAchievement { ApiName = "win" + n, DisplayName = "Win " + n + " games", Description = "Win " + n + " matches" });
            for (var i = 0; i < 4; i++) list.Add(new PublicAchievement { ApiName = "x" + i, DisplayName = "Chapter " + (char)('A' + i), Description = "Finish it" });
            list.Add(new PublicAchievement { ApiName = "end", DisplayName = "The End", Description = "Finish the game" });

            var scores = StructureFallback.Score(list).ToDictionary(s => s.ApiName);

            Assert.IsNotNull(scores["win10"].SeriesId);
            Assert.AreEqual(scores["win10"].SeriesId, scores["win100"].SeriesId);
            Assert.AreEqual(1, scores["win10"].SeriesPosition);
            Assert.AreEqual(2, scores["win50"].SeriesPosition);
            Assert.AreEqual(3, scores["win100"].SeriesPosition);
            Assert.IsTrue(scores["end"].IsCapstone);
            Assert.IsTrue(scores.Values.All(s => s.Confidence <= 0.25));
        }

        [TestMethod]
        public void Fallback_ProducesGradesThatStillFollowTheBudget()
        {
            var list = Enumerable.Range(0, 20).Select(i => new PublicAchievement { ApiName = "a" + i, DisplayName = "Thing " + i, Description = "Do it" }).ToList();
            var scores = StructureFallback.Score(list);
            var estimates = TrophyGradeEstimator.Estimate(new TrophyEstimationInput
            {
                Scores = scores,
                Game = new GameScoring { OverallConfidence = StructureFallback.FallbackConfidence }
            });

            var points = estimates.Sum(e => e.Grade == TrophyGrade.Gold ? 90 : e.Grade == TrophyGrade.Silver ? 30 : 15);
            Assert.IsTrue(points >= 940 && points <= 1060, "points were " + points);
        }
    }
}
