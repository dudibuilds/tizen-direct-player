using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Addons;

namespace Ru7100.Core.Ui
{
    // ---- Extra Cinemeta fields the contracts don't carry (episode overview/still, rating, runtime, trailers) ----

    public sealed class EpisodeExtras
    {
        public string Overview { get; set; }
        public string Thumbnail { get; set; }
    }

    public sealed class MetaExtras
    {
        public string Rating { get; set; }                 // "8.7"
        public string Runtime { get; set; }                // "49 min"
        public string Logo { get; set; }
        public string Description { get; set; }
        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Cast { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Trailers { get; set; } = Array.Empty<string>();   // YouTube ids
        public IReadOnlyDictionary<string, EpisodeExtras> Episodes { get; set; } = new Dictionary<string, EpisodeExtras>();
        /// Series: number of seasons with episodes (season 0, specials, not counted). 0 when unknown or a movie.
        public int Seasons { get; set; }

        public string TrailerId => Trailers.Count > 0 ? Trailers[0] : null;
    }

    /// Adapter for fields core/Addons' StremioCatalog drops (it is outside core/Ui and the contracts are fixed).
    public interface IMetaExtras
    {
        Task<MetaExtras> GetAsync(MediaType type, string id, CancellationToken token);
    }

    /// Cinemeta JSON → contract types and extras. Used by CinemetaExtras and the preview fixtures.
    public static class CinemetaJson
    {
        public static MetaDetail ParseMeta(JObject meta)
        {
            var type = AddonTypes.Parse((string)meta["type"]) ?? MediaType.Movie;
            var videos = (meta["videos"] as JArray ?? new JArray()).OfType<JObject>().Select(v => new VideoInfo
            {
                Id = (string)v["id"],
                Season = (int?)v["season"] ?? 0,
                Episode = (int?)v["episode"] ?? (int?)v["number"] ?? 0,
                Released = Date(v["released"] ?? v["firstAired"]),
                Title = (string)v["title"] ?? (string)v["name"]
            }).Where(v => v.Id != null).OrderBy(v => v.Season).ThenBy(v => v.Episode).ThenBy(v => v.Released).ToList();
            return new MetaDetail
            {
                Summary = Summary(meta),
                Genres = Strings(meta["genres"] ?? meta["genre"]),
                Country = (string)meta["country"],
                Description = (string)meta["description"],
                Background = (string)meta["background"],
                Videos = videos
            };
        }

        public static MetaSummary Summary(JObject meta) => new MetaSummary
        {
            Id = (string)meta["id"] ?? (string)meta["imdb_id"],
            Type = AddonTypes.Parse((string)meta["type"]) ?? MediaType.Movie,
            Name = (string)meta["name"],
            ReleaseInfo = (string)meta["releaseInfo"] ?? (string)meta["year"],
            Poster = (string)meta["poster"]
        };

