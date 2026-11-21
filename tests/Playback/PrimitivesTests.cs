using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Playback;
using Xunit;

namespace Ru7100.Tests.Playback
{
    public class PrimitivesTests
    {
        [Fact]
        public async Task RaceReturnsFailuresWhenEveryCandidateFails()
        {
            var clock = new ManualClock();
            var result = await CandidateRace.RunAsync<int, int>(new[] { 1, 2, 3 },
                (c, t) => Task.FromException<int>(new InvalidOperationException("bad " + c)),
                clock, new RaceSettings(), Array.Empty<int>(), null, CancellationToken.None);
            Assert.False(result.HasWinner);
            Assert.Equal(new[] { 1, 2, 3 }, result.Failures.Select(f => f.Candidate).OrderBy(x => x));
        }

        [Fact]
        public async Task RaceDiscardsLateWinner()
        {
            var clock = new ManualClock();
            var slow = new TaskCompletionSource<int>();
            var discarded = new List<int>();
            var result = await CandidateRace.RunAsync<int, int>(new[] { 1, 2 },
                (c, t) => c == 1 ? slow.Task : Task.FromResult(20), clock, new RaceSettings(), Array.Empty<int>(),
                (c, value) => { lock (discarded) discarded.Add(value); }, CancellationToken.None);
            Assert.Equal(2, result.WinnerCandidate);
            slow.SetResult(10);
            await Wait.Until(() => { lock (discarded) return discarded.Count == 1; }, "discard");
            Assert.Equal(10, discarded[0]);
        }

        /// Test harness for race-cut cases: each launch gets its own completion and records its token and lead.
        private sealed class CutRace
        {
            public readonly List<(int Candidate, CancellationToken Token, RaceLead Lead, TaskCompletionSource<int> Done)> Calls =
                new List<(int, CancellationToken, RaceLead, TaskCompletionSource<int>)>();

            public Task<int> Prepare(int candidate, RaceLead lead, CancellationToken token)
            {
                var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => done.TrySetCanceled(token));
                lock (Calls)
                    Calls.Add((candidate, token, lead, done));
                return done.Task;
            }

            public (int Candidate, CancellationToken Token, RaceLead Lead, TaskCompletionSource<int> Done) Last(int candidate)
            {
                lock (Calls)
                    return Calls.Last(c => c.Candidate == candidate);
            }

            public int Launches(int candidate) { lock (Calls) return Calls.Count(c => c.Candidate == candidate); }
        }

        private static RaceSettings CutSettings() => new RaceSettings
        {
            InitialConcurrency = 3, MaxConcurrent = 3, HedgeDelay = TimeSpan.FromMinutes(10), LeadStallAfter = TimeSpan.FromSeconds(12),
        };

        [Fact]
        public async Task RaceCutStopsOthersOnceALeaderClaimsWithoutCountingThemAsFailures()
        {
            var clock = new ManualClock();
            var race = new CutRace();
            var task = CandidateRace.RunAsync<int, int>(new[] { 1, 2, 3 }, race.Prepare, clock, CutSettings(), Array.Empty<int>(), null, CancellationToken.None);
            await Wait.Until(() => race.Launches(3) == 1, "three launched");
            race.Last(1).Lead.Claim();
            Assert.True(race.Last(2).Token.IsCancellationRequested);
            Assert.True(race.Last(3).Token.IsCancellationRequested);
            Assert.False(race.Last(1).Token.IsCancellationRequested);
            race.Last(1).Done.SetResult(10);
            var result = await task;
            Assert.Equal(1, result.WinnerCandidate);
            Assert.Empty(result.Failures);
            Assert.Equal(new[] { 2, 3 }, result.Cut.OrderBy(x => x));
            Assert.Equal(1, result.Leader);
        }

        [Fact]
        public async Task RaceCutRelaunchesTheCutCandidatesWhenTheLeaderFails()
        {
            var clock = new ManualClock();
            var race = new CutRace();
            var task = CandidateRace.RunAsync<int, int>(new[] { 1, 2, 3 }, race.Prepare, clock, CutSettings(), Array.Empty<int>(), null, CancellationToken.None);
            await Wait.Until(() => race.Launches(3) == 1, "three launched");
            race.Last(1).Lead.Claim();
            race.Last(1).Done.SetException(new InvalidOperationException("stalled"));
            await Wait.Until(() => race.Launches(2) == 2 && race.Launches(3) == 2, "cut candidates relaunched");
            race.Last(3).Done.SetResult(30);
            var result = await task;
            Assert.Equal(3, result.WinnerCandidate);
            Assert.Equal(new[] { 1 }, result.Failures.Select(f => f.Candidate));
        }

