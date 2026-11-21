using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// UI feedback batch 2026-11-19 19:08: larger title facts, open-ended Browse paging, logo trimming.
    public class Batch190800Tests
    {
        // ---- Browse pages on demand ----

        /// A deep catalog: `total` titles served PageSize at a time, like Cinemeta's `top`.
        private sealed class DeepSource : IBrowseSource
        {
            private readonly int total;
            public readonly List<int> Skips = new List<int>();
            public DeepSource(int total) => this.total = total;

            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
            {
                Skips.Add(skip);
                var items = Enumerable.Range(skip, Math.Max(0, Math.Min(BrowseViewModel.PageSize, total - skip))).Select(i => new BrowseItem
                {
                    Meta = new MetaSummary { Id = "tt" + i, Name = "Title " + i, Type = type, ReleaseInfo = i % 2 == 0 ? "2021" : "1990" },
                    Extras = new MetaExtras { Rating = i % 10 == 0 ? "8.5" : "6.0", Runtime = "50 min" }
                }).ToList();
                return Task.FromResult<IReadOnlyList<BrowseItem>>(items);
            }
        }

        private static (UiHarness H, DeepSource S, BrowseViewModel Vm) Open(int total)
        {
            var h = new UiHarness();
            var source = new DeepSource(total);
            h.Shell.Services.Browse = source;
            h.Shell.Start(new HomeViewModel());
            var vm = new BrowseViewModel();
            h.Shell.Push(vm);
            return (h, source, vm);
        }

        [Fact]
        public void BrowseKeepsPagingPastTheFirstHundred()
        {
            var (h, source, vm) = Open(1000);
            Assert.Equal(100, vm.Results.Count);
            Assert.Contains("100+ titles", h.Text);
            h.Press(RemoteKey.Down);
            for (var i = 0; i < 40; i++)
                h.Press(RemoteKey.Down);
            Assert.True(vm.Results.Count > 200, "paged to " + vm.Results.Count);
            Assert.Equal(source.Skips.Distinct().Count(), source.Skips.Count);   // every page fetched once
            Assert.True(vm.Index / BrowseViewModel.Columns + BrowseViewModel.PrefetchRows < (vm.Results.Count + 4) / 5);   // always rows ahead
        }

        [Fact]
        public void BrowseStopsWhenTheCatalogRunsOut()
        {
            var (h, source, vm) = Open(128);
            h.Press(RemoteKey.Down, RemoteKey.Right, RemoteKey.Right, RemoteKey.Right, RemoteKey.Right);
            for (var i = 0; i < 40; i++)
                h.Press(RemoteKey.Down);
            Assert.Equal(128, vm.Results.Count);
            Assert.True(vm.Exhausted);
            Assert.Contains(" of 128\n", h.Text);          // "Title 121 of 128": where the viewer is in the catalogue
            Assert.DoesNotContain("128+", h.Text);
            var calls = source.Skips.Count;
            h.Press(RemoteKey.Down, RemoteKey.Down);
            Assert.Equal(calls, source.Skips.Count);
            Assert.Equal(127, vm.Index);   // Down from the last full row lands on the last, shorter row
        }

        [Fact]
        public void NarrowLocalFilterFetchesABoundedNumberOfPages()
        {
            var (h, source, vm) = Open(100_000);
            while (BrowseViewModel.Filters[vm.FilterIndex] != BrowseFilter.Rating) h.Press(RemoteKey.Right);
            h.PickFilter(2);   // 8+: one title in ten
            Assert.Equal("8+", vm.Value(BrowseFilter.Rating));
            Assert.True(source.Skips.Count <= BrowseViewModel.Pages + 2 * BrowseViewModel.MaxPagesPerFill, source.Skips.Count + " pages");
            Assert.True(vm.Results.Count > 10);
            Assert.All(vm.Results, r => Assert.Equal("8.5", r.Extras.Rating));
        }

        [Fact]
        public void VeryNarrowFilterKeepsPagingOnEachDown()
        {
            var h = new UiHarness();
            var source = new SparseSource();
            h.Shell.Services.Browse = source;
            h.Shell.Start(new HomeViewModel());
            var vm = new BrowseViewModel(new Dictionary<BrowseFilter, string> { [BrowseFilter.Rating] = "8+" });
            h.Shell.Push(vm);
            var before = source.Skips.Count;
            h.Press(RemoteKey.Down);
            var after = source.Skips.Count;
            Assert.True(after > before, "Down fetches more pages");
            h.Press(RemoteKey.Down);
            Assert.True(source.Skips.Count > after, "and the next Down keeps going");
            source.Fail = true;
            h.Press(RemoteKey.Down);
            Assert.False(vm.Exhausted);   // a failed page does not end exploring
            source.Fail = false;
            var count = source.Skips.Count;
            h.Press(RemoteKey.Down);
            Assert.True(source.Skips.Count > count);
        }

        /// One title in 200 rates 8+; can fail on demand.
        private sealed class SparseSource : IBrowseSource
        {
            public readonly List<int> Skips = new List<int>();
            public bool Fail;

            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
            {
                Skips.Add(skip);
                if (Fail) return Task.FromException<IReadOnlyList<BrowseItem>>(new HttpRequestExceptionStub());
                return Task.FromResult<IReadOnlyList<BrowseItem>>(Enumerable.Range(skip, BrowseViewModel.PageSize).Select(i => new BrowseItem
                {
                    Meta = new MetaSummary { Id = "tt" + i, Name = "Title " + i, Type = type, ReleaseInfo = "2021" },
                    Extras = new MetaExtras { Rating = i % 200 == 0 ? "8.5" : "6.0" }
                }).ToList());
            }
        }

        private sealed class HttpRequestExceptionStub : Exception { }

        // ---- title facts read from ~3 m ----

        [Fact]
        public void HeroAndDetailsFactsUseTheLargerFactsSize()
        {
            var h = new UiHarness();
            h.Shell.Services.Extras = new FakeExtras { Items = { ["tt0903747"] = new MetaExtras { Rating = "9.5", Runtime = "49 min", Logo = "https://logo/bb.png", Description = "Chemistry." } } };
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            h.Wait(1000);
            var frame = UiLayout.Build(h.Shell);
            var facts = frame.Nodes.Single(n => n.Key == "hero:facts");
            Assert.Equal(UiScale.Facts, facts.FontSize);
            Assert.True(UiScale.Facts > UiScale.Secondary);
            // The facts line sits right under the logo box (renderers put the logo's visible bottom on that edge).
            var logo = frame.Nodes.Single(n => n.Key.StartsWith("hero:logo:", StringComparison.Ordinal));
            Assert.True(logo.AnchorBottom && logo.Contain);
            Assert.InRange(facts.Y - (logo.Y + logo.H), 0, 10);

            h.Shell.Push(new DetailsViewModel(MediaType.Series, "tt0903747"));
            frame = UiLayout.Build(h.Shell);
            Assert.Equal(UiScale.Facts, frame.Nodes.Single(n => n.Key == "details:facts").FontSize);
            Assert.True(frame.Nodes.Single(n => n.Key == "details:logo").AnchorBottom);
        }

        // ---- logos: transparent margins trimmed ----

        [Fact]
        public void OpaqueBoundsFindsTheLogoInsideItsCanvas()
        {
            // 80x31 RGBA canvas (metahub's 800x310, scaled down) with the artwork at x 1..78, y 11..19.
            var png = Png(80, 31, (x, y) => x >= 1 && x <= 78 && y >= 11 && y <= 19 ? (byte)255 : (byte)0);
            var bounds = ImageTrim.OpaqueBounds(new MemoryStream(png));
            Assert.NotNull(bounds);
            Assert.Equal(new PixelRect(1, 11, 78, 9).ToString(), bounds.Value.ToString());
            // A faint fringe stays transparent; a full-bleed or non-PNG picture is left as it is.
            Assert.Equal(bounds.Value.ToString(), ImageTrim.OpaqueBounds(new MemoryStream(Png(80, 31, (x, y) => x >= 1 && x <= 78 && y >= 11 && y <= 19 ? (byte)255 : (byte)10))).Value.ToString());
            Assert.Null(ImageTrim.OpaqueBounds(new MemoryStream(Png(8, 8, (x, y) => 255))));
            Assert.Null(ImageTrim.OpaqueBounds(new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 })));
        }

        [Fact]
        public void FitPutsTheOpaquePartOnTheBoxBottomLeft()
        {
            // The Rainmaker: 800x310 canvas, artwork 784x84 at (8, 113); box 480x140 at (96, 136).
            var fit = ImageTrim.Fit(96, 136, 480, 140, 800, 310, new PixelRect(8, 113, 784, 84), anchorBottom: true);
            var scale = 480.0 / 784;
            Assert.Equal(96, fit.X + (int)Math.Round(8 * scale));                         // artwork starts at the box's left edge
            Assert.InRange(fit.Y + (113 + 84) * scale, 136 + 140 - 1, 136 + 140 + 1);    // and ends on its bottom edge
            Assert.Equal((int)Math.Round(800 * scale), fit.W);
            // Without bounds it is a plain bottom-left contain fit.
            var whole = ImageTrim.Fit(96, 136, 480, 140, 800, 310, null, anchorBottom: true);
            Assert.Equal((96, 136, 361, 140), whole);
        }

        /// Minimal RGBA PNG (filter 0, one IDAT); white pixels with the given alpha. CRCs are not checked by the reader.
        private static byte[] Png(int w, int h, Func<int, int, byte> alpha)
        {
            var raw = new MemoryStream();
            for (var y = 0; y < h; y++)
            {
                raw.WriteByte(y % 2 == 0 ? (byte)0 : (byte)2);   // alternate None and Up filters
                for (var x = 0; x < w; x++)
                {
                    var a = alpha(x, y);
                    var above = y > 0 ? alpha(x, y - 1) : (byte)0;
                    if (y % 2 == 0)
                        raw.Write(new byte[] { 255, 255, 255, a }, 0, 4);
                    else
                        raw.Write(new byte[] { 0, 0, 0, (byte)(a - above) }, 0, 4);   // Up: deltas against the row above
                }
            }
            var z = new MemoryStream();
            z.WriteByte(0x78); z.WriteByte(0x9C);
            using (var d = new DeflateStream(z, CompressionMode.Compress, leaveOpen: true))
                d.Write(raw.ToArray(), 0, (int)raw.Length);
            z.Write(new byte[4], 0, 4);   // Adler-32 (unchecked)
            var png = new MemoryStream();
            png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }, 0, 8);
            void Chunk(string type, byte[] data)
            {
                png.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length }, 0, 4);
                png.Write(System.Text.Encoding.ASCII.GetBytes(type), 0, 4);
                png.Write(data, 0, data.Length);
                png.Write(new byte[4], 0, 4);
            }
            Chunk("IHDR", new byte[] { (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0 });
            Chunk("IDAT", z.ToArray());
            Chunk("IEND", Array.Empty<byte>());
            return png.ToArray();
        }
    }
}
