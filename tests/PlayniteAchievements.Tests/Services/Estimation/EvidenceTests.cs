using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Estimation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Estimation.Tests
{
    [TestClass]
    public class EvidenceTests
    {
        private const string Key = "tvly-dev-TestKey0123456789abcdefghijklmnop";

        [TestInitialize]
        public void Reset() { ResearchAvailability.ResetAll(); ResearchNotesCache.Configure(null); }

        [TestCleanup]
        public void Restore() { ResearchAvailability.ResetAll(); ResearchNotesCache.Configure(null); }

        // ---------------------------------------------------------------- passage matching

        private static CorpusPage Page(string url, string title, string text) => new CorpusPage
        {
            Url = url, Title = title, Text = text, Domain = PassageMatcher.Domain(url), Weight = PassageMatcher.PageWeight(url, title)
        };

        [TestMethod]
        public void Matcher_FindsTheNamedAchievementAndReturnsTheSurroundingText()
        {
            var pages = new[] { Page("https://www.psnprofiles.com/guide/1-game-trophy-guide", "Game Trophy Guide",
                "Intro line.\nSpeed Demon - Finish the final race in under two minutes. This needs a perfect run on hard.\nNext one: Collector.") };

            var found = PassageMatcher.Find(pages, "Speed Demon", "Finish the final race quickly");

            Assert.AreEqual(1, found.Count);
            Assert.IsTrue(found[0].Text.Contains("perfect run on hard"));
            Assert.AreEqual("psnprofiles.com", found[0].Domain);
        }

        [TestMethod]
        public void Matcher_ToleratesPunctuationAndSpacingDifferences()
        {
            var pages = new[] { Page("https://wiki.example.com/achievements", "Achievements", "Never Say Die: survive the last wave with no deaths.") };
            Assert.AreEqual(1, PassageMatcher.Find(pages, "Never-Say-Die!", "Survive").Count);
            Assert.AreEqual(1, PassageMatcher.Find(pages, "never  say   die", "Survive").Count);
        }

        [TestMethod]
        public void Matcher_ShortGenericNamesFindNothingRatherThanSomethingMisleading()
        {
            var pages = new[] { Page("https://a.com/guide", "Guide", "Boss fights are hard. The final boss has three phases.") };
            Assert.AreEqual(0, PassageMatcher.Find(pages, "Boss", "Beat a boss").Count);
            Assert.AreEqual(0, PassageMatcher.Find(pages, "", "x").Count);
        }

        [TestMethod]
        public void Matcher_PrefersGuideLikePagesAndDifferentSites()
        {
            var pages = new[]
            {
                Page("https://forum.example.com/thread/9", "Random thread", "Treasure Hunter is annoying."),
                Page("https://a.com/trophy-guide", "Trophy Guide", "Treasure Hunter: open every chest in the game."),
                Page("https://a.com/achievements-wiki", "Achievements wiki", "Treasure Hunter requires all 40 chests."),
                Page("https://b.com/walkthrough", "Walkthrough", "Treasure Hunter chest list.")
            };

            var found = PassageMatcher.Find(pages, "Treasure Hunter", "Open all chests", 2);

            Assert.AreEqual(2, found.Count);
            Assert.AreNotEqual(found[0].Domain, found[1].Domain, "two sites, not one page twice");
        }

        [TestMethod]
        public void Matcher_Helpers()
        {
            Assert.AreEqual("store.steampowered.com", PassageMatcher.Domain("https://store.steampowered.com/app/1"));
            Assert.AreEqual("example.com", PassageMatcher.Domain("https://www.example.com/x"));
            Assert.AreEqual("hello world 2", PassageMatcher.Normalize("  Hello, WORLD!! 2 "));
            Assert.IsTrue(PassageMatcher.PageWeight("https://x.com/trophy-guide", "Walkthrough") > PassageMatcher.PageWeight("https://x.com/forum", "Chat"));
        }

        // ---------------------------------------------------------------- Tavily client

        private sealed class Http : HttpMessageHandler
        {
            public readonly Queue<Func<HttpResponseMessage>> Responses = new Queue<Func<HttpResponseMessage>>();
            public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();
            public readonly List<string> Bodies = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests.Add(request);
                Bodies.Add(await request.Content.ReadAsStringAsync());
                if (Responses.Count == 0) throw new InvalidOperationException("unexpected request");
                return Responses.Dequeue()();
            }
        }

        private static HttpResponseMessage TavilyOk(params object[][] results) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JObject
            {
                ["results"] = new JArray(results.Select(r => new JObject { ["title"] = (string)r[0], ["url"] = (string)r[1], ["content"] = (string)r[2], ["raw_content"] = r.Length > 3 ? (string)r[3] : null }))
            }.ToString())
        };

        private static HttpResponseMessage Status(int code, string body = "{}") => new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) };

        [TestMethod]
        public void Tavily_SendsTheKeyInAHeaderAndOnlyTheQuery()
        {
            var http = new Http(); var log = new List<string>();
            http.Responses.Enqueue(() => TavilyOk(new object[] { "T", "https://a.com/x", "snippet", "full text" }));
            var client = new TavilySearchClient(new HttpClient(http), () => Key, log.Add, (s, t) => Task.FromResult(0));

            var result = client.SearchAsync(new SearchRequest { Query = "\"Game\" achievements guide", MaxResults = 5, IncludeRawContent = true }, CancellationToken.None).Result;

            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, result.Hits.Count);
            Assert.AreEqual("full text", result.Hits[0].RawContent);
            Assert.AreEqual(1, result.CreditsUsed);

            var request = http.Requests[0];
            Assert.AreEqual("https://api.tavily.com/search", request.RequestUri.ToString());
            Assert.AreEqual("Bearer " + Key, request.Headers.GetValues("Authorization").Single());
            Assert.IsFalse(request.RequestUri.ToString().Contains(Key));
            var body = JObject.Parse(http.Bodies[0]);
            Assert.AreEqual("basic", (string)body["search_depth"]);
            Assert.AreEqual("text", (string)body["include_raw_content"]);
            Assert.AreEqual(false, (bool)body["include_answer"]);
            Assert.IsFalse(http.Bodies[0].Contains(Key), "the key is never in the body");
            Assert.IsFalse(log.Any(l => l.Contains(Key)));
        }

        [TestMethod]
        public void Tavily_RawContentIsOffUnlessAsked()
        {
            var http = new Http();
            http.Responses.Enqueue(() => TavilyOk(new object[] { "T", "https://a.com/x", "snippet" }));
            var client = new TavilySearchClient(new HttpClient(http), () => Key);

            client.SearchAsync(new SearchRequest { Query = "q" }, CancellationToken.None).Wait();

            Assert.AreEqual(false, (bool)JObject.Parse(http.Bodies[0])["include_raw_content"]);
        }

        [TestMethod]
        public void Tavily_RefusalsThatCannotImproveAreUnavailableAndNotRetried()
        {
            foreach (var code in new[] { 401, 403, 432, 433 })
            {
                var http = new Http();
                http.Responses.Enqueue(() => Status(code, new JObject { ["detail"] = new JObject { ["error"] = "problem with key " + Key } }.ToString()));
                var client = new TavilySearchClient(new HttpClient(http), () => Key, null, (s, t) => Task.FromResult(0));

                var result = client.SearchAsync(new SearchRequest { Query = "q" }, CancellationToken.None).Result;

                Assert.AreEqual(SearchFailureKind.Unavailable, result.Failure, "HTTP " + code);
                Assert.AreEqual(1, http.Requests.Count);
                Assert.IsFalse(result.Error.Contains(Key));
            }
        }

        [TestMethod]
        public void Tavily_NoKeyIsUnavailableWithoutAnyRequest()
        {
            var http = new Http();
            var result = new TavilySearchClient(new HttpClient(http), () => "  ").SearchAsync(new SearchRequest { Query = "q" }, CancellationToken.None).Result;

            Assert.AreEqual(SearchFailureKind.Unavailable, result.Failure);
            Assert.AreEqual(0, http.Requests.Count);
        }

        [TestMethod]
        public void Tavily_RateLimitsAreRetriedThenGiveUpAsTransient()
        {
            var http = new Http(); var waits = new List<TimeSpan>();
            for (var i = 0; i < 3; i++) http.Responses.Enqueue(() => Status(429));
            var client = new TavilySearchClient(new HttpClient(http), () => Key, null, (s, t) => { waits.Add(s); return Task.FromResult(0); });

            var result = client.SearchAsync(new SearchRequest { Query = "q" }, CancellationToken.None).Result;

            Assert.AreEqual(SearchFailureKind.Transient, result.Failure);
            Assert.AreEqual(3, http.Requests.Count);
            Assert.AreEqual(2, waits.Count);
        }

        [TestMethod]
        public void Tavily_ABadRequestIsNotRetried()
        {
            var http = new Http();
            http.Responses.Enqueue(() => Status(400));
            var result = new TavilySearchClient(new HttpClient(http), () => Key, null, (s, t) => Task.FromResult(0))
                .SearchAsync(new SearchRequest { Query = "q" }, CancellationToken.None).Result;

            Assert.AreEqual(SearchFailureKind.Transient, result.Failure);
            Assert.AreEqual(1, http.Requests.Count);
        }

        // ---------------------------------------------------------------- the gatherer

        private sealed class FakeSearch : ISearchProvider
        {
            public readonly List<SearchRequest> Requests = new List<SearchRequest>();
            public Func<SearchRequest, SearchResponse> Handler = r => SearchResponse.Fail(SearchFailureKind.Transient, "none");

            public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken token)
            {
                Requests.Add(request);
                return Task.FromResult(Handler(request));
            }
        }

        private static SearchResponse Ok(params SearchHit[] hits)
        {
            var response = new SearchResponse { Success = true, CreditsUsed = 1 };
            response.Hits.AddRange(hits);
            return response;
        }

        private static SearchHit Hit(string url, string title, string snippet, string raw = null) =>
            new SearchHit { Url = url, Title = title, Snippet = snippet, RawContent = raw };

        private static EstimationGameContext Game(params string[] names) => new EstimationGameContext
        {
            Title = "Star Quest", ReleaseYear = 2021, TargetPlatform = "psn",
            Achievements = names.Select((n, i) => new PublicAchievement { ApiName = "A" + i, DisplayName = n, Description = "Do the thing called " + n }).ToList()
        };

        private const string GuideText =
            "Star Quest trophy guide. Difficulty: 4/10. Time to platinum: 25-30 hours. One playthrough needed.\n" +
            "Moon Walker - Visit every moon. Missable if you skip the side quest in chapter 2.\n" +
            "Void Slayer - Defeat the final boss on Nightmare difficulty without dying.\n";

        private EvidenceGatherer Gatherer(FakeSearch search, SearchUsageTracker usage = null, EvidenceOptions options = null) =>
            new EvidenceGatherer(search, usage, options ?? new EvidenceOptions { MonthlyCreditBudget = 0 });

        [TestMethod]
        public void Gatherer_ReadsGuidePagesOnceAndNotesEveryAchievementTheyMention()
        {
            var search = new FakeSearch();
            search.Handler = r => Ok(Hit("https://www.psnprofiles.com/guide/star-quest-trophy-guide", "Star Quest Trophy Guide", "snip", GuideText));

            var result = Gatherer(search).GatherAsync(Game("Moon Walker", "Void Slayer", "Mystery One"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;

            Assert.AreEqual(3, search.Requests.Take(3).Count(r => r.IncludeRawContent), "three game-level searches, each asking for the page text");
            Assert.IsTrue(result.NotesByApiName["A0"].Contains("Missable"));
            Assert.IsTrue(result.NotesByApiName["A1"].Contains("Nightmare"));
            Assert.IsTrue(result.NotesByApiName["A0"].Contains("[psnprofiles.com]"));
            Assert.AreEqual(2, result.FromGuides);
            Assert.IsTrue(result.GameNotes.Contains("4/10") && result.GameNotes.Contains("hours"), "the game's overall difficulty and time are picked up");
        }

        [TestMethod]
        public void Gatherer_DropsPagesAboutADifferentGame()
        {
            var search = new FakeSearch();
            search.Handler = r => Ok(Hit("https://other.com/other-game-guide", "Totally Different Game guide", "x",
                "Moon Walker - this is about a different game entirely, with unrelated advice."));

            var result = Gatherer(search, options: new EvidenceOptions { MonthlyCreditBudget = 0, MaxAchievementQueries = 0 })
                .GatherAsync(Game("Moon Walker"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;

            Assert.AreEqual(0, result.NotesByApiName.Count, "a page that never mentions this game is not evidence");
        }

        [TestMethod]
        public void Gatherer_TargetedSearchesAreLimitedAndGoToTheLikelyImportantFirst()
        {
            var search = new FakeSearch();
            search.Handler = r => r.IncludeRawContent
                ? Ok(Hit("https://a.com/star-quest-guide", "Star Quest guide", "x", "Star Quest guide with nothing useful."))
                : SearchResponse.Fail(SearchFailureKind.Transient, "none");

            var game = Game("Pebble Collector", "True Ending", "Lunch Break", "Final Boss Slayer", "Secret Room");
            game.Achievements[4].IsHidden = true;
            var options = new EvidenceOptions { MonthlyCreditBudget = 0, MaxAchievementQueries = 3, MaxGameQueries = 1 };

            Gatherer(search, options: options).GatherAsync(game, new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Wait();

            var targeted = search.Requests.Where(r => !r.IncludeRawContent).Select(r => r.Query).ToList();
            Assert.AreEqual(3, targeted.Count, "capped at the per-game limit");
            Assert.IsTrue(targeted[0].Contains("Secret Room"), "hidden first");
            Assert.IsTrue(targeted.Any(q => q.Contains("True Ending")) && targeted.Any(q => q.Contains("Final Boss Slayer")));
            Assert.IsFalse(targeted.Any(q => q.Contains("Pebble Collector") || q.Contains("Lunch Break")), "the plain ones wait their turn");
        }

        [TestMethod]
        public void Gatherer_ATargetedSearchThatFindsNothingIsRememberedAndNotRepeated()
        {
            var search = new FakeSearch();
            search.Handler = r => Ok();   // no results at all
            var notes = new Dictionary<string, string>(); var misses = new HashSet<string>();
            var options = new EvidenceOptions { MonthlyCreditBudget = 0, MaxGameQueries = 1, MaxAchievementQueries = 5 };

            Gatherer(search, options: options).GatherAsync(Game("Obscure Thing"), notes, misses, CancellationToken.None).Wait();
            Assert.IsTrue(misses.Contains("A0"));

            search.Requests.Clear();
            var again = Gatherer(search, options: options).GatherAsync(Game("Obscure Thing"), notes, misses, CancellationToken.None).Result;

            Assert.AreEqual(0, search.Requests.Count, "nothing is searched twice, so no credits are wasted");
            Assert.IsFalse(again.Unavailable);
        }

        [TestMethod]
        public void Gatherer_FindingsComeBackFromTheCacheForFree()
        {
            var search = new FakeSearch();
            search.Handler = r => Ok(Hit("https://a.com/star-quest-guide", "Star Quest guide", "x", GuideText));
            var notes = new Dictionary<string, string>(); var misses = new HashSet<string>();
            var game = Game("Moon Walker", "Void Slayer");

            Gatherer(search).GatherAsync(game, notes, misses, CancellationToken.None).Wait();
            search.Requests.Clear();
            var again = Gatherer(search).GatherAsync(game, notes, misses, CancellationToken.None).Result;

            Assert.AreEqual(0, search.Requests.Count);
            Assert.AreEqual(2, again.NotesByApiName.Count);
            Assert.IsNotNull(again.GameNotes);
        }

        [TestMethod]
        public void Gatherer_StopsAtTheMonthlyBudgetAndRecordsUsage()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pa-usage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var usage = new SearchUsageTracker(Path.Combine(dir, "u.json"));
                usage.Record(8);
                var search = new FakeSearch();
                search.Handler = r => Ok(Hit("https://a.com/star-quest-guide", "Star Quest guide", "x", "Star Quest guide."));

                var result = Gatherer(search, usage, new EvidenceOptions { MonthlyCreditBudget = 10, MaxGameQueries = 3, MaxAchievementQueries = 10 })
                    .GatherAsync(Game("One Thing", "Two Thing"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;

                Assert.AreEqual(2, search.Requests.Count, "only two credits were left");
                Assert.AreEqual(10, usage.UsedThisMonth);
                Assert.IsTrue(result.Notes.Any(n => n.Contains("budget")));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Gatherer_AnAlreadyExhaustedBudgetMeansUnavailableWithoutSearching()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pa-usage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var usage = new SearchUsageTracker(Path.Combine(dir, "u.json"));
                usage.Record(900);
                var search = new FakeSearch();

                var result = Gatherer(search, usage, new EvidenceOptions { MonthlyCreditBudget = 900 })
                    .GatherAsync(Game("One"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;

                Assert.IsTrue(result.Unavailable);
                Assert.AreEqual(0, search.Requests.Count);
                Assert.IsTrue(result.Reason.Contains("budget"));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void Gatherer_ARejectedKeyIsUnavailableAndRememberedSoItIsNotHammered()
        {
            var search = new FakeSearch();
            search.Handler = r => SearchResponse.Fail(SearchFailureKind.Unavailable, "Tavily did not accept the API key.");
            var gatherer = Gatherer(search);

            var first = gatherer.GatherAsync(Game("One Thing"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;
            Assert.IsTrue(first.Unavailable);
            Assert.AreEqual(1, search.Requests.Count);

            var second = gatherer.GatherAsync(Game("One Thing"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;
            Assert.IsTrue(second.Unavailable);
            Assert.AreEqual(1, search.Requests.Count, "no further search while it is known to be refused");
        }

        [TestMethod]
        public void Gatherer_IfCreditsRunOutPartWayWhatWasFoundIsKept()
        {
            var calls = 0;
            var search = new FakeSearch();
            search.Handler = r =>
            {
                calls++;
                if (r.IncludeRawContent) return Ok(Hit("https://a.com/star-quest-guide", "Star Quest guide", "x", GuideText));
                return SearchResponse.Fail(SearchFailureKind.Unavailable, "The Tavily plan's credit limit has been reached.");
            };

            var result = Gatherer(search, options: new EvidenceOptions { MonthlyCreditBudget = 0, MaxGameQueries = 1 })
                .GatherAsync(Game("Moon Walker", "Missing One"), new Dictionary<string, string>(), new HashSet<string>(), CancellationToken.None).Result;

            Assert.IsFalse(result.Unavailable, "something was found, so research carries on with it");
            Assert.AreEqual(1, result.NotesByApiName.Count);
            Assert.IsTrue(result.NotesByApiName.ContainsKey("A0"));
        }

        // ---------------------------------------------------------------- usage tracker and cache files

        [TestMethod]
        public void Usage_CountsPerMonthAndSurvivesRestarts()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pa-usage-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(dir, "usage.json");
                var now = new DateTime(2026, 11, 30, 12, 0, 0, DateTimeKind.Utc);
                var tracker = new SearchUsageTracker(path, () => now);
                tracker.Record(5); tracker.Record(3);
                Assert.AreEqual(8, tracker.UsedThisMonth);
                Assert.AreEqual(8, new SearchUsageTracker(path, () => now).UsedThisMonth, "saved to disk");

                now = new DateTime(2026, 12, 1, 0, 1, 0, DateTimeKind.Utc);
                Assert.AreEqual(0, tracker.UsedThisMonth, "a new month starts the count again");
                File.WriteAllText(path, "not json");
                Assert.AreEqual(0, new SearchUsageTracker(path, () => now).UsedThisMonth, "a damaged file is harmless");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [TestMethod]
        public void NotesCache_SavesToDiskLoadsAgainAndClears()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pa-notes-" + Guid.NewGuid().ToString("N"));
            try
            {
                ResearchNotesCache.Configure(dir);
                var game = Guid.NewGuid();
                ResearchNotesCache.For(game)["A0"] = "a note";
                ResearchNotesCache.MissesFor(game).Add("A1");
                ResearchNotesCache.Save(game);
                Assert.IsTrue(File.Exists(Path.Combine(dir, game.ToString("N") + ".json")));

                ResearchNotesCache.Configure(dir);   // as after a restart: memory is empty again
                Assert.AreEqual("a note", ResearchNotesCache.For(game)["A0"]);
                Assert.IsTrue(ResearchNotesCache.MissesFor(game).Contains("A1"));

                ResearchNotesCache.Clear(game);
                Assert.IsFalse(File.Exists(Path.Combine(dir, game.ToString("N") + ".json")));
                Assert.AreEqual(0, ResearchNotesCache.For(game).Count);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        // ---------------------------------------------------------------- the scoring flow

        private sealed class Scorer : IEstimationLlmClient
        {
            public readonly List<string> Requests = new List<string>();
            public Task<LlmCallResult> GenerateAsync(string json, CancellationToken t)
            {
                Requests.Add(json);
                var ids = ((JArray)JObject.Parse(json)["achievements"]).Select(a => (string)a["id"]).ToList();
                return Task.FromResult(LlmCallResult.Ok(new JObject
                {
                    ["game"] = new JObject { ["overallConfidence"] = 0.9, ["uniformSet"] = false },
                    ["achievements"] = new JArray(ids.Select(id => new JObject { ["id"] = id, ["difficulty"] = 3, ["type"] = "boss", ["phase"] = "mid", ["capstone"] = false, ["confidence"] = 0.95 }))
                }.ToString()));
            }
        }

        private sealed class FakeEvidence : IEvidenceSource
        {
            public Func<EvidenceResult> Result = () => new EvidenceResult();
            public Task<EvidenceResult> GatherAsync(EstimationGameContext c, IDictionary<string, string> n, ISet<string> m, CancellationToken t) => Task.FromResult(Result());
        }

        [TestMethod]
        public void Scoring_AttachesEvidenceAndGameNotesAndTrustsUncoveredAchievementsALittleLess()
        {
            var evidence = new FakeEvidence();
            evidence.Result = () =>
            {
                var r = new EvidenceResult { GameNotes = "[psnprofiles.com] Difficulty: 4/10", Searches = 4, Credits = 4, Sources = 3 };
                r.NotesByApiName["A0"] = "[psnprofiles.com] Defeat the final boss on Nightmare.";
                return r;
            };
            var scorer = new Scorer();

            var outcome = LlmScoringService.ScoreAsync(scorer, Game("Moon Walker", "Void Slayer"), CancellationToken.None, 100, null, 25, null, evidence, null).Result;

            var request = JObject.Parse(scorer.Requests[0]);
            Assert.AreEqual("[psnprofiles.com] Difficulty: 4/10", (string)request["game"]["gameNotes"]);
            Assert.IsTrue(((string)request["achievements"][0]["notes"]).Contains("Nightmare"));
            Assert.IsNull(request["achievements"][1]["notes"]);
            Assert.AreEqual(0, PrivacyFilter.UnexpectedKeys(scorer.Requests[0]).Count);

            Assert.AreEqual(1, outcome.ResearchedCount);
            Assert.IsTrue(outcome.Scores.Single(s => s.ApiName == "A0").Confidence > 0.9);
            Assert.AreEqual(LlmScoringService.NoEvidenceConfidenceCap, outcome.Scores.Single(s => s.ApiName == "A1").Confidence, 0.0001);
            Assert.AreEqual(4, outcome.SearchCredits);
            Assert.IsTrue(outcome.Notes[0].StartsWith("Found written evidence for 1 of 2"));
        }

        [TestMethod]
        public void Scoring_UnavailableSearchCarriesOnWithoutPenalty()
        {
            var evidence = new FakeEvidence { Result = () => new EvidenceResult { Unavailable = true, Reason = "No Tavily API key is set." } };
            var scorer = new Scorer();

            var outcome = LlmScoringService.ScoreAsync(scorer, Game("A Thing Here", "Another Thing"), CancellationToken.None, 100, null, 25, null, evidence, null).Result;

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual("No Tavily API key is set.", outcome.ResearchSkippedReason);
            Assert.IsTrue(outcome.Scores.All(s => s.Confidence > 0.9), "nothing was missed, so nothing is penalised");
            Assert.IsNull(JObject.Parse(scorer.Requests[0])["game"]["gameNotes"]);
        }

        [TestMethod]
        public void Runner_UsesTheEvidenceSourceInsteadOfGeminiSearchAndSavesWhatItFound()
        {
            var dir = Path.Combine(Path.GetTempPath(), "pa-run-" + Guid.NewGuid().ToString("N"));
            try
            {
                ResearchNotesCache.Configure(dir);
                var gameId = Guid.NewGuid();
                var source = new EstimationSourceGame
                {
                    PlayniteGameId = gameId, Title = "Star Quest",
                    Achievements = Enumerable.Range(0, 12).Select(i => new EstimationSourceAchievement { ApiName = "A" + i, DisplayName = "Name " + i, Description = "d" }).ToList()
                };
                var evidence = new FakeEvidence();
                evidence.Result = () => { var r = new EvidenceResult(); r.NotesByApiName["A0"] = "a note"; return r; };
                var scorer = new Scorer();

                var result = EstimationRunner.RunAsync(source, scorer, new EstimationRunOptions { UseCalibration = false, EvidenceSource = evidence }, CancellationToken.None).Result;

                Assert.AreEqual(EstimationRunStatus.Generated, result.Status);
                Assert.IsTrue(result.Message.Contains("Found written evidence for 1 of 12"));
                ResearchNotesCache.For(gameId)["A0"] = "a note";
                ResearchNotesCache.Save(gameId);
                Assert.IsTrue(File.Exists(Path.Combine(dir, gameId.ToString("N") + ".json")), "findings are saved so credits are never spent twice");
            }
            finally { ResearchNotesCache.Configure(null); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        // ---------------------------------------------------------------- settings

        [TestMethod]
        public void Config_TavilyKeyIsStoredObfuscatedAndLimitsAreClamped()
        {
            var settings = new PersistedSettings();
            Assert.AreEqual(ResearchProvider.TavilyWebSearch, settings.EstimatedValuesResearchProvider);
            Assert.IsTrue(EstimatedValuesConfig.UsesWebSearchResearch(settings));

            EstimatedValuesConfig.SetTavilyKey(settings, Key);
            Assert.IsFalse(settings.EstimatedValuesTavilyKey.Contains(Key));
            Assert.AreEqual(Key, EstimatedValuesConfig.GetTavilyKey(settings));

            settings.EstimatedValuesSearchCreditsPerMonth = 99999999;
            settings.EstimatedValuesSearchesPerGame = -4;
            var options = EstimatedValuesConfig.ToEvidenceOptions(settings);
            Assert.AreEqual(100000, options.MonthlyCreditBudget);
            Assert.AreEqual(0, options.MaxAchievementQueries);

            settings.EstimatedValuesResearchProvider = ResearchProvider.GoogleSearchViaGemini;
            Assert.IsFalse(EstimatedValuesConfig.UsesWebSearchResearch(settings));
            settings.EstimatedValuesResearchProvider = ResearchProvider.TavilyWebSearch;
            settings.EstimatedValuesResearchEnabled = false;
            Assert.IsFalse(EstimatedValuesConfig.UsesWebSearchResearch(settings));
        }
    }
}