        public static MetaExtras ParseExtras(JObject meta)
        {
            var trailers = new List<string>();
            foreach (var t in (meta["trailerStreams"] as JArray ?? new JArray()).OfType<JObject>())
                if ((string)t["ytId"] is string id && !trailers.Contains(id)) trailers.Add(id);
            foreach (var t in (meta["trailers"] as JArray ?? new JArray()).OfType<JObject>())
                if ((string)t["source"] is string id && !trailers.Contains(id)) trailers.Add(id);
            var episodes = new Dictionary<string, EpisodeExtras>();
            var seasons = new HashSet<int>();
            foreach (var v in (meta["videos"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var id = (string)v["id"];
                if (id == null) continue;
                if ((int?)v["season"] is int season && season > 0) seasons.Add(season);
                episodes[id] = new EpisodeExtras { Overview = (string)v["overview"] ?? (string)v["description"], Thumbnail = (string)v["thumbnail"] };
            }
            var rating = meta["imdbRating"];
            return new MetaExtras
            {
                Rating = rating == null || rating.Type == JTokenType.Null ? null : Convert.ToString(((JValue)rating).Value, CultureInfo.InvariantCulture),
                Runtime = (string)meta["runtime"],
                Logo = (string)meta["logo"],
                Description = (string)meta["description"],
                Genres = Strings(meta["genres"] ?? meta["genre"]),
                Cast = Strings(meta["cast"]),
                Trailers = trailers,
                Episodes = episodes,
                Seasons = seasons.Count
            };
        }

        private static IReadOnlyList<string> Strings(JToken token) =>
            token is JArray array ? array.Select(t => (string)t).Where(s => !string.IsNullOrEmpty(s)).ToList() : (IReadOnlyList<string>)Array.Empty<string>();

        private static DateTimeOffset? Date(JToken token)
        {
            var text = (string)token;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : (DateTimeOffset?)null;
        }
    }

    /// Fetches /meta/{type}/{id}.json from Cinemeta for the extras. Small in-memory cache.
    public sealed class CinemetaExtras : IMetaExtras
    {
        private readonly HttpClient http;
        private readonly string baseUrl;
        private readonly Dictionary<string, Task<MetaExtras>> cache = new Dictionary<string, Task<MetaExtras>>();
        private readonly LinkedList<string> recent = new LinkedList<string>();
        private const int Limit = 64;

        private readonly AddonHttp shared;

        public CinemetaExtras(HttpMessageHandler handler = null, string baseUrl = StremioCatalog.CinemetaBaseUrl)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(15);
            this.baseUrl = baseUrl;
        }

        /// Shares the catalog's AddonHttp: the title page's metadata and extras then come from one request and one
        /// parse of the same Cinemeta document (AddonHttp.GetJsonSharedAsync).
        public CinemetaExtras(AddonHttp shared, string baseUrl = StremioCatalog.CinemetaBaseUrl) : this((HttpMessageHandler)null, baseUrl)
        {
            this.shared = shared;
        }

        public Task<MetaExtras> GetAsync(MediaType type, string id, CancellationToken token)
        {
            var key = AddonTypes.ToStremio(type) + ":" + id;
            lock (cache)
            {
                if (cache.TryGetValue(key, out var hit) && !hit.IsFaulted && !hit.IsCanceled)
                    return token.CanBeCanceled && !hit.IsCompleted ? TaskUtil.WithCancellation(hit, token) : hit;
                var task = LoadAsync(type, id);
                cache[key] = task;
                recent.AddFirst(key);
                while (recent.Count > Limit)
                {
                    cache.Remove(recent.Last.Value);
                    recent.RemoveLast();
                }
                // The cached request is shared by every screen asking for this title: a caller's cancel (a superseded
                // search, a closed page) only stops its own wait, never the request the others wait on.
                return token.CanBeCanceled && !task.IsCompleted ? TaskUtil.WithCancellation(task, token) : task;
            }
        }

        private async Task<MetaExtras> LoadAsync(MediaType type, string id)
        {
            var url = baseUrl + "/meta/" + AddonTypes.ToStremio(type) + "/" + Uri.EscapeDataString(id) + ".json";
            if (shared != null)
            {
                var document = await shared.GetJsonSharedAsync(url, CancellationToken.None).ConfigureAwait(false);
                return document["meta"] is JObject sharedMeta ? CinemetaJson.ParseExtras(sharedMeta) : new MetaExtras();
            }
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            return JObject.Parse(json)["meta"] is JObject meta ? CinemetaJson.ParseExtras(meta) : new MetaExtras();
        }
    }

    // ---- Search across every installed catalog add-on that supports the `search` extra ----

    public sealed class SearchItem
    {
        public MetaSummary Meta { get; set; }
        public string AddonName { get; set; }
        public bool InLibrary { get; set; }
        public double Match { get; set; }
    }

    public sealed class SearchResults
    {
        public string Query { get; set; }
        public IReadOnlyList<string> Suggestions { get; set; } = Array.Empty<string>();
        public IReadOnlyList<SearchItem> Items { get; set; } = Array.Empty<SearchItem>();
    }

