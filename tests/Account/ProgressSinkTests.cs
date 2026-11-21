using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Account;
using Ru7100.Core.Account.Trakt;
using Xunit;

namespace Ru7100.Core.Tests.Account
{
    public class ProgressSinkTests
    {
        DateTimeOffset clock = Metas.At("2026-11-18T10:00:00Z");
        readonly FakeStremioServer server = new FakeStremioServer();
        readonly MemoryStore store = new MemoryStore();
        readonly FixedAuth auth = new FixedAuth();

        StremioLibrary Library() => new StremioLibrary(server.Client(), auth, store, new FakeCatalog(), () => clock);

        [Fact]
        public async Task SeriesSessionWritesInOrderAndMarksWatched()
        {
            var meta = Metas.Series("tt1", "Show", (1, 3));
            var ep1 = meta.Videos[0];
            var library = Library();
            var sink = new StremioProgressSink(library, () => clock);

            await sink.PlaybackStartedAsync(meta, ep1, 30000, CancellationToken.None);
            var state = server.Items["tt1"]["state"];
            Assert.Equal("tt1:1:1", (string)state["video_id"]);
            Assert.Equal(30000, (long)state["timeOffset"]);
            Assert.False((bool)state["noNotif"]);
            Assert.Single(server.Puts);

            // Throttled: < 60 s past the last save.
            await sink.ProgressAsync(meta, ep1, 60000, 2400000, CancellationToken.None);
            Assert.Single(server.Puts);
            await sink.ProgressAsync(meta, ep1, 90000, 2400000, CancellationToken.None);
            Assert.Equal(2, server.Puts.Count);
            state = server.Items["tt1"]["state"];
            Assert.Equal(90000, (long)state["timeOffset"]);
            Assert.Equal(90000, (long)state["timeWatched"]);
            Assert.Equal(2400000, (long)state["duration"]);

            Assert.False(StremioProgressSink.ShouldComplete(2150000, 2400000, 5, false));
            Assert.True(StremioProgressSink.ShouldComplete(2160000, 2400000, 2, false));
            Assert.False(StremioProgressSink.ShouldComplete(2160000, 2400000, 1, false));
            await sink.ProgressAsync(meta, ep1, 2160000, 2400000, CancellationToken.None);
            await sink.CompletedAsync(meta, ep1, CancellationToken.None);
            await sink.CompletedAsync(meta, ep1, CancellationToken.None);   // once per session
            state = server.Items["tt1"]["state"];
            Assert.Equal("tt1:1:2", (string)state["video_id"]);            // advanced to next (D4)
            Assert.Equal(1, (long)state["timeOffset"]);
            Assert.Equal(1, (int)state["timesWatched"]);
            Assert.Equal(2160000, (long)state["overallTimeWatched"]);
            var entry = await library.GetAsync("tt1", CancellationToken.None);
            Assert.True(library.IsWatched(entry, meta, "tt1:1:1"));
            int putsAfterComplete = server.Puts.Count;

            // A progress sample right after completion does not save (lastSaved moved to completion).
            await sink.ProgressAsync(meta, ep1, 2200000, 2400000, CancellationToken.None);
            Assert.Equal(putsAfterComplete, server.Puts.Count);

            // Stop after marked: no final save.
            await sink.StoppedAsync(meta, ep1, 2300000, 2400000, true, CancellationToken.None);
            Assert.Equal(putsAfterComplete, server.Puts.Count);

            // Puts happened in call order.
            var offsets = server.Puts.Select(p => (long)p["state"]["timeOffset"]).ToList();
            Assert.Equal(new long[] { 30000, 90000, 2160000, 1 }, offsets);
        }

