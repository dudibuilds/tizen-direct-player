using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Api;
using Xunit;

namespace Ru7100.Tests.Api
{
    /// Pins the wire shapes agents depend on (mirroring samsung-stremio-spotify-mcp results).
    public class ResultShapeTests
    {
        private static string[] Keys(JToken token) => ((JObject)token).Properties().Select(p => p.Name).ToArray();

        [Fact]
        public async Task SearchRanksByTitleSimilarityWithOldFields()
        {
            var result = (JArray)await new ApiFixture().Ok("library.search", "{\"query\":\"breaking\"}");
            var first = result[0];
            Assert.Equal(new[] { "id", "type", "name", "releaseInfo", "poster", "posterShape", "matchScore" }, Keys(first));
            Assert.Equal("tt0903747", (string)first["id"]);
            Assert.Equal("series", (string)first["type"]);
            Assert.Equal(0.9, (double)first["matchScore"]);
            Assert.Equal(JTokenType.Null, first["poster"].Type);
        }

        [Fact]
        public void TitleHelpersMatchTheOldRules()
        {
            Assert.Equal(1, TitleResolver.TitleSimilarity("Breaking Bad!", "breaking   bad"));
            Assert.Equal(0.9, TitleResolver.TitleSimilarity("bad", "Breaking Bad"));
            Assert.Equal(0.5, TitleResolver.TitleSimilarity("space dog", "Dog Days"));
            var parsed = TitleResolver.ParseEpisode("Breaking Bad season 2 episode 5 please");
            Assert.Equal(("Breaking Bad please", (int?)2, (int?)5), parsed);
            Assert.Equal(("The Office", (int?)3, (int?)12), TitleResolver.ParseEpisode("The Office S03E12"));
            Assert.Equal(("Dark", (int?)null, (int?)null), TitleResolver.ParseEpisode(" Dark "));
        }

        [Fact]
        public async Task LibraryListReportsStateProgressAndNextEpisode()
        {
            var result = (JArray)await new ApiFixture().Ok("library.list");
            var byName = result.ToDictionary(item => (string)item["name"]);
            Assert.Equal(new[] { "id", "name", "type", "state", "currentVideoId", "progress", "nextEpisode", "notifications" }, Keys(result[0]));
            Assert.Equal("watching", (string)byName["Breaking Bad"]["state"]);
            Assert.Equal(11.1, (double)byName["Breaking Bad"]["progress"]);
            Assert.Equal("tt0903747:2:1", (string)byName["Breaking Bad"]["nextEpisode"]["id"]);
            Assert.Equal("caught_up", (string)byName["Space Force"]["state"]);
            Assert.Equal("unwatched", (string)byName["Fresh Show"]["state"]);
            Assert.Equal(new[] { "id", "season", "episode", "released", "title" }, Keys(byName["Fresh Show"]["nextEpisode"]));
            Assert.Equal("library", (string)byName["Sintel"]["state"]);
            Assert.Equal(JTokenType.Null, byName["Sintel"]["nextEpisode"].Type);

            var watching = (JArray)await new ApiFixture().Ok("library.list", "{\"status\":\"caught_up\"}");
            Assert.Equal("Space Force", (string)watching.Single()["name"]);
        }

        [Fact]
        public async Task NewEpisodesNeedAPlayableStreamAndRespectAcknowledge()
        {
            var fixture = new ApiFixture();
            fixture.Sources.Unavailable.Add("tt9999001:1:2");
            var result = await fixture.Ok("library.new_episodes", "{\"sinceLastCheck\":true,\"acknowledge\":true}");
            Assert.Equal(3, (int)result["count"]);
            var episodes = (JArray)result["episodes"];
            Assert.Equal(new[] { "tt9999001:1:4", "tt9999001:1:3", "tt9999001:1:1" }, episodes.Select(e => (string)e["video"]["id"]));
            Assert.Equal(new[] { "series", "seriesId", "video", "bestStream" }, Keys(episodes[0]));
            Assert.Equal(new[] { "addon", "name", "title", "filename", "quality", "seeders", "sizeBytes", "codecs", "score" }, Keys(episodes[0]["bestStream"]));
            Assert.Equal(123.5, (double)episodes[0]["bestStream"]["score"]);
            Assert.Equal("h264", (string)episodes[0]["bestStream"]["codecs"]["video"]);

            var again = await fixture.Ok("library.new_episodes", "{\"sinceLastCheck\":true}");
            Assert.Equal(0, (int)again["count"]);
            var all = await fixture.Ok("library.new_episodes");
            Assert.Equal(3, (int)all["count"]);
        }

