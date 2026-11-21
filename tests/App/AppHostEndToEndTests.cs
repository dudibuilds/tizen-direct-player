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
    /// The composition root with fakes for the network and the player: an agent API command and a remote key act on
    /// the same session, and the UI, API and feedback log all observe it.
    public class AppHostEndToEndTests
    {
        private static readonly CancellationToken None = CancellationToken.None;
        private static readonly string[] Admin = { ApiScopes.Admin };

        private sealed class Rig : IDisposable
        {
            public readonly ManualClock Clock = new ManualClock();
            public readonly FakeCatalog Catalog = new FakeCatalog();
            public readonly FakeResolver Resolver = new FakeResolver();
            public readonly FakePlayer Player = new FakePlayer();
            public readonly MemoryStore Store = new MemoryStore();
            public readonly FakeProgress Progress = new FakeProgress();
            public readonly AppHost App;
            public readonly TvShell Shell;
            public readonly MetaDetail Show;

            public Rig()
            {
                Show = FakeCatalog.Series("tt0903747", "Breaking Bad", 1, 3);
                Catalog.Metas[Show.Summary.Id] = Show;
                Resolver.Streams = videoId => new[] { Harness.Torrent("A-" + videoId) };
                App = new AppHost(new AppPlatform
                {
                    Store = Store, Player = Player, Opener = new FakeOpener(Clock), Clock = Clock,
                    Catalog = Catalog, Sources = Resolver, Library = new FakeLibrary(), Account = new ApiFakeAccount(),
                    Addons = new ApiFakeAddons(), Progress = Progress, Subtitles = new FakeSubtitleLoader(),
                    CatalogRows = new StaticCatalogRows(Array.Empty<CatalogRowSpec>()),
                    PlaybackOptions = new PlaybackOptions { AutoMonitor = false },
                });
                App.InitializeAsync(None).GetAwaiter().GetResult();
                Shell = new TvShell(App.Ui);
                App.AttachShell(Shell);
                Shell.Start(new HomeViewModel());
            }

            public async Task<JToken> Api(string command, object args = null)
            {
                var response = await App.Router.ExecuteAsync(command, args == null ? null : JObject.FromObject(args), Admin, None);
                Assert.True(response.Ok, command + ": " + response.Body);
                return response.Body["result"];
            }

            public void Dispose() => App.Dispose();
        }

        [Fact]
        public async Task ApiPlayAndRemoteKeysDriveTheSameSession()
        {
            using (var rig = new Rig())
            {
                var played = await rig.Api("playback.play", new { query = "tt0903747", season = 1, episode = 2 });
                Assert.Equal("playing", (string)played["status"]);
                Assert.Equal("tt0903747:1:2", rig.Player.LastUrl == null ? null : rig.App.Session.GetStatus().Session.VideoId);

                // A session started by the agent opens the player screen on the TV.
                var player = Assert.IsType<PlayerViewModel>(rig.Shell.Current);
                Assert.True(player.Snapshot.SessionActive);
                Assert.Equal("Breaking Bad", player.Snapshot.Title);

                // Remote Pause key -> same engine -> the API sees it.
                rig.Shell.HandleKey(RemoteKey.Pause);
                await Wait.Until(() => rig.App.Session.GetStatus().Session?.UserPaused == true, "paused by the remote");
                Assert.Equal(PlayerState.Paused, rig.Player.State);
                var status = await rig.Api("status", new { scope = "stremio_playback" });
                Assert.Equal("PAUSED", (string)status["session"]["state"]);

                // API resume -> the overlay shows playing.
                await rig.Api("playback.resume");
                Assert.Equal(PlayerState.Playing, rig.App.PlayerControls.Snapshot.State);

                // Subtitle size from the agent is the overlay's setting.
                await rig.Api("playback.subtitle_size", new { size = 54 });
                Assert.Equal(54, player.Settings.SubtitleStyle.Size);

                // Remote Stop ends the session for everyone.
                rig.Shell.HandleKey(RemoteKey.Stop);
                await Wait.Until(() => !rig.App.Session.GetStatus().Active, "stopped by the remote");
                var after = await rig.Api("status", new { scope = "stremio_playback" });
                Assert.False((bool)after["active"]);
                Assert.Equal("user_stop", (string)after["lastSession"]["endReason"]);
                Assert.IsType<HomeViewModel>(rig.Shell.Current);

                // The viewing was recorded for the Mac's learner.
                await rig.App.Feedback.FlushAsync();
                var feedback = await rig.Api("recommendations.feedback.list");
                var kinds = ((JArray)feedback["events"]).Select(e => (string)e["kind"] + " " + (string)e["videoId"]).ToList();
                Assert.Contains("play_started tt0903747:1:2", kinds);
            }
        }

        [Fact]
        public async Task RemotePlayIsVisibleToTheApiAndRestoreIsOfferedAfterRestart()
        {
            var store = new MemoryStore();
            using (var rig = new Rig())
            {
                var details = rig.Show;
                rig.Shell.StartPlayback(new Ru7100.Core.Ui.PlayRequest { Meta = details, Video = details.Videos[0], ResumeMs = 30_000 });
                await Wait.Until(() => rig.App.Session.GetStatus().Active, "remote play started");
                var status = await rig.Api("status", new { scope = "stremio_playback" });
                Assert.Equal("tt0903747:1:1", (string)status["session"]["videoId"]);
                Assert.Equal(30_000, (long)status["session"]["positionMs"]);

                // Pausing for the background saves the session without ending it.
                rig.Player.PositionMs = 95_000;
                await rig.App.PauseForBackgroundAsync(None);
                await rig.App.ShutdownAsync(None);
                foreach (var pair in rig.Store.Values) store.Values[pair.Key] = pair.Value;
            }

            var catalog = new FakeCatalog();
            var show = FakeCatalog.Series("tt0903747", "Breaking Bad", 1, 3);
            catalog.Metas[show.Summary.Id] = show;
            var player = new FakePlayer();
            var clock = new ManualClock();
            using (var app = new AppHost(new AppPlatform
            {
                Store = store, Player = player, Opener = new FakeOpener(clock), Clock = clock, Catalog = catalog,
                Sources = new FakeResolver { Streams = videoId => new[] { Harness.Torrent("A-" + videoId) } },
                Library = new FakeLibrary(), Account = new ApiFakeAccount(), Addons = new ApiFakeAddons(), Progress = new FakeProgress(),
                Subtitles = new FakeSubtitleLoader(), CatalogRows = new StaticCatalogRows(Array.Empty<CatalogRowSpec>()),
                PlaybackOptions = new PlaybackOptions { AutoMonitor = false },
            }))
            {
                await app.InitializeAsync(None);
                Assert.Equal(95_000, app.Session.Restorable.PositionMs);
                var shell = new TvShell(app.Ui);
                app.AttachShell(shell);
                shell.Start(new HomeViewModel());
                Assert.True(app.OfferRestore());
                Assert.Equal("Continue watching?", shell.Dialog.Title);
                shell.HandleKey(RemoteKey.Select);   // Resume
                await Wait.Until(() => app.Session.GetStatus().Active, "restored");
                Assert.Equal(95_000, player.Opens.Single().StartMs);
                Assert.IsType<PlayerViewModel>(shell.Current);
            }
        }
    }
}