    public interface ISearchService
    {
        Task<SearchResults> SearchAsync(string query, CancellationToken token);
    }

    /// A search that also reports what it has found so far (on a worker thread), so the screen shows the fast
    /// catalogs' results without waiting for the slowest add-on. The final result is the same as SearchAsync's.
    public interface IProgressiveSearchService : ISearchService
    {
        Task<SearchResults> SearchAsync(string query, Action<SearchResults> partial, CancellationToken token);
    }

    public static class SearchMerge
    {
        /// Dedupes by id (first add-on wins), marks library titles, ranks by title similarity, adds
        /// library-only matches, and derives up to 6 text suggestions.
        public static SearchResults Merge(string query, IEnumerable<SearchItem> found, IEnumerable<LibraryEntry> library)
        {
            var libraryList = (library ?? Enumerable.Empty<LibraryEntry>()).Where(e => !e.Removed && !e.Temp).ToList();
            var libraryIds = new HashSet<string>(libraryList.Select(e => e.Id));
            var seen = new HashSet<string>();
            var items = new List<SearchItem>();
            foreach (var item in found)
            {
                if (item?.Meta?.Id == null || !seen.Add(item.Meta.Id)) continue;
                item.InLibrary = libraryIds.Contains(item.Meta.Id);
                item.Match = StremioCatalog.TitleSimilarity(query, item.Meta.Name ?? "");
                items.Add(item);
            }
            foreach (var entry in libraryList)
            {
                if (seen.Contains(entry.Id) || StremioCatalog.TitleSimilarity(query, entry.Name ?? "") < 0.5) continue;
                seen.Add(entry.Id);
                items.Add(new SearchItem
                {
                    Meta = new MetaSummary { Id = entry.Id, Type = entry.Type, Name = entry.Name, Poster = entry.Poster },
                    AddonName = "Library", InLibrary = true, Match = StremioCatalog.TitleSimilarity(query, entry.Name ?? "")
                });
            }
            var ranked = items.Select((item, index) => new { item, index })
                // Exact title first, then titles containing the query, then the rest; library titles lead each group.
                .OrderByDescending(x => x.item.Match >= 0.999 ? 3 : x.item.Match >= 0.9 ? 2 : x.item.InLibrary && x.item.Match >= 0.5 ? 1 : 0)
                .ThenByDescending(x => x.item.InLibrary)
                .ThenBy(x => x.index).Select(x => x.item).ToList();
            var suggestions = ranked.Select(i => i.Meta.Name).Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            return new SearchResults { Query = query, Items = ranked, Suggestions = suggestions };
        }
    }

    /// Real search: every installed catalog with a `search` extra, in parallel, 8 s per add-on. Add-on manifests are
    /// read in parallel too, and each catalog's answer is reported as it lands (IProgressiveSearchService).
    public sealed class AddonSearchService : IProgressiveSearchService
    {
        private readonly AddonRegistry registry;
        private readonly ILibrary library;
        private readonly HttpClient http;

        public AddonSearchService(AddonRegistry registry, ILibrary library = null, HttpMessageHandler handler = null)
        {
            this.registry = registry;
            this.library = library;
            http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(8);
        }

        public Task<SearchResults> SearchAsync(string query, CancellationToken token) => SearchAsync(query, null, token);

