using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Api;
using Xunit;

namespace Ru7100.Tests.Api
{
    public class RouterTests
    {
        /// One valid call per command, plus the scopes that unlock it (admin always does).
        public static readonly (string Command, string Args, string[] Scopes)[] Commands =
        {
            ("status", "{}", new[] { "playback", "library", "recommendations" }),
            ("playback.play", "{\"query\":\"Sintel\",\"confirmed\":true,\"quality\":\"1080p\",\"maxSizeGb\":4,\"positionSeconds\":30}", new[] { "playback" }),
            ("playback.pause", "{}", new[] { "playback" }),
            ("playback.resume", "{}", new[] { "playback" }),
            ("playback.stop", "{}", new[] { "playback" }),
            ("playback.seek", "{\"positionSeconds\":60}", new[] { "playback" }),
            ("playback.enable_binge", "{}", new[] { "playback" }),
            ("playback.disable_binge", "{}", new[] { "playback" }),
            ("playback.stop_after_current", "{\"enabled\":false}", new[] { "playback" }),
            ("playback.select_audio", "{\"index\":1}", new[] { "playback" }),
            ("playback.select_subtitle", "{\"index\":-1}", new[] { "playback" }),
            ("playback.subtitle_size", "{\"size\":48}", new[] { "playback" }),
            ("playback.subtitle_offset", "{\"offsetMs\":-1500}", new[] { "playback" }),
            ("queue.set", "{\"items\":[{\"query\":\"Breaking Bad S2E1\"},{\"query\":\"Fresh Show\",\"season\":1,\"episode\":2}],\"quality\":\"1080p\",\"maxSizeGb\":4}", new[] { "playback" }),
            ("queue.start", "{\"items\":[{\"query\":\"Breaking Bad\",\"season\":1,\"episode\":1}]}", new[] { "playback" }),
            ("queue.status", "{}", new[] { "playback" }),
            ("queue.clear", "{}", new[] { "playback" }),
            ("queue.skip", "{}", new[] { "playback" }),
            ("library.search", "{\"query\":\"breaking\",\"type\":\"series\",\"limit\":3}", new[] { "playback", "library" }),
            ("library.list", "{\"status\":\"watching\",\"query\":\"bad\"}", new[] { "playback", "library" }),
            ("library.new_episodes", "{\"sinceLastCheck\":true,\"acknowledge\":true}", new[] { "playback", "library" }),
            ("library.add", "{\"queryOrId\":\"Dog Days\"}", new[] { "library" }),
            ("library.remove", "{\"queryOrId\":\"tt1727587\"}", new[] { "library" }),
            ("library.set_notifications", "{\"queryOrId\":\"Breaking Bad\",\"notifications\":false}", new[] { "library" }),
            ("library.mark_watched", "{\"queryOrId\":\"Breaking Bad\",\"season\":2,\"episode\":1}", new[] { "library" }),
            ("addons.list", "{}", new[] { "playback", "library" }),
            ("addons.install", "{\"manifestUrl\":\"https://opensubtitles-v3.strem.io/manifest.json\"}", new string[0]),
            ("addons.remove", "{\"addonId\":\"com.stremio.torrentio.addon\"}", new string[0]),
            ("account.status", "{}", new[] { "playback", "library", "recommendations" }),
            ("account.link_begin", "{}", new string[0]),
            ("account.link_complete", "{\"code\":\"ABCDE\"}", new string[0]),
            ("account.sign_out", "{}", new string[0]),
            ("recommendations.set", "{\"rows\":[{\"id\":\"for-you\",\"title\":\"For You\",\"items\":[{\"id\":\"tt0903747\",\"type\":\"series\",\"name\":\"Breaking Bad\",\"reason\":\"Crime and Drama\"}]}],\"generatedAt\":\"2026-11-18T10:00:00+00:00\",\"source\":\"test\"}", new[] { "recommendations" }),
            ("recommendations.get", "{}", new[] { "recommendations" }),
            ("recommendations.feedback.list", "{\"afterSequence\":0,\"limit\":10}", new[] { "recommendations" }),
            ("recommendations.feedback.ack", "{\"sequence\":0}", new[] { "recommendations" }),
        };

