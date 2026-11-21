using System;
using System.IO;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Design language (2026-11-19): tokens, glyph icons and soft shadows that both painters draw.
    public class DesignLanguageTests
    {
        private static string TempDir()
        {
            var dump = Environment.GetEnvironmentVariable("RU7100_GLYPH_DUMP");
            return string.IsNullOrEmpty(dump) ? Path.Combine(Path.GetTempPath(), "ru7100-design-" + Guid.NewGuid().ToString("N")) : dump;
        }

        [Fact]
        public void PaletteIsInkWithOneAmberAccentAndWhiteFocus()
        {
            Assert.Equal("#0B0E14", UiTheme.Background);
            Assert.Equal("#FFB23E", UiTheme.Accent);
            Assert.Equal("#FFFFFF", UiTheme.Focus);
            Assert.NotEqual(UiTheme.Accent, UiTheme.FocusFill);
        }

        [Fact]
        public void TypeRampHasFourWorkingStepsWithVisibleJumps()
        {
            var ramp = new[] { UiScale.Meta, UiScale.Prose, UiScale.Head, UiScale.Title };
            Assert.Equal(new[] { 28, 34, 40, 56 }, ramp);
            Assert.True(UiScale.Label >= UiScale.Hint, "nothing is smaller than the 26 px floor");
        }

        [Fact]
        public void EveryGlyphDrawsSomethingAndLeavesAir()
        {
            foreach (var name in UiGlyphs.Names)
            {
                var covered = 0.0;
                for (var y = 0; y < 48; y++)
                    for (var x = 0; x < 48; x++)
                        covered += UiGlyphs.Coverage(name, x, y, 48);
                var share = covered / (48 * 48);
                Assert.True(share > 0.04, name + " is nearly empty");
                Assert.True(name == UiGlyphs.Disc || share < 0.75, name + " fills its box");
                Assert.Contains("<svg", UiGlyphs.Svg(name));
            }
        }

        [Fact]
        public void GlyphsAndShadowsLowerToTintedImagesTheTvPainterAlreadyDraws()
        {
            var dir = TempDir();
            try
            {
                var frame = new UiFrame();
                frame.Shadow("card:shadow", 100, 100, 400, 200, 16);
                frame.Glyph("play", UiGlyphs.Play, 10, 10, 34, UiTheme.TextOnAccent);
                var lowered = UiFrameLowering.Lower(frame, new UiAssets(dir));

                var shadow = lowered.Nodes.Single(n => n.Key == "card:shadow");
                Assert.StartsWith("file://", shadow.ImageUrl);
                Assert.Equal(16 + UiTheme.ShadowSpread, shadow.NinePatch);
                Assert.Equal(100 - UiTheme.ShadowSpread, shadow.X);
                Assert.Equal(400 + 2 * UiTheme.ShadowSpread, shadow.W);
                Assert.Equal(UiTheme.Shadow, shadow.Fill);

                var glyph = lowered.Nodes.Single(n => n.Key == "play");
                Assert.StartsWith("file://", glyph.ImageUrl);
                Assert.True(glyph.Stretch);
                Assert.Equal(UiTheme.TextOnAccent, glyph.Fill);   // the tint: a focus change needs no new image
                Assert.True(File.Exists(glyph.ImageUrl.Substring(7)));

                if (Environment.GetEnvironmentVariable("RU7100_GLYPH_DUMP") != null)
                    foreach (var name in UiGlyphs.Names)
                        new UiAssets(dir).Glyph(name);
            }
            finally
            {
                if (Environment.GetEnvironmentVariable("RU7100_GLYPH_DUMP") == null && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void ShadowFadesSmoothlyFromTheBoxEdge()
        {
            Assert.Equal(1, UiGlyphs.ShadowAlpha(-4, 48));
            Assert.Equal(0, UiGlyphs.ShadowAlpha(48, 48));
            var previous = 1.0;
            for (var d = 1; d < 48; d++)
            {
                var a = UiGlyphs.ShadowAlpha(d, 48);
                Assert.True(a <= previous && previous - a < 0.08, "no hard step at " + d);
                previous = a;
            }
        }

        [Fact]
        public void SnapshotHtmlDrawsGlyphsAsMasksAndShadowsAsCssShadows()
        {
            var frame = new UiFrame();
            frame.Shadow("s", 100, 100, 400, 200, 16);
            frame.Glyph("g", UiGlyphs.Pause, 10, 10, 34, "#FFFFFF");
            var html = UiHtml.RenderStage(frame);
            Assert.Contains("-webkit-mask:url(", html);
            Assert.Contains("box-shadow:", html);
            Assert.DoesNotContain("src=\"glyph:", html);
            Assert.DoesNotContain("src=\"shadow:", html);
        }

        // ---- player chrome ----

        [Fact]
        public void PlayerOverlayIsGradientsNotBandsWithARealTransport()
        {
            var h = new UiHarness();
            h.StartEpisode();
            h.Press(RemoteKey.Select);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            var top = nodes.Single(n => n.Key == "overlay:top");
            var bottom = nodes.Single(n => n.Key == "overlay:bottom");
            Assert.Equal(UiNodeKind.Gradient, top.Kind);
            Assert.Equal(UiNodeKind.Gradient, bottom.Kind);
            Assert.EndsWith("00", top.Color);       // melts into the picture: no hard edge
            Assert.EndsWith("00", bottom.Fill);
            // Seek bar on its own line across the safe width: track, amber played range, white playhead.
            var track = nodes.Single(n => n.Key == "transport:bar:track");
            Assert.Equal(UiScale.SafeX, track.X);
            Assert.Equal(UiScale.ContentW, track.W);
            var head = nodes.Single(n => n.Key == "transport:bar:head");
            Assert.Equal(UiTheme.Text, head.Fill);
            Assert.True(head.Y + head.H <= UiScale.Bottom);
            // The play/pause control is a glyph in a round button, focused with the light fill.
            var button = nodes.Single(n => n.Key == "transport:state:bg");
            Assert.Equal(button.W, button.H);
            Assert.Equal(UiTheme.FocusFill, button.Fill);
            Assert.Contains(nodes, n => n.Key == "transport:state:pause:0" && UiGlyphs.IsGlyph(n.ImageUrl, out var g) && g == UiGlyphs.Pause && n.Fill == UiTheme.TextOnFocus);
        }

        [Fact]
        public void PlayedRangeIsAmberAndFocusIsNeverAmber()
        {
            var h = new UiHarness();
            h.StartEpisode();
            h.Press(RemoteKey.Select, RemoteKey.Up);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Equal(UiTheme.Accent, nodes.Single(n => n.Key == "transport:bar:fill").Fill);
            var focused = nodes.Single(n => n.Key.StartsWith("action:", StringComparison.Ordinal) && n.Key.EndsWith(":bg", StringComparison.Ordinal) && n.Fill == UiTheme.FocusFill);
            Assert.Contains(nodes, n => n.Key == focused.Key.Replace(":bg", ":label") && n.Color == UiTheme.TextOnFocus && n.Bold);
            Assert.All(nodes.Where(n => n.Key.StartsWith("action:", StringComparison.Ordinal) && n.Key.EndsWith(":bg", StringComparison.Ordinal) && n != focused), n => Assert.Equal(UiTheme.Glass, n.Fill));
        }

        [Fact]
        public void PlayerPanelsAreRightEdgeSheetsOnOneDimLayer()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select, RemoteKey.Up, RemoteKey.Select);   // Audio & subtitles
            Assert.Equal(PlayerPanel.Tracks, vm.Panel);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            var dim = nodes.Single(n => n.Key == "sheet:dim");
            Assert.Equal(UiFrame.Width, dim.W);
            Assert.Equal(UiFrame.Height, dim.H);
            var sheet = nodes.Single(n => n.Key == "sheet:bg");
            Assert.Equal(0, sheet.Y);
            Assert.Equal(UiFrame.Height, sheet.H);
            Assert.Equal(UiFrame.Width, sheet.X + sheet.W);
            Assert.Equal(0, sheet.Radius);                       // part of the screen edge, not a floating card
            var lead = nodes.Single(n => n.Key == "sheet:lead");
            Assert.Equal(UiNodeKind.Gradient, lead.Kind);
            Assert.Equal(sheet.X, lead.X + lead.W);
            // List rows: the focused one is a soft light fill with a bold white label; the selected one has an amber tick.
            var focusedRow = nodes.Single(n => n.Key.StartsWith("tracks:", StringComparison.Ordinal) && n.Key.EndsWith(":bg", StringComparison.Ordinal) && n.Fill == UiTheme.FocusRow);
            var label = nodes.Single(n => n.Key == focusedRow.Key.Replace(":bg", ":label"));
            Assert.Equal(UiTheme.Text, label.Color);
            Assert.True(label.Bold);
            Assert.Contains(nodes, n => n.Key.EndsWith(":check", StringComparison.Ordinal) && UiGlyphs.IsGlyph(n.ImageUrl, out _) && n.Fill == UiTheme.Accent);
            // The subtitle cue keeps to the free area left of the sheet, inside the safe margin.
            var cue = nodes.SingleOrDefault(n => n.Key == "subtitle");
            if (cue != null)
            {
                Assert.True(cue.X >= UiScale.SafeX);
                Assert.True(cue.X + cue.W <= sheet.X);
            }
        }

        [Fact]
        public void StartCardHasOneStatusLineOverOneThinProgressLine()
        {
            var h = new UiHarness();
            var s = new PlayerSnapshot { SessionActive = true, State = PlayerState.Preparing, StartStep = 2, StatusText = "Checking 7 sources…", Title = "The Bear" };
            h.Player.Snapshot = s;
            h.Shell.Push(new PlayerViewModel(new PlayRequest()));
            h.Player.Snapshot = s;
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Single(nodes, n => n.Key.StartsWith("player:loading:step:", StringComparison.Ordinal));   // the current stage only, no stepper
            var track = nodes.Single(n => n.Key == "player:loading:bar:track");
            Assert.Equal(4, track.H);
            Assert.Equal(UiTheme.Text, nodes.Single(n => n.Key == "player:loading:bar:fill").Fill);        // loading is not watch progress: white, not amber
            var stage = nodes.Single(n => n.Key.StartsWith("player:loading:step:", StringComparison.Ordinal));
            var status = nodes.Single(n => n.Key == "player:status");
            Assert.Equal(stage.Y, status.Y);
        }

        // ---- motion and frame cost ----

        [Fact]
        public void MoveCurveLeavesFastAndLandsSoftly()
        {
            Assert.Equal(0, UiAnimator.EaseMove(0));
            Assert.Equal(1, UiAnimator.EaseMove(1));
            Assert.True(UiAnimator.EaseMove(0.25) > 0.5, "most of the distance in the first quarter");
            var previous = 0.0;
            for (var i = 1; i <= 100; i++)
            {
                var v = UiAnimator.EaseMove(i / 100.0);
                Assert.True(v >= previous);
                previous = v;
            }
        }

        [Fact]
        public void NodesAtRestAreReusedInsteadOfClonedEveryFrame()
        {
            var a = new UiAnimator();
            var frame = new UiFrame { ScreenKey = "s" };
            frame.Box("still", 0, 0, 10, 10, "#FFFFFF").Enter = UiEnter.None;
            frame.Box("moving", 0, 0, 10, 10, "#FFFFFF").Enter = UiEnter.None;
            a.Step(frame, 0);
            var next = new UiFrame { ScreenKey = "s" };
            next.Box("still", 0, 0, 10, 10, "#FFFFFF").Enter = UiEnter.None;
            next.Box("moving", 200, 0, 10, 10, "#FFFFFF").MoveMs = 200;
            var first = a.Step(next, 0);
            var second = a.Step(next, 100);
            Assert.Same(first.Nodes[0], second.Nodes[0]);          // the resting node: same object, nothing to repaint
            Assert.NotSame(first.Nodes[1], second.Nodes[1]);
            Assert.InRange(second.Nodes[1].X, 101, 199);           // past halfway at half time (move curve)
            var end = a.Step(next, 400);
            Assert.Equal(200, end.Nodes[1].X);
            Assert.Same(end.Nodes[1], a.Step(next, 500).Nodes[1]);
            // New content on a resting node is still delivered.
            var changed = new UiFrame { ScreenKey = "s" };
            changed.Box("still", 0, 0, 10, 10, "#000000").Enter = UiEnter.None;
            changed.Box("moving", 200, 0, 10, 10, "#FFFFFF");
            Assert.Equal("#000000", a.Step(changed, 600).Nodes[0].Fill);
        }

        [Fact]
        public void TwoThreadsMayGenerateTheSameAssetAtOnce()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-design-" + Guid.NewGuid().ToString("N"));
            try
            {
                for (var round = 0; round < 20; round++)
                {
                    // Two hosts on one folder, as the launch prewarm and the first paint are on the TV.
                    var a = new UiAssets(dir);
                    var b = new UiAssets(dir);
                    var radius = 30 + round;
                    string first = null, second = null;
                    System.Threading.Tasks.Parallel.Invoke(() => first = a.RoundedRect(radius), () => second = b.RoundedRect(radius));
                    Assert.Equal(first, second);
                    Assert.True(File.Exists(first));
                }
                Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ---- navigation cost: picture sizes and main-loop stalls ----

        [Fact]
        public void RowCardsAskForCardSizedArtAndTheBillboardKeepsTheFullBackdrop()
        {
            Assert.EndsWith("/background/small/tt0903747/img", UiFormat.CardArtFor("tt0903747"));
            Assert.EndsWith("/background/medium/tt0903747/img", UiFormat.BackgroundFor("tt0903747"));
            Assert.Equal("https://images.metahub.space/poster/small/tt1/img", UiFormat.CardPoster("https://images.metahub.space/poster/medium/tt1/img"));
            Assert.Equal("https://example.org/poster/medium/x.jpg", UiFormat.CardPoster("https://example.org/poster/medium/x.jpg"));

            var h = LayoutGridTests.FullHome();
            h.Shell.Start(new HomeViewModel());
            h.Wait(600);
            var frame = UiLayout.Build(h.Shell);
            var cards = frame.Nodes.Where(n => n.Kind == UiNodeKind.Image && n.Key.StartsWith("row:", StringComparison.Ordinal) && (n.Key.EndsWith(":art", StringComparison.Ordinal) || n.Key.EndsWith(":poster", StringComparison.Ordinal))).ToList();
            Assert.NotEmpty(cards);
            // No row card decodes a 1920x1080 backdrop or a 500x750 poster for a card a quarter of that size.
            Assert.DoesNotContain(cards, n => n.ImageUrl.Contains("/background/medium/") || n.ImageUrl.Contains("/poster/medium/"));
            Assert.Contains(frame.Nodes, n => n.Key.StartsWith("hero:bg:", StringComparison.Ordinal) && n.ImageUrl.Contains("/background/medium/"));
        }

        [Fact]
        public void HeartbeatCountsLateTicksAndRemembersWhatCameBeforeThem()
        {
            var perf = new UiPerfStats();
            double now = 0;
            for (var i = 0; i < 30; i++) { perf.Heartbeat(now, 33.3); now += 33.3; }
            Assert.Equal(0, perf.Stalls);
            now += 300;                                   // the main loop was busy for 300 ms
            perf.Heartbeat(now, 33.3, "images: hero:bg:tt1 1920x1080");
            Assert.Equal(1, perf.Stalls);
            Assert.InRange(perf.LongestStallMs, 300, 340);
            var text = Newtonsoft.Json.JsonConvert.SerializeObject(perf.Snapshot((long)now));
            Assert.Contains("\"longestStallMs\"", text);
            Assert.Contains("hero:bg:tt1 1920x1080", text);
            perf.Reset();
            Assert.Equal(0, perf.Stalls);
        }

        [Fact]
        public void CardLogosGetASmallTrimmedCopyMadeOffTheMainLoop()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-design-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // An 800x310 logo canvas with the artwork in a 600x150 block, as the artwork host serves them.
                const int w = 800, h = 310;
                var rgba = new byte[w * h * 4];
                for (var y = 80; y < 230; y++)
                    for (var x = 100; x < 700; x++)
                    {
                        var i = (y * w + x) * 4;
                        rgba[i] = 255; rgba[i + 1] = 200; rgba[i + 2] = 100; rgba[i + 3] = 255;
                    }
                var source = Path.Combine(dir, "logo.png");
                File.WriteAllBytes(source, Png.Encode(w, h, rgba));
                var fitted = ImageTrim.FittedCopy(source);
                Assert.NotNull(fitted);
                var bytes = File.ReadAllBytes(fitted);
                int Big(int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
                Assert.Equal(ImageTrim.CardLogoW, Big(16));            // 600x150 scaled to fit 320x120
                Assert.Equal(80, Big(20));
                Assert.Null(ImageTrim.OpaqueBounds(fitted));           // nothing left to trim: it is all artwork
                Assert.Equal(fitted, ImageTrim.FittedCopy(source));    // made once
                Assert.True(new FileInfo(fitted).Length < new FileInfo(source).Length * 4);
                // Not a PNG: no copy, no exception.
                var other = Path.Combine(dir, "poster.jpg");
                File.WriteAllBytes(other, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
                Assert.Null(ImageTrim.FittedCopy(other));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TheBillboardWaitsForFocusToRestBeforeItChanges()
        {
            var h = LayoutGridTests.FullHome();
            h.Shell.Start(new HomeViewModel());
            h.Wait(800);
            h.Press(RemoteKey.Down, RemoteKey.Down);
            h.Wait(800);
            string Backdrop() => UiLayout.Build(h.Shell).Nodes.Where(n => n.Key.StartsWith("hero:bg:", StringComparison.Ordinal)).Select(n => n.Key).LastOrDefault();
            var rested = Backdrop();
            Assert.NotNull(rested);
            // A held Right (a key every 120 ms) flies past titles: the billboard art, the largest picture on the
            // screen, never changes on the way, so nothing is decoded for a title that is only passed.
            var home = (HomeViewModel)h.Shell.Current;
            var moved = 0;
            for (var i = 0; i < 5; i++)
            {
                var before = home.FocusedTile?.Id;
                h.Shell.HandleKey(RemoteKey.Right);   // (UiHarness.Press waits 500 ms after each key: separate taps)
                if (home.FocusedTile?.Id != before) moved++;
                h.Wait(100);
                Assert.Equal(rested, Backdrop());
            }
            Assert.True(moved >= 3, "focus moved " + moved + " times");
            h.Wait(HomeViewModel.HeroDelayMs + 250);
            Assert.NotEqual(rested, Backdrop());   // focus rested: now it follows
        }
    }
}
