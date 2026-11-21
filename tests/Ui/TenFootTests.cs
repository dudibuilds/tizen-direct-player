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
    /// 10-foot UI sweep: every screen and its main states, checked for readable sizes, whole lines, no overlapping
    /// text, one-line boxes tall enough for the TV's line height, and text inside the overscan-safe area.
    public class TenFootTests
    {
        private const string Long = "A chemistry teacher diagnosed with inoperable lung cancer turns to manufacturing and selling methamphetamine "
            + "with a former student to secure his family's future, and nobody around him is ready for what he becomes.";

        private sealed class ListCatalog : ISourceCatalog
        {
            public IReadOnlyList<SourceEntry> Entries;
            public Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token) => Task.FromResult(Entries);
        }

        private static AddonStreamItem Stream(string title, string hash) => new AddonStreamItem
        {
            AddonId = "torrentio", AddonName = "Torrentio", Name = "Torrentio\n1080p", Title = title, InfoHash = hash, FileIdx = 0, Filename = title.Split('\n')[0]
        };

        /// Scrolling lists and strips may be cut by the bottom or right edge on purpose.
        private static readonly string[] Scrolling = { "row:", "result:", "browse:", "source:", "episode:", "chip:", "filter:", "subtitle" };

        private static bool Scrolls(UiNode n) => Scrolling.Any(p => n.Key.StartsWith(p, StringComparison.Ordinal));

        private static void Check(UiHarness h, string context)
        {
            var frame = UiLayout.Build(h.Shell);
            // The subtitle layer sits on top of everything on purpose (style preview over the panels); dialogs and
            // toasts float over the dimmed screen. Text within the same layer must never overlap.
            bool Floating(UiNode n) => n.Key == "subtitle" || n.Key.StartsWith("dialog:", StringComparison.Ordinal) || n.Key.StartsWith("toast:", StringComparison.Ordinal);
            FrameChecks.NoTextOverlap(frame, context, (a, b) => Floating(a) != Floating(b) || a.Key == "subtitle" || b.Key == "subtitle");
            FrameChecks.TextIsReadable(frame, context);
            FrameChecks.WrappedTextShowsWholeLines(frame, context);
            FrameChecks.LinesFitTheirBoxes(frame, context);
            FrameChecks.TextOnStage(frame, context, Scrolls);
            FrameChecks.TextInSafeArea(frame, context, Scrolls);
            // A focused card, grown and ringed, stays above the bottom safe line (rows below it may be cut).
            foreach (var ring in frame.Nodes.Where(n => n.Key.EndsWith(":ring", StringComparison.Ordinal)))
                Assert.True(ring.Y + ring.H <= UiScale.Bottom + 8, context + ": focus ring " + ring.Key + " ends at y " + (ring.Y + ring.H));
            // Button, key and menu labels are never cut to "…" (titles may be).
            foreach (var n in frame.Nodes.Where(n => n.Kind == UiNodeKind.Text && n.Key.EndsWith(":label", StringComparison.Ordinal) && !string.IsNullOrEmpty(n.Text)))
                Assert.True(n.Text.Length * n.FontSize * UiFormat.GlyphWidth <= n.W, context + ": label \"" + n.Text + "\" (" + n.Key + ") does not fit its " + n.W + " px box");
            // Player panels are right-edge sheets: full height, flush with the right edge, and nothing of the overlay's
            // own text (title, transport) reaches under them.
            var sheet = frame.Nodes.FirstOrDefault(n => n.Key == "sheet:bg");
            if (sheet != null)
            {
                Assert.True(sheet.Y == 0 && sheet.H == UiFrame.Height && sheet.X + sheet.W == UiFrame.Width, context + ": the sheet is not an edge sheet");
                foreach (var n in frame.Nodes.Where(n => n.Kind == UiNodeKind.Text && (n.Key.StartsWith("overlay:", StringComparison.Ordinal) || n.Key.StartsWith("transport:", StringComparison.Ordinal))))
                    Assert.True(n.X + n.W <= sheet.X, context + ": " + n.Key + " runs under the sheet");
            }
        }

        private static UiHarness Harness()
        {
            var h = new UiHarness();
            var extras = new FakeExtras();
            var episodes = UiHarness.Series.Videos.ToDictionary(v => v.Id, v => new EpisodeExtras { Overview = Long, Thumbnail = "https://img/" + v.Id + ".jpg" });
            extras.Items["tt0903747"] = new MetaExtras
            {
                Rating = "9.5", Runtime = "49 min", Description = Long, Genres = new[] { "Crime", "Drama", "Thriller" }, Cast = new[] { "Bryan Cranston", "Aaron Paul", "Anna Gunn", "Dean Norris" },
                Episodes = episodes
            };
            extras.Items["tt1727587"] = new MetaExtras { Rating = "7.4", Runtime = "15 min", Description = Long, Genres = new[] { "Animation" }, Cast = new[] { "Halina Reijn" } };
            h.Shell.Services.Extras = extras;
            for (var i = 0; i < 9; i++)
            {
                var id = "tt80000" + i;
                var summary = new MetaSummary { Id = id, Type = i % 2 == 0 ? MediaType.Movie : MediaType.Series, Name = "A Considerably Longer Search Result Title " + i, ReleaseInfo = "2021", Poster = "https://img/" + id + ".jpg" };
                h.Catalog.Metas[id] = new MetaDetail { Summary = summary, Videos = Array.Empty<VideoInfo>() };
                h.Catalog.SearchResults.Add(summary);
            }
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Night Harbor", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000 }
            };
            return h;
        }

        [Fact]
        public void HomeAndCardMenu()
        {
            var h = Harness();
            h.Shell.Services.Feedback = new RecordingFeedback();
            h.Shell.Start(new HomeViewModel());
            h.Wait(600);
            Check(h, "home");
            h.Press(RemoteKey.Up);
            Check(h, "home menu");
            h.Press(RemoteKey.Down, RemoteKey.Down);
            Check(h, "home scrolled");
            h.Press(RemoteKey.Up);
            h.Press(RemoteKey.Menu);
            Assert.NotNull(h.Shell.Dialog);
            Check(h, "card menu");
            h.Press(RemoteKey.Back);
            h.Shell.ShowToast("Added Night Harbor to your library and turned on new-episode alerts");
            Check(h, "toast");
        }

        [Fact]
        public void HomeWithPostersFirstKeepsTheFocusedPosterInTheSafeArea()
        {
            var h = Harness();
            h.Library.Entries.Clear();   // no Continue watching: the first row has posters
            h.Shell.Start(new HomeViewModel());
            h.Wait(600);
            var home = (HomeViewModel)h.Shell.Current;
            Assert.NotEqual(TileStyle.Landscape, home.Rows.First(r => !r.IsMenu).Style);
            Check(h, "home, posters first");
        }

        [Fact]
        public void SearchKeyboardResultsAndChips()
        {
            var h = Harness();
            h.Shell.Start(new HomeViewModel());
            var vm = new SearchViewModel();
            h.Shell.Push(vm);
            Check(h, "search empty");
            foreach (var c in "a")
            {
                vm.Keyboard.FocusLabel(c.ToString());
                h.Press(RemoteKey.Select);
            }
            h.Wait(600);
            Assert.True(vm.Items.Count > 3);
            Check(h, "search typed");
            for (var i = 0; i < 8 && vm.Focus == SearchFocus.Keyboard; i++)
                h.Press(RemoteKey.Right);
            Assert.Equal(SearchFocus.Results, vm.Focus);
            Check(h, "search first result");
            h.Press(RemoteKey.Up);
            Assert.Equal(SearchFocus.Suggestions, vm.Focus);
            Check(h, "search chips");
            for (var i = 0; i < 8; i++)
                h.Press(RemoteKey.Right);
            Check(h, "search chips scrolled");
            h.Press(RemoteKey.Down);
            Assert.Equal(SearchFocus.Results, vm.Focus);
            h.Wait(1500);
            Check(h, "search result focused");
            h.Press(RemoteKey.Right, RemoteKey.Right);
            Check(h, "search last column");
            h.Press(RemoteKey.Down);
            Check(h, "search second row");
        }

        [Fact]
        public void TitlePageMovieAndSeries()
        {
            var h = Harness();
            h.Shell.Start(new HomeViewModel());
            h.Shell.Push(new DetailsViewModel(MediaType.Movie, "tt1727587"));
            h.Wait(300);
            Check(h, "movie");
            h.Press(RemoteKey.Right, RemoteKey.Right, RemoteKey.Right);
            Check(h, "movie actions right");
            h.Shell.Pop();
            var details = new DetailsViewModel(MediaType.Series, "tt0903747");
            h.Shell.Push(details);
            h.Wait(300);
            Check(h, "series");
            h.Press(RemoteKey.Down);
            Check(h, "series seasons");
            h.Press(RemoteKey.Down);
            Assert.Equal(DetailsFocus.Episodes, details.Focus);
            Check(h, "series episodes");
            h.Press(RemoteKey.Down);
            Check(h, "series episode 2");
            h.Press(RemoteKey.Right);
            Check(h, "series episode actions");
            h.Press(RemoteKey.Right);
            Check(h, "series episode queue");
        }

        [Fact]
        public void SourcePicker()
        {
            var h = Harness();
            var meta = UiHarness.Movie;
            var streams = new[]
            {
                Stream("Sintel.2010.1080p.WEB-DL.DDP5.1.H.264-NTb.mkv\n\U0001F464 412 \U0001F4BE 2.3 GB\n\U0001F1EC\U0001F1E7 / \U0001F1EA\U0001F1F8", "1111111111111111111111111111111111111111"),
                Stream("Sintel.2010.2160p.WEB-DL.HDR10.DDP5.1.HEVC.mkv\n\U0001F464 60 \U0001F4BE 15.8 GB", "3333333333333333333333333333333333333333"),
                Stream("Sintel.2010.720p.BluRay.x264.AAC.mkv\n\U0001F464 300 \U0001F4BE 900 MB", "4444444444444444444444444444444444444444"),
                Stream("Sintel.2010.1080p.WEB.AV1.Opus.mkv\n\U0001F464 90 \U0001F4BE 900 MB", "2222222222222222222222222222222222222222"),
                Stream("Sintel.2010.1080p.BluRay.x265.10bit.EAC3.5.1.mkv\n\U0001F464 1200 \U0001F4BE 4.4 GB", "5555555555555555555555555555555555555555")
            };
            h.Shell.Services.SourceCatalog = new ListCatalog { Entries = AddonSourceCatalog.FromStreams(streams, meta, null, new SourceOptions(), DeviceCaps.Ru7100) };
            h.Shell.Start(new HomeViewModel());
            var picker = new SourcePickerViewModel(meta, null, 0, PlaybackMode.Single, false);
            h.Shell.Push(picker);
            Check(h, "sources");
            h.Press(RemoteKey.Up, RemoteKey.Right, RemoteKey.Select);   // show unplayable
            Check(h, "sources toolbar");
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Down);
            Check(h, "sources scrolled");
        }

        [Fact]
        public void BrowseFiltersAndGrid()
        {
            var h = Harness();
            h.Shell.Start(new HomeViewModel());
            var browse = new BrowseViewModel();
            h.Shell.Push(browse);
            h.Wait(300);
            Check(h, "browse filters");
            for (var i = 0; i < BrowseViewModel.Filters.Count; i++)
                h.Press(RemoteKey.Right);
            Check(h, "browse last filter");
            h.Press(RemoteKey.Down);
            Check(h, "browse grid");
        }

        [Fact]
        public void AccountScreensAndDialogs()
        {
            var h = Harness();
            h.Shell.Start(new HomeViewModel());
            h.Shell.Push(new AddonsViewModel());
            h.Wait(200);
            Check(h, "add-ons");
            h.Press(RemoteKey.Down);
            Check(h, "add-ons item");
            h.Press(RemoteKey.Up, RemoteKey.Select);
            Assert.IsType<TextEntryViewModel>(h.Shell.Current);
            Check(h, "text entry");
            var entry = (TextEntryViewModel)h.Shell.Current;
            entry.Keyboard.Text = "https://stream-addon.example/providers=alpha,beta,gamma,delta,epsilon,zeta|language=spanish|qualityfilter=480p,scr,cam/manifest.json";
            Check(h, "text entry long url");
            h.Shell.Pop();
            h.Shell.Pop();

            h.Queue.State = new QueueSnapshot
            {
                Status = "ready",
                Items = Enumerable.Range(1, 9).Select(i => new QueueItemView { MetaId = "tt0903747", Name = "Night Harbor", VideoId = "tt0903747:1:" + i, Season = 1, Episode = i, Title = "An Episode With A Rather Long Title " + i }).ToList()
            };
            h.Shell.Push(new QueueViewModel());
            h.Wait(200);
            Check(h, "queue");
            h.Press(RemoteKey.Down, RemoteKey.Down, RemoteKey.Down);
            Check(h, "queue item");
            h.Shell.Pop();

            h.Shell.Push(new SignInViewModel());
            h.Wait(200);
            Check(h, "sign-in");
        }

        [Fact]
        public void PlayerOverlayPanelsAndStyleMenu()
        {
            var h = Harness();
            var vm = h.StartEpisode();
            h.Player.Snapshot.Title = "Night Harbor";
            h.Player.Snapshot.EpisodeLabel = "S1:E1 · Pilot";
            h.Player.Snapshot.SourceLabel = "1080p · 2.3 GB · Torrentio";
            h.Shell.Tick();
            h.Press(RemoteKey.Select);
            Check(h, "overlay");
            foreach (var row in vm.Actions)
            {
                h.FocusRow(vm, row);
                Check(h, "overlay row " + vm.FocusedRow);
            }
            h.FocusRow(vm, OverlayRow.More);
            h.Press(RemoteKey.Select);
            Check(h, "more panel");
            h.Press(RemoteKey.Back);
            h.OpenSubtitleSettings(vm);
            Assert.True(vm.StyleMenuOpen);
            for (var i = 0; i < PlayerViewModel.StyleRows.Count; i++)
            {
                Check(h, "style row " + vm.FocusedStyleRow);
                h.Press(RemoteKey.Down);
            }
            h.Press(RemoteKey.Back);
            Assert.Equal(PlayerPanel.Tracks, vm.Panel);
            Check(h, "tracks");
            h.Press(RemoteKey.Back);
            h.FocusRow(vm, OverlayRow.Episodes);
            h.Press(RemoteKey.Select);
            Assert.Equal(PlayerPanel.Episodes, vm.Panel);
            Check(h, "episodes panel");
        }

        [Fact]
        public void PlayerStatusSeekAndCredits()
        {
            var h = Harness();
            var vm = h.StartEpisode(1_150_000);
            h.Player.Snapshot.DurationMs = 1_200_000;   // the binge card counts down to the hand-over, 30 s before the end
            h.Player.Snapshot.Binge = true;
            h.Shell.Tick();
            Assert.True(vm.CreditsCardVisible);
            Check(h, "credits");
            h.Press(RemoteKey.Back);
            h.Press(RemoteKey.Left);
            Check(h, "seek indicator");
            h.Player.Snapshot.State = PlayerState.Buffering;
            h.Player.Snapshot.BufferingPercent = 42;
            h.Shell.Tick();
            Check(h, "buffering");
            h.Player.Snapshot.StatusText = "Finding sources for this episode…";
            h.Press(RemoteKey.Select);
            h.FocusRow(vm, OverlayRow.More);
            h.Press(RemoteKey.Select);
            Check(h, "status beside the settings panel");
        }
    }
}