        public static IEnumerable<object[]> CommandNames() => Commands.Select(c => new object[] { c.Command });

        private static (string Command, string Args, string[] Scopes) Entry(string command) => Commands.Single(c => c.Command == command);

        /// Puts the fixture in a state where the command can succeed.
        private static async Task Prepare(ApiFixture fixture, string command)
        {
            if (command.StartsWith("playback.") && command != "playback.play" && command != "playback.stop")
                await fixture.Ok("playback.play", "{\"query\":\"Breaking Bad\",\"season\":1,\"episode\":1}");
            if (command == "queue.skip")
                await fixture.Ok("queue.start", "{\"items\":[{\"query\":\"Breaking Bad S1E1\"},{\"query\":\"Fresh Show S1E1\"}]}");
        }

        [Fact]
        public void TableCoversEveryRegisteredCommand()
        {
            var registered = new ApiFixture().Router.Commands.Select(c => c.Name).OrderBy(n => n).ToList();
            Assert.Equal(Commands.Select(c => c.Command).OrderBy(n => n).ToList(), registered);
        }

        [Theory]
        [MemberData(nameof(CommandNames))]
        public async Task EveryCommandSucceedsWithValidArgs(string command)
        {
            var fixture = new ApiFixture();
            await Prepare(fixture, command);
            var response = await fixture.Run(command, Entry(command).Args);
            Assert.True(response.StatusCode == 200, command + ": " + response.Body);
            Assert.True((bool)response.Body["ok"]);
        }

        [Theory]
        [MemberData(nameof(CommandNames))]
        public async Task EveryCommandRejectsUnknownArguments(string command)
        {
            var fixture = new ApiFixture();
            await Prepare(fixture, command);
            var args = JObject.Parse(Entry(command).Args);
            args["unexpectedField"] = 1;
            var response = await fixture.Router.ExecuteAsync(command, args, ApiFixture.AdminKey, CancellationToken.None);
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("irrelevant_argument", (string)response.Body["error"]["code"]);
        }

        [Theory]
        [MemberData(nameof(CommandNames))]
        public void CommandScopesMatchTheTable(string command)
        {
            var registered = new ApiFixture().Router.Find(command).Scopes.OrderBy(s => s).ToArray();
            var expected = Entry(command).Scopes.Length == 0 ? new[] { "admin" } : Entry(command).Scopes.OrderBy(s => s).ToArray();
            Assert.Equal(expected, registered);
        }

        [Theory]
        [MemberData(nameof(CommandNames))]
        public async Task EachSingleScopeKeyIsAllowedOrDeniedPerTable(string command)
        {
            foreach (var scope in new[] { ApiScopes.Playback, ApiScopes.Library, ApiScopes.Recommendations, ApiScopes.Admin })
            {
                var fixture = new ApiFixture();
                await Prepare(fixture, command);
                var response = await fixture.Run(command, Entry(command).Args, new[] { scope });
                var allowed = scope == ApiScopes.Admin || Entry(command).Scopes.Contains(scope);
                if (allowed) Assert.True(response.StatusCode == 200, command + " as " + scope + ": " + response.Body);
                else
                {
                    Assert.Equal(403, response.StatusCode);
                    Assert.Equal("forbidden", (string)response.Body["error"]["code"]);
                }
            }
        }

        [Fact]
        public async Task PlaybackKeyCannotChangeLibraryAddonsOrAccount()
        {
            var fixture = new ApiFixture();
            var playbackOnly = new[] { ApiScopes.Playback };
            foreach (var command in new[] { "library.add", "library.remove", "library.mark_watched", "library.set_notifications",
                         "addons.install", "addons.remove", "account.link_begin", "account.sign_out" })
            {
                var response = await fixture.Run(command, Entry(command).Args, playbackOnly);
                Assert.Equal(403, response.StatusCode);
            }
            Assert.Empty(fixture.Playback.Calls);
            Assert.Single(await fixture.Addons.GetInstalledAsync(CancellationToken.None));
        }

