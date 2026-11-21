using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Api;
using Ru7100.Fakes;
using Xunit;

namespace Ru7100.Tests.Api
{
    public class RecommendationTests
    {
        private static readonly string[] RecommendationsKey = { ApiScopes.Recommendations };
        private static readonly string[] PlaybackKey = { ApiScopes.Playback };

        private static string Item(string id = "tt0903747", string extra = "") =>
            "{\"id\":\"" + id + "\",\"type\":\"series\",\"name\":\"Breaking Bad\",\"reason\":\"Crime and Drama\"" + extra + "}";

        private static string Rows(params string[] items) =>
            "{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[" + string.Join(",", items) + "]}]}";

        [Fact]
        public async Task SetThenGetRoundTripsAndVersions()
        {
            var fixture = new ApiFixture();
            var empty = await fixture.Ok("recommendations.get", null, RecommendationsKey);
            Assert.Equal(0, (long)empty["version"]);
            Assert.Empty((JArray)empty["rows"]);

            var first = await fixture.Ok("recommendations.set",
                "{\"generatedAt\":\"2026-11-18T09:00:00+00:00\",\"source\":\"mac\",\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[" +
                Item(extra: ",\"score\":0.8123,\"poster\":\"https://img.example/bb.jpg\"") + "]},{\"id\":\"new-for-you\",\"title\":\"New for you\",\"items\":[" +
                "{\"id\":\"tt1727587\",\"type\":\"movie\",\"name\":\"Sintel\"}]}]}", RecommendationsKey);
            Assert.Equal(1, (long)first["version"]);
            Assert.Equal(2, (int)first["rows"]);
            Assert.Equal(2, (int)first["items"]);

            var stored = await fixture.Ok("recommendations.get", null, RecommendationsKey);
            Assert.Equal(new[] { "version", "generatedAt", "source", "publishedAt", "rows" }, ((JObject)stored).Properties().Select(p => p.Name));
            Assert.Equal("2026-11-18T09:00:00+00:00", (string)stored["generatedAt"]);
            Assert.Equal("For You", (string)stored["rows"][0]["title"]);
            var item = stored["rows"][0]["items"][0];
            Assert.Equal(new[] { "id", "type", "name", "poster", "reason", "score" }, ((JObject)item).Properties().Select(p => p.Name));
            Assert.Equal(0.8123, (double)item["score"]);
            Assert.Equal("", (string)stored["rows"][1]["items"][0]["reason"]);

            var second = await fixture.Ok("recommendations.set", Rows(Item()), RecommendationsKey);
            Assert.Equal(2, (long)second["version"]);
        }

        [Fact]
        public async Task ScopesAreIndependent()
        {
            var fixture = new ApiFixture();
            foreach (var command in new[] { "recommendations.set", "recommendations.get", "recommendations.feedback.list" })
            {
                Assert.Equal(403, (await fixture.Run(command, command == "recommendations.set" ? Rows(Item()) : "{}", PlaybackKey)).StatusCode);
                Assert.Equal(403, (await fixture.Run(command, command == "recommendations.set" ? Rows(Item()) : "{}", new[] { ApiScopes.Library })).StatusCode);
            }
            foreach (var command in new[] { "playback.play", "queue.set", "library.add", "addons.install", "account.link_begin" })
                Assert.Equal(403, (await fixture.Run(command, "{}", RecommendationsKey)).StatusCode);
            Assert.Equal(0, (await fixture.Recommendations.GetAsync(CancellationToken.None)).Version);
            Assert.Empty(fixture.Playback.Calls);
        }

