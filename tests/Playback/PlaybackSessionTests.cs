using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ru7100.Core;
using Ru7100.Core.Playback;
using Xunit;

namespace Ru7100.Tests.Playback
{
    public class PlaybackSessionTests
    {
        private static readonly CancellationToken None = CancellationToken.None;

        private static Harness ShowHarness(out MetaDetail show, int episodes = 4)
        {
            var h = new Harness();
            show = FakeCatalog.Series("tt100", "Show", 1, episodes);
            h.Catalog.Metas[show.Summary.Id] = show;
            h.Resolver.Streams = videoId => new[] { Harness.Torrent("A-" + videoId), Harness.Torrent("B-" + videoId) };
            return h;
        }

        private static void SetPlaying(Harness h, long position, long duration)
        {
            h.Player.State = PlayerState.Playing;
            h.Player.PositionMs = position;
            h.Player.DurationMs = duration;
        }

        // ---- H1-H3 resume and confirmation ----

        [Fact]
        public async Task BareSeriesTitleProposesResumeAndPlaysAtOffsetWhenConfirmed()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Library.Entries["tt100"] = new LibraryEntry
                {
                    Id = "tt100", State = new LibraryState { VideoId = "tt100:1:2", TimeOffsetMs = 300_000, DurationMs = 2_400_000 },
                };
                var session = await h.StartAsync();
                var proposal = await session.PlayAsync(new PlayRequest { MetaId = "tt100" }, None);
                Assert.Equal(PlayStatus.ConfirmationRequired, proposal.Status);
                Assert.Equal("Resume S1E2 at 5:00?", proposal.Proposal);
                Assert.Equal(0, h.Opener.CallCount);

