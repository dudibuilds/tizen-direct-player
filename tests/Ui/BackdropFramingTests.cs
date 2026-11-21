using System;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Hero and title-page backdrops crop from the bottom (faces sit in the upper part of key art), in both renderers.
    public class BackdropFramingTests
    {
        [Fact]
        public void HeroAndDetailsBackdropsKeepTheTop()
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            h.Wait(1000);
            var art = UiLayout.Build(h.Shell).Nodes.First(n => n.Key.StartsWith("hero:bg:", StringComparison.Ordinal));
            Assert.True(art.CropY < 0.25, "crop keeps the top");
            Assert.False(art.Contain);   // still a cover crop: no letterbox, no distortion
            Assert.Contains("object-position:60% 12%", UiHtml.RenderStage(UiLayout.Build(h.Shell)));

            h.Shell.Push(new DetailsViewModel(MediaType.Series, "tt0903747"));
            var details = UiLayout.Build(h.Shell).Nodes.First(n => n.Key.StartsWith("hero:bg:", StringComparison.Ordinal));
            Assert.Equal(art.CropY, details.CropY);
        }
    }
}
