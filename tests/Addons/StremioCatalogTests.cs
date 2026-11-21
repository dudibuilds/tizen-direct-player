using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Xunit;

namespace Ru7100.Core.Tests.Addons
{
    public class StremioCatalogTests
    {
        private const string SearchSeries = "https://v3-cinemeta.strem.io/catalog/series/top/search=breaking bad.json";
        private const string SearchMovie = "https://v3-cinemeta.strem.io/catalog/movie/top/search=breaking bad.json";
        private const string MetaUrl = "https://v3-cinemeta.strem.io/meta/series/tt0903747.json";

        private static (StremioCatalog Catalog, FixtureHandler Handler, FakeClock Clock, List<TimeSpan> Delays) Create()
        {
            var handler = new FixtureHandler();
            var clock = new FakeClock();
            var delays = new List<TimeSpan>();
            var http = Fixtures.Http(handler, delays);
            var registry = new AddonRegistry(new MemoryStore(), http, null, clock.Func);
            return (new StremioCatalog(registry, http, clock.Func), handler, clock, delays);
        }

        [Theory]
        [InlineData("Breaking Bad", "Breaking Bad", 1.0)]
        [InlineData("breaking bad", "Breaking Bad: Original Minisodes", 0.9)]
        [InlineData("The Office", "Office", 0.9)]
        [InlineData("house of the dragon", "House of Cards", 0.5)]
        [InlineData("Dune Part Two", "Dune: Part Two", 1.0)]
        public void TitleSimilarityMatchesTheOriginal(string query, string title, double expected)
        {
            Assert.Equal(expected, StremioCatalog.TitleSimilarity(query, title), 6);
        }

        [Theory]
        [InlineData("breaking bad season 2 episode 3", "breaking bad", 2, 3)]
        [InlineData("Breaking Bad S01E05", "Breaking Bad", 1, 5)]
        [InlineData("the office s3 e12 please", "the office please", 3, 12)]
        [InlineData("severance season 1 ep 4", "severance", 1, 4)]
        [InlineData("dune part two", "dune part two", null, null)]
        public void ParsesEpisodes(string input, string query, int? season, int? episode)
        {
            var parsed = StremioCatalog.ParseEpisode(input);
            Assert.Equal(query, parsed.Query);
            Assert.Equal(season, parsed.Season);
            Assert.Equal(episode, parsed.Episode);
        }

        [Fact]
        public async Task SearchRanksBySimilarityAndCachesSharedLoads()
        {
            var (catalog, handler, clock, _) = Create();
            handler.Fixture(SearchSeries, "cinemeta-search-series-breaking-bad.json")
                .Json(SearchMovie, "{\"metas\":[{\"id\":\"tt9243946\",\"type\":\"movie\",\"name\":\"El Camino: A Breaking Bad Movie\",\"releaseInfo\":\"2019\"}]}");

            var both = await Task.WhenAll(
                catalog.SearchRankedAsync("breaking bad", null, 0, CancellationToken.None),
                catalog.SearchRankedAsync("Breaking Bad ", null, 0, CancellationToken.None));
            var results = both[0];
            Assert.Equal(1, handler.CountOf(SearchSeries));
            Assert.Equal("tt0903747", results[0].Meta.Id);
            Assert.Equal(1.0, results[0].MatchScore);
            Assert.Equal(MediaType.Series, results[0].Meta.Type);
            Assert.Equal(8, results.Count);   // default limit
            // 0.9 containment ties are ordered by releaseInfo descending.
            var containment = results.Where(r => r.MatchScore == 0.9).Select(r => r.Meta.ReleaseInfo).ToList();
            Assert.Equal(containment.OrderByDescending(x => x, StringComparer.Ordinal), containment);

            var limited = await catalog.SearchAsync("breaking bad", null, 2, CancellationToken.None);
            Assert.Equal(2, limited.Count);
            Assert.Equal(1, handler.CountOf(SearchSeries));

            clock.Advance(TimeSpan.FromSeconds(61));
            await catalog.SearchAsync("breaking bad", null, 2, CancellationToken.None);
            Assert.Equal(2, handler.CountOf(SearchSeries));
        }