        [Fact]
        public async Task StoppedSavesFinalProgressWhenNotMarked()
        {
            var meta = Metas.Movie("tt9", "Nine");
            var sink = new StremioProgressSink(Library(), () => clock);
            await sink.PlaybackStartedAsync(meta, null, 0, CancellationToken.None);
            await sink.ProgressAsync(meta, null, 20000, 6000000, CancellationToken.None);
            await sink.StoppedAsync(meta, null, 45000, 6000000, false, CancellationToken.None);
            var state = server.Items["tt9"]["state"];
            Assert.Equal(45000, (long)state["timeOffset"]);
            Assert.Equal("tt9", (string)state["video_id"]);
            // Unknown duration (fewer than 2 credible samples) → no final save.
            int puts = server.Puts.Count;
            await sink.StoppedAsync(meta, null, 50000, 0, false, CancellationToken.None);
            Assert.Equal(puts, server.Puts.Count);
        }

        [Fact]
        public async Task CompletionOfEpisodeMissingFromMetaKeepsBitfield()
        {
            var meta = Metas.Series("tt1", "Show", (1, 2));
            var missing = new VideoInfo { Id = "tt1:1:3", Season = 1, Episode = 3 };
            var sink = new StremioProgressSink(Library(), () => clock);
            await sink.PlaybackStartedAsync(meta, missing, 0, CancellationToken.None);
            await sink.ProgressAsync(meta, missing, 2000000, 2200000, CancellationToken.None);
            await sink.CompletedAsync(meta, missing, CancellationToken.None);
            var state = server.Items["tt1"]["state"];
            Assert.Equal(0, (long)state["timeOffset"]);
            Assert.Equal("tt1:1:3", (string)state["video_id"]);
            Assert.Equal(JTokenType.Null, state["watched"].Type);
            Assert.Equal(0, (int)state["timesWatched"]);
        }

        [Fact]
        public async Task StartKeepsDurationForSameVideoOnly()
        {
            var meta = Metas.Series("tt1", "Show", (1, 2));
            server.Items["tt1"] = JObject.Parse(@"{""_id"":""tt1"",""type"":""series"",""name"":""Show"",""_ctime"":""2020-01-01T00:00:00.000Z"",""state"":{""video_id"":""tt1:1:1"",""duration"":100,""timeWatched"":50,""noNotif"":true}}");
            var sink = new StremioProgressSink(Library(), () => clock);
            await sink.PlaybackStartedAsync(meta, meta.Videos[0], 40, CancellationToken.None);
            Assert.Equal(100, (long)server.Items["tt1"]["state"]["duration"]);
            Assert.Equal(50, (long)server.Items["tt1"]["state"]["timeWatched"]);
            Assert.False((bool)server.Items["tt1"]["state"]["noNotif"]);
            await sink.PlaybackStartedAsync(meta, meta.Videos[1], -5, CancellationToken.None);
            Assert.Equal(0, (long)server.Items["tt1"]["state"]["duration"]);
            Assert.Equal(0, (long)server.Items["tt1"]["state"]["timeWatched"]);
            Assert.Equal(0, (long)server.Items["tt1"]["state"]["timeOffset"]);
        }

        [Fact]
        public async Task OfflineProgressIsKeptInOrderAndReplayed()
        {
            var meta = Metas.Series("tt1", "Show", (1, 3));
            var ep1 = meta.Videos[0];
            var library = Library();
            var sink = new StremioProgressSink(library, () => clock);
            server.Handler.Offline = true;

            await sink.PlaybackStartedAsync(meta, ep1, 0, CancellationToken.None);
            for (long pos = 60000; pos <= 600000; pos += 60000)
                await sink.ProgressAsync(meta, ep1, pos, 2400000, CancellationToken.None);
            await sink.ProgressAsync(meta, ep1, 2200000, 2400000, CancellationToken.None);
            await sink.CompletedAsync(meta, ep1, CancellationToken.None);
            await sink.StoppedAsync(meta, ep1, 2250000, 2400000, true, CancellationToken.None);

            Assert.NotNull(sink.LastError);
            // started + coalesced progress + completed
            var pending = library.PendingOperations.Select(o => (string)o["kind"]).ToArray();
            Assert.Equal(new[] { "started", "progress", "completed" }, pending);
            Assert.Equal(2200000, (long)library.PendingOperations[1]["positionMs"]);
            Assert.Empty(server.Puts);

            // The TV keeps showing local state while offline.
            var offline = await library.GetAsync("tt1", CancellationToken.None);
            Assert.Equal("tt1:1:2", offline.State.VideoId);
            Assert.True(library.IsWatched(offline, meta, "tt1:1:1"));

            // Network returns: a fresh process replays everything in order.
            server.Handler.Offline = false;
            var restarted = Library();
            Assert.True(await restarted.FlushAsync(CancellationToken.None));
            Assert.Equal(new long[] { 0, 2200000, 1 }, server.Puts.Select(p => (long)p["state"]["timeOffset"]));
            var state = server.Items["tt1"]["state"];
            Assert.Equal("tt1:1:2", (string)state["video_id"]);
            Assert.Equal(1, (int)state["timesWatched"]);
            Assert.True(restarted.IsWatched(await restarted.GetAsync("tt1", CancellationToken.None), meta, "tt1:1:1"));
        }

