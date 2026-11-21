using System;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// UI feedback batch 2026-11-19 19:20: Play and More info in the Home trailer view, as on Netflix.
    public class Batch192041Tests
    {
        private static (UiHarness H, HomeViewModel Home) TrailerView()
        {
            var h = new UiHarness();
            h.Shell.Services.TrailerCards = false;   // the billboard trailer view (the pop-out card's fallback mode)
            var extras = new FakeExtras();
            extras.Items[UiHarness.Series.Summary.Id] = new MetaExtras { Rating = "9.5", Runtime = "49 min", Trailers = new[] { "VFkjBy2b50Q" } };
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

        [Fact]
        public void TrailerViewShowsPlayAndMoreInfo()
        {
            var (h, home) = TrailerView();
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Key == "hero:tv:button:0:label" && n.Text == "Play");
            Assert.Contains(frame.Nodes, n => n.Key == "hero:tv:button:1:label" && n.Text == "More info");
            Assert.Equal(0, home.HeroButton);
            var strip = frame.Nodes.Where(n => n.Key.StartsWith("row:", StringComparison.Ordinal) && n.Kind == UiNodeKind.Image).Min(n => n.Y);
            Assert.True(frame.Nodes.Where(n => n.Key.StartsWith("hero:tv:button:", StringComparison.Ordinal)).Max(n => n.Y + n.H) < strip, "buttons clear the row strip");
        }

        [Fact]
        public void MoreInfoOpensTheTitlePage()
        {
            var (h, home) = TrailerView();
            h.Press(RemoteKey.Right);
            Assert.True(home.Immersive);   // moving between the buttons keeps the trailer
            Assert.Equal(1, home.HeroButton);
            h.Press(RemoteKey.Select);
            var details = Assert.IsType<DetailsViewModel>(h.Shell.Current);
            Assert.Equal("tt0903747", details.Id);
        }

        [Fact]
        public void PlayStartsPlayback()
        {
            var (h, _) = TrailerView();
            h.Press(RemoteKey.Select);
            Assert.Contains(h.Player.Commands, c => c.StartsWith("play-request", StringComparison.Ordinal));
        }

        [Fact]
        public void PlayPausePlaysTheTitleAndOtherKeysBringThePageBack()
        {
            var (h, home) = TrailerView();
            h.Press(RemoteKey.Left);   // left of Play: leaves the trailer view, focus stays
            Assert.False(home.Immersive);

            // Play/Pause always plays the focused title, also from the trailer view: it never toggles preview sound.
            (h, home) = TrailerView();
            var muted = home.TrailerMuted;
            h.Press(RemoteKey.PlayPause);
            Assert.Equal(muted, home.TrailerMuted);
            Assert.Contains(h.Player.Commands, c => c.StartsWith("play-request", StringComparison.Ordinal));
        }
    }
}