        public async Task<SearchResults> SearchAsync(string query, Action<SearchResults> partial, CancellationToken token)
        {
            var text = (query ?? "").Trim();
            if (text.Length == 0) return new SearchResults { Query = text };
            var addons = await registry.GetForResourceAsync("catalog", null, token).ConfigureAwait(false);
            if (!addons.Any(a => a.Id == AddonRegistry.CinemetaId))
                addons = addons.Concat(new[] { AddonRegistry.DefaultCinemeta() }).ToList();
            var libraryTask = library == null ? Task.FromResult<IReadOnlyList<LibraryEntry>>(Array.Empty<LibraryEntry>()) : SafeLibrary(token);
            // Every manifest at once: they used to be read one after another before the first search request went out.
            var manifests = addons.Select(addon => registry.GetCatalogsAsync(addon, token)).ToList();
            var requests = new List<Task<IReadOnlyList<SearchItem>>>();   // add-on order, then catalog order
            var reporter = partial == null ? null : new PartialReporter(text, requests, libraryTask, partial, token);
            for (var i = 0; i < addons.Count; i++)
            {
                var catalogs = await manifests[i].ConfigureAwait(false);
                foreach (var catalog in catalogs.Where(c => c.SupportsSearch && c.MediaType.HasValue))
                {
                    var request = FetchAsync(addons[i], catalog, text, token);
                    lock (requests)
                        requests.Add(request);
                    reporter?.Watch(request);
                }
            }
            Task<IReadOnlyList<SearchItem>>[] all;
            lock (requests)
                all = requests.ToArray();
            var lists = await Task.WhenAll(all).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return SearchMerge.Merge(text, lists.SelectMany(l => l), await libraryTask.ConfigureAwait(false));
        }

        /// Merges the catalogs that have answered so far in request order (so a partial ranking only grows toward the
        /// final one) and reports it each time another catalog brought titles. Never holds a report for the library.
        private sealed class PartialReporter
        {
            private readonly string query;
            private readonly List<Task<IReadOnlyList<SearchItem>>> requests;
            private readonly Task<IReadOnlyList<LibraryEntry>> libraryTask;
            private readonly Action<SearchResults> partial;
            private readonly CancellationToken token;
            private readonly object reportGate = new object();
            private int reportedItems;

            public PartialReporter(string query, List<Task<IReadOnlyList<SearchItem>>> requests, Task<IReadOnlyList<LibraryEntry>> libraryTask,
                Action<SearchResults> partial, CancellationToken token)
            {
                this.query = query;
                this.requests = requests;
                this.libraryTask = libraryTask;
                this.partial = partial;
                this.token = token;
            }

