using System.Collections.Generic;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class SearchChipsTests
    {
        private static (UiHarness H, SearchViewModel Vm) Searched(string text)
        {
            var h = new UiHarness();
            var extras = new FakeExtras();
            extras.Items["tt0903747"] = new MetaExtras { Genres = new[] { "Crime", "Drama" }, Description = "Chemistry teacher." };
            h.Shell.Services.Extras = extras;
            h.Shell.Start(new HomeViewModel());
            var vm = new SearchViewModel();
            h.Shell.Push(vm);
            foreach (var c in text)
            {
                vm.Keyboard.FocusLabel(c.ToString());
                h.Press(RemoteKey.Select);
            }
            h.Wait(400);
            return (h, vm);
        }

        [Fact]
        public void ChipsAreRefinementsOnlyNotTheTitlesAlreadyShownAsPosters()
        {
            var (_, vm) = Searched("br");
            var labels = vm.Chips.Select(c => c.Label).ToList();
            Assert.DoesNotContain("Breaking Bad", labels);
            Assert.All(vm.Chips, c => Assert.NotNull(c.Preset));
            Assert.Contains("Crime series", labels);
            Assert.Contains("With Spanish subtitles", labels);
            Assert.Contains("In 4K", labels);
            Assert.Contains("Series from the 2000s", labels);
        }

        [Fact]
        public void CollectionChipOpensBrowseWithFilter()
        {
            var (h, vm) = Searched("br");
            for (var i = 0; i < 8 && vm.Focus == SearchFocus.Keyboard; i++)
                h.Press(RemoteKey.Right);
            Assert.Equal(SearchFocus.Results, vm.Focus);       // Right from the keyboard lands on the first poster
            Assert.Equal(0, vm.ResultIndex);
            h.Press(RemoteKey.Up);                             // the chips are one Up from the first row
            Assert.Equal(SearchFocus.Suggestions, vm.Focus);
            while (vm.Chips[vm.SuggestionIndex].Label != "With Spanish subtitles")
                h.Press(vm.SuggestionIndex < vm.Chips.ToList().FindIndex(c => c.Label == "With Spanish subtitles") ? RemoteKey.Right : RemoteKey.Left);
            h.Press(RemoteKey.Select);
            var browse = Assert.IsType<BrowseViewModel>(h.Shell.Current);
            Assert.Equal("Spanish subtitles", browse.Value(BrowseFilter.Watchable));
            Assert.Equal(MediaType.Series, browse.Type);
        }

        [Fact]
        public void FocusedCardGetsSignatureLineAfterDwell()
        {
            var (h, vm) = Searched("br");
            for (var i = 0; i < 8 && vm.Focus == SearchFocus.Keyboard; i++)
                h.Press(RemoteKey.Right);
            Assert.Equal(SearchFocus.Results, vm.Focus);
            Assert.Null(vm.SignatureFor("tt0903747"));
            h.Wait(1700);
            Assert.NotNull(vm.SignatureFor("tt0903747"));
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "search:card:signature" && n.Text == "HD · Spanish subs · Ready to play");
        }
    }
}
