using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Api;
using Ru7100.Core.App;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using Ru7100.Tests.Playback;
using Xunit;
using ApiFakeAccount = Ru7100.Fakes.FakeAccount;
using ApiFakeAddons = Ru7100.Fakes.FakeAddonRegistry;

namespace Ru7100.Tests.App
{
    /// What a TV soak test reads: the bounded playback timeline (dev.timeline, and in dev.diagnostics), and cheap
    /// diagnostics by default with the expensive parts only on request.
    public class SoakDiagnosticsTests
    {
        private static readonly CancellationToken None = CancellationToken.None;
        private static readonly string[] Admin = { ApiScopes.Admin };

        private static AppHost NewApp(Func<object> cheap, Func<object> full, out FakePlayer player, out ManualClock clock)
        {
            var catalog = new FakeCatalog();
            var show = FakeCatalog.Series("tt0903747", "Breaking Bad", 1, 3);
            catalog.Metas[show.Summary.Id] = show;
            player = new FakePlayer();
            clock = new ManualClock();
            var app = new AppHost(new AppPlatform
            {
                Store = new MemoryStore(), Player = player, Opener = new FakeOpener(clock), Clock = clock, Catalog = catalog,
                Sources = new FakeResolver { Streams = videoId => new[] { Harness.Torrent("A-" + videoId) } },
                Library = new FakeLibrary(), Account = new ApiFakeAccount(), Addons = new ApiFakeAddons(), Progress = new FakeProgress(),
                Subtitles = new FakeSubtitleLoader(), CatalogRows = new StaticCatalogRows(Array.Empty<CatalogRowSpec>()),
                PlaybackOptions = new PlaybackOptions { AutoMonitor = false },
                Diagnostics = cheap, FullDiagnostics = full,
            });
            app.InitializeAsync(None).GetAwaiter().GetResult();
            return app;
        }

        private static async Task<JToken> Api(AppHost app, string command, object args = null)
        {
            var response = await app.Router.ExecuteAsync(command, args == null ? null : JObject.FromObject(args), Admin, None);
            Assert.True(response.Ok, command + ": " + response.Body);
            return response.Body["result"];
        }

        [Fact]
        public async Task DiagnosticsAreCheapUnlessFullIsAsked()
        {
            var cheap = 0;
            var full = 0;
            using (var app = NewApp(() => { cheap++; return new { kind = "cheap" }; }, () => { full++; return new { kind = "full" }; }, out _, out _))
            {
                var result = await Api(app, "dev.diagnostics");
                Assert.Equal("cheap", (string)result["platform"]["kind"]);
                Assert.NotNull(result["timeline"]);
                Assert.Equal(0, full);
                result = await Api(app, "dev.diagnostics", new { full = true });
                Assert.Equal("full", (string)result["platform"]["kind"]);
                Assert.Equal(1, full);
                Assert.Equal(1, cheap);
            }
        }

        [Fact]
        public async Task TheTimelineRecordsPlaybackSamplesAndSeeks()
        {
            using (var app = NewApp(null, null, out var player, out var clock))
            {
                await Api(app, "playback.play", new { query = "tt0903747", season = 1, episode = 1 });
                player.State = PlayerState.Playing;
                player.PositionMs = 120_000;
                player.DurationMs = 2_400_000;
                await app.Session.TickAsync();
                await app.Session.SeekAsync(300_000, None);
                var samples = await Api(app, "dev.timeline", new { kind = "sample", max = 5 });
                Assert.Contains(samples["entries"], e => ((string)e).Contains("sample pos=120000 state=Playing"));
                var seeks = await Api(app, "dev.timeline", new { kind = "seek" });
                Assert.Contains(seeks["entries"], e => ((string)e).Contains("target=300000"));
                Assert.Contains(seeks["entries"], e => ((string)e).Contains("landed=300000"));
                Assert.True((long)(await Api(app, "dev.timeline"))["total"] > 0);
            }
        }

        [Fact]
        public void TheTimelineIsBounded()
        {
            var timeline = new PlaybackTimeline(32);
            for (var i = 0; i < 100; i++)
                timeline.Add("test", "entry " + i);
            var lines = timeline.Lines(1000);
            Assert.Equal(32, lines.Count);
            Assert.EndsWith("entry 99", lines.Last());
            Assert.EndsWith("entry 68", lines.First());
            Assert.Equal(100, timeline.Count);
            Assert.Equal(3, timeline.Snapshot(3).Count);
        }
    }
}