        [Fact]
        public async Task NotificationsOffHidesNewEpisodes()
        {
            var fixture = new ApiFixture();
            var update = await fixture.Ok("library.set_notifications", "{\"queryOrId\":\"Fresh Show\",\"notifications\":false}");
            Assert.Equal(new[] { "ok", "action", "id", "name", "inLibrary", "notifications" }, Keys(update));
            Assert.False((bool)update["notifications"]);
            Assert.Equal(0, (int)(await fixture.Ok("library.new_episodes"))["count"]);
        }

        [Fact]
        public async Task LibraryAddRemoveAndMarkWatched()
        {
            var fixture = new ApiFixture();
            var added = await fixture.Ok("library.add", "{\"queryOrId\":\"Dog Days\"}");
            Assert.True((bool)added["inLibrary"]);
            Assert.Equal("tt0100000", (string)added["id"]);
            var removed = await fixture.Ok("library.remove", "{\"queryOrId\":\"tt0100000\"}");
            Assert.False((bool)removed["inLibrary"]);

            var marked = await fixture.Ok("library.mark_watched", "{\"queryOrId\":\"Fresh Show\",\"season\":1,\"episode\":1}");
            Assert.Equal(new[] { "ok", "name", "video", "watched", "nextEpisode" }, Keys(marked));
            Assert.Equal("tt9999001:1:2", (string)marked["nextEpisode"]["id"]);
            var movie = await fixture.Ok("library.mark_watched", "{\"queryOrId\":\"Sintel\",\"watched\":true}");
            Assert.Equal(JTokenType.Null, movie["nextEpisode"].Type);
        }

        [Fact]
        public async Task AmbiguousTitlesReturnCandidates()
        {
            var fixture = new ApiFixture();
            var update = await fixture.Ok("library.add", "{\"queryOrId\":\"space dog\"}");
            Assert.Equal("ambiguous", (string)update["status"]);
            Assert.Equal(2, ((JArray)update["candidates"]).Count);

            var play = await fixture.Ok("playback.play", "{\"query\":\"space dog\"}");
            Assert.Equal("ambiguous", (string)play["status"]);
            Assert.Equal(0.5, (double)play["candidates"][0]["matchScore"]);

            var queue = await fixture.Ok("queue.set", "{\"items\":[{\"query\":\"Breaking Bad S1E1\"},{\"query\":\"space dog\"}]}");
            Assert.Equal("ambiguous", (string)queue["status"]);
            Assert.Equal(1, (int)queue["itemIndex"]);
        }

        [Fact]
        public async Task UnknownTitleIs404()
        {
            var response = await new ApiFixture().Run("library.add", "{\"queryOrId\":\"zzzz\"}");
            Assert.Equal(404, response.StatusCode);
            Assert.Equal("not_found", (string)response.Body["error"]["code"]);
        }

        [Fact]
        public async Task PlayNeedsConfirmationForBareTitlesAndStatusShowsRisingPosition()
        {
            var fixture = new ApiFixture();
            var proposal = await fixture.Ok("playback.play", "{\"query\":\"Fresh Show\"}");
            Assert.Equal("confirmation_required", (string)proposal["status"]);
            Assert.Equal("Play S1E1?", (string)proposal["proposal"]);

            var playing = await fixture.Ok("playback.play", "{\"query\":\"Fresh Show\",\"confirmed\":true}");
            Assert.Equal("playing", (string)playing["status"]);
            Assert.Equal("PLAYING", (string)playing["state"]);
            Assert.Equal("Fresh Show", (string)playing["series"]);
            Assert.Equal("1080p", (string)playing["selected"]["quality"]);
            Assert.Empty((JArray)playing["fallbacksTried"]);

            var first = await fixture.Ok("status", "{\"scope\":\"stremio_playback\"}");
            Assert.True((bool)first["active"]);
            Assert.Equal("PLAYING", (string)first["session"]["state"]);
            fixture.Now = fixture.Now.AddSeconds(5);
            var second = await fixture.Ok("status", "{\"scope\":\"stremio_playback\"}");
            Assert.True((long)second["session"]["positionMs"] > (long)first["session"]["positionMs"]);

            var full = await fixture.Ok("status");
            Assert.Equal(new[] { "api", "playback", "account" }, Keys(full));
            Assert.True((bool)full["playback"]["active"]);
            Assert.Contains("playback.play", full["api"]["commands"].Select(c => (string)c));
        }

