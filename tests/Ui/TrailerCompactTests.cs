using System;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Netflix trailer view: ~3 s after the trailer's first frames the facts fade out and the logo shrinks to the
    /// bottom-left (only logo + buttons stay); the end of the trailer or a key restores the billboard.
    public class TrailerCompactTests
    {
        private static (UiHarness H, HomeViewModel Home) Trailer()
        {
            var h = new UiHarness();
            h.Shell.Services.TrailerCards = false;   // the billboard trailer view (the pop-out card's fallback mode)
            var extras = new FakeExtras();
            extras.Items[UiHarness.Series.Summary.Id] = new MetaExtras { Rating = "9.5", Runtime = "49 min", Logo = "https://logo/bb.png", Trailers = new[] { "VFkjBy2b50Q" } };
            h.Shell.Services.Extras = extras;
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            var home = new HomeViewModel();
            h.Shell.Start(home);
            h.Shell.Services.LastInteractionMs = 0; h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;
            h.Wait(HomeViewModel.TrailerDelayMs + 600);
            Assert.True(home.Immersive);
            return (h, home);
        }

        private static UiNode Logo(UiFrame f) => f.Nodes.Single(n => n.Key.StartsWith("hero:tv:logo:", StringComparison.Ordinal));

        [Fact]
        public void CompactsOnlyAfterTheTrailerActuallyStarts()
        {
            var (h, home) = Trailer();
            h.Wait(UiLayout.TrailerCompactDelayMs + 500);
            Assert.False(home.TrailerCompact);   // no frames reported yet: the billboard stays complete
            var full = UiLayout.Build(h.Shell);
            Assert.Contains(full.Nodes, n => n.Key == "hero:tv:facts");

            h.Shell.Services.ReportTrailerStarted(home.TrailerId);
            h.Wait(UiLayout.TrailerCompactDelayMs - 500);
            Assert.False(home.TrailerCompact);
            h.Wait(1000);
            Assert.True(home.TrailerCompact);
            var compact = UiLayout.Build(h.Shell);
            Assert.DoesNotContain(compact.Nodes, n => n.Key == "hero:tv:facts");
            Assert.Contains(compact.Nodes, n => n.Key == "hero:tv:button:0:label");
            var (a, b) = (Logo(full), Logo(compact));
            Assert.Equal(a.ImageUrl, b.ImageUrl);                // same image node glides: no reload
            Assert.InRange(b.W, (int)(a.W * 0.55), (int)(a.W * 0.65));
            Assert.True(b.Y > a.Y, "the logo slides down");
            var scrim = compact.Nodes.Single(n => n.Key == "hero:fade-left");
            Assert.True(scrim.Opacity < 1, "the scrim lightens");
        }

        [Fact]
        public void AKeyOrTheEndRestoresTheBillboard()
        {
            var (h, home) = Trailer();
            h.Shell.Services.ReportTrailerStarted(home.TrailerId);
            h.Wait(UiLayout.TrailerCompactDelayMs + 200);
            Assert.True(home.TrailerCompact);
            h.Press(RemoteKey.Right);                 // between the buttons: stays compact
            Assert.True(home.TrailerCompact);
            h.Shell.Services.ReportTrailerEnded(home.TrailerId);
            h.Wait(100);
            Assert.False(home.Immersive);
            Assert.False(home.TrailerCompact);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "hero:facts");
        }

        [Fact]
        public void DetailsTrailerViewCompactsToo()
        {
            var h = new UiHarness();
            var extras = new FakeExtras();
            extras.Items[UiHarness.Series.Summary.Id] = new MetaExtras { Logo = "https://logo/bb.png", Trailers = new[] { "VFkjBy2b50Q" } };
            h.Shell.Services.Extras = extras;
            h.Shell.Start(new HomeViewModel());
            var details = new DetailsViewModel(MediaType.Series, "tt0903747");
            h.Shell.Push(details);
            while (details.Actions[details.ActionIndex].Action != DetailsAction.Trailer) h.Press(RemoteKey.Right);
            h.Press(RemoteKey.Select);
            Assert.True(details.Immersive);
            var full = UiLayout.Build(h.Shell);
            h.Shell.Services.ReportTrailerStarted(details.TrailerId);
            h.Wait(UiLayout.TrailerCompactDelayMs + 200);
            var compact = UiLayout.Build(h.Shell);
            Assert.Contains(full.Nodes, n => n.Key == "details:facts");
            Assert.DoesNotContain(compact.Nodes, n => n.Key == "details:facts");
            var logo = compact.Nodes.Single(n => n.Key == "details:logo");
            Assert.True(logo.Y + logo.H <= UiScale.Bottom && logo.Y > full.Nodes.Single(n => n.Key == "details:logo").Y);
        }
    }
}
