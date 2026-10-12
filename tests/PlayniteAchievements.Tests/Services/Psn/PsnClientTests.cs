using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Psn;

namespace PlayniteAchievements.Services.Tests.Psn
{
    /// <summary>Drives PsnClient with canned replies; nothing here touches the network.</summary>
    [TestClass]
    public class PsnClientTests
    {
        private sealed class Seen
        {
            public HttpMethod Method;
            public string Url;
            public string Authorization;
            public string Cookie;
            public string Body;
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<Seen, HttpResponseMessage> _respond;
            public readonly List<Seen> Requests = new List<Seen>();

            public FakeHandler(Func<Seen, HttpResponseMessage> respond)
            {
                _respond = respond;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var seen = new Seen
                {
                    Method = request.Method,
                    Url = request.RequestUri.ToString(),
                    Authorization = request.Headers.Authorization?.ToString(),
                    Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : null,
                    Body = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false)
                };
                Requests.Add(seen);
                return _respond(seen);
            }
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.TryAddWithoutValidation("Location", location);
            return response;
        }

        private static async Task ExpectAsync(PsnFailure kind, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (PsnException ex)
            {
                Assert.AreEqual(kind, ex.Kind, ex.Message);
                return;
            }

            Assert.Fail("Expected a PsnException of kind " + kind + ".");
        }

        private const string NotFound = "{\"error\":{\"code\":2240526,\"message\":\"Resource not found\"}}";

        [TestMethod]
        public async Task SignInSendsTheNpssoAsACookieAndReturnsTheAccessToken()
        {
            var handler = new FakeHandler(seen =>
                seen.Url.Contains("/authorize?")
                    ? Redirect("com.scee.psxandroid.scecompcall://redirect/?code=v3.abc123&cid=xyz")
                    : Json(HttpStatusCode.OK, "{\"access_token\":\"tok-1\",\"expires_in\":3600}"));
            using (var client = new PsnClient(handler))
            {
                var token = await client.AuthenticateAsync("my-npsso-value-0123456789");

                Assert.AreEqual("tok-1", token);
            }

            Assert.AreEqual(2, handler.Requests.Count);
            var authorize = handler.Requests[0];
            Assert.AreEqual(HttpMethod.Get, authorize.Method);
            StringAssert.StartsWith(authorize.Url, "https://ca.account.sony.com/api/authz/v3/oauth/authorize?");
            StringAssert.Contains(authorize.Url, "client_id=09515159-7237-4370-9b40-3806e67c0891");
            StringAssert.Contains(authorize.Url, "response_type=code");
            StringAssert.Contains(authorize.Url, "access_type=offline");
            StringAssert.Contains(authorize.Url, "scope=psn%3Amobile.v2.core+psn%3Aclientapp");
            Assert.AreEqual("npsso=my-npsso-value-0123456789", authorize.Cookie);

            var exchange = handler.Requests[1];
            Assert.AreEqual(HttpMethod.Post, exchange.Method);
            Assert.AreEqual("https://ca.account.sony.com/api/authz/v3/oauth/token", exchange.Url);
            StringAssert.StartsWith(exchange.Authorization, "Basic ");
            StringAssert.Contains(exchange.Body, "code=v3.abc123");
            StringAssert.Contains(exchange.Body, "grant_type=authorization_code");
            StringAssert.Contains(exchange.Body, "token_format=jwt");
        }

        [TestMethod]
        public async Task ARejectedNpssoIsReportedAndNothingElseIsSent()
        {
            var handler = new FakeHandler(seen => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.InvalidNpsso, () => client.AuthenticateAsync("expired-npsso-0123456789"));
            }

            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task NoNpssoMeansNoRequestAtAll()
        {
            var handler = new FakeHandler(seen => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.InvalidNpsso, () => client.AuthenticateAsync("   "));
            }

