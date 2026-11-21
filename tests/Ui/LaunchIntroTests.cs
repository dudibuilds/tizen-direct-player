using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// The launch intro over Home (LaunchIntro, docs/design/09-launch.html): one logo, landing on Home's menu wordmark.
    public class LaunchIntroTests
    {
        private static (UiHarness H, HomeViewModel Home) Launch()
        {
            var h = LayoutGridTests.FullHome();
            var home = new HomeViewModel().WithLaunchIntro();
            h.Shell.Start(home);
            return (h, home);
        }

        private static UiNode Node(UiFrame f, string key) => f.Nodes.FirstOrDefault(n => n.Key == key);

        [Fact]
        public void IntroCoversHomeAndStartsWithTheFrontChevronAloneCentred()
        {
            var (h, home) = Launch();
            var f = UiLayout.Build(h.Shell);
            Assert.NotNull(home.Intro);
            var cover = Node(f, "intro:cover");
            Assert.Equal(1, cover.Opacity);
            Assert.Equal(UiFrame.Width, cover.W);
            var front = Node(f, UiWordmark.FrontKey);
            Assert.InRange(front.X + front.W / 2, UiFrame.Width / 2 - 2, UiFrame.Width / 2 + 2);
            Assert.Equal(0, Node(f, UiWordmark.BackKey).Opacity);
            Assert.All(Enumerable.Range(0, UiWordmark.LetterCount), i => Assert.Equal(0, Node(f, UiWordmark.LetterKey(i)).Opacity));
            // The intro's logo keys appear once: Home's menu does not draw its own copy while the intro plays.
            Assert.Equal(1, f.Nodes.Count(n => n.Key == UiWordmark.FrontKey));
        }

        [Fact]
        public void BeatsRevealTheBackChevronThenTheWord()
        {
            var (h, _) = Launch();
            h.Wait(500);
            var f = UiLayout.Build(h.Shell);
            Assert.Equal(UiWordmark.BackOpacity, Node(f, UiWordmark.BackKey).Opacity, 3);
            Assert.Equal(0, Node(f, UiWordmark.LetterKey(0)).Opacity);
            h.Wait(1200);
            f = UiLayout.Build(h.Shell);
            Assert.All(Enumerable.Range(0, UiWordmark.LetterCount), i => Assert.Equal(1, Node(f, UiWordmark.LetterKey(i)).Opacity));
            // The lockup is centred as a whole once the word is in.
            var first = Node(f, UiWordmark.BackKey);
            var width = UiWordmark.Width(LaunchIntro.Scale);
            Assert.InRange(first.X, (UiFrame.Width - width) / 2 - 2, (UiFrame.Width - width) / 2 + 2);
        }

        [Fact]
        public void HandsOverIntoTheMenuWordmarkAndLeavesOneLogo()
        {
            var (h, home) = Launch();
            h.Wait(2700);
            Assert.True(home.ReadyForReveal);
            var f = UiLayout.Build(h.Shell);
            Assert.Equal(0, Node(f, "intro:cover").Opacity);
            var menuParts = UiWordmark.Parts(1, UiWordmark.MenuX, UiWordmark.MenuY);
            foreach (var p in menuParts)
            {
                var n = Node(f, p.Key);
                Assert.Equal((int)System.Math.Round(p.X), n.X);
                Assert.Equal((int)System.Math.Round(p.Y), n.Y);
                Assert.Equal(LaunchIntro.HandoffMs, n.MoveMs);
            }
            h.Wait(900);
            Assert.Null(home.Intro);
            f = UiLayout.Build(h.Shell);
            Assert.Null(Node(f, "intro:cover"));
            // Home's own menu now draws the same keys at the same place: the landed logo simply stays.
            foreach (var p in menuParts)
            {
                Assert.Equal(1, f.Nodes.Count(n => n.Key == p.Key));
                Assert.Equal((int)System.Math.Round(p.X), Node(f, p.Key).X);
            }
        }

        [Fact]
        public void AnyKeySkipsToTheHandOffAndIsSwallowed()
        {
            var (h, home) = Launch();
            h.Wait(300);
            var row = home.FocusedRow?.Key;
            h.Shell.HandleKey(RemoteKey.Down);
            Assert.True(home.Intro.HandingOff);
            Assert.Equal(row, home.FocusedRow?.Key);
            h.Wait(900);
            Assert.Null(home.Intro);
        }

        [Fact]
        public void HoldsWithALoadingLineWhileHomeIsNotReady()
        {
            var intro = new LaunchIntro();
            intro.Start(0);
            intro.Tick(3200, homeReady: false);
            Assert.False(intro.HandingOff);
            var early = new UiFrame();
            intro.Draw(early, 3200);
            Assert.Null(Node(early, "intro:load:fill"));   // no line during Home's first paint window
            intro.Tick(4200, homeReady: false);
            var f = new UiFrame();
            intro.Draw(f, 4200);
            Assert.NotNull(Node(f, "intro:load:fill"));    // a long wait shows it
            intro.Tick(4300, homeReady: true);
            Assert.True(intro.HandingOff);
            intro.Tick(4300 + LaunchIntro.HandoffMs + 100, homeReady: true);
            Assert.True(intro.Done);
        }

        [Fact]
        public void RunsTheQueuedActionOnlyAfterHandingOver()
        {
            var h = LayoutGridTests.FullHome();
            var ran = 0;
            var home = new HomeViewModel().WithLaunchIntro(() => ran++);
            h.Shell.Start(home);
            h.Wait(1500);
            Assert.Equal(0, ran);
            h.Wait(2500);
            Assert.Equal(1, ran);
            h.Wait(1000);
            Assert.Equal(1, ran);
        }

        [Fact]
        public void LaunchSoundPlaysOnceAndFollowsTheSetting()
        {
            var h = LayoutGridTests.FullHome();
            var played = 0;
            h.Shell.Services.LaunchSound = () => played++;
            h.Shell.Start(new HomeViewModel().WithLaunchIntro());
            h.Wait(500);
            Assert.Equal(1, played);

            var muted = LayoutGridTests.FullHome();
            var mutedPlays = 0;
            muted.Shell.Services.LaunchSound = () => mutedPlays++;
            muted.Player.Settings.SetLaunchSound(false);
            muted.Shell.Start(new HomeViewModel().WithLaunchIntro());
            Assert.Equal(0, mutedPlays);
        }

        [Fact]
        public void LogoGlyphsAreRasterisedLargerThanIcons()
        {
            Assert.Equal(192, UiGlyphs.RasterSize(UiGlyphs.MarkFront));
            Assert.Equal(192, UiGlyphs.RasterSize(UiGlyphs.WordmarkE));
            Assert.Equal(96, UiGlyphs.RasterSize(UiGlyphs.Play));
            // The counter of the "e" is open and its bowl is filled (outline union, not even-odd artefacts).
            Assert.False(UiGlyphs.Contains(UiGlyphs.WordmarkE, 7.4, 10.3));
            Assert.True(UiGlyphs.Contains(UiGlyphs.WordmarkE, 3.4, 13.4));
        }

        [Fact]
        public void WaitsOnTheInkPageUntilTheLogoIsDrawableThenStartsWithTheSound()
        {
            var h = LayoutGridTests.FullHome();
            var ready = false;
            var played = 0;
            h.Shell.Services.LaunchIntroReady = () => ready;
            h.Shell.Services.LaunchSound = () => played++;
            var home = new HomeViewModel().WithLaunchIntro();
            h.Shell.Start(home);
            h.Wait(1500);
            var f = UiLayout.Build(h.Shell);
            Assert.False(home.Intro.Started);
            Assert.Equal(0, played);
            Assert.NotNull(Node(f, "intro:cover"));
            Assert.Null(Node(f, UiWordmark.FrontKey));
            ready = true;
            h.Wait(100);
            Assert.True(home.Intro.Started);
            Assert.Equal(1, played);
            Assert.NotNull(Node(UiLayout.Build(h.Shell), UiWordmark.FrontKey));
        }

        [Fact]
        public void GivesUpOnTheIntroIfTheLogoNeverBecomesDrawable()
        {
            var h = LayoutGridTests.FullHome();
            var played = 0;
            h.Shell.Services.LaunchIntroReady = () => false;
            h.Shell.Services.LaunchSound = () => played++;
            var home = new HomeViewModel().WithLaunchIntro();
            h.Shell.Start(home);
            h.Wait((int)LaunchIntro.GiveUpMs + 300);
            Assert.Null(home.Intro);
            Assert.Equal(0, played);
            Assert.Null(Node(UiLayout.Build(h.Shell), "intro:cover"));
        }

        [Fact]
        public void HomeIsDrawnUnderTheCoverOnlyOnceTheLogoIsIn()
        {
            var (h, _) = Launch();
            h.Wait(1000);
            Assert.DoesNotContain(UiLayout.Build(h.Shell).Nodes, n => n.Key.StartsWith("menu:home", System.StringComparison.Ordinal));
            h.Wait((int)(LaunchIntro.HomeUnderCoverMs - 1000) + 100);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key.StartsWith("menu:home", System.StringComparison.Ordinal));
        }

        [Fact]
        public void HomeLoadsItsRowsOnlyAfterTheStartUpReadsWhileTheIntroPlaysAtOnce()
        {
            var h = LayoutGridTests.FullHome();
            var reads = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var home = new HomeViewModel().WithLaunchIntro(null, reads.Task);
            h.Shell.Start(home);
            h.Wait(300);
            Assert.True(home.Intro.Started);
            Assert.False(home.ReadyForReveal);
            reads.SetResult(true);
            Assert.True(System.Threading.SpinWait.SpinUntil(() => { h.Wait(100); return home.ReadyForReveal; }, 3000));
        }
    }
}
