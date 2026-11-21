using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Ui
{
    public sealed class BrowseItem
    {
        public MetaSummary Meta { get; set; }
        public MetaExtras Extras { get; set; }
        public DateTimeOffset? Released { get; set; }
    }

    /// Catalog pages for Browse, optionally filtered by genre on the server.
    public interface IBrowseSource
    {
        Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token);
    }

    /// Cinemeta's "top" catalog with the genre extra; its metas carry rating, runtime, genres and description.
    public sealed class CinemetaBrowse : IBrowseSource
    {
        private readonly HttpClient http;

        public CinemetaBrowse(HttpMessageHandler handler = null)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
        {
            var extra = new List<string>();
            if (!string.IsNullOrEmpty(genre)) extra.Add("genre=" + Uri.EscapeDataString(genre));
            if (skip > 0) extra.Add("skip=" + skip.ToString(CultureInfo.InvariantCulture));
            var url = Ru7100.Core.Addons.StremioCatalog.CinemetaBaseUrl + "/catalog/" + (type == MediaType.Series ? "series" : "movie") + "/top"
                + (extra.Count > 0 ? "/" + string.Join("&", extra) : "") + ".json";
            using (var response = await http.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return (json["metas"] as JArray ?? new JArray()).OfType<JObject>().Select(Parse).Where(i => i.Meta.Id != null).ToList();
            }
        }

        public static BrowseItem Parse(JObject meta) => new BrowseItem
        {
            Meta = CinemetaJson.Summary(meta),
            Extras = CinemetaJson.ParseExtras(meta),
            Released = DateTimeOffset.TryParse((string)meta["released"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r) ? r : (DateTimeOffset?)null
        };
    }

    /// One streaming service's catalogue as Browse pages (docs/streaming-services.md). The genre filter runs on the
    /// server; the catalogue already carries rating, runtime, genres and synopsis, so no per-title lookups are needed.
    public sealed class ServiceBrowseSource : IBrowseSource
    {
        private readonly Ru7100.Core.Addons.IServiceCatalog catalog;
        private readonly Ru7100.Core.Addons.StreamingService service;

        public ServiceBrowseSource(Ru7100.Core.Addons.IServiceCatalog catalog, Ru7100.Core.Addons.StreamingService service)
        {
            this.catalog = catalog;
            this.service = service;
        }

        public async Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
        {
            var page = await catalog.GetTitlesAsync(service, type, genre, skip, BrowseViewModel.PageSize, token).ConfigureAwait(false);
            return page.Titles.Select(ToItem).ToList();
        }

        public static BrowseItem ToItem(Ru7100.Core.Addons.ServiceTitle t) => new BrowseItem
        {
            Meta = t.Meta,
            Extras = new MetaExtras { Rating = t.Rating, Runtime = t.Runtime, Description = t.Description, Genres = t.Genres ?? Array.Empty<string>() }
        };
    }

    /// Fallback over ICatalog (+ IMetaExtras for facts); the genre filter runs on the client.
    public sealed class CatalogBrowse : IBrowseSource
    {
        private readonly ICatalog catalog;
        private readonly IMetaExtras extras;

        public CatalogBrowse(ICatalog catalog, IMetaExtras extras)
        {
            this.catalog = catalog;
            this.extras = extras;
        }

        public async Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
        {
            var list = await catalog.GetCatalogAsync("com.linvo.cinemeta", "top", type, skip, token).ConfigureAwait(false);
            var items = await Task.WhenAll(list.Select(async m =>
            {
                MetaExtras e = null;
                if (extras != null)
                {
                    try { e = await extras.GetAsync(m.Type, m.Id, token).ConfigureAwait(false); }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                }
                return new BrowseItem { Meta = m, Extras = e ?? new MetaExtras() };
            })).ConfigureAwait(false);
            return items.Where(i => string.IsNullOrEmpty(genre) || i.Extras.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    public enum BrowseFilter { Type, Service, Genre, Year, Rating, Runtime, Watchable, Quality, Status }

    /// Browse with filters. OK on a filter opens a dropdown of its options (Up/Down choose, OK applies, Back
    /// cancels); a "Clear filters" pill follows the filters while any is set. Back steps one level: grid, filter
    /// bar, Home. Filters and position come back when Browse is reopened in the same session.
    /// Type and genre query the catalog; year, rating, runtime and status filter locally;
    /// "Watchable with" and quality use the source-signature check (the first matches are checked in the background,
    /// unknown ones stay visible with a "checking" mark).
    public sealed class BrowseViewModel : Screen
    {
        public const int Columns = 5;   // 10-foot posters: five per row
        /// Catalog page size (Cinemeta's `top` catalog answers 50 per `skip`, and pages run thousands deep).
        public const int PageSize = 50;
        /// Pages fetched on open; more follow on demand as focus nears the end of the grid (no fixed cap).
        public const int Pages = 2;
        /// Focus this many rows from the end fetches the next page.
        public const int PrefetchRows = 3;
        /// With local filters on, one trigger fetches at most this many pages looking for matches, so a narrow
        /// filter cannot page through the whole catalog at once.
        public const int MaxPagesPerFill = 6;
        /// A filter change queries the catalog only once the choice settles (several quick OK presses → one search).
        public const int QueryDebounceMs = 350;
        private long queryDueMs = -1;
        public const int SignatureChecks = 12;

        /// The Watchable filter's subtitle option, in the viewer's preferred subtitle language ("Spanish subtitles").
        public static string SubtitleOption => Languages.Name(Languages.Subtitles) + " subtitles";

        public static readonly IReadOnlyDictionary<BrowseFilter, string[]> Options = new Dictionary<BrowseFilter, string[]>
        {
            [BrowseFilter.Service] = new[] { "Any service" }.Concat(Ru7100.Core.Addons.StreamingServices.All.Select(s => s.Name)).ToArray(),
            [BrowseFilter.Type] = new[] { "Series", "Movies" },
            [BrowseFilter.Genre] = new[] { "Any genre", "Action", "Adventure", "Animation", "Comedy", "Crime", "Documentary", "Drama", "Family", "Fantasy", "Horror", "Mystery", "Romance", "Sci-Fi", "Thriller" },
            [BrowseFilter.Year] = new[] { "Any year", "2020s", "2010s", "2000s", "Before 2000" },
            [BrowseFilter.Rating] = new[] { "Any rating", "7+", "8+" },
            [BrowseFilter.Runtime] = new[] { "Any length", "Short (< 30 min)", "Under 2 h" },
            [BrowseFilter.Watchable] = new[] { "Any language", SubtitleOption, "English audio" },
            [BrowseFilter.Quality] = new[] { "Any quality", "4K", "HDR" },
            [BrowseFilter.Status] = new[] { "Any status", "New", "Not started", "In progress", "Finished" }
        };

        private readonly Dictionary<BrowseFilter, int> selected = Enum.GetValues(typeof(BrowseFilter)).Cast<BrowseFilter>().ToDictionary(f => f, f => 0);
        private IReadOnlyList<BrowseItem> pool = Array.Empty<BrowseItem>();
        private IReadOnlyList<LibraryEntry> library = Array.Empty<LibraryEntry>();
        private Dictionary<string, LibraryEntry> libraryIndex;

        /// The title's library entry (the first one not removed). Indexed once per library load: filtering used to scan
        /// the whole library for every title in the pool, on every refilter.
        private LibraryEntry LibraryEntryFor(string id)
        {
            if (libraryIndex == null)
            {
                libraryIndex = new Dictionary<string, LibraryEntry>(StringComparer.Ordinal);
                foreach (var l in library)
                    if (l?.Id != null && !l.Removed && !libraryIndex.ContainsKey(l.Id))
                        libraryIndex[l.Id] = l;
            }
            return id != null && libraryIndex.TryGetValue(id, out var entry) ? entry : null;
        }

        private readonly Dictionary<string, SourceEntry> signatures = new Dictionary<string, SourceEntry>();
        private readonly HashSet<string> checking = new HashSet<string>();
        private string loadedKey;
        private int nextSkip;
        /// Cancels the catalog requests of a superseded query (a newer type/genre) without any error.
        private CancellationTokenSource queryCancel = new CancellationTokenSource();
        private int fillPages;
        private IReadOnlyList<BrowseItem> results;

        private readonly bool restore;
        private int restoreIndex = -1;
        private long focusChangedMs;
        private string focusKey;

        /// Dwell on a title before its source check (the availability line of the info panel) starts.
        public const int SignatureDwellMs = 1500;

        public BrowseViewModel(IDictionary<BrowseFilter, string> preset = null)
        {
            restore = preset == null;
            if (preset != null)
                foreach (var p in preset)
                {
                    var index = Array.IndexOf(Options[p.Key], p.Value);
                    if (index >= 0) selected[p.Key] = index;
                }
        }

        public override ScreenKind Kind => ScreenKind.Browse;
        public override string Title => "Browse";
        /// The chosen streaming service (Service filter), or null for every catalogue.
        public Ru7100.Core.Addons.StreamingService Service => selected[BrowseFilter.Service] == 0 ? null : Ru7100.Core.Addons.StreamingServices.Find(Value(BrowseFilter.Service));
        /// Screen heading: the service's name inside a service, otherwise "Browse".
        public string Heading => Service?.Name ?? Title;

        /// The catalog behind the grid: the chosen service's catalogue when there is one, otherwise the default source.
        private IBrowseSource Source()
        {
            var service = Service;
            return service != null && Services.ServiceCatalog != null ? new ServiceBrowseSource(Services.ServiceCatalog, service) : Services.Browse;
        }
        public static IReadOnlyList<BrowseFilter> Filters { get; } = (BrowseFilter[])Enum.GetValues(typeof(BrowseFilter));
        /// -1 = filter row, otherwise index into Results.
        public int Index { get; private set; } = -1;
        public int FilterIndex { get; private set; }
        public bool Loading { get; private set; }
        /// A further catalog page is being fetched.
        public bool LoadingMore { get; private set; }
        /// The catalog has no further pages (a page added no new titles).
        public bool Exhausted { get; private set; }
        public string Value(BrowseFilter filter) => Options[filter][selected[filter]];
        public bool IsSet(BrowseFilter filter) => selected[filter] != 0 || filter == BrowseFilter.Type;
        public bool IsChecking(string id) => checking.Contains(id) && !signatures.ContainsKey(id);
        /// The dropdown of the focused filter is open; DropdownIndex is the highlighted option.
        public bool DropdownOpen { get; private set; }
        public int DropdownIndex { get; private set; }
        public int Selected(BrowseFilter filter) => selected[filter];
        /// Any filter besides the type is set: the bar ends in a "Clear filters" pill (index Filters.Count).
        public bool AnyFilterSet => Filters.Any(f => f != BrowseFilter.Type && selected[f] != 0);
        public bool ClearFocused => Index < 0 && FilterIndex >= Filters.Count;
        public BrowseItem FocusedItem => Index >= 0 && Index < Results.Count ? Results[Index] : null;
        public SourceEntry SignatureFor(string id) => signatures.TryGetValue(id, out var e) ? e : null;
        public MediaType Type => selected[BrowseFilter.Type] == 0 ? MediaType.Series : MediaType.Movie;

        /// Pool filtered by the local filters; cached until the pool, a filter or a source check changes.
        public IReadOnlyList<BrowseItem> Results => results ?? (results = pool.Where(Matches).ToList());

        private void Invalidate() => results = null;

        protected internal override void OnPushed()
        {
            var memory = Services.Memory;
            if (restore && memory.BrowseFilters != null)
            {
                foreach (var pair in memory.BrowseFilters)
                    // A service is entered from its page, never restored: Browse from the menu covers every catalogue.
                    if (pair.Key != BrowseFilter.Service && pair.Value >= 0 && pair.Value < Options[pair.Key].Length)
                        selected[pair.Key] = pair.Value;
                restoreIndex = memory.BrowseIndex;
            }
            if (Services.Library != null && (Services.Account == null || Services.Account.IsSignedIn))
                Load(token => Services.Library.GetAllAsync(token), list => { library = list ?? Array.Empty<LibraryEntry>(); libraryIndex = null; Invalidate(); }, _ => { });
            Query();
        }

        private void Query()
        {
            var genre = selected[BrowseFilter.Genre] == 0 ? null : Value(BrowseFilter.Genre);
            var key = Service?.Id + "/" + Type + "/" + genre;
            if (key == loadedKey)
            {
                CheckSignatures();
                return;
            }
            loadedKey = key;
            queryCancel.Cancel();
            queryCancel = new CancellationTokenSource();
            Loading = true;
            Error = null;
            LoadingMore = false;
            Exhausted = false;
            nextSkip = Pages * PageSize;
            fillPages = 0;
            var source = Source();
            var type = Type;
            Load(async token =>
            {
                var pages = await Task.WhenAll(Enumerable.Range(0, Pages).Select(p => source.GetAsync(type, genre, p * PageSize, token))).ConfigureAwait(false);
                return pages.SelectMany(p => p).GroupBy(i => i.Meta.Id).Select(g => g.First()).ToList();
            }, items =>
            {
                if (loadedKey != key) return;
                Loading = false;
                Error = null;
                pool = items;
                Exhausted = items.Count == 0;
                Invalidate();
                Index = Math.Min(Index, Results.Count - 1);
                if (restoreIndex >= 0)
                {
                    Index = Math.Min(restoreIndex, Results.Count - 1);   // back where the viewer left the grid
                    restoreIndex = -1;
                }
                CheckSignatures();
                MaybeLoadMore();
            }, error =>
            {
                if (loadedKey != key) return;
                Loading = false;
                Error = "Could not load the catalog: " + error.Message;
            }, queryCancel.Token);
        }

        /// Fetches the next catalog page when focus is within PrefetchRows of the end of the results, or when the
        /// local filters leave fewer than a screenful (bounded by MaxPagesPerFill per trigger).
        private void MaybeLoadMore()
        {
            if (Loading || LoadingMore || Exhausted || loadedKey == null)
                return;
            var count = Results.Count;
            var nearEnd = (Math.Max(Index, 0) / Columns) + PrefetchRows >= (count + Columns - 1) / Columns;
            if (!nearEnd)
            {
                fillPages = 0;
                return;
            }
            // Source-based filters: check titles already in the pool before fetching more (lookups are the slow part).
            if (NeedsSources && Services.Signatures != null)
            {
                if (checking.Any(id => !signatures.ContainsKey(id)))
                    return;   // wait for the lookups in flight
                if (pool.Any(i => MatchesLocal(i) && !checking.Contains(i.Meta.Id)))
                {
                    CheckSignatures();
                    return;
                }
            }
            if (fillPages >= MaxPagesPerFill)
                return;
            fillPages++;
            LoadingMore = true;
            var key = loadedKey;
            var genre = selected[BrowseFilter.Genre] == 0 ? null : Value(BrowseFilter.Genre);
            var type = Type;
            var skip = nextSkip;
            var source = Source();
            Load(token => source.GetAsync(type, genre, skip, token), page =>
            {
                if (loadedKey != key) return;
                LoadingMore = false;
                var known = new HashSet<string>(pool.Select(i => i.Meta.Id));
                var added = (page ?? Array.Empty<BrowseItem>()).Where(i => known.Add(i.Meta.Id)).ToList();
                if (added.Count == 0)
                {
                    Exhausted = true;   // an empty page, or a source that ignores skip
                    return;
                }
                nextSkip = skip + PageSize;
                pool = pool.Concat(added).ToList();
                Invalidate();
                CheckSignatures();
                MaybeLoadMore();
            }, error =>
            {
                if (loadedKey != key) return;
                LoadingMore = false;   // the next Down tries again
            }, queryCancel.Token);
        }

        private bool NeedsSources => selected[BrowseFilter.Watchable] != 0 || selected[BrowseFilter.Quality] != 0;

        /// Source checks for the first candidates when a source-based filter is on.
        private void CheckSignatures()
        {
            var service = Services.Signatures;
            if (!NeedsSources || service == null)
                return;
            // At most SignatureChecks lookups in flight; more start only as the grid needs them (MaybeLoadMore).
            var inFlight = checking.Count(id => !signatures.ContainsKey(id));
            foreach (var item in pool.Where(i => MatchesLocal(i) && !checking.Contains(i.Meta.Id)).Take(Math.Max(0, SignatureChecks - inFlight)))
            {
                var id = item.Meta.Id;
                if (!checking.Add(id)) continue;
                var meta = new MetaDetail { Summary = item.Meta, Genres = item.Extras?.Genres ?? Array.Empty<string>() };
                var video = item.Meta.Type == MediaType.Series ? new VideoInfo { Id = id + ":1:1", Season = 1, Episode = 1 } : null;
                Load(token => service.GetAsync(meta, video, token), entry => { signatures[id] = entry; Invalidate(); MaybeLoadMore(); }, _ => { signatures[id] = null; Invalidate(); MaybeLoadMore(); });
                Invalidate();
            }
        }

        private bool Matches(BrowseItem item)
        {
            if (!MatchesLocal(item))
                return false;
            if (!NeedsSources)
                return true;
            if (!signatures.TryGetValue(item.Meta.Id, out var entry))
                return checking.Contains(item.Meta.Id);   // unknown yet: keep while it is being checked
            if (entry == null)
                return false;
            var facts = entry.Facts ?? SourceFacts.Parse(entry.Candidate, false);
            var watchable = Value(BrowseFilter.Watchable);
            if (watchable == SubtitleOption && !facts.SubtitleLanguages.Contains(Languages.Short(Languages.Subtitles))) return false;
            if (watchable == "English audio" && !facts.AudioLanguages.Contains("EN")) return false;
            switch (Value(BrowseFilter.Quality))
            {
                case "4K": if (facts.Quality != "4K") return false; break;
                case "HDR": if (facts.Hdr == null) return false; break;
            }
            return true;
        }

        private bool MatchesLocal(BrowseItem item)
        {
            var e = item.Extras ?? new MetaExtras();
            var year = YearOf(item.Meta.ReleaseInfo);
            switch (Value(BrowseFilter.Year))
            {
                case "2020s": if (year < 2020) return false; break;
                case "2010s": if (year < 2010 || year >= 2020) return false; break;
                case "2000s": if (year < 2000 || year >= 2010) return false; break;
                case "Before 2000": if (year == 0 || year >= 2000) return false; break;
            }
            var rating = double.TryParse(e.Rating, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
            switch (Value(BrowseFilter.Rating))
            {
                case "7+": if (rating < 7) return false; break;
                case "8+": if (rating < 8) return false; break;
            }
            var minutes = UiFormat.RuntimeMinutes(e.Runtime) ?? 0;
            switch (Value(BrowseFilter.Runtime))
            {
                case "Short (< 30 min)": if (minutes == 0 || minutes >= 30) return false; break;
                case "Under 2 h": if (minutes == 0 || minutes >= 120) return false; break;
            }
            var entry = LibraryEntryFor(item.Meta.Id);
            var started = entry?.State != null && (entry.State.TimeOffsetMs > 0 || entry.State.TimesWatched > 0 || !string.IsNullOrEmpty(entry.State.Watched));
            var finished = entry?.State != null && (entry.State.FlaggedWatched > 0 || entry.State.TimesWatched > 0 && entry.State.TimeOffsetMs == 0);
            switch (Value(BrowseFilter.Status))
            {
                case "New":
                    var released = item.Released;
                    if (released == null ? year < Services.Clock.UtcNow.Year : released < Services.Clock.UtcNow.AddDays(-30)) return false;
                    break;
                case "Not started": if (started) return false; break;
                case "In progress": if (!started || finished) return false; break;
                case "Finished": if (!finished) return false; break;
            }
            return true;
        }

        private static int YearOf(string releaseInfo)
        {
            if (string.IsNullOrEmpty(releaseInfo) || releaseInfo.Length < 4) return 0;
            return int.TryParse(releaseInfo.Substring(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : 0;
        }

        protected override void OnClosed()
        {
            if (!restore)
                return;   // a preset collection (from a search chip) is not the viewer's own browsing state
            Services.Memory.BrowseFilters = new Dictionary<BrowseFilter, int>(selected);
            Services.Memory.BrowseIndex = Index;
        }

        private void Apply(BrowseFilter filter, int option, long nowMs)
        {
            if (selected[filter] == option)
                return;
            selected[filter] = option;
            Invalidate();
            fillPages = 0;
            queryDueMs = nowMs + QueryDebounceMs;
        }

        private bool DropdownKey(KeyPress key)
        {
            var filter = Filters[FilterIndex];
            var count = Options[filter].Length;
            switch (key.Key)
            {
                case RemoteKey.Up: DropdownIndex = Math.Max(0, DropdownIndex - 1); break;
                case RemoteKey.Down: DropdownIndex = Math.Min(count - 1, DropdownIndex + 1); break;
                case RemoteKey.Select:
                    DropdownOpen = false;
                    Apply(filter, DropdownIndex, key.AtMs);
                    break;
                case RemoteKey.Back:
                case RemoteKey.Left:
                case RemoteKey.Right:
                    DropdownOpen = false;   // cancel: nothing changes
                    break;
            }
            return true;
        }

        protected internal override void Tick(long nowMs)
        {
            // Availability for the focused title after a short dwell (the info panel's last line).
            var id = FocusedItem?.Meta?.Id;
            if (id != focusKey)
            {
                focusKey = id;
                focusChangedMs = nowMs;
            }
            else if (id != null && nowMs - focusChangedMs >= SignatureDwellMs && Services.Signatures != null && checking.Add(id))
            {
                var item = FocusedItem;
                var meta = new MetaDetail { Summary = item.Meta, Genres = item.Extras?.Genres ?? Array.Empty<string>() };
                var video = item.Meta.Type == MediaType.Series ? new VideoInfo { Id = id + ":1:1", Season = 1, Episode = 1 } : null;
                Load(token => Services.Signatures.GetAsync(meta, video, token), entry => { signatures[id] = entry; if (NeedsSources) Invalidate(); }, _ => { signatures[id] = null; if (NeedsSources) Invalidate(); });
                Changed();
            }
            if (queryDueMs >= 0 && nowMs >= queryDueMs)
            {
                queryDueMs = -1;
                Query();
                MaybeLoadMore();
                Changed();
            }
        }

        public override bool HandleKey(KeyPress key)
        {
            if (DropdownOpen)
                return DropdownKey(key);
            var results = Results;
            if (Index >= results.Count)
                Index = results.Count - 1;
            if (key.Key == RemoteKey.Back)
            {
                // One level up: from the grid to the filter bar; from the bar out of Browse.
                if (Index < 0)
                    return false;
                Index = -1;
                return true;
            }
            if (key.Key == RemoteKey.Play || key.Key == RemoteKey.PlayPause)
            {
                // The direct play key, as on Home.
                if (Index >= 0)
                {
                    var play = results[Index].Meta;
                    Shell.Push(new DetailsViewModel(play.Type, play.Id, play) { PlayWhenLoaded = true });
                }
                return true;
            }
            var pills = Filters.Count + (AnyFilterSet ? 1 : 0);
            FilterIndex = Math.Min(FilterIndex, pills - 1);
            if (Index < 0)
            {
                switch (key.Key)
                {
                    case RemoteKey.Left: FilterIndex = Math.Max(0, FilterIndex - 1); return true;
                    case RemoteKey.Right: FilterIndex = Math.Min(pills - 1, FilterIndex + 1); return true;
                    case RemoteKey.Down:
                        if (results.Count > 0) Index = 0;
                        fillPages = 0;
                        MaybeLoadMore();
                        return true;
                    case RemoteKey.Select:
                        // Only OK touches a filter; landing on one (or Up on the bar) never does.
                        if (FilterIndex >= Filters.Count)
                        {
                            foreach (var f in Filters.Where(f => f != BrowseFilter.Type).ToList())
                                selected[f] = 0;
                            FilterIndex = 0;
                            Invalidate();
                            fillPages = 0;
                            queryDueMs = key.AtMs + QueryDebounceMs;
                            return true;
                        }
                        DropdownOpen = true;
                        DropdownIndex = selected[Filters[FilterIndex]];
                        return true;
                    case RemoteKey.Up:
                        return true;
                }
                return true;
            }
            switch (key.Key)
            {
                case RemoteKey.Left: if (Index % Columns > 0) Index--; return true;
                case RemoteKey.Right: if (Index % Columns < Columns - 1 && Index + 1 < results.Count) Index++; return true;
                case RemoteKey.Up:
                    // A held Up stops at the top row of the grid: crossing into the filter bar takes a fresh press
                    // (or a pause longer than TvShell.RepeatWindowMs), so auto-repeat never overshoots onto a filter.
                    if (Index >= Columns) Index -= Columns;
                    else if (!key.IsRepeat) Index = -1;
                    return true;
                case RemoteKey.Down:
                    // The last row: Down lands on the last title of the next row, when that row is shorter.
                    if (Index + Columns < results.Count) Index += Columns;
                    else if (Index / Columns < (results.Count - 1) / Columns) Index = results.Count - 1;
                    fillPages = 0;   // each Down may fetch another bounded run of pages
                    MaybeLoadMore();
                    return true;
                case RemoteKey.Select:
                    var meta = results[Index].Meta;
                    Shell.Push(new DetailsViewModel(meta.Type, meta.Id, meta));
                    return true;
                case RemoteKey.Menu:
                case RemoteKey.Yellow:
                    var m = results[Index].Meta;
                    if (Services.Feedback != null)
                        RateDialog.ShowCardActions(Shell, Services.Feedback, m, () => Shell.Push(new DetailsViewModel(m.Type, m.Id, m)));
                    return true;
            }
            return true;
        }
    }
}