        [Fact]
        public async Task RecommendationCommandsRegisteredByTheHostNeedTheirOwnScope()
        {
            var fixture = new ApiFixture();
            var curated = 0;
            fixture.Router.Register(new ApiCommand("recommendations.curate", new[] { ApiScopes.Recommendations }, new[] { "items" },
                (a, t) => { curated++; return Task.FromResult<object>(new { curated = 1 }); }));
            Assert.Equal(403, (await fixture.Run("recommendations.curate", "{}", new[] { ApiScopes.Playback })).StatusCode);
            Assert.Equal(403, (await fixture.Run("recommendations.curate", "{}", new[] { ApiScopes.Library })).StatusCode);
            Assert.Equal(200, (await fixture.Run("recommendations.curate", "{}", new[] { ApiScopes.Recommendations })).StatusCode);
            Assert.Equal(403, (await fixture.Run("playback.play", "{\"query\":\"Sintel\"}", new[] { ApiScopes.Recommendations })).StatusCode);
            Assert.Equal(1, curated);
            Assert.Throws<ArgumentException>(() => fixture.Router.Register(new ApiCommand("x", new[] { "root" }, new string[0], (a, t) => Task.FromResult<object>(null))));
        }

        [Theory]
        [InlineData("playback.play", "{}", "missing_argument")]
        [InlineData("playback.play", "{\"query\":\"  \"}", "missing_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"season\":1}", "missing_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"episode\":1}", "missing_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"quality\":\"8k\"}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"mode\":\"queue\"}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"maxSizeGb\":0}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"positionSeconds\":-1}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"target\":\"samsung\"}", "irrelevant_argument")]
        [InlineData("playback.play", "{\"query\":5}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"confirmed\":\"yes\"}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"season\":1.5,\"episode\":1}", "invalid_argument")]
        [InlineData("playback.play", "{\"query\":\"x\",\"season\":-1,\"episode\":1}", "invalid_argument")]
        [InlineData("playback.pause", "{\"positionSeconds\":1}", "irrelevant_argument")]
        [InlineData("playback.resume", "{\"x\":1}", "irrelevant_argument")]
        [InlineData("playback.stop", "{\"x\":1}", "irrelevant_argument")]
        [InlineData("playback.seek", "{}", "missing_argument")]
        [InlineData("playback.seek", "{\"positionSeconds\":-1}", "invalid_argument")]
        [InlineData("playback.seek", "{\"positionSeconds\":\"10\"}", "invalid_argument")]
        [InlineData("playback.enable_binge", "{\"mode\":\"binge\"}", "irrelevant_argument")]
        [InlineData("playback.disable_binge", "{\"x\":1}", "irrelevant_argument")]
        [InlineData("playback.stop_after_current", "{\"enabled\":1}", "invalid_argument")]
        [InlineData("playback.select_audio", "{}", "missing_argument")]
        [InlineData("playback.select_audio", "{\"index\":-1}", "invalid_argument")]
        [InlineData("playback.select_subtitle", "{\"index\":-2}", "invalid_argument")]
        [InlineData("playback.select_subtitle", "{}", "missing_argument")]
        [InlineData("playback.subtitle_size", "{\"size\":23}", "invalid_argument")]
        [InlineData("playback.subtitle_size", "{\"size\":73}", "invalid_argument")]
        [InlineData("playback.subtitle_offset", "{\"offsetMs\":30001}", "invalid_argument")]
        [InlineData("playback.subtitle_offset", "{\"offsetMs\":-30001}", "invalid_argument")]
        [InlineData("queue.set", "{}", "missing_argument")]
        [InlineData("queue.set", "{\"items\":[]}", "invalid_argument")]
        [InlineData("queue.set", "{\"items\":\"Breaking Bad\"}", "invalid_argument")]
        [InlineData("queue.set", "{\"items\":[\"Breaking Bad\"]}", "invalid_argument")]
        [InlineData("queue.set", "{\"items\":[{}]}", "missing_argument")]
        [InlineData("queue.set", "{\"items\":[{\"query\":\"x\",\"season\":1}]}", "missing_argument")]
        [InlineData("queue.set", "{\"items\":[{\"query\":\"x\",\"foo\":1}]}", "irrelevant_argument")]
        [InlineData("queue.set", "{\"items\":[{\"query\":\"x\"}],\"quality\":\"720p\"}", "invalid_argument")]
        [InlineData("queue.set", "{\"items\":[{\"query\":\"x\"}],\"maxSizeGb\":-2}", "invalid_argument")]
        [InlineData("queue.start", "{\"quality\":\"1080p\"}", "irrelevant_argument")]
        [InlineData("queue.status", "{\"items\":[{\"query\":\"x\"}]}", "irrelevant_argument")]
        [InlineData("queue.clear", "{\"quality\":\"auto\"}", "irrelevant_argument")]
        [InlineData("queue.skip", "{\"maxSizeGb\":1}", "irrelevant_argument")]
        [InlineData("library.search", "{}", "missing_argument")]
        [InlineData("library.search", "{\"query\":\"x\",\"limit\":21}", "invalid_argument")]
        [InlineData("library.search", "{\"query\":\"x\",\"limit\":0}", "invalid_argument")]
        [InlineData("library.search", "{\"query\":\"x\",\"type\":\"anime\"}", "invalid_argument")]
        [InlineData("library.search", "{\"query\":\"x\",\"status\":\"all\"}", "irrelevant_argument")]
        [InlineData("library.list", "{\"status\":\"done\"}", "invalid_argument")]
        [InlineData("library.list", "{\"limit\":3}", "irrelevant_argument")]
        [InlineData("library.new_episodes", "{\"acknowledge\":\"true\"}", "invalid_argument")]
        [InlineData("library.new_episodes", "{\"limit\":5}", "irrelevant_argument")]
        [InlineData("library.add", "{}", "missing_argument")]
        [InlineData("library.add", "{\"queryOrId\":\"x\",\"notifications\":true}", "irrelevant_argument")]
        [InlineData("library.remove", "{\"queryOrId\":\"x\",\"watched\":true}", "irrelevant_argument")]
        [InlineData("library.set_notifications", "{\"queryOrId\":\"Breaking Bad\"}", "missing_argument")]
        [InlineData("library.set_notifications", "{\"queryOrId\":\"Breaking Bad\",\"notifications\":\"off\"}", "invalid_argument")]
        [InlineData("library.mark_watched", "{\"queryOrId\":\"Breaking Bad\",\"season\":1}", "missing_argument")]
        [InlineData("library.mark_watched", "{\"queryOrId\":\"Sintel\",\"season\":1,\"episode\":1}", "irrelevant_argument")]
        [InlineData("library.mark_watched", "{\"queryOrId\":\"Breaking Bad\",\"season\":1,\"episode\":1,\"watched\":\"no\"}", "invalid_argument")]
        [InlineData("addons.list", "{\"x\":1}", "irrelevant_argument")]
        [InlineData("addons.install", "{}", "missing_argument")]
        [InlineData("addons.install", "{\"manifestUrl\":\"ftp://example.com/manifest.json\"}", "invalid_argument")]
        [InlineData("addons.install", "{\"manifestUrl\":\"https://example.com/other.json\"}", "invalid_argument")]
        [InlineData("addons.install", "{\"manifestUrl\":\"not a url\"}", "invalid_argument")]
        [InlineData("addons.remove", "{}", "missing_argument")]
        [InlineData("account.status", "{\"verbose\":true}", "irrelevant_argument")]
        [InlineData("account.link_begin", "{\"code\":\"x\"}", "irrelevant_argument")]
        [InlineData("account.link_complete", "{}", "missing_argument")]
        [InlineData("account.sign_out", "{\"x\":1}", "irrelevant_argument")]
        [InlineData("status", "{\"scope\":\"tv\"}", "invalid_argument")]
        public async Task InvalidArgumentsAreRejectedWithOldErrorCodes(string command, string args, string code)
        {
            var fixture = new ApiFixture();
            await Prepare(fixture, command);
            var callsBefore = fixture.Playback.Calls.Count;
            var response = await fixture.Run(command, args);
            Assert.True(response.StatusCode == 400, command + " " + args + ": " + response.Body);
            Assert.False((bool)response.Body["ok"]);
            Assert.Equal(code, (string)response.Body["error"]["code"]);
            Assert.False(string.IsNullOrEmpty((string)response.Body["error"]["message"]));
            Assert.Equal(callsBefore, fixture.Playback.Calls.Count);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("")]
        [InlineData("{\"command\":5}")]
        [InlineData("{\"args\":{}}")]
        [InlineData("{\"command\":\"status\",\"args\":[]}")]
        [InlineData("{\"command\":\"status\",\"extra\":1}")]
        [InlineData("{\"command\":\"status\"} {}")]
        public async Task MalformedRequestBodiesAreRejected(string body)
        {
            var response = await new ApiFixture().Router.ExecuteJsonAsync(body, ApiFixture.AdminKey, CancellationToken.None);
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("invalid_request", (string)response.Body["error"]["code"]);
        }