        [Fact]
        public async Task SearchByTypeOnlyQueriesThatType()
        {
            var (catalog, handler, _, _) = Create();
            handler.Fixture(SearchSeries, "cinemeta-search-series-breaking-bad.json");
            var results = await catalog.SearchAsync("breaking bad", MediaType.Series, 50, CancellationToken.None);
            Assert.Equal(7, results.Count);
            Assert.Equal(0, handler.CountOf(SearchMovie));
        }

        [Fact]
        public void AmbiguityRule()
        {
            SearchMatch M(double score) => new SearchMatch { Meta = new MetaSummary(), MatchScore = score };
            Assert.True(StremioCatalog.IsAmbiguous(new[] { M(0.5), M(0.4) }));
            Assert.False(StremioCatalog.IsAmbiguous(new[] { M(0.5) }));
            Assert.False(StremioCatalog.IsAmbiguous(new[] { M(0.72), M(0.7) }));
            Assert.False(StremioCatalog.IsAmbiguous(new[] { M(0.6), M(0.4) }));
            Assert.True(StremioCatalog.IsAmbiguous(new[] { M(0.6), M(0.5) }));
        }

        [Fact]
        public async Task ResolveReturnsAmbiguousCandidates()
        {
            var (catalog, handler, _, _) = Create();
            handler.Json("https://v3-cinemeta.strem.io/catalog/series/top/search=red.json",
                "{\"metas\":[{\"id\":\"tt1\",\"type\":\"series\",\"name\":\"Red Rose\"},{\"id\":\"tt2\",\"type\":\"series\",\"name\":\"Red Dwarf\"},{\"id\":\"tt3\",\"type\":\"series\",\"name\":\"Red Oaks\"}]}")
                .Json("https://v3-cinemeta.strem.io/catalog/movie/top/search=red.json",
                "{\"metas\":[{\"id\":\"tt4\",\"type\":\"movie\",\"name\":\"Red Notice\"},{\"id\":\"tt5\",\"type\":\"movie\",\"name\":\"Red Eye\"},{\"id\":\"tt6\",\"type\":\"movie\",\"name\":\"Red Sparrow\"}]}");
            handler.Json("https://v3-cinemeta.strem.io/meta/series/tt1.json", "{\"meta\":{\"id\":\"tt1\",\"type\":\"series\",\"name\":\"Red Rose\"}}");
            // "red" is contained in each name -> 0.9 for all, not ambiguous by the rule (top >= 0.72).
            var clear = await catalog.ResolveAsync("red", null, CancellationToken.None);
            Assert.False(clear.Ambiguous);
            Assert.Equal("tt1", clear.Meta.Summary.Id);

            handler.Json("https://v3-cinemeta.strem.io/catalog/series/top/search=blue planet ocean.json",
                "{\"metas\":[{\"id\":\"tt7\",\"type\":\"series\",\"name\":\"Blue Planet II\"},{\"id\":\"tt8\",\"type\":\"series\",\"name\":\"Ocean Planet\"}]}")
                .Json("https://v3-cinemeta.strem.io/catalog/movie/top/search=blue planet ocean.json", "{\"metas\":[]}");
            var ambiguous = await catalog.ResolveAsync("blue planet ocean", null, CancellationToken.None);
            Assert.True(ambiguous.Ambiguous);
            Assert.Equal(new[] { "tt7", "tt8" }, ambiguous.Candidates.Select(c => c.Meta.Id));
            Assert.Null(ambiguous.Meta);
        }

