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
    /// Browse's Service filter: a streaming service's catalogue in the Browse grid (docs/streaming-services.md).
    public class ServiceBrowseTests
    {
        private sealed class Catalog : IServiceCatalog
        {
            public readonly List<(string Service, MediaType? Type, string Genre, int Skip)> Calls = new List<(string, MediaType?, string, int)>();

            public Task<ServicePage> GetTitlesAsync(StreamingService service, MediaType? type, string genre, int skip, int count, CancellationToken token)
            {
                Calls.Add((service.Id, type, genre, skip));
                if (skip > 0) return Task.FromResult(new ServicePage());
                return Task.FromResult(new ServicePage
                {
                    Total = 2,
                    Titles = new[]
                    {
                        new ServiceTitle { Meta = new MetaSummary { Id = "tt0000101", Name = "Glass Harbor", Type = type ?? MediaType.Series, ReleaseInfo = "2024" }, Rating = "7.6", Runtime = "54 min", Genres = new[] { "Drama" } },
                        new ServiceTitle { Meta = new MetaSummary { Id = "tt0000102", Name = "Lantern Row", Type = type ?? MediaType.Series, ReleaseInfo = "2019" }, Rating = "6.4", Runtime = "45 min", Genres = new[] { "Comedy" } }
                    }
                });
            }

            public Task<IReadOnlyList<ServiceTitle>> GetNewAsync(StreamingService service, DateTime today, int maxDays, int count, CancellationToken token) =>
                Task.FromResult<IReadOnlyList<ServiceTitle>>(Array.Empty<ServiceTitle>());
        }

        private sealed class DefaultSource : IBrowseSource
        {
            public int Calls;

            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
            {
                Calls++;
                return Task.FromResult<IReadOnlyList<BrowseItem>>(Array.Empty<BrowseItem>());
            }
        }

        private static (UiHarness H, Catalog C, DefaultSource D, BrowseViewModel Vm) Open(IDictionary<BrowseFilter, string> preset, bool withCatalog = true)
        {
            var h = new UiHarness();
            var catalog = new Catalog();
            var fallback = new DefaultSource();
            h.Shell.Services.Browse = fallback;
            if (withCatalog) h.Shell.Services.ServiceCatalog = catalog;
            h.Shell.Start(new HomeViewModel());
            catalog.Calls.Clear();   // Home's Services row asks for its numbers; only Browse's requests count here
            var vm = new BrowseViewModel(preset);
            h.Shell.Push(vm);
            return (h, catalog, fallback, vm);
        }

        [Fact]
        public void ServicePresetBrowsesThatServicesCatalogueUnderItsName()
        {
            var (_, catalog, fallback, vm) = Open(new Dictionary<BrowseFilter, string> { [BrowseFilter.Service] = "Apple TV", [BrowseFilter.Genre] = "Drama" });

            Assert.Equal("Apple TV", vm.Heading);
            Assert.Equal("atp", vm.Service.Id);
            Assert.Contains(catalog.Calls, c => c == ("atp", MediaType.Series, "Drama", 0));
            Assert.Equal(0, fallback.Calls);
            Assert.Equal(new[] { "Glass Harbor", "Lantern Row" }, vm.Results.Select(r => r.Meta.Name));
            Assert.Equal("7.6", vm.Results[0].Extras.Rating);   // facts come with the catalogue, no extra lookups
            Assert.True(vm.AnyFilterSet);                        // "Clear filters" also leaves the service
        }

        [Fact]
        public void LocalFiltersStillApplyInsideAService()
        {
            var (_, _, _, vm) = Open(new Dictionary<BrowseFilter, string> { [BrowseFilter.Service] = "Apple TV", [BrowseFilter.Year] = "2020s" });
            Assert.Equal(new[] { "Glass Harbor" }, vm.Results.Select(r => r.Meta.Name));
        }

        [Fact]
        public void NoServiceKeepsBrowseOnTheDefaultCatalogue()
        {
            var (_, catalog, fallback, vm) = Open(null);
            Assert.Equal("Any service", vm.Value(BrowseFilter.Service));
            Assert.Equal("Browse", vm.Heading);
            Assert.Empty(catalog.Calls);
            Assert.True(fallback.Calls > 0);
        }

        [Fact]
        public void WithoutAServiceCatalogTheFilterFallsBackToTheDefaultCatalogue()
        {
            var (_, _, fallback, vm) = Open(new Dictionary<BrowseFilter, string> { [BrowseFilter.Service] = "Apple TV" }, withCatalog: false);
            Assert.True(fallback.Calls > 0);
            Assert.Equal("Apple TV", vm.Heading);
        }

        [Fact]
        public void BrowseFromTheMenuNeverReopensInsideAService()
        {
            var (h, _, _, vm) = Open(null);
            h.Press(RemoteKey.Right);                      // the Service pill
            h.PickFilter(2);                               // Apple TV
            Assert.Equal("Apple TV", vm.Heading);
            h.Press(RemoteKey.Back);                       // leaves Browse; its filters are remembered
            Assert.IsType<HomeViewModel>(h.Shell.Current);
            var again = new BrowseViewModel();
            h.Shell.Push(again);
            Assert.Null(again.Service);
            Assert.Equal("Browse", again.Heading);
        }

        [Fact]
        public void ServiceOptionsListEveryService()
        {
            Assert.Equal(new[] { "Any service" }.Concat(StreamingServices.All.Select(s => s.Name)), BrowseViewModel.Options[BrowseFilter.Service]);
        }
    }
}