        [Fact]
        public async Task JsonBodyRoutesToTheCommand()
        {
            var response = await new ApiFixture().Router.ExecuteJsonAsync("{\"command\":\"library.search\",\"args\":{\"query\":\"sintel\"}}", ApiFixture.AdminKey, CancellationToken.None);
            Assert.Equal(200, response.StatusCode);
            Assert.Equal("Sintel", (string)response.Body["result"][0]["name"]);
        }

        [Fact]
        public async Task UnknownCommandIs404()
        {
            var response = await new ApiFixture().Run("playback.teleport", "{}");
            Assert.Equal(404, response.StatusCode);
            Assert.Equal("unknown_command", (string)response.Body["error"]["code"]);
        }

        [Fact]
        public async Task ControlWithoutSessionIsAConflict()
        {
            var response = await new ApiFixture().Run("playback.pause");
            Assert.Equal(409, response.StatusCode);
            Assert.Equal("no_active_playback", (string)response.Body["error"]["code"]);
        }

        [Fact]
        public async Task MissingServicesReport503()
        {
            var router = new ApiRouter(new ApiServices());
            var response = await router.ExecuteAsync("playback.pause", null, ApiFixture.AdminKey, CancellationToken.None);
            Assert.Equal(503, response.StatusCode);
            Assert.Equal("unavailable", (string)response.Body["error"]["code"]);
            Assert.Equal(503, (await router.ExecuteAsync("library.list", null, ApiFixture.AdminKey, CancellationToken.None)).StatusCode);
        }

        [Fact]
        public async Task ArgumentsReachTheEngineUnchanged()
        {
            var fixture = new ApiFixture();
            await fixture.Ok("playback.play", "{\"query\":\"Breaking Bad\",\"season\":2,\"episode\":3,\"mode\":\"binge\",\"quality\":\"4k\",\"maxSizeGb\":3.5,\"positionSeconds\":12}");
            await fixture.Ok("playback.seek", "{\"positionSeconds\":90.5}");
            await fixture.Ok("playback.stop_after_current");
            await fixture.Ok("playback.subtitle_offset", "{\"offsetMs\":250}");
            Assert.Equal(new[]
            {
                "Play Breaking Bad 2 3 False binge 4k 3.5 12",
                "seek 90.5",
                "stop_after_current True",
                "subtitle_offset 250",
            }, fixture.Playback.Calls);
        }
    }
}