            public void Watch(Task<IReadOnlyList<SearchItem>> request) =>
                request.ContinueWith(done => { if (done.Status == TaskStatus.RanToCompletion && done.Result.Count > 0) Report(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            private void Report()
            {
                lock (reportGate)
                {
                    if (token.IsCancellationRequested)
                        return;
                    List<SearchItem> found;
                    lock (requests)
                        found = requests.Where(r => r.Status == TaskStatus.RanToCompletion).SelectMany(r => r.Result)
                            // Copies: Merge marks the items it is given, and the final merge owns the originals.
                            .Select(i => new SearchItem { Meta = i.Meta, AddonName = i.AddonName }).ToList();
                    if (found.Count <= reportedItems)
                        return;
                    reportedItems = found.Count;
                    var entries = libraryTask.Status == TaskStatus.RanToCompletion ? libraryTask.Result : null;
                    try { partial(SearchMerge.Merge(query, found, entries)); }
                    catch (Exception) { /* a partial is only a preview of the final result */ }
                }
            }
        }

        private async Task<IReadOnlyList<LibraryEntry>> SafeLibrary(CancellationToken token)
        {
            try { return await library.GetAllAsync(token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { return Array.Empty<LibraryEntry>(); }
        }

        private async Task<IReadOnlyList<SearchItem>> FetchAsync(AddonDescriptor addon, CatalogDescriptor catalog, string query, CancellationToken token)
        {
            var url = AddonUrls.Resource(addon.TransportUrl, "catalog", catalog.Type, catalog.Id, "search=" + Uri.EscapeDataString(query));
            try
            {
                using (var response = await http.GetAsync(url, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) return Array.Empty<SearchItem>();
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    return (json["metas"] as JArray ?? new JArray()).OfType<JObject>().Select(m =>
                    {
                        var summary = CinemetaJson.Summary(m);
                        if (summary.Type == MediaType.Movie && catalog.MediaType == MediaType.Series) summary.Type = MediaType.Series;
                        return new SearchItem { Meta = summary, AddonName = addon.Name };
                    }).Where(i => i.Meta.Id != null && i.Meta.Name != null).Take(20).ToList();
                }
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return Array.Empty<SearchItem>();   // one slow or broken add-on never blocks the others
            }
        }
    }

    /// Fallback when only an ICatalog is available (tests, fakes).
    public sealed class CatalogSearchService : ISearchService
    {
        private readonly ICatalog catalog;
        private readonly ILibrary library;

        public CatalogSearchService(ICatalog catalog, ILibrary library = null)
        {
            this.catalog = catalog;
            this.library = library;
        }

        public async Task<SearchResults> SearchAsync(string query, CancellationToken token)
        {
            var found = await catalog.SearchAsync(query, null, 20, token).ConfigureAwait(false);
            var entries = library == null ? Array.Empty<LibraryEntry>() : await library.GetAllAsync(token).ConfigureAwait(false);
            return SearchMerge.Merge(query, found.Select(m => new SearchItem { Meta = m, AddonName = "Cinemeta" }), entries);
        }
    }

    // ---- Source list with hard-filtered entries and display facts ----

    public sealed class SourceEntry
    {
        public StreamCandidate Candidate { get; set; }
        public string AddonName { get; set; }
        /// Why the device can never play it (hidden by default), or null.
        public string RejectReason { get; set; }
        public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
        public SourceFacts Facts { get; set; }
        public bool Playable => RejectReason == null;
    }

    public interface ISourceCatalog
    {
        Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token);
    }

    /// Real sources from core/Addons: ranked candidates plus the streams the hard filters dropped.
    public sealed class AddonSourceCatalog : ISourceCatalog
    {
        private readonly SourceResolver resolver;

        public AddonSourceCatalog(SourceResolver resolver) => this.resolver = resolver;

        public async Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token)
        {
            var streams = await resolver.DiscoverAsync(meta.Summary.Type, video?.Id ?? meta.Summary.Id, null, token).ConfigureAwait(false);
            return FromStreams(streams, meta, video, options, resolver.Device);
        }

        /// Ranks raw add-on streams with core/Addons rules and appends the ones that were filtered out, with why.
        public static IReadOnlyList<SourceEntry> FromStreams(IReadOnlyList<AddonStreamItem> streams, MetaDetail meta, VideoInfo video, SourceOptions options, DeviceCaps device)
        {
            options = options ?? new SourceOptions();
            var anime = options.IsAnime || AnimeDetection.PrefersJapaneseAudio(meta, streams);
            var rank = new RankOptions { Episodic = video != null, Anime = anime, EnglishOriginal = options.EnglishOriginal || SourceOptions.IsEnglishOriginal(meta), Quality = options.Quality, MaxSizeGb = options.MaxSizeGb, Device = device };
            var ranked = StreamRanking.RankAnalyses(streams, rank);
            var list = ranked.Select(a => Entry(StreamRanking.ToCandidate(a, anime), a.Reasons, null, a.Stream.AddonName, anime)).ToList();
            var rankedIds = new HashSet<string>(ranked.Select(a => a.Stream.Identity));
            foreach (var stream in StreamRanking.Deduplicate(streams))
            {
                if (rankedIds.Contains(stream.Identity)) continue;
                var analysis = StreamRanking.Analyze(stream, video != null, anime, device);
                var reason = analysis.RejectReason ?? (analysis.SizeBytes > StreamRanking.EffectiveMaxSizeGb(options.MaxSizeGb) * 1e9
                    ? "over the " + StreamRanking.EffectiveMaxSizeGb(options.MaxSizeGb).ToString("0.#", CultureInfo.InvariantCulture) + " GB cap" : "filtered");
                list.Add(Entry(StreamRanking.ToCandidate(analysis, anime), analysis.Reasons, reason, stream.AddonName, anime));
            }
            return list;
        }

        private static SourceEntry Entry(StreamCandidate c, IEnumerable<string> reasons, string reject, string addonName, bool anime) => new SourceEntry
        {
            Candidate = c, Reasons = reasons.ToList(), RejectReason = reject, AddonName = addonName, Facts = SourceFacts.Parse(c, anime)
        };
    }

    /// Fallback over any ISourceResolver (no rejected list).
    public sealed class RankedSourceCatalog : ISourceCatalog
    {
        private readonly ISourceResolver resolver;

        public RankedSourceCatalog(ISourceResolver resolver) => this.resolver = resolver;

        public async Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token)
        {
            var list = await resolver.GetRankedAsync(meta.Summary.Type, video?.Id ?? meta.Summary.Id, options ?? new SourceOptions(), token).ConfigureAwait(false);
            return list.Select(c => new SourceEntry { Candidate = c, Facts = SourceFacts.Parse(c, options?.IsAnime ?? false), AddonName = SourceFacts.FirstLine(c.Name) }).ToList();
        }
    }

