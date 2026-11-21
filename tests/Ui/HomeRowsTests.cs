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
    public class HomeRowsTests
    {
        private sealed class Feed : IRecommendationsFeed
        {
            public IReadOnlyList<RecommendationItem> Items = Array.Empty<RecommendationItem>();
            public Task<IReadOnlyList<RecommendationItem>> GetAsync(CancellationToken token) => Task.FromResult(Items);
        }

        internal static UiHarness Harness()
        {
            var h = new UiHarness();
            h.Shell.Services.Recommendations = new Feed
            {
                Items = new[]
                {
                    new RecommendationItem { Meta = new MetaSummary { Id = "tt5753856", Type = MediaType.Series, Name = "Dark" }, Reason = "Because you watched Breaking Bad" },
                    new RecommendationItem { Meta = UiHarness.Movie.Summary, Reason = "Short and animated" }
                }
            };
            var extras = new FakeExtras();
            extras.Items["tt5753856"] = new MetaExtras { Runtime = "25 min" };
            h.Shell.Services.Extras = extras;
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000, LastWatched = h.Clock.UtcNow }
            };
            return h;
        }

        [Fact]
        public void RowsFollowTheResearchOrderWithLabelsAndStyles()
        {
            var h = Harness();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            var keys = home.Rows.Where(r => !r.IsMenu).Select(r => r.Key).ToList();
            Assert.Equal(new[] { "continue", "foryou", "firsteps", "top:movie", "top:series", "short", "library" }, keys.Take(7).ToArray());
            var cont = home.Rows.Single(r => r.Key == "continue");
            Assert.Equal(TileStyle.Landscape, cont.Style);
            Assert.Equal("S1:E2  ·  37m left", cont.Items[0].Subtitle);
            Assert.Equal("https://episodes.metahub.space/tt0903747/1/2/w780.jpg", cont.Items[0].Image);
            Assert.Equal(TileStyle.Ranked, home.Rows.Single(r => r.Key == "top:movie").Style);
            Assert.Equal(1, home.Rows.Single(r => r.Key == "top:movie").Items[0].Rank);
            Assert.Equal("Dark", home.Rows.Single(r => r.Key == "firsteps").Items.Single().Title);
            Assert.Equal("Dark", home.Rows.Single(r => r.Key == "short").Items.Single().Title);
        }

        [Fact]
        public void StartSomethingNewOpensTheTitlePageAndThePlayKeyPlaysThePilot()
        {
            var h = Harness();
            var home = new HomeViewModel();
            h.Shell.Start(home);
            h.Press(RemoteKey.Down, RemoteKey.Down);
            Assert.Equal("firsteps", home.FocusedRow.Key);
            h.Press(RemoteKey.Select);                       // OK never starts an unknown series by accident
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Back, RemoteKey.PlayPause);    // the direct play key
            Assert.IsType<PlayerViewModel>(h.Shell.Current);
            Assert.Equal("tt5753856:1:1", h.Player.LastRequest.Video.Id);
            Assert.Equal(0, h.Player.LastRequest.ResumeMs);
        }

        [Fact]
        public void NewSeasonAndUpcomingEpisodeLabels()
        {
            var h = new UiHarness();
            var now = h.Clock.UtcNow;
            var meta = new MetaDetail
            {
                Summary = new MetaSummary { Id = "tt1", Type = MediaType.Series, Name = "Show" },
                Videos = new[]
                {
                    new VideoInfo { Id = "tt1:1:1", Season = 1, Episode = 1, Released = now.AddDays(-400) },
                    new VideoInfo { Id = "tt1:2:1", Season = 2, Episode = 1, Released = now.AddDays(-2) }
                }
            };
            var upcoming = new MetaDetail
            {
                Summary = new MetaSummary { Id = "tt2", Type = MediaType.Series, Name = "Weekly" },
                Videos = new[] { new VideoInfo { Id = "tt2:1:5", Season = 1, Episode = 5, Released = now.AddDays(3) } }
            };
            h.Catalog.Metas["tt1"] = meta;
            h.Catalog.Metas["tt2"] = upcoming;
            h.Library.WatchedIds.Add("tt1:1:1");
            var entries = new[]
            {
                new LibraryEntry { Id = "tt1", Name = "Show", Type = MediaType.Series, State = new LibraryState { LastWatched = now.AddDays(-300) } },
                new LibraryEntry { Id = "tt2", Name = "Weekly", Type = MediaType.Series, State = new LibraryState { LastWatched = now.AddDays(-1) } }
            };
            var tiles = HomeViewModel.FindNewEpisodesAsync(h.Catalog, h.Library, entries, now, CancellationToken.None).Result;
            Assert.Equal("New Season", tiles[0].Badge);
            Assert.StartsWith("Next episode · ", tiles[1].Badge);
        }

        private static MetaDetail Show(string id, DateTimeOffset now, int newestDaysAgo) => new MetaDetail
        {
            Summary = new MetaSummary { Id = id, Type = MediaType.Series, Name = "Show" },
            Videos = Enumerable.Range(1, 4).Select(n => new VideoInfo
            {
                Id = id + ":1:" + n, Season = 1, Episode = n, Released = n < 4 ? now.AddDays(-400 + n) : now.AddDays(-newestDaysAgo)
            }).ToArray()
        };

        [Fact]
        public void AFinishedEpisodeLeavesItsNextEpisodeInContinueWatching()
        {
            var h = Harness();
            var now = h.Clock.UtcNow;
            h.Catalog.Metas["tt1"] = Show("tt1", now, 300);
            foreach (var n in new[] { 1, 2, 3 }) h.Library.WatchedIds.Add("tt1:1:" + n);
            // After E3 passed 90% the library points at E4 with Stremio's 1 ms "up next" offset.
            h.Library.Entries["tt1"] = new LibraryEntry
            {
                Id = "tt1", Name = "Show", Type = MediaType.Series, Modified = now,
                State = new LibraryState { VideoId = "tt1:1:4", TimeOffsetMs = 1, LastWatched = now.AddMinutes(1) }
            };
            var home = new HomeViewModel();
            h.Shell.Start(home);
            var card = home.Rows.Single(r => r.Key == "continue").Items[0];
            Assert.Equal("tt1", card.Id);
            Assert.Equal("S1:E4  ·  Up next", card.Subtitle);
            Assert.Equal(0, card.Progress);
            Assert.Null(card.Badge);                       // E4 is old news: no "new" label
            h.Press(RemoteKey.PlayPause);
            Assert.Equal("tt1:1:4", h.Player.LastRequest.Video.Id);
            Assert.Equal(0, h.Player.LastRequest.ResumeMs);  // from the start, not the end of E3
        }

        [Fact]
        public void AShowWithANewEpisodeIsLabelledAndMovesToTheFrontOfContinueWatching()
        {
            var h = Harness();                             // Breaking Bad, stopped part-way
            var now = h.Clock.UtcNow;
            h.Library.Entries["tt0903747"].State.LastWatched = now.AddDays(-3);
            h.Catalog.Metas["tt1"] = Show("tt1", now, 1);  // E4 came out yesterday
            foreach (var n in new[] { 1, 2, 3 }) h.Library.WatchedIds.Add("tt1:1:" + n);
            h.Library.Entries["tt1"] = new LibraryEntry
            {
                Id = "tt1", Name = "Show", Type = MediaType.Series, Modified = now.AddDays(-10),
                State = new LibraryState { VideoId = "tt1:1:4", TimeOffsetMs = 1, LastWatched = now.AddDays(-10) }
            };
            var home = new HomeViewModel();
            h.Shell.Start(home);
            var row = home.Rows.Single(r => r.Key == "continue").Items;
            Assert.Equal(new[] { "tt1", "tt0903747" }, row.Select(t => t.Id).ToArray());
            Assert.Equal("New Episode", row[0].Badge);
            Assert.Null(row[1].Badge);
            // Shown once: the label sits on the Continue watching card, not again in New episodes.
            Assert.DoesNotContain(home.Rows, r => r.Key == "new" && r.Items.Any(t => t.Id == "tt1"));
        }
    }
}
