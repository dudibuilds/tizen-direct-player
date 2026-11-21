using System;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Where a popped card grows from: the focused tile's picture rect in its row (a Top 10 tile's poster, not its numeral).
    internal sealed class PopAnchor
    {
        public string Key;
        public TileStyle Style;
        public Tile Tile;
        public int X, Y, W, H;
        /// Left edge of the tile's whole slot (a Top 10 tile's numeral included); the card of a ranked tile starts here.
        public int SlotX;
        public string RowTitle;
    }

    /// Popped card geometry: the 16:9 video area on top, the panel under it.
    internal struct PopCardRect
    {
        public int X, Y, W, VideoH, H;
        public int Bottom => Y + H;
        public int Right => X + W;
    }

    public static partial class UiLayout
    {
        // ---- Pop-out trailer card (Home rows, Netflix web hover card) ----

        /// Card width as a multiple of the tile's width. Posters are narrow, so their card grows more.
        internal const double CardScaleLandscape = 1.6;
        internal const double CardScalePoster = 1.8;
        internal const int CardPad = 24;
        internal const int CardButton = 72;           // round buttons (10-foot: the Play target stays big)
        internal const int CardButtonGap = 16;
        internal const int CardGrowMs = 240;
        internal const string CardPanel = UiTheme.Panel;   // raised: clearly lighter than the ink page
        internal const int CardPanelTop = 16;          // video → buttons
        internal const int CardPanelGap = 10;          // buttons → facts
        internal static int CardPanelH => CardPanelTop + CardButton + CardPanelGap + UiScale.LineH(UiScale.Secondary) + 2 + UiScale.LineH(UiScale.Small) + 16;

        /// The card is centred on the tile's picture and grows from it, then pushed inside the safe area: near the
        /// left/right edges it grows inward, and near the bottom (first row on the initial view, the last row) upward
        /// over the row title and the billboard. Rows above and below stay visible around it.
        internal static PopCardRect PopCardRectFor(TileStyle style, int artX, int artY, int artW, int artH, int? slotX = null)
        {
            var w = (int)Math.Round(artW * (style == TileStyle.Landscape ? CardScaleLandscape : CardScalePoster));
            var videoH = w * 9 / 16;
            var h = videoH + CardPanelH;
            // A Top 10 card grows from its own slot (numeral plus poster), so it covers its own numeral, which the
            // card repeats as a badge, and stays clear of the next tile's numeral.
            var x = style == TileStyle.Ranked && slotX != null ? slotX.Value : artX + artW / 2 - w / 2;
            var y = artY + artH / 2 - videoH / 2;
            x = Math.Max(UiScale.SafeX, Math.Min(x, UiScale.Right - w));
            y = Math.Max(UiScale.SafeTop, Math.Min(y, UiScale.Bottom - h));
            return new PopCardRect { X = x, Y = y, W = w, VideoH = videoH, H = h };
        }

        /// The popped card. The tile's own nodes (placeholder, picture, logo, scrim, progress, shadow) keep their keys, so
        /// the painter grows the objects it already has (no reload); the panel fades in and leaves at once on any key.
        /// The trailer node sits after the still art (the TV opens its video hole there and fades the art over it) and
        /// before the scrim, logo and panel, which stay on top of the video.
        private static void PopCard(UiFrame f, HomeViewModel vm, PopAnchor a)
        {
            var tile = a.Tile;
            var key = a.Key;
            var c = PopCardRectFor(a.Style, a.X, a.Y, a.W, a.H, a.SlotX);
            var landscape = a.Style == TileStyle.Landscape;
            var extras = vm.CardExtras;
            var glide = f.Nodes.Count;

            // Focus on the card itself (not yet in its button row): the same white ring as a focused tile.
            if (vm.CardButton == null)
                f.Box(key + ":ring", c.X - UiScale.FocusRing, c.Y - UiScale.FocusRing, c.W + 2 * UiScale.FocusRing, c.H + 2 * UiScale.FocusRing, UiTheme.Focus).R(UiTheme.RadiusCard + UiScale.FocusRing).ExitMs = 150;

            f.Shadow(key + ":shadow", c.X, c.Y, c.W, c.H, UiTheme.RadiusCard);   // soft lift, mostly below
            f.Box(key + ":ph", c.X, c.Y, c.W, c.H, CardPanel).R(UiTheme.RadiusCard);
            Line(f, key + ":alt", c.X + CardPad, c.Y + c.VideoH - UiScale.LineH(UiScale.Heading) - 20, c.W - 2 * CardPad, tile.Title, UiScale.Heading, UiTheme.Text, true);
            if (landscape)
            {
                var cardArt = HomeCardArt(TileStyle.Landscape, tile);
                CardStill(f, key + ":art", c, cardArt, 0.5);
                // The row card's art is card-sized (480x270 art, a w500 still); grown to the popped size it would be
                // soft, so sharper art fades in over it once focus has rested: the title's full backdrop (the
                // billboard shows the same file, so it is already decoded) or the full-size still.
                var sharpUrl = UiFormat.IsCardArt(tile.Image) ? UiFormat.BackgroundFor(tile.Id) : cardArt != (tile.Image ?? tile.Poster) ? tile.Image ?? tile.Poster : null;
                if (sharpUrl != null)
                {
                    var sharp = CardStill(f, key + ":card-art", c, sharpUrl, 0.5);
                    if (sharp != null)
                    {
                        sharp.EnterMs = 300;
                        sharp.ExitMs = 0;
                    }
                }
            }
            else
            {
                // A poster grows into the video area (cropped to its upper part), and the title's backdrop fades in over it.
                CardStill(f, key + ":poster", c, UiFormat.CardPoster(tile.Poster), 0.2);
                var backdrop = CardStill(f, key + ":card-art", c, UiFormat.BackgroundFor(tile.Id), BackdropCropY);
                if (backdrop != null)
                {
                    backdrop.CropX = 0.5;
                    backdrop.EnterMs = 300;
                    backdrop.ExitMs = 0;
                }
            }
            for (var n = glide; n < f.Nodes.Count; n++)
                f.Nodes[n].MoveMs = CardGrowMs;

            if (vm.CardTrailerPlaying && vm.CardTrailerTitleId == tile.Id)
            {
                // Final rect from its first frame and no exit fade: the TV positions its video plane once per trailer
                // and closes the hole the moment the card shrinks.
                var trailer = f.Trailer(key + ":trailer:" + tile.Id, c.X, c.Y, c.W, c.VideoH, vm.CardTrailerVideoId, vm.TrailerMuted, vm.CardTrailerTitleId).R(UiTheme.RadiusCard);
                trailer.MoveMs = 0;
                trailer.ExitMs = 0;
                trailer.EnterMs = 300;
            }

            var over = f.Nodes.Count;
            f.VerticalGradient(key + ":scrim", c.X, c.Y + c.VideoH / 2, c.W, c.VideoH - c.VideoH / 2, "#000000", 0, 200);
            var logoUrl = landscape ? tile.Logo : extras?.Logo ?? UiFormat.LogoFor(tile.Id);
            var logo = f.Image(key + (landscape ? ":logo" : ":card-logo"), c.X + CardPad, c.Y + c.VideoH - 24 - 96, (int)(c.W * 0.5), 96, logoUrl);
            if (logo != null)
            {
                logo.Contain = true;
                logo.AnchorBottom = true;
            }
            if (tile.Progress > 0)
                ProgressBar(f, key + ":progress", c.X + CardPad, c.Y + c.VideoH - 18, c.W - 2 * CardPad, 8, tile.Progress);
            for (var n = over; n < f.Nodes.Count; n++)
            {
                f.Nodes[n].MoveMs = CardGrowMs;
                if (!landscape)
                    f.Nodes[n].ExitMs = 0;
            }

            // Top 10: the card covers its numeral, so it carries the rank itself.
            if (tile.Rank > 0)
                Badge(f, key + ":card:rank", c.X + 12, c.Y + 12, "#" + tile.Rank + (string.IsNullOrEmpty(a.RowTitle) ? "" : " in " + RankScope(a.RowTitle)), UiTheme.Accent, UiTheme.TextOnAccent);

            // Panel: Play, My List, Rate, and More info on the right; then facts and genres. The filled button is the
            // one OK activates now: on the card itself that is what OK does on the tile (resume, or the title page);
            // after Down it is the focused button of this row.
            var panel = f.Nodes.Count;
            var by = c.Y + c.VideoH + CardPanelTop;
            var buttons = vm.CardButtons;
            var active = vm.CardButton ?? HomeViewModel.OkButton(tile);
            var left = c.X + CardPad;
            foreach (var button in buttons)
            {
                var filled = button == active;
                var bx = button == PopButton.Info ? c.Right - CardPad - CardButton : left;
                if (button != PopButton.Info)
                    left += CardButton + CardButtonGap;
                var name = button == PopButton.Play ? "play" : button == PopButton.MyList ? "list" : button == PopButton.Rate ? "rate" : "info";
                // Play is the primary action: amber at rest, and the white ring when it is the button OK activates.
                // Any other active button takes the light focus fill.
                var play = button == PopButton.Play;
                var ink = play ? UiTheme.TextOnAccent : filled ? UiTheme.TextOnFocus : UiTheme.Text;
                if (play)
                {
                    if (filled)
                        f.Box(key + ":card:play:ring", bx - 4, by - 4, CardButton + 8, CardButton + 8, UiTheme.Focus).R(UiTheme.RadiusPill);
                    f.Box(key + ":card:" + name, bx, by, CardButton, CardButton, UiTheme.Accent).R(UiTheme.RadiusPill);
                }
                else if (filled)
                    f.Box(key + ":card:" + name, bx, by, CardButton, CardButton, UiTheme.FocusFill).R(UiTheme.RadiusPill);
                else
                    RoundOutline(f, key + ":card:" + name, bx, by);
                switch (button)
                {
                    case PopButton.Play:
                        f.GlyphIn(key + ":card:play:icon", UiGlyphs.Play, bx + 2, by, CardButton, CardButton, 36, ink);
                        break;
                    case PopButton.MyList:
                        if (vm.InLibrary(tile.Id))
                            f.GlyphIn(key + ":card:list:icon", UiGlyphs.Check, bx, by, CardButton, CardButton, 36, ink);
                        else
                            f.GlyphIn(key + ":card:list:icon", UiGlyphs.Plus, bx, by, CardButton, CardButton, 36, ink);
                        break;
                    case PopButton.Rate:
                        f.GlyphIn(key + ":card:rate:icon", UiGlyphs.Thumb, bx, by - 1, CardButton, CardButton, 34, ink);
                        break;
                    default:
                        f.GlyphIn(key + ":card:info:icon", UiGlyphs.Down, bx, by + 1, CardButton, CardButton, 36, ink);
                        break;
                }
            }

            var fy = by + CardButton + CardPanelGap;
            var fx = c.X + CardPad;
            var quality = CardQuality(vm, tile);
            if (quality != null)
            {
                var qw = 24 + UiScale.TextW(quality, UiScale.Hint);
                var qy = fy + (UiScale.LineH(UiScale.Secondary) - UiScale.BadgeH) / 2;
                f.Box(key + ":card:q:ring", fx, qy, qw, UiScale.BadgeH, "#FFFFFFB3").R(UiTheme.RadiusBadge);
                f.Box(key + ":card:q:bg", fx + 2, qy + 2, qw - 4, UiScale.BadgeH - 4, CardPanel).R(UiTheme.RadiusBadge - 2);
                LineIn(f, key + ":card:q", fx, qy, UiScale.BadgeH, qw, quality, UiScale.Hint, UiTheme.Text, true, UiAlign.Center);
                fx += qw + 16;
            }
            Line(f, key + ":card:facts", fx, fy, c.Right - CardPad - fx, CardFacts(tile, extras), UiScale.Secondary, UiTheme.TextDim);
            var tags = extras == null ? "" : UiFormat.Facts(extras.Genres.Take(3).ToArray());
            Line(f, key + ":card:tags", c.X + CardPad, fy + UiScale.LineH(UiScale.Secondary) + 2, c.W - 2 * CardPad, tags, UiScale.Small, UiTheme.Text);
            for (var n = panel; n < f.Nodes.Count; n++)
            {
                f.Nodes[n].Enter = UiEnter.Fade;
                f.Nodes[n].EnterMs = CardGrowMs;
                f.Nodes[n].ExitMs = 0;   // any key: the panel goes at once while the picture shrinks back
                f.Nodes[n].MoveMs = 0;
            }
        }

        /// "Top 10 series today" → "series today".
        internal static string RankScope(string rowTitle)
        {
            var text = rowTitle ?? "";
            return text.StartsWith("Top 10 ", StringComparison.OrdinalIgnoreCase) ? text.Substring(7) : text;
        }

        private static UiNode CardStill(UiFrame f, string key, PopCardRect c, string url, double cropY)
        {
            var image = f.Image(key, c.X, c.Y, c.W, c.VideoH, url);
            if (image == null)
                return null;
            image.R(UiTheme.RadiusCard);
            // Corner masks (TV) use the page colour; the panel under the bottom corners is nearly the same shade.
            image.CornerMask = Ink(f);
            image.CropY = cropY;
            return image;
        }

        /// Outlined round button: a light ring and the panel colour inside (two rounded boxes; no per-frame assets).
        private static void RoundOutline(UiFrame f, string key, int x, int y)
        {
            f.Box(key + ":ring", x, y, CardButton, CardButton, "#FFFFFF73").R(UiTheme.RadiusPill);
            f.Box(key + ":bg", x + 3, y + 3, CardButton - 6, CardButton - 6, CardPanel).R(UiTheme.RadiusPill);
        }

        /// "S2:E3 · 26m left" on episode cards, else the type; then seasons (series) or runtime (movies) and rating.
        internal static string CardFacts(Tile tile, MetaExtras extras)
        {
            var series = tile.Type == MediaType.Series;
            // Continue / new episode / first episode cards carry "S2:E3 · 26m left" or "10m left" (a movie).
            var progress = !string.IsNullOrEmpty(tile.Subtitle) && (tile.Action == TileAction.Resume || tile.Action == TileAction.PlayFirst || tile.VideoId != null)
                ? tile.Subtitle : null;
            if (progress != null && series)
                return UiFormat.Facts(progress, Rating(extras));
            string length = progress;
            if (length == null && extras != null)
                length = series ? extras.Seasons > 0 ? extras.Seasons + (extras.Seasons == 1 ? " Season" : " Seasons") : null : extras.Runtime;
            return UiFormat.Facts(series ? "Series" : "Movie", length, Rating(extras));
        }

        /// HD / 4K only when the best playable source is already known (never resolves sources on a dwell).
        private static string CardQuality(HomeViewModel vm, Tile tile)
        {
            var signatures = vm.Shell?.Services?.Signatures;
            if (signatures == null || !signatures.TryGet(tile.VideoId ?? tile.Id, out var entry) || entry?.Facts?.Quality == null)
                return null;
            switch (entry.Facts.Quality)
            {
                case "4K": return "4K";
                case "1080p":
                case "720p": return "HD";
                default: return null;
            }
        }
    }
}