    /// Display facts parsed from a candidate's text (name + title + filename).
    public sealed class SourceFacts
    {
        public string Quality { get; set; }            // 4K | 1080p | 720p | SD
        public string Hdr { get; set; }                // DV | HDR10+ | HDR10 | HDR | null
        public string VideoCodec { get; set; }         // H.264 | HEVC | AV1 | null
        public string AudioCodec { get; set; }         // AAC | DD+ | DD | DTS | FLAC | Opus | TrueHD | null
        public string Channels { get; set; }           // 2.0 | 5.1 | 7.1 | null
        public string ReleaseType { get; set; }        // WEB-DL | BluRay | WEBRip | HDTV | null
        public IReadOnlyList<string> AudioLanguages { get; set; } = Array.Empty<string>();      // EN, HE, JA, MULTI ...
        public IReadOnlyList<string> SubtitleLanguages { get; set; } = Array.Empty<string>();

        private static readonly Regex Channel = new Regex(@"(?<![0-9.])([257])[ .]([01])(?![0-9])(?!\s*[GM]B)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex Flag = new Regex("\uD83C([\uDDE6-\uDDFF])\uD83C([\uDDE6-\uDDFF])", RegexOptions.CultureInvariant);

        public static string FirstLine(string text) => (text ?? "").Replace("\r", "").Split('\n')[0].Trim();

        public static SourceFacts Parse(StreamCandidate c, bool anime)
        {
            var text = (c.Name ?? "") + " " + (c.Title ?? "") + " " + (c.Filename ?? "");
            var lower = text.ToLowerInvariant();
            var f = new SourceFacts();
            f.Quality = c.Quality == "4k" ? "4K" : c.Quality == "1080p" ? "1080p" : c.Quality == "720p" ? "720p"
                : Regex.IsMatch(lower, @"2160p|\b4k\b") ? "4K" : lower.Contains("1080p") ? "1080p" : lower.Contains("720p") ? "720p" : "SD";
            if (Regex.IsMatch(lower, @"\b(dolby[ ._-]?vision|dovi|dv)\b")) f.Hdr = "DV";
            else if (Regex.IsMatch(lower, @"hdr10\+|hdr10plus")) f.Hdr = "HDR10+";
            else if (Regex.IsMatch(lower, @"\bhdr10\b")) f.Hdr = "HDR10";
            else if (Regex.IsMatch(lower, @"\bhdr\b")) f.Hdr = "HDR";
            var vc = (c.VideoCodec ?? "").ToLowerInvariant();
            f.VideoCodec = vc == "h264" || Regex.IsMatch(lower, @"\bh[ .]?264|x264|\bavc\b") ? "H.264"
                : vc == "hevc" || Regex.IsMatch(lower, @"\bh[ .]?265|x265|\bhevc\b") ? "HEVC" : vc == "av1" ? "AV1" : null;
            var ac = (c.AudioCodec ?? "").ToLowerInvariant();
            f.AudioCodec = ac == "aac" ? "AAC" : ac == "eac3" ? "DD+" : ac == "ac3" ? "DD" : ac == "dts" ? "DTS" : ac == "flac" ? "FLAC"
                : ac == "opus" ? "Opus" : ac == "truehd" ? "TrueHD"
                : Regex.IsMatch(lower, @"\be-?ac-?3|\bddp|\bdd\+") ? "DD+" : Regex.IsMatch(lower, @"\baac") ? "AAC" : null;
            var ch = Channel.Match(text);
            if (ch.Success) f.Channels = ch.Groups[1].Value + "." + ch.Groups[2].Value;
            f.ReleaseType = Regex.IsMatch(lower, @"web[- .]?dl") ? "WEB-DL" : Regex.IsMatch(lower, @"blu[- .]?ray|bdrip|bdremux") ? "BluRay"
                : Regex.IsMatch(lower, @"web[- .]?rip") ? "WEBRip" : Regex.IsMatch(lower, @"\bhdtv\b") ? "HDTV" : null;

            var audio = new List<string>();
            foreach (Match m in Flag.Matches(text))
            {
                var code = FlagLanguage((char)('A' + (m.Groups[1].Value[0] - '\uDDE6')), (char)('A' + (m.Groups[2].Value[0] - '\uDDE6')));
                if (code != null && !audio.Contains(code)) audio.Add(code);
            }
            if (Regex.IsMatch(lower, @"\b(dual|multi)[ ._-]*(audio|dub)\b") && !audio.Contains("MULTI")) audio.Add("MULTI");
            if (Regex.IsMatch(lower, @"\b(jpn|japanese)\b") && !audio.Contains("JA")) audio.Add("JA");
            if (audio.Count == 0 && (anime ? false : c.LanguageTier >= 4)) audio.Add("EN");

            var subs = new List<string>();
            if (Languages.HasPreferredSubtitles && (Languages.SubtitlePattern(Languages.Subtitles).IsMatch(text) || !anime && (c.LanguageTier == 7 || c.LanguageTier == 3))) subs.Add(Languages.Short(Languages.Subtitles));
            if (Regex.IsMatch(lower, @"\besubs?\b|eng[ ._-]?subs?|english[ ._-]?sub") || !anime && (c.LanguageTier == 6 || c.LanguageTier == 2) || anime && c.LanguageTier >= 4) subs.Add("EN");
            if (Regex.IsMatch(lower, @"multi[ ._-]*subs?") || !anime && c.LanguageTier == 5) subs.Add("MULTI");
            f.AudioLanguages = audio;
            f.SubtitleLanguages = subs.Distinct().ToList();
            return f;
        }

        private static string FlagLanguage(char a, char b)
        {
            switch ("" + a + b)
            {
                case "GB": case "US": case "AU": case "CA": return "EN";
                case "JP": return "JA";
                case "IT": return "IT";
                case "ES": case "MX": return "ES";
                case "FR": return "FR";
                case "DE": return "DE";
                case "RU": return "RU";
                case "UA": return "UK";
                case "KR": return "KO";
                case "CN": case "TW": return "ZH";
                case "IN": return "HI";
                case "PT": case "BR": return "PT";
                case "PL": return "PL";
                case "TR": return "TR";
                case "NL": return "NL";
                default: return "" + a + b;
            }
        }
    }

    // ---- Trailers ----

    /// Plays a title's trailer in the hero area: IMDb's official trailer on the TV's hardware player
    /// (probe/Tizen/Trailer), crossfaded over the hero still. The browser preview may fall back to YouTube's embed.
    public interface ITrailerPlayer
    {
        void Show(string titleId, string youTubeId, int x, int y, int w, int h, bool muted);
        void SetMuted(bool muted);
        void Hide();
    }
}

