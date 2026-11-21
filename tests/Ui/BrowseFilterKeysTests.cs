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
    /// Browse (TV feedback): holding Up overshot onto the filter bar and Up on a filter changed it and re-ran the search.
    public class BrowseFilterKeysTests
    {
        private sealed class Source : IBrowseSource
        {
            public readonly List<(MediaType Type, string Genre, int Skip)> Calls = new List<(MediaType, string, int)>();
            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
            {
                Calls.Add((type, genre, skip));
                return Task.FromResult<IReadOnlyList<BrowseItem>>(skip > 0 ? new List<BrowseItem>() : Enumerable.Range(0, 40).Select(i => new BrowseItem
                {
                    Meta = new MetaSummary { Id = "tt" + i, Name = "T" + i, Type = type, ReleaseInfo = "2020" }, Extras = new MetaExtras()
                }).ToList());
            }
        }

        private static (UiHarness H, Source S, BrowseViewModel Vm) Open()
        {
            var h = new UiHarness();
            var source = new Source();
            h.Shell.Services.Browse = source;
            h.Shell.Start(new HomeViewModel());
            var vm = new BrowseViewModel();
            h.Shell.Push(vm);
            return (h, source, vm);
        }

        [Fact]
        public void HeldUpStopsAtTheTopRowOfTheGrid()
        {
            var (h, _, vm) = Open();
            for (var i = 0; i < 4; i++) h.Press(RemoteKey.Down);
            Assert.True(vm.Index >= 3 * BrowseViewModel.Columns);
            h.Hold(RemoteKey.Up, 12);                 // auto-repeat, 100 ms apart
            Assert.InRange(vm.Index, 0, BrowseViewModel.Columns - 1);   // top row of the grid, not the filters
            h.Press(RemoteKey.Up);                    // still within the repeat window: stays
            Assert.InRange(vm.Index, 0, BrowseViewModel.Columns - 1);
            h.Wait(500);
            h.Press(RemoteKey.Up);                    // a fresh press after a pause crosses into the filters
            Assert.Equal(-1, vm.Index);
        }

        [Fact]
        public void LandingOnOrPressingUpOnAFilterChangesNothing()
        {
            var (h, source, vm) = Open();
            var calls = source.Calls.Count;
            var before = BrowseViewModel.Filters.Select(vm.Value).ToArray();
            h.Press(RemoteKey.Down, RemoteKey.Up);    // grid, then back onto the filter bar
            h.Hold(RemoteKey.Up, 6);                  // Up on the bar, held
            h.Press(RemoteKey.Right, RemoteKey.Right, RemoteKey.Left);
            h.Wait(1000);
            Assert.Equal(before, BrowseViewModel.Filters.Select(vm.Value).ToArray());
            Assert.Equal(calls, source.Calls.Count);
        }

        [Fact]
        public void QuickFilterChangesRunOneSearch()
        {
            var (h, source, vm) = Open();
            while (BrowseViewModel.Filters[vm.FilterIndex] != BrowseFilter.Genre) h.Press(RemoteKey.Right);
            var calls = source.Calls.Count;
            h.Shell.HandleKey(RemoteKey.Select);      // the dropdown lists every genre
            Assert.True(vm.DropdownOpen);
            Assert.Contains("[dropdown] >*Any genre | Action | Adventure", h.Text);
            h.Hold(RemoteKey.Down, 3, 120);           // Action, Adventure, Animation: stepping queries nothing
            Assert.Equal("Any genre", vm.Value(BrowseFilter.Genre));
            h.Shell.HandleKey(RemoteKey.Select);
            Assert.False(vm.DropdownOpen);
            Assert.Equal("Animation", vm.Value(BrowseFilter.Genre));
            Assert.Equal(calls, source.Calls.Count);  // nothing yet: the choice has not settled
            h.Wait(BrowseViewModel.QueryDebounceMs + 200);
            var queried = source.Calls.Skip(calls).Where(c => c.Skip == 0).Select(c => c.Genre).Distinct().ToList();
            Assert.Equal(new[] { "Animation" }, queried);
        }
    }
}
