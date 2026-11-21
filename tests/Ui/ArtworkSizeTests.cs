using System.Linq;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Artwork is requested at the size it is drawn: the TV decodes every picture on its main loop.
    public class ArtworkSizeTests
    {
        private const string Still = "https://episodes.metahub.space/tt0903747/1/2/w780.jpg";

        [Fact]
        public void StillsAreRequestedAtTheSmallestWidthThatCoversTheirSlot()
        {
            Assert.Equal("https://episodes.metahub.space/tt0903747/1/2/w185.jpg", UiFormat.ArtAtWidth(Still, 128));
            Assert.Equal("https://episodes.metahub.space/tt0903747/1/2/w300.jpg", UiFormat.ArtAtWidth(Still, 224));
            Assert.Equal("https://episodes.metahub.space/tt0903747/1/2/w500.jpg", UiFormat.ArtAtWidth(Still, 488));
            Assert.Equal(Still, UiFormat.ArtAtWidth(Still, 600));                       // the next-episode card keeps 780
            Assert.Equal("https://image.tmdb.org/t/p/w342/abc.jpg", UiFormat.ArtAtWidth("https://image.tmdb.org/t/p/w780/abc.jpg", 305));
            // Never upsized, and anything else is left alone.
            Assert.Equal("https://image.tmdb.org/t/p/w185/abc.jpg", UiFormat.ArtAtWidth("https://image.tmdb.org/t/p/w185/abc.jpg", 488));
            Assert.Equal("https://image.tmdb.org/t/p/original/abc.jpg", UiFormat.ArtAtWidth("https://image.tmdb.org/t/p/original/abc.jpg", 128));
            Assert.Equal("https://example.org/w780.jpg", UiFormat.ArtAtWidth("https://example.org/w780.jpg", 128));
            Assert.Equal("https://episodes.metahub.space/tt1/1/2/img", UiFormat.ArtAtWidth("https://episodes.metahub.space/tt1/1/2/img", 128));
            Assert.Null(UiFormat.ArtAtWidth(null, 128));
            Assert.Equal("https://image.tmdb.org/t/p/w342/p.jpg", UiFormat.CardPoster("https://image.tmdb.org/t/p/w500/p.jpg"));
        }

        [Fact]
        public void HomeCardsAndTheirPrefetchUseCardSizedStillsAndAPoppedCardSharpensThem()
        {
            var h = HomeRowsTests.Harness();
            var vm = new HomeViewModel();
            h.Shell.Start(vm);
            h.Wait(600);
            var cont = vm.Rows.First(r => r.Key == "continue");
            Assert.Equal("https://episodes.metahub.space/tt0903747/1/2/w780.jpg", cont.Items[0].Image);   // the data keeps the full-size still
            Assert.EndsWith("/w500.jpg", UiLayout.HomeCardArt(cont.Style, cont.Items[0]));
            var frame = UiLayout.Build(h.Shell);
            var art = frame.Nodes.First(n => n.Key == "row:continue:0:art");
            Assert.Equal(UiLayout.HomeCardArt(cont.Style, cont.Items[0]), art.ImageUrl);

            // Rest on the card until it pops: the grown card keeps the same file (no reload) and the full-size still
            // fades in over it.
            while (vm.FocusedRow?.Key != "continue")
                h.Press(RemoteKey.Down);
            h.Shell.Services.LastInteractionMs = h.Shell.NowMs;
            h.Wait(2500);
            Assert.NotNull(vm.PoppedTile);
            frame = UiLayout.Build(h.Shell);
            Assert.Equal(art.ImageUrl, frame.Nodes.First(n => n.Key == "row:continue:0:art").ImageUrl);
            Assert.EndsWith("/w780.jpg", frame.Nodes.First(n => n.Key == "row:continue:0:card-art").ImageUrl);
        }
    }
}