        [Fact]
        public async Task ConcurrentCallsAreSerialized()
        {
            var meta = Metas.Movie("tt9", "Nine");
            var library = Library();
            int inFlight = 0, maxInFlight = 0;
            server.Override = r =>
            {
                int now = Interlocked.Increment(ref inFlight);
                maxInFlight = Math.Max(maxInFlight, now);
                Thread.Sleep(5);
                Interlocked.Decrement(ref inFlight);
                return null;
            };
            var sink = new StremioProgressSink(library, () => clock);
            await sink.PlaybackStartedAsync(meta, null, 0, CancellationToken.None);
            var tasks = Enumerable.Range(1, 8).Select(i => Task.Run(() => library.MarkWatchedAsync(meta, "tt9", i % 2 == 0, CancellationToken.None)));
            await Task.WhenAll(tasks);
            Assert.Equal(1, maxInFlight);
            Assert.Equal(0, library.PendingWrites);
        }

        // ---- Trakt ----

        sealed class TraktFixture
        {
            public readonly FixtureHandler Handler = new FixtureHandler();
            public readonly MemoryStore Store = new MemoryStore();
            public readonly List<JObject> Scrobbles = new List<JObject>();
            public readonly List<string> Actions = new List<string>();

            public TraktFixture(bool authenticated = true)
            {
                Store.Values[AccountKeys.TraktCredentials] = "{\"client_id\":\"cid\",\"client_secret\":\"secret\"}";
                if (authenticated)
                    Store.Values[AccountKeys.TraktToken] = "{\"access_token\":\"at\",\"refresh_token\":\"rt\",\"created_at\":1795000000,\"expires_in\":7776000}";
                Handler.On(HttpMethod.Post, "https://trakt.test/scrobble/", r =>
                {
                    Actions.Add(r.Url.Substring("https://trakt.test/scrobble/".Length));
                    Scrobbles.Add(r.Json);
                    Assert.Equal("Bearer at", r.Headers["authorization"]);
                    Assert.Equal("cid", r.Headers["trakt-api-key"]);
                    Assert.Equal("2", r.Headers["trakt-api-version"]);
                    return FixtureHandler.Json(new { id = 1 }, HttpStatusCode.Created);
                });
                Handler.On(HttpMethod.Get, "https://trakt.test/shows/", r => FixtureHandler.Json(new { season = 1, number = 2, title = "Ep Two", ids = new { trakt = 99 } }));
            }
        }

