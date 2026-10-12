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
    public class WorkshopLikesTests
    {
        [TestMethod]
        public async Task Fetch_ReadsCountsAndOwnLikes_AndAsksForTheVoter()
        {
            var requested = new List<string>();
            var client = new WorkshopLikesClient(() => "https://service.invalid/", null, new FakeHandler(request =>
            {
                requested.Add(request.RequestUri.PathAndQuery);
                return Task.FromResult(Json("{\"counts\":{\"colors/neon\":3,\"sounds/chime\":0},\"mine\":[\"colors/neon\"]}"));
            }));

            var voter = new string('a', 64);
            var snapshot = await client.FetchAsync(voter, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "/v1/likes?voter=" + voter }, requested);
            Assert.AreEqual(3, snapshot.Counts["colors/neon"]);
            Assert.IsFalse(snapshot.Counts.ContainsKey("sounds/chime"), "an item nobody liked is not carried");
            Assert.IsTrue(snapshot.Mine.Contains("colors/neon"));
        }

        [TestMethod]
        public async Task Fetch_ReturnsNull_WhenTheServiceFails()
        {
            var client = new WorkshopLikesClient(() => "https://service.invalid", null,
                new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"nope\"}", Encoding.UTF8, "application/json")
                })));

            Assert.IsNull(await client.FetchAsync(null, CancellationToken.None));
        }

        [TestMethod]
        public async Task Set_PostsTheItemAndVoter_AndReturnsTheFreshCount()
        {
            JObject sent = null;
            var client = new WorkshopLikesClient(() => "https://service.invalid", null, new FakeHandler(async request =>
            {
                sent = JObject.Parse(await request.Content.ReadAsStringAsync());
                return Json("{\"itemId\":\"colors/neon\",\"liked\":true,\"count\":4}");
            }));

            var count = await client.SetAsync("colors/neon", new string('A', 64), true, CancellationToken.None);

            Assert.AreEqual(4, count);
            Assert.AreEqual("colors/neon", sent.Value<string>("itemId"));
            Assert.AreEqual(new string('a', 64), sent.Value<string>("voterHash"), "the hash is sent lowercase");
            Assert.IsTrue(sent.Value<bool>("liked"));
        }

        [TestMethod]
        public async Task Toggle_AppliesAtOnce_AndRevertsWhenTheServiceRefuses()
        {
            var directory = NewDirectory();
            try
            {
                var identity = new WorkshopIdentityStore(directory);
                var refuse = false;
                var store = new WorkshopLikeStore(
                    new WorkshopLikesClient(() => "https://service.invalid", null, new FakeHandler(request =>
                    {
                        if (request.Method == HttpMethod.Get)
                        {
                            return Task.FromResult(Json("{\"counts\":{\"colors/neon\":2},\"mine\":[]}"));
                        }

                        return Task.FromResult(refuse
                            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                            : Json("{\"count\":3}"));
                    })),
                    identity);

                Assert.IsTrue(await store.EnsureLoadedAsync(CancellationToken.None));
                Assert.IsTrue(store.IsAvailable);
                Assert.AreEqual(2, store.CountFor("colors/neon"));

                await store.ToggleAsync("colors/neon", CancellationToken.None);
                Assert.IsTrue(store.IsLiked("colors/neon"));
                Assert.AreEqual(3, store.CountFor("colors/neon"));
                Assert.IsTrue(identity.IsLiked("colors/neon"), "the like is remembered for the next session");

                refuse = true;
                await store.ToggleAsync("colors/neon", CancellationToken.None);
                Assert.IsTrue(store.IsLiked("colors/neon"), "a refused press is put back");
                Assert.AreEqual(3, store.CountFor("colors/neon"));
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
                var store = new WorkshopLikeStore(
                    new WorkshopLikesClient(() => "https://service.invalid", null,
                        new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)))),
                    new WorkshopIdentityStore(directory));

                Assert.IsFalse(await store.EnsureLoadedAsync(CancellationToken.None));
                Assert.IsFalse(store.IsAvailable);
                Assert.AreEqual(0, store.CountFor("colors/neon"));
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
                var store = new WorkshopLikeStore(
                    new WorkshopLikesClient(() => "https://service.invalid", null, new FakeHandler(async request =>
                    {
                        if (request.Method == HttpMethod.Get)
                        {
                            return Json("{\"counts\":{},\"mine\":[]}");
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
                Assert.AreEqual(1, store.CountFor("colors/mine"));
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
