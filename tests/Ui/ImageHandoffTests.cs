using System.Collections.Generic;
using System.Linq;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class ImageHandoffTests
    {
        private sealed class Pic
        {
            public string Name;
            public long Pixels;
            public bool Visible, Stale;
            public override string ToString() => Name;
        }

        private static Pic P(string name, long pixels = 130_000, bool visible = true, bool stale = false) =>
            new Pic { Name = name, Pixels = pixels, Visible = visible, Stale = stale };

        private static List<string> Pump(ImageHandoff<Pic> queue, long budget, out bool more, List<string> dropped = null)
        {
            var applied = new List<string>();
            more = queue.Pump(p => p.Stale, p => p.Visible, p => { applied.Add(p.Name); return p.Pixels; }, budget, p => dropped?.Add(p.Name));
            return applied;
        }

        [Fact]
        public void ABurstIsSpreadOverFramesWithinThePixelBudget()
        {
            var queue = new ImageHandoff<Pic>();
            Assert.True(queue.Add(P("a")));    // idle: the caller arms a pump
            for (var i = 0; i < 7; i++)
                Assert.False(queue.Add(P("c" + i)));   // a pump is pending already
            var first = Pump(queue, ImageHandoff<Pic>.NavigatingPixels, out var more);
            Assert.Equal(new[] { "a", "c0", "c1" }, first);   // ~0.35 MP: three card-sized pictures
            Assert.True(more);
            Assert.Equal(5, queue.Count);
            Pump(queue, ImageHandoff<Pic>.NavigatingPixels, out more);
            Pump(queue, ImageHandoff<Pic>.NavigatingPixels, out more);
            Assert.False(more);
            Assert.Equal(0, queue.Count);
            Assert.True(queue.Add(P("later")));   // idle again: a new pump is needed
        }

        [Fact]
        public void VisibleArtGoesFirstAndABackdropIsNeverStarved()
        {
            var queue = new ImageHandoff<Pic>();
            queue.Add(P("offscreen1", visible: false));
            queue.Add(P("offscreen2", visible: false));
            queue.Add(P("backdrop", 2_073_600));
            queue.Add(P("card"));
            var first = Pump(queue, ImageHandoff<Pic>.NavigatingPixels, out var more);
            Assert.Equal(new[] { "backdrop" }, first);   // one over-budget picture still goes alone
            Assert.True(more);
            var second = Pump(queue, ImageHandoff<Pic>.NavigatingPixels, out more);
            Assert.Equal(new[] { "card", "offscreen1", "offscreen2" }, second);
            Assert.False(more);
        }

        [Fact]
        public void StaleRequestsAreDroppedWithoutSpendingTheBudget()
        {
            var queue = new ImageHandoff<Pic>();
            var dropped = new List<string>();
            for (var i = 0; i < 5; i++)
                queue.Add(P("old" + i, stale: true));
            queue.Add(P("a"));
            queue.Add(P("b"));
            var applied = Pump(queue, 200_000, out var more, dropped);
            Assert.Equal(new[] { "a", "b" }, applied);
            Assert.Equal(5, dropped.Count);
            Assert.False(more);
        }

        [Fact]
        public void APictureThatThrowsLosesOnlyItselfAndTheQueueKeepsGoing()
        {
            var queue = new ImageHandoff<Pic>();
            queue.Add(P("bad"));
            queue.Add(P("next"));
            Assert.ThrowsAny<System.Exception>(() => queue.Pump(p => false, p => true, p => { if (p.Name == "bad") throw new System.InvalidOperationException(); return p.Pixels; }, long.MaxValue));
            Assert.Equal(1, queue.Count);
            Assert.True(queue.Add(P("later")));   // idle after the failure: the next picture arms a new pump
            var applied = Pump(queue, long.MaxValue, out var more);
            Assert.Equal(new[] { "next", "later" }, applied);
            Assert.False(more);
        }

        [Fact]
        public void PicturesArrivingDuringAPumpWaitBehindTheOnesAlreadyQueued()
        {
            var queue = new ImageHandoff<Pic>();
            queue.Add(P("a", 400_000));
            queue.Add(P("b", 400_000));
            var applied = new List<string>();
            var more = queue.Pump(p => false, p => true, p =>
            {
                applied.Add(p.Name);
                Assert.False(queue.Add(P("new")));   // a pump is running: no second pump is armed
                return p.Pixels;
            }, 350_000);
            Assert.True(more);
            Assert.Equal(new[] { "a" }, applied);
            applied.Clear();
            queue.Pump(p => false, p => true, p => { applied.Add(p.Name); return p.Pixels; }, long.MaxValue);
            Assert.Equal(new[] { "b", "new" }, applied);
        }
    }
}
