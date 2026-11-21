using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Xunit;

namespace Ru7100.Core.Tests.Addons
{
    public class AddonRegistryTests
    {
        private const string TorrentioConfigured = "https://torrentio.strem.fun/providers=yts,eztv|qualityfilter=480p,scr,cam/manifest.json";

        private static AddonRegistry Create(FixtureHandler handler, MemoryStore store = null,
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> account = null, FakeClock clock = null) =>
            new AddonRegistry(store ?? new MemoryStore(), Fixtures.Http(handler), account, (clock ?? new FakeClock()).Func);

        [Theory]
        [InlineData("https://torrentio.strem.fun/manifest.json", "https://torrentio.strem.fun/manifest.json")]
        [InlineData("https://torrentio.strem.fun", "https://torrentio.strem.fun/manifest.json")]
        [InlineData("https://torrentio.strem.fun/", "https://torrentio.strem.fun/manifest.json")]
        [InlineData("stremio://torrentio.strem.fun/manifest.json", "https://torrentio.strem.fun/manifest.json")]
        [InlineData("  https://example.org/addon/manifest.json?token=abc  ", "https://example.org/addon/manifest.json?token=abc")]
        [InlineData("https://example.org/addon?token=abc", "https://example.org/addon/manifest.json?token=abc")]
        [InlineData(TorrentioConfigured, TorrentioConfigured)]
        public void NormalizesManifestUrls(string input, string expected)
        {
            Assert.Equal(expected, AddonUrls.NormalizeManifestUrl(input));
        }

        [Theory]
        [InlineData("ftp://example.org/manifest.json")]
        [InlineData("file:///etc/manifest.json")]
        [InlineData("not a url")]
        [InlineData("https://user:pw@example.org/manifest.json")]
        public void RejectsNonHttpUrls(string input)
        {
            Assert.Throws<ArgumentException>(() => AddonUrls.NormalizeManifestUrl(input));
        }

        [Fact]
        public void BaseAndResourceUrls()
        {
            Assert.Equal("https://torrentio.strem.fun/providers=yts,eztv|qualityfilter=480p,scr,cam", AddonUrls.BaseUrl(TorrentioConfigured));
            Assert.Equal("https://example.org/a", AddonUrls.BaseUrl("https://example.org/a/manifest.json?x=1"));
            Assert.Equal("https://torrentio.strem.fun/stream/series/tt0903747%3A1%3A1.json",
                AddonUrls.Resource("https://torrentio.strem.fun/manifest.json", "stream", "series", "tt0903747:1:1"));
            Assert.Equal("https://v3-cinemeta.strem.io/catalog/movie/top/skip=20.json",
                AddonUrls.Resource(AddonRegistry.CinemetaManifestUrl, "catalog", "movie", "top", "skip=20"));
        }

        [Fact]
        public async Task InstallValidatesPersistsAndReloads()
        {
            var handler = new FixtureHandler().Fixture("https://torrentio.strem.fun/manifest.json", "torrentio-manifest.json");
            var store = new MemoryStore();
            var registry = Create(handler, store);

            var installed = await registry.InstallAsync("https://torrentio.strem.fun", CancellationToken.None);
            Assert.Equal("com.stremio.torrentio.addon", installed.Id);
            Assert.Equal("https://torrentio.strem.fun/manifest.json", installed.TransportUrl);
            Assert.Equal(new[] { "stream" }, installed.Resources);
            Assert.Contains("series", installed.Types);
            Assert.Empty(installed.Catalogs());
            Assert.True(store.Values.ContainsKey(AddonRegistry.StoreKey));

            // Reinstall replaces instead of duplicating.
            await registry.InstallAsync("https://torrentio.strem.fun/manifest.json", CancellationToken.None);

            var reloaded = Create(new FixtureHandler(), store);
            var list = await reloaded.GetInstalledAsync(CancellationToken.None);
            Assert.Single(list);
            Assert.Equal("Torrentio", list[0].Name);
        }

