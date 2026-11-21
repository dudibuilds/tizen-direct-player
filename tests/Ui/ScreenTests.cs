using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class ScreenTests
    {
        [Fact]
        public void HomeShowsContinueWatchingAndOpensDetails()
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series, Modified = h.Clock.UtcNow,
                State = new LibraryState { VideoId = "tt0903747:1:2", TimeOffsetMs = 600_000, DurationMs = 2_800_000, LastWatched = h.Clock.UtcNow }
            };
            h.Shell.Start(new HomeViewModel());
            var text = h.Text;
            Assert.Contains("> Continue watching: >Breaking Bad (21%)", text);
            Assert.Contains("Your library", text);
            Assert.Contains("Top 10 movies today", text);
            // One press to continue: center resumes the episode straight away.
            h.Press(RemoteKey.Select);
            Assert.IsType<PlayerViewModel>(h.Shell.Current);
            Assert.Equal(600_000, h.Player.LastRequest.ResumeMs);
            Assert.Equal("tt0903747:1:2", h.Player.LastRequest.Video.Id);
            h.Press(RemoteKey.Stop);
            // Info opens the title page.
            h.Press(RemoteKey.Info);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
        }

        [Fact]
        public void HomeMenuReachesSearchAndBackAtRootAsksBeforeLeaving()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Press(RemoteKey.Up, RemoteKey.Select);
            Assert.IsType<SearchViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Back);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
            h.Clock.Advance(1000);
            Assert.True(h.Shell.HandleKey(RemoteKey.Back));          // asks first
            Assert.Equal(TvShell.ExitHint, h.Shell.Toast);
            h.Clock.Advance(800);
            Assert.False(h.Shell.HandleKey(RemoteKey.Back));         // a second Back leaves (the host exits)
            Assert.Null(h.Shell.Toast);
        }

        [Fact]
        public void LeavingFromHomeNeedsTwoDeliberateBacks()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Clock.Advance(1000);
            Assert.True(h.Shell.HandleKey(RemoteKey.Back));
            // A held Back (auto-repeat) never confirms.
            h.Clock.Advance(100);
            Assert.True(h.Shell.HandleKey(RemoteKey.Back));
            h.Clock.Advance(100);
            Assert.True(h.Shell.HandleKey(RemoteKey.Back));
            // Too late: the hint has lapsed and Back asks again.
            h.Clock.Advance(TvShell.ExitConfirmMs + 500);
            Assert.True(h.Shell.HandleKey(RemoteKey.Back));
            Assert.Equal(TvShell.ExitHint, h.Shell.Toast);
            // Any other key in between cancels it.
            h.Clock.Advance(600);
            h.Shell.HandleKey(RemoteKey.Down);
            h.Clock.Advance(600);
            h.Shell.HandleKey(RemoteKey.Up);
            h.Clock.Advance(600);
            Assert.True(h.Shell.HandleKey(RemoteKey.Back));
            h.Clock.Advance(600);
            Assert.False(h.Shell.HandleKey(RemoteKey.Back));
        }

        [Fact]
        public void NewEpisodesListsReleasedUnwatchedAfterLastWatched()
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry
            {
                Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series,
                State = new LibraryState { LastWatched = new DateTimeOffset(2008, 2, 1, 0, 0, 0, TimeSpan.Zero) }
            };
            var tiles = HomeViewModel.FindNewEpisodesAsync(h.Catalog, h.Library, h.Library.Entries.Values.ToList(), h.Clock.UtcNow, CancellationToken.None).Result;
            var tile = Assert.Single(tiles);
            Assert.Equal("S2:E1 · 2 new episodes", tile.Subtitle);
        }

        [Fact]
        public void SearchTypesWithKeyboardDebouncesAndOpensResult()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            var search = new SearchViewModel();
            h.Shell.Push(search);
            // Typing fast: only the final query is searched once the 300 ms debounce settles.
            foreach (var c in "br")
            {
                Assert.True(search.Keyboard.FocusLabel(c.ToString()));
                h.Shell.HandleKey(RemoteKey.Select);
                h.Clock.Advance(100);
                h.Shell.Tick();
            }
            Assert.Empty(h.Catalog.Searches);
            h.Wait(400);
            Assert.Equal(new[] { "br" }, h.Catalog.Searches);
            Assert.Contains("Breaking Bad", h.Text);
            Assert.Contains("Refine: ", h.Text);
            Assert.DoesNotContain("Refine: Breaking Bad", h.Text);
            // Typing more updates results as you go.
            Type(h, search.Keyboard, "e");
            Assert.Equal("bre", h.Catalog.Searches.Last());
            // Right off the keyboard reaches the first poster; the refinement chips are one Up from it.
            for (var i = 0; i < 8 && search.Focus == SearchFocus.Keyboard; i++)
                h.Press(RemoteKey.Right);
            Assert.Equal(SearchFocus.Results, search.Focus);
            h.Press(RemoteKey.Up);
            Assert.Equal(SearchFocus.Suggestions, search.Focus);
            h.Press(RemoteKey.Down, RemoteKey.Select);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
            // Back steps one level at a time: title page, results, keyboard (query kept), Home.
            h.Press(RemoteKey.Back);
            Assert.Same(search, h.Shell.Current);
            Assert.Equal(SearchFocus.Results, search.Focus);
            h.Press(RemoteKey.Back);
            Assert.Same(search, h.Shell.Current);
            Assert.Equal(SearchFocus.Keyboard, search.Focus);
            Assert.Equal("bre", search.Query);
            h.Press(RemoteKey.Back);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
            // Reopened in the same session: the query and its results are back, and it is offered as a recent search.
            var again = new SearchViewModel();
            h.Shell.Push(again);
            h.Wait(100);
            Assert.Equal("bre", again.Query);
            Assert.NotEmpty(again.Items);
            Assert.Contains("bre", h.Shell.Services.Memory.RecentSearches);
        }

        [Fact]
        public void UrlKeyboardHasUppercaseAndSymbols()
        {
            var kb = new OnScreenKeyboard(true, "Install");
            Assert.True(kb.FocusLabel("https://"));
            kb.Press();
            Assert.True(kb.FocusLabel("abc"));
            kb.Press();   // shift
            Assert.True(kb.FocusLabel("Q"));
            kb.Press();
            Assert.True(kb.FocusLabel("?#&"));
            kb.Press();
            foreach (var s in new[] { "|", "%", "=", "&" })
            {
                if (!kb.FocusLabel(s))
                {
                    Assert.True(kb.FocusLabel("abc") || kb.FocusLabel("?#&"));
                    kb.Press();
                    Assert.True(kb.FocusLabel(s), s);
                }
                kb.Press();
            }
            Assert.Equal("https://Q|%=&", kb.Text);
        }

        [Fact]
        public void KeyboardLayoutSwitchKeepsFocusUsableWithArrows()
        {
            var kb = new OnScreenKeyboard(true, "Install");
            Assert.True(kb.FocusLabel("?#&"));
            kb.Press();                       // to symbols (fewer rows)
            Assert.Equal("abc", kb.Focused.Label);
            kb.Move(0, -1);
            kb.Move(-1, 0);
            kb.Move(1, 0);
            Assert.Equal(KeyAction.Insert, kb.Focused.Action);
            kb.Move(0, 1);
            kb.Press();                       // back to letters
            Assert.Equal(KeyboardLayout.English, kb.Layout);
            Assert.Equal("?#&", kb.Focused.Label);
            for (var i = 0; i < 10; i++)
                kb.Move(0, -1);
            Assert.Equal(0, kb.Row);
            Assert.True(kb.FocusLabel("abc"));
            kb.Press();                       // shift
            Assert.Equal(KeyAction.Shift, kb.Focused.Action);
        }

        [Fact]
        public void LongSubtitleCueGrowsUpward()
        {
            var h = new UiHarness();
            var vm = h.StartEpisode();
            h.Player.Settings.UpdateSubtitleStyle(s => s.Size = SubtitleStyle.MaxSize);
            h.Player.Snapshot.SubtitleText = "English subtitles. Use the player controls to change language, size or timing.";
            var node = UiLayout.Build(h.Shell).Nodes.Single(n => n.Key == "subtitle");
            Assert.True(node.H >= 2 * SubtitleStyle.MaxSize * 1.3);
            Assert.Equal(UiFrame.Height - vm.SubtitleBottom, node.Y + node.H);
        }

        [Fact]
        public void AddonsInstallByUrlAndRemoveWithConfirmation()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            h.Shell.Push(new AddonsViewModel());
            Assert.Contains("Torrentio", h.Text);
            h.Press(RemoteKey.Select);
            var entry = Assert.IsType<TextEntryViewModel>(h.Shell.Current);
            Type(h, entry.Keyboard, "x.io");
            entry.Keyboard.FocusLabel("Install");
            h.Press(RemoteKey.Select);
            Assert.IsType<AddonsViewModel>(h.Shell.Current);
            Assert.Equal("https://x.io", h.Addons.Installed.Last().TransportUrl);
            Assert.Contains("[toast] Installed Custom", h.Text);
            h.Press(RemoteKey.Down, RemoteKey.Select);
            Assert.NotNull(h.Shell.Dialog);
            h.Press(RemoteKey.Right, RemoteKey.Select);
            Assert.DoesNotContain(h.Addons.Installed, a => a.Name == "Torrentio");
        }

        [Fact]
        public void SignInShowsCodeAndPollsUntilApproved()
        {
            var h = new UiHarness();
            h.Account.IsSignedIn = false;
            h.Shell.Start(new HomeViewModel());
            Assert.Contains("Sign in to Stremio", h.Text);
            h.Shell.Push(new SignInViewModel());
            Assert.Contains("enter ABCD", h.Text);
            h.Wait(3100);
            Assert.Equal(1, h.Account.Polls);
            Assert.IsType<SignInViewModel>(h.Shell.Current);
            h.Wait(3100);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
            Assert.Contains("[toast] Signed in", h.Text);
            Assert.DoesNotContain("Sign in to Stremio", h.Text);
        }

        [Fact]
        public void DetailsEpisodeColumnsMarkWatchedAndQueue()
        {
            var h = new UiHarness();
            h.Library.Entries["tt0903747"] = new LibraryEntry { Id = "tt0903747", Name = "Breaking Bad", Type = MediaType.Series };
            h.Shell.Start(new HomeViewModel());
            h.Shell.Push(new DetailsViewModel(MediaType.Series, "tt0903747"));
            Assert.Contains(">[Play S1:E1]", h.Text);
            Assert.Contains("[In my library]", h.Text);
            h.Press(RemoteKey.Down, RemoteKey.Down);   // seasons, then episodes
            h.Press(RemoteKey.Right, RemoteKey.Select);
            Assert.Contains("tt0903747:1:1", h.Library.WatchedIds);
            Assert.Contains("> [x] S1E1", h.Text);
            Assert.Contains("[Play next: S1:E2]", h.Text);
            h.Press(RemoteKey.Right, RemoteKey.Select);
            Assert.Single(h.Queue.State.Items);
            h.Press(RemoteKey.Up, RemoteKey.Right);   // seasons row, season 2
            Assert.Contains("S2E1", h.Text);
        }

        [Fact]
        public void SourcePickerStartsPlaybackWithChosenSource()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            var details = new DetailsViewModel(MediaType.Movie, "tt1727587");
            h.Shell.Push(details);
            while (details.Actions[details.ActionIndex].Action != DetailsAction.More)
                h.Press(RemoteKey.Right);
            h.Press(RemoteKey.Select);                        // More: Choose a source is its first entry
            Assert.Contains("[dialog] More: >[Choose a source]", h.Text);
            h.Press(RemoteKey.Select);
            Assert.IsType<SourcePickerViewModel>(h.Shell.Current);
            Assert.Contains("> Torrentio 1080p · 1080p · 2.1 GB · EN audio · ES subs · 120 seeders", h.Text);
            h.Press(RemoteKey.Select);
            Assert.IsType<PlayerViewModel>(h.Shell.Current);
            Assert.Equal("AAAA", h.Player.LastRequest.Source.InfoHash);
            h.Press(RemoteKey.Stop);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
        }

        [Fact]
        public void LoadCompletingAfterBackIsDropped()
        {
            var h = new UiHarness();
            h.Catalog.MetaGate = new TaskCompletionSource<MetaDetail>();
            h.Shell.Start(new HomeViewModel());
            var details = new DetailsViewModel(MediaType.Series, "tt0903747");
            h.Shell.Push(details);
            h.Press(RemoteKey.Back);
            var version = h.Shell.Version;
            h.Catalog.MetaGate.SetResult(UiHarness.Series);
            Assert.Null(details.Meta);
            Assert.Equal(version, h.Shell.Version);
            Assert.IsType<HomeViewModel>(h.Shell.Current);
        }

        [Fact]
        public void QueueStartsAndShowsPlayer()
        {
            var h = new UiHarness();
            h.Queue.AddAsync(UiHarness.Series, UiHarness.Series.Videos[0], CancellationToken.None).Wait();
            h.Shell.Start(new HomeViewModel());
            h.Shell.Push(new QueueViewModel());
            Assert.Contains(">[Start queue]", h.Text);
            h.Press(RemoteKey.Select);
            Assert.IsType<PlayerViewModel>(h.Shell.Current);
        }

        [Fact]
        public void PlayerSettingsPersistAndReload()
        {
            var store = new MemoryStore();
            var settings = new PlayerSettings(store);
            settings.UpdateSubtitleStyle(s => { s.Bold = true; s.TextColor = "#5EE7F2"; s.Size = 999; });
            settings.RememberTracks("tt1", "jpn", "eng");
            settings.FlushAsync().Wait();
            var reloaded = new PlayerSettings(store);
            reloaded.LoadAsync(CancellationToken.None).Wait();
            Assert.True(reloaded.SubtitleStyle.Bold);
            Assert.Equal("Cyan", reloaded.SubtitleStyle.ColorName);
            Assert.Equal(SubtitleStyle.MaxSize, reloaded.SubtitleStyle.Size);
            Assert.Equal("jpn", reloaded.GetTrackPreference("tt1", false).AudioLanguage);
            Assert.Equal("jpn", reloaded.GetTrackPreference("tt2", true).AudioLanguage);
            Assert.Equal("spa", reloaded.GetTrackPreference("tt2", false).SubtitleLanguage);
        }

        [Fact]
        public void PosterCacheDownloadsOnceAndEvictsLeastRecentlyUsed()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-poster-test-" + Guid.NewGuid().ToString("N"));
            var handler = new CountingHandler();
            try
            {
                var cache = new PosterCache(dir, handler, maxBytes: 2500, maxFiles: 10);
                var a = cache.GetAsync("https://img/a.jpg", CancellationToken.None).Result;
                Assert.Equal(a, cache.GetAsync("https://img/a.jpg", CancellationToken.None).Result);
                Assert.Equal(1, handler.Calls);
                cache.GetAsync("https://img/b.jpg", CancellationToken.None).Wait();
                cache.GetAsync("https://img/c.jpg", CancellationToken.None).Wait();   // 3 x 1000 bytes > 2500
                cache.FlushAsync().Wait();   // eviction runs on a worker
                Assert.False(File.Exists(a));
                Assert.Null(cache.PathFor("https://img/a.jpg"));
                Assert.Equal(2, Directory.GetFiles(dir, "*.jpg").Length);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void PosterCacheMigratesLegacyFilesAndNeverServesUnknownFormats()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-poster-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var handler = new CountingHandler();
            try
            {
                // What the previous build left behind: "<sha1>.img" files, one a real JPEG, one PNG, one junk.
                string Legacy(string url, byte[] bytes)
                {
                    var probe = new PosterCache(Path.Combine(dir, "probe"), handler);
                    var name = Path.GetFileNameWithoutExtension(probe.GetAsync(url, CancellationToken.None).Result);
                    var path = Path.Combine(dir, name + ".img");
                    File.WriteAllBytes(path, bytes);
                    return path;
                }
                var jpeg = Legacy("https://img/a", CountingHandler.Jpeg(64));
                var png = Legacy("https://img/b", new byte[] { 0x89, 0x50, 0x4E, 0x47, 13, 10, 26, 10, 0, 0 });
                var junk = Legacy("https://img/c", System.Text.Encoding.ASCII.GetBytes("<html>not an image</html>"));
                Directory.Delete(Path.Combine(dir, "probe"), true);
                handler.Calls = 0;

                var cache = new PosterCache(dir, handler);
                Assert.Empty(Directory.GetFiles(dir, "*.img"));
                Assert.Equal(2, cache.Migrated);
                Assert.Equal(1, cache.Purged);
                Assert.Equal(Path.ChangeExtension(jpeg, ".jpg"), cache.GetAsync("https://img/a", CancellationToken.None).Result);
                Assert.Equal(Path.ChangeExtension(png, ".png"), cache.PathFor("https://img/b"));
                Assert.Equal(0, handler.Calls);                       // migrated files are served without a download
                Assert.Null(cache.PathFor("https://img/c"));          // junk is gone, not served
                Assert.False(File.Exists(junk));

                // A download in an unknown format is reported as a failure and never cached.
                handler.Body = System.Text.Encoding.ASCII.GetBytes("<svg/>");
                Assert.ThrowsAny<Exception>(() => cache.GetAsync("https://img/d", CancellationToken.None).GetAwaiter().GetResult());
                Assert.Null(cache.PathFor("https://img/d"));
                Assert.Equal(1, cache.Failed);
                Assert.All(Directory.GetFiles(dir), f => Assert.Contains(Path.GetExtension(f), new[] { ".jpg", ".png", ".webp" }));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void PosterCacheWithoutWebPAsksForJpegRetriesAndNeverCachesWebP()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-poster-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var webp = System.Text.Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 payload");
            var handler = new CountingHandler();
            try
            {
                // A WebP cached by an earlier build (as .webp or legacy .img) is purged on startup.
                var stale = new PosterCache(dir, handler).GetAsync("https://img/old", CancellationToken.None).Result;
                File.WriteAllBytes(Path.ChangeExtension(stale, ".webp"), webp);
                File.Delete(stale);
                File.WriteAllBytes(Path.Combine(dir, "legacy.img"), webp);
                handler.Requests.Clear();

                var cache = new PosterCache(dir, handler, webp: false);
                Assert.Empty(Directory.GetFiles(dir, "*.webp"));
                Assert.Empty(Directory.GetFiles(dir, "*.img"));
                Assert.Equal(2, cache.Purged);
                Assert.Null(cache.PathFor("https://img/old"));

                // The CDN ignores Accept on its cached URL and negotiates again when the query changes.
                handler.Respond = r => r.RequestUri.Query.Contains("format=jpg") ? CountingHandler.Jpeg(200) : webp;
                var path = cache.GetAsync("https://img/poster/small/tt1/img", CancellationToken.None).Result;
                Assert.EndsWith(".jpg", path);
                Assert.Equal(path, cache.PathFor("https://img/poster/small/tt1/img"));   // cached under the original URL
                Assert.Equal(new[] { "https://img/poster/small/tt1/img", "https://img/poster/small/tt1/img?format=jpg" }, handler.Requests.Select(r => r.Url).ToArray());
                Assert.All(handler.Requests, r => Assert.StartsWith("image/jpeg", r.Accept));

                // A host that only has WebP: counted as a failure, nothing cached, so the card keeps its text title.
                handler.Respond = _ => webp;
                Assert.ThrowsAny<Exception>(() => cache.GetAsync("https://img/only-webp?x=1", CancellationToken.None).GetAwaiter().GetResult());
                Assert.Equal("https://img/only-webp?x=1&format=jpg", handler.Requests.Last().Url);
                Assert.Null(cache.PathFor("https://img/only-webp?x=1"));
                Assert.Equal(1, cache.Failed);
                Assert.Empty(Directory.GetFiles(dir, "*.webp"));

                // Metahub posters and backgrounds get the hint up front: one request, no WebP download first.
                handler.Requests.Clear();
                handler.Respond = r => r.RequestUri.Query.Contains("format=jpg") ? CountingHandler.Jpeg(200) : webp;
                Assert.EndsWith(".jpg", cache.GetAsync("https://images.metahub.space/poster/small/tt7/img", CancellationToken.None).Result);
                Assert.EndsWith(".jpg", cache.GetAsync("https://images.metahub.space/background/medium/tt7/img", CancellationToken.None).Result);
                Assert.Equal(new[] { "https://images.metahub.space/poster/small/tt7/img?format=jpg", "https://images.metahub.space/background/medium/tt7/img?format=jpg" },
                    handler.Requests.Select(r => r.Url).ToArray());
                Assert.NotNull(cache.PathFor("https://images.metahub.space/poster/small/tt7/img"));
                Assert.True(PosterCache.NeedsFormatHint("https://live.metahub.space/poster/medium/tt7/img"));
                Assert.False(PosterCache.NeedsFormatHint("https://images.metahub.space/logo/medium/tt7/img"));          // logos are PNG
                Assert.False(PosterCache.NeedsFormatHint("https://images.metahub.space/poster/small/tt7/img?format=jpg"));
                Assert.False(PosterCache.NeedsFormatHint("https://image.tmdb.org/t/p/w780/x.jpg"));
                Assert.False(PosterCache.NeedsFormatHint("https://notmetahub.space/poster/a"));
                // A logo is requested as it is.
                handler.Requests.Clear();
                handler.Respond = _ => new byte[] { 0x89, 0x50, 0x4E, 0x47, 13, 10, 26, 10, 0, 0 };
                Assert.EndsWith(".png", cache.GetAsync("https://images.metahub.space/logo/medium/tt7/img", CancellationToken.None).Result);
                Assert.Equal("https://images.metahub.space/logo/medium/tt7/img", handler.Requests.Single().Url);

                // The browser preview keeps WebP and sends no Accept override.
                handler.Respond = _ => webp;
                handler.Requests.Clear();
                var preview = new PosterCache(Path.Combine(dir, "preview"), handler);
                Assert.EndsWith(".webp", preview.GetAsync("https://img/p", CancellationToken.None).Result);
                Assert.Equal("", handler.Requests.Single().Accept);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void HtmlStageRendersEveryScreen()
        {
            var h = new UiHarness();
            h.Shell.Start(new HomeViewModel());
            Assert.Contains("data-key=\"menu:search", UiHtml.RenderStage(UiLayout.Build(h.Shell)));
            var vm = h.StartEpisode();
            h.Press(RemoteKey.Select);
            var html = UiHtml.RenderStage(UiLayout.Build(h.Shell));
            Assert.Contains("Episodes", html);
            Assert.Contains("background:transparent", html);
            var preview = Environment.GetEnvironmentVariable("RU7100_UI_PREVIEW");
            if (!string.IsNullOrEmpty(preview))
                File.WriteAllText(Path.Combine(preview, "player.html"), "<body style=\"margin:0;background:#333\">" + html + "</body>");
        }

        private static void Type(UiHarness h, OnScreenKeyboard kb, string text)
        {
            foreach (var c in text)
            {
                Assert.True(kb.FocusLabel(c.ToString()), "key " + c);
                h.Press(RemoteKey.Select);
            }
        }

        private sealed class CountingHandler : HttpMessageHandler
        {
            public int Calls;
            public byte[] Body = Jpeg(1000);
            public readonly System.Collections.Generic.List<(string Url, string Accept)> Requests = new System.Collections.Generic.List<(string, string)>();
            /// Optional per-request body (CDN behaviour); falls back to Body.
            public Func<HttpRequestMessage, byte[]> Respond;
            public static byte[] Jpeg(int length)
            {
                var bytes = new byte[length];
                bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
                return bytes;
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                lock (Requests) Requests.Add((request.RequestUri.ToString(), request.Headers.Accept.ToString()));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Respond?.Invoke(request) ?? Body) });
            }
        }
    }
}
