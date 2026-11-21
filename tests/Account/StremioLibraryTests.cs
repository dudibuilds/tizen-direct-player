using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Account;
using Xunit;

namespace Ru7100.Core.Tests.Account
{
    public class StremioLibraryTests
    {
        DateTimeOffset clock = Metas.At("2026-11-18T10:00:00Z");
        readonly FakeStremioServer server = new FakeStremioServer();
        readonly MemoryStore store = new MemoryStore();
        readonly FakeCatalog catalog = new FakeCatalog();
        readonly FixedAuth auth = new FixedAuth();

        StremioLibrary Library() => new StremioLibrary(server.Client(), auth, store, catalog, () => clock);

        static JObject Parse(string json) => JsonConvert.DeserializeObject<JObject>(json, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });

        const string ExistingItem = @"{""_id"":""tt0903747"",""name"":""Breaking Bad"",""type"":""series"",""poster"":""https://old/poster.jpg"",""posterShape"":""poster"",
            ""removed"":false,""temp"":false,""_ctime"":""2021-03-04T05:06:07.123Z"",""_mtime"":""2024-01-01T00:00:00.000Z"",
            ""state"":{""lastWatched"":""2024-01-01T00:00:00.000Z"",""timeWatched"":0,""timeOffset"":0,""overallTimeWatched"":123,""timesWatched"":2,
                       ""flaggedWatched"":0,""duration"":0,""video_id"":""tt0903747:1:3"",""watched"":null,""noNotif"":false,""season"":1,""episode"":3,""futureStateField"":{""x"":[1,2]}},
            ""behaviorHints"":{""defaultVideoId"":null,""featuredVideoId"":null,""hasScheduledVideos"":true,""unknownHint"":7},
            ""futureTopLevel"":""keep me"",""year"":""2008–2013""}";

        [Fact]
        public async Task DatastoreRoundTripPreservesUnknownFields()
        {
            server.Items["tt0903747"] = Parse(ExistingItem);
            var meta = Metas.Series("tt0903747", "Breaking Bad", (1, 7), (2, 13));
            var library = Library();

            await library.AddAsync(meta, CancellationToken.None);

            Assert.Single(server.Puts);
            var put = server.Puts[0];
            Assert.Equal("keep me", (string)put["futureTopLevel"]);
            Assert.Equal("2008–2013", (string)put["year"]);
            Assert.Equal(7, (int)put["behaviorHints"]["unknownHint"]);
            Assert.True((bool)put["behaviorHints"]["hasScheduledVideos"]);
            Assert.Equal(new JArray(1, 2), put["state"]["futureStateField"]["x"]);
            Assert.Equal(1, (int)put["state"]["season"]);
            Assert.Equal("2021-03-04T05:06:07.123Z", (string)put["_ctime"]);       // kept, not reformatted
            Assert.Equal("2026-11-18T10:00:00.000Z", (string)put["_mtime"]);
            Assert.Equal("2024-01-01T00:00:00.000Z", (string)put["state"]["lastWatched"]);
            Assert.Equal("https://img/tt0903747.jpg", (string)put["poster"]);      // metadata wins
            Assert.Equal(123, (int)put["state"]["overallTimeWatched"]);

            // Every original field is still there.
            var original = Parse(ExistingItem);
            foreach (var p in original.Properties()) Assert.NotNull(put[p.Name]);
            foreach (var p in ((JObject)original["state"]).Properties()) Assert.NotNull(put["state"][p.Name]);
        }

        [Fact]
        public async Task AddCreatesDefaultItemShape()
        {
            var meta = Metas.Movie("tt0133093", "The Matrix");
            await Library().AddAsync(meta, CancellationToken.None);
            var item = server.Items["tt0133093"];
            Assert.Equal(new[] { "_id", "name", "type", "poster", "posterShape", "removed", "temp", "_ctime", "_mtime", "state", "behaviorHints" },
                item.Properties().Select(p => p.Name).ToArray());
            Assert.Equal(new[] { "lastWatched", "timeWatched", "timeOffset", "overallTimeWatched", "timesWatched", "flaggedWatched", "duration", "video_id", "watched", "noNotif" },
                ((JObject)item["state"]).Properties().Select(p => p.Name).ToArray());
            Assert.Equal("movie", (string)item["type"]);
            Assert.Equal("2026-11-18T10:00:00.000Z", (string)item["_ctime"]);
            Assert.False((bool)item["removed"]);
        }

        [Fact]
        public async Task RemoveAndNotifications()
        {
            server.Items["tt0903747"] = Parse(ExistingItem);
            var library = Library();
            await library.SetNotificationsAsync("tt0903747", false, CancellationToken.None);
            Assert.True((bool)server.Items["tt0903747"]["state"]["noNotif"]);
            Assert.False(StremioLibrary.NotificationsEnabled(await library.GetAsync("tt0903747", CancellationToken.None)));

            await library.RemoveAsync("tt0903747", CancellationToken.None);
            Assert.True((bool)server.Items["tt0903747"]["removed"]);
            Assert.Equal("series", (string)server.Items["tt0903747"]["type"]);
            Assert.Empty(await library.GetAllAsync(CancellationToken.None));

            // Removing an item the account does not have writes nothing.
            int puts = server.Puts.Count;
            await library.RemoveAsync("tt404", CancellationToken.None);
            Assert.Equal(puts, server.Puts.Count);
            Assert.Equal(0, library.PendingWrites);
        }

        [Fact]
        public async Task MarkWatchedSeriesAdvancesToNextEpisode()
        {
            var meta = Metas.Series("tt1", "Show", (0, 1), (1, 3), (2, 2));
            var library = Library();
            server.Items["tt1"] = Parse(@"{""_id"":""tt1"",""name"":""Show"",""type"":""series"",""_ctime"":""2020-01-01T00:00:00.000Z"",
                ""state"":{""timeWatched"":5000,""overallTimeWatched"":100,""timesWatched"":0,""timeOffset"":4000,""video_id"":""tt1:1:1"",""lastWatched"":""2030-01-01T00:00:00.000Z""}}");

            await library.MarkWatchedAsync(meta, "tt1:1:1", true, CancellationToken.None);
            var state = server.Items["tt1"]["state"];
            Assert.Equal(1, (int)state["timesWatched"]);
            Assert.Equal("tt1:1:2", (string)state["video_id"]);
            Assert.Equal(1, (int)state["timeOffset"]);
            Assert.Equal(0, (int)state["timeWatched"]);
            Assert.Equal(5100, (int)state["overallTimeWatched"]);
            Assert.Equal("2030-01-01T00:00:00.000Z", (string)state["lastWatched"]);   // max(existing, released)
            Assert.StartsWith("tt1:2:2:6:", (string)state["watched"]);                  // last sorted id, 6 videos

            var entry = await library.GetAsync("tt1", CancellationToken.None);
            Assert.True(library.IsWatched(entry, meta, "tt1:1:1"));
            Assert.False(library.IsWatched(entry, meta, "tt1:1:2"));

            // Marking again does not double count.
            await library.MarkWatchedAsync(meta, "tt1:1:1", true, CancellationToken.None);
            Assert.Equal(1, (int)server.Items["tt1"]["state"]["timesWatched"]);

            // Last episode: no next → timeOffset 0, video_id unchanged.
            await library.MarkWatchedAsync(meta, "tt1:2:2", true, CancellationToken.None);
            state = server.Items["tt1"]["state"];
            Assert.Equal(2, (int)state["timesWatched"]);
            Assert.Equal(0, (int)state["timeOffset"]);

            // Unmark decrements.
            await library.MarkWatchedAsync(meta, "tt1:1:1", false, CancellationToken.None);
            state = server.Items["tt1"]["state"];
            Assert.Equal(1, (int)state["timesWatched"]);
            entry = await library.GetAsync("tt1", CancellationToken.None);
            Assert.False(library.IsWatched(entry, meta, "tt1:1:1"));
            Assert.True(library.IsWatched(entry, meta, "tt1:2:2"));

            await Assert.ThrowsAsync<ArgumentException>(() => library.MarkWatchedAsync(meta, "tt1:9:9", true, CancellationToken.None));
        }

        [Fact]
        public async Task MarkWatchedSeriesLastWatchedUsesRelease()
        {
            var meta = Metas.Series("tt1", "Show", (1, 2));
            await Library().MarkWatchedAsync(meta, "tt1:1:2", true, CancellationToken.None);
            Assert.Equal("2020-01-08T00:00:00.000Z", (string)server.Items["tt1"]["state"]["lastWatched"]);
        }

        [Fact]
        public async Task MarkWatchedMovie()
        {
            var meta = Metas.Movie("tt0133093", "The Matrix");
            var library = Library();
            await library.MarkWatchedAsync(meta, "tt0133093", true, CancellationToken.None);
            var state = server.Items["tt0133093"]["state"];
            Assert.Equal(1, (int)state["flaggedWatched"]);
            Assert.Equal(1, (int)state["timesWatched"]);
            Assert.Equal("2026-11-18T10:00:00.000Z", (string)state["lastWatched"]);
            Assert.True(library.IsWatched(await library.GetAsync("tt0133093", CancellationToken.None), meta, "tt0133093"));

            await library.MarkWatchedAsync(meta, "tt0133093", false, CancellationToken.None);
            state = server.Items["tt0133093"]["state"];
            Assert.Equal(0, (int)state["flaggedWatched"]);
            Assert.Equal(0, (int)state["timesWatched"]);
            Assert.Equal(JTokenType.Null, state["lastWatched"].Type);
            Assert.Equal(0, (int)state["timeOffset"]);
        }

        [Fact]
        public void NextVideoRules()
        {
            var meta = Metas.Series("tt1", "Show", (0, 2), (1, 3), (2, 2));
            var ids = LibraryRules.SortVideos(meta.Videos).Select(v => v.Id).ToList();
            var library = Library();
            string Watched(params string[] watched) =>
                WatchedBitField.ConstructFromArray(ids.Select(id => watched.Contains(id)).ToList(), ids).Serialize();
            LibraryEntry Entry(string current, string watched) => new LibraryEntry { Id = "tt1", Type = MediaType.Series, State = new LibraryState { VideoId = current, Watched = watched } };

            // Current unwatched → itself.
            Assert.Equal("tt1:1:2", library.NextVideo(Entry("tt1:1:2", Watched("tt1:1:1")), meta).Id);
            // Current watched → step forward past watched ones.
            Assert.Equal("tt1:2:1", library.NextVideo(Entry("tt1:1:2", Watched("tt1:1:1", "tt1:1:2", "tt1:1:3")), meta).Id);
            // No current → first unwatched regular episode (specials skipped).
            Assert.Equal("tt1:1:1", library.NextVideo(Entry(null, null), meta).Id);
            // Everything regular watched → caught up (specials are not offered while regular episodes exist).
            var all = Watched("tt1:1:1", "tt1:1:2", "tt1:1:3", "tt1:2:1", "tt1:2:2");
            Assert.Null(library.NextVideo(Entry("tt1:2:2", all), meta));
            Assert.Equal(LibraryStatus.CaughtUp, library.GetStatus(Entry("tt1:2:2", all), meta));
            Assert.Equal(LibraryStatus.Unwatched, library.GetStatus(Entry(null, null), meta));
            var watching = Entry("tt1:1:1", null);
            watching.State.TimeOffsetMs = 1001;
            Assert.Equal(LibraryStatus.Watching, library.GetStatus(watching, meta));

            // Unreleased episodes are skipped.
            var future = Metas.Series("tt2", "Future", (1, 2));
            ((VideoInfo)future.Videos[1]).Released = clock.AddDays(1);
            var fids = future.Videos.Select(v => v.Id).ToList();
            var fw = WatchedBitField.ConstructFromArray(new[] { true, false }, fids).Serialize();
            Assert.Null(library.NextVideo(new LibraryEntry { Type = MediaType.Series, State = new LibraryState { VideoId = "tt2:1:1", Watched = fw } }, future));

            // C7: current episode missing from metadata → first later by season/episode.
            var after = LibraryRules.NextReleasedVideoAfter(meta, new VideoInfo { Id = "tt1:1:99", Season = 1, Episode = 2 }, clock);
            Assert.Equal("tt1:1:3", after.Id);
            // Season 0 current → specials stay candidates.
            Assert.Equal("tt1:0:2", LibraryRules.NextReleasedVideoAfter(meta, meta.Videos[0], clock).Id);
        }

        [Fact]
        public void StatusAndProgressForMovies()
        {
            var entry = new LibraryEntry { Type = MediaType.Movie, State = new LibraryState { TimeOffsetMs = 1234567, DurationMs = 7200000 } };
            Assert.Equal(LibraryStatus.Watching, LibraryRules.Status(entry, null, clock));
            Assert.Equal(17.1, StremioLibrary.ProgressPercent(entry));
            entry.State.TimeOffsetMs = 1000;
            Assert.Equal(LibraryStatus.Library, LibraryRules.Status(entry, null, clock));
        }

        [Fact]
        public async Task NewEpisodesFiltersAndAcknowledges()
        {
            var show = Metas.Series("tt1", "Show One", (1, 4));          // releases 2020-01-01, 01-08, 01-15, 01-22
            var other = Metas.Series("tt2", "Other", (1, 2));
            var muted = Metas.Series("tt3", "Muted", (1, 2));
            catalog.Metas["tt1"] = show; catalog.Metas["tt2"] = other; catalog.Metas["tt3"] = muted;
            var ids1 = show.Videos.Select(v => v.Id).ToList();
            server.Items["tt1"] = Parse(@"{""_id"":""tt1"",""name"":""Show One"",""type"":""series"",""removed"":false,""temp"":false,""_ctime"":""2019-01-01T00:00:00.000Z"",
                ""state"":{""lastWatched"":""2020-01-05T00:00:00.000Z"",""watched"":""" + WatchedBitField.ConstructFromArray(new[] { true, false, true, false }, ids1).Serialize() + @"""}}");
            server.Items["tt2"] = Parse(@"{""_id"":""tt2"",""name"":""Other"",""type"":""series"",""removed"":false,""temp"":false,""_ctime"":""2020-01-03T00:00:00.000Z"",""state"":{}}");
            server.Items["tt3"] = Parse(@"{""_id"":""tt3"",""name"":""Muted"",""type"":""series"",""removed"":false,""temp"":false,""_ctime"":""2019-01-01T00:00:00.000Z"",""state"":{""noNotif"":true}}");
            server.Items["tt4"] = Parse(@"{""_id"":""tt4"",""name"":""Gone"",""type"":""series"",""removed"":true,""temp"":false,""_ctime"":""2019-01-01T00:00:00.000Z"",""state"":{}}");
            var library = Library();
            var checkedVideos = new List<string>();
            Func<MetaDetail, VideoInfo, CancellationToken, Task<bool>> available = (meta, video, token) =>
            {
                lock (checkedVideos) checkedVideos.Add(video.Id);
                return Task.FromResult(video.Id != "tt1:1:4");   // no surviving stream for E4
            };

            var found = await library.GetNewEpisodesAsync(available, CancellationToken.None);
            // tt1: E1 released before lastWatched baseline, E2 unwatched + after baseline? released 01-08 > 01-05 yes,
            // E3 watched, E4 unavailable. tt2: E2 (01-08) after _ctime 01-03; E1 (01-01) before.
            Assert.Equal(new[] { "tt1:1:2", "tt2:1:2" }.OrderBy(x => x), found.Select(f => f.Video.Id).OrderBy(x => x));
            Assert.DoesNotContain("tt3:1:1", checkedVideos);
            Assert.True(found.Zip(found.Skip(1), (a, b) => a.Video.Released >= b.Video.Released).All(x => x));

            // Query filter is a case-insensitive substring of the name.
            var filtered = await library.GetNewEpisodesAsync(available, CancellationToken.None, query: "show");
            Assert.Equal(new[] { "tt1:1:2" }, filtered.Select(f => f.Video.Id));

            // Acknowledge, then sinceLastCheck skips them.
            await library.GetNewEpisodesAsync(available, CancellationToken.None, acknowledge: true);
            Assert.NotNull(store.Json(AccountKeys.StremioAlerts)["announced"]["tt1:1:2"]);
            var since = await library.GetNewEpisodesAsync(available, CancellationToken.None, sinceLastCheck: true);
            Assert.Empty(since);
            var without = await library.GetNewEpisodesAsync(available, CancellationToken.None);
            Assert.Equal(2, without.Count);

            Assert.True(library.IsNewEpisode(show.Videos[1], await library.GetAsync("tt1", CancellationToken.None)));
            Assert.False(library.IsNewEpisode(show.Videos[0], await library.GetAsync("tt1", CancellationToken.None)));
        }

        [Fact]
        public async Task NewEpisodesLimit()
        {
            var show = Metas.Series("tt1", "Big", (1, 30));
            catalog.Metas["tt1"] = show;
            server.Items["tt1"] = Parse(@"{""_id"":""tt1"",""name"":""Big"",""type"":""series"",""_ctime"":""2019-01-01T00:00:00.000Z"",""state"":{}}");
            var found = await Library().GetNewEpisodesAsync((m, v, t) => Task.FromResult(true), CancellationToken.None, limit: 5);
            Assert.Equal(5, found.Count);
            Assert.Equal("tt1:1:5", found[0].Video.Id);   // newest first
        }

        [Fact]
        public async Task LaunchServesTheSavedLibraryAtOnceAndRefreshesItBehind()
        {
            // First run fills the saved copy.
            server.Items["tt0133093"] = Parse(@"{""_id"":""tt0133093"",""name"":""The Matrix"",""type"":""movie"",""_ctime"":""2020-01-01T00:00:00.000Z"",""state"":{""timesWatched"":0}}");
            Assert.Single(await Library().GetAllAsync(CancellationToken.None));

            // Next launch: the server now has a second item, but the saved copy answers first (no wait on Stremio)...
            server.Items["tt0111161"] = Parse(@"{""_id"":""tt0111161"",""name"":""The Shawshank Redemption"",""type"":""movie"",""_ctime"":""2020-01-01T00:00:00.000Z"",""state"":{""timesWatched"":0}}");
            var relaunched = Library();
            Assert.Single(await relaunched.GetAllAsync(CancellationToken.None));
            // ...and the refresh behind it brings the new item in for the next read.
            var refreshed = false;
            for (var i = 0; i < 100 && !refreshed; i++)
            {
                await Task.Delay(20);
                refreshed = (await relaunched.GetAllAsync(CancellationToken.None)).Count == 2;
            }
            Assert.True(refreshed);
        }

        [Fact]
        public async Task OfflineEditsAreQueuedOverlaidAndReplayed()
        {
            var meta = Metas.Movie("tt0133093", "The Matrix");
            server.Items["tt0133093"] = Parse(@"{""_id"":""tt0133093"",""name"":""The Matrix"",""type"":""movie"",""custom"":1,""_ctime"":""2020-01-01T00:00:00.000Z"",""state"":{""timesWatched"":0}}");
            var library = Library();
            Assert.Single(await library.GetAllAsync(CancellationToken.None));   // fills the cache

            server.Handler.Offline = true;
            await library.MarkWatchedAsync(meta, "tt0133093", true, CancellationToken.None);   // does not throw
            Assert.Equal(1, library.PendingWrites);
            Assert.Contains("network unreachable", library.LastSyncError);
            var offlineEntry = (await library.GetAllAsync(CancellationToken.None)).Single();
            Assert.Equal(1, offlineEntry.State.FlaggedWatched);                       // overlay shows the change
            Assert.Equal(0, (int)server.Items["tt0133093"]["state"]["timesWatched"]);

            // Survives a restart: a new instance loads the outbox from the store.
            server.Handler.Offline = false;
            var restarted = Library();
            await restarted.InitializeAsync(CancellationToken.None);
            Assert.Equal(1, restarted.PendingWrites);
            Assert.True(await restarted.FlushAsync(CancellationToken.None));
            Assert.Equal(0, restarted.PendingWrites);
            Assert.Equal(1, (int)server.Items["tt0133093"]["state"]["timesWatched"]);
            Assert.Equal(1, (int)server.Items["tt0133093"]["custom"]);
            Assert.Equal(1, (await restarted.GetAllAsync(CancellationToken.None)).Single().State.TimesWatched);
        }

        [Fact]
        public async Task PermanentErrorsAreDroppedAfterRetries()
        {
            var meta = Metas.Movie("tt9", "Nine");
            server.Override = r => r.Url.EndsWith("/datastorePut") ? FixtureHandler.Json(new { error = new { message = "bad item" } }) : null;
            var library = Library();
            await library.AddAsync(meta, CancellationToken.None);                 // attempt 1
            for (int i = 2; i < OutboxQueue.MaxAttempts; i++)
                Assert.False(await library.FlushAsync(CancellationToken.None));   // attempts 2..4
            Assert.Equal(1, library.PendingWrites);
            Assert.Contains("bad item", library.LastSyncError);
            await library.FlushAsync(CancellationToken.None);
            Assert.Equal(0, library.PendingWrites);
        }

        [Fact]
        public async Task RejectedSessionKeepsWritesUntilRelinked()
        {
            auth.Key = "stale-key";   // server answers HTTP 200 {error:"Session does not exist"}
            var library = Library();
            await library.AddAsync(Metas.Movie("tt9", "Nine"), CancellationToken.None);
            for (int i = 0; i < OutboxQueue.MaxAttempts * 2; i++) Assert.False(await library.FlushAsync(CancellationToken.None));
            Assert.Equal(1, library.PendingWrites);
            Assert.Contains("Session does not exist", library.LastSyncError);
            auth.Key = "key-1";
            Assert.True(await library.FlushAsync(CancellationToken.None));
            Assert.True(server.Items.ContainsKey("tt9"));
        }

        [Fact]
        public async Task SignOutDropsCacheAndQueuedWrites()
        {
            store.Values[AccountKeys.StremioAuth] = "{\"authKey\":\"key-1\"}";
            server.Items["tt1"] = Parse(@"{""_id"":""tt1"",""name"":""Old account item"",""type"":""movie"",""state"":{}}");
            var account = new StremioAccount(server.Client(), store, () => clock);
            var library = new StremioLibrary(server.Client(), account, store, catalog, () => clock);
            Assert.Single(await library.GetAllAsync(CancellationToken.None));
            server.Handler.Offline = true;
            await library.AddAsync(Metas.Movie("tt9", "Nine"), CancellationToken.None);
            Assert.Equal(1, library.PendingWrites);

            await account.SignOutAsync(CancellationToken.None);
            Assert.Equal(0, library.PendingWrites);
            Assert.Empty(await library.GetAllAsync(CancellationToken.None));

            // Another account links; nothing from the previous one is replayed into it.
            server.Handler.Offline = false;
            server.Items.Clear();
            server.AuthKey = "key-2";
            server.Handler.On(System.Net.Http.HttpMethod.Get, FakeStremioServer.Link + "/read", r => FixtureHandler.Json(new { result = new { authKey = "key-2" } }));
            Assert.True(await account.CompleteLinkAsync("NEW", CancellationToken.None));
            Assert.True(await library.FlushAsync(CancellationToken.None));
            Assert.Empty(server.Puts);
            var restarted = new StremioLibrary(server.Client(), account, store, catalog, () => clock);
            await restarted.InitializeAsync(CancellationToken.None);
            Assert.Equal(0, restarted.PendingWrites);
        }

        [Fact]
        public async Task NotSignedInKeepsWritesQueued()
        {
            auth.Key = null;
            var library = Library();
            await library.AddAsync(Metas.Movie("tt9", "Nine"), CancellationToken.None);
            for (int i = 0; i < 10; i++) await library.FlushAsync(CancellationToken.None);
            Assert.Equal(1, library.PendingWrites);
            auth.Key = "key-1";
            Assert.True(await library.FlushAsync(CancellationToken.None));
            Assert.True(server.Items.ContainsKey("tt9"));
        }

        [Fact]
        public async Task SimultaneousLibraryReadsShareOneFetch()
        {
            server.Items["tt0903747"] = Parse(ExistingItem);
            var library = Library();
            await library.GetAllAsync(CancellationToken.None);   // the launch fetch
            var reads = 0;
            using (var entered = new ManualResetEventSlim())
            using (var held = new ManualResetEventSlim())
            {
                server.Override = request =>
                {
                    if (request.Url.EndsWith("/datastoreGet", StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref reads);
                        entered.Set();
                        held.Wait(TimeSpan.FromSeconds(10));
                    }
                    return null;
                };
                var home = Task.Run(() => library.GetAllAsync(CancellationToken.None));
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));   // Home's fetch is on the wire
                var search = library.GetAllAsync(CancellationToken.None);   // Search asks meanwhile
                held.Set();
                var lists = await Task.WhenAll(home, search);
                Assert.Equal(1, reads);
                Assert.All(lists, list => Assert.Single(list));
            }
        }

        [Fact]
        public async Task WatchedStateIsDecodedOncePerSeriesAndFollowsChanges()
        {
            var meta = Metas.Series("tt1", "Show", (1, 3));
            var library = Library();
            server.Items["tt1"] = Parse(@"{""_id"":""tt1"",""name"":""Show"",""type"":""series"",""_ctime"":""2020-01-01T00:00:00.000Z"",""state"":{""video_id"":""tt1:1:1""}}");
            await library.MarkWatchedAsync(meta, "tt1:1:2", true, CancellationToken.None);
            var entry = await library.GetAsync("tt1", CancellationToken.None);
            var ids = new[] { "tt1:1:1", "tt1:1:2", "tt1:1:3" };
            Assert.Equal(new[] { false, true, false }, ids.Select(id => library.IsWatched(entry, meta, id)));
            await library.MarkWatchedAsync(meta, "tt1:1:3", true, CancellationToken.None);
            entry = await library.GetAsync("tt1", CancellationToken.None);   // a new watched string: decoded again
            Assert.Equal(new[] { false, true, true }, ids.Select(id => library.IsWatched(entry, meta, id)));
            Assert.False(library.IsWatched(entry, meta, "tt1:9:9"));
        }
    }
}