        [Fact]
        public async Task TraktScrobblesFollowK4()
        {
            var f = new TraktFixture();
            var client = new TraktClient(f.Handler, f.Store, () => clock, "https://trakt.test");
            var trakt = new TraktProgressSink(client, f.Store, () => clock);
            var meta = Metas.Series("tt1", "Show", (1, 3));
            var ep2 = meta.Videos[1];

            await trakt.PlaybackStartedAsync(meta, ep2, 0, CancellationToken.None);
            await trakt.ProgressAsync(meta, ep2, 30000, 1000000, CancellationToken.None);    // < 60 s: nothing
            await trakt.ProgressAsync(meta, ep2, 60000, 1000000, CancellationToken.None);    // start again
            await trakt.PausedAsync(meta, ep2, 70000, 1000000, CancellationToken.None);
            await trakt.ProgressAsync(meta, ep2, 71000, 1000000, CancellationToken.None);    // resume → start
            await trakt.CompletedAsync(meta, ep2, CancellationToken.None);
            await trakt.StoppedAsync(meta, ep2, 950000, 1000000, false, CancellationToken.None);
            Assert.Equal(new[] { "start", "start", "pause", "start", "stop" }, f.Actions);
            Assert.Equal(6.0, (double)f.Scrobbles[1]["progress"]);
            Assert.Equal(7.1, (double)f.Scrobbles[3]["progress"]);
            Assert.Equal(100.0, (double)f.Scrobbles[4]["progress"]);
            var stop = f.Scrobbles[4];
            Assert.Equal("Show", (string)stop["show"]["title"]);
            Assert.Equal("tt1", (string)stop["show"]["ids"]["imdb"]);
            Assert.Equal("Ep Two", (string)stop["episode"]["title"]);
            Assert.Equal(99, (int)stop["episode"]["ids"]["trakt"]);
            Assert.Equal("0.2.1", (string)stop["app_version"]);
            Assert.Equal("2026-11-18", (string)stop["app_date"]);
            Assert.Single(f.Handler.Requests, r => r.Url.Contains("/shows/"));   // episode lookup cached
        }

        [Fact]
        public void TraktPayloadMatchesOriginal()
        {
            var movie = TraktClient.BuildScrobblePayload(new TraktScrobbleTarget { Type = MediaType.Movie, ImdbId = "tt9", Title = "Nine" }, 33.33333, null, clock);
            Assert.Equal(33.33, (double)movie["progress"]);
            Assert.Equal("Nine", (string)movie["movie"]["title"]);
            Assert.Null(movie["show"]);
            var ep = TraktClient.BuildScrobblePayload(new TraktScrobbleTarget { Type = MediaType.Series, ImdbId = "tt1", Title = "S", Season = 2, Episode = 5 }, 150, null, clock);
            Assert.Equal(100.0, (double)ep["progress"]);
            Assert.Equal(2, (int)ep["episode"]["season"]);
            Assert.Equal(5, (int)ep["episode"]["number"]);
        }

        [Fact]
        public async Task TraktSkippedWhenNotAuthenticated()
        {
            var f = new TraktFixture(authenticated: false);
            var trakt = new TraktProgressSink(new TraktClient(f.Handler, f.Store, () => clock, "https://trakt.test"), f.Store, () => clock);
            await trakt.PlaybackStartedAsync(Metas.Movie("tt9", "Nine"), null, 0, CancellationToken.None);
            Assert.Empty(f.Handler.Requests);
            Assert.Equal(0, trakt.PendingWrites);
        }

        [Fact]
        public async Task TraktOutageDoesNotBlockStremioAndStaleStartsAreDropped()
        {
            var f = new TraktFixture();
            var client = new TraktClient(f.Handler, f.Store, () => clock, "https://trakt.test");
            var trakt = new TraktProgressSink(client, f.Store, () => clock);
            var library = Library();
            var composite = new CompositeProgressSink(new StremioProgressSink(library, () => clock), trakt);
            var meta = Metas.Movie("tt9", "Nine");

            f.Handler.Offline = true;
            await composite.PlaybackStartedAsync(meta, null, 0, CancellationToken.None);
            await composite.ProgressAsync(meta, null, 100000, 1000000, CancellationToken.None);
            await composite.StoppedAsync(meta, null, 120000, 1000000, false, CancellationToken.None);
            Assert.Equal(120000, (long)server.Items["tt9"]["state"]["timeOffset"]);   // Stremio unaffected
            Assert.Equal(3, trakt.PendingWrites);
            Assert.NotNull(trakt.LastError);

            f.Handler.Offline = false;
            clock = clock.AddMinutes(30);
            Assert.True(await trakt.FlushAsync(CancellationToken.None));
            Assert.Equal(new[] { "stop" }, f.Actions);                                 // stale start dropped
            Assert.Equal(12.0, (double)f.Scrobbles[0]["progress"]);
        }