        [Fact]
        public async Task ResolveTitleWithEpisodeLoadsSortedMeta()
        {
            var (catalog, handler, _, _) = Create();
            handler.Fixture(SearchSeries, "cinemeta-search-series-breaking-bad.json")
                .Json(SearchMovie, "{\"metas\":[]}")
                .Fixture(MetaUrl, "cinemeta-meta-series-tt0903747.json");

            var result = await catalog.ResolveAsync("breaking bad s02e03", null, CancellationToken.None);
            Assert.False(result.Ambiguous);
            Assert.Equal(2, result.Season);
            Assert.Equal(3, result.Episode);
            var meta = result.Meta;
            Assert.Equal("Breaking Bad", meta.Summary.Name);
            Assert.Equal(MediaType.Series, meta.Summary.Type);
            Assert.Equal("United States", meta.Country);
            Assert.Contains("Drama", meta.Genres);
            // Fixture videos are shuffled; meta must come back sorted by season, episode, released.
            var keys = meta.Videos.Select(v => v.Season * 1000 + v.Episode).ToList();
            Assert.Equal(keys.OrderBy(k => k), keys);
            Assert.Equal("tt0903747:1:1", meta.Videos.First(v => v.Season == 1).Id);
            Assert.Equal("Pilot", meta.Videos.First(v => v.Id == "tt0903747:1:1").Title);
            Assert.Equal(new DateTimeOffset(2008, 1, 21, 5, 0, 0, TimeSpan.Zero), meta.Videos.First(v => v.Id == "tt0903747:1:1").Released);
        }

        [Fact]
        public async Task ResolveImdbIdTriesSeriesThenMovie()
        {
            var (catalog, handler, _, _) = Create();
            handler.Json("https://v3-cinemeta.strem.io/meta/series/tt1375666.json", "{\"meta\":null}")
                .Json("https://v3-cinemeta.strem.io/meta/movie/tt1375666.json",
                    "{\"meta\":{\"id\":\"tt1375666\",\"type\":\"movie\",\"name\":\"Inception\",\"releaseInfo\":\"2010\",\"genres\":[\"Action\"]}}");
            var result = await catalog.ResolveAsync("tt1375666", null, CancellationToken.None);
            Assert.Equal("Inception", result.Meta.Summary.Name);
            Assert.Equal(MediaType.Movie, result.Meta.Summary.Type);
            Assert.Empty(result.Meta.Videos);
        }

