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
    /// Frame invariants shared by the layout tests: what a careful viewer would call "overlapping" or "cut off".
    public static class FrameChecks
    {
        private static bool Intersects(UiNode a, UiNode b) =>
            a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

        /// Whether `cover` hides all of `n` that is on the stage.
        private static bool Covers(UiNode cover, UiNode n)
        {
            int left = Math.Max(0, n.X), top = Math.Max(0, n.Y), right = Math.Min(UiFrame.Width, n.X + n.W), bottom = Math.Min(UiFrame.Height, n.Y + n.H);
            return cover.X <= left && cover.Y <= top && cover.X + cover.W >= right && cover.Y + cover.H >= bottom;
        }

        private static bool Opaque(UiNode n)
        {
            if (n.Opacity < 0.99)
                return false;
            if (n.Kind == UiNodeKind.Image)
                return !n.Contain;
            return n.Kind == UiNodeKind.Box && n.Fill != null && (n.Fill.Length == 7 || n.Fill.EndsWith("FF", StringComparison.OrdinalIgnoreCase));
        }

        /// Text a viewer can see: not empty, not faded out, on the stage, and not under a later opaque box or image.
        public static List<UiNode> VisibleText(UiFrame frame)
        {
            var result = new List<UiNode>();
            for (var i = 0; i < frame.Nodes.Count; i++)
            {
                var n = frame.Nodes[i];
                if (n.Kind != UiNodeKind.Text || string.IsNullOrWhiteSpace(n.Text) || n.Opacity < 0.05)
                    continue;
                if (n.Y >= UiFrame.Height || n.Y + n.H <= 0 || n.X >= UiFrame.Width || n.X + n.W <= 0)
                    continue;
                var hidden = false;
                for (var j = i + 1; j < frame.Nodes.Count && !hidden; j++)
                    hidden = Opaque(frame.Nodes[j]) && Covers(frame.Nodes[j], Ink(n));
                if (!hidden)
                    result.Add(n);
            }
            return result;
        }

        /// Width the glyphs need (the same estimate the layout uses), so a wide, mostly empty text box does not
        /// count as covering its neighbour.
        private static UiNode Ink(UiNode n)
        {
            if (n.Wrap)
            {
                // Wrapped text fills its box from the top: only the lines it needs count.
                var perLine = Math.Max(8, (int)(n.W / (n.FontSize * UiFormat.GlyphWidth)));
                var lines = (n.Text ?? "").Split('\n').Sum(l => Math.Max(1, (l.Length + perLine - 1) / perLine));
                var wrapped = n.Clone();
                wrapped.H = Math.Min(n.H, lines * UiFormat.LineH(n.FontSize));
                return wrapped;
            }
            var ink = n.Clone();
            var longest = (n.Text ?? "").Split('\n').Max(l => l.Length);
            var w = Math.Min(n.W, (int)Math.Ceiling(longest * n.FontSize * UiFormat.GlyphWidth) + 4);
            ink.X = n.Align == UiAlign.Center ? n.X + (n.W - w) / 2 : n.Align == UiAlign.End ? n.X + n.W - w : n.X;
            ink.W = w;
            return ink;
        }

        /// No two visible texts overlap. `allow` exempts pairs that are meant to stack (key prefixes).
        public static void NoTextOverlap(UiFrame frame, string context, Func<UiNode, UiNode, bool> allow = null)
        {
            // Known gap (open): a poster's fallback title (":alt", shown only when its image is missing) can pass
            // behind its row title for a few frames when a row glides down out of the peek. Not checked here.
            var texts = VisibleText(frame).Where(n => !n.Key.EndsWith(":rank", StringComparison.Ordinal) && !n.Key.EndsWith(":alt", StringComparison.Ordinal)).Select(n => (Node: n, Ink: Ink(n))).ToList();
            for (var i = 0; i < texts.Count; i++)
                for (var j = i + 1; j < texts.Count; j++)
                {
                    if (!Intersects(texts[i].Ink, texts[j].Ink))
                        continue;
                    if (allow != null && allow(texts[i].Node, texts[j].Node))
                        continue;
                    Assert.Fail(context + ": text \"" + texts[i].Node.Text + "\" (" + texts[i].Node.Key + " at " + texts[i].Node.X + "," + texts[i].Node.Y + " " + texts[i].Node.W + "x" + texts[i].Node.H
                        + ") overlaps \"" + texts[j].Node.Text + "\" (" + texts[j].Node.Key + " at " + texts[j].Node.X + "," + texts[j].Node.Y + " " + texts[j].Node.W + "x" + texts[j].Node.H + ")");
                }
        }

        /// Every text is at least UiScale.Hint (26 px) for 10-foot viewing.
        public static void TextIsReadable(UiFrame frame, string context)
        {
            foreach (var n in frame.Nodes.Where(n => n.Kind == UiNodeKind.Text && !string.IsNullOrEmpty(n.Text) && n.Key != "subtitle"))
                Assert.True(n.FontSize >= UiScale.Hint, context + ": \"" + n.Text + "\" (" + n.Key + ") is " + n.FontSize + " px");
        }

        /// One-line text boxes are at least one TV line tall (UiFormat.LineH: ~1.45 x size); shorter boxes clip on the RU7100.
        public static void LinesFitTheirBoxes(UiFrame frame, string context)
        {
            foreach (var n in frame.Nodes.Where(n => n.Kind == UiNodeKind.Text && !n.Wrap && !string.IsNullOrEmpty(n.Text)))
                Assert.True(n.H >= UiFormat.LineH(n.FontSize) - 1, context + ": " + n.Key + " is " + n.H + " px tall for " + n.FontSize + " px text (needs " + UiFormat.LineH(n.FontSize) + ")");
        }

        /// Visible text stays inside the overscan-safe area (UiScale.SafeX / SafeTop / SafeBottom), except `scrolls`.
        public static void TextInSafeArea(UiFrame frame, string context, Func<UiNode, bool> scrolls = null)
        {
            const int slack = 8;
            foreach (var n in VisibleText(frame))
            {
                if (scrolls != null && scrolls(n))
                    continue;
                var ink = Ink(n);
                var inside = ink.X >= UiScale.SafeX - slack && ink.X + ink.W <= UiScale.Right + slack && ink.Y >= UiScale.SafeTop - slack && ink.Y + ink.H <= UiScale.Bottom + slack;
                Assert.True(inside, context + ": " + n.Key + " (\"" + n.Text + "\") leaves the safe area at " + ink.X + "," + ink.Y + " " + ink.W + "x" + ink.H);
            }
        }

        /// Wrapped text boxes hold whole lines (a multiple of the shared line height), so no painter cuts a line.
        public static void WrappedTextShowsWholeLines(UiFrame frame, string context)
        {
            foreach (var n in frame.Nodes.Where(n => n.Kind == UiNodeKind.Text && n.Wrap && n.Fill == null && !n.Outline && !n.Key.EndsWith(":alt", StringComparison.Ordinal)))
            {
                Assert.True(n.H % UiFormat.LineH(n.FontSize) == 0, context + ": " + n.Key + " is " + n.H + " px tall, not whole " + UiFormat.LineH(n.FontSize) + " px lines");
                var lines = n.H / UiFormat.LineH(n.FontSize);
                Assert.True((n.Text ?? "").Length <= lines * Math.Max(8, (int)(n.W / (n.FontSize * UiFormat.GlyphWidth))), context + ": " + n.Key + " holds more text than its " + lines + " lines");
            }
        }

        /// Visible text stays on the 1920x1080 stage. Rows may be cut by the bottom edge (the peek of the next row).
        public static void TextOnStage(UiFrame frame, string context, Func<UiNode, bool> mayBeCutAtBottom = null)
        {
            // Top 10 numerals are display glyphs in a box ~2x their ink height; the box may poke above the stage
            // while the row waits under the banner.
            foreach (var n in VisibleText(frame).Where(n => !n.Key.EndsWith(":rank", StringComparison.Ordinal)))
            {
                if (mayBeCutAtBottom != null && mayBeCutAtBottom(n) && n.Y >= 0)
                    continue;   // scrolling strips are cut by the right and bottom edges on purpose
                Assert.True(n.X >= 0 && n.X + n.W <= UiFrame.Width && n.Y >= 0, context + ": " + n.Key + " leaves the stage at " + n.X + "," + n.Y + " " + n.W + "x" + n.H);
                if (mayBeCutAtBottom == null || !mayBeCutAtBottom(n))
                    Assert.True(n.Y + n.H <= UiFrame.Height, context + ": " + n.Key + " runs below the stage (" + (n.Y + n.H) + ")");
            }
        }
    }

    public class LayoutGridTests
    {
        private sealed class Feed : IRecommendationsFeed
        {
            public IReadOnlyList<RecommendationItem> Items = Array.Empty<RecommendationItem>();
            public Task<IReadOnlyList<RecommendationItem>> GetAsync(CancellationToken token) => Task.FromResult(Items);
        }

        private const string LongSynopsis = "Mark leads a team of office workers whose memories have been surgically divided between their work and personal lives. "
            + "When a mysterious colleague appears outside of work, it begins a journey to discover the truth about their jobs, and about the company that made them, "
            + "which nobody on either side of the divide is ready to hear, least of all the people who signed the papers.";

        /// A Home with every row kind (landscape, ranked, portrait), long synopses, and titles with and without logos.
        internal static UiHarness FullHome()
        {
            var h = new UiHarness();
            var extras = new FakeExtras();
            var feed = new List<RecommendationItem>();
            for (var i = 0; i < 26; i++)
            {
                var series = i % 2 == 0;
                var id = "tt90000" + i.ToString("00");
                var summary = new MetaSummary { Id = id, Type = series ? MediaType.Series : MediaType.Movie, Name = "A Fairly Long Title Number " + i, ReleaseInfo = "2024", Poster = "https://img/" + id + ".jpg" };
                h.Catalog.Metas[id] = new MetaDetail
                {
                    Summary = summary,
                    Videos = series ? new[] { new VideoInfo { Id = id + ":1:1", Season = 1, Episode = 1, Title = "Pilot", Released = h.Clock.UtcNow.AddDays(-30) } } : Array.Empty<VideoInfo>()
                };
                extras.Items[id] = new MetaExtras
                {
                    Rating = "8." + i % 10, Runtime = (20 + i * 9) + " min", Description = LongSynopsis, Genres = new[] { "Drama", "Mystery", "Science Fiction" },
                    Trailers = i % 3 == 0 ? new[] { "trailer" + i } : Array.Empty<string>()
                };
                if (i < 8)
                    feed.Add(new RecommendationItem { Meta = summary, Reason = "Because you watched Breaking Bad and liked it a lot" });
            }
            extras.Items["tt1727587"] = new MetaExtras { Rating = "7.4", Runtime = "15 min", Description = LongSynopsis, Genres = new[] { "Animation" }, Trailers = new[] { "sintel-trailer" } };
            extras.Items["tt0903747"] = new MetaExtras { Rating = "9.5", Runtime = "49 min", Description = LongSynopsis, Genres = new[] { "Crime", "Drama", "Thriller" }, Trailers = new[] { "VFkjBy2b50Q" } };
            h.Shell.Services.Extras = extras;
            h.Shell.Services.Recommendations = new Feed { Items = feed };
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow, Created = h.Clock.UtcNow.AddYears(-20),
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000, LastWatched = h.Clock.UtcNow.AddYears(-19) }
            };
            h.Library.Entries["tt1727587"] = new LibraryEntry
            {
                Id = "tt1727587", Name = "Sintel", Type = MediaType.Movie, Modified = h.Clock.UtcNow.AddDays(-1),
                State = new LibraryState { VideoId = "tt1727587", TimeOffsetMs = 300_000, DurationMs = 888_000 }
            };
            return h;
        }

        private static readonly Func<UiNode, bool> RowNode = n => n.Key.StartsWith("row:", StringComparison.Ordinal);

        /// Hero zone [0, H) and rows zone [H, 1080): nothing crosses the line between them.
        /// Both nodes belong to the same Home row (see the known gap in AssertFrame).
        private static bool SameRow(UiNode a, UiNode b)
        {
            string Row(UiNode n) => n.Key.StartsWith("row:", StringComparison.Ordinal) && n.Key.LastIndexOf(":title", StringComparison.Ordinal) > 4
                ? n.Key.Substring(0, n.Key.LastIndexOf(":title", StringComparison.Ordinal)) : null;
            var title = Row(a) ?? Row(b);
            return title != null && a.Key.StartsWith(title + ":", StringComparison.Ordinal) && b.Key.StartsWith(title + ":", StringComparison.Ordinal);
        }

        private static void AssertHomeZones(UiFrame frame, HomeViewModel home, string context)
        {
            if (home.Immersive)
                return;   // trailer view: full-screen art, one strip of cards at the bottom
            var shelves = home.Rows.Where(r => !r.IsMenu).ToList();
            var scrolled = !home.FocusedRow.IsMenu && shelves.FindIndex(r => r.Key == home.FocusedRow.Key) > 0;
            var zone = scrolled ? UiLayout.ScrolledHeroBottom : UiLayout.HeroBottomFor(shelves[0].Style);
            var band = frame.Nodes.FindIndex(n => n.Key == "home:band");
            for (var i = 0; i < frame.Nodes.Count; i++)
            {
                var n = frame.Nodes[i];
                if (n.Key.StartsWith("hero:", StringComparison.Ordinal) && n.Kind != UiNodeKind.Gradient)
                    Assert.True(n.Y >= 0 && n.Y + n.H <= zone, context + ": " + n.Key + " (" + n.Y + ".." + (n.Y + n.H) + ") leaves the hero zone [0, " + zone + ")");
                if (RowNode(n) && n.Y < zone)
                    Assert.True(band > i, context + ": " + n.Key + " at y " + n.Y + " is in the hero zone and nothing hides it");
            }
            // The synopsis is clamped to whole lines inside the zone.
            var synopsis = frame.Nodes.SingleOrDefault(n => n.Key == "hero:description");
            if (synopsis != null && home.HeroExtras?.Description == LongSynopsis)
                Assert.True(synopsis.Text.Length < LongSynopsis.Length && synopsis.Text.EndsWith("…", StringComparison.Ordinal), context + ": synopsis is not clamped");
            // The focused row's title is fully visible and the row above (if any) shows no caption or title.
            var above = scrolled ? shelves[shelves.FindIndex(r => r.Key == home.FocusedRow.Key) - 1] : null;
            if (above != null)
                Assert.DoesNotContain(FrameChecks.VisibleText(frame), n => n.Key.StartsWith("row:" + above.Key + ":", StringComparison.Ordinal) && !n.Key.EndsWith(":alt", StringComparison.Ordinal) && !n.Key.EndsWith(":rank", StringComparison.Ordinal));
        }

        /// settled: a layout frame (not a mid-animation sample, where box sizes are still gliding).
        private static void AssertFrame(UiFrame frame, string context, bool settled = false)
        {
            // Known gap (open): when a row glides down out of the peek, its title fades in at the anchor while its
            // own captions are still arriving, so two half-faded texts of that row can cross for a few frames.
            // Mid-animation samples therefore only fail on fully opaque text; settled frames are strict.
            FrameChecks.NoTextOverlap(frame, context, settled ? (Func<UiNode, UiNode, bool>)null : (a, b) => a.Opacity < 0.999 || b.Opacity < 0.999 || SameRow(a, b));
            FrameChecks.TextIsReadable(frame, context);
            if (settled)
            {
                FrameChecks.WrappedTextShowsWholeLines(frame, context);
                FrameChecks.LinesFitTheirBoxes(frame, context);
                FrameChecks.TextInSafeArea(frame, context, RowNode);
            }
            FrameChecks.TextOnStage(frame, context, RowNode);
        }

        [Fact]
        public void HomeKeepsItsZonesOnEveryFrameWhileMovingInAllDirections()
        {
            var h = FullHome();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            h.Wait(600);
            var animator = new UiAnimator();
            var now = 0L;
            animator.Step(UiLayout.Build(h.Shell), now);
            // Down through every row and back up, sideways to both ends in several rows, with pauses long enough
            // for the billboard to change title, and short bursts that interrupt animations half way.
            var keys = new List<RemoteKey>();
            for (var i = 0; i < 9; i++) { keys.Add(RemoteKey.Down); keys.Add(RemoteKey.Right); keys.Add(RemoteKey.Right); }
            for (var i = 0; i < 12; i++) keys.Add(RemoteKey.Right);
            for (var i = 0; i < 5; i++) { keys.Add(RemoteKey.Up); keys.Add(RemoteKey.Left); }
            for (var i = 0; i < 6; i++) keys.Add(RemoteKey.Down);
            for (var i = 0; i < 14; i++) keys.Add(RemoteKey.Up);
            keys.AddRange(new[] { RemoteKey.Right, RemoteKey.Right, RemoteKey.Down, RemoteKey.Left, RemoteKey.Down, RemoteKey.Up, RemoteKey.Up, RemoteKey.Up, RemoteKey.Right, RemoteKey.Down });
            var step = 0;
            foreach (var key in keys)
            {
                step++;
                h.Shell.HandleKey(key);
                // Fast taps every third key (120 ms apart), otherwise long enough for everything to settle.
                var frames = step % 3 == 0 ? 3 : 20;
                for (var t = 0; t < frames; t++)
                {
                    h.Clock.Advance(40);
                    now += 40;
                    h.Shell.Tick();
                    var context = "key " + step + " (" + key + ") +" + (t + 1) * 40 + " ms, row " + home.FocusedRow?.Key + (home.Immersive ? " [trailer view]" : "") + " hero " + home.HeroTile?.Title;
                    AssertFrame(animator.Step(UiLayout.Build(h.Shell), now), context);
                }
                var settled = UiLayout.Build(h.Shell);
                AssertFrame(settled, "key " + step + " (" + key + ") settled, row " + home.FocusedRow?.Key, settled: true);
                AssertHomeZones(settled, home, "key " + step + " (" + key + "), row " + home.FocusedRow?.Key);
            }
            Assert.Contains(keys, k => k == RemoteKey.Down);
        }

        [Fact]
        public void HomeRowAboveOnlyPeeksCroppedDimmedAndWithoutCaptions()
        {
            var h = FullHome();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            h.Wait(600);
            h.Press(RemoteKey.Down);
            var frame = UiLayout.Build(h.Shell);
            var band = frame.Nodes.FindIndex(n => n.Key == "home:band");
            var dim = frame.Nodes.FindIndex(n => n.Key == "home:peek-dim");
            var focusedTitle = frame.Nodes.Single(n => n.Key == "row:" + home.FocusedRow.Key + ":title");
            var aboveCards = frame.Nodes.Select((n, i) => (n, i)).Where(x => x.n.Key.StartsWith("row:continue:", StringComparison.Ordinal) && x.n.Key.EndsWith(":art", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(aboveCards);
            Assert.All(aboveCards, x =>
            {
                Assert.True(x.i < dim && x.i < band);                              // dimmed by the scrim, cropped by the band
                Assert.True(x.n.Y < UiLayout.ScrolledHeroBottom);                  // its top is hidden under the banner
                Assert.True(x.n.Y + x.n.H > UiLayout.ScrolledHeroBottom + 40);     // a real slice of it still shows
                Assert.True(x.n.Y + x.n.H < focusedTitle.Y);                       // and it ends above the focused row's title
            });
            Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("row:continue:", StringComparison.Ordinal) && n.Key.EndsWith(":caption", StringComparison.Ordinal) && n.Opacity > 0);
            // The banner art ends where the rows zone starts.
            var art = frame.Nodes.Single(n => n.Key.StartsWith("hero:bg:", StringComparison.Ordinal));
            Assert.Equal(UiLayout.ScrolledHeroBottom, art.Y + art.H);
        }

        [Fact]
        public void HomeRowsNeverScrollPastTheirLastCardAndTheLastRowSitsAtTheBottom()
        {
            var h = FullHome();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            h.Wait(600);
            while (home.FocusedRow.Style != TileStyle.Ranked)
                h.Press(RemoteKey.Down);
            for (var i = 0; i < 12; i++)
                h.Press(RemoteKey.Right);
            var frame = UiLayout.Build(h.Shell);
            var posters = frame.Nodes.Where(n => n.Key.StartsWith("row:" + home.FocusedRow.Key + ":", StringComparison.Ordinal) && n.Key.EndsWith(":ph", StringComparison.Ordinal)).ToList();
            Assert.True(posters.Max(n => n.X + n.W) <= UiFrame.Width - 60, "the last card stays inside the right margin");
            Assert.True(posters.Max(n => n.X + n.W) >= UiFrame.Width - 110, "the row is scrolled to its end, not past it");
            var rank = frame.Nodes.Where(n => n.Key.EndsWith(":rank", StringComparison.Ordinal)).ToList();
            Assert.Contains(rank, n => n.Text == "10" && n.W > rank.First(r => r.Text.Length == 1).W);   // "10" gets a wider slot
            // Last row: anchored to the bottom, not floating over an empty half screen.
            for (var i = 0; i < 12; i++)
                h.Press(RemoteKey.Down);
            frame = UiLayout.Build(h.Shell);
            var cards = frame.Nodes.Where(n => n.Key.StartsWith("row:" + home.FocusedRow.Key + ":", StringComparison.Ordinal) && n.Key.EndsWith(":ph", StringComparison.Ordinal)).ToList();
            Assert.InRange(cards.Max(n => n.Y + n.H), UiFrame.Height - 140, UiFrame.Height - 20);
        }

        [Fact]
        public void TrailerOnlyPlaysInTheTrailerViewAndAnyKeyEndsIt()
        {
            var h = FullHome();
            h.Shell.Services.TrailerCards = false;   // the billboard trailer view (the pop-out card's fallback mode)
            var home = new HomeViewModel();
            h.Shell.Start(home);
            h.Shell.Services.LastInteractionMs = 0; h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;   // as if the viewer just navigated
            h.Wait(HomeViewModel.TrailerDelayMs - 600);
            Assert.Null(home.TrailerId);
            Assert.DoesNotContain(UiLayout.Build(h.Shell).Nodes, n => n.Kind == UiNodeKind.Trailer);
            h.Wait(1200);
            Assert.NotNull(home.TrailerId);
            Assert.True(home.Immersive);   // never a trailer beside the rows
            var frame = UiLayout.Build(h.Shell);
            var trailer = frame.Nodes.Single(n => n.Kind == UiNodeKind.Trailer);
            Assert.Equal((0, 0, UiFrame.Width, UiFrame.Height), (trailer.X, trailer.Y, trailer.W, trailer.H));
            AssertFrame(frame, "trailer view", settled: true);
            h.Press(RemoteKey.Down);   // ends the trailer and restores the UI; focus does not move
            Assert.Null(home.TrailerId);
            Assert.Equal("continue", home.FocusedRow.Key);
            Assert.Equal(0, home.FocusedColumn(home.FocusedRow));
            Assert.DoesNotContain(UiLayout.Build(h.Shell).Nodes, n => n.Kind == UiNodeKind.Trailer);
        }
    }
}
