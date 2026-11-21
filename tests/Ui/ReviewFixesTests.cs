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
    public class ReviewFixesTests
    {
        private static UiHarness WithContinue(long offset, long duration)
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = offset, DurationMs = duration, LastWatched = h.Clock.UtcNow }
            };
            return h;
        }

        [Fact]
        public void OnePressPlayIgnoresRepeatsAndLateResults()
        {
            var h = WithContinue(600_000, 2_820_000);
            h.Shell.Start(new HomeViewModel());
            h.Catalog.MetaGate = new TaskCompletionSource<MetaDetail>();
            h.Press(RemoteKey.Select, RemoteKey.Select);
            h.Press(RemoteKey.Up, RemoteKey.Select);   // user went to Search meanwhile
            Assert.IsType<SearchViewModel>(h.Shell.Current);
            h.Catalog.MetaGate.SetResult(UiHarness.Series);
            Assert.IsType<SearchViewModel>(h.Shell.Current);
            Assert.Null(h.Player.LastRequest);
        }

        [Fact]
        public void OnePressPlayStartsOnceWhenMetadataArrives()
        {
            var h = WithContinue(600_000, 2_820_000);
            h.Shell.Start(new HomeViewModel());
            h.Catalog.MetaGate = new TaskCompletionSource<MetaDetail>();
            h.Press(RemoteKey.Select, RemoteKey.Select);
            h.Catalog.MetaGate.SetResult(UiHarness.Series);
            Assert.Single(h.Shell.Stack.OfType<PlayerViewModel>());
        }

        [Fact]
        public void ResumeNearTheEndPlaysTheNextEpisode()
        {
            var h = WithContinue(2_700_000, 2_820_000);
            h.Shell.Start(new HomeViewModel());
            h.Press(RemoteKey.Select);
            Assert.Equal("tt0903747:1:3", h.Player.LastRequest.Video.Id);
            Assert.Equal(0, h.Player.LastRequest.ResumeMs);
        }

        [Fact]
        public void LongDialogsStackTheirButtons()
        {
            var h = WithContinue(600_000, 2_820_000);
            h.Shell.Services.Feedback = new RecordingFeedback();
            h.Shell.Start(new HomeViewModel());
            h.Press(RemoteKey.Menu);
            var buttons = UiLayout.Build(h.Shell).Nodes.Where(n => n.Key.StartsWith("dialog:button:", StringComparison.Ordinal) && n.Key.EndsWith(":bg", StringComparison.Ordinal)).ToList();
            Assert.Equal(5, buttons.Count);
            Assert.All(buttons, b => Assert.True(b.X + b.W <= UiLayout.DialogX + UiLayout.DialogW));
            Assert.Equal(buttons.Count, buttons.Select(b => b.Y).Distinct().Count());
        }

        [Fact]
        public void NextEpisodePreviewDoesNotCoverTheTransportBar()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Shell.Services.Extras = new FakeExtras();
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.NextEpisode);
            var nodes = UiLayout.Build(h.Shell).Nodes;
            var bottom = nodes.Where(n => n.Key.StartsWith("overlay:next", StringComparison.Ordinal)).Max(n => n.Y + n.H);
            Assert.True(bottom < 880, "preview ends at " + bottom);
        }

        private sealed class SubtitleResolver : ISourceResolver
        {
            public Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token) =>
                Task.FromResult<IReadOnlyList<StreamCandidate>>(new[] { new StreamCandidate { Name = "x", InfoHash = "AAAA", Quality = "1080p", LanguageTier = 4 } });
            public Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token) =>
                Task.FromResult<IReadOnlyList<SubtitleCandidate>>(new[] { new SubtitleCandidate { Language = "spa", Url = "https://subs/es.srt" } });
        }

        [Fact]
        public void SignatureAddsSubtitleAddonLanguages()
        {
            var resolver = new SubtitleResolver();
            var signatures = new SourceSignatures(new RankedSourceCatalog(resolver), resolver);
            var entry = signatures.GetAsync(UiHarness.Movie, null, CancellationToken.None).Result;
            Assert.Contains("ES", entry.Facts.SubtitleLanguages);
            Assert.EndsWith("EN audio · ES subs", SourceSignatures.Line(entry));
        }
    }
}
