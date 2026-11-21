using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Ru7100.Core.App;
using Ru7100.Core.Ui;
using Ru7100.Host;

namespace Ru7100.Preview
{
    /// COMPOSITION ROOT for the browser preview.
    /// Live (default when Cinemeta answers): the TV's own composition root, core/App AppHost (real add-on registry,
    /// Cinemeta catalog, search, source ranking, PlaybackSession, player settings, feedback recorder), with the Mac
    /// host's simulated player and torrent opener, and a fake library and account (no Stremio sign-in here).
    /// Fake (`--fake`, or offline): fixtures from preview/fixtures/cinemeta.json and a fake player that plays the
    /// sample clip with demo subtitle cues.
    internal static class Composition
    {
        public sealed class Built
        {
            public UiServices Services;
            public IVideoFollower Video;
            public AppHost App;
            public bool Live;
            public BrowserPlayer Browser;
            /// Stops the torrent engine and deletes its temporary cache.
            public Action Cleanup = () => { };
        }

        public static Built Build(bool? live, IUiDispatcher dispatcher, string dataDirectory)
        {
            var metas = Fixtures.All();
            var library = new PreviewLibrary(metas);
            var useLive = live ?? NetworkWorks();

            if (useLive)
            {
                // Real sources: the TV's TorrentSourceOpener (cache in a temp dir with a size budget, deleted on exit)
                // and direct HTTP URLs, played by the page's <video> through BrowserPlayer. Dev-only on the Mac.
                var store = new Ru7100.Core.App.FileKeyValueStore(Path.Combine(dataDirectory, "app"));
                var http = new Ru7100.Core.Addons.AddonHttp();
                var registry = new Ru7100.Core.Addons.AddonRegistry(store, http, null, null);
                var resolver = new Ru7100.Core.Addons.SourceResolver(registry, http);
                var torrentRoot = Path.Combine(dataDirectory, "torrents-" + System.Diagnostics.Process.GetCurrentProcess().Id);
                var opener = new Ru7100Probe.TorrentSourceOpener(new Ru7100Probe.TorrentSourceSettings
                {
                    RootDirectory = torrentRoot,
                    IdleCacheBudgetBytes = 2L * 1024 * 1024 * 1024,
                    Log = line => Console.WriteLine("torrent: " + line)
                });
                var browser = new BrowserPlayer();
                var app = new AppHost(new AppPlatform
                {
                    Store = store,
                    Player = browser,
                    Opener = opener,
                    Catalog = new Ru7100.Core.Addons.StremioCatalog(registry, http),
                    Addons = registry,
                    Sources = new BrowserPreferringResolver(resolver),   // preview-only: browser-decodable sources first
                    Library = library,                                   // fake: no Stremio account in the preview
                    Account = new PreviewAccount(),
                    Dispatcher = dispatcher,
                    Log = line => Console.WriteLine(line),
                    // A Mac on home broadband gets torrent metadata slower than the TV budget assumes.
                    PlaybackOptions = new Ru7100.Core.Playback.PlaybackOptions { PrepareBudget = TimeSpan.FromSeconds(120), StartupInitial = TimeSpan.FromSeconds(240), StartupMaximum = TimeSpan.FromSeconds(600) },
                });
                app.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                var services = app.Ui;
                services.Extras = new CinemetaExtras(http);   // one request for the title page's details and extras
                services.Search = new AddonSearchService(registry, library);
                services.Browse = new CinemetaBrowse();
                services.ServiceCatalog = new Ru7100.Core.Addons.FallbackServiceCatalog(new Ru7100.Core.Addons.JustWatchCatalog(), new Ru7100.Core.Addons.StreamingCatalogsAddon(new Ru7100.Core.Addons.AddonHttp()));
                services.SourceCatalog = new AddonSourceCatalog(resolver);   // the picker shows the real core/Addons order
                services.CatalogRows = new Ru7100.Core.App.AddonCatalogRows(registry);
                // The recommendations store is empty on a fresh Mac; show the fixture feed so the rows have content.
                services.Recommendations = new PreviewRecommendations(metas);
                services.Player = new PreviewStatusPlayer(services.Player, browser);   // decode failures fold into the start card
                Console.WriteLine("Live: core/App AppHost, real sources via TorrentSourceOpener, browser playback (cache " + torrentRoot + ").");
                return new Built
                {
                    Services = services, Video = browser, Browser = browser, App = app, Live = true,
                    Cleanup = () =>
                    {
                        try { app.Dispose(); } catch (Exception) { }
                        try { opener.Dispose(); } catch (Exception) { }
                        try { if (Directory.Exists(torrentRoot)) Directory.Delete(torrentRoot, true); } catch (Exception) { }
                    }
                };
            }

            var settings = new PlayerSettings(new FileKeyValueStore(Path.Combine(dataDirectory, "settings")));
            settings.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            var player = new PreviewPlayer(library, settings);
            var sources = new PreviewSources();
            Console.WriteLine("Fake data (fixtures from preview/fixtures/cinemeta.json).");
            return new Built
            {
                Services = new UiServices
                {
                    Catalog = new PreviewCatalog(metas),
                    Library = library,
                    Account = new PreviewAccount(),
                    Addons = new PreviewAddons(),
                    Sources = sources,
                    SourceCatalog = sources,
                    Extras = new PreviewExtras(),
                    Recommendations = new PreviewRecommendations(metas),
                    Queue = new PreviewQueue(player),
                    Player = player,
                    Feedback = new PreviewFeedback(),
                    CatalogRows = StaticCatalogRows.CinemetaDefaults(),
                    Dispatcher = dispatcher,
                    Clock = new SystemClock()
                },
                Video = player,
                Live = false
            };
        }

