using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class PlayerOverlayTests
    {
        [Theory]
        [InlineData("Return", RemoteKey.Select)]
        [InlineData("Enter", RemoteKey.Select)]
        [InlineData("XF86Back", RemoteKey.Back)]
        [InlineData("Escape", RemoteKey.Back)]
        [InlineData("XF86PlayBack", RemoteKey.PlayPause)]
        [InlineData("XF86Playback", RemoteKey.PlayPause)]
        [InlineData("XF86AudioPlay", RemoteKey.Play)]
        [InlineData("XF86AudioPause", RemoteKey.Pause)]
        [InlineData("XF86AudioStop", RemoteKey.Stop)]
        [InlineData("XF86AudioRewind", RemoteKey.Rewind)]
        [InlineData("XF86AudioForward", RemoteKey.FastForward)]
        [InlineData("XF86AudioNext", RemoteKey.Next)]
        [InlineData("XF86SimpleMenu", RemoteKey.Menu)]
        [InlineData("Up", RemoteKey.Up)]
        [InlineData("Left", RemoteKey.Left)]
        [InlineData("7", RemoteKey.Digit7)]
        [InlineData("XF86SomethingNew", RemoteKey.None)]
        [InlineData(null, RemoteKey.None)]
        public void MapsTizenKeyNames(string name, RemoteKey expected) => Assert.Equal(expected, RemoteKeys.FromTizen(name));

        [Fact]
        public void OverlayIsHiddenByDefaultAndCenterOpensIt()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            Assert.False(vm.OverlayVisible);
            h.Press(RemoteKey.Select);
            Assert.True(vm.OverlayVisible);
            Assert.Equal(OverlayRow.Transport, vm.FocusedRow);
            Assert.Empty(h.Player.Commands);
            Assert.Contains("overlay: open", h.Text);
        }

        [Fact]
        public void CenterOnTransportTogglesPause()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select, RemoteKey.Select);
            Assert.Equal(new[] { "toggle" }, h.Player.Commands);
            Assert.True(vm.OverlayVisible);
        }

        [Fact]
        public void OverlayIsOneRowOfButtonsAboveTheTransportBar()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select);
            Assert.Equal(new[] { OverlayRow.Episodes, OverlayRow.NextEpisode, OverlayRow.AudioSubtitles, OverlayRow.Sources, OverlayRow.More }, vm.Actions.ToArray());
            h.Press(RemoteKey.Up);
            Assert.Equal(OverlayRow.AudioSubtitles, vm.FocusedRow);   // the most used button is one press away
            h.Press(RemoteKey.Right);
            Assert.Equal(OverlayRow.Sources, vm.FocusedRow);          // changing source: OK, Up, Right, OK
            h.Press(RemoteKey.Select);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Back);
            h.Press(RemoteKey.Down);
            Assert.Equal(OverlayRow.Transport, vm.FocusedRow);
            h.Press(RemoteKey.Up);
            Assert.Equal(OverlayRow.Sources, vm.FocusedRow);          // Up returns to the button last used
            var nodes = UiLayout.Build(h.Shell).Nodes;
            var buttons = nodes.Where(n => n.Key.StartsWith("action:", System.StringComparison.Ordinal) && n.Key.EndsWith(":bg", System.StringComparison.Ordinal)).ToList();
            Assert.Equal(5, buttons.Count);
            Assert.Single(buttons.Select(b => b.Y).Distinct());
            Assert.All(buttons, b => Assert.True(b.Y + b.H < nodes.Single(n => n.Key == "transport:bar:track").Y));
        }

        [Fact]
        public void BackClosesOverlayThenShowsExitHintAndSecondBackLeaves()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select, RemoteKey.Back);
            Assert.False(vm.OverlayVisible);
            Assert.Same(vm, h.Shell.Current);
            h.Press(RemoteKey.Back);
            Assert.Null(h.Shell.Dialog);                      // no confirmation dialog any more
            Assert.True(vm.OverlayVisible);
            Assert.True(vm.ExitHintVisible);
            Assert.Contains("Press Back again to exit", h.Text);
            Assert.DoesNotContain("stop", h.Player.Commands);
            h.Press(RemoteKey.Back);
            Assert.Contains("stop", h.Player.Commands);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void ExitHintLapsesAndAnyOtherKeyDisarmsIt()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Back);
            Assert.True(vm.ExitHintVisible);
            h.Wait(PlayerViewModel.ExitConfirmMs + 200);
            Assert.False(vm.ExitHintVisible);
            h.Press(RemoteKey.Back);                          // too late: this one only closes the overlay
            Assert.Same(vm, h.Shell.Current);
            Assert.False(vm.OverlayVisible);
            h.Press(RemoteKey.Back, RemoteKey.Up, RemoteKey.Back);
            Assert.Same(vm, h.Shell.Current);
            Assert.DoesNotContain("stop", h.Player.Commands);
        }

        [Fact]
        public void ExitHintOverlayHidesByItselfEvenAfterAButtonWasFocused()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select, RemoteKey.Up, RemoteKey.Back);   // overlay closed with a button focused
            h.Press(RemoteKey.Back);                                   // exit hint
            Assert.Equal(OverlayRow.Transport, vm.FocusedRow);
            h.Wait(PlayerViewModel.ExitConfirmMs + PlayerViewModel.AutoHideMs + 500);
            Assert.False(vm.OverlayVisible);
            Assert.Same(vm, h.Shell.Current);
        }

        [Fact]
        public void ACardGivesWayWhenPlaybackStartsFromElsewhere()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.SessionActive = false;
            h.Player.Snapshot.State = PlayerState.Completed;
            h.Shell.Tick();
            Assert.Equal(PlayerCard.Ended, vm.Card);
            h.Player.Snapshot.SessionActive = true;                    // e.g. the agent API started the next episode
            h.Player.Snapshot.State = PlayerState.Playing;
            h.Shell.Tick();
            Assert.Equal(PlayerCard.None, vm.Card);
            Assert.Same(vm, h.Shell.Current);
        }

        [Fact]
        public void StartCardBackCancelsAtOnceAndOffersNoPlayerControls()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.StartStep = 2;
            h.Player.Snapshot.StatusText = "Trying the best sources · 8 found";
            h.Shell.Tick();
            Assert.True(vm.Loading);
            h.Press(RemoteKey.Up, RemoteKey.Down, RemoteKey.PlayPause, RemoteKey.Next, RemoteKey.Right);
            Assert.False(vm.OverlayVisible);
            Assert.Empty(h.Player.Commands);
            Assert.Null(h.Shell.Toast);
            var keys = UiLayout.Build(h.Shell).Nodes.Select(n => n.Key).ToList();
            Assert.DoesNotContain(keys, k => k.StartsWith("transport:", System.StringComparison.Ordinal) || k.StartsWith("overlay:", System.StringComparison.Ordinal) || k.StartsWith("action:", System.StringComparison.Ordinal));
            Assert.Contains("[start] Trying the best sources · 8 found", h.Text);
            h.Wait(PlayerViewModel.StartHintDelayMs);
            Assert.Contains("Back: cancel · OK: choose a source", h.Text);
            h.Press(RemoteKey.Back);
            Assert.Null(h.Shell.Dialog);
            Assert.Contains("stop", h.Player.Commands);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void StartCardOkOpensTheSourcePicker()
        {
            var h = new UiHarness();
            h.StartEpisode();
            h.Player.Snapshot.StartStep = 2;
            h.Press(RemoteKey.Select);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            Assert.Equal(2, h.Shell.Stack.Count);             // the picker took the player's place
            h.Press(RemoteKey.Down, RemoteKey.Select);
            var player = Assert.IsType<PlayerViewModel>(h.Shell.Current);
            Assert.Equal("bbbb:auto", player.Request.Source.Identity);
        }

        [Fact]
        public void FailedStartStaysOnTheCardWithReasonAndButtons()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Player.PlayGate = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var vm = h.Shell.StartPlayback(new PlayRequest { Meta = UiHarness.Series, Video = UiHarness.Series.Videos[0], ResumeMs = 60_000 });
            h.Player.Snapshot.SessionActive = false;
            h.Player.PlayGate.SetException(new System.InvalidOperationException("All candidate streams failed: Source did not become ready within 30 s; dead swarm"));
            h.Until(() => vm.Card != PlayerCard.None);
            h.Shell.Tick();
            Assert.Same(vm, h.Shell.Current);                  // not thrown back to Home
            Assert.Null(h.Shell.Toast);
            Assert.Equal(PlayerCard.Failed, vm.Card);
            Assert.Contains("[failed] None of the sources that were tried could be played.", h.Text);
            Assert.Contains("Source did not become ready within 30 s", h.Text);
            Assert.Equal(new[] { "Try again", "Choose another source", "Back" }, vm.CardButtons.Select(b => b.Label).ToArray());
            var focused = UiLayout.Build(h.Shell).Nodes.Single(n => n.Key == "player:card:Retry:bg");
            Assert.Equal(UiTheme.FocusFill, focused.Fill);   // focused buttons fill light; white stays the card ring

            h.Player.PlayGate = null;
            h.Player.Commands.Clear();
            h.Press(RemoteKey.Select);                         // Try again: same episode, same resume point
            Assert.Equal(PlayerCard.None, vm.Card);
            Assert.Equal("play-request tt0903747:1:1", h.Player.Commands.First());
            Assert.Equal(60_000, h.Player.LastRequest.ResumeMs);
        }

        [Fact]
        public void FailureCardButtonsChooseSourceAndBack()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Player.PlayGate = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var vm = h.Shell.StartPlayback(new PlayRequest { Meta = UiHarness.Series, Video = UiHarness.Series.Videos[0] });
            h.Player.Snapshot.SessionActive = false;
            h.Player.PlayGate.SetException(new System.InvalidOperationException("No viable streams found for Breaking Bad"));
            h.Until(() => vm.Card != PlayerCard.None);
            Assert.Contains("No playable source was found", vm.CardMessage);
            h.Press(RemoteKey.Right, RemoteKey.Right, RemoteKey.Right);
            Assert.Equal(2, vm.CardIndex);
            h.Press(RemoteKey.Left, RemoteKey.Select);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Back);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void StreamThatFailsMidPlaybackShowsTheFailureCard()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.SessionActive = false;
            h.Player.Snapshot.State = PlayerState.Stopped;
            h.Player.Snapshot.Error = "Stream startup deadline exceeded";
            h.Shell.Tick();
            Assert.Same(vm, h.Shell.Current);
            Assert.Equal(PlayerCard.Failed, vm.Card);
            Assert.Contains("took too long", vm.CardMessage);
            h.Press(RemoteKey.Back);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void FinishedEpisodeOffersTheNextOneInsteadOfClosing()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.SessionActive = false;
            h.Player.Snapshot.State = PlayerState.Completed;
            h.Shell.Tick();
            Assert.Same(vm, h.Shell.Current);
            Assert.Equal(PlayerCard.Ended, vm.Card);
            Assert.Equal(new[] { "Next episode · S1:E2", "Episodes", "Back" }, vm.CardButtons.Select(b => b.Label).ToArray());
            Assert.Contains("[ended]", h.Text);
            h.Player.Commands.Clear();
            h.Press(RemoteKey.Select);
            Assert.Equal("play-request tt0903747:1:2", h.Player.Commands.First());
            Assert.Equal(PlayerCard.None, vm.Card);
        }

        [Fact]
        public void EndCardEpisodesOpensTheTitlePage()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.SessionActive = false;
            h.Player.Snapshot.State = PlayerState.Completed;
            h.Shell.Tick();
            h.Press(RemoteKey.Right, RemoteKey.Select);
            var details = Assert.IsType<DetailsViewModel>(h.Shell.Current);
            Assert.Equal(UiHarness.Series.Summary.Id, details.Id);
            Assert.Equal(2, h.Shell.Stack.Count);
        }

        [Fact]
        public void PausingWithTheMediaKeyShowsTheTransportStripUntilPlayResumes()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.Title = "Breaking Bad";
            h.Press(RemoteKey.Pause);
            Assert.False(vm.OverlayVisible);
            Assert.True(vm.PauseStripVisible);
            Assert.Contains("[paused] Breaking Bad", h.Text);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Contains(nodes, n => n.Key == "pause:title");
            Assert.Contains(nodes, n => n.Key.StartsWith("transport:state:play", System.StringComparison.Ordinal));
            Assert.Contains(nodes, n => n.Key == "transport:duration" && n.Text.StartsWith("−"));   // time left
            h.Wait(30_000);
            Assert.True(vm.PauseStripVisible);                 // it stays for as long as the picture is frozen
            h.Shell.HandleKey(RemoteKey.Play);
            h.Shell.Tick();
            Assert.False(vm.PauseStripVisible);
            Assert.True(vm.ResumeFlashVisible);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key.StartsWith("player:resumed", System.StringComparison.Ordinal));
            h.Wait(PlayerViewModel.ResumeFlashMs + 200);
            Assert.False(vm.ResumeFlashVisible);
        }

        [Fact]
        public void TransportShowsTheActionGlyphElapsedAndRemainingAndASeekBubble()
        {
            var h = new UiHarness();
            h.StartEpisode(600_000);
            h.Press(RemoteKey.Select);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Contains(nodes, n => n.Key == "transport:state:pause:0");       // playing: OK pauses
            Assert.DoesNotContain(nodes, n => n.Text == "Playing" || n.Text == "Paused");
            Assert.Equal("10:00", nodes.Single(n => n.Key == "transport:position").Text);
            Assert.Equal("−36:40", nodes.Single(n => n.Key == "transport:duration").Text);
            Assert.Contains(nodes, n => n.Key == "transport:back" && n.Text == "−10");
            Assert.Contains(nodes, n => n.Key == "transport:forward" && n.Text == "+10");
            h.Shell.HandleKey(RemoteKey.Right);
            nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Equal("10:10", nodes.Single(n => n.Key == "transport:seek").Text);
            h.Player.Snapshot.DurationMs = 0;
            Assert.DoesNotContain(UiLayout.Build(h.Shell).Nodes, n => n.Key == "transport:duration");   // no "--:--"
        }

        [Fact]
        public void PreviousRestartsTheEpisodeOrGoesToTheOneBefore()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Shell.StartPlayback(new PlayRequest { Meta = UiHarness.Series, Video = UiHarness.Series.Videos[1], ResumeMs = 600_000 });
            h.Player.Snapshot.DurationMs = 2_800_000;
            h.Shell.Tick();
            h.Player.Commands.Clear();
            h.Press(RemoteKey.Previous);
            Assert.Equal(new[] { "seek-to 0" }, h.Player.Commands);   // more than 10 s in: start over
            h.Player.Commands.Clear();
            h.Press(RemoteKey.Previous);                               // at the start: the episode before
            Assert.Equal("play-request tt0903747:1:1", h.Player.Commands.Single());
        }

        [Fact]
        public void NextKeyOnTheLatestEpisodeSaysSoInPlainWords()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Shell.StartPlayback(new PlayRequest { Meta = UiHarness.Series, Video = UiHarness.Series.Videos[3], ResumeMs = 600_000 });
            h.Shell.Tick();
            h.Player.Commands.Clear();
            h.Shell.HandleKey(RemoteKey.Next);
            Assert.Empty(h.Player.Commands);
            Assert.Equal("This is the latest episode.", h.Shell.Toast);
        }

        [Fact]
        public void MediaKeysBypassOverlay()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Pause, RemoteKey.Play, RemoteKey.PlayPause);
            Assert.Equal(new[] { "pause", "play", "toggle" }, h.Player.Commands);
            Assert.False(vm.OverlayVisible);

            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.AudioSubtitles);
            h.Player.Commands.Clear();
            h.Press(RemoteKey.PlayPause);
            Assert.Equal(new[] { "toggle" }, h.Player.Commands);
            Assert.Equal(OverlayRow.AudioSubtitles, vm.FocusedRow);
            h.Press(RemoteKey.Stop);
            Assert.Contains("stop", h.Player.Commands);
            Assert.Null(h.Shell.Dialog);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void MediaKeysReachPlayerUnderDialogAndPicker()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.Sources);
            h.Press(RemoteKey.Select);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Pause);
            Assert.Contains("pause", h.Player.Commands);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Stop);
            Assert.Contains("stop", h.Player.Commands);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void OverlayAutoHidesAfterEightSecondsIdleOnTheTransportRow()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Shell.HandleKey(RemoteKey.Select);
            h.Wait(7000);
            Assert.True(vm.OverlayVisible);
            h.Shell.HandleKey(RemoteKey.Left);   // interaction resets the timer
            h.Wait(7500);
            Assert.True(vm.OverlayVisible);
            h.Wait(700);
            Assert.False(vm.OverlayVisible);
        }

        [Fact]
        public void OverlayNeverAutoHidesWithFocusOffTransportOrAPanelOpen()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Shell.HandleKey(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.AudioSubtitles);
            h.Wait(20000);
            Assert.True(vm.OverlayVisible);
            h.Press(RemoteKey.Select);   // open the panel
            Assert.Equal(PlayerPanel.Tracks, vm.Panel);
            h.Wait(60000);
            Assert.True(vm.OverlayVisible);
            Assert.Equal(PlayerPanel.Tracks, vm.Panel);
            h.Press(RemoteKey.Back);   // closes only the panel
            Assert.True(vm.OverlayVisible);
            Assert.Null(h.Shell.Dialog);
        }

        [Fact]
        public void ReopeningSoonRestoresTheLastOverlayFocus()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Shell.HandleKey(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.AudioSubtitles);
            h.Press(RemoteKey.Back);
            Assert.False(vm.OverlayVisible);
            h.Wait(10000);
            h.Press(RemoteKey.Select);
            Assert.Equal(OverlayRow.AudioSubtitles, vm.FocusedRow);
            h.Press(RemoteKey.Back);
            h.Wait(31000);
            h.Press(RemoteKey.Select);
            Assert.Equal(OverlayRow.Transport, vm.FocusedRow);
        }

        [Fact]
        public void OneTapLeftOrRightSeeksTenSecondsWhenOverlayHidden()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(600_000);
            h.Shell.HandleKey(RemoteKey.Left);
            Assert.True(vm.SeekIndicatorVisible);
            Assert.False(vm.OverlayVisible);
            h.Wait(800);
            Assert.Equal(new[] { "seek-to 590000" }, h.Player.Commands);
            h.Shell.HandleKey(RemoteKey.Right);
            h.Wait(800);
            Assert.Equal(new[] { "seek-to 590000", "seek-to 600000" }, h.Player.Commands);
        }

        [Fact]
        public void HeldSeekAcceleratesAndCommitsOnce()
        {
            var h = new UiHarness();
            h.StartEpisode(600_000);
            h.Hold(RemoteKey.Right, 6);   // 10+10+10+30+30+30 = 120 s
            Assert.Empty(h.Player.Commands);
            h.Wait(800);
            Assert.Equal(new[] { "seek-to 720000" }, h.Player.Commands);
        }

        [Fact]
        public void CenterCommitsPendingSeekImmediately()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode(600_000);
            h.Shell.HandleKey(RemoteKey.Right);
            h.Shell.HandleKey(RemoteKey.Select);
            Assert.Equal(new[] { "seek-to 610000" }, h.Player.Commands);
            Assert.False(vm.OverlayVisible);
        }

        [Fact]
        public void SubtitleTimingStepsAndClamps()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.OpenSubtitleSettings(vm);
            Assert.Equal(StyleRow.Timing, vm.FocusedStyleRow);   // opens on the setting viewers come for
            Assert.Equal("In sync", vm.StyleValue(StyleRow.Timing));
            h.Press(RemoteKey.Right, RemoteKey.Right);
            Assert.Equal("Subtitles 0.5 s later", vm.StyleValue(StyleRow.Timing));
            Assert.Contains("OK: reset", UiLayout.Build(h.Shell).Nodes.Single(n => n.Key == "style:hint").Text);
            Assert.Equal(500, h.Player.Snapshot.SubtitleOffsetMs);
            h.Hold(RemoteKey.Left, 10);   // 4 x 250 then 6 x 1000
            Assert.Equal(500 - 1000 - 6000, h.Player.Snapshot.SubtitleOffsetMs);
            h.Player.Snapshot.SubtitleOffsetMs = 29_900;
            h.Press(RemoteKey.Right);
            Assert.Equal(PlayerSettings.MaxSubtitleOffsetMs, h.Player.Snapshot.SubtitleOffsetMs);
            h.Press(RemoteKey.Select);
            Assert.Equal(0, h.Player.Snapshot.SubtitleOffsetMs);
        }

        [Fact]
        public void SubtitleStyleMenuAdjustsPersistedStyleWithPreview()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.OpenSubtitleSettings(vm);
            Assert.True(vm.StyleMenuOpen);
            Assert.Equal("Medium", vm.StyleValue(StyleRow.Size));          // names, not pixels
            Assert.Equal("Standard", vm.StyleValue(StyleRow.Position));
            Assert.Equal(PlayerViewModel.PreviewText, vm.SubtitleText);   // no live cue: sample line
            h.Press(RemoteKey.Up, RemoteKey.Right);      // size Medium -> Large
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Right);    // colour White -> Yellow
            h.Press(RemoteKey.Down, RemoteKey.Select);   // outline off
            h.Press(RemoteKey.Down, RemoteKey.Right);    // background None -> Translucent
            var style = h.Player.Settings.SubtitleStyle;
            Assert.Equal(54, style.Size);
            Assert.Equal("Large", vm.StyleValue(StyleRow.Size));
            Assert.Equal("Yellow", style.ColorName);
            Assert.False(style.Outline);
            Assert.Equal(SubtitleBackground.Translucent, style.Background);
            Assert.Contains("overlay: subtitle settings", h.Text);
            h.Player.Settings.FlushAsync().Wait();
            var frame = UiLayout.Build(h.Shell);
            var subtitle = frame.Nodes.Single(n => n.Key == "subtitle");
            Assert.Equal(54, subtitle.FontSize);
            Assert.Equal("#FFE14D", subtitle.Color);
            Assert.NotNull(subtitle.Fill);
            h.Press(RemoteKey.Back);                     // one level up: the Audio & subtitles panel
            Assert.False(vm.StyleMenuOpen);
            Assert.Equal(PlayerPanel.Tracks, vm.Panel);
            Assert.True(vm.OverlayVisible);
        }

        [Fact]
        public void SubtitlesRiseWhileOverlayVisible()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.SubtitleText = "Hello world";
            var hidden = vm.SubtitleBottom;
            h.Press(RemoteKey.Select);
            Assert.Equal(hidden + SubtitleStyle.OverlayRaise, vm.SubtitleBottom);
            Assert.Single(UiLayout.Build(h.Shell).Nodes, n => n.Key == "subtitle");
        }

        [Fact]
        public void AppliesRememberedLanguagesOnNextEpisode()
        {
            var h = new UiHarness();
            h.Player.Settings.RememberTracks(UiHarness.Series.Summary.Id, "spa", "off");
            h.StartEpisode();   // clears commands after the first tick
            h.Player.Snapshot.Video = UiHarness.Series.Videos[1];   // binge advanced
            h.Player.Snapshot.SelectedAudio = 0;
            h.Player.Snapshot.SelectedSubtitleId = "external:es";
            h.Shell.Tick();
            Assert.Equal(new[] { "audio 1", "subtitle off" }, h.Player.Commands);
        }

        [Fact]
        public void GlobalDefaultPrefersEnglishAudioSpanishSubtitles()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Shell.StartPlayback(new PlayRequest { Meta = UiHarness.Series, Video = UiHarness.Series.Videos[0] });
            h.Player.Snapshot.AudioTracks = new[] { new TrackInfo { Index = 3, Language = "rus" }, new TrackInfo { Index = 4, Language = "en" } };
            h.Player.Snapshot.SelectedAudio = 3;
            h.Player.Snapshot.Subtitles = new[] { new SubtitleOption { Id = "e:1", Language = "eng" }, new SubtitleOption { Id = "x:es", Language = "es", External = true } };
            h.Player.Snapshot.SelectedSubtitleId = "e:1";
            h.Shell.Tick();
            Assert.Contains("audio 4", h.Player.Commands);
            Assert.Contains("subtitle x:es", h.Player.Commands);
        }

        [Fact]
        public void LeavesPlayerWhenSessionEnds()
        {
            var h = new UiHarness();
            h.StartEpisode();
            h.Player.Snapshot.SessionActive = false;
            h.Shell.Tick();
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void BingeAndStopAfterToggle()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.More);
            h.Press(RemoteKey.Select);
            Assert.Equal(PlayerPanel.More, vm.Panel);
            Assert.Contains("> Start from beginning", h.Text);
            h.Press(RemoteKey.Down, RemoteKey.Select, RemoteKey.Down, RemoteKey.Select);
            Assert.Equal(new[] { "binge True", "stop-after True" }, h.Player.Commands);
            h.Press(RemoteKey.Up, RemoteKey.Up, RemoteKey.Select);     // Start from beginning
            Assert.Contains("seek-to 0", h.Player.Commands);
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.Sources);
            h.Press(RemoteKey.Select);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Down, RemoteKey.Select);
            Assert.Contains("switch bbbb:auto", h.Player.Commands);
            Assert.Same(vm, h.Shell.Current);
        }
    }
}