        [Theory]
        [InlineData("{}", "missing_argument")]
        [InlineData("{\"rows\":[]}", "invalid_argument")]
        [InlineData("{\"rows\":{}}", "invalid_argument")]
        [InlineData("{\"rows\":[1]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"title\":\"For You\",\"items\":[]}]}", "missing_argument")]
        [InlineData("{\"rows\":[{\"id\":\"For You\",\"title\":\"For You\",\"items\":[]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\"}]}", "missing_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"}],\"color\":\"red\"}]}", "irrelevant_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"0903747\",\"type\":\"series\",\"name\":\"x\"}]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"anime\",\"name\":\"x\"}]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\"}]}]}", "missing_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\",\"poster\":\"javascript:alert(1)\"}]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\",\"score\":\"high\"}]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\",\"year\":2008}]}]}", "irrelevant_argument")]
        [InlineData("{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"},{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"}]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"a\",\"title\":\"A\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"}]},{\"id\":\"a\",\"title\":\"B\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"}]}]}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"a\",\"title\":\"A\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"}]}],\"expectedVersion\":1.5}", "invalid_argument")]
        [InlineData("{\"rows\":[{\"id\":\"a\",\"title\":\"A\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\"}]}],\"theme\":\"dark\"}", "irrelevant_argument")]
        public async Task InvalidPushesAreRejectedAndKeepTheLastGoodSnapshot(string args, string code)
        {
            var fixture = new ApiFixture();
            await fixture.Ok("recommendations.set", Rows(Item()), RecommendationsKey);
            var response = await fixture.Run("recommendations.set", args, RecommendationsKey);
            Assert.True(response.StatusCode == 400, args + ": " + response.Body);
            Assert.Equal(code, (string)response.Body["error"]["code"]);
            var kept = await fixture.Ok("recommendations.get", null, RecommendationsKey);
            Assert.Equal(1, (long)kept["version"]);
            Assert.Equal("tt0903747", (string)kept["rows"][0]["items"][0]["id"]);
        }

        [Theory]
        [InlineData("reason", 141)]
        [InlineData("name", 201)]
        public async Task OverlongTextIsRejected(string field, int length)
        {
            var args = Rows("{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"x\",\"" + field + "\":\"" + new string('a', length) + "\"}")
                .Replace("\"name\":\"x\",\"name\"", "\"name\"");
            var response = await new ApiFixture().Run("recommendations.set", args, RecommendationsKey);
            Assert.Equal(400, response.StatusCode);
        }

        [Fact]
        public async Task SizeCapsAreEnforced()
        {
            var fixture = new ApiFixture();
            var tooManyInRow = Rows(Enumerable.Range(0, RecommendationLimits.MaxItemsPerRow + 1).Select(i => Item("tt" + (1000000 + i))).ToArray());
            Assert.Equal(400, (await fixture.Run("recommendations.set", tooManyInRow, RecommendationsKey)).StatusCode);

            var row = "{\"id\":\"r{0}\",\"title\":\"Row\",\"items\":[" + string.Join(",", Enumerable.Range(0, 50).Select(i => Item("tt" + (1000000 + i)))) + "]}";
            var sevenRows = "{\"rows\":[" + string.Join(",", Enumerable.Range(0, 7).Select(i => row.Replace("{0}", i.ToString()))) + "]}";
            var response = await fixture.Run("recommendations.set", sevenRows, RecommendationsKey);
            Assert.Equal(400, response.StatusCode);
            Assert.Contains("300", (string)response.Body["error"]["message"]);

            var tooManyRows = "{\"rows\":[" + string.Join(",", Enumerable.Range(0, 21).Select(i => "{\"id\":\"r" + i + "\",\"title\":\"R\",\"items\":[" + Item() + "]}")) + "]}";
            Assert.Equal(400, (await fixture.Run("recommendations.set", tooManyRows, RecommendationsKey)).StatusCode);
        }

        [Fact]
        public async Task ExpectedVersionGuardsAgainstStalePushes()
        {
            var fixture = new ApiFixture();
            await fixture.Ok("recommendations.set", Rows(Item()), RecommendationsKey);
            var stale = await fixture.Run("recommendations.set", Rows(Item()).TrimEnd('}') + ",\"expectedVersion\":0}", RecommendationsKey);
            Assert.Equal(409, stale.StatusCode);
            Assert.Equal("version_conflict", (string)stale.Body["error"]["code"]);
            var fresh = await fixture.Ok("recommendations.set", Rows(Item()).TrimEnd('}') + ",\"expectedVersion\":1}", RecommendationsKey);
            Assert.Equal(2, (long)fresh["version"]);
        }

        [Fact]
        public async Task SnapshotPersistsInTheKeyValueStoreAcrossRestarts()
        {
            var store = new InMemoryStore();
            var first = new KeyValueRecommendationsStore(store);
            var rows = RecommendationValidator.ParseRows(JArray.Parse("[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[" + Item() + "]}]"));
            await first.ReplaceAsync(new RecommendationSnapshot { Rows = rows, Source = "test" }, null, CancellationToken.None);

            var restarted = new KeyValueRecommendationsStore(store);
            var snapshot = await restarted.GetAsync(CancellationToken.None);
            Assert.Equal(1, snapshot.Version);
            Assert.Equal("Breaking Bad", snapshot.Rows[0].Items[0].Name);
            Assert.NotNull(snapshot.PublishedAt);
        }

        [Fact]
        public async Task CorruptCurrentSnapshotFallsBackToThePreviousGoodOne()
        {
            var store = new InMemoryStore();
            var recommendations = new KeyValueRecommendationsStore(store);
            var rows = RecommendationValidator.ParseRows(JArray.Parse("[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[" + Item() + "]}]"));
            await recommendations.ReplaceAsync(new RecommendationSnapshot { Rows = rows, Source = "one" }, null, CancellationToken.None);
            await recommendations.ReplaceAsync(new RecommendationSnapshot { Rows = rows, Source = "two" }, null, CancellationToken.None);
            await store.WriteAsync(KeyValueRecommendationsStore.SnapshotKey, "{ truncated", CancellationToken.None);
            var snapshot = await recommendations.GetAsync(CancellationToken.None);
            Assert.Equal("one", snapshot.Source);
            Assert.Equal(1, snapshot.Version);
        }

        private static Task<FeedbackEvent> Record(KeyValueRecommendationsStore store, string kind, string imdbId = "tt0903747", string videoId = null, long? position = null, long? duration = null) =>
            store.RecordFeedbackAsync(new FeedbackEvent { ImdbId = imdbId, VideoId = videoId, Kind = kind, PositionMs = position, DurationMs = duration }, CancellationToken.None);

        [Fact]
        public async Task FeedbackEventsArePulledAfterTheAckCursor()
        {
            var fixture = new ApiFixture();
            await Record(fixture.Recommendations, FeedbackKinds.PlayStarted, videoId: "tt0903747:1:1", position: 0, duration: 2_700_000);
            await Record(fixture.Recommendations, FeedbackKinds.Completed, videoId: "tt0903747:1:1", position: 2_650_000, duration: 2_700_000);
            await Record(fixture.Recommendations, FeedbackKinds.NotInterested, imdbId: "tt1727587");

            var page = await fixture.Ok("recommendations.feedback.list", "{\"limit\":2}", RecommendationsKey);
            Assert.Equal(new[] { "events", "lastSequence", "ackedSequence", "latestSequence", "hasMore", "gap" }, ((JObject)page).Properties().Select(p => p.Name));
            var first = page["events"][0];
            Assert.Equal(new[] { "sequence", "imdbId", "videoId", "kind", "positionMs", "durationMs", "at" }, ((JObject)first).Properties().Select(p => p.Name));
            Assert.Equal("play_started", (string)first["kind"]);
            Assert.Equal("2026-11-18T12:00:00+00:00", (string)first["at"]);
            Assert.Equal(2, (long)page["lastSequence"]);
            Assert.True((bool)page["hasMore"]);
            Assert.False((bool)page["gap"]);

            var acked = await fixture.Ok("recommendations.feedback.ack", "{\"sequence\":2}", RecommendationsKey);
            Assert.Equal(2, (long)acked["ackedSequence"]);
            var rest = await fixture.Ok("recommendations.feedback.list", null, RecommendationsKey);
            Assert.Equal("tt1727587", (string)rest["events"].Single()["imdbId"]);
            Assert.Equal(JTokenType.Null, rest["events"][0]["videoId"].Type);
            Assert.False((bool)rest["hasMore"]);

            // The cursor never moves back, and acknowledged events are gone.
            Assert.Equal(2, (long)(await fixture.Ok("recommendations.feedback.ack", "{\"sequence\":1}", RecommendationsKey))["ackedSequence"]);
            Assert.Single((JArray)(await fixture.Ok("recommendations.feedback.list", "{\"afterSequence\":0}", RecommendationsKey))["events"]);

            await fixture.Ok("recommendations.feedback.ack", "{\"sequence\":3}", RecommendationsKey);
            var none = await fixture.Ok("recommendations.feedback.list", null, RecommendationsKey);
            Assert.Empty((JArray)none["events"]);
            Assert.Equal(3, (long)none["lastSequence"]);
            Assert.Equal(3, (long)none["latestSequence"]);

            Assert.Equal(400, (await fixture.Run("recommendations.feedback.ack", "{\"sequence\":4}", RecommendationsKey)).StatusCode);
            Assert.Equal(400, (await fixture.Run("recommendations.feedback.ack", "{}", RecommendationsKey)).StatusCode);
            Assert.Equal(400, (await fixture.Run("recommendations.feedback.list", "{\"limit\":0}", RecommendationsKey)).StatusCode);
            Assert.Equal(403, (await fixture.Run("recommendations.feedback.ack", "{\"sequence\":3}", PlaybackKey)).StatusCode);
        }

        [Fact]
        public async Task FeedbackIsValidatedWhenTheTvRecordsIt()
        {
            var store = new KeyValueRecommendationsStore(new InMemoryStore());
            await Assert.ThrowsAsync<ApiException>(() => Record(store, "meh"));
            await Assert.ThrowsAsync<ApiException>(() => Record(store, FeedbackKinds.ThumbsUp, imdbId: "Breaking Bad"));
            await Assert.ThrowsAsync<ApiException>(() => Record(store, FeedbackKinds.Progress, position: -1));
            foreach (var kind in FeedbackKinds.All) await Record(store, kind);
            Assert.Equal(FeedbackKinds.All.Length, (await store.ListFeedbackAsync(null, 100, CancellationToken.None)).Events.Count);
        }

        [Fact]
        public async Task RetentionCapDropsTheOldestUnreadEventsAndReportsTheGap()
        {
            var store = new KeyValueRecommendationsStore(new InMemoryStore());
            for (var i = 0; i < RecommendationLimits.MaxFeedbackKept + 5; i++) await Record(store, FeedbackKinds.Progress, position: i);
            var page = await store.ListFeedbackAsync(null, 10, CancellationToken.None);
            Assert.Equal(6, page.Events[0].Sequence);
            Assert.True(page.Gap);
            Assert.Equal(RecommendationLimits.MaxFeedbackKept + 5, page.LatestSequence);
            var restarted = await new KeyValueRecommendationsStore(new InMemoryStore()).ListFeedbackAsync(null, 10, CancellationToken.None);
            Assert.False(restarted.Gap);
        }
    }
}
