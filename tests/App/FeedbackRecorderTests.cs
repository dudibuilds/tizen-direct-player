using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Api;
using Ru7100.Core.App;
using Ru7100.Core.Playback;
using Ru7100.Tests.Playback;
using Xunit;

namespace Ru7100.Tests.App
{
    public class FeedbackRecorderTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);
        private const string Show = "tt0903747";
        private const long Duration = 2_700_000;

        private sealed class Rig
        {
            public readonly MemoryStore Kv = new MemoryStore();
            public readonly KeyValueRecommendationsStore Store;
            public readonly FeedbackRecorder Recorder;

            public Rig()
            {
                Store = new KeyValueRecommendationsStore(Kv, () => T0);
                Recorder = new FeedbackRecorder(Store, Kv, () => T0);
            }

            public async Task<string[]> EventsAsync()
            {
                await Recorder.FlushAsync();
                var page = await Store.ListFeedbackAsync(0, 1000, CancellationToken.None);
                return page.Events.Select(e => e.Kind + " " + (e.VideoId ?? e.ImdbId) + (e.PositionMs.HasValue ? " " + e.PositionMs / 1000 : "")).ToArray();
            }
        }

        private static SessionStatus Session(string videoId, long position, bool watched = false, string id = "s1", string metaId = Show) => new SessionStatus
        {
            Id = id, MetaId = metaId, VideoId = videoId, PositionMs = position, DurationMs = Duration, MarkedWatched = watched, Mode = "single",
        };

        private static PlaybackStatus Active(SessionStatus session, SessionStatus last = null) =>
            new PlaybackStatus { Active = true, Phase = "playing", Session = session, LastSession = last };

        private static PlaybackStatus Ended(SessionStatus last, string reason)
        {
            last.EndedAt = T0;
            last.EndReason = reason;
            return new PlaybackStatus { Active = false, Phase = "ended", LastSession = last };
        }

        [Fact]
        public async Task PlayThroughRecordsStartMilestonesAndCompletionOnce()
        {
            var rig = new Rig();
            var ep = Show + ":1:1";
            rig.Recorder.Observe(Active(Session(ep, 0)));
            rig.Recorder.Observe(Active(Session(ep, 700_000)));
            rig.Recorder.Observe(Active(Session(ep, 1_400_000)));
            rig.Recorder.Observe(Active(Session(ep, 2_100_000)));
            rig.Recorder.Observe(Active(Session(ep, 2_450_000, watched: true)));
            rig.Recorder.Observe(Active(Session(ep, 2_500_000, watched: true)));
            rig.Recorder.Observe(Ended(Session(ep, 2_690_000, watched: true), "ended"));
            Assert.Equal(new[]
            {
                "play_started " + ep + " 0", "progress " + ep + " 700", "progress " + ep + " 1400", "progress " + ep + " 2100", "completed " + ep + " 2450",
            }, await rig.EventsAsync());
        }

        [Fact]
        public async Task StoppingAfterTwoMinutesBeforeNinetyPercentIsAbandoned()
        {
            var rig = new Rig();
            var ep = Show + ":1:2";
            rig.Recorder.Observe(Active(Session(ep, 700_000)));
            rig.Recorder.Observe(Active(Session(ep, 860_000)));
            rig.Recorder.Observe(Ended(Session(ep, 860_000), "user_stop"));
            var events = await rig.EventsAsync();
            // Resumed past 25 %, so that milestone is not reported again.
            Assert.Equal(new[] { "play_started " + ep + " 700", "abandoned " + ep + " 860" }, events);
        }

        [Fact]
        public async Task ShortTriesAndFailuresAreNotAbandons()
        {
            var rig = new Rig();
            rig.Recorder.Observe(Active(Session(Show + ":1:1", 0)));
            rig.Recorder.Observe(Active(Session(Show + ":1:1", 90_000)));
            rig.Recorder.Observe(Ended(Session(Show + ":1:1", 90_000), "user_stop"));
            rig.Recorder.Observe(Active(Session(Show + ":1:2", 0, id: "s2")));
            rig.Recorder.Observe(Active(Session(Show + ":1:2", 900_000, id: "s2")));
            rig.Recorder.Observe(Ended(Session(Show + ":1:2", 900_000, id: "s2"), "stream_failed"));
            var events = await rig.EventsAsync();
            Assert.DoesNotContain(events, e => e.StartsWith("abandoned", StringComparison.Ordinal));
            Assert.Equal(2, events.Count(e => e.StartsWith("play_started", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task SourceRecoveryContinuesTheSameItem()
        {
            var rig = new Rig();
            var ep = Show + ":1:3";
            rig.Recorder.Observe(Active(Session(ep, 0)));
            rig.Recorder.Observe(Active(Session(ep, 400_000)));
            rig.Recorder.Observe(Ended(Session(ep, 400_000), "stream_recovery"));
            rig.Recorder.Observe(Active(Session(ep, 400_000, id: "s2")));
            Assert.Equal(new[] { "play_started " + ep + " 0" }, await rig.EventsAsync());
        }

        [Fact]
        public async Task SwitchingToAnotherTitleAbandonsTheFirstAfterTwoMinutes()
        {
            var rig = new Rig();
            var ep = Show + ":1:1";
            rig.Recorder.Observe(Active(Session(ep, 0)));
            rig.Recorder.Observe(Active(Session(ep, 300_000)));
            var previous = Session(ep, 300_000);
            previous.EndedAt = T0;
            previous.EndReason = "user_play";
            rig.Recorder.Observe(Active(Session("tt1375666", 0, id: "s2", metaId: "tt1375666"), previous));
            Assert.Equal(new[] { "play_started " + ep + " 0", "abandoned " + ep + " 300", "play_started tt1375666 0" },
                await rig.EventsAsync());
        }

        [Fact]
        public async Task NonImdbIdsAreSkippedSilently()
        {
            var rig = new Rig();
            rig.Recorder.Observe(Active(Session("kitsu:1:1", 0, metaId: "kitsu:1")));
            Assert.Empty(await rig.EventsAsync());
        }

        [Fact]
        public async Task ThumbsAndNotInterestedAreRecordedAndHideTitles()
        {
            var rig = new Rig();
            await rig.Recorder.ThumbsUpAsync("tt0903747", CancellationToken.None);
            await rig.Recorder.NotInterestedAsync("tt1375666", CancellationToken.None);
            await rig.Recorder.ThumbsDownAsync("tt2560140", CancellationToken.None);
            Assert.Equal(new[] { "thumbs_up tt0903747", "not_interested tt1375666", "thumbs_down tt2560140" }, await rig.EventsAsync());
            Assert.Equal(new[] { "tt1375666", "tt2560140" }, (await rig.Recorder.GetHiddenAsync(CancellationToken.None)).OrderBy(x => x));
            var reopened = new FeedbackRecorder(rig.Store, rig.Kv);
            Assert.Equal(2, (await reopened.GetHiddenAsync(CancellationToken.None)).Count);

            await rig.Store.ReplaceAsync(new RecommendationSnapshot
            {
                Rows = new[]
                {
                    new RecommendationRow { Id = "for-you", Title = "For You", Items = new[]
                    {
                        new RecommendationItem { Id = "tt1375666", Type = "movie", Name = "Inception" },
                        new RecommendationItem { Id = "tt0944947", Type = "series", Name = "Game of Thrones", Reason = "Drama you finish" },
                    } },
                    new RecommendationRow { Id = "because-dark", Title = "Because you watched Dark", Items = new[]
                    {
                        new RecommendationItem { Id = "tt0944947", Type = "series", Name = "Game of Thrones" },
                        new RecommendationItem { Id = "tt5753856", Type = "series", Name = "Dark" },
                    } },
                },
            }, null, CancellationToken.None);
            var feed = await new RecommendationsFeed(rig.Store, rig.Recorder).GetAsync(CancellationToken.None);
            Assert.Equal(new[] { "tt0944947", "tt5753856" }, feed.Select(i => i.Meta.Id));
            Assert.Equal(MediaType.Series, feed[0].Meta.Type);
            Assert.Equal("Drama you finish", feed[0].Reason);
            Assert.Equal("Because you watched Dark", feed[1].Reason);
        }

        [Fact]
        public async Task LibraryEditsThroughTheDecoratorAreRecorded()
        {
            var rig = new Rig();
            var library = new FeedbackLibrary(new FakeLibrary(), rig.Recorder);
            await library.AddAsync(FakeCatalog.Series(Show, "Breaking Bad", 1, 2), CancellationToken.None);
            await library.RemoveAsync(Show, CancellationToken.None);
            Assert.Equal(new[] { "added_to_library " + Show, "removed " + Show }, await rig.EventsAsync());
        }
    }
}
