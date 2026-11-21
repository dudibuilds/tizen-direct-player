using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Xunit;

namespace Ru7100.Core.Tests.Addons
{
    public class SourceResolverTests
    {
        private const string FastManifest = "https://fast.example/manifest.json";
        private const string SlowManifest = "https://slow.example/manifest.json";
        private const string BrokenManifest = "https://broken.example/manifest.json";

        private static AddonDescriptor Stream(string id, string url, params string[] types) =>
            new AddonDescriptor { Id = id, Name = id, TransportUrl = url, Resources = new[] { "stream" }, Types = types };

        private static (SourceResolver Resolver, FixtureHandler Handler, FakeClock Clock) Create(params AddonDescriptor[] addons)
        {
            var handler = new FixtureHandler();
            var clock = new FakeClock();
            var http = Fixtures.Http(handler);
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> account = null;
            if (addons.Length > 0) account = token => Task.FromResult<IReadOnlyList<AddonDescriptor>>(addons);
            var registry = new AddonRegistry(new MemoryStore(), http, account, clock.Func);
            return (new SourceResolver(registry, http, clock.Func), handler, clock);
        }

        private static string StreamUrl(string manifest, string type, string id) =>
            Uri.UnescapeDataString(AddonUrls.Resource(manifest, "stream", type, id));

        [Fact]
        public async Task RanksRealTorrentioStreams()
        {
            var (resolver, handler, _) = Create(Fixtures.TorrentioDescriptor());
            handler.Fixture("https://torrentio.strem.fun/stream/series/tt0903747:1:1.json", "torrentio-series-tt0903747-1-1.json");
            var ranked = await resolver.GetRankedAsync(MediaType.Series, "tt0903747:1:1", new SourceOptions(), CancellationToken.None);
            Assert.NotEmpty(ranked);
            Assert.All(ranked, c => Assert.Equal("com.stremio.torrentio.addon", c.AddonId));
            Assert.All(ranked, c => Assert.True(!c.SizeBytes.HasValue || c.SizeBytes <= 6.5e9));
            Assert.Equal(ranked.Select(c => c.Identity).Distinct().Count(), ranked.Count);
        }

        [Fact]
        public async Task ReturnsEarlyOnceAViableTopTierCandidateArrives()
        {
            var (resolver, handler, _) = Create(Stream("fast", FastManifest), Stream("slow", SlowManifest));
            handler.Json(StreamUrl(FastManifest, "movie", "tt1"), Fixtures.StreamsJson(
                    Fixtures.StreamJson("Movie.2020.1080p.WEB-DL.x264.ENG.SpaSub\n👤 500 💾 2 GB")))
                .Hang(StreamUrl(SlowManifest, "movie", "tt1"));

            var watch = Stopwatch.StartNew();
            var ranked = await resolver.GetRankedAsync(MediaType.Movie, "tt1", new SourceOptions(), CancellationToken.None);
            watch.Stop();
            Assert.Single(ranked);
            Assert.Equal(7, ranked[0].LanguageTier);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "should not wait for the hanging add-on");
        }

