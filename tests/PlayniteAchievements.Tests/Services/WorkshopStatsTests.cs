using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Services.Workshop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class WorkshopStatsTests
    {
        [TestMethod]
        public async Task Fetch_ReadsCountsAndOwnLikes_AndAsksForTheVoter()
        {
            var requested = new List<string>();
            var client = new WorkshopStatsClient(() => "https://service.invalid/", null, new FakeHandler(request =>
            {
                requested.Add(request.RequestUri.PathAndQuery);
                return Task.FromResult(Json(
                    "{\"likes\":{\"colors/neon\":3,\"sounds/chime\":0},\"installs\":{\"colors/neon\":11},\"mine\":[\"colors/neon\"]}"));
            }));

            var voter = new string('a', 64);
            var snapshot = await client.FetchAsync(voter, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "/v1/stats?voter=" + voter }, requested);
            Assert.AreEqual(3, snapshot.Likes["colors/neon"]);
            Assert.AreEqual(11, snapshot.Installs["colors/neon"]);
            Assert.IsFalse(snapshot.Likes.ContainsKey("sounds/chime"), "an item nobody liked is not carried");
            Assert.IsTrue(snapshot.Mine.Contains("colors/neon"));
        }

        [TestMethod]
        public async Task Fetch_ReturnsNull_WhenTheServiceFails()
        {
            var client = new WorkshopStatsClient(() => "https://service.invalid", null,
                new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"nope\"}", Encoding.UTF8, "application/json")
                })));

            Assert.IsNull(await client.FetchAsync(null, CancellationToken.None));
        }

        [TestMethod]
        public async Task SetLike_PostsTheItemAndVoter_AndReturnsTheFreshCount()
        {
            JObject sent = null;
            var path = string.Empty;
            var client = new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(async request =>
            {
                path = request.RequestUri.AbsolutePath;
                sent = JObject.Parse(await request.Content.ReadAsStringAsync());
                return Json("{\"itemId\":\"colors/neon\",\"liked\":true,\"count\":4}");
            }));

            var count = await client.SetLikeAsync("colors/neon", new string('A', 64), true, CancellationToken.None);

            Assert.AreEqual(4, count);
            Assert.AreEqual("/v1/likes", path);
            Assert.AreEqual("colors/neon", sent.Value<string>("itemId"));
            Assert.AreEqual(new string('a', 64), sent.Value<string>("voterHash"), "the hash is sent lowercase");
            Assert.IsTrue(sent.Value<bool>("liked"));
        }

        [TestMethod]
        public async Task RecordInstall_PostsTheItemAlone_AndReturnsTheFreshCount()
        {
            JObject sent = null;
            var path = string.Empty;
            var client = new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(async request =>
            {
                path = request.RequestUri.AbsolutePath;
                sent = JObject.Parse(await request.Content.ReadAsStringAsync());
                return Json("{\"itemId\":\"colors/neon\",\"count\":12}");
            }));

            var count = await client.RecordInstallAsync("colors/neon", CancellationToken.None);

            Assert.AreEqual(12, count);
            Assert.AreEqual("/v1/installs", path);
            Assert.AreEqual("colors/neon", sent.Value<string>("itemId"));
            Assert.IsNull(sent["voterHash"], "an install carries no identity");
        }

        [TestMethod]
        public async Task ToggleLike_AppliesAtOnce_AndRevertsWhenTheServiceRefuses()
        {
            var directory = NewDirectory();
            try
            {
                var identity = new WorkshopIdentityStore(directory);
                var refuse = false;
                var store = new WorkshopStatsStore(
                    new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(request =>
                    {
                        if (request.Method == HttpMethod.Get)
                        {
                            return Task.FromResult(Json("{\"likes\":{\"colors/neon\":2},\"installs\":{},\"mine\":[]}"));
                        }

                        return Task.FromResult(refuse
                            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            : Json("{\"count\":3}"));
                    })),
                    identity);

                Assert.IsTrue(await store.EnsureLoadedAsync(CancellationToken.None));
                Assert.IsTrue(store.IsAvailable);
                Assert.AreEqual(2, store.LikesFor("colors/neon"));

                await store.ToggleLikeAsync("colors/neon", CancellationToken.None);
                Assert.IsTrue(store.IsLiked("colors/neon"));
                Assert.AreEqual(3, store.LikesFor("colors/neon"));
                Assert.IsTrue(identity.IsLiked("colors/neon"), "the like is remembered for the next session");

                refuse = true;
                await store.ToggleLikeAsync("colors/neon", CancellationToken.None);
                Assert.IsTrue(store.IsLiked("colors/neon"), "a refused press is put back");
                Assert.AreEqual(3, store.LikesFor("colors/neon"));
            }
            finally
            {
                Delete(directory);
            }
        }

        [TestMethod]
        public async Task RecordInstall_RaisesTheItemsCount()
        {
            var directory = NewDirectory();
            try
            {
                var store = new WorkshopStatsStore(
                    new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(request =>
                        Task.FromResult(request.Method == HttpMethod.Get
                            ? Json("{\"likes\":{},\"installs\":{\"colors/neon\":4},\"mine\":[]}")
                            : Json("{\"count\":5}")))),
                    new WorkshopIdentityStore(directory));

                await store.EnsureLoadedAsync(CancellationToken.None);
                Assert.AreEqual(4, store.InstallsFor("colors/neon"));

                await store.RecordInstallAsync("colors/neon", CancellationToken.None);
                Assert.AreEqual(5, store.InstallsFor("colors/neon"));
            }
            finally
            {
                Delete(directory);
            }
        }

        [TestMethod]
        public async Task EnsureLoaded_LeavesTheStoreUnavailable_WhenTheServiceCannotBeReached()
        {
            var directory = NewDirectory();
            try
            {
                var store = new WorkshopStatsStore(
                    new WorkshopStatsClient(() => "https://service.invalid", null,
                        new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)))),
                    new WorkshopIdentityStore(directory));

                Assert.IsFalse(await store.EnsureLoadedAsync(CancellationToken.None));
                Assert.IsFalse(store.IsAvailable);
                Assert.AreEqual(0, store.LikesFor("colors/neon"));
                Assert.AreEqual(0, store.InstallsFor("colors/neon"));
            }
            finally
            {
                Delete(directory);
            }
        }

        [TestMethod]
        public async Task LikeOwnItems_LikesWhatThisInstallPublished_Once()
        {
            var directory = NewDirectory();
            try
            {
                var identity = new WorkshopIdentityStore(directory);
                var owner = identity.GetSubmitterHash();
                var posted = new List<string>();
                var store = new WorkshopStatsStore(
                    new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(async request =>
                    {
                        if (request.Method == HttpMethod.Get)
                        {
                            return Json("{\"likes\":{},\"installs\":{},\"mine\":[]}");
                        }

                        posted.Add(JObject.Parse(await request.Content.ReadAsStringAsync()).Value<string>("itemId"));
                        return Json("{\"count\":1}");
                    })),
                    identity);

                var items = new List<WorkshopItem>
                {
                    new WorkshopItem { Id = "colors/mine", OwnerHash = owner },
                    new WorkshopItem { Id = "colors/theirs", OwnerHash = new string('b', 64) }
                };

                await store.EnsureLoadedAsync(CancellationToken.None);
                await store.LikeOwnItemsAsync(items, CancellationToken.None);
                await store.LikeOwnItemsAsync(items, CancellationToken.None);

                CollectionAssert.AreEqual(new[] { "colors/mine" }, posted, "only this install's own item, and only the first time");
                Assert.AreEqual(1, store.LikesFor("colors/mine"));
            }
            finally
            {
                Delete(directory);
            }
        }

        [TestMethod]
        public async Task Row_LikeCommand_BecomesExecutable_WhenTheCountsArrive()
        {
            var directory = NewDirectory();
            try
            {
                var store = new WorkshopStatsStore(
                    new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(_ =>
                        Task.FromResult(Json("{\"likes\":{},\"installs\":{},\"mine\":[]}")))),
                    new WorkshopIdentityStore(directory));

                // A row binds its heart before the service has answered, as the browse list does.
                var row = new ViewModels.Workshop.WorkshopItemViewModel(new WorkshopItem { Id = "colors/neon", Name = "Neon" })
                {
                    Stats = store
                };
                Assert.IsFalse(row.CanLike);
                Assert.IsFalse(row.ToggleLikeCommand.CanExecute(null), "nothing to like until the counts are known");

                var raised = 0;
                row.ToggleLikeCommand.CanExecuteChanged += (s, e) => raised++;

                await store.EnsureLoadedAsync(CancellationToken.None);
                row.RaiseStatsChanged();

                Assert.IsTrue(row.CanLike);
                Assert.IsTrue(row.ToggleLikeCommand.CanExecute(null), "the heart is clickable once the counts are in");
                Assert.IsTrue(raised > 0, "the button is told its command can run now");
            }
            finally
            {
                Delete(directory);
            }
        }

        [TestMethod]
        public async Task Load_ShowsTheCachedCounts_BeforeTheServiceAnswers()
        {
            var directory = NewDirectory();
            try
            {
                var identity = new WorkshopIdentityStore(directory);
                var gate = new SemaphoreSlim(0, 1);
                var store = new WorkshopStatsStore(
                    new WorkshopStatsClient(() => "https://service.invalid", null, new FakeHandler(async _ =>
                    {
                        await gate.WaitAsync();
                        return Json("{\"likes\":{\"colors/neon\":9},\"installs\":{\"colors/neon\":4},\"mine\":[]}");
                    })),
                    identity);

                // What an earlier session left behind.
                Directory.CreateDirectory(Path.Combine(directory, WorkshopIdentityStore.DirectoryName));
                File.WriteAllText(
                    Path.Combine(directory, WorkshopIdentityStore.DirectoryName, "stats.json"),
                    "{\"Likes\":{\"colors/neon\":2},\"Installs\":{\"colors/neon\":3}}");

                var load = store.EnsureLoadedAsync(CancellationToken.None);
                Assert.IsTrue(store.IsAvailable, "the cached counts are up before the service answers");
                Assert.AreEqual(2, store.LikesFor("colors/neon"));
                Assert.AreEqual(3, store.InstallsFor("colors/neon"));

                gate.Release();
                Assert.IsTrue(await load);
                Assert.AreEqual(9, store.LikesFor("colors/neon"), "the service's answer replaces the cache");
                Assert.AreEqual(4, store.InstallsFor("colors/neon"));

                // And the fresh answer is what the next session starts from.
                var written = File.ReadAllText(Path.Combine(directory, WorkshopIdentityStore.DirectoryName, "stats.json"));
                StringAssert.Contains(written, "\"colors/neon\":9");
            }
            finally
            {
                Delete(directory);
            }
        }

        private static string NewDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void Delete(string directory)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static HttpResponseMessage Json(string body) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

            public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
            {
                _respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _respond(request);
        }
    }
}