        [Fact]
        public async Task TraktDeviceAuthFlow()
        {
            var f = new TraktFixture(authenticated: false);
            int polls = 0;
            f.Handler
                .On(HttpMethod.Post, "https://trakt.test/oauth/device/code", r =>
                {
                    Assert.Equal("cid", (string)r.Json["client_id"]);
                    return FixtureHandler.Json(new { device_code = "dev", user_code = "UC12", verification_url = "https://trakt.tv/activate", expires_in = 600, interval = 5 });
                })
                .On(HttpMethod.Post, "https://trakt.test/oauth/device/token", r =>
                {
                    Assert.Equal("dev", (string)r.Json["code"]);
                    Assert.Equal("secret", (string)r.Json["client_secret"]);
                    polls++;
                    if (polls == 1) return FixtureHandler.Json("{}", HttpStatusCode.BadRequest);
                    if (polls == 2) return FixtureHandler.Json("{}", (HttpStatusCode)429);
                    return FixtureHandler.Json(new { access_token = "at", refresh_token = "rt", created_at = 1795000000, expires_in = 7776000 });
                })
                .On(HttpMethod.Get, "https://trakt.test/users/settings", r => FixtureHandler.Json(new { user = new { username = "dudi", name = "D" } }));
            var client = new TraktClient(f.Handler, f.Store, () => clock, "https://trakt.test");
            Assert.True(await client.IsConfiguredAsync(CancellationToken.None));
            var code = await client.BeginDeviceAuthAsync(CancellationToken.None);
            Assert.Equal("UC12", code.UserCode);
            Assert.Equal(clock.AddMinutes(10), code.ExpiresAt);
            Assert.Equal(TraktAuthStatus.Pending, await client.CompleteDeviceAuthAsync(CancellationToken.None));
            Assert.Equal(TraktAuthStatus.SlowDown, await client.CompleteDeviceAuthAsync(CancellationToken.None));
            Assert.Equal(TraktAuthStatus.Authenticated, await client.CompleteDeviceAuthAsync(CancellationToken.None));
            Assert.True(await client.IsAuthenticatedAsync(CancellationToken.None));
            Assert.Equal("dudi", (await client.GetUserAsync(CancellationToken.None)).Username);

            // Expiry is checked locally before polling.
            await client.BeginDeviceAuthAsync(CancellationToken.None);
            clock = clock.AddMinutes(11);
            Assert.Equal(TraktAuthStatus.Expired, await client.CompleteDeviceAuthAsync(CancellationToken.None));
        }

        [Fact]
        public async Task TraktRefreshesNearExpiry()
        {
            var f = new TraktFixture(authenticated: false);
            long created = clock.ToUnixTimeSeconds() - 1000;
            f.Store.Values[AccountKeys.TraktToken] = "{\"access_token\":\"old\",\"refresh_token\":\"rt\",\"created_at\":" + created + ",\"expires_in\":1100}";
            f.Handler.On(HttpMethod.Post, "https://trakt.test/oauth/token", r =>
            {
                Assert.Equal("refresh_token", (string)r.Json["grant_type"]);
                Assert.Equal("urn:ietf:wg:oauth:2.0:oob", (string)r.Json["redirect_uri"]);
                return FixtureHandler.Json(new { access_token = "at", refresh_token = "rt2", created_at = clock.ToUnixTimeSeconds(), expires_in = 7776000 });
            });
            var client = new TraktClient(f.Handler, f.Store, () => clock, "https://trakt.test");
            Assert.True(await client.ScrobbleAsync("start", new TraktScrobbleTarget { Type = MediaType.Movie, ImdbId = "tt9", Title = "Nine" }, 1, CancellationToken.None));
            Assert.Equal("rt2", (string)f.Store.Json(AccountKeys.TraktToken)["refresh_token"]);
        }
    }
}
