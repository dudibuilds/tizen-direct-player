using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Ru7100.Core.Ui
{
    public enum SearchFocus { Keyboard, Suggestions, Results }

    /// A chip under the query: a title suggestion (fills the query) or a collection (opens Browse with a filter).
    public sealed class SearchChip
    {
        public string Label { get; set; }
        public IDictionary<BrowseFilter, string> Preset { get; set; }

        public static SearchChip Collection(string label, MediaType type, BrowseFilter filter, string value) => new SearchChip
        {
            Label = label,
            Preset = new Dictionary<BrowseFilter, string> { [BrowseFilter.Type] = type == MediaType.Movie ? "Movies" : "Series", [filter] = value }
        };
    }

    /// Search as you type: keyboard on the left; chips and a poster grid on the right. Before anything is typed the
    /// grid shows what is popular now and the chips are the recent searches; with results the chips are refinements
    /// (collections that open Browse). Right from the keyboard goes straight to the posters; the chips are one Up
    /// from the first row. Back steps one level: results or chips, keyboard (query kept), Home.
    /// Each edit schedules a search 300 ms later and cancels the request still in flight.
    public sealed class SearchViewModel : Screen
    {
        public const int Columns = 3;   // 10-foot posters: three per row beside the keyboard
        public const int DebounceMs = 300;
        public const int MinQueryLength = 1;
        public const int ExtrasPrefetch = 12;

        private long searchDueMs = -1;
        private string lastQuery = "";
        private CancellationTokenSource inflight;
        private readonly Dictionary<string, MetaExtras> extras = new Dictionary<string, MetaExtras>();
        private readonly HashSet<string> extrasRequested = new HashSet<string>();

        public SearchViewModel() => Keyboard = new OnScreenKeyboard(false, "Search");

        public override ScreenKind Kind => ScreenKind.Search;
        public override string Title => "Search";
        public OnScreenKeyboard Keyboard { get; }
        public SearchFocus Focus { get; private set; }
        public int ResultIndex { get; private set; }
        public int SuggestionIndex { get; private set; }
        public IReadOnlyList<SearchItem> Items { get; private set; } = Array.Empty<SearchItem>();
        private IReadOnlyList<SearchItem> popular = Array.Empty<SearchItem>();
        /// Nothing typed yet: the grid holds the popular titles instead of results.
        public bool ShowingPopular => ShownQuery.Length == 0 && Query.Trim().Length < MinQueryLength && !Searching;
        public const int PopularCount = 12;
        private long focusChangedMs;
        private string focusKey;
        private readonly Dictionary<string, SourceEntry> signatures = new Dictionary<string, SourceEntry>();
        private readonly HashSet<string> signatureRequested = new HashSet<string>();

        /// Dwell on a result before its source check (signature line) starts.
        public const int SignatureDwellMs = 1500;

        /// Chip row. Nothing typed: the recent searches (OK runs one again). With results: only refinements, i.e.
        /// collections we can serve (genres seen in the results, subtitles in the preferred language, 4K, the top result's decade),
        /// which open Browse with that filter. Title suggestions are not repeated here: they are the posters below.
        public IReadOnlyList<SearchChip> Chips
        {
            get
            {
                if (ShowingPopular)
                    return Services.Memory.RecentSearches.Select(q => new SearchChip { Label = q }).ToList();
                var chips = new List<SearchChip>();
                if (Items.Count == 0)
                    return chips;
                var type = Items[0].Meta.Type;
                var typeLabel = type == MediaType.Movie ? "Movies" : "Series";
                var genres = Items.Take(ExtrasPrefetch).Select(i => ExtrasFor(i.Meta.Id)).Where(e => e != null)
                    .SelectMany(e => e.Genres).GroupBy(g => g).OrderByDescending(g => g.Count()).Select(g => g.Key)
                    .Where(g => Array.IndexOf(BrowseViewModel.Options[BrowseFilter.Genre], g) > 0).Take(2);
                foreach (var g in genres)
                    chips.Add(SearchChip.Collection(g + " " + typeLabel.ToLowerInvariant(), type, BrowseFilter.Genre, g));
                chips.Add(SearchChip.Collection("With " + BrowseViewModel.SubtitleOption, type, BrowseFilter.Watchable, BrowseViewModel.SubtitleOption));
                chips.Add(SearchChip.Collection("In 4K", type, BrowseFilter.Quality, "4K"));
                var year = Items[0].Meta.ReleaseInfo;
                if (!string.IsNullOrEmpty(year) && year.Length >= 4 && int.TryParse(year.Substring(0, 4), out var y) && y >= 2000)
                {
                    var decade = (y / 10 * 10) + "s";
                    chips.Add(SearchChip.Collection(typeLabel + " from the " + decade, type, BrowseFilter.Year, decade));
                }
                return chips;
            }
        }

        public IReadOnlyList<string> Suggestions => Chips.Select(c => c.Label).ToList();

        public SourceEntry SignatureFor(string id) => id != null && signatures.TryGetValue(id, out var e) ? e : null;
        public bool SignatureChecking(string id) => id != null && signatureRequested.Contains(id) && !signatures.ContainsKey(id);
        public IReadOnlyList<MetaSummary> Results => Items.Select(i => i.Meta).ToList();
        public bool Searching { get; private set; }
        public string Query => Keyboard.Text;
        public string ShownQuery { get; private set; } = "";

        public MetaExtras ExtrasFor(string id) => id != null && extras.TryGetValue(id, out var e) ? e : null;
        public SearchItem FocusedItem => Focus == SearchFocus.Results && ResultIndex < Items.Count ? Items[ResultIndex] : null;

        protected internal override void OnPushed()
        {
            var memory = Services.Memory;
            if (!string.IsNullOrEmpty(memory.SearchQuery))
            {
                // Reopened in the same session: the query, its results and the focused poster come back.
                Keyboard.Text = memory.SearchQuery;
                restoreIndex = memory.SearchOnResults ? memory.SearchIndex : -1;
                SearchNow();
            }
            if (Services.Catalog != null)
                Load(token => Services.Catalog.GetCatalogAsync("com.linvo.cinemeta", "top", MediaType.Series, 0, token), list =>
                {
                    popular = (list ?? Array.Empty<MetaSummary>()).Take(PopularCount).Select(m => new SearchItem { Meta = m }).ToList();
                    if (ShowingPopular)
                    {
                        Items = popular;
                        PrefetchExtras();
                    }
                }, _ => { });
        }

        private int restoreIndex = -1;

        public override bool HandleKey(KeyPress key)
        {
            if (key.Key == RemoteKey.Back)
            {
                // One level up: from the posters or chips to the keyboard (the query stays); from the keyboard out.
                if (Focus == SearchFocus.Keyboard)
                    return false;
                Focus = SearchFocus.Keyboard;
                return true;
            }
            if ((key.Key == RemoteKey.Play || key.Key == RemoteKey.PlayPause) && Focus == SearchFocus.Results && ResultIndex < Items.Count)
            {
                var play = Items[ResultIndex].Meta;
                Services.Memory.RememberSearch(ShownQuery);
                Shell.Push(new DetailsViewModel(play.Type, play.Id, play) { PlayWhenLoaded = true });
                return true;
            }
            switch (Focus)
            {
                case SearchFocus.Keyboard: return KeyboardKey(key);
                case SearchFocus.Suggestions: return SuggestionKey(key);
                default: return ResultKey(key);
            }
        }

        private bool KeyboardKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Up: Keyboard.Move(0, -1); return true;
                case RemoteKey.Down: Keyboard.Move(0, 1); return true;
                case RemoteKey.Left: Keyboard.Move(-1, 0); return true;
                case RemoteKey.Right:
                    if (!Keyboard.Move(1, 0))
                    {
                        // Straight to the nearest poster; the chips are one Up from there.
                        if (Items.Count > 0) Focus = SearchFocus.Results;
                        else if (Suggestions.Count > 0) Focus = SearchFocus.Suggestions;
                    }
                    return true;
                case RemoteKey.Select:
                    var result = Keyboard.Press();
                    if (result == KeyboardResult.TextChanged)
                        Schedule();
                    else if (result == KeyboardResult.Submit)
                        SearchNow();
                    return true;
            }
            return true;
        }

        private bool SuggestionKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Left:
                    if (SuggestionIndex == 0) Focus = SearchFocus.Keyboard;
                    else SuggestionIndex--;
                    return true;
                case RemoteKey.Right: SuggestionIndex = Math.Min(Suggestions.Count - 1, SuggestionIndex + 1); return true;
                case RemoteKey.Down:
                    if (Items.Count > 0) Focus = SearchFocus.Results;
                    return true;
                case RemoteKey.Select:
                    var chip = Chips[SuggestionIndex];
                    if (chip.Preset != null)
                    {
                        Shell.Push(new BrowseViewModel(chip.Preset));
                        return true;
                    }
                    Keyboard.Text = chip.Label;
                    SearchNow();
                    return true;
            }
            return true;
        }

        private bool ResultKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Left:
                    if (ResultIndex % Columns == 0) Focus = SearchFocus.Keyboard;
                    else ResultIndex--;
                    return true;
                case RemoteKey.Right:
                    if (ResultIndex % Columns < Columns - 1 && ResultIndex + 1 < Items.Count) ResultIndex++;
                    return true;
                case RemoteKey.Up:
                    if (ResultIndex >= Columns) ResultIndex -= Columns;
                    else if (Suggestions.Count > 0) Focus = SearchFocus.Suggestions;
                    return true;
                case RemoteKey.Down:
                    if (ResultIndex + Columns < Items.Count) ResultIndex += Columns;
                    else if (ResultIndex / Columns < (Items.Count - 1) / Columns) ResultIndex = Items.Count - 1;
                    return true;
                case RemoteKey.Select:
                    var item = Items[ResultIndex].Meta;
                    Services.Memory.RememberSearch(ShownQuery);
                    Shell.Push(new DetailsViewModel(item.Type, item.Id, item));
                    return true;
            }
            return true;
        }

        private void Schedule()
        {
            searchDueMs = NowMs + DebounceMs;
            if (Query.Trim().Length < MinQueryLength)
            {
                searchDueMs = -1;
                inflight?.Cancel();
                Searching = false;
                Items = popular;
                ShownQuery = "";
                lastQuery = "";
                ResultIndex = 0;
                SuggestionIndex = 0;
            }
        }

        protected internal override void Tick(long nowMs)
        {
            // Signature line for the focused card after a short dwell.
            var focused = FocusedItem;
            var key = focused?.Meta?.Id;
            if (key != focusKey)
            {
                focusKey = key;
                focusChangedMs = nowMs;
            }
            else if (key != null && nowMs - focusChangedMs >= SignatureDwellMs && Services.Signatures != null && signatureRequested.Add(key))
            {
                var meta = new MetaDetail { Summary = focused.Meta, Genres = ExtrasFor(key)?.Genres ?? Array.Empty<string>() };
                var video = focused.Meta.Type == MediaType.Series ? new VideoInfo { Id = key + ":1:1", Season = 1, Episode = 1 } : null;
                Load(token => Services.Signatures.GetAsync(meta, video, token), entry => signatures[key] = entry, _ => signatures[key] = null);
                Changed();
            }
            if (searchDueMs >= 0 && nowMs >= searchDueMs)
            {
                searchDueMs = -1;
                SearchNow();
            }
        }

        protected override void OnClosed()
        {
            inflight?.Cancel();
            var memory = Services.Memory;
            memory.SearchQuery = Query.Trim();
            memory.SearchIndex = ResultIndex;
            memory.SearchOnResults = Focus == SearchFocus.Results;
        }

        public void SearchNow()
        {
            searchDueMs = -1;
            var query = Query.Trim();
            if (query.Length < MinQueryLength || query == lastQuery)
                return;
            lastQuery = query;
            inflight?.Cancel();
            // Extras still on their way for the old results are dropped with it; let them be asked again if needed.
            extrasRequested.RemoveWhere(id => !extras.ContainsKey(id));
            var cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
            inflight = cts;
            Searching = true;
            var service = Services.Search;
            var finalApplied = false;
            Func<CancellationToken, System.Threading.Tasks.Task<SearchResults>> fetch = token => service.SearchAsync(query, token);
            if (service is IProgressiveSearchService progressive)
            {
                // The fast catalogs' results show while the slow ones are still answering (it used to wait for all).
                var dispatcher = Services.Dispatcher;
                Action<SearchResults> partial = results => dispatcher.Post(() =>
                {
                    if (finalApplied || cts.IsCancellationRequested || !IsAlive || query != Query.Trim())
                        return;
                    ShowResults(query, results, final: false);
                    Changed();
                });
                fetch = token => progressive.SearchAsync(query, partial, token);
            }
            Load(fetch, results =>
            {
                if (cts.IsCancellationRequested || query != Query.Trim())
                    return;   // superseded by newer typing
                finalApplied = true;
                Searching = false;
                Error = null;
                ShowResults(query, results, final: true);
            }, error =>
            {
                if (cts.IsCancellationRequested) return;
                finalApplied = true;
                Searching = false;
                Error = error.Message;
            }, cts.Token);   // superseded searches end silently
        }

        /// Shows a batch of results. A partial batch (more catalogs still answering) keeps the focused poster on the
        /// same title when it is still there; a reopened search returns to its saved poster only with the final list.
        private void ShowResults(string query, SearchResults results, bool final)
        {
            var focusedId = Focus == SearchFocus.Results ? FocusedItem?.Meta?.Id : null;
            ShownQuery = query;
            Items = results?.Items ?? Array.Empty<SearchItem>();
            if (focusedId != null)
            {
                var at = IndexOfId(Items, focusedId);
                if (at >= 0) ResultIndex = at;
            }
            ResultIndex = Math.Min(ResultIndex, Math.Max(0, Items.Count - 1));
            if (final)
            {
                if (restoreIndex >= 0 && Items.Count > 0)
                {
                    ResultIndex = Math.Min(restoreIndex, Items.Count - 1);
                    Focus = SearchFocus.Results;
                }
                restoreIndex = -1;
            }
            SuggestionIndex = Math.Min(SuggestionIndex, Math.Max(0, Suggestions.Count - 1));
            if (Items.Count == 0 && Focus == SearchFocus.Results && final) Focus = SearchFocus.Keyboard;
            if (Suggestions.Count == 0 && Focus == SearchFocus.Suggestions && final) Focus = Items.Count > 0 ? SearchFocus.Results : SearchFocus.Keyboard;
            PrefetchExtras(inflight?.Token ?? default);
        }

        private static int IndexOfId(IReadOnlyList<SearchItem> items, string id)
        {
            for (var i = 0; i < items.Count; i++)
                if (items[i].Meta?.Id == id)
                    return i;
            return -1;
        }

        /// Rating, genres and a short description for the first cards (one cached request per title). The batch is
        /// applied in one go (one repaint instead of one per title) and is dropped when a newer search replaces it.
        private void PrefetchExtras(CancellationToken cancel = default)
        {
            var source = Services.Extras;
            if (source == null)
                return;
            var wanted = Items.Take(ExtrasPrefetch).Select(i => i.Meta).Where(m => m?.Id != null && extrasRequested.Add(m.Id)).ToList();
            // One batch per poster row: a row's cards fill in together, and one slow title holds back only its own row.
            for (var start = 0; start < wanted.Count; start += Columns)
            {
                var row = wanted.Skip(start).Take(Columns).ToList();
                Load(token => System.Threading.Tasks.Task.WhenAll(row.Select(m => ExtrasOrNull(source, m, token))), batch =>
                {
                    for (var i = 0; i < row.Count; i++)
                        if (batch[i] != null)
                            extras[row[i].Id] = batch[i];
                }, _ => { }, cancel);
            }
        }

        private static async System.Threading.Tasks.Task<MetaExtras> ExtrasOrNull(IMetaExtras source, MetaSummary meta, CancellationToken token)
        {
            try { return await source.GetAsync(meta.Type, meta.Id, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested) { return null; }   // a title without extras keeps its plain card
        }
    }
}
