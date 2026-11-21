using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Account;
using Xunit;

namespace Ru7100.Core.Tests.Account
{
    public class StremioAccountTests
    {
        DateTimeOffset clock = Metas.At("2026-11-18T10:00:00Z");
        readonly FakeStremioServer server = new FakeStremioServer();
        readonly MemoryStore store = new MemoryStore();

        StremioAccount Account() => new StremioAccount(server.Client(), store, () => clock);

        [Fact]
        public async Task LinkFlowPendingThenApproved()
        {
            int reads = 0;
            server.Handler
                .On(HttpMethod.Get, FakeStremioServer.Link + "/create?type=Create", r => FixtureHandler.Json(new { result = new { code = "ABCD", link = "https://link.stremio.com/?code=ABCD" } }))
                .On(HttpMethod.Get, FakeStremioServer.Link + "/read?type=Read&code=ABCD", r =>
                {
                    reads++;
                    if (reads == 1) return FixtureHandler.Json(new { result = new { success = false, error = new { message = "Invalid token" } } });
                    if (reads == 2) return FixtureHandler.Json(new { result = new { } });
                    return FixtureHandler.Json(new { result = new { success = true, result = new { authKey = "key-1" } } });
                });
            var account = Account();
            await account.InitializeAsync(CancellationToken.None);
            Assert.False(account.IsSignedIn);

            var (code, link) = await account.BeginLinkAsync(CancellationToken.None);
            Assert.Equal("ABCD", code);
            Assert.Equal("https://link.stremio.com/?code=ABCD", link);
            Assert.Equal("ABCD", (string)store.Json(AccountKeys.StremioPendingLink)["code"]);

            Assert.False(await account.CompleteLinkAsync(null, CancellationToken.None));   // error "Invalid token" → pending
            Assert.False(await account.CompleteLinkAsync(null, CancellationToken.None));   // no authKey yet → pending
            Assert.True(await account.CompleteLinkAsync(null, CancellationToken.None));
            Assert.True(account.IsSignedIn);
            var saved = store.Json(AccountKeys.StremioAuth);
            Assert.Equal("key-1", (string)saved["authKey"]);
            Assert.Equal("2026-11-18T10:00:00.000Z", (string)saved["linkedAt"]);
            Assert.Null(store.Json(AccountKeys.StremioPendingLink));

            // A fresh instance reads the persisted key.
            var again = Account();
            await again.InitializeAsync(CancellationToken.None);
            Assert.True(again.IsSignedIn);
            var user = await again.GetUserAsync(CancellationToken.None);
            Assert.Equal("u1", user.Id);
            Assert.Equal("a@b.c", user.Email);
        }

        [Fact]
        public async Task LinkAcceptsSnakeCaseKey()
        {
            server.Handler
                .On(HttpMethod.Get, FakeStremioServer.Link + "/read", r => FixtureHandler.Json(new { result = new { auth_key = "key-1" } }));
            var account = Account();
            Assert.True(await account.CompleteLinkAsync("ZZZZ", CancellationToken.None));
            Assert.True(account.IsSignedIn);
        }

        [Fact]
        public async Task LinkExpiresAfterTenMinutes()
        {
            server.Handler
                .On(HttpMethod.Get, FakeStremioServer.Link + "/create", r => FixtureHandler.Json(new { result = new { code = "EXP1", link = "l" } }))
                .On(HttpMethod.Get, FakeStremioServer.Link + "/read", r => FixtureHandler.Json(new { error = new { message = "Code expired" } }));
            var account = Account();
            await account.BeginLinkAsync(CancellationToken.None);
            clock = clock.AddMinutes(5);
            Assert.False(await account.CompleteLinkAsync(null, CancellationToken.None));
            clock = clock.AddMinutes(6);
            var error = await Assert.ThrowsAsync<StremioLinkExpiredException>(() => account.CompleteLinkAsync(null, CancellationToken.None));
            Assert.Equal("EXP1", error.Code);
            Assert.False(account.IsSignedIn);
        }