        [Fact]
        public async Task RaceCutLetsTheOthersRaceAgainBesideALeaderThatStalls()
        {
            var clock = new ManualClock();
            var race = new CutRace();
            var task = CandidateRace.RunAsync<int, int>(new[] { 1, 2, 3 }, race.Prepare, clock, CutSettings(), Array.Empty<int>(), null, CancellationToken.None);
            await Wait.Until(() => race.Launches(3) == 1, "three launched");
            race.Last(1).Lead.Claim();
            await clock.AdvanceAsync(TimeSpan.FromSeconds(11));
            Assert.Equal(1, race.Launches(2));
            await clock.AdvanceAsync(TimeSpan.FromSeconds(2));
            await Wait.Until(() => race.Launches(2) == 2 && race.Launches(3) == 2, "cut candidates relaunched after the stall budget");
            Assert.False(race.Last(1).Token.IsCancellationRequested);
            race.Last(1).Done.SetResult(10);
            var result = await task;
            Assert.Equal(1, result.WinnerCandidate);
            Assert.Empty(result.Failures);
        }

        [Fact]
        public async Task RaceCutIsOffWhenDisabled()
        {
            var clock = new ManualClock();
            var race = new CutRace();
            var settings = CutSettings();
            settings.CutOnLead = false;
            var task = CandidateRace.RunAsync<int, int>(new[] { 1, 2 }, race.Prepare, clock, settings, Array.Empty<int>(), null, CancellationToken.None);
            await Wait.Until(() => race.Launches(2) == 1, "two launched");
            race.Last(1).Lead.Claim();
            Assert.False(race.Last(2).Token.IsCancellationRequested);
            race.Last(2).Done.SetResult(20);
            Assert.Equal(2, (await task).WinnerCandidate);
        }

