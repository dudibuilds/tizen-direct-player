using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Api;
using Ru7100.Core.App;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using Ru7100.Tests.Playback;
using Xunit;
using ApiPlayRequest = Ru7100.Core.Api.PlayRequest;
using EnginePlayRequest = Ru7100.Core.Playback.PlayRequest;
using EnginePlaybackMode = Ru7100.Core.Playback.PlaybackMode;
using UiPlayRequest = Ru7100.Core.Ui.PlayRequest;

namespace Ru7100.Tests.App
{
    public class PlaybackAdapterTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static Harness ShowHarness(out MetaDetail show, int episodes = 3)
        {
            var h = new Harness();
            show = FakeCatalog.Series("tt0903747", "Breaking Bad", 1, episodes);
            h.Catalog.Metas[show.Summary.Id] = show;
            h.Resolver.Streams = videoId => new[] { Harness.Torrent("A-" + videoId) };
            return h;
        }

        private static SessionPlayerControls Controls(Harness h) =>
            new SessionPlayerControls(h.Session, new PlayerSettings(), () => h.Clock.UtcNow);

        [Fact]
        public async Task UiPlayRequestStartsTheEngineAtTheGivenPositionAndMapsTheSnapshot()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                h.Player.AudioFor = _ => new[] { new TrackInfo { Index = 0, Language = "eng" }, new TrackInfo { Index = 1, Language = "jpn" } };
                h.Player.SubtitlesFor = _ => new[] { new TrackInfo { Index = 0, Language = "spa" } };
                h.Resolver.Subtitles = new[] { new SubtitleCandidate { Url = "https://subs/en.srt", Language = "eng" } };
                h.Subtitles.Files["https://subs/en.srt"] = "1\n00:00:01,000 --> 00:00:02,000\nHi\n";
                var controls = Controls(h);
                await controls.PlayAsync(new UiPlayRequest { Meta = show, Video = show.Videos[1], ResumeMs = 61_000 }, None);
                await h.Session.PendingSubtitleWork;