        [Fact]
        public async Task InstallParsesCatalogsWithRequiredExtras()
        {
            var handler = new FixtureHandler().Fixture(AddonRegistry.CinemetaManifestUrl, "cinemeta-manifest.json");
            var registry = Create(handler);
            var cinemeta = await registry.InstallAsync(AddonRegistry.CinemetaManifestUrl, CancellationToken.None);
            var catalogs = cinemeta.Catalogs();
            Assert.Contains(catalogs, c => c.Id == "top" && c.Type == "movie" && c.SupportsSearch && c.IsBrowsable);
            var year = catalogs.First(c => c.Id == "year" && c.Type == "movie");
            Assert.Equal(new[] { "genre" }, year.ExtraRequired);
            Assert.False(year.IsBrowsable);
            Assert.False(catalogs.First(c => c.Id == "last-videos").IsBrowsable);

            var rows = await registry.GetHomeCatalogsAsync(CancellationToken.None);
            Assert.Equal(new[] { "movie/top", "series/top", "movie/imdbRating", "series/imdbRating" },
                rows.Select(r => r.Catalog.Type + "/" + r.Catalog.Id));
            Assert.Equal("Popular Movies", rows[0].Title);
        }

        [Theory]
        [InlineData("{\"name\":\"x\",\"resources\":[\"stream\"],\"types\":[\"movie\"],\"catalogs\":[]}")]
        [InlineData("{\"id\":\"x\",\"resources\":[\"stream\"],\"types\":[\"movie\"],\"catalogs\":[]}")]
        [InlineData("{\"id\":\"x\",\"name\":\"x\",\"resources\":[],\"types\":[\"movie\"],\"catalogs\":[]}")]
        [InlineData("{\"id\":\"x\",\"name\":\"x\",\"resources\":[\"stream\"],\"catalogs\":[]}")]
        [InlineData("{\"id\":\"x\",\"name\":\"x\",\"resources\":[\"stream\"],\"types\":[\"movie\"]}")]
        [InlineData("{\"id\":\"x\",\"name\":\"x\",\"resources\":[\"stream\"],\"types\":[\"movie\"],\"catalogs\":{}}")]
        public async Task InstallRejectsInvalidManifests(string manifest)
        {
            var handler = new FixtureHandler().Json("https://bad.example/manifest.json", manifest);
            var store = new MemoryStore();
            await Assert.ThrowsAsync<AddonRequestException>(() => Create(handler, store).InstallAsync("https://bad.example/manifest.json", CancellationToken.None));
            Assert.Empty(store.Values);
        }

        [Fact]
        public async Task AccountAddonsComeFirstAndDedupeById()
        {
            var handler = new FixtureHandler()
                .Fixture("https://torrentio.strem.fun/manifest.json", "torrentio-manifest.json")
                .Json("https://local.example/manifest.json", "{\"id\":\"local.only\",\"name\":\"Local\",\"resources\":[{\"name\":\"stream\",\"types\":[\"movie\"]}],\"types\":[],\"catalogs\":[]}");
            var calls = 0;
            var clock = new FakeClock();
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> account = token =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<AddonDescriptor>>(new[]
                {
                    new AddonDescriptor { Id = "com.stremio.torrentio.addon", Name = "Torrentio (account)", TransportUrl = "https://torrentio.strem.fun/sort=size/manifest.json", Resources = new[] { "stream" } },
                    new AddonDescriptor { Id = AddonRegistry.CastAddonId, Name = "Cast", TransportUrl = "http://127.0.0.1:4512/stremio-cast/manifest.json", Resources = new[] { "stream" } },
                    new AddonDescriptor { Id = "other.cast", Name = "Loopback cast", TransportUrl = "http://localhost:9/stremio-cast/manifest.json", Resources = new[] { "stream" } },
                    new AddonDescriptor { Id = "org.stremio.opensubtitlesv3", Name = "OpenSubtitles", TransportUrl = AddonRegistry.OpenSubtitlesManifestUrl, Resources = new[] { "subtitles" } },
                });
            };
            var registry = Create(handler, account: account, clock: clock);
            await registry.InstallAsync("https://torrentio.strem.fun/manifest.json", CancellationToken.None);
            await registry.InstallAsync("https://local.example/manifest.json", CancellationToken.None);

            var list = await registry.GetInstalledAsync(CancellationToken.None);
            Assert.Equal(new[] { "Torrentio (account)", "OpenSubtitles", "Local" }, list.Select(a => a.Name));

            var streams = await registry.GetForResourceAsync("stream", MediaType.Series, CancellationToken.None);
            Assert.Equal(new[] { "com.stremio.torrentio.addon" }, streams.Select(a => a.Id)); // Local declares movie only
            var movieStreams = await registry.GetForResourceAsync("stream", MediaType.Movie, CancellationToken.None);
            Assert.Equal(new[] { "com.stremio.torrentio.addon", "local.only" }, movieStreams.Select(a => a.Id));