        [Fact]
        public async Task ProgressDeadlineExtendsOnProgressUpToMaximum()
        {
            var clock = new ManualClock();
            using (var deadline = new ProgressDeadline(clock, CancellationToken.None, TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300)))
            {
                await clock.AdvanceAsync(TimeSpan.FromSeconds(100));
                deadline.Progress();                       // now 160 s
                await clock.AdvanceAsync(TimeSpan.FromSeconds(50));
                Assert.False(deadline.Token.IsCancellationRequested);
                for (var i = 0; i < 5; i++)
                {
                    deadline.Progress();
                    await clock.AdvanceAsync(TimeSpan.FromSeconds(40));
                }
                // 350 s elapsed: past the 300 s hard maximum.
                await Wait.Until(() => deadline.Token.IsCancellationRequested, "deadline");
                Assert.True(deadline.Expired);
            }
        }

        [Fact]
        public void HealthWaitsThroughGraceThenReplacesPersistentStall()
        {
            var t0 = DateTimeOffset.UtcNow;
            StreamHealth health = null;
            long position = 0;
            for (var s = 0; s <= 20; s += 5)
            {
                health = StreamHealth.Observe(health, t0.AddSeconds(s), position, PlayerState.Playing, false);
                position += 5000;
            }
            Assert.Equal("playing", health.Reason);
            var frozenAt = 20;
            string action = null;
            var second = frozenAt;
            while (action != "replace" && second < 600)
            {
                second += 5;
                health = StreamHealth.Observe(health, t0.AddSeconds(second), position, PlayerState.Buffering, false);
                action = health.Action;
            }
            Assert.Equal("persistent_stall", health.Reason);
            // Stalled 20 s after the last progress (40 s), then 20 s of recovery grace: replaced about 40 s into the freeze.
            Assert.InRange(second, 55, 65);
            Assert.True(StreamHealth.IsStarvedStop(health, t0.AddSeconds(second), position, 2_400_000));
            Assert.False(StreamHealth.IsStarvedStop(health, t0.AddSeconds(second), 2_390_000, 2_400_000));
        }

        [Fact]
        public void HealthResetsOnSeekAndPause()
        {
            var t0 = DateTimeOffset.UtcNow;
            var health = StreamHealth.Observe(null, t0, 10_000, PlayerState.Playing, false);
            health = StreamHealth.Observe(health, t0.AddSeconds(5), 600_000, PlayerState.Playing, false);
            Assert.Equal("inactive_or_seek", health.Reason);
            health = StreamHealth.Observe(health, t0.AddSeconds(10), 605_000, PlayerState.Paused, false);
            Assert.Empty(health.Samples);
        }

        [Fact]
        public void NextReleasedVideoSkipsSpecialsAndUnreleased()
        {
            var meta = FakeCatalog.Series("tt1", "S", 2, 2);
            var videos = meta.Videos.ToList();
            videos.Insert(0, new VideoInfo { Id = "tt1:0:1", Season = 0, Episode = 1 });
            videos[videos.Count - 1].Released = DateTimeOffset.UtcNow.AddDays(30);
            meta.Videos = videos;
            var now = DateTimeOffset.UtcNow;
            Assert.Equal("tt1:2:1", PlayResolver.NextReleasedVideoAfter(meta, meta.Videos.First(v => v.Id == "tt1:1:2"), now).Id);
            Assert.Null(PlayResolver.NextReleasedVideoAfter(meta, meta.Videos.First(v => v.Id == "tt1:2:1"), now));
            Assert.Equal("tt1:1:1", PlayResolver.NextReleasedVideoAfter(meta, new VideoInfo { Id = "x", Season = 0, Episode = 9 }, now).Id);
        }

        [Fact]
        public void CaughtUpReportsNextScheduledEpisode()
        {
            var meta = FakeCatalog.Series("tt1", "S", 1, 3);
            var future = meta.Videos.Last();
            future.Released = DateTimeOffset.UtcNow.AddDays(3);
            var library = new FakeLibrary();
            foreach (var v in meta.Videos)
                library.Watched.Add(v.Id);
            var entry = new LibraryEntry { Id = "tt1", State = new LibraryState { VideoId = "tt1:1:2" } };
            var result = PlayResolver.Resolve(meta, entry, library, null, null, false, DateTimeOffset.UtcNow);
            Assert.Equal(PlayStatus.CaughtUp, result.Status);
            Assert.Equal(future.Id, result.NextScheduled.Id);
        }

        [Fact]
        public void TitleSimilarityMatchesParityRules()
        {
            Assert.Equal(1, PlayResolver.TitleSimilarity("Breaking Bad", "breaking-bad"));
            Assert.Equal(0.9, PlayResolver.TitleSimilarity("The Office", "The Office (US)"));
            Assert.Equal(0.5, PlayResolver.TitleSimilarity("dark matter", "dark knight"), 2);
        }

        [Fact]
        public void AudioRulesAcceptUnknownAndRejectOnlyBrokenPromises()
        {
            var promise = new LanguageHints { EnglishAudio = true };
            var rus = new[] { new TrackInfo { Index = 0, Language = "rus" } };
            Assert.False(TrackRules.DecideAudio(rus, false, promise, null).Accept);
            Assert.True(TrackRules.DecideAudio(rus, false, new LanguageHints(), null).Accept);
            Assert.True(TrackRules.DecideAudio(new[] { new TrackInfo { Index = 0, Language = "und" } }, false, promise, null).Accept);
            var anime = TrackRules.DecideAudio(new[]
            {
                new TrackInfo { Index = 0, Language = "eng" }, new TrackInfo { Index = 1, Language = "ja" },
            }, true, new LanguageHints { JapaneseAudio = true }, null);
            Assert.Equal(1, anime.SelectIndex);
            Assert.Equal("jpn", anime.Language);
            Assert.Equal("spa", TrackRules.Normalize("", "Spanish"));
            Assert.Null(TrackRules.Normalize("und"));
        }

        [Fact]
        public void SubtitleRulesPreferEmbeddedPerLanguageAndAnimeEnglishOnly()
        {
            var embeddedEnglish = new[] { new TrackInfo { Index = 2, Language = "en" } };
            var external = new[]
            {
                new SubtitleCandidate { Url = "u-es", Language = "spa" }, new SubtitleCandidate { Url = "u-en", Language = "eng" },
            };
            var general = TrackRules.DecideSubtitle(embeddedEnglish, external, false, null);
            Assert.Equal(SubtitleKind.External, general.Kind);
            Assert.Equal("u-es", general.External.Url);
            var anime = TrackRules.DecideSubtitle(embeddedEnglish, external, true, null);
            Assert.Equal(SubtitleKind.Embedded, anime.Kind);
            Assert.Equal(2, anime.EmbeddedIndex);
            var sdh = TrackRules.DecideSubtitle(new[]
            {
                new TrackInfo { Index = 0, Language = "eng", Label = "English SDH" }, new TrackInfo { Index = 1, Language = "eng", Label = "English" },
            }, null, false, null);
            Assert.Equal(1, sdh.EmbeddedIndex);
            Assert.Equal(-30000, TrackRules.ClampSubtitleOffset(-99999));
        }

        [Fact]
        public void SubtitleTextParsesSrtAndVttWithOffsetPump()
        {
            var vtt = new SubtitleText("WEBVTT\n\n00:01.000 --> 00:02.500\n<i>Hello</i>\n\n1\n00:00:03,000 --> 00:00:04,000\nWorld\n");
            Assert.Equal(2, vtt.Count);
            Assert.Equal("Hello", vtt.At(1500).Text);
            Assert.Null(vtt.At(2600));
            Assert.Equal("World", vtt.At(3500).Text);

            var pump = new SubtitleCuePump();
            var seen = new List<string>();
            pump.CueChanged += (_, cue) => seen.Add(cue.Text);
            pump.SetSource(vtt);
            pump.OffsetMs = 2000;          // subtitles appear 2 s later
            pump.Update(3200);             // media 1200 -> Hello
            pump.Update(3300);             // unchanged, no event
            pump.Update(4700);             // media 2700 -> clear
            Assert.Equal(new[] { "Hello", "" }, seen);
        }
    }
}
