using System;
using System.Collections.Generic;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Netflix-style pop-out card on Home rows: focus rests ~1.2 s → the title grows in place with its trailer on top
    /// and Play / My List / Rate / More info plus facts and genres below; any key shrinks it at once.
    public class PopCardTests
    {
        private static (UiHarness H, HomeViewModel Home) Started(bool interacted = true)
        {
            var h = LayoutGridTests.FullHome();
            h.Shell.Services.CatalogRows = new StaticCatalogRows(new[]
            {
                new CatalogRowSpec { AddonId = "com.linvo.cinemeta", CatalogId = "top", Type = MediaType.Movie, Title = "Popular movies" },
                new CatalogRowSpec { AddonId = "com.linvo.cinemeta", CatalogId = "year", Type = MediaType.Series, Title = "New series" }
            });
            var home = new HomeViewModel();
            h.Shell.Start(home);
            if (interacted)
            {
                h.Shell.Services.LastInteractionMs = 0;
                h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;   // as if the viewer just navigated
            }
            return (h, home);
        }

        private static (UiHarness H, HomeViewModel Home) Popped()
        {
            var (h, home) = Started();
            h.Wait(h.Shell.Services.CardPopDelayMs + 200);
            Assert.NotNull(home.PoppedTile);
            return (h, home);
        }

        private static UiNode Card(UiFrame f, HomeViewModel home) =>
            f.Nodes.Single(n => n.Key == "row:" + home.PoppedRowKey + ":" + home.FocusedColumn(home.FocusedRow) + ":ph");

        private static IEnumerable<UiNode> CardNodes(UiFrame f, HomeViewModel home)
        {
            var prefix = "row:" + home.PoppedRowKey + ":" + home.FocusedColumn(home.FocusedRow) + ":";
            return f.Nodes.Where(n => n.Key.StartsWith(prefix, StringComparison.Ordinal) && !n.Key.EndsWith(":rank", StringComparison.Ordinal));
        }

        [Fact]
        public void PopsAfterTheDwellWithItsTrailerAndOnlyOneTrailer()
        {
            var (h, home) = Started();
            var before = UiLayout.Build(h.Shell);
            h.Wait(h.Shell.Services.CardPopDelayMs - 300);
            Assert.Null(home.PoppedTile);
            h.Wait(400);
            Assert.Equal("tt1727587", home.PoppedTile?.Id);   // Sintel: the most recent Continue watching card
            Assert.False(home.Immersive);   // the billboard's trailer view never starts with cards on
            Assert.Null(home.TrailerId);
            var frame = UiLayout.Build(h.Shell);
            var trailer = Assert.Single(frame.Nodes, n => n.Kind == UiNodeKind.Trailer);
            Assert.Equal("tt1727587", trailer.TitleId);   // IMDb first; the YouTube id is only the fallback
            Assert.Equal("sintel-trailer", trailer.VideoId);
            Assert.False(trailer.Muted);
            Assert.Equal(0, trailer.ExitMs);
            var card = Card(frame, home);
            Assert.Equal(card.X, trailer.X);
            Assert.Equal(card.Y, trailer.Y);
            Assert.Equal(card.W, trailer.W);
            Assert.InRange(trailer.W * 9 / 16 - trailer.H, -1, 1);   // 16:9 video area on top of the card
            // The tile's own picture grows into the video area: same key, same image (no reload).
            var small = before.Nodes.Single(n => n.Key == "row:continue:0:art");
            var big = frame.Nodes.Single(n => n.Key == "row:continue:0:art");
            Assert.Equal(1, frame.Nodes.Count(n => n.Key == "row:continue:0:art"));
            Assert.Equal(small.ImageUrl, big.ImageUrl);
            Assert.InRange(big.W / (double)small.W, 1.5, 1.8);
            // On top of everything, the trailer under the logo and panel.
            var trailerAt = frame.Nodes.IndexOf(trailer);
            Assert.True(frame.Nodes.FindIndex(n => n.Key == "home:band") < frame.Nodes.IndexOf(card));
            Assert.True(frame.Nodes.IndexOf(big) < trailerAt);
            Assert.True(frame.Nodes.FindIndex(n => n.Key == "row:continue:0:card:play") > trailerAt);
            // Facts and genres from the title's meta.
            var facts = frame.Nodes.Single(n => n.Key.EndsWith(":card:facts", StringComparison.Ordinal)).Text;
            Assert.Matches(@"^Movie  ·  \d+m left  ·  ★ 7\.4$", facts);
            Assert.Equal("Animation", frame.Nodes.Single(n => n.Key.EndsWith(":card:tags", StringComparison.Ordinal)).Text);

            h.Press(RemoteKey.Right);
            h.Wait(h.Shell.Services.CardPopDelayMs);
            frame = UiLayout.Build(h.Shell);
            Assert.Matches(@"^S1:E2  ·  \d+m left  ·  ★ 9\.5$", frame.Nodes.Single(n => n.Key.EndsWith(":card:facts", StringComparison.Ordinal)).Text);
            Assert.Equal("Crime  ·  Drama  ·  Thriller", frame.Nodes.Single(n => n.Key.EndsWith(":card:tags", StringComparison.Ordinal)).Text);
        }

        [Fact]
        public void NeverPopsOnLaunchOrIdle()
        {
            var (h, home) = Started(interacted: false);
            h.Wait(6000);
            Assert.Null(home.PoppedTile);
            h.Press(RemoteKey.Left);   // any remote input (focus stays on the first card)
            h.Wait(h.Shell.Services.CardPopDelayMs);
            Assert.NotNull(home.PoppedTile);
        }

        [Fact]
        public void AnyNavigationShrinksAtOnceAndRestartsTheDwell()
        {
            var (h, home) = Popped();
            h.Shell.HandleKey(RemoteKey.Right);
            Assert.Null(home.PoppedTile);
            Assert.False(home.CardTrailerPlaying);
            var frame = UiLayout.Build(h.Shell);
            Assert.DoesNotContain(frame.Nodes, n => n.Kind == UiNodeKind.Trailer);
            Assert.DoesNotContain(frame.Nodes, n => n.Key.Contains(":card:"));
            Assert.Equal(1, home.FocusedColumn(home.FocusedRow));   // the key still moved focus
            h.Wait(h.Shell.Services.CardPopDelayMs - 200);
            Assert.Null(home.PoppedTile);
            h.Wait(400);
            Assert.Equal("tt0903747", home.PoppedTile?.Id);   // the next title pops after its own dwell

            // A key that doesn't move focus (Left on the first tile) also shrinks and restarts the dwell.
            h.Shell.HandleKey(RemoteKey.Left);
            h.Wait(h.Shell.Services.CardPopDelayMs + 200);
            Assert.Equal("tt1727587", home.PoppedTile?.Id);
            h.Shell.HandleKey(RemoteKey.Left);
            Assert.Null(home.PoppedTile);
            h.Wait(h.Shell.Services.CardPopDelayMs - 200);
            Assert.Null(home.PoppedTile);
        }

        [Fact]
        public void BackOnlyShrinksAndTheCardWaitsForFocusToMove()
        {
            var (h, home) = Popped();
            h.Press(RemoteKey.Back);
            Assert.Same(home, h.Shell.Current);
            Assert.Null(home.PoppedTile);
            Assert.Equal("continue", home.FocusedRow.Key);
            h.Wait(4000);
            Assert.Null(home.PoppedTile);
            h.Press(RemoteKey.Right);
            h.Wait(h.Shell.Services.CardPopDelayMs);
            Assert.NotNull(home.PoppedTile);
        }

        [Fact]
        public void OkPlaysInfoOpensTheTitlePageAndPlayPausePlays()
        {
            var (h, home) = Popped();
            Assert.False(home.TrailerMuted);
            h.Press(RemoteKey.PlayPause);   // always plays the focused title, even while its preview is sounding
            Assert.Contains(h.Player.Commands, c => c.StartsWith("play-request", StringComparison.Ordinal));

            (h, home) = Popped();
            h.Press(RemoteKey.Select);
            Assert.Contains(h.Player.Commands, c => c.StartsWith("play-request", StringComparison.Ordinal));

            (h, home) = Popped();
            h.Press(RemoteKey.Info);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
        }

        [Fact]
        public void DownEntersTheButtonRowAndEveryButtonIsReachableWithArrows()
        {
            var (h, home) = Popped();
            Assert.Null(home.CardButton);
            Assert.Equal(new[] { PopButton.Play, PopButton.MyList, PopButton.Info }, home.CardButtons.ToArray());
            h.Press(RemoteKey.Down);
            Assert.NotNull(home.PoppedTile);                    // Down steps into the card instead of leaving it
            Assert.Equal("continue", home.FocusedRow.Key);
            Assert.Equal(PopButton.Play, home.CardButton);
            h.Press(RemoteKey.Right);
            Assert.Equal(PopButton.MyList, home.CardButton);
            Assert.NotNull(home.PoppedTile);                    // Right no longer makes the card vanish
            var filled = UiLayout.Build(h.Shell).Nodes.Where(n => n.Key.Contains(":card:") && n.Fill == UiTheme.FocusFill && n.W == UiLayout.CardButton).ToList();
            Assert.EndsWith(":card:list", Assert.Single(filled).Key);   // one focus style: the focused button is filled
            h.Press(RemoteKey.Right, RemoteKey.Right);
            Assert.Equal(PopButton.Info, home.CardButton);
            h.Press(RemoteKey.Up);
            Assert.Null(home.CardButton);                       // back on the card itself
            h.Press(RemoteKey.Down, RemoteKey.Down);            // through the row and on to the next row
            Assert.Null(home.PoppedTile);
            var next = home.Rows.Where(r => !r.IsMenu).SkipWhile(r => r.Key != "continue").Skip(1).First();
            Assert.Equal(next.Key, home.FocusedRow.Key);
        }

        [Fact]
        public void CardButtonsActivateWithOk()
        {
            var (h, home) = Popped();
            h.Press(RemoteKey.Down, RemoteKey.Right, RemoteKey.Right, RemoteKey.Select);   // More info
            Assert.IsType<DetailsViewModel>(h.Shell.Current);

            (h, home) = Popped();
            var id = home.PoppedTile.Id;
            var was = home.InLibrary(id);
            h.Press(RemoteKey.Down, RemoteKey.Right, RemoteKey.Select);                    // My List
            Assert.NotEqual(was, home.InLibrary(id));
            Assert.Same(home, h.Shell.Current);
        }

        [Fact]
        public void OkOpensTheTitlePageOnEveryCardExceptContinueWatching()
        {
            var (h, home) = Started();
            h.Press(RemoteKey.Down);
            Assert.NotEqual("continue", home.FocusedRow.Key);
            h.Shell.Services.LastInteractionMs = h.Shell.NowMs;
            h.Wait(h.Shell.Services.CardPopDelayMs + 200);
            Assert.NotNull(home.PoppedTile);
            // The filled button is what OK does: More info here, Play on a Continue watching card.
            var filled = UiLayout.Build(h.Shell).Nodes.Where(n => n.Key.Contains(":card:") && n.Fill == UiTheme.FocusFill && n.W == UiLayout.CardButton).ToList();
            Assert.EndsWith(":card:info", Assert.Single(filled).Key);
            h.Press(RemoteKey.Select);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
            Assert.DoesNotContain(h.Player.Commands, c => c.StartsWith("play-request", StringComparison.Ordinal));
        }

        [Fact]
        public void PoppedCardNeverSlicesTheBillboardTextOrItsRowTitle()
        {
            var (h, home) = Started();
            h.Wait(600);
            Assert.Contains(UiLayout.Build(h.Shell).Nodes, n => n.Key == "hero:description");
            h.Shell.Services.LastInteractionMs = h.Shell.NowMs;
            h.Wait(h.Shell.Services.CardPopDelayMs + 200);
            var frame = UiLayout.Build(h.Shell);
            var card = Card(frame, home);
            Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("hero:description", StringComparison.Ordinal) || n.Key.StartsWith("hero:facts", StringComparison.Ordinal) || n.Key.StartsWith("hero:logo", StringComparison.Ordinal));
            Assert.Contains(frame.Nodes, n => n.Key.StartsWith("hero:bg:", StringComparison.Ordinal));   // the backdrop stays
            var title = frame.Nodes.Single(n => n.Key == "row:" + home.PoppedRowKey + ":title");
            Assert.True(title.Y + title.H <= card.Y, "the row title sits above the card");
            Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("row:" + home.PoppedRowKey + ":", StringComparison.Ordinal) && n.Key.EndsWith(":caption", StringComparison.Ordinal) && n.Opacity > 0);
        }

        [Fact]
        public void Top10CardCarriesItsRankAndLeavesTheNextNumeralAlone()
        {
            var (h, home) = Started();
            for (var i = 0; i < 12 && home.FocusedRow.Style != TileStyle.Ranked; i++)
                h.Press(RemoteKey.Down);
            h.Shell.HandleKey(RemoteKey.Right);
            h.Shell.HandleKey(RemoteKey.Right);
            h.Shell.Services.LastInteractionMs = h.Shell.NowMs;
            h.Wait(h.Shell.Services.CardPopDelayMs + 200);
            Assert.Equal(3, home.PoppedTile.Rank);
            var frame = UiLayout.Build(h.Shell);
            var card = Card(frame, home);
            Assert.Contains(frame.Nodes, n => n.Key.EndsWith(":card:rank", StringComparison.Ordinal) && n.Text.StartsWith("#3 in ", StringComparison.Ordinal));
            var column = home.FocusedColumn(home.FocusedRow);
            var next = frame.Nodes.Single(n => n.Key == "row:" + home.PoppedRowKey + ":" + (column + 1) + ":rank");
            Assert.True(card.X + card.W <= next.X + next.W / 2, "the card covers less than half of the next numeral's box");
        }

        [Fact]
        public void BackFromTheTitlePageDoesNotPopAgainUntilTheViewerNavigates()
        {
            var (h, home) = Popped();
            h.Press(RemoteKey.Info);
            Assert.IsType<DetailsViewModel>(h.Shell.Current);
            h.Press(RemoteKey.Back);
            Assert.Same(home, h.Shell.Current);
            h.Wait(4000);
            Assert.Null(home.PoppedTile);
            h.Press(RemoteKey.Right);
            h.Wait(h.Shell.Services.CardPopDelayMs);
            Assert.NotNull(home.PoppedTile);
        }

        [Fact]
        public void TrailerPlaysOnceAndFollowsPreviewSettings()
        {
            var (h, home) = Popped();
            h.Shell.Services.ReportTrailerEnded(home.CardTrailerVideoId);
            h.Wait(200);
            Assert.NotNull(home.PoppedTile);   // the card stays, with its still art
            Assert.DoesNotContain(UiLayout.Build(h.Shell).Nodes, n => n.Kind == UiNodeKind.Trailer);
            h.Wait(5000);
            Assert.False(home.CardTrailerPlaying);   // played once

            var settings = h.Shell.Services.Player.Settings;
            settings.SetPreviewSound(false);
            h.Press(RemoteKey.Right, RemoteKey.Left);
            h.Wait(h.Shell.Services.CardPopDelayMs + 200);
            Assert.True(home.CardTrailerPlaying);
            Assert.True(home.TrailerMuted);   // Preview sound off: starts muted

            settings.SetAutoplayPreviews(false);
            h.Press(RemoteKey.Right, RemoteKey.Left);
            h.Wait(h.Shell.Services.CardPopDelayMs + 2000);
            Assert.NotNull(home.PoppedTile);         // the card still pops
            Assert.False(home.CardTrailerPlaying);   // but no trailer
        }

        [Fact]
        public void PopDelayIsASetting()
        {
            var (h, home) = Started();
            h.Shell.Services.CardPopDelayMs = 1500;
            h.Wait(1300);
            Assert.Null(home.PoppedTile);
            h.Wait(300);
            Assert.NotNull(home.PoppedTile);
        }

        /// Every row, first and last tile: the card stays inside the TV safe area, grows inward at the edges, covers no
        /// row title above its own row, and its text is at least the 10-foot minimum.
        [Fact]
        public void CardFitsTheSafeAreaAtBothEdgesOfEveryRow()
        {
            var (h, home) = Started();
            h.Wait(500);
            var rows = home.Rows.Where(r => !r.IsMenu).ToList();
            Assert.Contains(rows, r => r.Style == TileStyle.Ranked);
            Assert.Contains(rows, r => r.Style == TileStyle.Portrait);
            var checkedCards = 0;
            for (var r = 0; r < rows.Count; r++)
            {
                if (r > 0)
                {
                    if (home.PoppedTile != null)
                        h.Press(RemoteKey.Back);   // Down on a popped card would step into its buttons
                    h.Press(RemoteKey.Down);
                }
                foreach (var end in new[] { false, true })
                {
                    var row = home.FocusedRow;
                    var target = end ? row.Items.Count - 1 : 0;
                    while (home.FocusedColumn(row) != target)
                        h.Shell.HandleKey(target > 0 ? RemoteKey.Right : RemoteKey.Left);
                    h.Shell.Services.LastInteractionMs = h.Shell.NowMs;
                    h.Wait(h.Shell.Services.CardPopDelayMs + 200);
                    Assert.NotNull(home.PoppedTile);
                    var frame = UiLayout.Build(h.Shell);
                    var card = Card(frame, home);
                    Assert.InRange(card.X, UiScale.SafeX, UiScale.Right - card.W);
                    Assert.InRange(card.Y, UiScale.SafeTop, UiScale.Bottom - card.H);
                    var nodes = CardNodes(frame, home).ToList();
                    Assert.All(nodes.Where(n => n.Kind != UiNodeKind.Trailer && !n.Key.EndsWith(":shadow", StringComparison.Ordinal) && !n.Key.EndsWith(":ring", StringComparison.Ordinal)), n =>
                        Assert.True(n.X >= card.X && n.X + n.W <= card.X + card.W && n.Y >= card.Y && n.Y + n.H <= card.Y + card.H, n.Key + " leaves the card"));
                    Assert.All(nodes.Where(n => n.Kind == UiNodeKind.Text && n.Text.Length > 0), n => Assert.True(n.FontSize >= UiScale.Hint, n.Key));
                    var width = card.W / (double)(row.Style == TileStyle.Landscape ? UiScale.LandW : UiScale.PosterW);
                    Assert.InRange(width, 1.5, 1.81);   // the user's 1.5-1.8x
                    // Row titles above the focused row stay clear (the card may cover its own row's title near the bottom).
                    var focusedTitle = frame.Nodes.Single(n => n.Key == "row:" + row.Key + ":title");
                    foreach (var title in frame.Nodes.Where(n => n.Key.StartsWith("row:", StringComparison.Ordinal) && n.Key.EndsWith(":title", StringComparison.Ordinal) && n.Y < focusedTitle.Y))
                        Assert.False(title.Y + title.H > card.Y && title.Y < card.Y + card.H && title.X < card.X + card.W, title.Key + " under the card");
                    // The card never reaches into the menu bar.
                    Assert.DoesNotContain(frame.Nodes, n => n.Key.StartsWith("menu:", StringComparison.Ordinal) && n.Y + n.H > card.Y);
                    Assert.True(frame.Nodes.Count(n => n.Kind == UiNodeKind.Trailer) <= 1);
                    checkedCards++;
                }
            }
            Assert.True(checkedCards >= 8);
        }

        [Fact]
        public void EdgeTilesGrowInward()
        {
            var first = UiLayout.PopCardRectFor(TileStyle.Landscape, UiScale.SafeX, 700, UiScale.LandW, UiScale.LandH);
            Assert.Equal(UiScale.SafeX, first.X);
            var last = UiLayout.PopCardRectFor(TileStyle.Landscape, UiScale.Right - UiScale.LandW, 700, UiScale.LandW, UiScale.LandH);
            Assert.Equal(UiScale.Right, last.Right);
            var middle = UiLayout.PopCardRectFor(TileStyle.Portrait, 800, 500, UiScale.PosterW, UiScale.PosterH);
            Assert.InRange(middle.X + middle.W / 2 - (800 + UiScale.PosterW / 2), -1, 1);   // grows around its centre
            Assert.True(middle.Bottom <= UiScale.Bottom);
        }

        [Fact]
        public void CardsOffKeepTheBillboardTrailerView()
        {
            var (h, home) = Started();
            h.Shell.Services.TrailerCards = false;
            h.Wait(HomeViewModel.TrailerDelayMs + 500);
            Assert.Null(home.PoppedTile);
            Assert.True(home.Immersive);
        }

        [Fact]
        public void SeasonsComeFromTheMetaVideos()
        {
            var meta = Newtonsoft.Json.Linq.JObject.Parse(@"{ ""id"": ""tt1"", ""type"": ""series"", ""videos"": [
                { ""id"": ""tt1:0:1"", ""season"": 0, ""episode"": 1 }, { ""id"": ""tt1:1:1"", ""season"": 1, ""episode"": 1 },
                { ""id"": ""tt1:1:2"", ""season"": 1, ""episode"": 2 }, { ""id"": ""tt1:2:1"", ""season"": 2, ""episode"": 1 } ] }");
            var extras = CinemetaJson.ParseExtras(meta);
            Assert.Equal(2, extras.Seasons);
            var tile = new Tile { Id = "tt1", Type = MediaType.Series, Title = "X" };
            Assert.Equal("Series  ·  2 Seasons", UiLayout.CardFacts(tile, extras));
            Assert.Equal("Movie  ·  49 min  ·  ★ 8.1", UiLayout.CardFacts(new Tile { Id = "tt2", Type = MediaType.Movie }, new MetaExtras { Runtime = "49 min", Rating = "8.1" }));
        }
    }
}