                Assert.Equal(61_000, h.Player.Opens.Single().StartMs);
                var s = controls.Snapshot;
                Assert.True(s.SessionActive);
                Assert.Equal(PlayerState.Playing, s.State);
                Assert.Equal("Breaking Bad", s.Title);
                Assert.Equal("tt0903747:1:2", s.Video.Id);
                Assert.StartsWith("S1E2", s.EpisodeLabel.Replace(" ", ""));
                Assert.Equal(2, s.AudioTracks.Count);
                Assert.Equal(new[] { "embedded:0", "external:https://subs/en.srt" }, s.Subtitles.Select(o => o.Id));
                Assert.True(s.Subtitles[1].External);
                Assert.Equal("1080p", s.SourceLabel.Split(' ')[0]);
            }
        }

        [Fact]
        public async Task PlayFailureThrowsSoThePlayerScreenCanReportIt()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                h.Resolver.Streams = _ => Array.Empty<StreamCandidate>();
                var controls = Controls(h);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    controls.PlayAsync(new UiPlayRequest { Meta = show, Video = show.Videos[0] }, None));
                Assert.Contains("No viable", error.Message);
                Assert.False(controls.Snapshot.SessionActive);
            }
        }

        [Fact]
        public async Task SubtitleCueBecomesSnapshotTextAndExpires()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                h.Player.SubtitlesFor = _ => new[] { new TrackInfo { Index = 3, Language = "eng" } };
                var controls = Controls(h);
                await controls.PlayAsync(new UiPlayRequest { Meta = show, Video = show.Videos[0] }, None);
                await h.Session.PendingSubtitleWork;
                controls.SelectSubtitle("embedded:3");
                await Wait.Until(() => h.Session.GetStatus().Tracks.Subtitle?.Reason == "user", "user subtitle choice");
                Assert.Equal("embedded:3", controls.Snapshot.SelectedSubtitleId);
                Assert.Equal(3, h.Player.SelectedSubtitle);

                h.Player.RaiseCue(new SubtitleCue { Text = "Say my name.", StartMs = 1000, DurationMs = 2000, Embedded = true });
                Assert.Equal("Say my name.", controls.Snapshot.SubtitleText);
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(3));
                Assert.Null(controls.Snapshot.SubtitleText);

                controls.SelectSubtitle(null);
                await Wait.Until(() => h.Session.GetStatus().Tracks.Subtitle?.Kind == SubtitleKind.None, "subtitles off");
                Assert.Null(controls.Snapshot.SelectedSubtitleId);
            }
        }

        [Fact]
        public async Task SessionStaysActiveAcrossABingeAdvanceAndDropsWhenAdvanceFails()
        {
            using (var h = ShowHarness(out var show, episodes: 3))
            {
                await h.StartAsync();
                var controls = Controls(h);
                var seen = new List<bool>();
                h.Session.StatusChanged += (_, status) => { lock (seen) seen.Add(PlaybackMapping.IsSessionActive(status, h.Clock.UtcNow)); };
                await controls.PlayAsync(new UiPlayRequest { Meta = show, Video = show.Videos[0], Mode = Ru7100.Core.Ui.PlaybackMode.Binge }, None);
                Assert.True(controls.Snapshot.Binge);

                h.Player.State = PlayerState.Playing;
                h.Player.DurationMs = 2_400_000;
                h.Player.PositionMs = 2_370_000;
                await h.PlayTicksAsync(3);
                await Wait.Until(() => h.Session.GetStatus().Session?.VideoId == "tt0903747:1:2", "advance to episode 2");
                lock (seen)
                {
                    var firstActive = seen.IndexOf(true);
                    Assert.True(firstActive >= 0);
                    Assert.DoesNotContain(false, seen.Skip(firstActive));
                }
                Assert.True(controls.Snapshot.SessionActive);

                // Episode 3 has no sources: the advance fails and the player screen must close.
                h.Resolver.Streams = videoId => videoId.EndsWith(":3", StringComparison.Ordinal) ? Array.Empty<StreamCandidate>() : new[] { Harness.Torrent("A-" + videoId) };
                h.Player.State = PlayerState.Playing;
                h.Player.DurationMs = 2_400_000;
                h.Player.PositionMs = 2_370_000;
                await h.PlayTicksAsync(3);
                await Wait.Until(() => !h.Session.GetStatus().Active, "advance failure ends the session");
                Assert.Equal("advance_failed", h.Session.GetStatus().LastSession.EndReason);
                Assert.False(controls.Snapshot.SessionActive);
            }
        }

        [Fact]
        public async Task SwitchSourcePlaysThePickedCandidateAtTheCurrentPosition()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                var picked = Harness.Torrent("Picked", tier: 3, band: 0);
                h.Resolver.Streams = videoId => new[] { Harness.Torrent("A-" + videoId), picked };
                var controls = Controls(h);
                await controls.PlayAsync(new UiPlayRequest { Meta = show, Video = show.Videos[0] }, None);
                h.Player.PositionMs = 120_000;
                await controls.SwitchSourceAsync(picked, None);
                Assert.Equal(picked.Identity, h.Session.GetStatus().Session == null ? null : h.Session.CurrentCandidate.Identity);
                Assert.Equal(120_000, h.Player.Opens.Last().StartMs);
                Assert.True(h.Session.GetStatus().Active);
            }
        }

        [Fact]
        public async Task QueueControlsAddRemoveAndStart()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                var controls = Controls(h);
                Assert.Equal("empty", (await controls.GetAsync(None)).Status);
                await controls.AddAsync(show, show.Videos[0], None);
                await controls.AddAsync(show, show.Videos[1], None);
                await controls.AddAsync(show, show.Videos[2], None);
                await controls.RemoveAtAsync(1, None);
                var queue = await controls.GetAsync(None);
                Assert.Equal("ready", queue.Status);
                Assert.Equal(new[] { 1, 3 }, queue.Items.Select(i => i.Episode));

                await controls.StartAsync(None);
                Assert.Equal("playing", (await controls.GetAsync(None)).Status);
                Assert.Equal("queue", h.Session.GetStatus().Session.Mode);
                await Assert.ThrowsAsync<InvalidOperationException>(() => controls.RemoveAtAsync(0, None));
                await controls.SkipAsync(None);
                Assert.Equal("tt0903747:1:3", h.Session.GetStatus().Session.VideoId);
            }
        }

        [Fact]
        public async Task SessionStaysActiveAcrossAQueueSkip()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                var controls = Controls(h);
                await controls.AddAsync(show, show.Videos[0], None);
                await controls.AddAsync(show, show.Videos[1], None);
                await controls.StartAsync(None);
                var seen = new List<bool>();
                h.Session.StatusChanged += (_, status) => { lock (seen) seen.Add(PlaybackMapping.IsSessionActive(status, h.Clock.UtcNow)); };
                await controls.SkipAsync(None);
                Assert.Equal("tt0903747:1:2", h.Session.GetStatus().Session.VideoId);
                lock (seen)
                {
                    Assert.NotEmpty(seen);
                    Assert.DoesNotContain(false, seen);
                }
            }
        }

        [Fact]
        public async Task ApiCommandsMapEngineResultsAndErrorCodes()
        {
            using (var h = ShowHarness(out var show))
            {
                await h.StartAsync();
                h.Player.AudioFor = _ => new[] { new TrackInfo { Index = 0, Language = "eng" }, new TrackInfo { Index = 1, Language = "jpn" } };
                var settings = new PlayerSettings();
                var api = new SessionPlaybackCommands(h.Session, settings);

                var noSession = await Assert.ThrowsAsync<ApiException>(() => api.PauseAsync(None));
                Assert.Equal("no_active_playback", noSession.Code);
                var noQueue = await Assert.ThrowsAsync<ApiException>(() => api.StartQueueAsync(None));
                Assert.Equal("no_queue", noQueue.Code);

                var proposal = await api.PlayAsync(new ApiPlayRequest { Query = "tt0903747" }, None);
                Assert.Equal("confirmation_required", proposal.Status);

                var played = await api.PlayAsync(new ApiPlayRequest { Query = "tt0903747 S1E2", Confirmed = true, Mode = "binge" }, None);
                Assert.Equal("playing", played.Status);
                Assert.Equal("Breaking Bad", played.Series);
                Assert.Equal(2, played.Video.Episode);
                Assert.Equal("binge", played.Mode);
                Assert.Equal("1080p", played.Selected.Quality);
                Assert.Equal("tt0903747", played.Session.Id);
                Assert.Equal(PlayerState.Playing, played.State);

                var paused = await api.PauseAsync(None);
                Assert.Equal(PlayerState.Paused, paused.State);
                var audio = await api.SelectAudioAsync(1, None);
                Assert.Equal("jpn", settings.GetTrackPreference("tt0903747", false).AudioLanguage);
                await Assert.ThrowsAsync<ApiException>(() => api.SelectAudioAsync(7, None));
                var size = await api.SetSubtitleSizeAsync(60, None);
                Assert.Equal(60, settings.SubtitleStyle.Size);
                Assert.Equal(60, size.Session.SubtitleSize);
                var offset = await api.SetSubtitleOffsetAsync(-1500, None);
                Assert.Equal(-1500, offset.Session.SubtitleOffsetMs);

                var status = await api.GetStatusAsync(None);
                Assert.True(status.Active);
                Assert.Equal("tt0903747:1:2", status.Session.VideoId);
                var json = ApiJson.ToToken(status);
                Assert.Equal("PAUSED", (string)json["session"]["state"]);

                var stopped = await api.StopAsync(None);
                Assert.Equal("user_stop", stopped.Session.EndReason);
                var after = await api.GetStatusAsync(None);
                Assert.False(after.Active);
                Assert.Equal("tt0903747:1:2", after.LastSession.VideoId);
                var skip = await Assert.ThrowsAsync<ApiException>(() => api.SkipQueueAsync(None));
                Assert.Equal("no_active_queue", skip.Code);
            }
        }

        [Fact]
        public void SubtitleMenuIndexesMatchBetweenApiAndUi()
        {
            var status = new PlaybackStatus
            {
                Tracks = new TrackStatus
                {
                    EmbeddedSubtitles = new[] { new TrackInfo { Index = 2, Language = "eng" }, new TrackInfo { Index = 5, Language = "spa" } },
                    Subtitle = new SubtitleDecision { Kind = SubtitleKind.External, External = new SubtitleCandidate { Url = "u2", Language = "spa" } },
                },
            };
            var externals = new[] { new SubtitleCandidate { Url = "u1", Language = "spa" }, new SubtitleCandidate { Url = "u2", Language = "spa" } };
            var menu = PlaybackMapping.SubtitleMenu(status, externals);
            Assert.Equal(new[] { "embedded:2", "embedded:5", "external:u2", "external:u1" }, menu.Select(e => e.Id));
            Assert.Equal(new[] { "Spanish", "Spanish 2" }, menu.Skip(2).Select(e => e.Label));
            Assert.Equal(2, PlaybackMapping.SelectedSubtitleIndex(menu, status.Tracks.Subtitle));
            Assert.Equal("external:u2", PlaybackMapping.SelectedSubtitleId(status.Tracks.Subtitle));
            Assert.Equal(-1, PlaybackMapping.SelectedSubtitleIndex(menu, new SubtitleDecision { Kind = SubtitleKind.None }));
        }

        [Fact]
        public void TransitionGraceOnlyCoversTransitionEnds()
        {
            var now = new DateTimeOffset(2026, 11, 18, 21, 0, 0, TimeSpan.Zero);
            PlaybackStatus Ended(string reason, double secondsAgo) => new PlaybackStatus
            {
                Phase = "ended",
                LastSession = new SessionStatus { EndReason = reason, EndedAt = now.AddSeconds(-secondsAgo) },
            };
            Assert.True(PlaybackMapping.IsSessionActive(Ended("episode_advance", 0.5), now));
            Assert.True(PlaybackMapping.IsSessionActive(Ended("queue_skip", 1), now));
            Assert.False(PlaybackMapping.IsSessionActive(Ended("episode_advance", 10), now));
            Assert.False(PlaybackMapping.IsSessionActive(Ended("user_stop", 0), now));
            Assert.False(PlaybackMapping.IsSessionActive(Ended("advance_failed", 0), now));
            Assert.True(PlaybackMapping.IsSessionActive(new PlaybackStatus { Phase = "starting" }, now));
            Assert.False(PlaybackMapping.IsSessionActive(new PlaybackStatus { Phase = "idle" }, now));
        }
    }
}
