using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public sealed class FakeExtras : IMetaExtras
    {
        public readonly Dictionary<string, MetaExtras> Items = new Dictionary<string, MetaExtras>();
        public Task<MetaExtras> GetAsync(MediaType type, string id, CancellationToken token) =>
            Task.FromResult(Items.TryGetValue(id, out var e) ? e : new MetaExtras());
    }

    public sealed class GatedSearch : ISearchService
    {
        public readonly List<(string Query, TaskCompletionSource<SearchResults> Gate, CancellationToken Token)> Calls = new List<(string, TaskCompletionSource<SearchResults>, CancellationToken)>();

        public Task<SearchResults> SearchAsync(string query, CancellationToken token)
        {
            var gate = new TaskCompletionSource<SearchResults>();
            token.Register(() => gate.TrySetCanceled());
            Calls.Add((query, gate, token));
            return gate.Task;
        }
    }

    public class PolishTests
    {
        private static UiHarness WithExtras(out FakeExtras extras)
        {
            var h = new UiHarness();
            extras = new FakeExtras();
            extras.Items[UiHarness.Series.Summary.Id] = new MetaExtras
            {
                Rating = "9.5", Runtime = "49 min", Trailers = new[] { "VFkjBy2b50Q" }, Description = "Chemistry teacher.",
                Episodes = new Dictionary<string, EpisodeExtras> { ["tt0903747:1:1"] = new EpisodeExtras { Overview = "Walter's 50th birthday.", Thumbnail = "https://episodes/1.jpg" } }
            };
            h.Shell.Services.Extras = extras;
            return h;
        }

        // ---- animator ----

        private static UiFrame Frame(string screen, params UiNode[] nodes)
        {
            var f = new UiFrame { ScreenKey = screen };
            f.Nodes.AddRange(nodes);
            return f;
        }

        [Fact]
        public void AnimatorGlidesMovesWithEaseOut()
        {
            var a = new UiAnimator();
            a.Step(Frame("s", new UiNode { Key = "tile", X = 0, Y = 0, W = 100, H = 100, Enter = UiEnter.None }), 0);
            var target = Frame("s", new UiNode { Key = "tile", X = 300, Y = 0, W = 100, H = 100, MoveMs = 200 });
            Assert.Equal(0, a.Step(target, 0).Nodes.Single().X);
            var mid = a.Step(target, 100).Nodes.Single();
            Assert.InRange(mid.X, 240, 280);   // ease-out: well past half way at t = 0.5
            Assert.True(a.IsAnimating);
            Assert.Equal(300, a.Step(target, 250).Nodes.Single().X);
            Assert.False(a.IsAnimating);
        }

        [Fact]
        public void AnimatorFadesInNewNodesAndFadesOutLeavingOnes()
        {
            var a = new UiAnimator();
            a.Step(Frame("s", new UiNode { Key = "old", W = 10, H = 10, Enter = UiEnter.None, ExitMs = 200 }), 0);
            var target = Frame("s", new UiNode { Key = "new", W = 10, H = 10, EnterMs = 200 });
            var first = a.Step(target, 0);
            Assert.Equal(new[] { "old", "new" }, first.Nodes.Select(n => n.Key).OrderByDescending(k => k).ToArray());
            Assert.Equal(0, first.Nodes.Single(n => n.Key == "new").Opacity);
            var mid = a.Step(target, 100);
            // Fades ease out when appearing and ease in when leaving: halfway, the new node is mostly there and the
            // old one has only begun to go.
            Assert.InRange(mid.Nodes.Single(n => n.Key == "old").Opacity, 0.5, 0.99);
            Assert.InRange(mid.Nodes.Single(n => n.Key == "new").Opacity, 0.5, 0.99);
            var end = a.Step(target, 300);
            Assert.Equal(new[] { "new" }, end.Nodes.Select(n => n.Key).ToArray());
            Assert.Equal(1, end.Nodes.Single().Opacity);
        }

        [Fact]
        public void ReducedMotionJumps()
        {
            var a = new UiAnimator { ReducedMotion = true };
            a.Step(Frame("s", new UiNode { Key = "tile", X = 0, W = 10, H = 10 }), 0);
            var f = a.Step(Frame("s", new UiNode { Key = "tile", X = 500, W = 10, H = 10 }), 1);
            Assert.Equal(500, f.Nodes.Single().X);
            Assert.Equal(1, f.Nodes.Single().Opacity);
            Assert.False(a.IsAnimating);
        }

        [Fact]
        public void ScreenChangeSlidesNewScreenIn()
        {
            var a = new UiAnimator();
            a.Step(Frame("home", new UiNode { Key = "x", X = 100, W = 10, H = 10, Enter = UiEnter.None }), 0);
            var f = a.Step(Frame("details", new UiNode { Key = "x", X = 100, W = 10, H = 10 }), 1000);
            Assert.Equal(100 + UiAnimator.ScreenSlidePx, f.Nodes.Single().X);
            Assert.Equal(0, f.Nodes.Single().Opacity);
        }

        [Fact]
        public void ReducedMotionSettingTogglesFromHomeMenu()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            var home = (HomeViewModel)h.Shell.Current;
            h.Press(RemoteKey.Up);
            while (home.FocusedTile.Action != TileAction.Motion)
                h.Press(RemoteKey.Right);
            Assert.Equal("Settings", home.FocusedTile.Title);   // the nav shows a plain "Settings", not the value
            h.Press(RemoteKey.Select);
            Assert.Contains(">[Add-ons] [Stremio account] [Reduced motion: Off]", h.Text);   // the rarely used items live here
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Select);
            Assert.True(h.Player.Settings.ReducedMotion);
            Assert.Contains(">[Reduced motion: On]", h.Text);   // the list stays open on the same row
            h.Press(RemoteKey.Back);
            Assert.Null(h.Shell.Dialog);
        }

        // ---- scroll anchoring ----

        [Fact]
        public void FocusedHomeRowStaysAtAnchorBelowTheBillboardText()
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            h.Wait(100);   // the billboard picks up the focused title on the first tick
            // Initial view: the first row starts below the billboard frame.
            var initial = UiLayout.Build(h.Shell);
            var hero = initial.Nodes.Single(n => n.Key == "hero:bg:tt0903747");
            Assert.True(initial.Nodes.Single(n => n.Key == "row:continue:title").Y >= hero.Y + hero.H - 40);
            h.Press(RemoteKey.Down);
            var home = (HomeViewModel)h.Shell.Current;
            var frame = UiLayout.Build(h.Shell);
            var focusedTitle = frame.Nodes.Single(n => n.Key == "row:" + home.FocusedRow.Key + ":title");
            h.Press(RemoteKey.Down);
            var deeper = UiLayout.Build(h.Shell).Nodes.Single(n => n.Key == "row:" + home.FocusedRow.Key + ":title");
            Assert.Equal(focusedTitle.Y, deeper.Y);   // scrolled rows keep the same anchor
            // The row above only peeks: no captions, and whatever of it reaches into the banner is under the band.
            Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("row:continue:", StringComparison.Ordinal) && n.Key.EndsWith(":caption", StringComparison.Ordinal) && n.Opacity > 0);
            var band = frame.Nodes.FindIndex(n => n.Key == "home:band");
            Assert.All(frame.Nodes.Select((n, i) => (n, i)).Where(x => x.n.Key.StartsWith("row:", StringComparison.Ordinal) && x.n.Y < UiLayout.ScrolledHeroBottom), x => Assert.True(x.i < band));
            Assert.Contains(frame.Nodes, n => n.Key.StartsWith("row:", StringComparison.Ordinal) && n.Key.EndsWith(":title", StringComparison.Ordinal) && n.Y > focusedTitle.Y && n.Y < UiFrame.Height);   // next row peeks below
        }

        // ---- trailers and billboard ----

        [Fact]
        public void HomePreviewNeverAutoplaysWithoutRecentRemoteInput()
        {
            var h = WithExtras(out _);
            h.Shell.Services.TrailerCards = false;   // the billboard trailer view (the pop-out card's fallback mode)
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            var home = (HomeViewModel)h.Shell.Current;
            h.Wait(HomeViewModel.TrailerDelayMs + 5000);
            Assert.Equal("Night Harbor", home.HeroTile.Title);
            Assert.Null(home.TrailerId);   // launched and left alone: no preview (Netflix-style)
            h.Press(RemoteKey.Left);       // any remote input (focus stays on the first card)
            h.Wait(HomeViewModel.TrailerDelayMs + 200);
            Assert.Equal("VFkjBy2b50Q", home.TrailerId);   // right after interacting: the preview starts
            h.Wait(15_000);
            h.Press(RemoteKey.PlayPause);
        }

        [Fact]
        public void HomeBillboardStartsTrailerWithSoundAfterIdle()
        {
            var h = WithExtras(out _);
            h.Shell.Services.TrailerCards = false;   // the billboard trailer view (the pop-out card's fallback mode)
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            h.Shell.Services.LastInteractionMs = 0; h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;   // as if the viewer just navigated
            var home = (HomeViewModel)h.Shell.Current;
            h.Wait(500);
            Assert.Equal("Night Harbor", home.HeroTile.Title);
            Assert.Null(home.TrailerId);
            h.Wait(HomeViewModel.TrailerDelayMs);
            Assert.Equal("VFkjBy2b50Q", home.TrailerId);
            Assert.False(home.TrailerMuted);   // previews play with sound (the player fades it in)
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Kind == UiNodeKind.Trailer && !n.Muted);
            // Trailer view from the first frame of the trailer: the rows slide down to a strip and the menu hides.
            Assert.True(home.Immersive);
            var frame = UiLayout.Build(h.Shell);
            Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("menu:", StringComparison.Ordinal));
            Assert.True(frame.Nodes.Where(n => n.Key.EndsWith(":ph", StringComparison.Ordinal) && n.Key.StartsWith("row:", StringComparison.Ordinal)).Min(n => n.Y) >= UiFrame.Height - 130);
            Assert.Contains(frame.Nodes, n => n.Kind == UiNodeKind.Trailer && n.X == 0 && n.W == UiFrame.Width);
            h.Press(RemoteKey.Down);   // any key ends the trailer, brings the UI back and does nothing else
            Assert.False(home.Immersive);
            Assert.Null(home.TrailerId);
            Assert.Equal("continue", home.FocusedRow.Key);
            h.Wait(HomeViewModel.TrailerDelayMs + 1000);
            Assert.Null(home.TrailerId);   // dismissed: not again until focus moves away and back
        }

        [Fact]
        public void HomePreviewPlaysOnceAndFollowsPreviewSettings()
        {
            var h = WithExtras(out _);
            h.Shell.Services.TrailerCards = false;   // the billboard trailer view (the pop-out card's fallback mode)
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            h.Shell.Services.LastInteractionMs = 0; h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;   // as if the viewer just navigated
            var home = (HomeViewModel)h.Shell.Current;
            h.Wait(500 + HomeViewModel.TrailerDelayMs);
            Assert.Equal("VFkjBy2b50Q", home.TrailerId);
            h.Shell.Services.ReportTrailerEnded("VFkjBy2b50Q");   // the painter's player reached the end
            h.Wait(200);
            Assert.Null(home.TrailerId);
            Assert.False(home.Immersive);
            h.Wait(HomeViewModel.TrailerDelayMs * 2);
            Assert.Null(home.TrailerId);   // played once

            var settings = h.Shell.Services.Player.Settings;
            settings.SetPreviewSound(false);
            h.Press(RemoteKey.Up);
            h.Press(RemoteKey.Down);       // focus away (menu row) and back
            h.Wait(500 + HomeViewModel.TrailerDelayMs);
            Assert.Equal("VFkjBy2b50Q", home.TrailerId);
            Assert.True(home.TrailerMuted);   // Preview sound off: starts muted

            settings.SetAutoplayPreviews(false);
            h.Press(RemoteKey.Up);
            h.Press(RemoteKey.Down);
            h.Wait(500 + HomeViewModel.TrailerDelayMs * 2);
            Assert.Null(home.TrailerId);      // Autoplay previews off
        }

        [Fact]
        public void PainterWithoutTrailerPlaybackNeverEntersTrailerView()
        {
            // The TV has no trailer player: the rows must stay put and no sound hint or Trailer button shows.
            var h = WithExtras(out _);
            h.Shell.Services.TrailerPlayback = false;
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            h.Shell.Start(new HomeViewModel());
            var home = (HomeViewModel)h.Shell.Current;
            h.Wait(9000);
            Assert.Null(home.TrailerId);
            Assert.False(home.Immersive);
            var frame = UiLayout.Build(h.Shell);
            Assert.DoesNotContain(frame.Nodes, n => n.Kind == UiNodeKind.Trailer || n.Key == "hero:sound");
            Assert.Contains(frame.Nodes, n => n.Key.StartsWith("menu:", StringComparison.Ordinal));

            var details = new DetailsViewModel(MediaType.Series, "tt0903747");
            h.Shell.Push(details);
            h.Wait(9000);
            Assert.Null(details.TrailerId);
            Assert.False(details.Immersive);
            Assert.DoesNotContain(details.Actions, a => a.Action == DetailsAction.Trailer);
        }

        [Fact]
        public void DetailsShowsEpisodeOverviewAndTrailerButton()
        {
            var h = WithExtras(out _);
            h.Shell.Start(new HomeViewModel());
            h.Shell.Services.LastInteractionMs = 0; h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;   // as if the viewer just navigated
            var details = new DetailsViewModel(MediaType.Series, "tt0903747");
            h.Shell.Push(details);
            Assert.Contains("[Trailer]", h.Text);
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Text == "Walter's 50th birthday.");
            Assert.Contains(frame.Nodes, n => n.ImageUrl == "https://episodes/1.jpg");
            Assert.Contains(frame.Nodes, n => n.Text == "1.  Pilot");
            h.Wait(DetailsViewModel.TrailerDelayMs + 100);
            Assert.Equal("VFkjBy2b50Q", details.TrailerId);   // with sound, behind the page: nothing is hidden
            Assert.True(!details.Immersive && !details.TrailerMuted);
            var playing = UiLayout.Build(h.Shell).Nodes;
            Assert.Contains(playing, n => n.Kind == UiNodeKind.Trailer);
            Assert.Contains(playing, n => n.Key == "action:Primary:bg");
            Assert.Contains(playing, n => n.Key == "details:description");
            // No on-screen sound hint over the trailer (user feedback, batch 190800); sound is the "Preview sound" setting.
            Assert.DoesNotContain(UiLayout.Build(h.Shell).Nodes, n => n.Key.StartsWith("hero:sound", StringComparison.Ordinal));
            h.Press(RemoteKey.Right);                         // any other key stops it, and the key still acts
            Assert.Null(details.TrailerId);
            Assert.Equal(DetailsFocus.Actions, details.Focus);
            Assert.Equal(1, details.ActionIndex);
            h.Wait(DetailsViewModel.TrailerDelayMs + 2000);
            Assert.Null(details.TrailerId);                   // it does not start again by itself
            while (details.Actions[details.ActionIndex].Action != DetailsAction.Trailer)
                h.Press(RemoteKey.Right);
            Assert.Equal("Trailer", details.Actions[details.ActionIndex].Label);   // one fixed label
            h.Press(RemoteKey.Select);
            Assert.True(details.Immersive);
            Assert.False(details.TrailerMuted);               // the button plays it with sound
        }

        [Fact]
        public void WatchedEpisodeGetsCheckBadgeInBothFocusStates()
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry { Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series };
            h.Library.WatchedIds.Add("tt0903747:1:1");
            h.Shell.Start(new HomeViewModel());
            h.Shell.Push(new DetailsViewModel(MediaType.Series, "tt0903747"));
            string Badge(UiFrame f) => f.Nodes.Where(n => n.Key == "episode:tt0903747:1:1:watched-icon").Select(n => n.Fill).Distinct().Single();
            var unfocused = Badge(UiLayout.Build(h.Shell));
            h.Press(RemoteKey.Down, RemoteKey.Down);
            var focused = Badge(UiLayout.Build(h.Shell));
            Assert.Equal(UiTheme.Watched, unfocused);
            Assert.Equal(unfocused, focused);
        }

        // ---- search ----

        [Fact]
        public void SearchCancelsStaleRequestsAndShowsLatest()
        {
            var h = new UiHarness();
            var search = new GatedSearch();
            h.Shell.Services.Search = search;
            h.Shell.Start(new HomeViewModel());
            var vm = new SearchViewModel();
            h.Shell.Push(vm);
            vm.Keyboard.FocusLabel("d");
            h.Press(RemoteKey.Select);          // "d" searched after 300 ms
            Assert.Single(search.Calls);
            vm.Keyboard.FocusLabel("a");
            h.Shell.HandleKey(RemoteKey.Select);
            h.Wait(400);                        // "da" supersedes "d"
            Assert.Equal(2, search.Calls.Count);
            Assert.True(search.Calls[0].Token.IsCancellationRequested);
            search.Calls[1].Gate.SetResult(new SearchResults
            {
                Query = "da", Suggestions = new[] { "Dark" },
                Items = new[] { new SearchItem { Meta = new MetaSummary { Id = "tt5753856", Name = "Dark", Type = MediaType.Series }, InLibrary = true } }
            });
            Assert.Contains("Dark [in library]", h.Text);
            Assert.Contains("Refine: ", h.Text);
        }

        [Fact]
        public void SearchMergeDedupesMarksLibraryAndRanksExactFirst()
        {
            var found = new[]
            {
                new SearchItem { Meta = new MetaSummary { Id = "tt1", Name = "The Dark Knight" }, AddonName = "Cinemeta" },
                new SearchItem { Meta = new MetaSummary { Id = "tt2", Name = "Dark" }, AddonName = "Cinemeta" },
                new SearchItem { Meta = new MetaSummary { Id = "tt1", Name = "The Dark Knight" }, AddonName = "Other" }
            };
            var library = new[] { new LibraryEntry { Id = "tt2", Name = "Dark" }, new LibraryEntry { Id = "tt3", Name = "Dark Matter" } };
            var r = SearchMerge.Merge("dark", found, library);
            Assert.Equal(new[] { "tt2", "tt3", "tt1" }, r.Items.Select(i => i.Meta.Id).ToArray());
            Assert.True(r.Items[0].InLibrary);
            Assert.Equal("Dark", r.Suggestions[0]);
        }

        // ---- sources ----

        private static AddonStreamItem Stream(string title, string hash) => new AddonStreamItem
        {
            AddonId = "torrentio", AddonName = "Torrentio", Name = "Torrentio\n1080p", Title = title, InfoHash = hash, FileIdx = 0, Filename = title.Split('\n')[0]
        };

        [Fact]
        public void SourceEntriesKeepRankAndListUnplayableWithReason()
        {
            var meta = new MetaDetail { Summary = new MetaSummary { Id = "tt1", Type = MediaType.Movie, Name = "Film" } };
            var streams = new[]
            {
                Stream("Film.2020.1080p.WEB-DL.DDP5.1.H.264-NTb.mkv\n\U0001F464 412 \U0001F4BE 2.3 GB\n\U0001F1EC\U0001F1E7 / \U0001F1EA\U0001F1F8", "1111111111111111111111111111111111111111"),
                Stream("Film.2020.1080p.WEB.AV1.Opus.mkv\n\U0001F464 90 \U0001F4BE 900 MB", "2222222222222222222222222222222222222222"),
                Stream("Film.2020.2160p.WEB-DL.HDR10.DDP5.1.HEVC.mkv\n\U0001F464 60 \U0001F4BE 5.8 GB", "3333333333333333333333333333333333333333")
            };
            var entries = AddonSourceCatalog.FromStreams(streams, meta, null, new SourceOptions(), DeviceCaps.Ru7100);
            Assert.True(entries[0].Playable);
            var av1 = entries.Single(e => e.Candidate.InfoHash == "2222222222222222222222222222222222222222");
            Assert.False(av1.Playable);
            Assert.Contains("AV1", av1.RejectReason);
            var best = entries.First(e => e.Playable).Facts;
            Assert.Equal("1080p", best.Quality);
            Assert.Equal("H.264", best.VideoCodec);
            Assert.Equal("DD+", best.AudioCodec);
            Assert.Equal("5.1", best.Channels);
            Assert.Equal(new[] { "EN", "ES" }, best.AudioLanguages.ToArray());
            var uhd = entries.Single(e => e.Candidate.InfoHash == "3333333333333333333333333333333333333333").Facts;
            Assert.Equal("4K", uhd.Quality);
            Assert.Equal("HDR10", uhd.Hdr);
        }

        private sealed class ListCatalog : ISourceCatalog
        {
            public IReadOnlyList<SourceEntry> Entries;
            public Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token) => Task.FromResult(Entries);
        }

        [Fact]
        public void SourcePickerHidesUnplayableUntilToggledAndTagsBestMatch()
        {
            var h = new UiHarness();
            var meta = UiHarness.Movie;
            var streams = new[]
            {
                Stream("Sintel.2010.1080p.BluRay.x264.AAC.mkv\n\U0001F464 300 \U0001F4BE 1.9 GB", "1111111111111111111111111111111111111111"),
                Stream("Sintel.2010.1080p.CAM.x264.mkv\n\U0001F464 30 \U0001F4BE 1.2 GB", "2222222222222222222222222222222222222222")
            };
            h.Shell.Services.SourceCatalog = new ListCatalog { Entries = AddonSourceCatalog.FromStreams(streams, meta, null, new SourceOptions(), DeviceCaps.Ru7100) };
            h.Shell.Start(new HomeViewModel());
            var picker = new SourcePickerViewModel(meta, null, 0, PlaybackMode.Single, false);
            h.Shell.Push(picker);
            Assert.Single(picker.Entries);
            Assert.Contains("[BEST MATCH:", h.Text);
            Assert.Contains("Show unplayable (1)", h.Text);
            h.Press(RemoteKey.Up, RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);   // chips All, 1080p, then the toggle
            Assert.Equal(2, picker.Entries.Count);
            Assert.Contains("[unplayable: cam/screener]", h.Text);
            var frame = UiLayout.Build(h.Shell);
            Assert.Contains(frame.Nodes, n => n.Text == "Best match" && n.Color == UiTheme.TextOnAccent);
            Assert.Contains(frame.Nodes, n => n.Text != null && n.Text.StartsWith("Can't play on this TV", StringComparison.Ordinal));
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Select);   // unplayable: explains instead of playing
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
        }

        [Fact]
        public void MiddleEllipsisKeepsBothEnds()
        {
            var s = UiFormat.MiddleEllipsis("Night.Harbor.S01E01.1080p.BluRay.x264.DTS-HD.MA.5.1-GROUP.mkv", 30);
            Assert.Equal(30, s.Length);
            Assert.StartsWith("Night.Harbor", s);
            Assert.EndsWith("GROUP.mkv", s);
        }
    }
}
