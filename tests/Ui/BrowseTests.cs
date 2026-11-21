using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class BrowseTests
    {
        private sealed class Source : IBrowseSource
        {
            public readonly List<(MediaType Type, string Genre, int Skip)> Calls = new List<(MediaType, string, int)>();

            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
            {
                Calls.Add((type, genre, skip));
                if (skip > 0) return Task.FromResult<IReadOnlyList<BrowseItem>>(Array.Empty<BrowseItem>());
                BrowseItem I(string id, string name, string year, string rating, string runtime) => new BrowseItem
                {
                    Meta = new MetaSummary { Id = id, Name = name, Type = type, ReleaseInfo = year },
                    Extras = new MetaExtras { Rating = rating, Runtime = runtime, Genres = new[] { genre ?? "Drama" } }
                };
                return Task.FromResult<IReadOnlyList<BrowseItem>>(new[]
                {
                    I("tt0903747", "Breaking Bad", "2008–2013", "9.5", "49 min"),
                    I("tt2", "Short Comedy", "2021–", "7.4", "22 min"),
                    I("tt3", "Old Show", "1995–1999", "6.1", "45 min")
                });
            }
        }

        private static (UiHarness H, Source S, BrowseViewModel Vm) Open(IDictionary<BrowseFilter, string> preset = null)
        {
            var h = new UiHarness();
            var source = new Source();
            h.Shell.Services.Browse = source;
            h.Shell.Start(new HomeViewModel());
            var vm = new BrowseViewModel(preset);
            h.Shell.Push(vm);
            return (h, source, vm);
        }

        [Fact]
        public void LocalFiltersNarrowResults()
        {
            var (h, _, vm) = Open();
            Assert.Equal(3, vm.Results.Count);
            // Rating: 8+
            while (BrowseViewModel.Filters[vm.FilterIndex] != BrowseFilter.Rating) h.Press(RemoteKey.Right);
            h.PickFilter(2);
            Assert.Equal("8+", vm.Value(BrowseFilter.Rating));
            Assert.Equal(new[] { "Breaking Bad" }, vm.Results.Select(r => r.Meta.Name).ToArray());
            h.PickFilter(-2);            // back to Any rating
            while (BrowseViewModel.Filters[vm.FilterIndex] != BrowseFilter.Runtime) h.Press(RemoteKey.Right);
            h.PickFilter(1);             // short
            Assert.Equal(new[] { "Short Comedy" }, vm.Results.Select(r => r.Meta.Name).ToArray());
            Assert.Contains("1 titles", h.Text);
        }

        [Fact]
        public void TypeAndGenreQueryTheSource()
        {
            var (h, source, vm) = Open(new Dictionary<BrowseFilter, string> { [BrowseFilter.Genre] = "Comedy" });
            Assert.Equal((MediaType.Series, "Comedy", 0), source.Calls[0]);
            h.PickFilter(1);   // Type -> Movies
            Assert.Equal(MediaType.Movie, source.Calls.Last().Type);
            h.Press(RemoteKey.Down, RemoteKey.Select);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
        }

        [Fact]
        public void WatchableWithUsesSourceChecks()
        {
            var (h, _, vm) = Open(new Dictionary<BrowseFilter, string> { [BrowseFilter.Watchable] = "Spanish subtitles" });
            // FakeSources: the best source is EN audio + ES subs (tier 7) for every title.
            Assert.Equal(3, vm.Results.Count);
            Assert.NotNull(vm.SignatureFor("tt0903747"));
            h.Press(RemoteKey.Down);     // the focused title's panel says it in plain words
            while (vm.FocusedItem.Meta.Id != "tt0903747") h.Press(RemoteKey.Right);
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Key == "browse:card:signature" && n.Text == "HD · Spanish subs · Ready to play");
            Assert.DoesNotContain(frame.Nodes, n => n.Text != null && (n.Text.Contains("GB") || n.Text.Contains("ES subs")));
        }

        [Fact]
        public void HomeMenuOpensBrowse()
        {
            var h = new UiHarness();
            h.Shell.Services.Browse = new Source();
            h.Shell.Start(new HomeViewModel());
            h.Press(RemoteKey.Up, RemoteKey.Right, RemoteKey.Select);
            Assert.IsType<BrowseViewModel>(h.Shell.Current);
        }
    }
}