            Assert.Equal(1, calls); // cached 60 s
            clock.Advance(TimeSpan.FromSeconds(61));
            await registry.GetInstalledAsync(CancellationToken.None);
            Assert.Equal(2, calls);

            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RemoveAsync("org.stremio.opensubtitlesv3", CancellationToken.None));
        }

        [Fact]
        public async Task AccountFailureFallsBackToLocalAndDefaults()
        {
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> account = token => throw new InvalidOperationException("offline");
            var registry = Create(new FixtureHandler(), account: account);
            Assert.Empty(await registry.GetInstalledAsync(CancellationToken.None));
            Assert.Empty(await registry.GetForResourceAsync("stream", MediaType.Movie, CancellationToken.None)); // no built-in stream add-on
            Assert.Equal(AddonRegistry.OpenSubtitlesManifestUrl, (await registry.GetForResourceAsync("subtitles", null, CancellationToken.None)).Single().TransportUrl);
            Assert.Equal(AddonRegistry.CinemetaManifestUrl, (await registry.GetForResourceAsync("catalog", null, CancellationToken.None)).Single().TransportUrl);
            Assert.Equal(AddonRegistry.CinemetaManifestUrl, (await registry.GetForResourceAsync("meta", null, CancellationToken.None)).Single().TransportUrl);
        }

        [Fact]
        public async Task CastAddonAloneLeavesNoStreamSource()
        {
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> account = token => Task.FromResult<IReadOnlyList<AddonDescriptor>>(new[]
            {
                new AddonDescriptor { Id = AddonRegistry.CastAddonId, Name = "Cast", TransportUrl = "http://127.0.0.1:4512/stremio-cast/manifest.json", Resources = new[] { "stream" } },
            });
            var registry = Create(new FixtureHandler(), account: account);
            var streams = await registry.GetForResourceAsync("stream", MediaType.Movie, CancellationToken.None);
            Assert.Empty(streams);
        }

        [Fact]
        public async Task RemoveDeletesLocalInstall()
        {
            var handler = new FixtureHandler().Fixture("https://torrentio.strem.fun/manifest.json", "torrentio-manifest.json");
            var store = new MemoryStore();
            var registry = Create(handler, store);
            await registry.InstallAsync("https://torrentio.strem.fun/manifest.json", CancellationToken.None);
            await registry.RemoveAsync("com.stremio.torrentio.addon", CancellationToken.None);
            Assert.Empty(await registry.GetInstalledAsync(CancellationToken.None));
            Assert.Empty(await Create(new FixtureHandler(), store).GetInstalledAsync(CancellationToken.None));
            await registry.RemoveAsync("unknown.addon", CancellationToken.None); // no-op
        }

        [Fact]
        public async Task AccountAddonCatalogsAreFetchedFromTheManifestOnce()
        {
            var handler = new FixtureHandler().Fixture("https://cinemeta.mirror.example/manifest.json", "cinemeta-manifest.json");
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> account = token => Task.FromResult<IReadOnlyList<AddonDescriptor>>(new[]
            {
                new AddonDescriptor { Id = "com.linvo.cinemeta", Name = "Cinemeta", TransportUrl = "https://cinemeta.mirror.example/manifest.json", Resources = new[] { "catalog", "meta" }, Types = new[] { "movie", "series" } },
            });
            var registry = Create(handler, account: account);
            var rows = await registry.GetHomeCatalogsAsync(CancellationToken.None);
            Assert.Equal(4, rows.Count);
            await registry.GetHomeCatalogsAsync(CancellationToken.None);
            Assert.Equal(1, handler.CountOf("https://cinemeta.mirror.example/manifest.json"));
        }

        [Fact]
        public async Task DefaultHomeRowsAreCinemetaTopAndFeatured()
        {
            var rows = await Create(new FixtureHandler()).GetHomeCatalogsAsync(CancellationToken.None);
            Assert.Equal(new[] { "Popular Movies", "Popular Series", "Featured Movies", "Featured Series" }, rows.Select(r => r.Title));
            Assert.All(rows, r => Assert.Equal(AddonRegistry.CinemetaId, r.Addon.Id));
        }
    }
}