        [Fact]
        public async Task WaitsForAllAddonsWhenNothingIsViableUntilTheDeadline()
        {
            var (resolver, handler, _) = Create(Stream("fast", FastManifest), Stream("slow", SlowManifest));
            resolver.DiscoveryDeadline = TimeSpan.FromMilliseconds(300);
            handler.Json(StreamUrl(FastManifest, "movie", "tt2"), Fixtures.StreamsJson(
                    Fixtures.StreamJson("Movie.2020.1080p.WEB-DL.x264\n👤 500 💾 2 GB")))
                .Hang(StreamUrl(SlowManifest, "movie", "tt2"));

            var watch = Stopwatch.StartNew();
            var ranked = await resolver.GetRankedAsync(MediaType.Movie, "tt2", new SourceOptions(), CancellationToken.None);
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), "tier 0 is not viable, so discovery waits");
            Assert.Single(ranked);
        }

        [Fact]
        public async Task MergesAndDedupesAcrossAddonsAndSurvivesAFailingAddon()
        {
            var (resolver, handler, _) = Create(Stream("a", FastManifest), Stream("b", SlowManifest), Stream("broken", BrokenManifest));
            var shared = Fixtures.StreamJson("Shared.Movie.1080p.x264\n👤 90", hash: "ABCDEF0123456789ABCDEF0123456789ABCDEF01", fileIdx: 1);
            handler.Json(StreamUrl(FastManifest, "movie", "tt3"), Fixtures.StreamsJson(shared, Fixtures.StreamJson("Only.A.1080p.x264\n👤 80")))
                .Json(StreamUrl(SlowManifest, "movie", "tt3"), Fixtures.StreamsJson(
                    Fixtures.StreamJson("Shared.Movie.1080p.x264 (B copy)\n👤 90", hash: "abcdef0123456789abcdef0123456789abcdef01", fileIdx: 1),
                    Fixtures.StreamJson("Only.B.1080p.x264\n👤 70")))
                .Status(StreamUrl(BrokenManifest, "movie", "tt3"), HttpStatusCode.InternalServerError);

            var ranked = await resolver.GetRankedAsync(MediaType.Movie, "tt3", new SourceOptions(), CancellationToken.None);
            Assert.Equal(3, ranked.Count);
            Assert.Single(ranked, c => c.Identity == "abcdef0123456789abcdef0123456789abcdef01:1");
        }

        [Fact]
        public async Task SharesInFlightDiscoveryAndCachesTwoMinutes()
        {
            var (resolver, handler, clock) = Create(Stream("fast", FastManifest));
            var url = StreamUrl(FastManifest, "movie", "tt4");
            handler.Json(url, Fixtures.StreamsJson(Fixtures.StreamJson("Movie.1080p.x264\n👤 90")));

            await Task.WhenAll(
                resolver.GetRankedAsync(MediaType.Movie, "tt4", new SourceOptions(), CancellationToken.None),
                resolver.GetRankedAsync(MediaType.Movie, "tt4", new SourceOptions { Quality = "1080p" }, CancellationToken.None));
            Assert.Equal(1, handler.CountOf(url));

            clock.Advance(TimeSpan.FromSeconds(119));
            await resolver.GetRankedAsync(MediaType.Movie, "tt4", new SourceOptions(), CancellationToken.None);
            Assert.Equal(1, handler.CountOf(url));
            clock.Advance(TimeSpan.FromSeconds(2));
            await resolver.GetRankedAsync(MediaType.Movie, "tt4", new SourceOptions(), CancellationToken.None);
            Assert.Equal(2, handler.CountOf(url));
        }

        [Fact]
        public async Task EmptyResultsAreCachedOnlyFiveSeconds()
        {
            var (resolver, handler, clock) = Create(Stream("fast", FastManifest));
            var url = StreamUrl(FastManifest, "movie", "tt5");
            handler.Json(url, "{\"streams\":[]}");
            Assert.Empty(await resolver.GetRankedAsync(MediaType.Movie, "tt5", new SourceOptions(), CancellationToken.None));
            clock.Advance(TimeSpan.FromSeconds(6));
            await resolver.GetRankedAsync(MediaType.Movie, "tt5", new SourceOptions(), CancellationToken.None);
            Assert.Equal(2, handler.CountOf(url));
        }

        [Fact]
        public async Task CallerCancellationDoesNotBreakTheSharedDiscovery()
        {
            var (resolver, handler, _) = Create(Stream("slow", SlowManifest));
            var gate = new TaskCompletionSource<bool>();
            handler.Route(StreamUrl(SlowManifest, "movie", "tt6"), async (request, token) =>
            {
                await gate.Task;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Fixtures.StreamsJson(Fixtures.StreamJson("Movie.1080p.x264\n👤 90")), Encoding.UTF8, "application/json"),
                };
            });
            using (var cancel = new CancellationTokenSource())
            {
                var first = resolver.GetRankedAsync(MediaType.Movie, "tt6", new SourceOptions(), cancel.Token);
                var second = resolver.GetRankedAsync(MediaType.Movie, "tt6", new SourceOptions(), CancellationToken.None);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                gate.SetResult(true);
                Assert.Single(await second);
            }
        }

        [Fact]
        public async Task ExcludedIdentitiesAreSkippedAndDoNotCountAsViable()
        {
            var (resolver, handler, _) = Create(Stream("fast", FastManifest));
            var best = Fixtures.StreamJson("Movie.1080p.x264.ENG.SpaSub\n👤 900", hash: "1111111111111111111111111111111111111111", fileIdx: 0);
            var next = Fixtures.StreamJson("Movie.1080p.x264.ENG\n👤 800", hash: "2222222222222222222222222222222222222222", fileIdx: 0);
            handler.Json(StreamUrl(FastManifest, "movie", "tt7"), Fixtures.StreamsJson(best, next));
            var options = new SourceOptions { ExcludeIdentities = new HashSet<string> { "1111111111111111111111111111111111111111:0" } };
            var ranked = await resolver.GetRankedAsync(MediaType.Movie, "tt7", options, CancellationToken.None);
            Assert.Equal("2222222222222222222222222222222222222222", ranked.Single().InfoHash);
            Assert.False(resolver.IsViable(StreamRanking.ParseStreams(Newtonsoft.Json.Linq.JObject.Parse(Fixtures.StreamsJson(best, next)), null),
                MediaType.Movie, options, null));
        }

        [Fact]
        public async Task SkipsAddonsThatDoNotServeTheType()
        {
            var (resolver, handler, _) = Create(Stream("movies", FastManifest, "movie"), Stream("series", SlowManifest, "series"));
            handler.Json(StreamUrl(SlowManifest, "series", "tt8:1:1"), Fixtures.StreamsJson(Fixtures.StreamJson("Show.S01E01.1080p.x264\n👤 90")));
            var ranked = await resolver.GetRankedAsync(MediaType.Series, "tt8:1:1", new SourceOptions(), CancellationToken.None);
            Assert.Single(ranked);
            Assert.Equal(0, handler.CountOf(StreamUrl(FastManifest, "series", "tt8:1:1")));
        }

        [Fact]
        public async Task AnimeMetaSwitchesToAnimeRules()
        {
            var (resolver, handler, _) = Create(Fixtures.TorrentioDescriptor());
            handler.Fixture("https://torrentio.strem.fun/stream/series/tt2560140:1:1.json", "torrentio-series-tt2560140-1-1.json");
            var meta = new MetaDetail
            {
                Summary = new MetaSummary { Id = "tt2560140", Type = MediaType.Series, Name = "Attack on Titan" },
                Genres = new[] { "Animation", "Action" },
                Country = "Japan",
            };
            Assert.True(AnimeDetection.IsJapaneseAnime(meta));
            var ranked = await resolver.GetRankedAsync(MediaType.Series, "tt2560140:1:1", new SourceOptions(), meta, CancellationToken.None);
            Assert.True(ranked[0].LanguageTier >= 4);
        }

        [Fact]
        public void AnimeDetectionRules()
        {
            MetaDetail M(string country, params string[] genres) => new MetaDetail
            {
                Summary = new MetaSummary { Type = MediaType.Series },
                Country = country,
                Genres = genres,
            };
            var japanese = new[] { Fixtures.Torrent("[Group] Show - 01 [1080p] Japanese audio Eng subs") };
            var plain = new[] { Fixtures.Torrent("Show S01E01 1080p WEB-DL x264") };

            Assert.True(AnimeDetection.IsJapaneseAnime(M("", "Anime")));
            Assert.True(AnimeDetection.IsJapaneseAnime(M("Japan", "Animation")));
            Assert.False(AnimeDetection.IsJapaneseAnime(M("United States", "Animation")));
            Assert.False(AnimeDetection.PrefersJapaneseAudio(M("United States", "Animation"), japanese));
            Assert.True(AnimeDetection.IsAmbiguousAnimation(M("", "Animation")));
            Assert.True(AnimeDetection.IsAmbiguousAnimation(M("South Korea", "Animation")));
            Assert.True(AnimeDetection.PrefersJapaneseAudio(M("", "Animation"), japanese));
            Assert.False(AnimeDetection.PrefersJapaneseAudio(M("China", "Animation"), plain));
            Assert.False(AnimeDetection.PrefersJapaneseAudio(M("Japan", "Drama"), japanese));
        }

        [Fact]
        public void AmbiguousAnimationThatIsNotAnimeIsNeverViableEarly()
        {
            var (resolver, _, _) = Create();
            var meta = new MetaDetail { Summary = new MetaSummary { Type = MediaType.Series }, Genres = new[] { "Animation" }, Country = "" };
            var streams = new[] { Fixtures.Torrent("Show.S01E01.1080p.x264.ENG.SpaSub\n👤 900") };
            Assert.True(resolver.IsViable(streams, MediaType.Series, new SourceOptions(), null));
            Assert.False(resolver.IsViable(streams, MediaType.Series, new SourceOptions(), meta));
        }

        [Fact]
        public async Task SubtitlesComeFromOpenSubtitlesSpanishThenEnglish()
        {
            var (resolver, handler, _) = Create();
            var url = "https://opensubtitles-v3.strem.io/subtitles/series/tt0903747:1:1/filename=Night.Harbor.S01E01.mkv.json";
            handler.Json(url, "{\"subtitles\":[" +
                "{\"id\":\"1\",\"url\":\"https://subs.example/por.srt\",\"lang\":\"por\"}," +
                "{\"id\":\"2\",\"url\":\"https://subs.example/eng.srt\",\"lang\":\"eng\"}," +
                "{\"id\":\"3\",\"url\":\"https://subs.example/spa.srt\",\"lang\":\"spa\"}," +
                "{\"id\":\"4\",\"url\":\"ftp://subs.example/bad.srt\",\"lang\":\"spa\"}," +
                "{\"id\":\"5\",\"url\":\"https://subs.example/es2.srt\",\"lang\":\"es\"}]}");
            var source = new StreamCandidate { InfoHash = "abc", Filename = "Night.Harbor.S01E01.mkv" };
            var subtitles = await resolver.GetSubtitlesAsync(MediaType.Series, "tt0903747:1:1", source, CancellationToken.None);
            Assert.Equal(new[] { "spa", "es", "eng", "por" }, subtitles.Select(s => s.Language));
            Assert.All(subtitles, s => Assert.Equal("org.stremio.opensubtitlesv3", s.AddonId));

            Assert.Equal("https://subs.example/spa.srt", SubtitleSelection.SelectPreferred(subtitles, anime: false).Url);
            Assert.Equal("https://subs.example/eng.srt", SubtitleSelection.SelectPreferred(subtitles, anime: true).Url);
            Assert.Equal(new[] { "eng", "spa", "es", "por" }, SubtitleSelection.Order(subtitles, anime: true).Select(s => s.Language));
            Assert.Null(SubtitleSelection.SelectPreferred(subtitles.Where(s => s.Language == "por"), anime: false));
        }

        [Fact]
        public void SubtitleEmbeddedRules()
        {
            Assert.True(SubtitleSelection.ShouldSkipExternal("spa", anime: false));
            Assert.False(SubtitleSelection.ShouldSkipExternal("eng", anime: false));
            Assert.True(SubtitleSelection.ShouldSkipExternal("en-US", anime: true));
            Assert.Equal(30000, SubtitleSelection.ClampOffsetMs(45000));
            Assert.Equal(-30000, SubtitleSelection.ClampOffsetMs(-31000));
            Assert.Equal(1200, SubtitleSelection.ClampOffsetMs(1200));
        }
    }
}