        [Fact]
        public async Task LinkHardErrorPropagates()
        {
            server.Handler.On(HttpMethod.Get, FakeStremioServer.Link + "/read", r => FixtureHandler.Json(new { error = new { message = "rate limited by policy" } }));
            await Assert.ThrowsAsync<StremioApiException>(() => Account().CompleteLinkAsync("X", CancellationToken.None));
        }

        [Fact]
        public async Task LinkCreateRetriesTransientFailures()
        {
            int calls = 0;
            server.Handler.On(HttpMethod.Get, FakeStremioServer.Link + "/create", r =>
                ++calls < 3 ? FixtureHandler.Json("{}", HttpStatusCode.ServiceUnavailable) : FixtureHandler.Json(new { result = new { code = "C", link = "L" } }));
            var result = await Account().BeginLinkAsync(CancellationToken.None);
            Assert.Equal("C", result.Code);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task AddonCollectionMapsAndCaches60s()
        {
            int calls = 0;
            server.Override = r =>
            {
                if (!r.Url.EndsWith("/addonCollectionGet")) return null;
                calls++;
                Assert.Equal("key-1", (string)r.Json["authKey"]);
                Assert.True((bool)r.Json["update"]);
                return FixtureHandler.Json(@"{""result"":{""addons"":[
                    {""transportUrl"":""https://v3-cinemeta.strem.io/manifest.json"",""manifest"":{""id"":""com.linvo.cinemeta"",""name"":""Cinemeta"",""resources"":[""catalog"",{""name"":""meta"",""types"":[""movie""]}],""types"":[""movie"",""series""]}},
                    {""transportUrl"":""https://torrentio.strem.fun/x/manifest.json"",""manifest"":{""id"":""com.stremio.torrentio.addon"",""name"":""Torrentio"",""resources"":[{""name"":""stream""}],""types"":[""movie""]}}]}}");
            };
            store.Values[AccountKeys.StremioAuth] = "{\"authKey\":\"key-1\"}";
            var account = Account();
            var addons = await account.GetAddonCollectionAsync(CancellationToken.None);
            Assert.Equal(2, addons.Count);
            Assert.Equal("com.linvo.cinemeta", addons[0].Id);
            Assert.Equal(new[] { "catalog", "meta" }, addons[0].Resources);
            Assert.Equal(new[] { "movie", "series" }, addons[0].Types);
            Assert.Equal(new[] { "stream" }, addons[1].Resources);
            Assert.Equal("https://torrentio.strem.fun/x/manifest.json", addons[1].TransportUrl);

            await account.GetAddonCollectionAsync(CancellationToken.None);
            clock = clock.AddSeconds(59);
            await account.GetAddonCollectionAsync(CancellationToken.None);
            Assert.Equal(1, calls);
            clock = clock.AddSeconds(2);
            await account.GetAddonCollectionAsync(CancellationToken.None);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task SignOutClearsKey()
        {
            store.Values[AccountKeys.StremioAuth] = "{\"authKey\":\"key-1\"}";
            var account = Account();
            await account.InitializeAsync(CancellationToken.None);
            Assert.True(account.IsSignedIn);
            await account.SignOutAsync(CancellationToken.None);
            Assert.False(account.IsSignedIn);
            await Assert.ThrowsAsync<StremioNotSignedInException>(() => account.GetAddonCollectionAsync(CancellationToken.None));
            var fresh = Account();
            await fresh.InitializeAsync(CancellationToken.None);
            Assert.False(fresh.IsSignedIn);
        }

        [Fact]
        public async Task ApiErrorFieldFails()
        {
            store.Values[AccountKeys.StremioAuth] = "{\"authKey\":\"bad\"}";
            var error = await Assert.ThrowsAsync<StremioApiException>(() => Account().GetUserAsync(CancellationToken.None));
            Assert.Contains("Session does not exist", error.Message);
            Assert.False(error.IsTransient);
        }
    }
}