                var result = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Confirmed = true }, None);
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Equal("tt100:1:2", result.Video.Id);
                Assert.Equal(300_000, h.Player.Opens.Single().StartMs);
                Assert.Equal(PlayerState.Playing, h.Player.State);
            }
        }

        [Fact]
        public async Task FinishedEpisodeProposesNextAndMovieNeedsConfirmation()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Catalog.Metas["tt9"] = FakeCatalog.Movie("tt9", "Film");
                h.Library.Entries["tt100"] = new LibraryEntry
                {
                    Id = "tt100", Created = DateTimeOffset.MaxValue,
                    State = new LibraryState { VideoId = "tt100:1:2", TimeOffsetMs = 2_300_000, DurationMs = 2_400_000 },
                };
                var session = await h.StartAsync();
                var next = await session.PlayAsync(new PlayRequest { MetaId = "tt100" }, None);
                Assert.Equal("Play S1E3?", next.Proposal);

                var movie = await session.PlayAsync(new PlayRequest { MetaId = "tt9" }, None);
                Assert.Equal(PlayStatus.ConfirmationRequired, movie.Status);
                Assert.Equal("Play Film?", movie.Proposal);
            }
        }

        [Fact]
        public async Task ExplicitEpisodePlaysWithoutConfirmationAndResumesOnlyTheSameVideo()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Library.Entries["tt100"] = new LibraryEntry
                {
                    Id = "tt100", State = new LibraryState { VideoId = "tt100:1:2", TimeOffsetMs = 60_000, DurationMs = 2_400_000 },
                };
                var session = await h.StartAsync();
                var other = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 3 }, None);
                Assert.Equal(PlayStatus.Playing, other.Status);
                Assert.Equal(0, h.Player.Opens.Last().StartMs);
                var same = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2 }, None);
                Assert.Equal(60_000, h.Player.Opens.Last().StartMs);
                Assert.Equal(PlayStatus.Playing, same.Status);
            }
        }

        [Fact]
        public async Task AHandPickedSourceOpensWithoutWaitingForDiscovery()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                var picked = Harness.Torrent("Picked-1080p");
                var result = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1, Source = picked }, None);
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Empty(h.Resolver.Requests);   // its ranked list was never used: no discovery round
                Assert.Equal(picked.Identity, session.GetStatus().Source.Identity);
            }
        }

        // ---- H5 binge, E3 completion ----

        [Fact]
        public async Task BingeAdvancesToNextEpisodeAfterCompletion()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                var first = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1, Mode = PlaybackMode.Binge }, None);
                Assert.Equal(PlayStatus.Playing, first.Status);
                SetPlaying(h, 2_370_000, 2_400_000);
                await h.PlayTicksAsync(3);
                await Wait.Until(() => h.Player.LastUrl?.Contains("1:2") == true, "second episode");
                var status = session.GetStatus();
                Assert.Equal("tt100:1:2", status.Session.VideoId);
                Assert.Equal("binge", status.Session.Mode);
                await session.FlushHistoryAsync();
                var events = h.Progress.Snapshot();
                Assert.Contains("completed tt100:1:1", events);
                Assert.Contains("stopped tt100:1:1 True", events);
                Assert.Contains("start tt100:1:2 0", events);
            }
        }

        [Fact]
        public async Task BingeEndsAfterLastEpisode()
        {
            using (var h = ShowHarness(out var show, episodes: 2))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2, Mode = PlaybackMode.Binge }, None);
                SetPlaying(h, 2_390_000, 2_400_000);
                await h.PlayTicksAsync(3, 1000);
                var status = session.GetStatus();
                Assert.False(status.Active);
                Assert.Equal("binge_complete", status.LastSession.EndReason);
            }
        }

        // ---- H6 stop-after ----

        [Fact]
        public async Task StopAfterCurrentEndsBingeAtEndOfEpisodeWithoutAdvancing()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1, Mode = PlaybackMode.Binge }, None);
                var flagged = await session.StopAfterCurrentAsync(true, None);
                Assert.True(flagged.StopAfterCurrent);
                SetPlaying(h, 2_380_000, 2_400_000);
                await h.PlayTicksAsync(3, 2000);
                Assert.True(session.GetStatus().Active);           // credits keep playing
                Assert.Equal(1, h.Player.OpenCount);
                h.Player.State = PlayerState.Completed;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                await session.TickAsync();
                var status = session.GetStatus();
                Assert.False(status.Active);
                Assert.Equal("stop_after", status.LastSession.EndReason);
                Assert.Equal(1, h.Player.OpenCount);

                // A new play starts with the flag cleared.
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2, Mode = PlaybackMode.Binge }, None);
                Assert.False(session.GetStatus().Session.StopAfterCurrent);
            }
        }

        [Fact]
        public async Task StopAfterCurrentInQueueParksQueueAtNextItem()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                var started = await session.QueueStartAsync(new[]
                {
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 1 },
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 3 },
                }, "auto", null, None);
                Assert.Equal("ok", started.Status);
                await session.StopAfterCurrentAsync(true, None);
                SetPlaying(h, 2_390_000, 2_400_000);
                await h.PlayTicksAsync(2, 1000);
                h.Player.State = PlayerState.Completed;
                await session.TickAsync();
                var queue = session.GetQueue();
                Assert.Equal(QueueStatuses.Paused, queue.Status);
                Assert.Equal(1, queue.CurrentIndex);
                Assert.False(session.GetStatus().Active);

                var resumed = await session.QueueStartAsync(null, null, null, None);
                Assert.Equal("ok", resumed.Status);
                Assert.Equal("tt100:1:3", session.GetStatus().Session.VideoId);
            }
        }

        // ---- J queue ----

        [Fact]
        public async Task QueuePlaysItemsInOrderAndCompletes()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                var set = await session.QueueSetAsync(new[]
                {
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 2 },
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 4 },
                }, "1080p", 10, None);
                Assert.Equal(QueueStatuses.Ready, set.Queue.Status);
                Assert.Equal(6.5, set.Queue.MaxSizeGb);
                Assert.Contains("\"Status\":\"ready\"", h.Store.Values[PlaybackKeys.Queue]);

                var start = await session.QueueStartAsync(null, null, null, None);
                Assert.Equal("ok", start.Status);
                Assert.Equal("tt100:1:2", session.GetStatus().Session.VideoId);
                Assert.Equal("1080p", h.Resolver.Requests.Last().Options.Quality);

                SetPlaying(h, 2_390_000, 2_400_000);
                await h.PlayTicksAsync(2, 1000);
                await Wait.Until(() => session.GetStatus().Session?.VideoId == "tt100:1:4", "second queue item");
                Assert.Equal(1, session.GetQueue().CurrentIndex);

                SetPlaying(h, 2_390_000, 2_400_000);
                await h.PlayTicksAsync(2, 1000);
                await Wait.Until(() => session.GetQueue().Status == QueueStatuses.Completed, "queue completed");
                Assert.False(session.GetStatus().Active);
                Assert.Equal("queue_completed", session.GetStatus().LastSession.EndReason);
            }
        }

        [Fact]
        public async Task AQueueItemThatFailsToStartIsNotSkippedPast()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = videoId => videoId == "tt100:1:2" ? new StreamCandidate[0] : new[] { Harness.Torrent("A-" + videoId) };
                var session = await h.StartAsync();
                await session.QueueStartAsync(new[]
                {
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 1 },
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 2 },
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 3 },
                }, null, null, None);
                Assert.Equal("tt100:1:1", session.GetStatus().Session.VideoId);
                SetPlaying(h, 2_390_000, 2_400_000);
                await h.PlayTicksAsync(2, 1000);
                await Wait.Until(() => session.GetQueue().Status == QueueStatuses.Error, "item 2 failing");
                Assert.False(session.GetStatus().Active);                 // item 1 ended; it cannot trigger another advance
                Assert.Equal("advance_failed", session.GetStatus().LastSession.EndReason);
                Assert.Equal(1, session.GetQueue().CurrentIndex);           // a resume retries item 2
                await h.PlayTicksAsync(3, 1000);
                Assert.DoesNotContain(h.Resolver.Requests, r => r.VideoId == "tt100:1:3");
            }
        }

        [Fact]
        public async Task BingeWaitsAtTheCutOffWhilePausedOrHeldForTheCredits()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1, Mode = PlaybackMode.Binge }, None);
                await session.SetBingeAsync(true, None);
                SetPlaying(h, 2_200_000, 2_400_000);
                await h.PlayTicksAsync(2);                                 // watched, before the cut-off
                await session.PauseAsync(None);
                h.Player.State = PlayerState.Paused;
                h.Player.PositionMs = 2_360_000;                           // past the hand-over point, paused
                await h.PlayTicksAsync(2, 0);
                Assert.Equal("tt100:1:1", session.CurrentVideo.Id);
                await session.ResumeAsync(None);
                await session.HoldAdvanceAsync(None);                      // Back on the next-episode card
                h.Player.State = PlayerState.Playing;
                await h.PlayTicksAsync(3, 5000);
                Assert.Equal("tt100:1:1", session.CurrentVideo.Id);       // the credits keep playing
                h.Player.State = PlayerState.Completed;
                await h.PlayTicksAsync(1, 0);
                await Wait.Until(() => session.CurrentVideo?.Id == "tt100:1:2", "the next episode at the end");
            }
        }

        [Fact]
        public async Task QueueSkipAdvancesAndUserStopMarksQueueStopped()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.QueueStartAsync(new[]
                {
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 1 },
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 2 },
                    new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 3 },
                }, null, null, None);
                var skipped = await session.QueueSkipAsync(None);
                Assert.Equal("ok", skipped.Status);
                Assert.Equal("tt100:1:2", session.GetStatus().Session.VideoId);
                Assert.Equal("queue_skip", session.GetStatus().LastSession.EndReason);

                await session.StopAsync(None);
                Assert.Equal(QueueStatuses.Stopped, session.GetQueue().Status);
                Assert.Equal("user_stop", session.GetQueue().LastStopReason);

                await session.QueueClearAsync(None);
                Assert.Null(session.GetQueue());
                Assert.Equal("null", h.Store.Values[PlaybackKeys.Queue]);
            }
        }

        [Fact]
        public async Task EnableBingeAttachesToMatchingQueueAndDisablePausesIt()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.QueueStartAsync(new[] { new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 1 } }, null, null, None);
                var off = await session.SetBingeAsync(false, None);
                Assert.Equal("single", off.Mode);
                Assert.Equal(QueueStatuses.Paused, session.GetQueue().Status);
                var on = await session.SetBingeAsync(true, None);
                Assert.Equal("queue", on.Mode);
                Assert.Equal(QueueStatuses.Playing, session.GetQueue().Status);
            }
        }

        // ---- H7 superseding ----

        [Fact]
        public async Task NewerPlaySupersedesStartInProgress()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = videoId => new[] { Harness.Torrent("slow-" + videoId) };
                h.Opener.Blocked.Add("slow-tt100:1:1");
                var session = await h.StartAsync();
                var first = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => h.Opener.CallCount == 1, "first open");
                var second = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2 }, None);
                Assert.Equal(PlayStatus.Playing, second.Status);
                Assert.Equal(PlayStatus.Superseded, (await first).Status);
                Assert.True(h.Opener.Call("slow-tt100:1:1").Token.IsCancellationRequested);
                Assert.Equal("tt100:1:2", session.GetStatus().Session.VideoId);
                Assert.Single(h.Player.Opens);
                // Superseded preparations are not blacklisted.
                Assert.Empty(session.Blacklist.Snapshot());
            }
        }

        [Fact]
        public async Task StopCancelsStartInProgress()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = videoId => new[] { Harness.Torrent("slow") };
                h.Opener.Blocked.Add("slow");
                var session = await h.StartAsync();
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => h.Opener.CallCount == 1, "open");
                await session.StopAsync(None);
                Assert.Equal(PlayStatus.Superseded, (await play).Status);
                Assert.Empty(h.Player.Opens);
            }
        }

        // ---- G1-G4 race, reserve, blacklist ----

        [Fact]
        public async Task RaceHedgesTwoThenThreeAndReserveJoinsAfterTenSeconds()
        {
            using (var h = ShowHarness(out var show))
            {
                var group = new[] { Harness.Torrent("g1"), Harness.Torrent("g2"), Harness.Torrent("g3"), Harness.Torrent("g4") };
                var reserve = Harness.Torrent("r1", tier: 6);
                var weak = Harness.Torrent("weak", tier: 7, band: 0);
                h.Resolver.Streams = _ => group.Concat(new[] { reserve, weak }).ToList();
                // Parity race values (G2); the TV defaults are wider and faster, this test checks the mechanism.
                h.Options.Race = new RaceSettings { InitialConcurrency = 2, MaxConcurrent = 3, HedgeDelay = TimeSpan.FromMilliseconds(750),
                    ReserveDelay = TimeSpan.FromSeconds(10), ReserveExtraSlots = 2 };
                foreach (var name in new[] { "g1", "g2", "g3", "g4", "r1" })
                    h.Opener.Blocked.Add(name);
                var session = await h.StartAsync();
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);

                await Wait.Until(() => h.Opener.CallCount == 2, "initial two");
                await h.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(749));
                Assert.Equal(2, h.Opener.CallCount);
                await h.Clock.AdvanceAsync(TimeSpan.FromMilliseconds(1));
                await Wait.Until(() => h.Opener.CallCount == 3, "hedged third");
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, h.Opener.CallCount);          // max concurrency 3
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4.25));   // t = 10 s: reserve joins with 2 extra slots
                await Wait.Until(() => h.Opener.CallCount == 4, "first extra slot");
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(0.75));   // next hedge tick fills the second
                await Wait.Until(() => h.Opener.CallCount == 5, "reserve slots");
                var names = h.Opener.Calls.Select(c => c.Candidate.Name).ToList();
                Assert.Equal(new[] { "g1", "g2", "g3", "g4", "r1" }, names);
                Assert.DoesNotContain("weak", names);           // different health band is never a rival

                h.Opener.Complete(h.Opener.Call("r1"));
                var result = await play;
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Equal("http://127.0.0.1/r1", h.Player.LastUrl);
                foreach (var name in new[] { "g1", "g2", "g3", "g4" })
                    Assert.True(h.Opener.Call(name).Token.IsCancellationRequested);
                // Losers that prepared for 10 s or more are treated as stalled (G4); g4 only ran 0.75 s.
                try { await Wait.Until(() => session.Blacklist.Snapshot().Count == 3, "stalled losers", 2000); }
                catch (TimeoutException)
                {
                    var stalledNames = group.Concat(new[] { reserve }).Where(c => session.Blacklist.IsStalled(c.InfoHash)).Select(c => c.Name);
                    Assert.Fail("stalled: " + string.Join(",", stalledNames) + " calls: "
                        + string.Join(",", h.Opener.Calls.Select(c => c.Candidate.Name + "@" + (c.At - h.Opener.Calls[0].At).TotalSeconds)));
                }
                Assert.False(session.Blacklist.IsStalled(group[3].InfoHash));
            }
        }

        [Fact]
        public async Task AHealthyCandidateCutsTheRaceAndTheCutOnesAreNeitherFailuresNorStalled()
        {
            using (var h = ShowHarness(out var show))
            {
                var group = new[] { Harness.Torrent("g1"), Harness.Torrent("g2"), Harness.Torrent("g3") };
                h.Resolver.Streams = _ => group.ToList();
                h.Options.Race = new RaceSettings { InitialConcurrency = 3, MaxConcurrent = 3, HedgeDelay = TimeSpan.FromMinutes(10) };
                foreach (var name in new[] { "g1", "g2", "g3" })
                    h.Opener.Blocked.Add(name);
                var session = await h.StartAsync();
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => h.Opener.CallCount == 3, "three racing");
                await Wait.Until(() => session.GetStatus().StepRacing == 3, "status counts the parallel candidates");
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(11));      // past StallAfter: a failure now would blacklist

                h.Opener.Call("g2").Progress(new OpenProgress { HasMetadata = true, Peers = 2, DownloadBytesPerSecond = 1e6 });
                Assert.False(h.Opener.Call("g1").Token.IsCancellationRequested);   // 2 peers: not healthy yet
                h.Opener.Call("g2").Progress(new OpenProgress { HasMetadata = true, Peers = 5, DownloadBytesPerSecond = 1e6 });
                Assert.True(h.Opener.Call("g1").Token.IsCancellationRequested);
                Assert.True(h.Opener.Call("g3").Token.IsCancellationRequested);
                Assert.False(h.Opener.Call("g2").Token.IsCancellationRequested);
                await Wait.Until(() => session.GetStatus().StepRacing == 1, "status shows the leader alone");

                h.Opener.Complete(h.Opener.Call("g2"));
                var result = await play;
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Equal("http://127.0.0.1/g2", h.Player.LastUrl);
                Assert.Empty(result.FallbacksTried);
                await Task.Delay(50);
                Assert.Empty(session.Blacklist.Snapshot());
            }
        }

        [Fact]
        public async Task StalledSourceIsBlacklistedPersistedAndSkippedNextTime()
        {
            using (var h = ShowHarness(out var show))
            {
                var stalled = Harness.Torrent("stalled");
                var good = Harness.Torrent("good");
                h.Resolver.Streams = _ => new[] { stalled, good };
                h.Opener.Blocked.Add("stalled");
                h.Opener.Blocked.Add("good");
                h.Options.Race.InitialConcurrency = 1;
                h.Options.Race.HedgeDelay = TimeSpan.FromMinutes(10);
                h.Options.PrepareRetries = 0;
                var session = await h.StartAsync();
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => h.Opener.CallCount == 1, "first open");
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(30));   // prepare budget expires
                await Wait.Until(() => h.Opener.CallCount == 2, "fallback");
                h.Opener.Complete(h.Opener.Call("good"));
                var result = await play;
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Single(result.FallbacksTried);
                await session.Blacklist.Flushed;
                Assert.Contains(stalled.InfoHash, h.Store.Values[StallBlacklist.StoreKey]);

                // A fresh app run loads the blacklist and tries the healthy source first.
                using (var next = new Harness(h.Store))
                {
                    next.Catalog.Metas["tt100"] = show;
                    next.Resolver.Streams = _ => new[] { stalled, good };
                    var again = await next.StartAsync();
                    var replay = await again.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2 }, None);
                    Assert.Equal(PlayStatus.Playing, replay.Status);
                    Assert.Equal("good", next.Opener.Calls.First().Candidate.Name);
                    Assert.True(again.Blacklist.IsStalled(stalled.InfoHash));
                    await next.Clock.AdvanceAsync(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
                    Assert.False(again.Blacklist.IsStalled(stalled.InfoHash));
                }
            }
        }

        [Fact]
        public async Task AllCandidatesFailingReportsEveryError()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = _ => new[] { Harness.Torrent("x"), Harness.Torrent("y") };
                h.Opener.Failing["x"] = "no peers";
                h.Opener.Failing["y"] = "no video";
                var session = await h.StartAsync();
                var result = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.Equal(PlayStatus.Failed, result.Status);
                Assert.Contains("no peers", result.Error);
                Assert.Contains("no video", result.Error);
                Assert.Equal(2, result.FallbacksTried.Count);
            }
        }

        [Fact]
        public async Task AttemptsAreCappedAtEight()
        {
            using (var h = ShowHarness(out var show))
            {
                var many = Enumerable.Range(0, 12).Select(i => Harness.Torrent("c" + i)).ToList();
                h.Resolver.Streams = _ => many;
                foreach (var c in many)
                    h.Opener.Failing[c.Name] = "dead";
                var session = await h.StartAsync();
                var result = await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.Equal(PlayStatus.Failed, result.Status);
                Assert.Equal(8, h.Opener.CallCount);
            }
        }

        // ---- F12 track rules ----

        [Fact]
        public async Task SourceWithWrongPromisedAudioIsRejectedAndNextCandidatePlays()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = _ => new[] { Harness.Torrent("dubbed"), Harness.Torrent("english") };
                h.Options.Race.InitialConcurrency = 1;
                h.Opener.Blocked.Add("english");
                h.Player.AudioFor = url => url.EndsWith("dubbed")
                    ? new[] { new TrackInfo { Index = 0, Language = "rus" } }
                    : new[] { new TrackInfo { Index = 0, Language = "rus" }, new TrackInfo { Index = 1, Language = "eng" } };
                var session = await h.StartAsync();
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => h.Player.OpenCount == 1, "dubbed open");
                await Wait.Until(() => h.Opener.Call("english") != null, "next candidate");
                h.Opener.Complete(h.Opener.Call("english"));
                var result = await play;
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Contains("English audio was promised", result.FallbacksTried.Single().Error);
                Assert.Equal(1, h.Player.SelectedAudio);
                Assert.True(h.Opener.Opened.First(s => s.PlayerUrl.EndsWith("dubbed")).Disposed);
            }
        }

        [Fact]
        public async Task AudioChoiceIsRememberedPerShowAndAppliedToNextEpisode()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Player.AudioFor = _ => new[]
                {
                    new TrackInfo { Index = 0, Language = "eng" }, new TrackInfo { Index = 1, Language = "spa" },
                };
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.Equal(0, h.Player.SelectedAudio);
                await session.SelectAudioAsync(1, None);
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2 }, None);
                Assert.Equal(1, h.Player.SelectedAudio);
                Assert.Contains("spa", h.Store.Values[PlaybackKeys.TrackPreferences]);
            }
        }

        // ---- F14 subtitles ----

        [Fact]
        public async Task ExternalSpanishBeatsEmbeddedEnglishAndCuesFlowThroughPump()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Player.SubtitlesFor = _ => new[] { new TrackInfo { Index = 0, Language = "eng" } };
                h.Resolver.Subtitles = new[] { new SubtitleCandidate { Url = "http://subs/es.srt", Language = "spa" } };
                h.Subtitles.Files["http://subs/es.srt"] = "1\n00:00:01,000 --> 00:00:03,000\nHola\n";
                var cues = new List<SubtitleCue>();
                var session = await h.StartAsync();
                session.SubtitleCue += (_, cue) => { lock (cues) cues.Add(cue); };
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;
                var decision = session.GetStatus().Tracks.Subtitle;
                Assert.Equal(SubtitleKind.External, decision.Kind);
                Assert.Equal("spa", decision.Language);
                Assert.Null(h.Player.SelectedSubtitle);

                Assert.Equal(1000, session.SetSubtitleOffset(1000));
                h.Player.PositionMs = 2500;   // media 1500 after the +1 s offset
                Assert.Equal(30000, session.SetSubtitleOffset(45000));
                session.SetSubtitleOffset(1000);
                lock (cues)
                    Assert.Contains(cues, c => c.Text == "Hola");
                Assert.Equal(1000, h.Player.SubtitleOffset);
            }
        }

        [Fact]
        public async Task EmbeddedSpanishIsSelectedWithoutWaitingForExternal()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Player.SubtitlesFor = _ => new[]
                {
                    new TrackInfo { Index = 0, Language = "spa", Label = "Spanish (Forced)" },
                    new TrackInfo { Index = 1, Language = "spa" },
                };
                h.Resolver.SubtitlesTask = new TaskCompletionSource<IReadOnlyList<SubtitleCandidate>>().Task;   // never answers
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => session.GetStatus().Tracks.Subtitle != null, "decision");
                var decision = session.GetStatus().Tracks.Subtitle;
                Assert.Equal(SubtitleKind.Embedded, decision.Kind);
                Assert.Equal(1, decision.EmbeddedIndex);       // full dialogue beats forced
                Assert.Equal(1, h.Player.SelectedSubtitle);
                var seen = new List<SubtitleCue>();
                session.SubtitleCue += (_, cue) => seen.Add(cue);
                h.Player.RaiseCue(new SubtitleCue { Text = "hi", Embedded = true, DurationMs = 1000 });
                Assert.Single(seen);
            }
        }

        [Fact]
        public async Task SubtitlePreferenceOffIsRemembered()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Player.SubtitlesFor = _ => new[] { new TrackInfo { Index = 0, Language = "spa" } };
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;
                await session.SelectSubtitleAsync(new SubtitleDecision { Kind = SubtitleKind.None }, None);
                Assert.Null(h.Player.SelectedSubtitle);
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2 }, None);
                await session.PendingSubtitleWork;
                Assert.Equal(SubtitleKind.None, session.GetStatus().Tracks.Subtitle.Kind);
            }
        }

        // ---- I5-I8 failover ----

        [Fact]
        public async Task StarvedPlayerIsRecoveredAtLastPosition()
        {
            using (var h = ShowHarness(out var show))
            {
                var sources = Enumerable.Range(0, 6).Select(i => Harness.Torrent("s" + i)).ToList();
                h.Resolver.Streams = _ => sources;
                h.Options.Race.InitialConcurrency = 1;
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.EndsWith("s0", h.Player.LastUrl);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                h.Player.State = PlayerState.Error;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                await session.TickAsync();
                Assert.EndsWith("s1", h.Player.LastUrl);
                Assert.Equal(610_000, h.Player.Opens.Last().StartMs);
                Assert.Equal(1, session.GetStatus().Session.RecoverySwitches);
                Assert.Equal("stream_recovery", session.GetStatus().LastSession.EndReason);
                await session.FlushHistoryAsync();
                Assert.Contains("stopped tt100:1:1 False", h.Progress.Snapshot());
            }
        }

        [Fact]
        public async Task RejectedReplacementEndsSessionAsStreamFailedAndInterruptsQueue()
        {
            using (var h = ShowHarness(out var show))
            {
                var sources = new[] { Harness.Torrent("good"), Harness.Torrent("dub") };
                h.Resolver.Streams = _ => sources;
                h.Options.Race.InitialConcurrency = 1;
                h.Player.AudioFor = url => url.EndsWith("dub") ? new[] { new TrackInfo { Index = 0, Language = "rus" } } : new TrackInfo[0];
                var session = await h.StartAsync();
                await session.QueueStartAsync(new[] { new QueueItemRequest { MetaId = "tt100", Season = 1, Episode = 1 } }, null, null, None);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                h.Player.State = PlayerState.Error;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                await session.TickAsync();
                Assert.EndsWith("dub", h.Player.LastUrl);           // tried, then rejected by the audio check
                var status = session.GetStatus();
                Assert.False(status.Active);
                Assert.Equal("stream_failed", status.LastSession.EndReason);
                Assert.Contains("English audio was promised", status.LastSession.RecoveryError);
                Assert.Equal(QueueStatuses.Interrupted, session.GetQueue().Status);
            }
        }

        [Fact]
        public async Task FailedReplacementStartsCooldownForStallRecovery()
        {
            using (var h = ShowHarness(out var show))
            {
                var sources = new[] { Harness.Torrent("stuck"), Harness.Torrent("dead"), Harness.Torrent("late") };
                h.Resolver.Streams = _ => sources;
                h.Options.Race.InitialConcurrency = 1;
                h.Opener.Failing["dead"] = "no peers";
                h.Opener.Failing["late"] = "no peers";
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 100_000, 2_400_000);
                await h.PlayTicksAsync(4);
                h.Player.State = PlayerState.Buffering;
                var calls = 0;
                for (var i = 0; i < 40 && calls == 0; i++)
                {
                    await h.PlayTicksAsync(1, 0);
                    calls = h.Opener.CallCount - 1;
                }
                Assert.Equal(2, calls);                               // both alternatives tried and failed
                Assert.True(session.GetStatus().Active);             // stalled but still playing the original
                Assert.Contains("no peers", session.GetStatus().Session.RecoveryError);
                await h.PlayTicksAsync(12, 0);                        // 1 min later: still cooling down
                Assert.Equal(3, h.Opener.CallCount);
            }
        }

        [Fact]
        public async Task ReplacementsStopAfterThreeSwitches()
        {
            using (var h = ShowHarness(out var show))
            {
                var sources = Enumerable.Range(0, 6).Select(i => Harness.Torrent("s" + i)).ToList();
                h.Resolver.Streams = _ => sources;
                h.Options.Race.InitialConcurrency = 1;
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                for (var i = 1; i <= 4; i++)
                {
                    SetPlaying(h, 600_000, 2_400_000);
                    await h.PlayTicksAsync(2);
                    await h.Clock.AdvanceAsync(TimeSpan.FromMinutes(5));
                    h.Player.State = PlayerState.Error;
                    await session.TickAsync();
                    if (i <= 3)
                    {
                        Assert.EndsWith("s" + i, h.Player.LastUrl);
                        Assert.Equal(i, session.GetStatus().Session.RecoverySwitches);
                        Assert.DoesNotContain(h.Resolver.Requests.Last().Options.ExcludeIdentities, id => id == sources[i].Identity);
                        Assert.Contains(sources[i - 1].Identity, h.Resolver.Requests.Last().Options.ExcludeIdentities);
                    }
                }
                Assert.False(session.GetStatus().Active);
                Assert.Equal("stream_failed", session.GetStatus().LastSession.EndReason);
                Assert.Equal(4, h.Player.OpenCount);
            }
        }

        [Fact]
        public async Task PersistentStallTriggersReplacementAfterGracePeriods()
        {
            using (var h = ShowHarness(out var show))
            {
                var candidates = new[] { Harness.Torrent("stuck"), Harness.Torrent("fresh") };
                h.Resolver.Streams = _ => candidates;
                h.Options.Race.InitialConcurrency = 1;
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 100_000, 2_400_000);
                await h.PlayTicksAsync(4);                       // healthy
                h.Player.State = PlayerState.Buffering;
                var ticks = 0;
                while (h.Player.LastUrl.EndsWith("stuck") && ticks < 60)
                {
                    await h.PlayTicksAsync(1, 0);                // frozen playhead
                    ticks++;
                }
                Assert.True(h.Player.LastUrl.EndsWith("fresh"), string.Join("\n", h.Log) + "\n" + JsonConvert.SerializeObject(session.GetStatus()));
                // Stalled after 20 s frozen, replaced after 20 s more with nothing arriving at the playhead (was ~120 s).
                Assert.InRange(ticks, 7, 10);
                Assert.Equal(1, session.GetStatus().Session.RecoverySwitches);
            }
        }

        // ---- failed replacements ----

        [Fact]
        public async Task RecoveryNeverRacesAnotherFileOfThePlayingTorrent()
        {
            using (var h = ShowHarness(out var show))
            {
                var sources = new[] { Harness.Torrent("pack", fileIndex: 1), Harness.Torrent("pack", fileIndex: 2), Harness.Torrent("other") };
                h.Resolver.Streams = _ => sources;
                h.Options.Race.InitialConcurrency = 1;
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.Equal(sources[0].Identity, session.CurrentCandidate.Identity);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                h.Player.State = PlayerState.Error;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                await session.TickAsync();
                Assert.EndsWith("other", h.Player.LastUrl);
                lock (h.Opener.Calls)
                    Assert.DoesNotContain(h.Opener.Calls, c => c.Candidate.Identity == sources[1].Identity);
            }
        }

        [Fact]
        public async Task FailedSourcePickRestoresThePreviousSourceAtItsPosition()
        {
            using (var h = ShowHarness(out var show))
            {
                var good = Harness.Torrent("good");
                var dub = Harness.Torrent("dub");
                h.Resolver.Streams = _ => new[] { good, dub };
                h.Options.Race.InitialConcurrency = 1;
                h.Player.AudioFor = url => url.EndsWith("dub") ? new[] { new TrackInfo { Index = 0, Language = "rus" } } : new TrackInfo[0];
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);                                   // confirmed at 610 s
                var result = await session.SwitchSourceAsync(dub, None);
                Assert.Equal(PlayStatus.Failed, result.Status);
                Assert.Equal(3, h.Player.OpenCount);                          // good, dub (rejected), good again
                Assert.EndsWith("good", h.Player.LastUrl);
                Assert.Equal(610_000, h.Player.Opens.Last().StartMs);
                Assert.Equal(PlayerState.Playing, h.Player.State);
                var status = session.GetStatus();
                Assert.True(status.Active);
                Assert.Equal("playing", status.Phase);
                Assert.Equal(good.Identity, status.Source.Identity);
            }
        }

        [Fact]
        public async Task FailedReplacementWhoseRestoreFailsEndsTheSession()
        {
            using (var h = ShowHarness(out var show))
            {
                var good = Harness.Torrent("good");
                var dub = Harness.Torrent("dub");
                h.Resolver.Streams = _ => new[] { good, dub };
                h.Options.Race.InitialConcurrency = 1;
                h.Player.AudioFor = url => url.EndsWith("dub") ? new[] { new TrackInfo { Index = 0, Language = "rus" } } : new TrackInfo[0];
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                var opens = 0;
                h.Player.OpenHook = (url, start, token) =>
                {
                    if (++opens == 2 && url.EndsWith("good"))
                        throw new InvalidOperationException("prepare failed");   // the restore
                    h.Player.State = PlayerState.Paused;
                    h.Player.AudioTracks = h.Player.AudioFor(url);
                    return Task.CompletedTask;
                };
                var result = await session.SwitchSourceAsync(dub, None);
                Assert.Equal(PlayStatus.Failed, result.Status);
                var status = session.GetStatus();
                Assert.False(status.Active);
                Assert.Equal("stream_failed", status.LastSession.EndReason);
            }
        }

        [Fact]
        public async Task StopAbandonsARestoreWhosePrepareNeverFinishes()
        {
            using (var h = ShowHarness(out var show))
            {
                var good = Harness.Torrent("good");
                var dub = Harness.Torrent("dub");
                h.Resolver.Streams = _ => new[] { good, dub };
                h.Options.Race.InitialConcurrency = 1;
                h.Player.AudioFor = url => url.EndsWith("dub") ? new[] { new TrackInfo { Index = 0, Language = "rus" } } : new TrackInfo[0];
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                var opens = 0;
                h.Player.OpenHook = (url, start, token) =>
                {
                    if (++opens == 2)
                        return new TaskCompletionSource<bool>().Task;   // the restore's prepare hangs (ignores its token)
                    h.Player.State = PlayerState.Paused;
                    h.Player.AudioTracks = h.Player.AudioFor(url);
                    return Task.CompletedTask;
                };
                var pick = session.SwitchSourceAsync(dub, None);
                await Wait.Until(() => h.Player.OpenCount == 3, "the restore");
                var stop = session.StopAsync(None);
                Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(5000)));
                Assert.False(session.GetStatus().Active);
                Assert.Equal("user_stop", session.GetStatus().LastSession.EndReason);
                await pick;
            }
        }

        [Fact]
        public async Task StopDuringAHungPrepareReleasesThePlayer()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                h.Player.OpenHook = (url, start, token) =>
                {
                    h.Player.State = PlayerState.Preparing;
                    return new TaskCompletionSource<bool>().Task;   // never prepares
                };
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => h.Player.OpenCount == 1, "the prepare");
                await session.StopAsync(None);
                Assert.Equal(PlayStatus.Superseded, (await play).Status);
                Assert.Equal(1, h.Player.Stops);
                Assert.False(session.GetStatus().Active);
            }
        }

        // ---- bounded seek ----

        [Fact]
        public async Task StopEndsASeekThatNeverCompletes()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                h.Player.SeekHook = (_, __) => new TaskCompletionSource<bool>().Task;   // ignores its token too
                var seek = session.SeekAsync(1_500_000, None);
                await ManualClock.Settle();
                Assert.False(seek.IsCompleted);
                var stop = session.StopAsync(None);
                Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(5000)));
                await stop;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek);
                Assert.False(session.GetStatus().Active);
                Assert.Equal("user_stop", session.GetStatus().LastSession.EndReason);
                Assert.True(h.Player.Stops >= 1);
            }
        }

        [Fact]
        public async Task HungSeekTimesOutAndReopensTheSourceAtTheLastConfirmedPosition()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                var first = h.Player.LastUrl;
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);                                   // confirmed at 610 s
                h.Player.SeekHook = (_, __) => new TaskCompletionSource<bool>().Task;
                var seek = session.SeekAsync(1_500_000, None);
                await ManualClock.Settle();
                await session.TickAsync();                                    // the monitor skips while the seek holds the gate
                Assert.False(seek.IsCompleted);
                h.Player.SeekHook = null;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(31));
                await Assert.ThrowsAsync<TimeoutException>(() => seek);
                await Wait.Until(() => h.Player.OpenCount == 2, "the reopen");
                Assert.Equal(first, h.Player.LastUrl);                       // the same source
                Assert.Equal(610_000, h.Player.Opens.Last().StartMs);
                await Wait.Until(() => session.GetStatus().Phase == "playing", "playing again");
                Assert.True(session.GetStatus().Active);
                Assert.Equal("stream_recovery", session.GetStatus().LastSession.EndReason);
            }
        }

        [Fact]
        public async Task HungSeekWhoseReopenFailsEndsTheSession()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                h.Player.SeekHook = (_, __) => new TaskCompletionSource<bool>().Task;
                h.Opener.Failing[session.CurrentCandidate.Name] = "swarm gone";
                var seek = session.SeekAsync(1_500_000, None);
                await ManualClock.Settle();
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(31));
                await Assert.ThrowsAsync<TimeoutException>(() => seek);
                await Wait.Until(() => !session.GetStatus().Active, "a terminal outcome");
                Assert.Equal("seek_failed", session.GetStatus().LastSession.EndReason);
            }
        }

        // ---- recovery timing ----

        private static async Task<int> FrozenTicksUntilAsync(Harness h, Func<bool> done, int max, Action<int> each = null)
        {
            var ticks = 0;
            while (!done() && ticks < max)
            {
                each?.Invoke(ticks);
                await h.PlayTicksAsync(1, 0);
                ticks++;
            }
            return ticks;
        }

        [Fact]
        public async Task RisingThroughputWithoutDataAtThePlayheadStillFailsOver()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = _ => new[] { Harness.Torrent("stuck"), Harness.Torrent("fresh") };
                h.Options.Race.InitialConcurrency = 1;
                h.Options.ThroughputSampleInterval = TimeSpan.FromSeconds(5);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                var source = h.Opener.Opened.Last();
                source.ContiguousBytesAhead = 0;                 // the piece at the playhead is missing
                source.ReadyThroughBytes = 10_000_000;           // and the ready frontier does not move
                SetPlaying(h, 100_000, 2_400_000);
                await h.PlayTicksAsync(4);
                h.Player.State = PlayerState.Buffering;
                // Unrelated pieces keep the aggregate rate climbing; under the old rule that held recovery for minutes.
                var ticks = await FrozenTicksUntilAsync(h, () => h.Player.LastUrl.EndsWith("fresh"), 60,
                    i => source.DownloadBytesPerSecond *= 1.5);
                Assert.True(h.Player.LastUrl.EndsWith("fresh"));
                Assert.InRange(ticks, 7, 10);
            }
        }

        [Fact]
        public async Task DataArrivingAtThePlayheadGetsTimeBeforeReplacement()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = _ => new[] { Harness.Torrent("slow"), Harness.Torrent("fresh") };
                h.Options.Race.InitialConcurrency = 1;
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                var source = h.Opener.Opened.Last();
                source.ContiguousBytesAhead = 0;
                SetPlaying(h, 100_000, 2_400_000);
                await h.PlayTicksAsync(4);
                h.Player.State = PlayerState.Buffering;
                source.ReadyThroughBytes = 50_000_000;
                // Each piece is consumed as it lands (contiguous stays 0), but the ready frontier keeps moving.
                var ticks = await FrozenTicksUntilAsync(h, () => h.Player.LastUrl.EndsWith("fresh"), 80,
                    i => source.ReadyThroughBytes += 512 * 1024);
                Assert.True(h.Player.LastUrl.EndsWith("fresh"));     // but not forever
                Assert.InRange(ticks, 38, 45);                         // stalled at 20 s, then up to 180 s of feeding
            }
        }

        [Fact]
        public async Task StallInTheClosingSecondsOfAWatchedMovieEndsIt()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 2_330_000, 2_400_000);
                await h.PlayTicksAsync(3);                              // watched, 55 s left
                h.Player.PositionMs = 2_380_000;                        // 20 s left: the old check ignored this window
                h.Player.State = PlayerState.Buffering;
                await h.PlayTicksAsync(1, 0);
                var ticks = await FrozenTicksUntilAsync(h, () => !session.GetStatus().Active, 40);
                Assert.False(session.GetStatus().Active);
                Assert.Equal("ended", session.GetStatus().LastSession.EndReason);
                Assert.InRange(ticks, 3, 12);
            }
        }

        [Fact]
        public async Task ErrorAfterTheWatchedThresholdAdvancesTheBinge()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1, Mode = PlaybackMode.Binge }, None);
                SetPlaying(h, 2_200_000, 2_400_000);
                await session.SetBingeAsync(true, None);
                await h.PlayTicksAsync(3);
                Assert.True(session.GetStatus().Session.MarkedWatched);
                h.Player.State = PlayerState.Error;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                await session.TickAsync();
                await Wait.Until(() => session.CurrentVideo?.Id == "tt100:1:2", "the next episode");
            }
        }

        [Fact]
        public async Task FailedSoftReplacementsCountAndAnUnrecoverableStallEnds()
        {
            using (var h = ShowHarness(out var show))
            {
                h.Resolver.Streams = _ => new[] { Harness.Torrent("stuck"), Harness.Torrent("dead"), Harness.Torrent("late") };
                h.Options.Race.InitialConcurrency = 1;
                h.Opener.Failing["dead"] = "no peers";
                h.Opener.Failing["late"] = "no peers";
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                h.Opener.Opened.Last().ContiguousBytesAhead = 0;
                SetPlaying(h, 100_000, 2_400_000);
                await h.PlayTicksAsync(4);
                h.Player.State = PlayerState.Buffering;
                var ticks = await FrozenTicksUntilAsync(h, () => !session.GetStatus().Active, 120);
                var status = session.GetStatus();
                Assert.False(status.Active);
                Assert.Equal("stream_failed", status.LastSession.EndReason);
                Assert.Equal(1, status.LastSession.RecoveryFailures);
                Assert.Equal(3, h.Opener.CallCount);                    // one attempt (two failed opens), no loop
                Assert.InRange(ticks, 20, 40);                          // GiveUpAfter (120 s) from when the playhead froze
            }
        }

        // ---- subtitles off the session gate ----

        private static SubtitleDecision External(string url) =>
            new SubtitleDecision { Kind = SubtitleKind.External, External = new SubtitleCandidate { Url = url, Language = "spa" } };

        [Fact]
        public async Task AStalledSubtitleDownloadNeverHoldsPauseOrStop()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 60_000, 2_400_000);
                h.Subtitles.Hook = (url, token) => new TaskCompletionSource<string>().Task;   // headers, then nothing
                var select = session.SelectSubtitleAsync(External("http://subs/stalled.srt"), None);
                await ManualClock.Settle();
                Assert.False(select.IsCompleted);
                var pause = session.PauseAsync(None);
                Assert.Same(pause, await Task.WhenAny(pause, Task.Delay(5000)));
                var stop = session.StopAsync(None);
                Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(5000)));
                Assert.False(session.GetStatus().Active);
            }
        }

        [Fact]
        public async Task ASubtitleDownloadThatNeverFinishesIsReportedAsFailed()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 60_000, 2_400_000);
                h.Subtitles.Hook = (url, token) => new TaskCompletionSource<string>().Task;
                var select = session.SelectSubtitleAsync(External("http://subs/stalled.srt"), None);
                await ManualClock.Settle();
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(26));
                await select;
                Assert.StartsWith("load_failed", session.GetStatus().Tracks.Subtitle.Reason);
                Assert.True(session.GetStatus().Active);
            }
        }

        [Fact]
        public async Task ALateSubtitleDownloadDoesNotReplaceANewerChoice()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 1_000, 2_400_000);
                var slow = new TaskCompletionSource<string>();
                h.Subtitles.Hook = (url, token) => slow.Task;
                var cues = new List<SubtitleCue>();
                session.SubtitleCue += (_, cue) => { lock (cues) cues.Add(cue); };
                var first = session.SelectSubtitleAsync(External("http://subs/slow.srt"), None);
                await ManualClock.Settle();
                await session.SelectSubtitleAsync(new SubtitleDecision { Kind = SubtitleKind.None }, None);
                slow.SetResult("1\n00:00:00,500 --> 00:00:05,000\nlate cue\n");
                await first;
                await session.SeekAsync(2_000, None);
                lock (cues)
                    Assert.DoesNotContain(cues, c => c.Text == "late cue");
                Assert.Equal(SubtitleKind.None, session.GetStatus().Tracks.Subtitle.Kind);
            }
        }

        // ---- storage off the gate ----

        private sealed class SlowStore : IKeyValueStore
        {
            public readonly MemoryStore Inner = new MemoryStore();
            public TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int Writes;
            public Task<string> ReadAsync(string key, CancellationToken token) => Inner.ReadAsync(key, token);
            public async Task WriteAsync(string key, string value, CancellationToken token)
            {
                if (key == PlaybackKeys.Session)
                {
                    Interlocked.Increment(ref Writes);
                    await Gate.Task;   // a flash write that takes forever
                }
                await Inner.WriteAsync(key, value, token);
            }
        }

        [Fact]
        public async Task ASlowSessionWriteNeverHoldsTheMonitorPauseOrStop()
        {
            var store = new SlowStore();
            using (var h = new Harness(new MemoryStore()))
            {
                var show = FakeCatalog.Series("tt100", "Show", 1, 4);
                h.Catalog.Metas[show.Summary.Id] = show;
                h.Resolver.Streams = videoId => new[] { Harness.Torrent("A-" + videoId) };
                var session = new PlaybackSession(new PlaybackServices
                {
                    Catalog = h.Catalog, Library = h.Library, Resolver = h.Resolver, Opener = h.Opener, Player = h.Player,
                    Progress = h.Progress, Store = store, Clock = h.Clock, Subtitles = h.Subtitles,
                }, h.Options);
                h.Session = session;
                await session.InitializeAsync(None);
                var play = session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.Same(play, await Task.WhenAny(play, Task.Delay(5000)));
                SetPlaying(h, 60_000, 2_400_000);
                await h.PlayTicksAsync(3);
                var pause = session.PauseAsync(None);
                Assert.Same(pause, await Task.WhenAny(pause, Task.Delay(5000)));
                Assert.Equal(1, store.Writes);                        // one write in flight; the rest coalesce behind it
                var stop = session.StopAsync(None);                    // waits for its record to reach storage
                await ManualClock.Settle();
                Assert.False(session.GetStatus().Active);              // the gate was free: Stop ran at once
                store.Gate.SetResult(true);
                Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(5000)));
                var saved = JsonConvert.DeserializeObject<SessionRecord>(store.Inner.Values[PlaybackKeys.Session]);
                Assert.Equal("user_stop", saved.EndReason);            // the newest record won
                Assert.Equal(2, store.Writes);
            }
        }

        // ---- background and foreground ----

        [Fact]
        public async Task AReplacementThatLandsWhileTheAppIsHiddenStartsPaused()
        {
            using (var h = ShowHarness(out var show))
            {
                var stuck = Harness.Torrent("stuck");
                var fresh = Harness.Torrent("fresh");
                h.Resolver.Streams = _ => new[] { stuck, fresh };
                h.Options.Race.InitialConcurrency = 1;
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);
                h.Opener.Blocked.Add("fresh");
                h.Player.State = PlayerState.Error;
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                var tick = session.TickAsync();                         // the recovery waits for "fresh" to open
                await Wait.Until(() => h.Opener.Call("fresh") != null, "the replacement open");
                session.SetBackground(true);                            // TV Home pressed meanwhile
                h.Opener.Complete(h.Opener.Call("fresh"));
                await tick;
                Assert.EndsWith("fresh", h.Player.LastUrl);
                Assert.Equal(PlayerState.Paused, h.Player.State);       // opened, never started unseen
                var status = session.GetStatus();
                Assert.True(status.Active);
                Assert.True(status.Session.UserPaused);
                Assert.Equal("paused", status.Phase);
            }
        }

        [Fact]
        public async Task ComingBackAfterTheDecoderWasTakenReopensAtTheSavedPositionPaused()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                var url = h.Player.LastUrl;
                SetPlaying(h, 600_000, 2_400_000);
                await h.PlayTicksAsync(2);                               // saved at 610 s
                session.SetBackground(true);
                await session.PauseAsync(None);
                h.Player.Interruption = "ResourceConflict";
                h.Player.State = PlayerState.Idle;
                session.SetBackground(false);
                await session.ReconcileAfterForegroundAsync(None);
                Assert.Equal(2, h.Player.OpenCount);
                Assert.Equal(url, h.Player.LastUrl);
                Assert.Equal(610_000, h.Player.Opens.Last().StartMs);
                Assert.Equal(PlayerState.Paused, h.Player.State);
                Assert.True(session.GetStatus().Session.UserPaused);
            }
        }

        [Fact]
        public async Task APlayRequestAfterAMissedForegroundSignalStillPlays()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                session.SetBackground(true);                             // the host never reports the return
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                Assert.Equal(PlayerState.Playing, h.Player.State);
                Assert.False(session.GetStatus().Session.UserPaused);
            }
        }

        [Fact]
        public async Task ComingBackToAHealthyPlayerTouchesNothing()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 600_000, 2_400_000);
                session.SetBackground(true);
                await session.PauseAsync(None);
                session.SetBackground(false);
                await session.ReconcileAfterForegroundAsync(None);
                Assert.Equal(1, h.Player.OpenCount);
            }
        }

        // ---- I10 restore ----

        [Fact]
        public async Task UnfinishedSessionIsRestoredAtLastPositionOnRelaunch()
        {
            var store = new MemoryStore();
            MetaDetail show;
            using (var h = ShowHarness(out show))
            {
                var first = new Harness(store);
                first.Catalog.Metas["tt100"] = show;
                first.Resolver.Streams = h.Resolver.Streams;
                var session = await first.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 3, Mode = PlaybackMode.Binge }, None);
                await session.StopAfterCurrentAsync(true, None);
                SetPlaying(first, 900_000, 2_400_000);
                await first.PlayTicksAsync(2);
                // App is killed here: no finalize.
            }
            using (var relaunch = new Harness(store))
            {
                relaunch.Catalog.Metas["tt100"] = show;
                relaunch.Resolver.Streams = videoId => new[] { Harness.Torrent("A-" + videoId) };
                var session = await relaunch.StartAsync();
                Assert.NotNull(session.Restorable);
                Assert.Equal("tt100:1:3", session.Restorable.VideoId);
                var result = await session.RestoreAsync(None);
                Assert.Equal(PlayStatus.Playing, result.Status);
                Assert.Equal(910_000, relaunch.Player.Opens.Single().StartMs);
                var status = session.GetStatus().Session;
                Assert.Equal("binge", status.Mode);
                Assert.True(status.StopAfterCurrent);
                Assert.Null(session.Restorable);
            }
        }

        [Fact]
        public async Task EndedSessionIsNotRestorable()
        {
            var store = new MemoryStore();
            using (var h = ShowHarness(out var show))
            {
                var first = new Harness(store);
                first.Catalog.Metas["tt100"] = show;
                first.Resolver.Streams = h.Resolver.Streams;
                var session = await first.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.StopAsync(None);
                var record = JsonConvert.DeserializeObject<SessionRecord>(store.Values[PlaybackKeys.Session]);
                Assert.Equal("user_stop", record.EndReason);
                var relaunch = new Harness(store);
                var again = await relaunch.StartAsync();
                Assert.Null(again.Restorable);
                Assert.Equal("user_stop", again.GetStatus().LastSession.EndReason);
            }
        }

        // ---- E2 progress cadence, pause ----

        [Fact]
        public async Task ProgressIsSavedEverySixtySecondsAndPauseReports()
        {
            using (var h = ShowHarness(out var show))
            {
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                SetPlaying(h, 0, 2_400_000);
                await h.PlayTicksAsync(25);                       // 125 s of playback
                await session.PauseAsync(None);
                Assert.Equal("paused", session.GetStatus().Phase);
                await session.FlushHistoryAsync();
                var events = h.Progress.Snapshot();
                Assert.Equal(2, events.Count(e => e.StartsWith("progress")));
                Assert.Contains("paused tt100:1:1", events);
            }
        }
    }
}