        private static bool NetworkWorks()
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) })
                using (var response = http.GetAsync(Ru7100.Core.Addons.AddonRegistry.CinemetaManifestUrl).GetAwaiter().GetResult())
                    return response.IsSuccessStatusCode;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// What the browser's <video> element should do (play/pause/seek the looping sample clip).
    internal interface IVideoFollower
    {
        object VideoState { get; }
    }

    /// Follows any IPlayerControls snapshot: a seek is a position jump the wall clock doesn't explain.
    internal sealed class SnapshotVideoFollower : IVideoFollower
    {
        private readonly IPlayerControls player;
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private long lastPosition = -1;
        private long lastClock;
        private int seekSeq;

        public SnapshotVideoFollower(IPlayerControls player) => this.player = player;

        public object VideoState
        {
            get
            {
                var s = player.Snapshot ?? new PlayerSnapshot();
                var now = clock.ElapsedMilliseconds;
                var expected = lastPosition + (s.State == Ru7100.Core.PlayerState.Playing ? now - lastClock : 0);
                if (lastPosition < 0 || Math.Abs(s.PositionMs - expected) > 3000)
                    seekSeq++;
                lastPosition = s.PositionMs;
                lastClock = now;
                var showing = s.SessionActive && (s.State == Ru7100.Core.PlayerState.Playing || s.State == Ru7100.Core.PlayerState.Paused);
                return new { active = s.SessionActive, url = showing ? PreviewPlayer.SampleVideoUrl : null, playing = s.State == Ru7100.Core.PlayerState.Playing, positionMs = s.PositionMs, clipMs = PreviewPlayer.ClipDurationMs, seekSeq };
            }
        }
    }

    internal sealed class PreviewFeedback : IFeedbackSink
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ratings = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        public System.Threading.Tasks.Task ThumbsUpAsync(string metaId, CancellationToken token) { ratings[metaId] = "up"; return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task ThumbsDownAsync(string metaId, CancellationToken token) { ratings[metaId] = "down"; return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task NotInterestedAsync(string metaId, CancellationToken token) { ratings[metaId] = "hidden"; return System.Threading.Tasks.Task.CompletedTask; }
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyCollection<string>> GetHiddenAsync(CancellationToken token) =>
            System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyCollection<string>>(ratings.Where(r => r.Value != "up").Select(r => r.Key).ToList());
    }
}
