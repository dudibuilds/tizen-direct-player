using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Ru7100.Core.Ui;
using Xunit;
using AddonFixtures = Ru7100.Core.Tests.Addons.Fixtures;
using AddonStore = Ru7100.Core.Tests.Addons.MemoryStore;
using FixtureHandler = Ru7100.Core.Tests.Addons.FixtureHandler;

namespace Ru7100.Tests.Ui
{
    /// Behaviour behind the 2026-11-20 performance audit fixes: search shows early results, superseded work is dropped,
    /// shared fetches are shared, and the player redraws for what it shows.
    public class PerfAuditTests
    {
        // ---- search: early results, final result unchanged ----

        private const string MovieSearch = "https://v3-cinemeta.strem.io/catalog/movie/top/search=dark.json";
        private const string SeriesSearch = "https://v3-cinemeta.strem.io/catalog/series/top/search=dark.json";

        private static string Metas(params (string Id, string Name)[] items) =>
            "{\"metas\":[" + string.Join(",", items.Select(i => "{\"id\":\"" + i.Id + "\",\"type\":\"movie\",\"name\":\"" + i.Name + "\"}")) + "]}";

        private static HttpResponseMessage Ok(string body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [Fact]
        public async Task SearchReportsTheFastCatalogBeforeTheSlowOneAnswers()
        {
            var slow = new TaskCompletionSource<string>();
            var handler = new FixtureHandler()
                .Json(MovieSearch, Metas(("tt1", "Dark Waters")))
                .Route(SeriesSearch, async (request, token) => Ok(await slow.Task));
            var registry = new AddonRegistry(new AddonStore(), AddonFixtures.Http(handler), null, () => DateTimeOffset.UtcNow);
            var search = new AddonSearchService(registry, null, handler);
            var partials = new List<SearchResults>();
            var firstPartial = new TaskCompletionSource<bool>();

            var running = search.SearchAsync("dark", results => { lock (partials) partials.Add(results); firstPartial.TrySetResult(true); }, CancellationToken.None);
            await firstPartial.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);   // the series catalog is still answering
            Assert.Equal(new[] { "tt1" }, partials[0].Items.Select(i => i.Meta.Id));

            slow.SetResult(Metas(("tt2", "Dark")));
            var final = await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "tt2", "tt1" }, final.Items.Select(i => i.Meta.Id));   // exact title first, as before

