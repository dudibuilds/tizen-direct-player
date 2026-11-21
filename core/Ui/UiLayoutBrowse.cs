using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Home, Search, Details and Source picker layouts. Lists keep the focused item at a fixed anchor so the
    /// previous item peeks above and the next below (no scrollbars); edges fade into the background.
    /// Sizes come from UiScale (10-foot UI).
    public static partial class UiLayout
    {
        // ---- shared browse pieces ----

        /// Billboard art: crossfades when the key (title id) changes; one smooth gradient each way blends it into
        /// the page. Immersive (trailer view) widens it to the full screen so nothing covers the trailer.
        private static void Billboard(UiFrame f, string id, string background, string trailerId, bool muted, int bottom, bool immersive = false, bool compact = false)
        {
            var x = immersive ? 0 : 600;
            var w = UiFrame.Width - x;
            if (!string.IsNullOrEmpty(background))
            {
                var art = f.Image("hero:bg:" + id, x, 0, w, bottom, background);
                if (art != null)
                {
                    art.EnterMs = 600;
                    art.ExitMs = 600;
                    art.MoveMs = immersive ? 450 : 280;
                    // Key art is composed with faces in the upper part: crop from the bottom, never through heads.
                    // A little right bias keeps the subject clear of the text gradient on the left.
                    art.CropY = BackdropCropY;
                    art.CropX = BackdropCropX;
                }
            }
            // Home passes a trailer only for its full-screen trailer view; the title page also plays its automatic
            // preview here, in the header behind the page.
            if (!string.IsNullOrEmpty(trailerId))
            {
                // No on-screen sound hint (user feedback, 2026-11-19): preview sound is the "Preview sound" setting.
                f.Trailer("hero:trailer:" + id, x, 0, w, bottom, trailerId, muted, id).MoveMs = 450;
            }
            // The left fade is wide, so the synopsis over it reads on a dark backdrop.
            var fadeLeft = f.HorizontalGradient("hero:fade-left", x, 0, immersive ? 900 : 760, bottom, Ink(f), immersive ? 200 : 255, 0);
            var fadeBottom = f.VerticalGradient("hero:fade-bottom", x, bottom - 320, w, 320, Ink(f), 0, 255);
            fadeLeft.MoveMs = fadeBottom.MoveMs = immersive ? 450 : 280;
            if (compact)
            {
                // Compact trailer view: the scrims lighten (node opacity: the painter fades the existing gradient, no
                // new image) so more of the video shows.
                fadeLeft.Opacity = 0.5;
                fadeBottom.Opacity = 0.6;
                fadeLeft.MoveMs = fadeBottom.MoveMs = CompactMoveMs;
            }
        }

        /// Billboard art: the card's own backdrop when it has one (live channels), else the title's Cinemeta background.
        internal static string HeroBackground(Tile hero) => hero.Background ?? UiFormat.BackgroundFor(hero.Id);

        /// A billboard with no art (a card that is not a catalog title and brought none): a designed panel instead of
        /// black, in the design language: a panel-to-ink gradient behind the art area and the card's logo, large and
        /// quiet, on the right. Billboard's own fades then blend it into the page like real art.
        private static void DesignedBackdrop(UiFrame f, Tile hero, int bottom)
        {
            const int x = 600;
            var w = UiFrame.Width - x;
            var panel = f.Box("hero:designed:" + hero.Id, x, 0, w, bottom, UiTheme.CardPanel);
            panel.EnterMs = 600;
            panel.ExitMs = 600;
            var glow = f.HorizontalGradient("hero:designed:glow:" + hero.Id, x, 0, w, bottom, UiTheme.PanelFocused, 0, 255);
            glow.EnterMs = 600;
            glow.ExitMs = 600;
            if (hero.Logo == null)
                return;
            // Centred in the art area's height (short while scrolled), clear of the safe edges.
            var markH = Math.Max(100, Math.Min(300, bottom - 2 * UiScale.SafeTop));
            var markW = markH * 2;
            var mark = f.Image("hero:designed:logo:" + hero.Id, RightEdge - markW, (bottom - markH) / 2, markW, markH, hero.Logo);
            if (mark != null)
            {
                mark.Contain = true;
                mark.Opacity = 0.22;
                mark.EnterMs = 600;
                mark.ExitMs = 600;
            }
        }

        /// Backdrop crop anchor (see UiNode.CropX/CropY): keep the top tenth-ish, and lean slightly right.
        internal const double BackdropCropY = 0.12;
        internal const double BackdropCropX = 0.6;

        /// Netflix trailer view: this long after the trailer's first frames the billboard text gives way to the video.
        public const int TrailerCompactDelayMs = 3000;
        /// The logo shrinks to this fraction and the facts fade out; everything glides over CompactMoveMs.
        internal const double CompactLogoScale = 0.6;
        internal const int CompactMoveMs = 600;

        /// Rounded poster card. Corner masks (TV painter) use the page background behind the card.
        private static void Poster(UiFrame f, string key, int x, int y, int w, int h, string poster, string title, bool focused, double progress, string badge)
        {
            if (focused)
            {
                // Grow around the centre, with a soft glow and drop shadow; the ring follows the card radius.
                var dw = (int)(w * UiScale.PosterGrow);
                var dh = (int)(h * UiScale.PosterGrow);
                x -= dw / 2;
                y -= dh / 2;
                w += dw;
                h += dh;
                FocusDecor(f, key, x, y, w, h);
            }
            f.Box(key + ":ph", x, y, w, h, UiTheme.Placeholder).R(UiTheme.RadiusCard);
            f.Text(key + ":alt", x + 16, y + 16, w - 32, h - 32, title, UiScale.Small, UiTheme.TextDim, wrap: true);
            var image = f.Image(key + ":poster", x, y, w, h, UiFormat.CardPoster(poster));
            if (image != null)
            {
                image.R(UiTheme.RadiusCard);
                image.CornerMask = focused ? UiTheme.Focus : Ink(f);
            }
            if (progress > 0)
                ProgressBar(f, key + ":progress", x + 12, y + h - 22, w - 24, 10, progress);
            if (!string.IsNullOrEmpty(badge))
                Badge(f, key + ":badge", x + 12, y + 12, badge);
        }

        /// Small label on a card ("New Episode", "In library"): bold Label text on a neutral ink plate that holds on
        /// any artwork. Only state uses colour (the amber rank on a popped Top 10 card).
        private static void Badge(UiFrame f, string key, int x, int y, string label, string fill = BadgePlate, string color = UiTheme.Text)
        {
            var w = 32 + UiScale.TextW(label, UiScale.Hint);
            f.Box(key + ":bg", x, y, w, UiScale.BadgeH, fill).R(UiTheme.RadiusBadge);
            LineIn(f, key, x, y, UiScale.BadgeH, w, label, UiScale.Hint, color, true, UiAlign.Center);
        }

        private const string BadgePlate = "#0B0E14D9";

        /// Focus on a card: a soft shadow under it and one white ring hugging it (no glow band, no dark border).
        private static void FocusDecor(UiFrame f, string key, int x, int y, int w, int h)
        {
            const int ring = UiScale.FocusRing;
            f.Shadow(key + ":shadow", x, y, w, h, UiTheme.RadiusCard).ExitMs = 150;
            f.Box(key + ":ring", x - ring, y - ring, w + 2 * ring, h + 2 * ring, UiTheme.Focus).R(UiTheme.RadiusCard + ring).ExitMs = 150;
        }

        private static void EdgeFades(UiFrame f, string key, int top, int topH, int bottom, int bottomH, int x = 0, int w = UiFrame.Width)
        {
            if (topH > 0)
                f.VerticalGradient(key + ":top", x, top, w, topH, Ink(f), 255, 0);
            if (bottomH > 0)
                f.VerticalGradient(key + ":bottom", x, bottom - bottomH, w, bottomH, Ink(f), 0, 255);
        }

        private static string Rating(MetaExtras e) => string.IsNullOrWhiteSpace(e?.Rating) ? null : "★ " + e.Rating;

        // ---- Home ----

        private const int PosterW = UiScale.PosterW;
        private const int PosterH = UiScale.PosterH;
        private const int LandW = UiScale.LandW;
        private const int LandH = UiScale.LandH;
        private const int RankSlot = 190;        // space a one-digit numeral owns left of its poster
        private const int RankSlotWide = 312;    // "10"
        private const int RankOverlap = 40;      // the poster covers this much of its own numeral
        private const int RankBleed = 36;        // numeral box extends this far under its poster, so the digit is never cut
        private const int RankSize = 330;        // numeral font size (one digit); two digits are a little smaller
        private const int RankSizeWide = 272;
        private const int TileGap = UiScale.CardGap;
        private const int RowTitleH = 64;        // row title line (40 px text) and the gap to its cards
        private const int CaptionGap = 16;
        // Home is two zones that never overlap: the hero zone [0, H) holds the art, logo, facts and synopsis; the
        // rows zone [H, 1080) holds the focused row, the rows below it and the dimmed peek of the row above.
        internal const int HeroBottom = 640;            // H on the initial view when the first row has landscape cards
        internal const int HeroBottomTall = 476;        // ... when the first row has posters (taller; the focused one grows)
        internal const int ScrolledHeroBottom = 380;    // H once scrolled: the billboard becomes a banner
        private const int RowAnchor = 460;       // focused row title once scrolled
        private const int PeekGap = 32;          // between the bottom of the peeking row above and the focused row title
        private const int ImmersiveStrip = 128;  // trailer view: only this much of the focused row's cards shows

        /// Hero zone height on the initial view: the first row's cards always fit above the bottom safe line.
        internal static int HeroBottomFor(TileStyle firstRow) => firstRow == TileStyle.Landscape ? HeroBottom : HeroBottomTall;

        private static int TileH(TileStyle style) => style == TileStyle.Landscape ? LandH : PosterH;
        /// Title line plus a gap that clears the focused card's scale-up (it grows from its centre) and focus ring, so a
        /// focused poster never covers its row title (user feedback, 2026-11-19).
        private static int RowTitleSpace(TileStyle style) =>
            RowTitleH + (int)Math.Ceiling(TileH(style) * (style == TileStyle.Landscape ? UiScale.LandscapeGrow : UiScale.PosterGrow) / 2) + UiScale.FocusRing + 6;

        private static int RowHeight(TileStyle style) => RowTitleSpace(style) + TileH(style) + (style == TileStyle.Landscape ? CaptionGap + UiScale.LineH(UiScale.Small) + 32 : 56);

        /// Width a tile owns in its row (ranked tiles include their numeral slot).
        private static int TileW(TileStyle style, Tile tile)
        {
            switch (style)
            {
                case TileStyle.Landscape: return LandW;
                case TileStyle.Ranked: return tile.Action == TileAction.SeeAll ? PosterW : (tile.Rank >= 10 ? RankSlotWide : RankSlot) - RankOverlap + PosterW;
                default: return PosterW;
            }
        }

        /// Left edge of every tile of a row, scrolled so the focused tile sits second from the left, and clamped so
        /// the row never scrolls past its last tile (its right edge stops at the right margin).
        internal static int[] TileXs(HomeRow row, int column)
        {
            var xs = new int[row.Items.Count];
            var x = 0;
            for (var i = 0; i < xs.Length; i++)
            {
                xs[i] = x;
                x += TileW(row.Style, row.Items[i]) + TileGap;
            }
            var total = x - TileGap;
            var scroll = column <= 0 || xs.Length == 0 ? 0 : xs[Math.Min(column, xs.Length - 1) - 1];
            scroll = Math.Max(0, Math.Min(scroll, total - CW));
            for (var i = 0; i < xs.Length; i++)
                xs[i] += M - scroll;
            return xs;
        }

        private static void Home(UiFrame f, HomeViewModel vm)
        {
            if (vm.Service != null)
                f.Background = vm.Service.PageInk;   // the service's tint: page ink, scrims and card corners follow
            var rows = vm.Rows;
            var focusedRow = rows.Count == 0 ? 0 : vm.FocusedRowIndex;
            var menuFocused = rows.Count > 0 && rows[focusedRow].IsMenu;
            var shelves = rows.Where(r => !r.IsMenu).ToList();
            var focusedShelf = menuFocused ? 0 : Math.Max(0, shelves.FindIndex(r => r.Key == rows[focusedRow].Key));
            var hero = vm.HeroTile;
            var immersive = vm.Immersive;
            var scrolled = focusedShelf > 0 && !immersive;
            var initialZone = HeroBottomFor(shelves.Count > 0 ? shelves[0].Style : TileStyle.Landscape);

            // Rows. Initial view: the first row starts below the billboard. Scrolled: the focused row sits at the
            // anchor, the next row peeks at the bottom and the bottom slice of the row above peeks over it, dimmed,
            // so there is always a sense of more above. The last row sits at the bottom of the screen, which shows
            // more of the row above instead of an empty half screen. Trailer view: rows slide down to a strip.
            var anchor = immersive ? UiFrame.Height - ImmersiveStrip - RowTitleH : scrolled ? RowAnchor : initialZone;
            PopAnchor pop = null;
            var popTitleY = 0;
            if (scrolled && focusedShelf == shelves.Count - 1)
                anchor = Math.Max(anchor, UiFrame.Height - 40 - RowHeight(shelves[focusedShelf].Style));
            if (scrolled)
            {
                var above = shelves[focusedShelf - 1];
                var aboveY = anchor - PeekGap - TileH(above.Style);
                // The row two above waits entirely under the banner band, so rows glide in and out of the peek
                // instead of appearing in it.
                if (focusedShelf > 1)
                {
                    var hidden = shelves[focusedShelf - 2];
                    // (Top 10 numerals reach a fifth of their size below the poster, so that row waits a little higher.)
                    var numeralBleed = hidden.Style == TileStyle.Ranked ? RankSize / 5 + 8 : 0;
                    HomeRowCards(f, vm, hidden, Math.Min(aboveY - RowHeight(hidden.Style), ScrolledHeroBottom - TileH(hidden.Style) - 8 - numeralBleed), false, false, false, false);
                }
                // Rows above show cards only: a title leaves the moment its row stops being the focused one, so it
                // never travels across other text.
                HomeRowCards(f, vm, above, aboveY, false, false, false, false);
                // One scrim dims the whole peeking row (cheaper than fading every card, and nothing shows through).
                f.Box("home:peek-dim", 0, ScrolledHeroBottom, UiFrame.Width, anchor - PeekGap + 8 - ScrolledHeroBottom, Ink(f) + "99").Enter = UiEnter.None;
            }

            // Trailer view: the art fills the screen and the strip of cards lies on top of it.
            if (immersive && hero != null)
                Billboard(f, hero.Id, HeroBackground(hero), vm.TrailerId, vm.TrailerMuted, UiFrame.Height, immersive: true, compact: vm.TrailerCompact);

            var top = anchor;
            // One row beyond the bottom edge is laid out too, so the next row glides in from below.
            for (var r = focusedShelf; r < shelves.Count && top < UiFrame.Height + 500; r++)
            {
                var row = shelves[r];
                var isFocusedRow = !menuFocused && r == focusedShelf;
                // Row titles leave at once for the trailer view and fade back in place afterwards: they never glide
                // through the trailer view's own text.
                var popAnchor = HomeRowCards(f, vm, row, top + RowTitleSpace(row.Style), isFocusedRow && !immersive, !immersive, immersive, isFocusedRow);
                pop = pop ?? popAnchor;
                if (popAnchor != null)
                    popTitleY = top;   // drawn with the card, above everything, so the card never slices it
                else if (!immersive)
                {
                    var title = Line(f, "row:" + row.Key + ":title", M, top, 1400, row.Title, UiScale.RowTitle, isFocusedRow ? UiTheme.Text : UiTheme.TextDim, true);
                    title.MoveMs = 280;
                    // Under a popped card the next rows' titles step out (their art stays, dimmed): the card would cut them.
                    if (pop != null)
                        title.Opacity = 0;
                }
                top += RowHeight(row.Style);
            }

            if (!immersive)
            {
                EdgeFades(f, "home:fade", 0, 0, UiFrame.Height, 110);
                // The hero zone is drawn over the rows zone: an opaque band [0, H) hides whatever part of a row is
                // above the line (the peek's upper part, rows gliding in or out), then the art and text go on it.
                var zone = scrolled ? ScrolledHeroBottom : initialZone;
                var band = f.Box("home:band", 0, 0, UiFrame.Width, zone, Ink(f));
                band.Enter = UiEnter.None;
                band.MoveMs = 280;
                if (scrolled)
                    f.VerticalGradient("home:band-fade", 0, zone, UiFrame.Width, BandFade, Ink(f), 255, 0).MoveMs = 280;   // the peeking row dissolves before its logos show
                if (hero != null)
                {
                    var background = hero.ServiceCard?.Backdrop ?? HeroBackground(hero);
                    if (background == null)
                        DesignedBackdrop(f, hero, zone);
                    Billboard(f, hero.Id, background, null, true, zone);
                }
                if (vm.Service != null)
                    ServiceGlow(f, vm.Service, zone);
            }

            // Billboard text. Scrolled, it moves up into the banner. While a card is popped the text block steps aside
            // (the backdrop stays): the card names the same title, and nothing is left for it to cut through.
            if (hero?.ServiceCard != null && pop == null)
                ServiceBanner(f, hero.ServiceCard, scrolled ? UiScale.SafeTop : 136);
            else if (hero != null && pop == null)
            {
                var e = vm.HeroExtras;
                var tall = !scrolled && !immersive && initialZone >= HeroBottom;
                var textTop = immersive ? 560 : scrolled ? UiScale.SafeTop : vm.Service != null ? 166 : 136;   // a service page's header sits above   // trailer view: room for Play / More info
                var logoH = tall ? 170 : immersive || scrolled ? 110 : 140;
                var logoW = tall ? 640 : immersive ? 520 : 480;
                // The trailer view has its own text nodes (keys "hero:tv…"): entering or leaving it crossfades the
                // two blocks instead of gliding one through the rows.
                var k = immersive ? "hero:tv" : "hero";
                var compact = immersive && vm.TrailerCompact;
                // Fixed button row of the trailer view (below the facts line of the full billboard).
                var buttonsY = textTop + logoH + 6 + UiScale.LineH(UiScale.Facts) + 24;
                var logo = compact
                    ? f.Image(k + ":logo:" + hero.Id, M, buttonsY - 24 - (int)(logoH * CompactLogoScale), (int)(logoW * CompactLogoScale), (int)(logoH * CompactLogoScale), e?.Logo ?? hero.Logo)
                    : f.Image(k + ":logo:" + hero.Id, M, textTop, logoW, logoH, e?.Logo ?? hero.Logo);
                const int heroMove = 280;   // the hero text moves with the rows
                if (logo != null)
                {
                    logo.Contain = true;
                    logo.AnchorBottom = true;   // a wide, thin logo sits on the facts line instead of floating mid-box
                    logo.ExitMs = 200;
                    logo.MoveMs = immersive ? CompactMoveMs : heroMove;   // the same image glides and shrinks (no reload)
                }
                else
                    LineIn(f, k + ":title", M, textTop, logoH, 1100, hero.Title, logoH >= UiScale.LineH(UiScale.Display) ? UiScale.Display : UiScale.Title, bold: true).MoveMs = heroMove;
                var facts = hero.Facts ?? UiFormat.Facts(hero.Type == MediaType.Series ? "Series" : "Movie", Rating(e), e?.Runtime,
                    e == null ? null : string.Join(", ", e.Genres.Take(3)), hero.Badge);
                // Renderers trim a logo's transparent margins and sit its visible bottom on the box's bottom edge, so
                // the facts line follows closely (the line box adds its own top padding above the glyphs).
                var factsY = textTop + logoH + (logo != null ? 6 : 16);
                if (!compact)
                {
                    var factsNode = Line(f, k + ":facts", M, factsY, 1100, facts, UiScale.Facts, UiTheme.TextDim);
                    factsNode.MoveMs = heroMove;
                    factsNode.ExitMs = 400;   // fades out when the trailer view turns compact
                }
                if (immersive)
                {
                    // Netflix trailer view: Play and More info under the facts; the focused one is filled white.
                    var bx = M;
                    var by = buttonsY;
                    for (var i = 0; i < HomeViewModel.HeroButtons.Length; i++)
                    {
                        var label = HomeViewModel.HeroButtons[i];
                        var bw = ButtonWidth(label) + (i == 0 ? 24 : 0);
                        Button(f, "hero:tv:button:" + i, bx, by, bw, UiScale.ButtonH, label, vm.HeroButton == i, fill: "#FFFFFF33");
                        bx += bw + 20;
                    }
                }
                if (!immersive)
                    f.Paragraph("hero:description", M, factsY + UiScale.LineH(UiScale.Facts) + 8, 960, tall ? 3 : 2, e?.Description ?? hero.Description ?? "", UiScale.Body, UiTheme.Text).MoveMs = heroMove;
            }

            // Menu bar: Home carries the current-section underline, Search its icon, and Settings (which holds the rarely
            // used destinations) sits apart at the right. A pill highlight marks the focused item. Hidden while
            // scrolled or in trailer view.
            var menu = rows.FirstOrDefault(r => r.IsMenu);
            if (menu != null && !scrolled && !immersive && vm.Service != null)
                ServiceHeader(f, vm, menu, menuFocused);
            else if (menu != null && !scrolled && !immersive)
            {
                const int menuH = 64, iconW = 44;
                var start = f.Nodes.Count;
                // Top scrim so the labels hold on bright artwork, then the app's mark (amber) ahead of the menu.
                f.VerticalGradient("menu:scrim", 0, 0, UiFrame.Width, 180, Ink(f), 179, 0);
                // The logo (mark + "direct"). While the launch intro plays it draws these same keys and lands them here.
                if (vm.Intro == null)
                    UiWordmark.DrawMenu(f);
                var x = M + (int)Math.Ceiling(UiWordmark.Width(1)) + 36 - 28;
                for (var i = 0; i < menu.Items.Count; i++)
                {
                    var item = menu.Items[i];
                    var label = item.Title;
                    var icon = item.Action == TileAction.Search ? iconW : 0;
                    var w = UiScale.TextW(label, UiScale.Button) + 56 + icon;
                    var ix = item.Action == TileAction.Motion ? RightEdge + 28 - w : x;
                    var focused = menuFocused && vm.FocusedColumn(menu) == i;
                    var home = item.Action == TileAction.Home;
                    var color = focused ? UiTheme.TextOnFocus : home ? UiTheme.Text : UiTheme.TextDim;
                    if (focused)
                        f.Box("menu:" + item.Id + ":bg", ix, UiScale.SafeTop, w, menuH, UiTheme.FocusFill).R(UiTheme.RadiusPill);
                    else if (home)
                        f.Box("menu:" + item.Id + ":here", ix + 28, UiScale.SafeTop + menuH - 8, w - 56, 4, UiTheme.Accent).R(2);
                    if (icon > 0)
                        UiIcons.Search(f, "menu:" + item.Id + ":icon", ix + 24, UiScale.SafeTop + (menuH - 34) / 2, 34, color);
                    LineIn(f, "menu:" + item.Id + ":label", ix + icon, UiScale.SafeTop, menuH, w - icon, label, UiScale.Button, color, focused || home, UiAlign.Center);
                    if (item.Action != TileAction.Motion)
                        x += w + 8;
                }
                Animate(f, start, UiEnter.Fade, 200);
            }
            else if (rows.Count <= 1)
                Line(f, "home:loading", M, 200, CW, "Loading…", UiScale.Heading, UiTheme.TextDim);

            // The popped card goes on top of everything (the billboard band and menu are drawn after the rows). Its row
            // title rides just above it, so the heading stays readable wherever the card had to grow.
            if (pop != null)
            {
                var card = PopCardRectFor(pop.Style, pop.X, pop.Y, pop.W, pop.H, pop.SlotX);
                var titleY = Math.Max(UiScale.SafeTop, Math.Min(popTitleY, card.Y - RowTitleH - 12));
                // One scrim dims the neighbours and the rows below, so the lifted card reads as depth and never
                // slices bright text (one box: cheaper than fading every card).
                var dim = f.Box("home:pop-dim", 0, titleY + RowTitleH, UiFrame.Width, UiFrame.Height - titleY - RowTitleH, Ink(f) + "8C");
                dim.Enter = UiEnter.Fade;
                dim.EnterMs = 200;
                dim.ExitMs = 120;
                Line(f, "row:" + vm.PoppedRowKey + ":title", M, titleY, 1400, pop.RowTitle, UiScale.RowTitle, UiTheme.Text, true).MoveMs = 280;
                PopCard(f, vm, pop);
            }
        }

        /// The cards of one row at y. `captions` off for the peeking row above and the trailer view. Returns where the
        /// popped card grows from when this row holds it (that tile is then left to PopCard, except a Top 10 numeral).
        private static PopAnchor HomeRowCards(UiFrame f, HomeViewModel vm, HomeRow row, int y, bool focusedRow, bool captions, bool immersive, bool scrolls)
        {
            var column = vm.FocusedColumn(row);
            var xs = TileXs(row, column);
            PopAnchor pop = null;
            // Only the focused row scrolls sideways (Left/Right); every other row keeps its column through Up/Down,
            // so its cards beyond the screen edges can never come into view with one key press and are left out
            // (they would cost the TV native objects and picture loads for nothing).
            var overscan = scrolls ? 500 : 0;
            for (var i = 0; i < row.Items.Count; i++)
            {
                var w = TileW(row.Style, row.Items[i]);
                if (overscan > 0 ? xs[i] + w < -TileGap - overscan || xs[i] > UiFrame.Width + overscan : xs[i] + w <= 0 || xs[i] >= UiFrame.Width)
                    continue;   // the focused row keeps one card beyond each edge, so cards glide in instead of popping
                var start = f.Nodes.Count;
                var key = "row:" + row.Key + ":" + i;
                if (focusedRow && i == column && vm.PoppedTile != null && vm.PoppedTile.Id == row.Items[i].Id && vm.PoppedRowKey == row.Key)
                {
                    var tile = row.Items[i];
                    var artX = row.Style == TileStyle.Ranked ? xs[i] + (tile.Rank >= 10 ? RankSlotWide : RankSlot) - RankOverlap : xs[i];
                    pop = new PopAnchor { Key = key, Style = row.Style, Tile = tile, X = artX, Y = y, W = TileH(row.Style) == LandH ? LandW : PosterW, H = TileH(row.Style), SlotX = xs[i], RowTitle = row.Title };
                    if (row.Style == TileStyle.Ranked)
                        RankNumeral(f, key, tile, xs[i], y);
                    continue;
                }
                // A row with a popped card shows no captions: the card would slice the neighbours' lines.
                HomeCard(f, key, row.Style, row.Items[i], xs[i], y, focusedRow && i == column, row.Key, captions && !(focusedRow && vm.PoppedTile != null && vm.PoppedRowKey == row.Key));
                for (var n = start; n < f.Nodes.Count; n++)
                {
                    f.Nodes[n].MoveMs = immersive ? 450 : 280;   // rows glide; the trailer view slides them away slower
                    if (f.Nodes[n].Kind != UiNodeKind.Text)
                        f.Nodes[n].ExitMs = Math.Max(f.Nodes[n].ExitMs, 160);   // text leaves at once, so it never crosses new text
                }
            }
            return pop;
        }

        private const int BandFade = 96;

        private static void RankNumeral(UiFrame f, string key, Tile tile, int x, int y)
        {
            // The numeral owns its slot left of its poster: dark fill with a light stroke, so it reads on
            // any background without shouting. Only its own poster overlaps it (drawn after it).
            var digits = tile.Rank.ToString(CultureInfo.InvariantCulture);
            var slot = tile.Rank >= 10 ? RankSlotWide : RankSlot;
            var size = digits.Length > 1 ? RankSizeWide : RankSize;
            // The box is wider than the slot (a big digit is wider than its slot on the TV); the extra lies under the poster.
            var numeral = f.Text(key + ":rank", x, y + PosterH - UiFormat.LineH(size) + (int)(size * 0.2), slot + RankBleed, UiFormat.LineH(size), digits, size, UiTheme.Panel, true, UiAlign.Center);
            numeral.Stroke = UiTheme.TextDim;
        }

        private static void HomeCard(UiFrame f, string key, TileStyle style, Tile tile, int x, int y, bool focused, string rowKey, bool caption = true)
        {
            if (tile.Action == TileAction.SeeAll)
            {
                SeeAllCard(f, key, x, y, style == TileStyle.Landscape ? LandW : PosterW, TileH(style), focused);
                return;
            }
            switch (style)
            {
                case TileStyle.Ranked:
                    RankNumeral(f, key, tile, x, y);
                    Poster(f, key, x + (tile.Rank >= 10 ? RankSlotWide : RankSlot) - RankOverlap, y, PosterW, PosterH, tile.Poster, tile.Title, focused, 0, null);
                    break;
                case TileStyle.Landscape:
                    Landscape(f, key, x, y, tile, focused);
                    // The caption always rides with its card and only fades, so it never appears in the path of
                    // text that is still gliding.
                    if (!string.IsNullOrEmpty(tile.Subtitle))
                    {
                        var shown = caption && (rowKey == "continue" || rowKey == "new" || rowKey == "firsteps" || focused);
                        // A row without captions (the peek above, the trailer view) tucks them behind the card's lower edge,
                        // so a fading caption pulls away from the next row's title instead of drifting into it.
                        Line(f, key + ":caption", x, caption ? y + LandH + CaptionGap : y + LandH - 48, LandW, tile.Subtitle, UiScale.Small, focused ? UiTheme.Text : UiTheme.TextDim).Opacity = shown ? 1 : 0;
                    }
                    break;
                default:
                    Poster(f, key, x, y, PosterW, PosterH, tile.Poster, tile.Title, focused, tile.Progress, tile.Badge);
                    break;
            }
        }

        /// Widest a landscape card is drawn (focused, grown): its art is requested at this width.
        internal const int LandCardArtW = (int)(LandW * (1 + UiScale.LandscapeGrow)) + 1;

        /// The picture a Home card draws, at card size (episode stills at w500 instead of w780, metahub posters at
        /// "small"). The TV prefetches exactly this URL, and a popped card grows the same file before its sharper
        /// art fades in over it.
        internal static string HomeCardArt(TileStyle style, Tile tile) =>
            style == TileStyle.Landscape ? UiFormat.ArtAtWidth(tile.Image ?? tile.Poster, LandCardArtW) : UiFormat.CardPoster(tile.Poster);

        /// Landscape card: episode still or background art, the title logo over a bottom scrim, labels and progress.
        private static void Landscape(UiFrame f, string key, int x, int y, Tile tile, bool focused)
        {
            int w = LandW, h = LandH;
            if (focused)
            {
                var dw = (int)(w * UiScale.LandscapeGrow);
                var dh = (int)(h * UiScale.LandscapeGrow);
                x -= dw / 2;
                y -= dh / 2;
                w += dw;
                h += dh;
                FocusDecor(f, key, x, y, w, h);
            }
            f.Box(key + ":ph", x, y, w, h, UiTheme.Placeholder).R(UiTheme.RadiusCard);
            Line(f, key + ":alt", x + 20, y + h - UiScale.LineH(UiScale.Secondary) - 16, w - 40, tile.Title, UiScale.Secondary, UiTheme.Text, true);
            var art = f.Image(key + ":art", x, y, w, h, HomeCardArt(TileStyle.Landscape, tile));
            if (art != null)
            {
                art.R(UiTheme.RadiusCard);
                art.CornerMask = focused ? UiTheme.Focus : Ink(f);
            }
            if (tile.ServiceCard?.Service != null)
                ServiceCardFace(f, key, x, y, w, h, tile);
            if (tile.Logo != null && art != null && !tile.ArtHasTitle)   // over art only: without art the card names its title in text
            {
                f.VerticalGradient(key + ":scrim", x, y + h / 2, w, h - h / 2, "#000000", 0, 200).R(UiTheme.RadiusCard);
                var logo = f.Image(key + ":logo", x + 20, y + h - 112, (int)(w * 0.55), 80, tile.Logo);   // clear of the progress bar
                logo.Contain = true;
                logo.AnchorBottom = true;
            }
            if (tile.Progress > 0)
                ProgressBar(f, key + ":progress", x + 14, y + h - 18, w - 28, 8, tile.Progress);
            if (tile.Watched)
                UiIcons.Check(f, key + ":watched", x + w - 62, y + 14, 48);
            if (!string.IsNullOrEmpty(tile.Badge))
                Badge(f, key + ":badge", x + 12, y + 12, tile.Badge);
        }

        // ---- Streaming services (docs/streaming-services.md) ----

        /// Soft wash of the service's tint over the top-left of the page, behind the header and billboard text.
        private static void ServiceGlow(UiFrame f, Ru7100.Core.Addons.StreamingService service, int bottom)
        {
            var glow = f.HorizontalGradient("svc:glow", 0, 0, 1300, bottom, service.Tint, 150, 0);
            glow.Enter = UiEnter.None;
            glow.MoveMs = 280;
            // Fades back into the page ink above the rows, so the wash never ends in an edge.
            var fadeH = Math.Min(260, bottom / 2);
            var fade = f.VerticalGradient("svc:glow-fade", 0, bottom - fadeH, 1300, fadeH, Ink(f), 0, 255);
            fade.Enter = UiEnter.None;
            fade.MoveMs = 280;
        }

        /// Service page header in place of the menu: "Services", the service name as plain Title text, then the
        /// service's own chips (amber = where you are, the light fill = focus).
        private static void ServiceHeader(UiFrame f, HomeViewModel vm, HomeRow menu, bool menuFocused)
        {
            var start = f.Nodes.Count;
            f.VerticalGradient("menu:scrim", 0, 0, UiFrame.Width, 180, Ink(f), 179, 0);
            Line(f, "svc:crumb", M, UiScale.SafeTop - 22, 600, "Services", UiScale.Small, UiTheme.TextDim);
            var titleY = UiScale.SafeTop + 14;
            var titleH = UiScale.LineH(UiScale.Title);
            var name = vm.Service.Name;
            Line(f, "svc:name", M, titleY, 900, name, UiScale.Title, UiTheme.Text, true);
            var chipsX = M + UiScale.TextW(name, UiScale.Title) + 56;
            string Label(int i) => menu.Items[i].Title;
            int Width(int i) => Math.Max(150, UiScale.TextW(Label(i), UiScale.Small) + 64);
            Strip(f, "svc:chip", chipsX, titleY + (titleH - UiScale.ChipH) / 2, menu.Items.Count, Label, Width,
                menuFocused ? vm.FocusedColumn(menu) : -1, i => UiTheme.CardPanel, i => menu.Items[i].Id == vm.ServiceMode);
            Animate(f, start, UiEnter.Fade, 200);
        }

        /// Billboard text while a Services-row card has focus: the service name in Display type, its counts and
        /// what is popular on it.
        private static void ServiceBanner(UiFrame f, ServiceCard card, int top)
        {
            const int move = 280;
            var titleH = UiScale.LineH(UiScale.Display);
            Line(f, "hero:svc:name", M, top, 1100, card.Service.Name, UiScale.Display, UiTheme.Text, true).MoveMs = move;
            Line(f, "hero:svc:facts", M, top + titleH + 8, 1100, card.Facts, UiScale.Facts, UiTheme.TextDim).MoveMs = move;
            f.Paragraph("hero:svc:description", M, top + titleH + 8 + UiScale.LineH(UiScale.Facts) + 8, 960, 1, card.Description, UiScale.Body, UiTheme.Text).MoveMs = move;
        }

        /// Services-row card: a gradient in the service's tint over the art, the name as plain Title text and a meta
        /// line, no logos.
        private static void ServiceCardFace(UiFrame f, string key, int x, int y, int w, int h, Tile tile)
        {
            var service = tile.ServiceCard.Service;
            f.VerticalGradient(key + ":tint", x, y, w, h, service.Tint, 40, 190).R(UiTheme.RadiusCard);
            f.HorizontalGradient(key + ":ink", x, y, w * 3 / 4, h, service.PageInk, 220, 0).R(UiTheme.RadiusCard);
            var captionH = UiScale.LineH(UiScale.Small);
            var nameH = UiScale.LineH(UiScale.Title);
            Line(f, key + ":name", x + 28, y + h - 22 - captionH - nameH, w - 56, service.Name, UiScale.Title, UiTheme.Text, true);
            Line(f, key + ":meta", x + 28, y + h - 22 - captionH, w - 56, tile.ServiceCard.Caption, UiScale.Small, UiTheme.Text);
        }

        /// Last card of a service row: opens the row's titles in Browse.
        private static void SeeAllCard(UiFrame f, string key, int x, int y, int w, int h, bool focused)
        {
            if (focused)
            {
                var grow = Math.Abs(h - LandH) < 2 ? UiScale.LandscapeGrow : UiScale.PosterGrow;
                var dw = (int)(w * grow);
                var dh = (int)(h * grow);
                x -= dw / 2;
                y -= dh / 2;
                w += dw;
                h += dh;
                FocusDecor(f, key, x, y, w, h);
            }
            f.Box(key + ":bg", x, y, w, h, focused ? UiTheme.Panel : UiTheme.Glass).R(UiTheme.RadiusCard);
            LineIn(f, key + ":label", x, y, h, w, "See all", UiScale.Body, UiTheme.Text, true, UiAlign.Center);
        }

        // ---- Search ----

        private const int CardW = UiScale.PosterW;
        private const int CardH = UiScale.PosterH;
        
        private const int CardRowH = CardH + 120;
        private const int SearchKeyCell = 80;
        private const int SearchKeyGap = 8;
        private const int SearchPanelW = M + 7 * SearchKeyCell + 6 * SearchKeyGap + 40;   // keyboard panel (744)
        private const int SearchResultsX = SearchPanelW + 56;                             // 800
        private const int SearchInfoW = 528;
        // The grid spans the space right of the keyboard, edge to edge of the safe area.
        private const int CardGap = (RightEdge - SearchResultsX - SearchViewModel.Columns * CardW) / (SearchViewModel.Columns - 1);

        private static void Search(UiFrame f, SearchViewModel vm)
        {
            const int rx = SearchResultsX;
            var focusRow = vm.ResultIndex / SearchViewModel.Columns;
            const int gridTop = 268;
            var resultsFocused = vm.Focus == SearchFocus.Results;

            // Results grid first (so headers and chips sit above the faded peek).
            for (var i = 0; i < vm.Items.Count; i++)
            {
                var row = i / SearchViewModel.Columns;
                var delta = row - (resultsFocused ? focusRow : 0);
                if (delta < -1 || delta > 2) continue;
                var y = delta >= 0 ? gridTop + delta * CardRowH : gridTop - CardRowH - 120;
                var col = i % SearchViewModel.Columns;
                var x = rx + col * (CardW + CardGap);
                var item = vm.Items[i];
                var focused = resultsFocused && i == vm.ResultIndex;
                var key = "result:" + item.Meta.Id;
                var start = f.Nodes.Count;
                Poster(f, key, x, y, CardW, CardH, item.Meta.Poster, item.Meta.Name, focused, 0, item.InLibrary ? "In library" : null);
                // The focused row's captions give way to the info panel, which names the focused title.
                if (!(resultsFocused && delta == 0))
                {
                    var e = vm.ExtrasFor(item.Meta.Id);
                    Line(f, key + ":name", x, y + CardH + 16, CardW, item.Meta.Name, UiScale.Small, UiTheme.TextDim);
                    Line(f, key + ":facts", x, y + CardH + 16 + UiScale.LineH(UiScale.Small), CardW,
                        UiFormat.Facts(item.Meta.ReleaseInfo, Rating(e)).Replace("  ·  ", " · "), UiScale.Hint, UiTheme.TextFaint);
                }
                for (var n = start; n < f.Nodes.Count; n++)
                    f.Nodes[n].MoveMs = 240;
            }
            EdgeFades(f, "search:fade", 0, 0, UiFrame.Height, 110, SearchPanelW, UiFrame.Width - SearchPanelW);
            HeaderBand(f, "search", SearchPanelW, UiFrame.Width - SearchPanelW, 244);

            // Focused card details: the info panel that unfolds below the card (the same panel Browse shows).
            var focusedItem = vm.FocusedItem;
            if (focusedItem != null)
            {
                var col = vm.ResultIndex % SearchViewModel.Columns;
                var px = Math.Min(rx + col * (CardW + CardGap) - 12, RightEdge - SearchInfoW);
                var e = vm.ExtrasFor(focusedItem.Meta.Id);
                TitleInfoPanel(f, "search:card", px, gridTop + CardH + 28, focusedItem.Meta, e, focusedItem.AddonName != null ? "From " + focusedItem.AddonName : "",
                    vm.SignatureFor(focusedItem.Meta.Id), vm.SignatureChecking(focusedItem.Meta.Id));
            }

            // Header: the query in a field with a caret, and chips (recent searches, or refinements of the results).
            const int fieldW = 700, fieldH = 80;
            var typing = vm.Focus == SearchFocus.Keyboard;
            f.Box("search:field", rx, UiScale.SafeTop, fieldW, fieldH, typing ? UiTheme.PanelFocused : UiTheme.Panel).R(UiTheme.RadiusCard);
            var shown = vm.Query.Length == 0 ? "Search titles" : Tail(vm.Query, fieldW - 96, UiScale.Heading, 1);
            LineIn(f, "search:query", rx + 28, UiScale.SafeTop, fieldH, fieldW - 56, vm.Query.Length == 0 ? shown : shown + (typing ? "▏" : ""), UiScale.Heading, vm.Query.Length == 0 ? UiTheme.TextFaint : UiTheme.Text, true);
            var status = vm.Searching ? "Searching all catalogs…" : vm.ShownQuery.Length > 0 ? vm.Items.Count + " results" : vm.Items.Count > 0 ? "Popular now" : "";
            LineIn(f, "search:status", RightEdge - 300, UiScale.SafeTop, fieldH, 300, status, UiScale.Small, UiTheme.TextFaint, align: UiAlign.End);
            var chips = vm.Chips;
            var chipY = UiScale.SafeTop + UiScale.LineH(UiScale.Title) + 24;
            int ChipW(string label) => Math.Min(440, UiScale.TextW(label, UiScale.Small) + 56);
            Strip(f, "chip", rx, chipY, chips.Count, i => chips[i].Label, i => ChipW(chips[i].Label), vm.Focus == SearchFocus.Suggestions ? vm.SuggestionIndex : -1,
                i => UiTheme.CardPanel, i => false);

            // Keyboard panel.
            f.Box("search:kbpanel", 0, 0, SearchPanelW, UiFrame.Height, "#080A0F");
            Line(f, "search:title", M, UiScale.SafeTop, SearchPanelW - M, "Search", UiScale.Heading, bold: true);
            var kbTop = UiScale.SafeTop + UiScale.LineH(UiScale.Heading) + 24;
            Keyboard(f, "kb", vm.Keyboard, vm.Focus == SearchFocus.Keyboard, M, kbTop, SearchKeyCell, SearchKeyCell, SearchKeyGap);
            var kbBottom = kbTop + vm.Keyboard.Rows.Count * (SearchKeyCell + SearchKeyGap);
            f.Paragraph("search:hint", M, kbBottom + 24, SearchPanelW - M - 40, 2, "Searches every installed catalog add-on and your library as you type.", UiScale.Hint, UiTheme.TextFaint);
        }

        /// The focused-title panel shared by Search and Browse: name, year · rating · length, genres, a two-line
        /// synopsis and, once known, the availability in plain words ("HD · Spanish subs · Ready to play").
        private static void TitleInfoPanel(UiFrame f, string k, int px, int py, MetaSummary meta, MetaExtras e, string fallback, SourceEntry signature, bool checking)
        {
            const int pad = 24, iw = SearchInfoW - 2 * pad;
            var start = f.Nodes.Count;
            var sm = UiScale.LineH(UiScale.Small);
            f.Box(k + ":bg", px, py, SearchInfoW, UiScale.Bottom - py, UiTheme.Panel).R(UiTheme.RadiusPanel);
            f.Box(k + ":accent", px, py, SearchInfoW, 2, UiTheme.Line);
            var y = py + 16;
            Line(f, k + ":name", px + pad, y, iw, meta.Name, UiScale.Body, bold: true);
            y += UiScale.LineH(UiScale.Body);
            var length = meta.Type == MediaType.Series ? (e != null && e.Seasons > 0 ? e.Seasons + (e.Seasons == 1 ? " season" : " seasons") : null) : e?.Runtime;
            Line(f, k + ":facts", px + pad, y, iw, UiFormat.Facts(meta.ReleaseInfo, Rating(e), length).Replace("  ·  ", " · "), UiScale.Small, UiTheme.TextDim);
            y += sm;
            Line(f, k + ":genres", px + pad, y, iw, e == null ? "" : string.Join(" · ", e.Genres.Take(3)), UiScale.Small, UiTheme.TextDim);
            y += sm;
            f.Paragraph(k + ":description", px + pad, y, iw, 2, e?.Description ?? fallback ?? "", UiScale.Small, UiTheme.Text);
            y += 2 * sm + 8;
            if (signature != null)
                Line(f, k + ":signature", px + pad, y, iw, SourceBadges.Line(signature), UiScale.Small, UiTheme.Text, true);
            else if (checking)
                Line(f, k + ":signature", px + pad, y, iw, "Checking sources…", UiScale.Small, UiTheme.TextFaint);
            Animate(f, start, UiEnter.SlideUp, 120);
        }

        private const int StripGap = 16;

        /// Left edge of the first pill of a strip once it has glided so that pill `focus` is on screen.
        private static int StripStart(int x0, int count, Func<int, int> width, int focus)
        {
            if (focus < 0)
                return x0;
            var focusRight = x0;
            for (var i = 0; i <= Math.Min(focus, count - 1); i++)
                focusRight += width(i) + StripGap;
            return x0 - Math.Max(0, focusRight - StripGap - RightEdge);
        }

        /// A row of pills (search chips, browse filters) that glides left so the focused pill stays on screen.
        private static void Strip(UiFrame f, string prefix, int x0, int y, int count, Func<int, string> label, Func<int, int> width, int focus,
            Func<int, string> fill, Func<int, bool> strong)
        {
            const int gap = StripGap;
            var cx = StripStart(x0, count, width, focus);
            for (var i = 0; i < count; i++)
            {
                var w = width(i);
                var focused = i == focus;
                // Only whole pills inside the stage are drawn; the next one appears as the strip glides.
                if (cx >= x0 - 20 && cx + w <= UiFrame.Width)
                {
                    // Chips are neutral; a set one is amber (state), the focused one takes the light fill.
                    f.Box(prefix + ":" + i + ":bg", cx, y, w, UiScale.ChipH, focused ? UiTheme.FocusFill : fill(i)).R(UiTheme.RadiusPill).MoveMs = 200;
                    LineIn(f, prefix + ":" + i, cx + 16, y, UiScale.ChipH, w - 32, label(i), UiScale.Small, focused ? UiTheme.TextOnFocus : strong(i) ? UiTheme.Accent : UiTheme.TextDim, focused || strong(i), UiAlign.Center).MoveMs = 200;
                }
                cx += w + gap;
            }
        }

        // ---- Details ----

        private static void Details(UiFrame f, DetailsViewModel vm)
        {
            var meta = vm.Meta;
            var summary = meta?.Summary ?? vm.Preview;
            var e = vm.Extras;
            Billboard(f, vm.Id, meta?.Background ?? UiFormat.BackgroundFor(vm.Id), vm.TrailerId, vm.TrailerMuted, vm.Immersive ? UiFrame.Height : 760, vm.Immersive, vm.TrailerCompact);
            if (vm.Immersive)
            {
                // Trailer view: logo and one line; everything else slides away until a key is pressed.
                // Compact (a few seconds into the trailer): only a smaller logo at the bottom-left; the facts fade out.
                var compact = vm.TrailerCompact;
                int lw = 520, lh = 130;
                if (compact) { lw = (int)(lw * CompactLogoScale); lh = (int)(lh * CompactLogoScale); }
                var ly = compact ? UiScale.Bottom - 40 - lh : 740;
                var bigLogo = f.Image("details:logo", M, ly, lw, lh, e?.Logo);
                if (bigLogo != null) { bigLogo.Contain = true; bigLogo.AnchorBottom = true; bigLogo.MoveMs = CompactMoveMs; }
                else LineIn(f, "details:title", M, ly, lh, 1200, vm.Title, compact ? UiScale.Title : UiScale.Display, bold: true).MoveMs = CompactMoveMs;
                if (!compact)
                {
                    var factsNode = Line(f, "details:facts", M, bigLogo != null ? 876 : 886, 1400, UiFormat.Facts(summary?.ReleaseInfo, Rating(e), e?.Runtime), UiScale.Facts, UiTheme.TextDim);
                    factsNode.MoveMs = 450;
                    factsNode.ExitMs = 400;
                }
                return;
            }

            const int logoY = UiScale.SafeTop, logoH = 160;
            if (!string.IsNullOrEmpty(e?.Logo))
            {
                var logo = f.Image("details:logo", M, logoY, 640, logoH, e.Logo);
                logo.Contain = true;
                logo.AnchorBottom = true;
            }
            else
                LineIn(f, "details:title", M, logoY, logoH, 1300, vm.Title, UiScale.Display, bold: true);
            var y = logoY + logoH + (string.IsNullOrEmpty(e?.Logo) ? 20 : 10);
            if (meta == null)
            {
                Line(f, "details:loading", M, y, 1000, vm.Error == null ? "Loading…" : "", UiScale.Body, UiTheme.TextDim);
                return;
            }
            var series = vm.Type == MediaType.Series;
            var seasons = series ? meta.Videos.Where(v => v.Season > 0).Select(v => v.Season).Distinct().Count() : 0;
            var facts = UiFormat.Facts(series ? "Series" : "Movie", string.Join(", ", meta.Genres.Take(3)), summary.ReleaseInfo,
                series ? seasons + (seasons == 1 ? " season" : " seasons") : e?.Runtime, Rating(e), vm.InLibrary ? "In your library" : null);
            Line(f, "details:facts", M, y, 1400, facts, UiScale.Facts, UiTheme.TextDim);
            y += UiScale.LineH(UiScale.Facts) + 6;
            SignatureLine(f, "details:signature", M, y, vm.Signature, vm.SignatureEntry);
            y += UiScale.LineH(UiScale.Secondary) + 16;
            // Browsing episodes collapses the header (description and buttons fade out, the list slides up).
            var collapsed = series && vm.Focus == DetailsFocus.Episodes;
            if (collapsed)
            {
                DetailsTabs(f, vm, y);
                Episodes(f, vm, y + TabsH + 24);
                return;
            }
            var lines = series ? 2 : 3;
            var description = f.Paragraph("details:description", M, y, 1040, lines, meta.Description ?? e?.Description ?? "", UiScale.Body);
            description.ExitMs = 180;
            if (vm.TrailerId != null)
                description.Opacity = 0.75;   // the preview plays behind the page: the synopsis stays, a little dimmed
            y += lines * UiScale.LineH(UiScale.Body) + 8;
            // Movies name the cast; series spend that line on the episode list below.
            if (!series && e != null && e.Cast.Count > 0)
            {
                var cast = "Starring " + string.Join(", ", e.Cast.Take(3)) + (e.Cast.Count > 3 ? ", more" : "");
                Line(f, "details:cast", M, y, 1040, cast, UiScale.Small, UiTheme.TextFaint).ExitMs = 180;
                y += UiScale.LineH(UiScale.Small);
            }
            y += 12;

            var actionsY = y;
            // The button row glides left when the focused button would run past the right margin.
            var right = M;
            for (var i = 0; i <= Math.Min(vm.ActionIndex, vm.Actions.Count - 1); i++)
                right += ButtonWidth(vm.Actions[i].Label) + 20 + (vm.Actions[i].Action == DetailsAction.Primary ? PrimaryExtraW : 0);
            var x = M - Math.Max(0, right - 20 - RightEdge);
            for (var i = 0; i < vm.Actions.Count; i++)
            {
                var label = vm.Actions[i].Label;
                var primary = vm.Actions[i].Action == DetailsAction.Primary;
                var w = ButtonWidth(label) + (primary ? PrimaryExtraW : 0);   // the primary action is the widest button
                var focused = vm.Focus == DetailsFocus.Actions && i == vm.ActionIndex;
                if (primary)
                {
                    // The one primary action: amber at rest and in focus; focus adds the white ring (white is focus,
                    // amber is never a focus colour).
                    const int ring = UiScale.FocusRing;
                    if (focused)
                        f.Box("action:glow", x - ring, actionsY - ring, w + 2 * ring, UiScale.ButtonH + 2 * ring, UiTheme.Focus).R(UiTheme.RadiusPill).MoveMs = 160;
                    f.Box("action:Primary:bg", x, actionsY, w, UiScale.ButtonH, UiTheme.Accent).R(UiTheme.RadiusPill);
                    var labelW = Math.Min(w - 2 * UiScale.ButtonPadX - UiScale.GlyphSize - 12, UiScale.TextW(label, UiScale.Button));
                    var gx = x + (w - labelW - UiScale.GlyphSize - 12) / 2;
                    f.GlyphIn("action:Primary:glyph", UiGlyphs.Play, gx, actionsY, UiScale.GlyphSize, UiScale.ButtonH, 30, UiTheme.TextOnAccent);
                    LineIn(f, "action:Primary:label", gx + UiScale.GlyphSize + 12, actionsY, UiScale.ButtonH, labelW + 12, label, UiScale.Button, UiTheme.TextOnAccent, true);
                }
                else
                    Button(f, "action:" + vm.Actions[i].Action, x, actionsY, w, UiScale.ButtonH, label, focused, fill: UiTheme.Glass);
                if (primary && vm.PrimaryProgress > 0)
                    ProgressBar(f, "action:Primary:progress", x + 36, actionsY + UiScale.ButtonH - 14, w - 72, 6, vm.PrimaryProgress, UiTheme.TextOnAccent, "#0B0E1440");
                x += w + 20;
            }
            for (var n = 0; n < f.Nodes.Count; n++)
                if (f.Nodes[n].Key.StartsWith("action:", StringComparison.Ordinal))
                    f.Nodes[n].ExitMs = 180;
            if (!series)
                return;
            var tabsY = actionsY + UiScale.ButtonH + 20;
            DetailsTabs(f, vm, tabsY);
            Episodes(f, vm, tabsY + TabsH + 24);
        }

        private const int PrimaryExtraW = 72;

        /// Availability in plain words ("HD · Spanish subs · Ready to play"), or the checking state. Size, channels and
        /// swarm health are the source picker's business.
        private static void SignatureLine(UiFrame f, string key, int x, int y, SignatureState state, SourceEntry entry)
        {
            switch (state)
            {
                case SignatureState.Checking:
                    Line(f, key, x, y, 1000, "Checking sources…", UiScale.Secondary, UiTheme.TextFaint);
                    break;
                case SignatureState.Found:
                    Line(f, key, x, y, 1300, SourceBadges.Line(entry), UiScale.Secondary, UiTheme.TextDim);
                    break;
                case SignatureState.Unavailable:
                    Line(f, key, x, y, 1000, "No playable source found yet", UiScale.Secondary, UiTheme.Warn);
                    break;
            }
        }

        private const int TabsH = 64;

        private static void DetailsTabs(UiFrame f, DetailsViewModel vm, int y)
        {
            // Season tabs + subtle counter. The focused tab is filled; the selected season, when focus is elsewhere,
            // is bold with an underline, so the two never look alike.
            var x = M;
            for (var i = 0; i < vm.Seasons.Count; i++)
            {
                var label = vm.Seasons[i] <= 0 ? "Specials" : "Season " + vm.Seasons[i];
                var w = UiScale.TextW(label, UiScale.Secondary) + 56;
                var focused = vm.Focus == DetailsFocus.Seasons && i == vm.SeasonIndex;
                var current = i == vm.SeasonIndex;
                f.Box("season:" + i + ":bg", x, y, w, TabsH, focused ? UiTheme.FocusFill : "#FFFFFF00").R(UiTheme.RadiusPill).MoveMs = 240;
                if (current && !focused)
                    f.Box("season:" + i + ":here", x + 28, y + TabsH - 8, w - 56, 4, UiTheme.Accent).R(2);
                LineIn(f, "season:" + i, x, y, TabsH, w, label, UiScale.Secondary, focused ? UiTheme.TextOnFocus : current ? UiTheme.Text : UiTheme.TextDim, current, UiAlign.Center);
                x += w + 12;
            }
            if (vm.Episodes.Count > 0)
                LineIn(f, "episodes:count", RightEdge - 460, y, TabsH, 460, "Episode " + (vm.EpisodeIndex + 1) + " of " + vm.Episodes.Count, UiScale.Small, UiTheme.TextFaint, align: UiAlign.End);
        }

        private const int EpisodeCompactH = 96;
        private const int EpisodeExpandedH = 264;
        private const int EpisodeW = 1480;
        private const int StillW = 400;
        private const int StillH = 225;
        private const int ThumbW = 128;
        private const int ThumbH = 72;

        /// The previous episode peeks above the focused one, dimmed; the focused episode shows its still and synopsis.
        private static void Episodes(UiFrame f, DetailsViewModel vm, int listTop)
        {
            var focus = vm.EpisodeIndex;
            var anchor = focus > 0 ? listTop + EpisodeCompactH : listTop;
            var focusedList = vm.Focus == DetailsFocus.Episodes;
            for (var i = Math.Max(0, focus - 1); i < vm.Episodes.Count; i++)
            {
                int y;
                if (i == focus) y = anchor;
                else if (i < focus) y = anchor - (focus - i) * EpisodeCompactH;
                else y = anchor + EpisodeExpandedH + 10 + (i - focus - 1) * EpisodeCompactH;
                if (y > UiFrame.Height) break;
                var start = f.Nodes.Count;
                EpisodeRowNodes(f, vm, vm.Episodes[i], y, i == focus, focusedList && i == focus);
                if (i < focus)
                    for (var n = start; n < f.Nodes.Count; n++)
                        f.Nodes[n].Opacity = 0.45;   // the previous episode peeks above, dimmed
            }
            EdgeFades(f, "episodes:fade", 0, 0, UiFrame.Height, 90);
        }

        /// Every row: a thumbnail (with the progress bar on it), the watched tick beside the number, the title and the
        /// runtime. The focused row uses the app's white focus ring, adds the synopsis, and holds its two actions
        /// (Mark watched, + Queue) inside its right edge, where Right reaches them.
        private static void EpisodeRowNodes(UiFrame f, DetailsViewModel vm, EpisodeRow e, int y, bool expanded, bool focused)
        {
            var key = "episode:" + e.Video.Id;
            var start = f.Nodes.Count;
            var h = expanded ? EpisodeExpandedH : EpisodeCompactH - 8;
            var playFocused = focused && vm.EpisodeColumn == 0;
            if (playFocused)
            {
                const int ring = UiScale.FocusRing;
                f.Box(key + ":ring", M - ring, y - ring, EpisodeW + 2 * ring, h + 2 * ring, UiTheme.Focus).R(UiTheme.RadiusCard + ring).ExitMs = 150;
            }
            f.Box(key + ":bg", M, y, EpisodeW, h, expanded ? UiTheme.Panel : "#FFFFFF00").R(UiTheme.RadiusCard);
            var x = M + 24;
            var extras = vm.ExtrasFor(e.Video);
            // One width for the compact thumbnail and the expanded still (the same object grows; a second URL would
            // reload it on every focus move): w500 covers the 400 px expanded still instead of a 780 px file.
            var still = UiFormat.ArtAtWidth(extras?.Thumbnail ?? UiFormat.EpisodeStill(vm.Id, e.Video.Season, e.Video.Episode), StillW);
            int imageW = expanded ? StillW : ThumbW, imageH = expanded ? StillH : ThumbH;
            var imageY = expanded ? y + 20 : y + (h - ThumbH) / 2;
            f.Image(key + ":still", x, imageY, imageW, imageH, still, UiTheme.Placeholder);
            if (e.Progress > 0)
                ProgressBar(f, key + ":progress", x, imageY + imageH - (expanded ? 12 : 8), imageW, expanded ? 8 : 6, e.Progress);
            x += imageW + 24;

            // Row actions only on the focused episode, inside its right edge.
            var actionsW = 0;
            if (focused)
            {
                var actionStart = f.Nodes.Count;
                var watchedW = ButtonWidth("Mark unwatched", UiScale.Small);
                var queueW = vm.EpisodeColumnCount > 2 ? ButtonWidth("+ Queue", UiScale.Small) : 0;
                actionsW = watchedW + (queueW > 0 ? queueW + 16 : 0) + 24;
                var bx = M + EpisodeW - actionsW;
                Button(f, key + ":watched", bx, y + 16, watchedW, 68, e.Watched ? "Mark unwatched" : "Mark watched", vm.EpisodeColumn == 1, UiScale.Small, fill: "#FFFFFF26");
                if (queueW > 0)
                    Button(f, key + ":queue", bx + watchedW + 16, y + 16, queueW, 68, "+ Queue", vm.EpisodeColumn == 2, UiScale.Small, fill: "#FFFFFF26");
                Animate(f, actionStart, UiEnter.Fade, 120);
            }

            // The watched tick (or the resume glyph) sits right before the episode number.
            const int iconW = 40;
            var titleH = expanded ? UiScale.LineH(UiScale.Body) : h;
            var titleY = expanded ? y + 16 : y;
            if (e.Watched)
                UiIcons.Check(f, key + ":watched-icon", x, titleY + (titleH - iconW) / 2, iconW);
            else if (e.Progress > 0)
                UiIcons.Resume(f, key + ":resume-icon", x, titleY + (titleH - iconW) / 2, iconW);
            if (e.Watched || e.Progress > 0)
                x += iconW + 14;

            var runtime = vm.Extras?.Runtime;
            var runtimeW = expanded || string.IsNullOrEmpty(runtime) ? 0 : UiScale.TextW(runtime, UiScale.Small) + 24;
            var textW = M + EpisodeW - 24 - actionsW - runtimeW - x;
            var color = e.Released ? UiTheme.Text : UiTheme.TextDim;
            var name = e.Video.Episode + ".  " + (string.IsNullOrEmpty(e.Video.Title) ? "Episode " + e.Video.Episode : e.Video.Title);
            var bodyH = UiScale.LineH(UiScale.Body);
            if (expanded)
            {
                Line(f, key + ":title", x, y + 16, textW, name, UiScale.Body, color, true);
                var tx = M + 24 + StillW + 24;
                var air = e.Video.Released?.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                Line(f, key + ":facts", tx, y + 16 + bodyH + 4, M + EpisodeW - 40 - tx - actionsW, UiFormat.Facts(e.Marker, UiFormat.Episode(e.Video), air, runtime, e.Released ? null : "Upcoming"), UiScale.Small, UiTheme.TextDim);
                f.Paragraph(key + ":overview", tx, y + 16 + bodyH + 4 + UiScale.LineH(UiScale.Small) + 8, M + EpisodeW - 40 - tx, 3, extras?.Overview ?? "", UiScale.Small, UiTheme.Text);
            }
            else
            {
                LineIn(f, key + ":title", x, y, h, textW, e.Marker != null ? name + "  ·  " + e.Marker : name, UiScale.Body, color, e.IsCurrent || e.IsPrimary);
                if (runtimeW > 0)
                    LineIn(f, key + ":runtime", M + EpisodeW - 24 - runtimeW, y, h, runtimeW, runtime, UiScale.Small, UiTheme.TextFaint, align: UiAlign.End);
            }
            for (var n = start; n < f.Nodes.Count; n++)
                f.Nodes[n].MoveMs = 220;
        }

        // ---- Browse ----

        private static void Browse(UiFrame f, BrowseViewModel vm)
        {
            if (vm.Service != null)
                f.Background = vm.Service.PageInk;   // inside a service the page keeps its tint
            // Caption space is reserved under every poster (clear of the focused poster's growth), so nothing shifts
            // when focus moves and a caption never touches the next row.
            const int gridTop = 304, cw = UiScale.PosterW, ch = UiScale.PosterH, captionGap = 16 + (int)(UiScale.PosterH * UiScale.PosterGrow) / 2, rowH = ch + 132;
            const int gap = (CW - BrowseViewModel.Columns * cw) / (BrowseViewModel.Columns - 1);   // edge to edge of the safe area
            var results = vm.Results;
            var focusRow = vm.Index < 0 ? 0 : vm.Index / BrowseViewModel.Columns;
            for (var i = 0; i < results.Count; i++)
            {
                var row = i / BrowseViewModel.Columns;
                var delta = row - focusRow;
                if (delta < -1 || delta > 2) continue;
                var y = delta >= 0 ? gridTop + delta * rowH : gridTop - rowH - 110;
                var x = M + (i % BrowseViewModel.Columns) * (cw + gap);
                var item = results[i];
                var focused = i == vm.Index;
                var key = "browse:" + item.Meta.Id;
                var start = f.Nodes.Count;
                Poster(f, key, x, y, cw, ch, item.Meta.Poster, item.Meta.Name, focused, 0, vm.IsChecking(item.Meta.Id) && !focused ? "Checking" : null);
                // The focused row's captions give way to the info panel, which names the focused title.
                if (!(vm.Index >= 0 && delta == 0))
                {
                    var captionY = y + ch + captionGap;
                    Line(f, key + ":name", x, captionY, cw, item.Meta.Name, UiScale.Small, UiTheme.TextDim);
                    Line(f, key + ":facts", x, captionY + UiScale.LineH(UiScale.Small), cw, BrowseFacts(item), UiScale.Hint, UiTheme.TextFaint);
                }
                for (var n = start; n < f.Nodes.Count; n++)
                    f.Nodes[n].MoveMs = 240;
            }
            EdgeFades(f, "browse:fade", 0, 0, UiFrame.Height, 100);
            HeaderBand(f, "browse", h: 276);
            if (vm.Service != null)
                ServiceGlow(f, vm.Service, 276);
            if (vm.Loading)
                Line(f, "browse:loading", M, gridTop + 40, CW, "Loading titles…", UiScale.Body, UiTheme.TextDim);

            // The same focused-title panel as Search, under the focused poster.
            var focusedItem = vm.FocusedItem;
            if (focusedItem != null)
            {
                var col = vm.Index % BrowseViewModel.Columns;
                var px = Math.Min(M + col * (cw + gap) - 12, RightEdge - SearchInfoW);
                TitleInfoPanel(f, "browse:card", px, gridTop + ch + 28, focusedItem.Meta, focusedItem.Extras, "", vm.SignatureFor(focusedItem.Meta.Id), vm.IsChecking(focusedItem.Meta.Id));
            }

            Line(f, "browse:title", M, UiScale.SafeTop, 800, vm.Heading, UiScale.Title, bold: true);
            LineIn(f, "browse:count", RightEdge - 560, UiScale.SafeTop, UiScale.LineH(UiScale.Title), 560, BrowseCount(vm, results.Count), UiScale.Small, UiTheme.TextFaint, align: UiAlign.End);
            var filters = BrowseViewModel.Filters;
            var pills = filters.Count + (vm.AnyFilterSet ? 1 : 0);
            var filterY = UiScale.SafeTop + UiScale.LineH(UiScale.Title) + 18;
            string PillLabel(int i) => i < filters.Count ? vm.Value(filters[i]) : "Clear filters";
            int PillW(int i) => Math.Max(150, UiScale.TextW(PillLabel(i), UiScale.Small) + 48);
            var pillFocus = vm.Index < 0 ? Math.Min(vm.FilterIndex, pills - 1) : -1;
            Strip(f, "filter", M, filterY, pills, PillLabel, PillW, pillFocus,
                i => UiTheme.CardPanel, i => i < filters.Count && vm.IsSet(filters[i]));
            if (vm.Index < 0 && !vm.DropdownOpen)
                Line(f, "browse:hint", M, filterY + UiScale.ChipH + 12, 1400, vm.ClearFocused ? "OK: clear every filter  ·  Down: titles  ·  Back: Home" : "OK: choose  ·  Down: titles  ·  Back: Home", UiScale.Hint, UiTheme.TextFaint);

            if (vm.DropdownOpen)
            {
                // The options of the focused filter, under its pill: the current one ticked, the highlighted one filled.
                var filter = filters[vm.FilterIndex];
                var options = BrowseViewModel.Options[filter];
                const int optionH = 64, maxVisible = 11, padY = 12;
                var x = StripStart(M, pills, PillW, pillFocus);
                for (var i = 0; i < vm.FilterIndex; i++)
                    x += PillW(i) + StripGap;
                var w = Math.Max(PillW(vm.FilterIndex), options.Max(o => UiScale.TextW(o, UiScale.Secondary)) + 120);
                x = Math.Max(M, Math.Min(x, RightEdge - w));
                var visible = Math.Min(maxVisible, options.Length);
                var first = Window(vm.DropdownIndex, options.Length, visible, visible / 2);
                var top = filterY + UiScale.ChipH + 8;
                var start = f.Nodes.Count;
                f.Box("dropdown:scrim", 0, top - 8, UiFrame.Width, UiFrame.Height - top + 8, UiTheme.ScrimLight);
                f.Shadow("dropdown:shadow", x, top, w, visible * optionH + 2 * padY, UiTheme.RadiusPanel);
                f.Box("dropdown:bg", x, top, w, visible * optionH + 2 * padY, UiTheme.Panel).R(UiTheme.RadiusPanel);
                for (var i = first; i < first + visible; i++)
                {
                    var oy = top + padY + (i - first) * optionH;
                    var focused = i == vm.DropdownIndex;
                    var current = i == vm.Selected(filter);
                    f.Box("dropdown:" + i + ":bg", x + 8, oy, w - 16, optionH - 4, focused ? UiTheme.Focus : "#00000000").R(UiTheme.RadiusCard);
                    if (current)
                        UiIcons.Tick(f, "dropdown:" + i + ":tick", x + 26, oy + (optionH - 4 - 28) / 2, 28, focused ? UiTheme.TextOnFocus : UiTheme.Text);
                    LineIn(f, "dropdown:" + i, x + 72, oy, optionH - 4, w - 96, options[i], UiScale.Secondary, focused ? UiTheme.TextOnFocus : UiTheme.Text, current);
                }
                Animate(f, start, UiEnter.Fade, 120);
            }
        }

        /// Caption facts that always fit a poster's width: year and rating; a movie adds its length. Seasons (never an
        /// episode's runtime) are in the focused title's panel.
        internal static string BrowseFacts(BrowseItem item)
        {
            var e = item.Extras;
            return UiFormat.Facts(item.Meta.ReleaseInfo, Rating(e), item.Meta.Type == MediaType.Series ? null : e?.Runtime).Replace("  ·  ", " · ");
        }

        /// The grid pages in more titles as focus nears its end, so the count is open-ended until the catalog runs out.
        /// With focus in the grid it says where the viewer is ("Title 12 of 100+").
        internal static string BrowseCount(BrowseViewModel vm, int count)
        {
            if (vm.Loading)
                return "Loading…";
            var total = vm.Exhausted ? count.ToString(CultureInfo.InvariantCulture) : count + "+";
            var text = vm.Index >= 0 && count > 0 ? "Title " + (vm.Index + 1) + " of " + total : total + " titles";
            return vm.LoadingMore ? text + " · loading…" : text;
        }

        // ---- Source picker ----

        /// Opaque header band: lists scroll underneath it, so nothing shows through the title area.
        private const int HeaderBandH = 244;

        private static void HeaderBand(UiFrame f, string key, int x = 0, int w = UiFrame.Width, int h = HeaderBandH)
        {
            f.Box(key + ":band", x, 0, w, h, Ink(f)).Enter = UiEnter.None;
            f.VerticalGradient(key + ":band-fade", x, h, w, 40, Ink(f), 255, 0);
        }

        private const int SourceChipsY = 176;

        private static void Sources(UiFrame f, SourcePickerViewModel vm)
        {
            if (!vm.Loading && (vm.Entries.Count > 0 || vm.HasShowAllRow))
                SourceList(f, vm);
            HeaderBand(f, "sources", h: SourceChipsY + UiScale.ChipH + 12);

            Line(f, "sources:title", M, UiScale.SafeTop, 900, "Choose a source", UiScale.Title, bold: true);
            LineIn(f, "sources:subtitle", M + 900, UiScale.SafeTop, UiScale.LineH(UiScale.Title), CW - 900, vm.Title + (vm.Switching ? "  ·  switches the current playback" : ""), UiScale.Secondary, UiTheme.TextDim, align: UiAlign.End);

            const int contentY = 268;
            if (vm.Loading)
            {
                Line(f, "sources:loading", M, contentY, CW, "Asking your add-ons for sources…", UiScale.Body, UiTheme.TextDim);
                return;
            }
            // Chip bar: quality chips, then the unplayable toggle at the right. The selected quality stays marked
            // while focus is on the list (Left/Right there switch it directly).
            var qualities = vm.Qualities;
            var x = M;
            for (var i = 0; i < qualities.Count; i++)
            {
                var label = qualities[i] == SourcePickerViewModel.AllQualities ? "All" : qualities[i];
                var w = Math.Max(120, UiScale.TextW(label, UiScale.Small) + 56);
                var focused = vm.Index < 0 && vm.ToolbarIndex == i;
                var selected = vm.QualityIndex == i;
                // Selected is amber (state); focus is the light fill. A chip can be both: then it is focused.
                f.Box("sources:q:" + i + ":bg", x, SourceChipsY, w, UiScale.ChipH, focused ? UiTheme.FocusFill : UiTheme.CardPanel).R(UiTheme.RadiusPill);
                LineIn(f, "sources:q:" + i, x, SourceChipsY, UiScale.ChipH, w, label, UiScale.Small, focused ? UiTheme.TextOnFocus : selected ? UiTheme.Accent : UiTheme.TextDim, focused || selected, UiAlign.Center);
                x += w + 12;
            }
            if (vm.HiddenCount > 0)
            {
                var hiddenLabel = (vm.ShowHidden ? "Hide unplayable" : "Show unplayable") + " (" + vm.HiddenCount + ")";
                var hw = ButtonWidth(hiddenLabel, UiScale.Small);
                Button(f, "sources:hidden", RightEdge - hw, SourceChipsY, hw, UiScale.ChipH, hiddenLabel, vm.Index < 0 && vm.ToolbarIndex >= qualities.Count, UiScale.Small, fill: UiTheme.CardPanel);
                x += 12;
            }
            var entries = vm.Entries;
            if (entries.Count == 0 && !vm.HasShowAllRow)
            {
                Line(f, "sources:empty", M, contentY, CW, vm.HiddenCount > 0 ? "No playable sources. Show unplayable to see why." : "No sources found", UiScale.Body, UiTheme.TextDim);
                return;
            }
            var count = vm.ShowAllFocused || vm.Index < 0 ? entries.Count + (vm.MoreCount > 0 ? " of " + (entries.Count + vm.MoreCount) : "") + " sources"
                : (vm.Index + 1) + " of " + entries.Count + (vm.MoreCount > 0 ? " shown" : "");
            LineIn(f, "sources:count", x + 12, SourceChipsY, UiScale.ChipH, 520, count + "  ·  Left/Right: quality", UiScale.Hint, UiTheme.TextFaint);
        }

        private const int SourceRowH = 88;          // one line per source
        private const int SourceFocusedH = 148;     // the focused row adds its file name (and why it is the best match)

        private static void SourceList(UiFrame f, SourcePickerViewModel vm)
        {
            var entries = vm.Entries;
            const int listTop = SourceChipsY + UiScale.ChipH + 28, gap = 10;
            var rows = entries.Count + (vm.HasShowAllRow ? 1 : 0);
            var focus = Math.Max(0, vm.Index);
            var best = vm.BestEntry;
            int Height(int i) => i == vm.Index && i < entries.Count ? SourceFocusedH : SourceRowH;
            // The focused row sits third from the top once the list scrolls, so two rows above it stay visible.
            var first = Math.Max(0, focus - 2);
            var y = listTop;
            for (var i = first; i < rows; i++)
            {
                if (y > UiFrame.Height) break;
                var h = Height(i);
                if (i < entries.Count)
                    SourceRow(f, vm, entries[i], i, y, h, vm.Index == i, entries[i] == best);
                else
                {
                    var focused = vm.Index == i;
                    f.Box("source:all:bg", M, y, CW, h, focused ? UiTheme.FocusRow : "#FFFFFF0A").R(UiTheme.RadiusRow).MoveMs = 200;
                    LineIn(f, "source:all", M + 24, y, h, CW - 48, vm.ShowAllLabel + "  ›", UiScale.Secondary, UiTheme.Text, true).MoveMs = 200;
                }
                y += h + gap;
            }
            EdgeFades(f, "sources:fade", 0, 0, UiFrame.Height, 100);
        }

        /// Quality labels are neutral; 4K is only a little brighter. Colour is kept for state (best match, health).
        private static string QualityColor(string quality) => quality == "4K" ? "#FFFFFF40" : UiTheme.Glass;

        /// One line: quality badge, size, health dots with the seeder count, then only the facts this source has, as
        /// chips (HDR, codec, channels, audio and subtitle languages). The focused row gets the soft light fill of every
        /// focused list row and adds the file name; the best match carries the only amber element on the screen.
        private static void SourceRow(UiFrame f, SourcePickerViewModel vm, SourceEntry entry, int index, int y, int h, bool focused, bool isBest)
        {
            var c = entry.Candidate;
            var facts = entry.Facts ?? SourceFacts.Parse(c, vm.IsAnime);
            var key = "source:" + (c.Identity ?? index.ToString(CultureInfo.InvariantCulture));
            var start = f.Nodes.Count;
            f.Box(key + ":bg", M, y, CW, h, focused ? UiTheme.FocusRow : "#FFFFFF0A").R(UiTheme.RadiusRow);
            var text = UiTheme.Text;
            var dim = focused ? UiTheme.Text : UiTheme.TextDim;

            const int lineH = SourceRowH, badgeH = 56;
            var x = M + 24;
            f.Box(key + ":q:bg", x, y + (lineH - badgeH) / 2, 140, badgeH, QualityColor(facts.Quality)).R(UiTheme.RadiusBadge);
            LineIn(f, key + ":q", x, y, lineH, 140, facts.Quality, UiScale.Secondary, bold: true, align: UiAlign.Center);
            x += 140 + 28;
            LineIn(f, key + ":size", x, y, lineH, 150, UiFormat.Size(c.SizeBytes) ?? "", UiScale.Secondary, text, true);
            x += 150 + 16;
            UiIcons.Health(f, key + ":health", x, y + (lineH - UiIcons.HealthH) / 2, c.HealthBand);
            LineIn(f, key + ":seeders", x + UiIcons.HealthW + 10, y, lineH, 110, c.Seeders != null ? c.Seeders.ToString() : c.InfoHash == null ? "direct" : "", UiScale.Small, dim);
            x += UiIcons.HealthW + 10 + 110 + 16;

            // Chips for what exists; nothing is drawn for what the release does not say.
            var chips = new List<string>();
            if (facts.Hdr != null) chips.Add(facts.Hdr);
            if (facts.VideoCodec != null) chips.Add(facts.VideoCodec);
            var audio = UiFormat.Facts(facts.AudioCodec, facts.Channels).Replace("  ·  ", " ");
            if (audio.Length > 0) chips.Add(audio);
            if (facts.AudioLanguages.Count > 0) chips.Add(string.Join("/", facts.AudioLanguages.Take(3)) + " audio");
            if (facts.SubtitleLanguages.Count > 0) chips.Add(string.Join("/", facts.SubtitleLanguages.Take(3)) + " subs");
            if (vm.ShowAddon && !string.IsNullOrEmpty(entry.AddonName)) chips.Add(entry.AddonName);
            const int bestW = 196;
            var chipsRight = RightEdge - 24 - (isBest ? bestW + 16 : 0);
            for (var i = 0; i < chips.Count; i++)
            {
                var w = 28 + UiScale.TextW(chips[i], UiScale.Hint);
                if (x + w > chipsRight) break;
                f.Box(key + ":chip:" + i + ":bg", x, y + (lineH - UiScale.BadgeH) / 2, w, UiScale.BadgeH, UiTheme.Glass).R(UiTheme.RadiusBadge);
                LineIn(f, key + ":chip:" + i, x, y, lineH, w, chips[i], UiScale.Hint, text, true, UiAlign.Center);
                x += w + 10;
            }
            if (isBest)
            {
                f.Box(key + ":best:bg", RightEdge - 24 - bestW, y + (lineH - 44) / 2, bestW, 44, UiTheme.Accent).R(UiTheme.RadiusBadge);
                LineIn(f, key + ":best", RightEdge - 24 - bestW, y, lineH, bestW, "Best match", UiScale.Hint, UiTheme.TextOnAccent, true, UiAlign.Center);
            }

            if (focused || !entry.Playable)
            {
                var file = UiFormat.MiddleEllipsis(c.Filename ?? SourceFacts.FirstLine(c.Title), 96);
                var lineY = y + lineH - 14;
                var hintH = UiScale.LineH(UiScale.Hint);
                if (!entry.Playable)
                    Line(f, key + ":reject", M + 24, focused ? lineY : y + lineH - hintH - 2, CW - 48, "Can't play on this TV: " + entry.RejectReason + (focused ? "   ·   " + file : ""), UiScale.Hint, UiTheme.Bad);
                else
                    Line(f, key + ":file", M + 24, lineY, CW - 48, UiFormat.Facts(isBest ? "Why: " + SourcePickerViewModel.WhyBest(entry, vm.IsAnime) : null, facts.ReleaseType, file).Replace("  ·  ", " · "), UiScale.Hint, dim);
            }

            for (var n = start; n < f.Nodes.Count; n++)
            {
                f.Nodes[n].MoveMs = 200;
                if (!entry.Playable && !focused)
                    f.Nodes[n].Opacity = 0.55;
            }
        }
    }
}
