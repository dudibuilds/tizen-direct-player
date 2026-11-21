using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Home's Services row and a service's own page (docs/streaming-services.md, mocks 10 and 11).
    public class ServicePageTests
    {
        /// Every service has three made-up titles; Comedy and Animation are empty so their rows drop out.
        private sealed class Catalog : IServiceCatalog
        {
            public readonly List<(string Service, MediaType? Type, string Genre, int Count)> Calls = new List<(string, MediaType?, string, int)>();
            public int NewCalls;

            private static ServiceTitle T(string id, string name, MediaType type) =>
                new ServiceTitle { Meta = new MetaSummary { Id = id, Name = name, Type = type, ReleaseInfo = "2024" }, Rating = "7.0" };

            public Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
            {
                lock (Calls) Calls.Add((service.Id, type, genre, count));
                if (genre == "Comedy" || genre == "Animation")
                    return Task.FromResult(new ServicePage());
                var all = new[] { T("tt0000101", "Glass Harbor", MediaType.Series), T("tt0000102", "Quiet Orbit", MediaType.Movie), T("tt0000103", "Lantern Row", MediaType.Series) };
                var list = all.Where(t => type == null || t.Meta.Type == type).Take(count).ToList();
                return Task.FromResult(new ServicePage { Titles = list, Total = type == MediaType.Movie ? 132 : type == MediaType.Series ? 228 : 360 });
            }

            public Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token)
            {
                Interlocked.Increment(ref NewCalls);
                return Task.FromResult<IReadOnlyList<ServiceTitle>>(maxDays <= ServiceSummaryCache.NewDays
                    ? new[] { T("tt0000103", "Lantern Row", MediaType.Series) }
                    : new[] { T("tt0000103", "Lantern Row", MediaType.Series), T("tt0000102", "Quiet Orbit", MediaType.Movie) });
            }
        }

        private sealed class Feed : IRecommendationsFeed
        {
            public Task<IReadOnlyList<RecommendationItem>> GetAsync(CancellationToken token) =>
                Task.FromResult<IReadOnlyList<RecommendationItem>>(new[] { new RecommendationItem { Meta = new MetaSummary { Id = "tt5753856", Type = MediaType.Series, Name = "Dark" }, Reason = "Because" } });
        }

        private static (UiHarness H, Catalog C) Harness(bool withFeed = true)
        {
            var h = new UiHarness();
            var catalog = new Catalog();
            h.Shell.Services.ServiceCatalog = catalog;
            if (withFeed) h.Shell.Services.Recommendations = new Feed();
            return (h, catalog);
        }

        [Fact]
        public void ServicesRowSitsBelowThePersonalRowsWithOneCardPerService()
        {
            var (h, _) = Harness();
            var home = new HomeViewModel();
            h.Shell.Start(home);

            var keys = home.Rows.Where(r => !r.IsMenu).Select(r => r.Key).ToList();
            Assert.True(keys.IndexOf("services") > keys.IndexOf("foryou"));
            var row = home.Rows.Single(r => r.Key == "services");
            Assert.Equal(StreamingServices.All.Select(s => s.Name), row.Items.Select(t => t.Title));
            var apple = row.Items.Single(t => t.Title == "Apple TV");
            Assert.Equal(TileAction.Service, apple.Action);
            Assert.Equal("228 series  ·  132 movies  ·  1 new this week", apple.ServiceCard.Facts);
            Assert.Equal("Popular now: Glass Harbor, Quiet Orbit, Lantern Row", apple.ServiceCard.Description);
            Assert.Equal("1 new this week", apple.ServiceCard.Caption);
            Assert.Equal("https://images.metahub.space/background/small/tt0000101/img", apple.Image);
        }

        [Fact]
        public void ServicesRowIsNeverTheFirstRow()
        {
            var (h, _) = Harness(withFeed: false);
            h.Library.Entries.Clear();
            h.Catalog.Metas.Clear();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            var shelves = home.Rows.Where(r => !r.IsMenu).Select(r => r.Key).ToList();
            Assert.Contains("services", shelves);
            if (shelves.Count > 1)
                Assert.NotEqual("services", shelves[0]);
        }

        [Fact]
        public void HomeAsksForServiceNumbersOnlyOnceWithinTheCacheWindow()
        {
            var (h, catalog) = Harness();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            var first = catalog.Calls.Count;
            home.Reload();
            Assert.Equal(first, catalog.Calls.Count);
        }

        private static HomeViewModel OpenApple(UiHarness h)
        {
            var home = new HomeViewModel();
            h.Shell.Start(home);
            var page = new HomeViewModel(StreamingServices.Find("atp"));
            h.Shell.Push(page);
            return page;
        }

        [Fact]
        public void ServicePageHasItsOwnRowsEachEndingInSeeAll()
        {
            var (h, _) = Harness();
            var page = OpenApple(h);

            Assert.Equal("Apple TV", page.Title);
            var shelves = page.Rows.Where(r => !r.IsMenu).ToList();
            Assert.Equal(new[] { "Popular in the US", "New on Apple TV", "Drama", "Thriller", "Sci-Fi", "Action", "Documentary" }, shelves.Select(r => r.Title));
            Assert.Equal(TileStyle.Ranked, shelves[0].Style);
            Assert.Equal(new[] { 1, 2, 3 }, shelves[0].Items.Take(3).Select(t => t.Rank));
            Assert.All(shelves, r => Assert.Equal(TileAction.SeeAll, r.Items.Last().Action));
            Assert.Equal(new[] { "Home", "Series", "Movies", "Genres" }, page.Rows.Single(r => r.IsMenu).Items.Select(t => t.Title));
            Assert.Equal("svc:popular", page.FocusedRow.Key);   // opens on the first row, not the chips
        }

        [Fact]
        public void SeeAllOpensBrowseWithTheServiceAndGenre()
        {
            var (h, _) = Harness();
            var page = OpenApple(h);
            while (page.FocusedRow.Title != "Drama") h.Press(RemoteKey.Down);
            for (var i = 0; i < 5; i++) h.Press(RemoteKey.Right);
            Assert.Equal(TileAction.SeeAll, page.FocusedTile.Action);
            h.Press(RemoteKey.Select);

            var browse = Assert.IsType<BrowseViewModel>(h.Shell.Current);
            Assert.Equal("Apple TV", browse.Heading);
            Assert.Equal("Drama", browse.Value(BrowseFilter.Genre));
            Assert.Equal("Series", browse.Value(BrowseFilter.Type));
        }

        [Fact]
        public void MoviesChipReloadsTheRowsWithMoviesOnly()
        {
            var (h, catalog) = Harness();
            var page = OpenApple(h);
            h.Press(RemoteKey.Up);                                    // the chips
            Assert.True(page.FocusedRow.IsMenu);
            h.Press(RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);   // Movies
            Assert.Equal("movie", page.ServiceMode);
            var popular = page.Rows.Single(r => r.Key == "svc:popular");
            Assert.All(popular.Items.Where(t => t.Action != TileAction.SeeAll), t => Assert.Equal(MediaType.Movie, t.Type));
            Assert.Contains(catalog.Calls, c => c.Service == "atp" && c.Type == MediaType.Movie && c.Genre == "Drama");
            Assert.All(page.Rows.Single(r => r.Key == "svc:new").Items.Where(t => t.Action != TileAction.SeeAll), t => Assert.Equal(MediaType.Movie, t.Type));
        }

        [Fact]
        public void ServiceCardOpensThePageAndBackReturnsToTheSameCard()
        {
            var (h, _) = Harness();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            while (home.FocusedRow.Key != "services") h.Press(RemoteKey.Down);
            h.Press(RemoteKey.Right);                                 // Apple TV
            h.Wait(HomeViewModel.HeroDelayMs + 200);
            Assert.Equal("Apple TV", home.HeroTile.Title);            // the billboard describes the service
            Assert.Contains("[billboard] Apple TV", h.Text);
            h.Press(RemoteKey.Select);
            var page = Assert.IsType<HomeViewModel>(h.Shell.Current);
            Assert.Equal("atp", page.Service.Id);
            h.Press(RemoteKey.Back);
            Assert.Same(home, h.Shell.Current);
            Assert.Equal("services", home.FocusedRow.Key);
            Assert.Equal("Apple TV", home.FocusedTile.Title);
        }

        [Fact]
        public void ServicePagePaintsInTheServicesInk()
        {
            var (h, _) = Harness();
            OpenApple(h);
            var frame = UiLayout.Build(h.Shell);
            Assert.Equal(StreamingServices.Find("atp").PageInk, frame.Background);
            Assert.Contains(frame.Nodes, n => n.Key == "svc:name" && n.Text == "Apple TV");
            Assert.Contains(frame.Nodes, n => n.Key == "svc:glow");

            h.Press(RemoteKey.Back);
            Assert.Equal(UiTheme.Background, UiLayout.Build(h.Shell).Background);   // Home stays ink
        }
    }
}
