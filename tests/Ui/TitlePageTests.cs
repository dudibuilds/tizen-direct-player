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
    public sealed class RecordingFeedback : IFeedbackSink
    {
        public readonly List<string> Calls = new List<string>();
        public Task ThumbsUpAsync(string metaId, CancellationToken token) { Calls.Add("up " + metaId); return Task.CompletedTask; }
        public Task ThumbsDownAsync(string metaId, CancellationToken token) { Calls.Add("down " + metaId); return Task.CompletedTask; }
        public Task NotInterestedAsync(string metaId, CancellationToken token) { Calls.Add("hide " + metaId); return Task.CompletedTask; }
        public Task<IReadOnlyCollection<string>> GetHiddenAsync(CancellationToken token) => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());
    }

    public class TitlePageTests
    {
        private static UiHarness Harness(LibraryState state = null)
        {
            var h = new UiHarness();
            if (state != null)
                h.Library.Entries["tt0903747"] = new LibraryEntry { Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, State = state };
            return h;
        }

        private static DetailsViewModel Open(UiHarness h)
        {
            h.Shell.Start(new HomeViewModel());
            var details = new DetailsViewModel(MediaType.Series, "tt0903747");
            h.Shell.Push(details);
            return details;
        }

        [Fact]
        public void PrimaryActionResumesWithTimeLeft()
        {
            var h = Harness(new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_820_000 });
            var details = Open(h);
            Assert.Equal("Resume S1:E2 · 37m left", details.Actions[0].Label);
            Assert.DoesNotContain(details.Actions, a => a.Action == DetailsAction.Episodes);   // Down reaches the list
            Assert.InRange(details.PrimaryProgress, 0.2, 0.22);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "action:Primary:progress:fill");
            Assert.Contains(details.MoreActions, a => a.Label == "Play next: S1:E3");
        }

        [Fact]
        public void EpisodeRowsShareTheFocusRingThumbnailsAndInRowActions()
        {
            var h = Harness(new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_820_000 });
            h.Library.WatchedIds.Add("tt0903747:1:1");
            var details = Open(h);
            h.Press(RemoteKey.Down);
            if (details.Focus == DetailsFocus.Seasons) h.Press(RemoteKey.Down);
            Assert.Equal(DetailsFocus.Episodes, details.Focus);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            var focused = details.FocusedEpisode.Video.Id;
            var row = nodes.Single(n => n.Key == "episode:" + focused + ":bg");
            var ring = nodes.Single(n => n.Key == "episode:" + focused + ":ring");
            Assert.Equal(UiTheme.Focus, ring.Fill);                       // the white ring, not a thin red edge
            Assert.DoesNotContain(nodes, n => n.Key.EndsWith(":edge", StringComparison.Ordinal));
            // Every row has a thumbnail, and the watched tick sits beside its own title, not across the screen.
            foreach (var e in details.Episodes.Take(3))
                Assert.Contains(nodes, n => n.Key == "episode:" + e.Video.Id + ":still");
            var tick = nodes.First(n => n.Key.StartsWith("episode:tt0903747:1:1:watched-icon", StringComparison.Ordinal));
            var title = nodes.Single(n => n.Key == "episode:tt0903747:1:1:title");
            Assert.InRange(title.X - tick.X, 0, 80);
            // Mark watched and + Queue are inside the focused row's right edge.
            var watched = nodes.Single(n => n.Key == "episode:" + focused + ":watched:bg");
            Assert.True(watched.X >= row.X && watched.X + watched.W <= row.X + row.W && watched.Y >= row.Y && watched.Y + watched.H <= row.Y + row.H);
            h.Press(RemoteKey.Right);
            Assert.Equal(1, details.EpisodeColumn);
            nodes = UiLayout.Build(h.Shell).Nodes;
            Assert.Equal(UiTheme.FocusFill, nodes.Single(n => n.Key == "episode:" + focused + ":watched:bg").Fill);
            Assert.DoesNotContain(nodes, n => n.Key == "episode:" + focused + ":ring");
        }

        [Fact]
        public void PrimaryActionPlaysNextAndMarksNewEpisodes()
        {
            var h = Harness(new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 2_700_000, DurationMs = 2_820_000, LastWatched = new DateTimeOffset(2008, 2, 1, 0, 0, 0, TimeSpan.Zero) });
            var details = Open(h);
            Assert.Equal("Play next: S1:E3 (new)", details.Actions[0].Label);
        }

        [Fact]
        public void SignatureLineShowsBestPlayableSource()
        {
            var h = Harness();
            var details = Open(h);
            Assert.Equal(SignatureState.Found, details.Signature);
            var line = SourceSignatures.Line(details.SignatureEntry);
            Assert.StartsWith("1080p · 2.1 GB", line);
            // The title page says it in plain words; size, channels and health stay in the source picker.
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Key == "details:signature" && n.Text == "HD · Spanish subs · Ready to play" && !n.Bold);
            Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("details:signature:health", StringComparison.Ordinal));
        }

        [Fact]
        public void ActionRowKeepsFourEverydayActionsAndMoreHoldsTheRest()
        {
            var harness = Harness();
            harness.Shell.Services.Feedback = new RecordingFeedback();
            var details = Open(harness);
            var h = details.Shell;
            Assert.Equal(new[] { DetailsAction.Primary, DetailsAction.Library, DetailsAction.Rate, DetailsAction.More }, details.Actions.Select(a => a.Action).ToArray());
            Assert.Contains(details.MoreActions, a => a.Action == DetailsAction.ChooseSource);
            Assert.Contains(details.MoreActions, a => a.Action == DetailsAction.Binge);
            // The primary action is the widest button in the row.
            var nodes = UiLayout.Build(h).Nodes;
            var widths = details.Actions.ToDictionary(a => a.Action, a => nodes.Single(n => n.Key == "action:" + a.Action + ":bg").W);
            Assert.Equal(widths.Values.Max(), widths[DetailsAction.Primary]);
            // Down reaches the episode list without a button for it.
            h.HandleKey(RemoteKey.Down);
            if (details.Focus == DetailsFocus.Seasons) h.HandleKey(RemoteKey.Down);
            Assert.Equal(DetailsFocus.Episodes, details.Focus);
            h.HandleKey(RemoteKey.Up);
            h.HandleKey(RemoteKey.Up);
            Assert.Equal(DetailsFocus.Actions, details.Focus);
            for (var i = 0; i < 6; i++) h.HandleKey(RemoteKey.Right);
            Assert.Equal(DetailsAction.More, details.Actions[details.ActionIndex].Action);   // three presses from Play at most
            h.HandleKey(RemoteKey.Select);
            Assert.Contains("[dialog] More:", harness.Text);
            Assert.Contains("[Choose a source]", harness.Text);
            Assert.Contains("[Play and keep going (binge)]", harness.Text);
        }

        [Fact]
        public void RateSendsFeedbackAndLabelsTheButton()
        {
            var h = Harness();
            var feedback = new RecordingFeedback();
            h.Shell.Services.Feedback = feedback;
            var details = Open(h);
            while (details.Actions[details.ActionIndex].Action != DetailsAction.Rate)
                h.Press(RemoteKey.Right);
            h.Press(RemoteKey.Select);
            Assert.Contains("[dialog] Rate Breaking Bad", h.Text);
            h.Press(RemoteKey.Select);   // "I like this"
            Assert.Equal(new[] { "up tt0903747" }, feedback.Calls);
            Assert.Contains("[Rated: like]", h.Text);
            h.Press(RemoteKey.Select, RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);   // Not interested
            Assert.Equal("hide tt0903747", feedback.Calls.Last());
        }

        [Fact]
        public void LibraryButtonOffersRemoveAndAlerts()
        {
            var h = Harness(new LibraryState());
            var details = Open(h);
            while (details.Actions[details.ActionIndex].Action != DetailsAction.Library)
                h.Press(RemoteKey.Right);
            h.Press(RemoteKey.Select);
            Assert.Contains("[dialog] Breaking Bad is in your library: [Remove from library] [Turn alerts off] [Cancel]".Replace("[Remove", ">[Remove"), h.Text);
            h.Press(RemoteKey.Right, RemoteKey.Select);
            Assert.True(h.Library.Entries["tt0903747"].State.NoNotif);
        }

        [Fact]
        public void HomeMenuKeyRatesFocusedCard()
        {
            var h = Harness(new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_820_000 });
            var feedback = new RecordingFeedback();
            h.Shell.Services.Feedback = feedback;
            h.Shell.Start(new HomeViewModel());
            h.Press(RemoteKey.Menu);
            Assert.Contains("[dialog] Breaking Bad", h.Text);
            h.Press(RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);   // Not for me
            Assert.Equal(new[] { "down tt0903747" }, feedback.Calls);
        }
    }
}
