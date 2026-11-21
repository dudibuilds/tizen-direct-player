using System;
using System.Collections.Generic;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// UI feedback batch 2026-11-19 19:13: Netflix-style episode targeting, and a start card instead of a black screen.
    public class Batch191324Tests
    {
        private const string Id = "tt31510819";

        /// MobLand-shaped: two seasons of 10, the last episode of season 2 not out yet.
        private static MetaDetail MobLand(DateTimeOffset now)
        {
            var videos = new List<VideoInfo>();
            for (var s = 1; s <= 2; s++)
                for (var e = 1; e <= 10; e++)
                    videos.Add(new VideoInfo
                    {
                        Id = Id + ":" + s + ":" + e, Season = s, Episode = e, Title = "S" + s + "E" + e,
                        Released = s == 2 && e == 10 ? now.AddDays(7) : now.AddDays(-400 + s * 200 + e)
                    });
            return new MetaDetail
            {
                Summary = new MetaSummary { Id = Id, Type = MediaType.Series, Name = "MobLand", ReleaseInfo = "2025–" },
                Genres = new[] { "Crime" },
                Videos = videos
            };
        }

        private static (UiHarness H, DetailsViewModel Vm) Open(Action<UiHarness> library = null)
        {
            var h = new UiHarness();
            h.Catalog.Metas[Id] = MobLand(h.Clock.UtcNow);
            library?.Invoke(h);
            h.Shell.Start(new HomeViewModel());
            var vm = new DetailsViewModel(MediaType.Series, Id);
            h.Shell.Push(vm);
            return (h, vm);
        }

        private static void Entry(UiHarness h, string videoId, long offsetMs, long durationMs, params string[] watched)
        {
            h.Library.Entries[Id] = new LibraryEntry
            {
                Id = Id, Name = "MobLand", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = videoId, TimeOffsetMs = offsetMs, DurationMs = durationMs, LastWatched = h.Clock.UtcNow.AddDays(-30) }
            };
            foreach (var w in watched)
                h.Library.WatchedIds.Add(w);
        }

        private static string[] Watched(int season, int through) =>
            Enumerable.Range(1, through).Select(e => Id + ":" + season + ":" + e).ToArray();

        [Fact]
        public void UnstartedShowPlaysTheFirstEpisodeAndOpensSeasonOne()
        {
            var (h, vm) = Open();
            Assert.Equal("Play S1:E1", vm.Actions[0].Label);
            h.Press(RemoteKey.Down);
            Assert.Equal(1, vm.Seasons[vm.SeasonIndex]);
            Assert.Equal(Id + ":1:1", vm.FocusedEpisode.Video.Id);
        }

        [Fact]
        public void MidEpisodeResumesAndTheListOpensOnThatEpisode()
        {
            var (h, vm) = Open(x => Entry(x, Id + ":2:3", 600_000, 2_800_000, Watched(1, 10).Concat(Watched(2, 2)).ToArray()));
            Assert.StartsWith("Resume S2:E3", vm.Actions[0].Label);
            h.Press(RemoteKey.Down);                  // the list is one Down away (seasons first, then the episodes)
            if (vm.Focus == DetailsFocus.Seasons) h.Press(RemoteKey.Down);
            Assert.Equal(DetailsFocus.Episodes, vm.Focus);
            Assert.Equal(2, vm.Seasons[vm.SeasonIndex]);
            Assert.Equal(Id + ":2:3", vm.FocusedEpisode.Video.Id);
            Assert.StartsWith("Resume", vm.FocusedEpisode.Marker);
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Key == "episode:" + Id + ":2:3:facts" && n.Text.StartsWith("Resume", StringComparison.Ordinal));
        }

        [Fact]
        public void FinishedEpisodeMovesOnToTheNextOne()
        {
            var (_, vm) = Open(x => Entry(x, Id + ":1:4", 2_700_000, 2_800_000, Watched(1, 4)));
            Assert.Equal("Play next: S1:E5", vm.Actions[0].Label);
            Assert.Equal("Up next", vm.Episodes.Single(e => e.IsPrimary).Marker);
        }

        [Fact]
        public void FinishedSeasonOpensTheNextSeason()
        {
            var (h, vm) = Open(x => Entry(x, Id + ":1:10", 2_750_000, 2_800_000, Watched(1, 10)));
            Assert.StartsWith("Play next: S2:E1", vm.Actions[0].Label);
            h.Press(RemoteKey.Down);
            Assert.Equal(2, vm.Seasons[vm.SeasonIndex]);
            Assert.Equal(Id + ":2:1", vm.FocusedEpisode.Video.Id);
        }

        [Fact]
        public void ABrowsedSeasonStaysUntilPlaybackMovesOn()
        {
            var (h, vm) = Open();
            h.Press(RemoteKey.Down, RemoteKey.Right);   // seasons: pick season 2 by hand
            Assert.Equal(2, vm.Seasons[vm.SeasonIndex]);
            h.Press(RemoteKey.Up, RemoteKey.Down);      // back up and down again: the choice stays
            Assert.Equal(2, vm.Seasons[vm.SeasonIndex]);
        }

        // ---- start card ----

        [Theory]
        [InlineData(1, 7, 41, "Trying the best of 41 sources")]
        [InlineData(1, 3, 41, "Trying the best of 41 sources")]     // the number racing changes; the line does not
        [InlineData(1, 1, 1, "Connecting to the source")]
        [InlineData(3, 4, 41, "Trying other sources · 2 tried of 41")]
        [InlineData(5, 1, 41, "Trying other sources · 4 tried of 41")]
        public void TheStartStatusIsStableWhileSourcesRace(int attempt, int racing, int found, string text)
        {
            var status = new Ru7100.Core.Playback.PlaybackStatus { Phase = "starting", Step = "connecting", StepSources = found, StepTry = attempt, StepRacing = racing };
            Assert.Equal(text, Ru7100.Core.App.SessionPlayerControls.StatusText(status, sessionActive: true));
        }

        [Fact]
        public void StartingPlaybackShowsATitleCardWithStages()
        {
            var s = new PlayerSnapshot
            {
                SessionActive = true, State = PlayerState.Preparing, StartStep = 2, StatusText = "Checking 7 sources…",
                Meta = MobLand(DateTimeOffset.UtcNow), Title = "MobLand", EpisodeLabel = "S2E1 · I Wanna Be Your Dog"
            };
            Assert.True(UiLayout.LoadingScreen(s));
            var (h, _) = Open();
            h.Player.Snapshot = s;
            h.Shell.Push(new PlayerViewModel(new PlayRequest { Meta = s.Meta, Video = s.Meta.Videos[10] }));
            h.Player.Snapshot = s;
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Key == "player:loading:art");
            Assert.Contains(frame.Nodes, n => n.Key == "player:loading:step:1" && n.Bold);   // current stage
            Assert.Contains(frame.Nodes, n => n.Key == "player:status" && n.Text == "Checking 7 sources…");
            // A failed start says so, with a way out.
            s.Error = "No viable streams found for MobLand";
            s.SessionActive = false;
            h.Shell.Tick();
            frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Key == "player:loading:error" && n.Text == "No playable source was found for this title.");
            Assert.Contains(frame.Nodes, n => n.Key == "player:card:Retry:bg");
            // Mid-playback buffering keeps the video visible (no card).
            Assert.False(UiLayout.LoadingScreen(new PlayerSnapshot { SessionActive = true, State = PlayerState.Buffering, PositionMs = 60_000 }));
        }
    }
}
