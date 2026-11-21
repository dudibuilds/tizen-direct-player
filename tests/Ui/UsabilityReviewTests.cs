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
    /// Behaviour changed by the usability review (finding ids in the test names' comments).
    public class UsabilityReviewTests
    {
        private sealed class ListCatalog : ISourceCatalog
        {
            public IReadOnlyList<SourceEntry> Entries;
            public Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token) => Task.FromResult(Entries);
        }

        private static AddonStreamItem Stream(string title, string hash, string name = "Torrentio") =>
            new AddonStreamItem { Name = name, Title = title, InfoHash = hash, FileIdx = 0, AddonId = "torrentio", AddonName = "Torrentio" };

        private static string Hash(int n) => n.ToString("x").PadLeft(40, '0');

        private static readonly MetaDetail English = new MetaDetail
        {
            Summary = new MetaSummary { Id = "tt0903747", Type = MediaType.Series, Name = "Night Harbor" }, Country = "United States",
            Videos = new[] { new VideoInfo { Id = "tt0903747:2:3", Season = 2, Episode = 3, Title = "Bit by a Dead Bee" } }
        };

        // ---- P8: ranking ----

        private static readonly AddonStreamItem[] DubVersusBetter =
        {
            Stream("Night.Harbor.S02E03.720p.BluRay.Eng.Rus.mkv\nMulti Audio\n\U0001F464 89 \U0001F4BE 2.7 GB", Hash(1)),
            Stream("Night Harbor S02E03 1080p BluRay x265.mkv\n\U0001F464 508 \U0001F4BE 2.0 GB", Hash(2)),
            Stream("Night.Harbor.S02E03.1080p.BluRay.mkv\n\U0001F464 486 \U0001F4BE 540 MB", Hash(3))
        };

        [Fact]
        public void AnExtraDubDoesNotOutrankResolutionOnAnEnglishTitle()
        {
            var old = StreamRanking.Rank(DubVersusBetter, new RankOptions { Episodic = true });
            var ranked = StreamRanking.Rank(DubVersusBetter, new RankOptions { Episodic = true, EnglishOriginal = true });
            Assert.Equal("1080p", ranked[0].Quality);
            Assert.Equal("720p", ranked.Last().Quality);
            // Without the hint (a foreign or unknown title) the language tier still leads, as before.
            Assert.Equal(old[0].LanguageTier >= 1 ? "720p" : "1080p", old[0].Quality);
        }

        [Fact]
        public void EnglishOriginalComesFromTheFirstCountry()
        {
            Assert.True(SourceOptions.IsEnglishOriginal(English));
            Assert.True(SourceOptions.IsEnglishOriginal(new MetaDetail { Country = "United Kingdom, France" }));
            Assert.False(SourceOptions.IsEnglishOriginal(new MetaDetail { Country = "Germany, United States" }));
            Assert.False(SourceOptions.IsEnglishOriginal(new MetaDetail()));
            Assert.False(SourceOptions.IsEnglishOriginal(null));
        }

        [Fact]
        public void ThePickerRanksAnEnglishTitleByQualityAndExplainsWithoutRepeatingTheRow()
        {
            var entries = AddonSourceCatalog.FromStreams(DubVersusBetter, English, English.Videos[0], new SourceOptions(), DeviceCaps.Ru7100);
            Assert.Equal("1080p", entries[0].Facts.Quality);
            var why = SourcePickerViewModel.WhyBest(entries[0], false);
            Assert.DoesNotContain("GB", why);
            Assert.DoesNotContain("seeders", why);
            Assert.DoesNotContain("1080p", why);
        }

        // ---- P9-P11: the picker ----

        private static (UiHarness, SourcePickerViewModel) Picker(int perQuality = 8)
        {
            var streams = new List<AddonStreamItem>();
            var n = 1;
            foreach (var quality in new[] { "2160p", "1080p", "720p" })
                for (var i = 0; i < perQuality; i++)
                    streams.Add(Stream("Night.Harbor.S02E03." + quality + ".WEB-DL.x264.AAC." + i + ".mkv\n\U0001F464 " + (i < perQuality - 2 ? 400 - 10 * i : 2) + " \U0001F4BE 2." + i + " GB", Hash(n++)));
            var h = new UiHarness();
            h.Shell.Services.SourceCatalog = new ListCatalog { Entries = AddonSourceCatalog.FromStreams(streams, English, English.Videos[0], new SourceOptions(), DeviceCaps.Ru7100) };
            h.Shell.Start(new HomeViewModel());
            var picker = new SourcePickerViewModel(English, English.Videos[0], 0, PlaybackMode.Single, false);
            h.Shell.Push(picker);
            return (h, picker);
        }

        [Fact]
        public void ThePickerOpensOnAShortListWithAShowAllRow()
        {
            var (h, picker) = Picker();
            Assert.Equal(24, picker.PlayableCount);
            Assert.Equal(3 * SourcePickerViewModel.PerQuality, picker.Entries.Count);
            Assert.All(picker.Entries.GroupBy(e => e.Facts.Quality), g => Assert.Equal(SourcePickerViewModel.PerQuality, g.Count()));
            Assert.All(picker.Entries, e => Assert.True(e.Candidate.Seeders >= SourcePickerViewModel.SeederFloor));
            Assert.True(picker.HasShowAllRow);
            Assert.Contains("[Show all 24 sources]", h.Text);
            for (var i = 0; i < 20; i++) h.Press(RemoteKey.Down);
            Assert.True(picker.ShowAllFocused);                 // the list ends in the row, 9 presses down at most
            h.Press(RemoteKey.Select);
            Assert.True(picker.ShowAll);
            Assert.Equal(24, picker.Entries.Count);
            Assert.False(picker.HasShowAllRow);
            Assert.Equal(9, picker.Index);                      // focus is on the first source that just appeared
        }

        [Fact]
        public void LeftAndRightOnTheListSwitchQualityChips()
        {
            var (h, picker) = Picker();
            Assert.Equal(new[] { "All", "4K", "1080p", "720p" }, picker.Qualities.ToArray());
            h.Press(RemoteKey.Down, RemoteKey.Right);
            Assert.Equal("4K", picker.Quality);
            Assert.Equal(0, picker.Index);
            Assert.All(picker.Entries, e => Assert.Equal("4K", e.Facts.Quality));
            Assert.Equal(SourcePickerViewModel.PerQualityFocused, picker.Entries.Count);
            Assert.Contains("[Show all 8 4K sources]", h.Text);
            h.Press(RemoteKey.Left, RemoteKey.Left);
            Assert.Equal("All", picker.Quality);
            // The chip bar is also focusable: Up from the first row, Right along the chips, OK applies.
            h.Press(RemoteKey.Up, RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);
            Assert.Equal("1080p", picker.Quality);
            Assert.Equal(-1, picker.Index);
            h.Press(RemoteKey.Down, RemoteKey.Select);
            Assert.IsType<PlayerViewModel>(h.Shell.Current);
        }

        [Fact]
        public void SourceRowsAreOneLineAndTheFocusedRowUsesTheFocusFill()
        {
            var (h, picker) = Picker();
            h.Press(RemoteKey.Down);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            var rows = nodes.Where(n => n.Key.StartsWith("source:", StringComparison.Ordinal) && n.Key.EndsWith(":bg", StringComparison.Ordinal)
                && !n.Key.Contains(":q:") && !n.Key.Contains(":chip:") && !n.Key.Contains(":best:")).ToList();
            var focused = rows.Single(r => r.Fill == UiTheme.FocusRow);   // the soft light fill of every focused list row
            Assert.True(rows.Count(r => r.Y + r.H <= UiFrame.Height) >= 7, "rows on screen: " + rows.Count);
            Assert.All(rows.Where(r => r != focused), r => Assert.True(r.H <= 88));
            // Only the focused row carries a file name; no row shows an empty column or the single add-on's name.
            Assert.Single(nodes.Where(n => n.Key.EndsWith(":file", StringComparison.Ordinal)));
            Assert.DoesNotContain(nodes, n => n.Text == "—" || n.Text == "Torrentio");
            Assert.Contains(nodes, n => n.Key.Contains(":chip:") && n.Text == "h264" || n.Key.Contains(":chip:") && n.Text != null);
            Assert.Equal(UiTheme.Text, nodes.Single(n => n.Key == focused.Key.Replace(":bg", ":size")).Color);   // white on the soft fill
        }

        // ---- B3, B4, B7, B12, B14: Browse and Search ----

        private sealed class BrowseSource : IBrowseSource
        {
            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token) =>
                Task.FromResult<IReadOnlyList<BrowseItem>>(skip > 0 ? new List<BrowseItem>() : Enumerable.Range(0, 40).Select(i => new BrowseItem
                {
                    Meta = new MetaSummary { Id = "tt" + i, Name = "T" + i, Type = type, ReleaseInfo = "2020" },
                    Extras = new MetaExtras { Rating = "8.1", Runtime = "231 min", Seasons = 3, Genres = new[] { "Drama" }, Description = "About T" + i }
                }).ToList());
        }

        private static (UiHarness, BrowseViewModel) Browse()
        {
            var h = new UiHarness();
            h.Shell.Services.Browse = new BrowseSource();
            h.Shell.Start(new HomeViewModel());
            var vm = new BrowseViewModel();
            h.Shell.Push(vm);
            return (h, vm);
        }

        [Fact]
        public void FilterDropdownListsOptionsStepsBothWaysAndBackCancels()
        {
            var (h, vm) = Browse();
            h.Press(RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);   // Genre (after Type and Service)
            Assert.True(vm.DropdownOpen);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Contains(nodes, n => n.Key == "dropdown:0" && n.Text == "Any genre");
            Assert.Contains(nodes, n => n.Key == "dropdown:0:tick:m0");   // the current choice is ticked
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Down, RemoteKey.Up);
            Assert.Equal(2, vm.DropdownIndex);                       // stepping back is one press
            h.Press(RemoteKey.Back);                                 // cancel: Browse stays, nothing changed
            Assert.Same(vm, h.Shell.Current);
            Assert.False(vm.DropdownOpen);
            Assert.Equal("Any genre", vm.Value(BrowseFilter.Genre));
            h.PickFilter(4);
            Assert.Equal("Comedy", vm.Value(BrowseFilter.Genre));
            Assert.True(vm.AnyFilterSet);
            Assert.Contains("[Clear filters]", h.Text);
            for (var i = 0; i < 10; i++) h.Press(RemoteKey.Right);
            Assert.True(vm.ClearFocused);
            h.Press(RemoteKey.Select);
            Assert.False(vm.AnyFilterSet);
            Assert.DoesNotContain("[Clear filters]", h.Text);
        }

        [Fact]
        public void BackInBrowseStepsToTheFilterBarAndStateReturnsOnReopen()
        {
            var (h, vm) = Browse();
            h.Press(RemoteKey.Right, RemoteKey.Right);               // Genre (after Type and Service)
            h.PickFilter(4);                                         // Comedy
            h.Wait(BrowseViewModel.QueryDebounceMs + 200);
            for (var i = 0; i < 5; i++) h.Press(RemoteKey.Down);
            h.Press(RemoteKey.Right, RemoteKey.Right);
            var index = vm.Index;
            Assert.True(index >= 4 * BrowseViewModel.Columns);
            Assert.Contains("Title " + (index + 1) + " of 40", h.Text);   // a position cue
            h.Press(RemoteKey.Back);
            Assert.Same(vm, h.Shell.Current);                        // not thrown out to Home
            Assert.Equal(-1, vm.Index);
            h.Press(RemoteKey.Back);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
            var again = new BrowseViewModel();
            h.Shell.Push(again);
            Assert.Equal("Comedy", again.Value(BrowseFilter.Genre));
            h.Wait(BrowseViewModel.QueryDebounceMs + 200);
            Assert.Equal("Comedy", again.Value(BrowseFilter.Genre));
        }

        [Fact]
        public void BrowseShowsTheSameFocusedTitlePanelAsSearchAndCaptionsNeverShift()
        {
            var (h, vm) = Browse();
            var before = UiLayout.Build(h.Shell).Nodes;
            var captionY = before.Single(n => n.Key == "browse:tt6:name").Y;
            Assert.Equal("2020 · ★ 8.1", before.Single(n => n.Key == "browse:tt6:facts").Text);   // short enough to fit; no episode runtime on a series
            h.Press(RemoteKey.Down, RemoteKey.Down);                 // tt5, the row of tt6
            h.Press(RemoteKey.Up);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Equal("T0", nodes.Single(n => n.Key == "browse:card:name").Text);
            Assert.Equal("About T0", nodes.Single(n => n.Key == "browse:card:description").Text);
            Assert.Equal("Drama", nodes.Single(n => n.Key == "browse:card:genres").Text);
            Assert.Equal("2020 · ★ 8.1 · 3 seasons", nodes.Single(n => n.Key == "browse:card:facts").Text);
            h.Press(RemoteKey.Down);
            nodes = UiLayout.Build(h.Shell).Nodes;
            // A caption sits at the same offset from its poster whether or not its row holds focus elsewhere.
            var poster = nodes.Single(n => n.Key == "browse:tt11:ph");
            var caption = nodes.Single(n => n.Key == "browse:tt11:name");
            var poster0 = before.Single(n => n.Key == "browse:tt6:ph");
            Assert.Equal(captionY - (poster0.Y + poster0.H), caption.Y - (poster.Y + poster.H));
            var next = nodes.Single(n => n.Key == "browse:tt11:facts");
            Assert.True(next.Y + next.H <= nodes.Where(n => n.Key == "browse:tt15:ph").Select(n => n.Y).DefaultIfEmpty(int.MaxValue).First());
        }

        [Fact]
        public void SearchOpensWithPopularTitlesRecentSearchesAndAQueryField()
        {
            var h = new UiHarness();
            h.Catalog.SearchResults.Clear();
            h.Shell.Services.Memory.RememberSearch("dark");
            h.Shell.Start(new HomeViewModel());
            var vm = new SearchViewModel();
            h.Shell.Push(vm);
            Assert.True(vm.ShowingPopular);
            Assert.Contains("Recent: dark", h.Text);
            Assert.Contains("Popular now:", h.Text);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Contains(nodes, n => n.Key == "search:field");
            Assert.Contains(nodes, n => n.Key == "search:query" && n.Text == "Search titles");
            vm.Keyboard.FocusLabel("d");
            h.Press(RemoteKey.Select);
            Assert.Equal("d▏", UiLayout.Build(h.Shell).Nodes.Single(n => n.Key == "search:query").Text);   // a caret while typing
        }
    }
}