            Assert.AreEqual(0, handler.Requests.Count);
        }

        [TestMethod]
        public async Task AFailedTokenExchangeIsReported()
        {
            var handler = new FakeHandler(seen =>
                seen.Url.Contains("/authorize?")
                    ? Redirect("com.scee.psxandroid.scecompcall://redirect/?code=v3.abc123")
                    : Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}"));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.AuthRejected, () => client.AuthenticateAsync("my-npsso-value-0123456789"));
            }
        }

        [TestMethod]
        public async Task AnUnreachableServerIsReportedAsANetworkProblem()
        {
            var handler = new FakeHandler(seen => throw new HttpRequestException("offline"));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.Network, () => client.AuthenticateAsync("my-npsso-value-0123456789"));
            }
        }

        [TestMethod]
        public async Task AnPs5SetIsReadFromTheTrophy2ServiceInOneRequest()
        {
            var handler = new FakeHandler(seen => Json(HttpStatusCode.OK, PsnTrophyResponseTests.Ps5Set));
            using (var client = new PsnClient(handler))
            {
                var platinum = await client.GetPlatinumAsync("tok-1", "npwr39144_00");

                Assert.AreEqual("Test Platinum", platinum.TrophyName);
                Assert.AreEqual("PS5", platinum.Platform);
                Assert.AreEqual("NPWR39144_00", platinum.Npwr);
            }

            Assert.AreEqual(1, handler.Requests.Count);
            var request = handler.Requests[0];
            StringAssert.StartsWith(request.Url, "https://m.np.playstation.com/api/trophy/v1/npCommunicationIds/NPWR39144_00/trophyGroups/all/trophies?");
            StringAssert.Contains(request.Url, "npServiceName=trophy2");
            Assert.AreEqual("Bearer tok-1", request.Authorization);
        }

        [TestMethod]
        public async Task AnOlderSetFallsBackToTheClassicServiceWhenTrophy2DoesNotKnowIt()
        {
            var handler = new FakeHandler(seen =>
                seen.Url.Contains("npServiceName=trophy2")
                    ? Json(HttpStatusCode.NotFound, NotFound)
                    : Json(HttpStatusCode.OK, PsnTrophyResponseTests.Ps5Set));
            using (var client = new PsnClient(handler))
            {
                var platinum = await client.GetPlatinumAsync("tok-1", "NPWR16485_00");

                Assert.AreEqual("PS4", platinum.Platform);
            }

            Assert.AreEqual(2, handler.Requests.Count);
            StringAssert.Contains(handler.Requests[0].Url, "npServiceName=trophy2");
            StringAssert.Contains(handler.Requests[1].Url, "npServiceName=trophy&");
        }

        [TestMethod]
        public async Task AnUnknownSetIsReportedAfterBothServicesWereTried()
        {
            var handler = new FakeHandler(seen => Json(HttpStatusCode.NotFound, NotFound));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.NotFound, () => client.GetPlatinumAsync("tok-1", "NPWR00001_00"));
            }

            Assert.AreEqual(2, handler.Requests.Count);
        }

        [TestMethod]
        public async Task ASetWithoutAPlatinumIsReportedAndNotRetried()
        {
            var handler = new FakeHandler(seen => Json(
                HttpStatusCode.OK,
                "{\"trophies\":[{\"trophyId\":0,\"trophyType\":\"gold\",\"trophyName\":\"G\"}],\"totalItemCount\":1}"));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.NoPlatinum, () => client.GetPlatinumAsync("tok-1", "NPWR00002_00"));
            }

            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task LaterPagesAreReadWhenThePlatinumIsNotOnTheFirst()
        {
            var handler = new FakeHandler(seen => seen.Url.Contains("offset=100")
                ? Json(HttpStatusCode.OK, PsnTrophyResponseTests.Ps5Set)
                : Json(HttpStatusCode.OK, "{\"trophies\":[{\"trophyType\":\"bronze\",\"trophyName\":\"B\"}],\"nextOffset\":100,\"totalItemCount\":150}"));
            using (var client = new PsnClient(handler))
            {
                var platinum = await client.GetPlatinumAsync("tok-1", "NPWR00003_00");

                Assert.AreEqual("Test Platinum", platinum.TrophyName);
            }

            Assert.AreEqual(2, handler.Requests.Count);
            StringAssert.Contains(handler.Requests[1].Url, "offset=100");
        }

        [TestMethod]
        public async Task SignInRefusalAndRateLimitingAreTold_AndNotRetried()
        {
            var unauthorized = new FakeHandler(seen => Json(HttpStatusCode.Unauthorized, "{}"));
            using (var client = new PsnClient(unauthorized))
            {
                await ExpectAsync(PsnFailure.AuthRejected, () => client.GetPlatinumAsync("tok-1", "NPWR00004_00"));
            }

            Assert.AreEqual(1, unauthorized.Requests.Count);

            var limited = new FakeHandler(seen => Json((HttpStatusCode)429, "{}"));
            using (var client = new PsnClient(limited))
            {
                await ExpectAsync(PsnFailure.RateLimited, () => client.GetPlatinumAsync("tok-1", "NPWR00005_00"));
            }

            Assert.AreEqual(1, limited.Requests.Count);
        }

        [TestMethod]
        public async Task AnInvalidIdIsRejectedWithoutAnyRequest()
        {
            var handler = new FakeHandler(seen => new HttpResponseMessage(HttpStatusCode.OK));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.NotFound, () => client.GetPlatinumAsync("tok-1", "CUSA12345"));
            }

            Assert.AreEqual(0, handler.Requests.Count);
        }

        private static HttpResponseMessage Image(byte[] bytes, string type = "image/png")
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return response;
        }

        [TestMethod]
        public async Task AnIconIsDownloadedFromAPlayStationHost()
        {
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            var handler = new FakeHandler(seen => Image(png));
            using (var client = new PsnClient(handler))
            {
                var bytes = await client.DownloadIconAsync("https://psnobj.prod.dl.playstation.net/psnobj/x.png");

                CollectionAssert.AreEqual(png, bytes);
            }
        }

        [TestMethod]
        public async Task UnsafeIconsAreRefused()
        {
            var handler = new FakeHandler(seen => Image(new byte[] { 1 }));
            using (var client = new PsnClient(handler))
            {
                await ExpectAsync(PsnFailure.BadIcon, () => client.DownloadIconAsync("https://evil.example.com/a.png"));
                await ExpectAsync(PsnFailure.BadIcon, () => client.DownloadIconAsync("http://psnobj.prod.dl.playstation.net/a.png"));
            }

            Assert.AreEqual(0, handler.Requests.Count);

            var notAnImage = new FakeHandler(seen => Image(Encoding.UTF8.GetBytes("<html>"), "text/html"));
            using (var client = new PsnClient(notAnImage))
            {
                await ExpectAsync(PsnFailure.BadIcon, () => client.DownloadIconAsync("https://psnobj.prod.dl.playstation.net/a.png"));
            }

            var tooBig = new FakeHandler(seen => Image(new byte[PsnClient.MaxIconBytes + 10]));
            using (var client = new PsnClient(tooBig))
            {
                await ExpectAsync(PsnFailure.BadIcon, () => client.DownloadIconAsync("https://psnobj.prod.dl.playstation.net/a.png"));
            }
        }
    }
}
