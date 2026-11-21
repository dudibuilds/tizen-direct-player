using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Xunit;

namespace Ru7100.Core.Tests.Addons
{
    /// Made-up catalogue responses in JustWatch's and the Streaming Catalogs add-on's shapes. No real network.
    public class StreamingServicesTests
    {
        private static readonly StreamingService Apple = StreamingServices.Find("atp");

        /// Answers every GraphQL POST with respond(variables, query) and records the variables.
        private static (FixtureHandler Handler, List<JObject> Calls) GraphQl(Func<JObject, string, string> respond)
        {
            var calls = new List<JObject>();
            var handler = new FixtureHandler().Route(JustWatchCatalog.Endpoint, async (request, token) =>
            {
                var body = JObject.Parse(await request.Content.ReadAsStringAsync());
                var variables = (JObject)body["variables"];
                lock (calls) calls.Add(variables);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(variables, (string)body["query"]), Encoding.UTF8, "application/json") };
            });
            return (handler, calls);
        }

        private static string Content(string title, string imdb, int year = 2024, int runtime = 50, double? score = 7.5, params string[] genres) =>
            new JObject
            {
                ["title"] = title, ["originalReleaseYear"] = year, ["runtime"] = runtime, ["shortDescription"] = "About " + title + ".",
                ["externalIds"] = new JObject { ["imdbId"] = imdb }, ["scoring"] = new JObject { ["imdbScore"] = score },
                ["genres"] = new JArray(genres.Select(g => new JObject { ["shortName"] = g }))
            }.ToString();

        [Fact]
        public async Task PopularPageSendsFilterAndPagingAndParsesTitles()
        {
            var (handler, calls) = GraphQl((v, q) => "{\"data\":{\"popularTitles\":{\"totalCount\":33,\"edges\":[" +
                "{\"node\":{\"objectType\":\"SHOW\",\"content\":" + Content("Glass Harbor", "tt0000101", 2024, 54, 7.6, "drm", "trl", "scf") + "}}," +
                "{\"node\":{\"objectType\":\"SHOW\",\"content\":" + Content("No Id Show", null) + "}}," +
                "{\"node\":{\"objectType\":\"MOVIE\",\"content\":" + Content("Quiet Orbit", "tt0000102", 2023, 0, null, "act") + "}}]}}}");
            var catalog = new JustWatchCatalog(handler);

            var page = await catalog.GetTitlesAsync(Apple, MediaType.Series, "Sci-Fi", 50, 50, CancellationToken.None);

            var v = calls.Single();
            Assert.Equal("US", (string)v["country"]);
            Assert.Equal(50, (int)v["offset"]);
            Assert.Equal(50, (int)v["first"]);
            Assert.Equal("POPULAR", (string)v["sortBy"]);
            Assert.Equal(new[] { "atp" }, v["filter"]["packages"].Values<string>());
            Assert.Equal(new[] { "SHOW" }, v["filter"]["objectTypes"].Values<string>());
            Assert.Equal(new[] { "scf" }, v["filter"]["genres"].Values<string>());
            Assert.Equal(new[] { "FLATRATE" }, v["filter"]["monetizationTypes"].Values<string>());

            Assert.Equal(33, page.Total);
            Assert.Equal(new[] { "tt0000101", "tt0000102" }, page.Titles.Select(t => t.Meta.Id));   // no IMDb id: dropped
            var first = page.Titles[0];
            Assert.Equal(MediaType.Series, first.Meta.Type);
            Assert.Equal("Glass Harbor", first.Meta.Name);
            Assert.Equal("2024", first.Meta.ReleaseInfo);
            Assert.Equal("https://images.metahub.space/poster/medium/tt0000101/img", first.Meta.Poster);
            Assert.Equal("7.6", first.Rating);
            Assert.Equal("54 min", first.Runtime);
            Assert.Equal(new[] { "Drama", "Mystery", "Thriller", "Sci-Fi" }, first.Genres);
            var second = page.Titles[1];
            Assert.Equal(MediaType.Movie, second.Meta.Type);
            Assert.Null(second.Rating);
            Assert.Null(second.Runtime);
        }

        [Fact]
        public async Task BothTypesLeaveObjectTypesOutAndUnknownGenreSkipsTheRequest()
        {
            var (handler, calls) = GraphQl((v, q) => "{\"data\":{\"popularTitles\":{\"totalCount\":0,\"edges\":[]}}}");
            var catalog = new JustWatchCatalog(handler);

            await catalog.GetTitlesAsync(Apple, null, null, 0, 20, CancellationToken.None);
            Assert.Null(calls.Single()["filter"]["objectTypes"]);
            Assert.Null(calls.Single()["filter"]["genres"]);

            var none = await catalog.GetTitlesAsync(Apple, MediaType.Movie, "Opera", 0, 20, CancellationToken.None);
            Assert.Empty(none.Titles);
            Assert.Single(calls);
        }

        [Fact]
        public async Task NewTitlesWalkBackDayByDayUntilFullAndResolveSeasonsToTheirShow()
        {
            var (handler, calls) = GraphQl((v, q) =>
            {
                switch ((string)v["date"])
                {
                    case "2026-11-20":
                        return "{\"data\":{\"newTitles\":{\"edges\":[" +
                            "{\"node\":{\"__typename\":\"Season\",\"show\":{\"content\":" + Content("Glass Harbor", "tt0000101") + "}}}," +
                            "{\"node\":{\"__typename\":\"Movie\",\"content\":" + Content("Quiet Orbit", "tt0000102") + "}}]}}}";
                    case "2026-11-19":
                        return "{\"data\":{\"newTitles\":{\"edges\":[]}}}";
                    default:
                        return "{\"data\":{\"newTitles\":{\"edges\":[" +
                            "{\"node\":{\"__typename\":\"Season\",\"show\":{\"content\":" + Content("Glass Harbor", "tt0000101") + "}}}," +
                            "{\"node\":{\"__typename\":\"Season\",\"show\":{\"content\":" + Content("Lantern Row", "tt0000103") + "}}}]}}}";
                }
            });
            var catalog = new JustWatchCatalog(handler);

            var titles = await catalog.GetNewAsync(Apple, new DateTime(2026, 11, 20), 30, 3, CancellationToken.None);

            Assert.Equal(new[] { "tt0000101", "tt0000102", "tt0000103" }, titles.Select(t => t.Meta.Id));   // deduplicated
            Assert.Equal(MediaType.Series, titles[0].Meta.Type);
            Assert.Equal("Glass Harbor", titles[0].Meta.Name);
            Assert.Equal(MediaType.Movie, titles[1].Meta.Type);
            Assert.Equal(new[] { "2026-11-20", "2026-11-19", "2026-11-18" }, calls.Select(c => (string)c["date"]));   // stops once full
            Assert.All(calls, c => Assert.Equal(new[] { "atp" }, c["filter"]["packages"].Values<string>()));
        }

        [Fact]
        public async Task GraphQlErrorsFallBackToTheAddonsPopularList()
        {
            var (handler, _) = GraphQl((v, q) => "{\"errors\":[{\"message\":\"schema changed\"}],\"data\":null}");
            var addonUrl = StreamingCatalogsAddon.BaseUrl + "/" + Uri.UnescapeDataString(StreamingCatalogsAddon.ConfigPath()) + "/catalog/series/atp.json";
            handler.Json(addonUrl, "{\"metas\":[{\"imdb_id\":\"tt0000201\",\"name\":\"Paper Moons\",\"type\":\"series\",\"releaseInfo\":\"2025–\",\"imdbRating\":\"8.0\",\"genre\":[\"Drama\"]}," +
                "{\"id\":\"kitsu:1\",\"name\":\"Not IMDb\"}]}");
            var catalog = new FallbackServiceCatalog(new JustWatchCatalog(handler), new StreamingCatalogsAddon(new AddonHttp(handler)));

            var page = await catalog.GetTitlesAsync(Apple, MediaType.Series, null, 0, 20, CancellationToken.None);
            var title = Assert.Single(page.Titles);
            Assert.Equal("tt0000201", title.Meta.Id);
            Assert.Equal("Paper Moons", title.Meta.Name);
            Assert.Equal("8.0", title.Rating);
            Assert.Equal("https://images.metahub.space/poster/medium/tt0000201/img", title.Meta.Poster);

            // The add-on has one fixed list: no further pages, no genres, no new arrivals.
            Assert.Empty((await catalog.GetTitlesAsync(Apple, MediaType.Series, null, 50, 20, CancellationToken.None)).Titles);
            Assert.Empty((await catalog.GetTitlesAsync(Apple, MediaType.Series, "Drama", 0, 20, CancellationToken.None)).Titles);
            Assert.Empty(await catalog.GetNewAsync(Apple, new DateTime(2026, 11, 20), 7, 20, CancellationToken.None));
        }

        [Fact]
        public void AddonConfigNamesEveryServiceInTheUs()
        {
            var config = Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(StreamingCatalogsAddon.ConfigPath())));
            Assert.Equal("nfx,atp,dnp,hbm,amp,hlu,pmp,pcp,cru::US:0:0:0:", config);
        }

        [Fact]
        public void ServicesHaveNamesAndApprovedTints()
        {
            Assert.Equal(new[] { "Netflix", "Apple TV", "Disney+", "HBO Max", "Prime Video", "Hulu", "Paramount+", "Peacock", "Crunchyroll" }, StreamingServices.All.Select(s => s.Name));
            Assert.All(StreamingServices.All, s =>
            {
                Assert.Matches("^#[0-9A-F]{6}$", s.PageInk);
                Assert.Matches("^#[0-9A-F]{6}$", s.Tint);
                Assert.NotEqual("#FFB23E", s.Tint);   // amber stays the viewer-state colour
            });
            Assert.Same(StreamingServices.Find("atp"), StreamingServices.Find("apple tv"));
            Assert.Null(StreamingServices.Find("Nope"));
        }
    }
}
