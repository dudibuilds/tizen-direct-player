using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Addons
{
    /// A streaming service whose catalogue can be browsed as a feed (docs/streaming-services.md). Titles still play
    /// from our own sources; the service is only a way to browse.
    public sealed class StreamingService
    {
        public string Id { get; set; }              // JustWatch package short name in the US ("atp")
        public string Name { get; set; }            // plain text shown to the viewer ("Apple TV")
        public string AddonCatalogId { get; set; }  // the Streaming Catalogs add-on's catalog id (fallback)
        public string PageInk { get; set; }         // tinted page background, "#RRGGBB"
        public string Tint { get; set; }            // glow and gradient hue, "#RRGGBB"
    }

    /// The services shown in the Services row, in row order, with their approved tints (AGENTS.md, 2026-11-20).
    /// Names and colours only: no title data.
    public static class StreamingServices
    {
        /// The least restricted catalogue region (user decision, 2026-11-20).
        public const string Region = "US";

        public static readonly IReadOnlyList<StreamingService> All = new[]
        {
            new StreamingService { Id = "nfx", Name = "Netflix", AddonCatalogId = "nfx", PageInk = "#160B0E", Tint = "#7A2430" },
            new StreamingService { Id = "atp", Name = "Apple TV", AddonCatalogId = "atp", PageInk = "#0E1622", Tint = "#3C5A7A" },
            new StreamingService { Id = "dnp", Name = "Disney+", AddonCatalogId = "dnp", PageInk = "#0A1022", Tint = "#2A4C9A" },
            new StreamingService { Id = "mxx", Name = "HBO Max", AddonCatalogId = "hbm", PageInk = "#120C1F", Tint = "#5A3A9A" },
            new StreamingService { Id = "amp", Name = "Prime Video", AddonCatalogId = "amp", PageInk = "#091620", Tint = "#1F6680" },
            new StreamingService { Id = "hlu", Name = "Hulu", AddonCatalogId = "hlu", PageInk = "#0A1712", Tint = "#2F7A55" },
            new StreamingService { Id = "pmp", Name = "Paramount+", AddonCatalogId = "pmp", PageInk = "#0A1020", Tint = "#3354A0" },
            new StreamingService { Id = "pcp", Name = "Peacock", AddonCatalogId = "pcp", PageInk = "#0C1416", Tint = "#3F6E70" },
            new StreamingService { Id = "cru", Name = "Crunchyroll", AddonCatalogId = "cru", PageInk = "#170F0A", Tint = "#7A4A2A" }
        };

        /// By id or by name, ignoring case; null when unknown.
        public static StreamingService Find(string idOrName) =>
            idOrName == null ? null : All.FirstOrDefault(s => string.Equals(s.Id, idOrName, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    /// One title in a service's catalogue, with the facts the catalogue already returns (so rows need no extra lookups).
    public sealed class ServiceTitle
    {
        public MetaSummary Meta { get; set; }
        public string Rating { get; set; }          // "8.1"
        public string Runtime { get; set; }         // "51 min"
        public string Description { get; set; }
        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();
    }

    public sealed class ServicePage
    {
        public IReadOnlyList<ServiceTitle> Titles { get; set; } = Array.Empty<ServiceTitle>();
        public int Total { get; set; }              // titles in the whole filtered catalogue; 0 when unknown
    }

    public interface IServiceCatalog
    {
        /// Most popular first. type null = series and movies together; genre is one of Browse's genre names or null.
        Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token);

        /// Titles that arrived on the service, newest day first, walking back at most maxDays from today (UTC).
        Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token);
    }

    /// JustWatch's GraphQL API (unofficial and undocumented; approved as the primary source, 2026-11-20).
    public sealed class JustWatchCatalog : IServiceCatalog
    {
        public const string Endpoint = "https://apis.justwatch.com/graphql";
        private const string Fields = "title originalReleaseYear runtime shortDescription externalIds { imdbId } scoring { imdbScore } genres { shortName }";

        private const string PopularQuery =
            "query P($country: Country!, $language: Language!, $first: Int!, $offset: Int, $filter: TitleFilter, $sortBy: PopularTitlesSorting!) { " +
            "popularTitles(country: $country, first: $first, offset: $offset, filter: $filter, sortBy: $sortBy) { totalCount edges { node { objectType " +
            "content(country: $country, language: $language) { " + Fields + " } } } } }";

        private const string NewQuery =
            "query N($country: Country!, $language: Language!, $first: Int!, $filter: TitleFilter, $date: Date!) { " +
            "newTitles(country: $country, first: $first, filter: $filter, pageType: NEW, date: $date) { edges { node { __typename " +
            "... on Movie { content(country: $country, language: $language) { " + Fields + " } } " +
            "... on Season { show { content(country: $country, language: $language) { " + Fields + " } } } } } } }";

        /// Browse's genre names → JustWatch genre codes. JustWatch merges Action & Adventure and Mystery & Thriller.
        private static readonly Dictionary<string, string> GenreCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Action"] = "act", ["Adventure"] = "act", ["Animation"] = "ani", ["Comedy"] = "cmy", ["Crime"] = "crm",
            ["Documentary"] = "doc", ["Drama"] = "drm", ["Family"] = "fml", ["Fantasy"] = "fnt", ["Horror"] = "hrr",
            ["Mystery"] = "trl", ["Romance"] = "rma", ["Sci-Fi"] = "scf", ["Thriller"] = "trl", ["History"] = "hst",
            ["War"] = "war", ["Western"] = "wsn", ["Music"] = "msc", ["Sport"] = "spt", ["Reality-TV"] = "rly"
        };

        /// JustWatch genre codes → the names the app shows (a merged code yields both names).
        private static readonly Dictionary<string, string[]> GenreNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["act"] = new[] { "Action", "Adventure" }, ["ani"] = new[] { "Animation" }, ["cmy"] = new[] { "Comedy" },
            ["crm"] = new[] { "Crime" }, ["doc"] = new[] { "Documentary" }, ["drm"] = new[] { "Drama" }, ["fml"] = new[] { "Family" },
            ["fnt"] = new[] { "Fantasy" }, ["hrr"] = new[] { "Horror" }, ["trl"] = new[] { "Mystery", "Thriller" },
            ["rma"] = new[] { "Romance" }, ["scf"] = new[] { "Sci-Fi" }, ["hst"] = new[] { "History" }, ["war"] = new[] { "War" },
            ["wsn"] = new[] { "Western" }, ["msc"] = new[] { "Music" }, ["spt"] = new[] { "Sport" }, ["rly"] = new[] { "Reality-TV" }
        };

        private readonly HttpClient http;
        private readonly string country;

        public JustWatchCatalog(HttpMessageHandler handler = null, string country = StreamingServices.Region)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(15);
            this.country = country;
        }

        public static string GenreCode(string genre) => genre != null && GenreCodes.TryGetValue(genre, out var code) ? code : null;

        public async Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
        {
            var filter = new JObject
            {
                ["packages"] = new JArray(service.Id),
                ["monetizationTypes"] = new JArray("FLATRATE")
            };
            if (type != null)
                filter["objectTypes"] = new JArray(type == MediaType.Series ? "SHOW" : "MOVIE");
            if (!string.IsNullOrEmpty(genre))
            {
                var code = GenreCode(genre);
                if (code == null)
                    return new ServicePage();   // a genre JustWatch doesn't know: nothing matches
                filter["genres"] = new JArray(code);
            }
            var variables = new JObject
            {
                ["country"] = country, ["language"] = "en", ["first"] = count, ["offset"] = skip,
                ["filter"] = filter, ["sortBy"] = "POPULAR"
            };
            var data = await PostAsync(PopularQuery, variables, token).ConfigureAwait(false);
            var popular = data["popularTitles"] as JObject;
            var titles = new List<ServiceTitle>();
            foreach (var edge in (popular?["edges"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var node = edge["node"] as JObject;
                var t = Parse(node?["content"] as JObject, (string)node?["objectType"] == "SHOW" ? MediaType.Series : MediaType.Movie);
                if (t != null) titles.Add(t);
            }
            return new ServicePage { Titles = titles, Total = (int?)popular?["totalCount"] ?? 0 };
        }

        public async Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token)
        {
            var result = new List<ServiceTitle>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var day = 0; day < maxDays && result.Count < count; day++)
            {
                var variables = new JObject
                {
                    ["country"] = country, ["language"] = "en", ["first"] = 40,
                    ["filter"] = new JObject { ["packages"] = new JArray(service.Id) },
                    ["date"] = today.Date.AddDays(-day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
                var data = await PostAsync(NewQuery, variables, token).ConfigureAwait(false);
                foreach (var edge in (data["newTitles"]?["edges"] as JArray ?? new JArray()).OfType<JObject>())
                {
                    var node = edge["node"] as JObject;
                    var season = (string)node?["__typename"] == "Season";
                    var content = season ? node?["show"]?["content"] as JObject : node?["content"] as JObject;
                    var t = Parse(content, season ? MediaType.Series : MediaType.Movie);
                    if (t != null && seen.Add(t.Meta.Id))
                        result.Add(t);
                    if (result.Count >= count) break;
                }
            }
            return result;
        }

        /// Titles without an IMDb id are dropped: sources are found by IMDb id.
        internal static ServiceTitle Parse(JObject content, MediaType type)
        {
            var id = (string)content?["externalIds"]?["imdbId"];
            if (string.IsNullOrEmpty(id) || !id.StartsWith("tt", StringComparison.Ordinal))
                return null;
            var year = (int?)content["originalReleaseYear"];
            var runtime = (int?)content["runtime"];
            var score = (double?)content["scoring"]?["imdbScore"];
            var genres = new List<string>();
            foreach (var g in (content["genres"] as JArray ?? new JArray()).OfType<JObject>())
                if ((string)g["shortName"] is string code && GenreNames.TryGetValue(code, out var names))
                    foreach (var name in names)
                        if (!genres.Contains(name)) genres.Add(name);
            return new ServiceTitle
            {
                Meta = new MetaSummary
                {
                    Id = id, Type = type, Name = (string)content["title"],
                    ReleaseInfo = year?.ToString(CultureInfo.InvariantCulture),
                    Poster = "https://images.metahub.space/poster/medium/" + id + "/img"
                },
                Rating = score > 0 ? score.Value.ToString("0.0", CultureInfo.InvariantCulture) : null,
                Runtime = runtime > 0 ? runtime.Value.ToString(CultureInfo.InvariantCulture) + " min" : null,
                Description = (string)content["shortDescription"],
                Genres = genres
            };
        }

        private async Task<JObject> PostAsync(string query, JObject variables, CancellationToken token)
        {
            var body = new JObject { ["query"] = query, ["variables"] = variables }.ToString(Formatting.None);
            using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (var response = await http.PostAsync(Endpoint, content, token).ConfigureAwait(false))
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("JustWatch: HTTP " + (int)response.StatusCode);
                var json = JObject.Parse(text);
                if (json["errors"] is JArray errors && errors.Count > 0)
                    throw new HttpRequestException("JustWatch: " + ((string)errors[0]?["message"] ?? "query failed"));
                return json["data"] as JObject ?? new JObject();
            }
        }
    }

    /// The Streaming Catalogs Stremio add-on: ~99 popular titles per service, no paging, no genres, no new arrivals.
    /// Used only when JustWatch fails.
    public sealed class StreamingCatalogsAddon : IServiceCatalog
    {
        public const string BaseUrl = "https://7a82163c306e-stremio-netflix-catalog-addon.baby-beamup.club";
        private readonly AddonHttp http;

        public StreamingCatalogsAddon(AddonHttp http) => this.http = http;

        /// The add-on's configuration path: every service's catalog, US.
        public static string ConfigPath()
        {
            var config = string.Join(",", StreamingServices.All.Select(s => s.AddonCatalogId)) + "::" + StreamingServices.Region + ":0:0:0:";
            return Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(config)));
        }

        public async Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
        {
            if (skip > 0 || !string.IsNullOrEmpty(genre) || service.AddonCatalogId == null)
                return new ServicePage();
            var types = type == null ? new[] { MediaType.Series, MediaType.Movie } : new[] { type.Value };
            var lists = await Task.WhenAll(types.Select(t => GetListAsync(service, t, token))).ConfigureAwait(false);
            // Series and movies interleaved, so a mixed row isn't all one type.
            var merged = new List<ServiceTitle>();
            for (var i = 0; merged.Count < count && lists.Any(l => i < l.Count); i++)
                foreach (var list in lists)
                    if (i < list.Count && merged.Count < count) merged.Add(list[i]);
            return new ServicePage { Titles = merged };
        }

        public Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ServiceTitle>>(Array.Empty<ServiceTitle>());

        private async Task<IReadOnlyList<ServiceTitle>> GetListAsync(StreamingService service, MediaType type, CancellationToken token)
        {
            var url = BaseUrl + "/" + ConfigPath() + "/catalog/" + AddonTypes.ToStremio(type) + "/" + service.AddonCatalogId + ".json";
            var json = await http.GetJsonAsync(url, token).ConfigureAwait(false);
            var titles = new List<ServiceTitle>();
            foreach (var meta in (json["metas"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var id = (string)meta["imdb_id"] ?? (string)meta["id"];
                if (string.IsNullOrEmpty(id) || !id.StartsWith("tt", StringComparison.Ordinal))
                    continue;
                titles.Add(new ServiceTitle
                {
                    Meta = new MetaSummary
                    {
                        Id = id, Type = type, Name = (string)meta["name"],
                        ReleaseInfo = (string)meta["releaseInfo"] ?? (string)meta["year"],
                        Poster = (string)meta["poster"] ?? "https://images.metahub.space/poster/medium/" + id + "/img"
                    },
                    Rating = (string)meta["imdbRating"],
                    Runtime = (string)meta["runtime"],
                    Description = (string)meta["description"],
                    Genres = (meta["genre"] ?? meta["genres"]) is JArray g ? g.Values<string>().Where(s => s != null).ToList() : (IReadOnlyList<string>)Array.Empty<string>()
                });
            }
            return titles;
        }
    }

    /// JustWatch first; when it fails (it is unofficial and may change), the add-on's popular list.
    public sealed class FallbackServiceCatalog : IServiceCatalog
    {
        private readonly IServiceCatalog primary;
        private readonly IServiceCatalog fallback;

        public FallbackServiceCatalog(IServiceCatalog primary, IServiceCatalog fallback)
        {
            this.primary = primary;
            this.fallback = fallback;
        }

        public async Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
        {
            try { return await primary.GetTitlesAsync(service, type, genre, skip, count, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { }
            return await fallback.GetTitlesAsync(service, type, genre, skip, count, token).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token)
        {
            try { return await primary.GetNewAsync(service, today, maxDays, count, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { }
            return await fallback.GetNewAsync(service, today, maxDays, count, token).ConfigureAwait(false);
        }
    }
}