        [Fact]
        public async Task ControlResultsCarryStateAndSession()
        {
            var fixture = new ApiFixture();
            await fixture.Ok("playback.play", "{\"query\":\"tt0903747\",\"season\":1,\"episode\":2}");
            var paused = await fixture.Ok("playback.pause");
            Assert.Equal(new[] { "ok", "action", "state", "positionMs", "durationMs", "mode", "session" }, Keys(paused));
            Assert.Equal("PAUSED", (string)paused["state"]);
            var audio = await fixture.Ok("playback.select_audio", "{\"index\":1}");
            Assert.Equal(1, (int)audio["session"]["selectedAudio"]);
            var size = await fixture.Ok("playback.subtitle_size", "{\"size\":60}");
            Assert.Equal(60, (int)size["session"]["subtitleSize"]);
            var stopped = await fixture.Ok("playback.stop");
            Assert.Equal("STOPPED", (string)stopped["state"]);
            var status = await fixture.Ok("status", "{\"scope\":\"stremio_playback\"}");
            Assert.False((bool)status["active"]);
            Assert.Equal("user_stop", (string)status["lastSession"]["endReason"]);
        }

        [Fact]
        public async Task QueueLifecycle()
        {
            var fixture = new ApiFixture();
            var set = await fixture.Ok("queue.set", "{\"items\":[{\"query\":\"Breaking Bad\"},{\"query\":\"Fresh Show S1E3\"}],\"maxSizeGb\":9}");
            Assert.Equal("ready", (string)set["queue"]["status"]);
            Assert.Equal(6.5, (double)set["queue"]["maxSizeGb"]);
            Assert.Equal("tt0903747:2:1", (string)set["queue"]["items"][0]["videoId"]);
            var start = await fixture.Ok("queue.start");
            Assert.Equal("playing", (string)start["queue"]["status"]);
            Assert.Equal("playing", (string)start["status"]);
            Assert.Equal("queue", (string)start["mode"]);
            Assert.Equal("tt0903747:2:1", (string)start["video"]["id"]);
            Assert.Equal(1, (int)start["queue"]["currentPosition"]);
            Assert.Equal(2, (int)start["queue"]["total"]);
            var skip = await fixture.Ok("queue.skip");
            Assert.Equal(2, (int)skip["queue"]["currentPosition"]);
            Assert.True((bool)skip["skipped"]);
            Assert.True((bool)skip["queue"]["items"][1]["current"]);
            var done = await fixture.Ok("queue.skip");
            Assert.Equal("queue_complete", (string)done["status"]);
            Assert.Equal("completed", (string)done["queue"]["status"]);
            var status = await fixture.Ok("queue.status");
            Assert.Equal("status", (string)status["action"]);
            var cleared = await fixture.Ok("queue.clear");
            Assert.Equal(JTokenType.Null, cleared["queue"].Type);
        }

        [Fact]
        public async Task AddonsAndAccountFlow()
        {
            var fixture = new ApiFixture();
            var list = await fixture.Ok("addons.list");
            Assert.Equal(new[] { "transportUrl", "id", "name", "resources", "types" }, Keys(list["addons"][0]));
            var installed = await fixture.Ok("addons.install", "{\"manifestUrl\":\"https://opensubtitles-v3.strem.io/manifest.json\"}");
            Assert.Equal("fake.opensubtitles-v3.strem.io", (string)installed["addon"]["id"]);
            Assert.Equal(2, ((JArray)(await fixture.Ok("addons.list"))["addons"]).Count);
            var missing = await fixture.Run("addons.remove", "{\"addonId\":\"nope\"}");
            Assert.Equal(404, missing.StatusCode);

            Assert.False((bool)(await fixture.Ok("account.status"))["authenticated"]);
            var link = await fixture.Ok("account.link_begin");
            Assert.Equal("ABCDE", (string)link["code"]);
            Assert.Equal("ABCDE", (string)(await fixture.Ok("account.status"))["pendingCode"]);
            var pending = await fixture.Ok("account.link_complete");
            Assert.True((bool)pending["pending"]);
            var done = await fixture.Ok("account.link_complete");
            Assert.True((bool)done["authenticated"]);
            Assert.True((bool)(await fixture.Ok("account.status"))["authenticated"]);
            await fixture.Ok("account.sign_out");
            Assert.False((bool)(await fixture.Ok("account.status"))["authenticated"]);
        }
    }
}