            // The progressive path ends with the same list a plain search gives.
            var plain = await new AddonSearchService(registry, null, handler).SearchAsync("dark", CancellationToken.None);
            Assert.Equal(plain.Items.Select(i => i.Meta.Id), final.Items.Select(i => i.Meta.Id));
        }

        /// A progressive search the test drives by hand.
        private sealed class ScriptedSearch : IProgressiveSearchService
        {
            public readonly List<(string Query, Action<SearchResults> Partial, TaskCompletionSource<SearchResults> Done, CancellationToken Token)> Calls =
                new List<(string, Action<SearchResults>, TaskCompletionSource<SearchResults>, CancellationToken)>();

            public Task<SearchResults> SearchAsync(string query, CancellationToken token) => SearchAsync(query, null, token);

            public Task<SearchResults> SearchAsync(string query, Action<SearchResults> partial, CancellationToken token)
            {
                var done = new TaskCompletionSource<SearchResults>();
                token.Register(() => done.TrySetCanceled(token));
                Calls.Add((query, partial, done, token));
                return done.Task;
            }
        }

        private static SearchResults Results(string query, params string[] ids) => new SearchResults
        {
            Query = query,
            Items = ids.Select(id => new SearchItem { Meta = new MetaSummary { Id = id, Name = id, Type = MediaType.Movie } }).ToList()
        };

        private static (UiHarness H, SearchViewModel Vm, ScriptedSearch Search) OpenSearch()
        {
            var h = new UiHarness();
            var search = new ScriptedSearch();
            h.Shell.Services.Search = search;
            h.Shell.Start(new HomeViewModel());
            var vm = new SearchViewModel();
            h.Shell.Push(vm);
            return (h, vm, search);
        }

        private static void Type(UiHarness h, SearchViewModel vm, string text)
        {
            vm.Keyboard.Text = text;
            vm.SearchNow();
        }

        [Fact]
        public void SearchScreenShowsEarlyResultsWhileTheRestAreStillComing()
        {
            var (h, vm, search) = OpenSearch();
            Type(h, vm, "dark");
            var call = search.Calls.Single(c => c.Query == "dark");
            call.Partial(Results("dark", "tt1"));
            Assert.True(vm.Searching);
            Assert.Equal(new[] { "tt1" }, vm.Items.Select(i => i.Meta.Id));
            Assert.Equal("dark", vm.ShownQuery);

            call.Done.SetResult(Results("dark", "tt2", "tt1"));
            Assert.False(vm.Searching);
            Assert.Equal(new[] { "tt2", "tt1" }, vm.Items.Select(i => i.Meta.Id));
        }

        [Fact]
        public void APartialKeepsTheFocusedPosterOnTheSameTitle()
        {
            var (h, vm, search) = OpenSearch();
            Type(h, vm, "dark");
            var call = search.Calls.Single(c => c.Query == "dark");
            call.Partial(Results("dark", "tt1", "tt3"));
            for (var i = 0; i < 12 && vm.Focus != SearchFocus.Results; i++)
                h.Press(RemoteKey.Right);        // across the keyboard to the posters
            h.Press(RemoteKey.Right);            // the second poster: tt3
            Assert.Equal("tt3", vm.FocusedItem.Meta.Id);
            call.Partial(Results("dark", "tt2", "tt1", "tt3"));
            Assert.Equal("tt3", vm.FocusedItem.Meta.Id);
        }

        [Fact]
        public void ANewerQueryCancelsTheOldSearchAndIgnoresItsLateResults()
        {
            var (h, vm, search) = OpenSearch();
            Type(h, vm, "dar");
            var old = search.Calls.Single(c => c.Query == "dar");
            Type(h, vm, "dark");
            Assert.True(old.Token.IsCancellationRequested);
            old.Partial(Results("dar", "tt9"));   // late, from the superseded search
            Assert.DoesNotContain(vm.Items, i => i.Meta.Id == "tt9");
            search.Calls.Single(c => c.Query == "dark").Done.SetResult(Results("dark", "tt1"));
            Assert.Equal(new[] { "tt1" }, vm.Items.Select(i => i.Meta.Id));
        }

        // ---- shared requests ----

        [Fact]
        public async Task SimultaneousRequestsForOneDocumentShareOneFetch()
        {
            var answer = new TaskCompletionSource<string>();
            const string url = "https://v3-cinemeta.strem.io/meta/series/tt1.json";
            var handler = new FixtureHandler().Route(url, async (request, token) => Ok(await answer.Task));
            var http = AddonFixtures.Http(handler);
            var a = http.GetJsonSharedAsync(url, CancellationToken.None);
            var b = http.GetJsonSharedAsync(url, CancellationToken.None);
            answer.SetResult("{\"meta\":{\"id\":\"tt1\",\"type\":\"series\",\"name\":\"One\"}}");
            Assert.Same(await a, await b);
            Assert.Equal(1, handler.CountOf(url));
            // Once answered it is not kept: a later request asks again.
            await http.GetJsonSharedAsync(url, CancellationToken.None);
            Assert.Equal(2, handler.CountOf(url));
        }

        [Fact]
        public async Task TitlePageDetailsAndExtrasComeFromOneCinemetaRequest()
        {
            var answer = new TaskCompletionSource<HttpResponseMessage>();
            const string url = "https://v3-cinemeta.strem.io/meta/series/tt1.json";
            var handler = new FixtureHandler().Route(url, (request, token) => answer.Task);
            var http = AddonFixtures.Http(handler);
            var catalog = new StremioCatalog(null, http);
            var extras = new CinemetaExtras(http);
            var meta = catalog.GetMetaAsync(MediaType.Series, "tt1", CancellationToken.None);
            var more = extras.GetAsync(MediaType.Series, "tt1", CancellationToken.None);
            answer.SetResult(Ok("{\"meta\":{\"id\":\"tt1\",\"type\":\"series\",\"name\":\"One\",\"imdbRating\":\"8.1\",\"videos\":[{\"id\":\"tt1:1:1\",\"season\":1,\"episode\":1,\"overview\":\"Pilot\"}]}}"));
            Assert.Equal("One", (await meta).Summary.Name);
            Assert.Equal("8.1", (await more).Rating);
            Assert.Equal("Pilot", (await more).Episodes["tt1:1:1"].Overview);
            Assert.Equal(1, handler.CountOf(url));
        }

        [Fact]
        public async Task ACancelledCallerDoesNotCancelTheSharedExtrasRequest()
        {
            var answer = new TaskCompletionSource<HttpResponseMessage>();
            const string url = "https://v3-cinemeta.strem.io/meta/movie/tt7.json";
            var handler = new FixtureHandler().Route(url, (request, token) => answer.Task);
            var extras = new CinemetaExtras(AddonFixtures.Http(handler));
            using (var cts = new CancellationTokenSource())
            {
                var first = extras.GetAsync(MediaType.Movie, "tt7", cts.Token);
                var second = extras.GetAsync(MediaType.Movie, "tt7", CancellationToken.None);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                answer.SetResult(Ok("{\"meta\":{\"id\":\"tt7\",\"type\":\"movie\",\"name\":\"Seven\",\"runtime\":\"99 min\"}}"));
                Assert.Equal("99 min", (await second).Runtime);
            }
        }

        // ---- service pages: a chip batch is dropped once another chip was chosen ----

        private sealed class PendingServiceCatalog : IServiceCatalog
        {
            public readonly List<(MediaType? Type, string Genre, TaskCompletionSource<ServicePage> Page, CancellationToken Token)> Titles =
                new List<(MediaType?, string, TaskCompletionSource<ServicePage>, CancellationToken)>();

            public Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
            {
                var page = new TaskCompletionSource<ServicePage>();
                if (count >= ServiceRows.PopularCount)   // the page's own rows (Home's Services row asks for counts too)
                    lock (Titles) Titles.Add((type, genre, page, token));
                return page.Task;
            }

            public Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token) =>
                Task.FromResult<IReadOnlyList<ServiceTitle>>(Array.Empty<ServiceTitle>());
        }

        private static ServicePage Page(string id, MediaType type) => new ServicePage
        {
            Titles = new[] { new ServiceTitle { Meta = new MetaSummary { Id = id, Name = id, Type = type } } }
        };

        [Fact]
        public void ComingBackToAChipIgnoresTheFirstVisitsLateRows()
        {
            var h = new UiHarness();
            var catalog = new PendingServiceCatalog();
            h.Shell.Services.ServiceCatalog = catalog;
            h.Shell.Start(new HomeViewModel());
            var page = new HomeViewModel(StreamingServices.Find("atp"));
            h.Shell.Push(page);
            var firstPopular = catalog.Titles.Single(t => t.Genre == null);
            Assert.DoesNotContain(catalog.Titles, t => t.Genre != null);   // genre rows wait for Popular

            h.Press(RemoteKey.Up);                                     // the chips
            h.Press(RemoteKey.Right, RemoteKey.Select);                // Series
            h.Press(RemoteKey.Left, RemoteKey.Select);                 // back to Home
            Assert.Equal("all", page.ServiceMode);
            Assert.True(firstPopular.Token.IsCancellationRequested);

            firstPopular.Page.SetResult(Page("tt-old", MediaType.Series));   // the first visit's answer, late
            Assert.DoesNotContain(page.Rows, r => r.Key == "svc:popular");
            var latest = catalog.Titles.Last(t => t.Genre == null && t.Type == null);
            latest.Page.SetResult(Page("tt-new", MediaType.Series));
            Assert.Contains(page.Rows.Single(r => r.Key == "svc:popular").Items, t => t.Id == "tt-new");
            Assert.Contains(catalog.Titles, t => t.Genre == "Drama" && !t.Token.IsCancellationRequested);   // genre rows follow Popular
        }

        [Fact]
        public async Task ServiceCardsShareInFlightWorkAndLoadAFewServicesAtATime()
        {
            var catalog = new CountingServiceCatalog();
            var cache = new ServiceSummaryCache(catalog);
            var now = DateTimeOffset.UtcNow;
            var cards = StreamingServices.All.Take(4).Select(s => cache.GetAsync(s, now, CancellationToken.None)).ToList();
            var again = cache.GetAsync(StreamingServices.All[0], now, CancellationToken.None);
            await Task.Delay(100);
            Assert.True(catalog.MaxServicesAtOnce <= ServiceSummaryCache.Concurrency, "services fetched at once: " + catalog.MaxServicesAtOnce);
            catalog.Release.SetResult(true);
            await Task.WhenAll(cards.Concat(new[] { again }));
            Assert.Equal(4, catalog.ServicesFetched.Distinct().Count());
            Assert.Equal(1, catalog.PopularCalls(StreamingServices.All[0].Id));   // the second ask shared the first fetch
            Assert.Same(await cards[0], await again);
        }

        private sealed class CountingServiceCatalog : IServiceCatalog
        {
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly List<string> ServicesFetched = new List<string>();
            private readonly List<(string Service, string Kind)> calls = new List<(string, string)>();
            private readonly HashSet<string> active = new HashSet<string>();
            public int MaxServicesAtOnce;

            public int PopularCalls(string service) { lock (calls) return calls.Count(c => c.Service == service && c.Kind == "popular"); }

            public async Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
            {
                lock (calls)
                {
                    calls.Add((service.Id, type == null ? "popular" : "count"));
                    if (type == null) ServicesFetched.Add(service.Id);
                    active.Add(service.Id);
                    MaxServicesAtOnce = Math.Max(MaxServicesAtOnce, active.Count);
                }
                await Release.Task;
                lock (calls) active.Remove(service.Id);
                return Page("tt-" + service.Id, MediaType.Series);
            }

            public async Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token)
            {
                await Release.Task;
                return Array.Empty<ServiceTitle>();
            }
        }

        // ---- player ----

        [Fact]
        public void ACommittedSeekStaysOnScreenUntilThePlayerLands()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(600_000);
            h.Shell.Tick();
            h.Player.SeekLag = true;   // the native seek takes a while
            h.Press(RemoteKey.FastForward);
            h.Wait(PlayerViewModel.SeekCommitDelayMs + 200);
            Assert.Null(vm.PendingSeekMs);
            Assert.Equal(610_000, vm.DisplayPositionMs);   // not back at 600 000 while the seek runs
            h.Player.Snapshot.PositionMs = 610_400;        // landed
            h.Shell.Tick();
            Assert.Equal(610_400, vm.DisplayPositionMs);
        }

        [Fact]
        public void ASubtitleCueRedrawsWithoutWaitingForTheNextSecond()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(600_000);
            h.Shell.Tick();
            var version = h.Shell.Version;
            h.Player.Snapshot.SubtitleText = "Say my name.";   // same second, same state
            h.Shell.Tick();
            Assert.NotEqual(version, h.Shell.Version);
            Assert.Equal("Say my name.", vm.SubtitleText);
        }

        [Fact]
        public void AStartStepChangeRedrawsTheStartCard()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(600_000);
            h.Player.Snapshot.StartStep = 1;
            h.Player.Snapshot.StatusText = "Finding sources";
            h.Shell.Tick();
            var version = h.Shell.Version;
            h.Player.Snapshot.StartStep = 2;
            h.Player.Snapshot.StatusText = "Trying the best of 6 sources";
            h.Shell.Tick();
            Assert.NotEqual(version, h.Shell.Version);
        }

        [Fact]
        public void ThePlayerReadsTheEngineOncePerFrameNotOncePerAccessor()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(600_000);
            h.Press(RemoteKey.Select);   // overlay open: the layout reads a lot
            var before = h.Player.SnapshotReads;
            UiLayout.Build(h.Shell);
            Assert.True(h.Player.SnapshotReads - before <= 1, "snapshot reads in one layout: " + (h.Player.SnapshotReads - before));
        }
    }
}