        [Fact]
        public async Task RetriesServerErrorsWithBackoffAndDoesNotCacheFailures()
        {
            var (catalog, handler, _, delays) = Create();
            var meta = Fixtures.Read("cinemeta-meta-series-tt0903747.json");
            handler.Sequence(MetaUrl, (HttpStatusCode.ServiceUnavailable, "{}"), ((HttpStatusCode)429, "{}"), (HttpStatusCode.OK, meta));
            var detail = await catalog.GetMetaAsync(MediaType.Series, "tt0903747", CancellationToken.None);
            Assert.Equal("Breaking Bad", detail.Summary.Name);
            Assert.Equal(3, handler.CountOf(MetaUrl));
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500) }, delays);

            const string missing = "https://v3-cinemeta.strem.io/meta/series/tt0000001.json";
            handler.Sequence(missing, (HttpStatusCode.NotFound, "{}"), (HttpStatusCode.OK, meta));
            await Assert.ThrowsAsync<AddonRequestException>(() => catalog.GetMetaAsync(MediaType.Series, "tt0000001", CancellationToken.None));
            Assert.Equal(1, handler.CountOf(missing)); // 404 is not retried
            var second = await catalog.GetMetaAsync(MediaType.Series, "tt0000001", CancellationToken.None);
            Assert.NotNull(second);
            Assert.Equal(2, handler.CountOf(missing)); // failure was not cached
        }

        [Fact]
        public async Task GivesUpAfterThreeTries()
        {
            var (catalog, handler, _, _) = Create();
            handler.Status(MetaUrl, HttpStatusCode.BadGateway);
            var error = await Assert.ThrowsAsync<AddonRequestException>(() => catalog.GetMetaAsync(MediaType.Series, "tt0903747", CancellationToken.None));
            Assert.Equal(502, error.StatusCode);
            Assert.Equal(3, handler.CountOf(MetaUrl));
        }

        [Fact]
        public async Task BrowsesCinemetaCatalogByDefaultWithSkip()
        {
            var (catalog, handler, _, _) = Create();
            handler.Json("https://v3-cinemeta.strem.io/catalog/movie/top.json",
                    "{\"metas\":[{\"id\":\"tt1\",\"type\":\"movie\",\"name\":\"One\"},{\"id\":\"x\",\"type\":\"channel\",\"name\":\"skip me\"}]}")
                .Json("https://v3-cinemeta.strem.io/catalog/movie/top/skip=100.json",
                    "{\"metas\":[{\"id\":\"tt2\",\"type\":\"movie\",\"name\":\"Two\"}]}");
            var first = await catalog.GetCatalogAsync(AddonRegistry.CinemetaId, "top", MediaType.Movie, 0, CancellationToken.None);
            Assert.Equal(new[] { "tt1" }, first.Select(m => m.Id));
            var page = await catalog.GetCatalogAsync(AddonRegistry.CinemetaId, "top", MediaType.Movie, 100, CancellationToken.None);
            Assert.Equal(new[] { "tt2" }, page.Select(m => m.Id));
        }

        [Fact]
        public async Task RetriesTimeoutsThenFails()
        {
            var handler = new FixtureHandler().Hang("https://slow.example/x.json");
            var delays = new List<TimeSpan>();
            var http = Fixtures.Http(handler, delays);
            await Assert.ThrowsAsync<AddonRequestException>(() => http.GetJsonAsync("https://slow.example/x.json", TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Equal(3, handler.CountOf("https://slow.example/x.json"));
            Assert.Equal(2, delays.Count);
        }

        [Fact]
        public async Task CallerCancellationIsNotRetried()
        {
            var handler = new FixtureHandler().Hang("https://slow.example/y.json");
            var http = Fixtures.Http(handler);
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetJsonAsync("https://slow.example/y.json", TimeSpan.FromSeconds(20), cancel.Token));
            Assert.Equal(1, handler.CountOf("https://slow.example/y.json"));
        }

        [Fact]
        public async Task CachedMetaIsCopiedPerCaller()
        {
            var (catalog, handler, _, _) = Create();
            handler.Fixture(MetaUrl, "cinemeta-meta-series-tt0903747.json");
            var first = await catalog.GetMetaAsync(MediaType.Series, "tt0903747", CancellationToken.None);
            first.Summary.Name = "changed";
            first.Videos = Array.Empty<VideoInfo>();
            var second = await catalog.GetMetaAsync(MediaType.Series, "tt0903747", CancellationToken.None);
            Assert.Equal("Breaking Bad", second.Summary.Name);
            Assert.NotEmpty(second.Videos);
            Assert.Equal(1, handler.CountOf(MetaUrl));
        }

        [Fact]
        public void SortVideosAndReleasedFilter()
        {
            var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var meta = new MetaDetail
            {
                Videos = new[]
                {
                    new VideoInfo { Id = "c", Season = 2, Episode = 1, Released = now.AddDays(5) },
                    new VideoInfo { Id = "b", Season = 1, Episode = 2 },
                    new VideoInfo { Id = "a2", Season = 1, Episode = 1, Released = now.AddDays(-1) },
                    new VideoInfo { Id = "a1", Season = 1, Episode = 1, Released = now.AddDays(-2) },
                    new VideoInfo { Id = "s0", Season = 0, Episode = 1, Released = now.AddDays(-9) },
                },
            };
            Assert.Equal(new[] { "s0", "a1", "a2", "b", "c" }, StremioCatalog.SortVideos(meta.Videos).Select(v => v.Id));
            Assert.Equal(new[] { "s0", "a1", "a2", "b" }, StremioCatalog.ReleasedVideos(meta, now).Select(v => v.Id));
        }
    }
}
