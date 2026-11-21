using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class PlayerPanelsTests
    {
        [Fact]
        public void NextEpisodeRowShowsPreviewCardAndPlaysNext()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.NextEpisode);
            Assert.Equal("tt0903747:1:2", vm.NextVideo.Id);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "overlay:next:title" && n.Text.StartsWith("S1:E2"));
            h.Press(RemoteKey.Select);
            Assert.Contains("next", h.Player.Commands);
        }

        [Fact]
        public void AudioAndSubtitlesPanelOrdersOriginalAndPreferredFirst()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.Subtitles = new[]
            {
                new SubtitleOption { Id = "e:fr", Language = "fre" },
                new SubtitleOption { Id = "e:en", Language = "eng" },
                new SubtitleOption { Id = "x:es", Language = "spa", External = true }
            };
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.AudioSubtitles);
            h.Press(RemoteKey.Select);
            Assert.Equal(PlayerPanel.Tracks, vm.Panel);
            Assert.EndsWith("[Original]", vm.AudioChoices[0].Label);
            var subs = vm.SubtitleChoices;
            Assert.Equal(new[] { "Timing, size & style  ›", "Off", "Spanish (add-on)", "English", "French" }, subs.Select(c => c.Label).ToArray());
            Assert.True(subs[1].Divider);
            Assert.True(subs[4].Divider);
            Assert.Equal(1, vm.SubtitleIndex);   // focus starts on the selected item (Off), one Up below the timing
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Select);   // English
            Assert.Equal("subtitle e:en", h.Player.Commands.Last());
            h.Press(RemoteKey.Left, RemoteKey.Down, RemoteKey.Select);   // second audio track
            Assert.Equal("audio 1", h.Player.Commands.Last());
            Assert.Contains("overlay: audio & subtitles", h.Text);
            h.Press(RemoteKey.Back);
            Assert.Equal(PlayerPanel.None, vm.Panel);
            Assert.Equal(OverlayRow.AudioSubtitles, vm.FocusedRow);
        }

        [Fact]
        public void SubtitleTimingSitsAtTheTopOfTheSubtitlesListWhateverTheNumberOfLanguages()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.Subtitles = Enumerable.Range(0, 40).Select(i => new SubtitleOption { Id = "x:" + i, Language = "l" + i.ToString("00"), External = true }).ToArray();
            h.Player.Snapshot.SubtitleOffsetMs = -1500;
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.AudioSubtitles);
            h.Press(RemoteKey.Select, RemoteKey.Up, RemoteKey.Select);
            Assert.Equal(PlayerPanel.Style, vm.Panel);
            Assert.Equal(StyleRow.Timing, vm.FocusedStyleRow);
            h.Press(RemoteKey.Back);
            Assert.Equal("Timing 1.5 s earlier  ›", vm.SubtitleChoices[0].Label);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "tracks:s:0:label" && n.Text == "Timing 1.5 s earlier  ›");
        }

        [Fact]
        public void EpisodesPanelSwitchesSeasonAndPlaysAnEpisode()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.Episodes);
            h.Press(RemoteKey.Select);
            Assert.Equal(PlayerPanel.Episodes, vm.Panel);
            Assert.Contains("S1:E1 Pilot [now playing]", h.Text);
            h.Press(RemoteKey.Right);
            Assert.Equal(2, vm.EpisodesSeason);
            h.Press(RemoteKey.Select);
            Assert.Equal("play-request tt0903747:2:1", h.Player.Commands.Last());
            Assert.False(vm.OverlayVisible);
        }

        [Fact]
        public void CreditsCardCountsDownWhenBingeIsOnAndBackOnlyHidesIt()
        {
            var h = new UiHarness();
            // A 20-minute episode: the hand-over is 30 s before the end, so the card (last 60 s) counts 20 s down to it.
            var vm = h.StartEpisode(1_150_000);
            h.Player.Snapshot.DurationMs = 1_200_000;
            h.Player.Snapshot.Binge = true;
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            Assert.Contains("[credits] Next episode in 20 s: S1:E2", h.Text);
            h.Press(RemoteKey.Back);
            Assert.False(vm.CreditsCardVisible);
            Assert.DoesNotContain("stop-after True", h.Player.Commands);   // Back hides the card and changes nothing else
            Assert.Same(vm, h.Shell.Current);
            Assert.False(vm.ExitHintVisible);
        }

        [Fact]
        public void CreditsCardSelectPlaysNextNow()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(1_150_000);
            h.Player.Snapshot.DurationMs = 1_200_000;
            h.Player.Snapshot.Binge = true;
            h.Shell.Tick();
            h.Press(RemoteKey.Select);
            Assert.Contains("next", h.Player.Commands);
            Assert.False(vm.OverlayVisible);
        }

        [Fact]
        public void NextEpisodeCardAlsoShowsWithoutBingeAndWaitsForOk()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(2_790_000);
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            Assert.False(vm.CreditsAutoplay);
            Assert.Contains("[credits] Next episode: S1:E2", h.Text);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "credits:hint" && n.Text.StartsWith("OK: play "));
            h.Press(RemoteKey.Select);
            Assert.Contains("next", h.Player.Commands);
        }

        [Fact]
        public void BackOnTheNextEpisodeCardKeepsTheCreditsPlaying()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(1_150_000);
            h.Player.Snapshot.DurationMs = 1_200_000;
            h.Player.Snapshot.Binge = true;
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            h.Press(RemoteKey.Back);
            Assert.False(vm.CreditsCardVisible);
            Assert.Contains("keep-watching-credits", h.Player.Commands);   // the engine skips the automatic hand-over
            Assert.DoesNotContain("next", h.Player.Commands);
        }

        [Fact]
        public void NextEpisodeCardStartsSixtySecondsBeforeTheEnd()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(2_800_000 - 70_000);
            h.Player.Snapshot.DurationMs = 2_800_000;
            h.Shell.Tick();
            Assert.False(vm.CreditsCardVisible);
            h.Player.Snapshot.PositionMs = 2_800_000 - 59_000;
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
        }

        [Fact]
        public void BingeCountdownReachesZeroWhereTheEngineHandsOver()
        {
            // A 46-minute episode: the engine hands over at 98 % (2,744 s), 56 s before the end. The card used to count
            // to the end of the file and showed "56 s" while the next episode was already starting.
            var h = new UiHarness();
            var vm = h.StartEpisode(2_740_000);
            h.Player.Snapshot.Binge = true;
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            Assert.Equal(EpisodeHandOff.AtMs(2_800_000) - 2_740_000, vm.CreditsRemainingMs);
            Assert.Contains("[credits] Next episode in 4 s: S1:E2", h.Text);
            Assert.DoesNotContain("next", h.Player.Commands);
        }

        [Fact]
        public void BingeCountdownStopsAtZeroAndLeavesTheHandOverToTheEngine()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(2_740_000);
            h.Player.Snapshot.Binge = true;
            h.Shell.Tick();
            h.Player.Snapshot.PositionMs = EpisodeHandOff.AtMs(2_800_000) + 1000;
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            Assert.Equal(0, vm.CreditsRemainingMs);
            Assert.DoesNotContain("next", h.Player.Commands);   // the engine advances (with binge's quality and size cap)
        }

        [Fact]
        public void WithoutBingeTheCardNeverPlaysByItself()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(2_790_000);
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            Assert.DoesNotContain("next", h.Player.Commands);
        }

        [Theory]
        [InlineData(2_800_000L, 2_744_000L)]   // long episode: 98 %
        [InlineData(1_200_000L, 1_170_000L)]   // short episode: 30 s before the end
        public void HandOffIsTheFirstOf98PercentAnd30SecondsLeft(long duration, long expected)
        {
            Assert.Equal(expected, EpisodeHandOff.AtMs(duration));
            Assert.False(EpisodeHandOff.Reached(expected - 1, duration));
            Assert.True(EpisodeHandOff.Reached(expected, duration));
        }
    }
}
