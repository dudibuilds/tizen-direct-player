using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Addons
{
    /// Cinemeta search/meta (B1-B6) and catalog browsing for any installed catalog add-on.
    public sealed class StremioCatalog : ICatalog
    {
        public const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";
        private static readonly TimeSpan SearchTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MetaTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan CatalogTtl = TimeSpan.FromMinutes(5);
        private const double AmbiguousTopScore = 0.72;
        private const double AmbiguousMargin = 0.15;

        private readonly AddonRegistry registry;
        private readonly AddonHttp http;
        private readonly AsyncCache<IReadOnlyList<SearchMatch>> searchCache;
        private readonly AsyncCache<MetaDetail> metaCache;
        private readonly AsyncCache<IReadOnlyList<MetaSummary>> catalogCache;

        public StremioCatalog(AddonRegistry registry, AddonHttp http, Func<DateTimeOffset> clock = null)
        {
            this.registry = registry;
            this.http = http ?? new AddonHttp();
            searchCache = new AsyncCache<IReadOnlyList<SearchMatch>>(256, clock);
            metaCache = new AsyncCache<MetaDetail>(256, clock);
            catalogCache = new AsyncCache<IReadOnlyList<MetaSummary>>(256, clock);
        }

        // ---- B1 search ----

        public async Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token)
        {
            var matches = await SearchRankedAsync(query, type, limit, token).ConfigureAwait(false);
            return matches.Select(match => match.Meta).ToList();
        }

        /// Cinemeta search ranked by titleSimilarity, then releaseInfo descending. limit is clamped to 1..20 (<= 0 means 8).
        public async Task<IReadOnlyList<SearchMatch>> SearchRankedAsync(string query, MediaType? type, int limit, CancellationToken token)
        {
            var text = (query ?? "").Trim();
            if (text.Length == 0) throw new ArgumentException("query is required", nameof(query));
            var types = type.HasValue ? new[] { type.Value } : new[] { MediaType.Series, MediaType.Movie };
            var key = string.Join("+", types.Select(AddonTypes.ToStremio)) + ":" + text.ToLowerInvariant();
            var ranked = await searchCache.GetOrLoadAsync(key, async () =>
            {
                var payloads = await Task.WhenAll(types.Select(mediaType => http.GetJsonAsync(
                    CinemetaBaseUrl + "/catalog/" + AddonTypes.ToStremio(mediaType) + "/top/search=" + Uri.EscapeDataString(text) + ".json",
                    CancellationToken.None))).ConfigureAwait(false);
                var all = new List<SearchMatch>();
                foreach (var payload in payloads)
                    foreach (var meta in ParseMetas(payload))
                        all.Add(new SearchMatch { Meta = meta, MatchScore = TitleSimilarity(text, meta.Name) });
                return (IReadOnlyList<SearchMatch>)all
                    .OrderByDescending(match => match.MatchScore)
                    .ThenByDescending(match => match.Meta.ReleaseInfo ?? "", StringComparer.Ordinal)
                    .ToList();
            }, value => SearchTtl, token).ConfigureAwait(false);
            var count = limit <= 0 ? 8 : Math.Min(20, limit);
            return ranked.Take(count).Select(Clone).ToList();
        }

        // ---- B2 resolve ----

        /// B2 + B4: parses "season N episode M"/"sNNeMM", then resolves the rest as a tt id or a search.
        public async Task<ResolveResult> ResolveAsync(string queryOrId, MediaType? type, CancellationToken token)
        {
            var parsed = ParseEpisode(queryOrId);
            var text = parsed.Query;
            if (Regex.IsMatch(text, "^tt\\d+$", RegexOptions.IgnoreCase))
            {
                foreach (var mediaType in type.HasValue ? new[] { type.Value } : new[] { MediaType.Series, MediaType.Movie })
                {
                    try
                    {
                        var meta = await GetMetaAsync(mediaType, text, token).ConfigureAwait(false);
                        return new ResolveResult { Meta = meta, Season = parsed.Season, Episode = parsed.Episode };
                    }
                    catch (AddonRequestException)
                    {
                    }
                }
            }
            var results = await SearchRankedAsync(text, type, 5, token).ConfigureAwait(false);
            if (results.Count == 0) throw new AddonRequestException("No Stremio result found for: " + text);
            if (IsAmbiguous(results))
                return new ResolveResult { Ambiguous = true, Candidates = results.Take(5).ToList(), Season = parsed.Season, Episode = parsed.Episode };
            var top = results[0].Meta;
            var detail = await GetMetaAsync(top.Type, top.Id, token).ConfigureAwait(false);
            return new ResolveResult { Meta = detail, Season = parsed.Season, Episode = parsed.Episode };
        }

        /// B2: more than one result, top below 0.72, and top - second below 0.15.
        public static bool IsAmbiguous(IReadOnlyList<SearchMatch> ranked) =>
            ranked.Count > 1 && ranked[0].MatchScore < AmbiguousTopScore
                && ranked[0].MatchScore - ranked[1].MatchScore < AmbiguousMargin;

        // ---- B3 meta ----

        public async Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required", nameof(id));
            var key = AddonTypes.ToStremio(type) + ":" + id;
            var meta = await metaCache.GetOrLoadAsync(key, async () =>
            {
                var payload = await http.GetJsonSharedAsync(
                    CinemetaBaseUrl + "/meta/" + AddonTypes.ToStremio(type) + "/" + Uri.EscapeDataString(id) + ".json",
                    CancellationToken.None).ConfigureAwait(false);
                if (!(payload["meta"] is JObject metaJson))
                    throw new AddonRequestException("No metadata for " + AddonTypes.ToStremio(type) + "/" + id);
                return ParseMetaDetail(metaJson, type);
            }, value => MetaTtl, token).ConfigureAwait(false);
            return CloneMeta(meta);
        }

        // ---- catalogs (Home rows) ----

        public async Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token)
        {
            AddonDescriptor addon = null;
            if (registry != null) addon = await registry.FindAsync(addonId, token).ConfigureAwait(false);
            if (addon == null && addonId == AddonRegistry.CinemetaId) addon = AddonRegistry.DefaultCinemeta();
            if (addon == null) throw new AddonRequestException("Add-on not installed: " + addonId);
            var extra = skip > 0 ? "skip=" + skip.ToString(CultureInfo.InvariantCulture) : null;
            var url = AddonUrls.Resource(addon.TransportUrl, "catalog", AddonTypes.ToStremio(type), catalogId, extra);
            return await catalogCache.GetOrLoadAsync(url, async () =>
            {
                var payload = await http.GetJsonAsync(url, CancellationToken.None).ConfigureAwait(false);
                return (IReadOnlyList<MetaSummary>)ParseMetas(payload).ToList();
            }, value => CatalogTtl, token).ConfigureAwait(false);
        }

        // ---- pure helpers ----

        /// B1 titleSimilarity: equal 1, containment 0.9, else shared words / max word count.
        public static double TitleSimilarity(string query, string title)
        {
            var a = NormalizeTitle(query);
            var b = NormalizeTitle(title);
            if (a == b) return 1;
            if (a.Contains(b) || b.Contains(a)) return 0.9;
            var aa = new HashSet<string>(a.Split(' '));
            var bb = new HashSet<string>(b.Split(' '));
            var common = aa.Count(word => bb.Contains(word));
            return (double)common / Math.Max(Math.Max(aa.Count, bb.Count), 1);
        }

        private static readonly Regex NonAlnum = new Regex("[^a-z0-9]+", RegexOptions.CultureInvariant);

        public static string NormalizeTitle(string value) =>
            NonAlnum.Replace((value ?? "").ToLowerInvariant(), " ").Trim();

        private static readonly Regex EpisodePattern = new Regex(
            @"(?:season\s*(\d+)\s*(?:episode|ep)\s*(\d+)|s(\d{1,2})\s*e(\d{1,3}))",
            RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.ECMAScript);

        /// B4: "season N episode|ep M" or "sNN eNNN"; the match is stripped from the query.
        public static EpisodeQuery ParseEpisode(string query)
        {
            var text = query ?? "";
            var match = EpisodePattern.Match(text);
            if (!match.Success) return new EpisodeQuery { Query = text.Trim() };
            var season = int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value, CultureInfo.InvariantCulture);
            var episode = int.Parse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value, CultureInfo.InvariantCulture);
            var stripped = text.Substring(0, match.Index) + " " + text.Substring(match.Index + match.Length);
            return new EpisodeQuery { Query = Spaces.Replace(stripped, " ").Trim(), Season = season, Episode = episode };
        }

        /// B3: season, then episode, then released (missing = epoch). Stable.
        public static IReadOnlyList<VideoInfo> SortVideos(IEnumerable<VideoInfo> videos) =>
            (videos ?? Enumerable.Empty<VideoInfo>())
                .OrderBy(video => video.Season)
                .ThenBy(video => video.Episode)
                .ThenBy(video => video.Released ?? DateTimeOffset.FromUnixTimeMilliseconds(0))
                .ToList();

        /// B3: released means no date or date <= now.
        public static IReadOnlyList<VideoInfo> ReleasedVideos(MetaDetail meta, DateTimeOffset now) =>
            SortVideos(meta?.Videos).Where(video => !video.Released.HasValue || video.Released.Value <= now).ToList();

        public static IEnumerable<MetaSummary> ParseMetas(JObject payload)
        {
            if (!(payload?["metas"] is JArray metas)) yield break;
            foreach (var item in metas.OfType<JObject>())
            {
                var summary = ParseSummary(item, null);
                if (summary != null) yield return summary;
            }
        }

        public static MetaSummary ParseSummary(JObject meta, MediaType? fallbackType)
        {
            var id = Str(meta["id"]);
            var type = AddonTypes.Parse(Str(meta["type"])) ?? fallbackType;
            if (string.IsNullOrEmpty(id) || !type.HasValue) return null;
            return new MetaSummary
            {
                Id = id,
                Type = type.Value,
                Name = Str(meta["name"]),
                ReleaseInfo = Str(meta["releaseInfo"]) ?? Str(meta["year"]),
                Poster = Str(meta["poster"]),
            };
        }

        public static MetaDetail ParseMetaDetail(JObject meta, MediaType type)
        {
            var summary = ParseSummary(meta, type) ?? new MetaSummary { Type = type };
            var genres = (meta["genres"] as JArray ?? meta["genre"] as JArray)?
                .Where(token => token.Type == JTokenType.String).Select(token => (string)token).ToList() ?? new List<string>();
            var videos = new List<VideoInfo>();
            if (meta["videos"] is JArray videoArray)
            {
                foreach (var item in videoArray.OfType<JObject>())
                {
                    var videoId = Str(item["id"]);
                    if (string.IsNullOrEmpty(videoId)) continue;
                    videos.Add(new VideoInfo
                    {
                        Id = videoId,
                        Season = Int(item["season"]) ?? 0,
                        Episode = Int(item["episode"]) ?? Int(item["number"]) ?? 0,
                        Released = Date(item["released"]),
                        Title = Str(item["title"]) ?? Str(item["name"]),
                    });
                }
            }
            return new MetaDetail
            {
                Summary = summary,
                Genres = genres,
                Country = Str(meta["country"]),
                Description = Str(meta["description"]),
                Background = Str(meta["background"]),
                Videos = SortVideos(videos),
            };
        }

        /// Callers get their own copy so a mutation cannot corrupt the 5 minute cache.
        public static MetaDetail CloneMeta(MetaDetail meta) => meta == null ? null : new MetaDetail
        {
            Summary = meta.Summary == null ? null : new MetaSummary
            {
                Id = meta.Summary.Id,
                Type = meta.Summary.Type,
                Name = meta.Summary.Name,
                ReleaseInfo = meta.Summary.ReleaseInfo,
                Poster = meta.Summary.Poster,
            },
            Genres = meta.Genres.ToList(),
            Country = meta.Country,
            Description = meta.Description,
            Background = meta.Background,
            Videos = meta.Videos.Select(video => new VideoInfo
            {
                Id = video.Id,
                Season = video.Season,
                Episode = video.Episode,
                Released = video.Released,
                Title = video.Title,
            }).ToList(),
        };

        private static SearchMatch Clone(SearchMatch match) => new SearchMatch
        {
            MatchScore = match.MatchScore,
            Meta = new MetaSummary
            {
                Id = match.Meta.Id,
                Type = match.Meta.Type,
                Name = match.Meta.Name,
                ReleaseInfo = match.Meta.ReleaseInfo,
                Poster = match.Meta.Poster,
            },
        };

        private static string Str(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return (string)token;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.ToString();
            return null;
        }

        private static int? Int(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.Integer) return (int)(long)token;
            if (token.Type == JTokenType.Float) return (int)(double)token;
            if (token.Type == JTokenType.String && int.TryParse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return value;
            return null;
        }

        private static DateTimeOffset? Date(JToken token)
        {
            var text = Str(token);
            if (string.IsNullOrEmpty(text)) return null;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value : (DateTimeOffset?)null;
        }
    }
}
