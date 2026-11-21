using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace Ru7100.Core.Ui
{
    /// Plain-text snapshot of the UI for tests and quick looks: `>` marks focus, [brackets] mark buttons.
    public static class UiText
    {
        public static string RenderText(TvShell shell)
        {
            var b = new StringBuilder();
            var screen = shell.Current;
            b.Append("== ").Append(screen?.Kind.ToString() ?? "(empty)").Append(": ").Append(screen?.Title).Append(" ==\n");
            switch (screen)
            {
                case HomeViewModel home:
                    if (home.HeroTile != null)
                        b.Append("[billboard] ").Append(home.HeroTile.Title).Append(home.TrailerId != null ? " [trailer " + home.TrailerId + (home.TrailerMuted ? " muted]" : " sound]") : "").Append('\n');
                    if (home.PoppedTile != null)
                        b.Append("[card] ").Append(home.PoppedTile.Title).Append(home.CardTrailerPlaying ? " [trailer " + home.CardTrailerTitleId + (home.TrailerMuted ? " muted]" : " sound]") : "").Append('\n');
                    var rows = home.Rows;
                    for (var r = 0; r < rows.Count; r++)
                    {
                        var row = rows[r];
                        var focusedRow = r == home.FocusedRowIndex;
                        b.Append(focusedRow ? "> " : "  ").Append(row.IsMenu ? "[menu]" : row.Title).Append(": ");
                        b.Append(string.Join(" | ", row.Items.Select((t, i) => (focusedRow && i == home.FocusedColumn(row) ? ">" : "") + t.Title + (t.Progress > 0 ? " (" + Math.Round(t.Progress * 100) + "%)" : "") + (t.Badge != null ? " [" + t.Badge + "]" : ""))));
                        b.Append('\n');
                    }
                    break;
                case SearchViewModel search:
                    b.Append("Query: \"").Append(search.Query).Append("\" layout=").Append(search.Keyboard.Layout).Append('\n');
                    Keyboard(b, search.Keyboard, search.Focus == SearchFocus.Keyboard);
                    if (search.Suggestions.Count > 0)
                        b.Append(search.Focus == SearchFocus.Suggestions ? "> " : "  ").Append(search.ShowingPopular ? "Recent: " : "Refine: ")
                            .Append(string.Join(" | ", search.Suggestions.Select((x, i) => (search.Focus == SearchFocus.Suggestions && i == search.SuggestionIndex ? ">" : "") + x))).Append('\n');
                    b.Append(search.ShowingPopular ? "Popular now:" : "Results:").Append(search.Searching ? " (searching)" : "").Append('\n');
                    for (var i = 0; i < search.Items.Count; i++)
                        b.Append(search.Focus == SearchFocus.Results && i == search.ResultIndex ? "> " : "  ").Append(search.Items[i].Meta.Name)
                            .Append(search.Items[i].InLibrary ? " [in library]" : "").Append('\n');
                    break;
                case TextEntryViewModel entry:
                    b.Append(entry.Prompt).Append(": \"").Append(entry.Keyboard.Text).Append("\"").Append(entry.Busy ? " (working)" : "").Append('\n');
                    Keyboard(b, entry.Keyboard, true);
                    break;
                case DetailsViewModel details:
                    if (!details.Loaded)
                    {
                        b.Append("Loading\n");
                        break;
                    }
                    if (details.TrailerId != null)
                        b.Append("[trailer ").Append(details.TrailerId).Append(details.TrailerMuted ? " muted]\n" : " sound]\n");
                    b.Append(details.Focus == DetailsFocus.Actions ? "> " : "  ");
                    b.Append(string.Join(" ", details.Actions.Select((a, i) => (details.Focus == DetailsFocus.Actions && i == details.ActionIndex ? ">" : "") + "[" + a.Label + "]"))).Append('\n');
                    if (details.Seasons.Count > 0)
                    {
                        b.Append(details.Focus == DetailsFocus.Seasons ? "> " : "  ");
                        b.Append(string.Join(" ", details.Seasons.Select((s, i) => (i == details.SeasonIndex ? "*" : "") + "S" + s))).Append('\n');
                    }
                    for (var i = 0; i < details.Episodes.Count; i++)
                    {
                        var e = details.Episodes[i];
                        var focused = details.Focus == DetailsFocus.Episodes && i == details.EpisodeIndex;
                        b.Append(focused ? "> " : "  ").Append(e.Watched ? "[x] " : "[ ] ").Append(UiFormat.Episode(e.Video)).Append(' ').Append(e.Video.Title);
                        if (e.Progress > 0) b.Append(" (").Append(Math.Round(e.Progress * 100)).Append("%)");
                        if (!e.Released) b.Append(" (upcoming)");
                        if (focused) b.Append("  col=").Append(new[] { "play", "watched", "queue" }[details.EpisodeColumn]);
                        b.Append('\n');
                    }
                    break;
                case SourcePickerViewModel picker:
                    if (picker.Loading) b.Append("Loading\n");
                    var qualities = picker.Qualities;
                    b.Append(picker.Index < 0 ? "> " : "  ")
                        .Append(string.Join(" ", qualities.Select((q, i) => (picker.Index < 0 && picker.ToolbarIndex == i ? ">" : "") + "[" + (i == picker.QualityIndex ? "*" : "") + q + "]")))
                        .Append(' ').Append(picker.Index < 0 && picker.ToolbarIndex >= qualities.Count ? ">" : "").Append("[").Append(picker.ShowHidden ? "Hide" : "Show").Append(" unplayable (").Append(picker.HiddenCount).Append(")]\n");
                    var entries = picker.Entries;
                    var bestEntry = picker.BestEntry;
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var c = entries[i].Candidate;
                        b.Append(i == picker.Index ? "> " : "  ").Append(SourceFacts.FirstLine(c.Name)).Append(" · ").Append(string.Join(" · ", SourcePickerViewModel.Badges(c, picker.IsAnime)));
                        if (entries[i] == bestEntry) b.Append(" [BEST MATCH: ").Append(SourcePickerViewModel.WhyBest(entries[i], picker.IsAnime)).Append(']');
                        if (!entries[i].Playable) b.Append(" [unplayable: ").Append(entries[i].RejectReason).Append(']');
                        b.Append('\n');
                    }
                    if (picker.HasShowAllRow)
                        b.Append(picker.ShowAllFocused ? "> " : "  ").Append('[').Append(picker.ShowAllLabel).Append("]\n");
                    break;
                case PlayerViewModel player:
                    Player(b, player);
                    break;
                case BrowseViewModel browse:
                    b.Append(browse.Index < 0 ? "> " : "  ");
                    b.Append(string.Join(" ", BrowseViewModel.Filters.Select((x, i) => (browse.Index < 0 && i == browse.FilterIndex ? ">" : "") + "[" + browse.Value(x) + "]")));
                    if (browse.AnyFilterSet) b.Append(browse.ClearFocused ? " >" : " ").Append("[Clear filters]");
                    b.Append('\n');
                    if (browse.DropdownOpen)
                    {
                        var filter = BrowseViewModel.Filters[browse.FilterIndex];
                        var options = BrowseViewModel.Options[filter];
                        b.Append("[dropdown] ").Append(string.Join(" | ", options.Select((o, i) => (i == browse.DropdownIndex ? ">" : "") + (i == browse.Selected(filter) ? "*" : "") + o))).Append('\n');
                    }
                    if (browse.Loading) b.Append("Loading\n");
                    var found = browse.Results;
                    if (!browse.Loading) b.Append(UiLayout.BrowseCount(browse, found.Count)).Append('\n');
                    for (var i = 0; i < found.Count && i < 24; i++)
                        b.Append(i == browse.Index ? "> " : "  ").Append(found[i].Meta.Name).Append(browse.IsChecking(found[i].Meta.Id) ? " [checking]" : "").Append('\n');
                    break;
                case SignInViewModel signIn:
                    b.Append("Open ").Append(signIn.Link ?? "…").Append(" and enter ").Append(signIn.Code ?? "…").Append('\n');
                    b.Append(string.Join(" ", signIn.Buttons.Select((x, i) => (i == signIn.ButtonIndex ? ">" : "") + "[" + x + "]"))).Append('\n');
                    break;
                case AddonsViewModel addons:
                    b.Append(addons.Index == 0 ? "> " : "  ").Append("[+ Install from URL]\n");
                    for (var i = 0; i < addons.Addons.Count; i++)
                        b.Append(addons.Index == i + 1 ? "> " : "  ").Append(addons.Addons[i].Name).Append(" · ").Append(addons.Addons[i].TransportUrl).Append('\n');
                    break;
                case QueueViewModel queue:
                    b.Append(queue.Index < 0 ? "> " : "  ").Append(string.Join(" ", queue.Buttons.Select((x, i) => (queue.Index < 0 && i == queue.ButtonIndex ? ">" : "") + "[" + queue.ButtonLabel(x) + "]"))).Append('\n');
                    for (var i = 0; i < queue.Queue.Items.Count; i++)
                    {
                        var item = queue.Queue.Items[i];
                        b.Append(i == queue.Index ? "> " : "  ").Append(i == queue.Queue.CurrentIndex ? "* " : "  ").Append(item.Name).Append(' ').Append(UiFormat.Episode(item.Season, item.Episode)).Append('\n');
                    }
                    break;
            }
            if (screen?.Error != null)
                b.Append("! ").Append(screen.Error).Append('\n');
            if (shell.Dialog != null)
            {
                var d = shell.Dialog;
                b.Append("[dialog] ").Append(d.Title).Append(": ");
                b.Append(string.Join(" ", d.Buttons.Select((x, i) => (i == d.Focus ? ">" : "") + "[" + x.Label + "]"))).Append('\n');
            }
            if (shell.Toast != null)
                b.Append("[toast] ").Append(shell.Toast).Append('\n');
            return b.ToString();
        }

        private static void Keyboard(StringBuilder b, OnScreenKeyboard kb, bool focused)
        {
            var rows = kb.Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                b.Append("  ");
                for (var c = 0; c < rows[r].Count; c++)
                {
                    var on = focused && r == kb.Row && c == Math.Min(kb.Column, rows[r].Count - 1);
                    b.Append(on ? ">" : " ").Append(rows[r][c].Label).Append(on ? "<" : " ");
                }
                b.Append('\n');
            }
        }

        private static void Player(StringBuilder b, PlayerViewModel p)
        {
            var s = p.Snapshot;
            if (p.Card != PlayerCard.None)
            {
                b.Append(p.Card == PlayerCard.Failed ? "[failed] " + p.CardMessage : "[ended]").Append('\n');
                if (!string.IsNullOrEmpty(p.CardDetail))
                    b.Append("  ").Append(p.CardDetail).Append('\n');
                b.Append("> ").Append(string.Join(" ", p.CardButtons.Select((x, i) => (i == p.CardIndex ? ">" : "") + "[" + x.Label + "]"))).Append('\n');
                return;
            }
            b.Append(s.State).Append(' ').Append(UiFormat.Clock(p.DisplayPositionMs)).Append(" / ").Append(UiFormat.Clock(s.DurationMs));
            if (p.PendingSeekMs != null) b.Append(" (seeking)");
            b.Append('\n');
            if (p.Loading)
            {
                b.Append("[start] ").Append(s.StatusText ?? UiLayout.StartSteps[Math.Max(1, Math.Min(3, s.StartStep)) - 1]).Append('\n');
                if (p.StartHintVisible)
                    b.Append(p.CanChooseSource ? "Back: cancel · OK: choose a source\n" : "Back: cancel\n");
                return;
            }
            if (!string.IsNullOrEmpty(p.SubtitleText))
                b.Append("subtitle: ").Append(p.SubtitleText.Replace("\n", " / ")).Append(" @").Append(p.SubtitleBottom).Append("px\n");
            if (p.PauseStripVisible)
                b.Append("[paused] ").Append(s.Title).Append('\n');
            else if (p.SeekIndicatorVisible)
                b.Append("[seek bar]\n");
            if (!p.OverlayVisible)
            {
                b.Append("overlay: hidden\n");
                if (p.CreditsCardVisible && p.NextVideo != null)
                    b.Append("[credits] Next episode").Append(p.CreditsAutoplay ? " in " + (int)Math.Ceiling(p.CreditsRemainingMs / 1000.0) + " s" : "").Append(": ").Append(UiFormat.EpisodeColon(p.NextVideo)).Append('\n');
                return;
            }
            if (p.Panel == PlayerPanel.Tracks)
            {
                b.Append("overlay: audio & subtitles\n");
                var audio = p.AudioChoices;
                var subs = p.SubtitleChoices;
                b.Append("Audio:\n");
                for (var i = 0; i < audio.Count; i++)
                    b.Append(p.TracksColumn == 0 && p.AudioIndex == i ? "> " : "  ").Append(audio[i].Selected ? "(x) " : "( ) ").Append(audio[i].Label).Append('\n');
                b.Append("Subtitles:\n");
                for (var i = 0; i < subs.Count; i++)
                {
                    if (subs[i].Divider) b.Append("  ----\n");
                    b.Append(p.TracksColumn == 1 && p.SubtitleIndex == i ? "> " : "  ").Append(subs[i].IsSettings ? "" : subs[i].Selected ? "(x) " : "( ) ").Append(subs[i].Label).Append('\n');
                }
                return;
            }
            if (p.Panel == PlayerPanel.Episodes)
            {
                b.Append("overlay: episodes · season ").Append(p.EpisodesSeason).Append('\n');
                var list = p.PanelEpisodes;
                var current = p.Snapshot.Video?.Id;
                for (var i = 0; i < list.Count; i++)
                    b.Append(i == p.EpisodesIndex ? "> " : "  ").Append(UiFormat.EpisodeColon(list[i])).Append(' ').Append(list[i].Title).Append(list[i].Id == current ? " [now playing]" : "").Append('\n');
                return;
            }
            if (p.Panel == PlayerPanel.Style)
            {
                b.Append("overlay: subtitle settings\n");
                foreach (var row in PlayerViewModel.StyleRows)
                    b.Append(p.FocusedStyleRow == row ? "> " : "  ").Append(PlayerViewModel.StyleLabel(row)).Append(": ").Append(p.StyleValue(row)).Append('\n');
                return;
            }
            if (p.Panel == PlayerPanel.More)
            {
                b.Append("overlay: more\n");
                foreach (var row in p.MoreRows)
                    b.Append(p.FocusedMoreRow == row ? "> " : "  ").Append(PlayerViewModel.MoreLabel(row)).Append(": ").Append(p.MoreValue(row)).Append('\n');
                return;
            }
            b.Append("overlay: open\n");
            if (p.ExitHintVisible)
                b.Append("Press Back again to exit\n");
            b.Append(p.FocusedRow == OverlayRow.Transport ? "  " : "> ")
                .Append(string.Join(" ", p.Actions.Select(row => (p.FocusedRow == row ? ">" : "") + "[" + PlayerViewModel.RowLabel(row) + "]"))).Append('\n');
            b.Append(p.FocusedRow == OverlayRow.Transport ? "> " : "  ").Append(PlayerViewModel.RowLabel(OverlayRow.Transport)).Append(": ").Append(p.RowValue(OverlayRow.Transport)).Append('\n');
        }
    }

    /// Renders a UiFrame as absolutely positioned HTML on a 1920x1080 stage (browser preview).
    public static class UiHtml
    {
        /// Browser side of ImageTrim: logos (Contain images) are fitted by their opaque part, so a logo padded on a
        /// transparent canvas (metahub: 800x310) sits on its box's left and bottom edges like on the TV. The scan
        /// needs CORS; a host without it is reloaded without CORS and fitted whole. Shared by the live preview page
        /// and the static snapshot.
        public const string TrimScript = @"<script>
const ruTrim = new Map();
function ruBounds(img) {
  const src = img.currentSrc || img.src;
  if (ruTrim.has(src)) return ruTrim.get(src);
  let b = null;
  try {
    const w = img.naturalWidth, h = img.naturalHeight, c = document.createElement('canvas');
    c.width = w; c.height = h;
    const g = c.getContext('2d'); g.drawImage(img, 0, 0);
    const d = g.getImageData(0, 0, w, h).data;
    let x0 = w, y0 = h, x1 = -1, y1 = -1;
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) if (d[(y * w + x) * 4 + 3] > 16) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
    if (x1 >= 0 && !(x0 === 0 && y0 === 0 && x1 === w - 1 && y1 === h - 1)) b = { x: x0, y: y0, w: x1 - x0 + 1, h: y1 - y0 + 1 };
  } catch (e) { }
  ruTrim.set(src, b);
  return b;
}
function ruFitLogo(img) {
  if (!img.complete || !img.naturalWidth) return;
  const box = img.parentElement, W = parseFloat(box.style.width) || 0, H = parseFloat(box.style.height) || 0;
  const nw = img.naturalWidth, nh = img.naturalHeight, o = ruBounds(img) || { x: 0, y: 0, w: nw, h: nh };
  const s = Math.min(W / o.w, H / o.h), top = img.dataset.ab ? H - o.h * s : (H - o.h * s) / 2;
  Object.assign(img.style, { position: 'absolute', objectFit: 'fill', maxWidth: 'none', left: (-o.x * s) + 'px', top: (top - o.y * s) + 'px', width: (nw * s) + 'px', height: (nh * s) + 'px' });
}
function ruLogoError(img) {
  if (img.crossOrigin) { const src = img.getAttribute('src'); img.removeAttribute('crossorigin'); img.src = ''; img.src = src; }
  else img.style.visibility = 'hidden';
}
</script>";

        /// Preview stand-in for the TV's 9-slice shadow: a box inset by the spread that casts a CSS shadow.
        public static string ShadowCss(int radius, int spread, string color) =>
            "position:absolute;left:" + spread + "px;top:" + spread + "px;right:" + spread + "px;bottom:" + spread + "px;border-radius:" + radius
            + "px;box-shadow:0 0 " + (spread * 6 / 10) + "px " + (spread / 8) + "px " + Css(color ?? UiTheme.Shadow) + ";";

        public static string RenderStage(UiFrame frame)
        {
            var b = new StringBuilder(TrimScript);
            b.Append("<div class=\"stage\" style=\"position:relative;width:1920px;height:1080px;overflow:hidden;font-family:'Helvetica Neue',Arial,sans-serif;background:")
                .Append(frame.Background == null ? "transparent" : Css(frame.Background)).Append("\">");
            foreach (var n in frame.Nodes)
            {
                b.Append("<div data-key=\"").Append(Enc(n.Key)).Append("\" style=\"position:absolute;left:").Append(n.X).Append("px;top:").Append(n.Y)
                    .Append("px;width:").Append(n.W).Append("px;height:").Append(n.H).Append("px;");
                if (n.Opacity < 1)
                    b.Append("opacity:").Append(n.Opacity.ToString("0.###", CultureInfo.InvariantCulture)).Append(';');
                if (n.EffectiveRadius > 0)
                    b.Append("border-radius:").Append(n.EffectiveRadius).Append("px;overflow:hidden;");
                switch (n.Kind)
                {
                    case UiNodeKind.Box:
                        b.Append("background:").Append(Css(n.Fill)).Append(";\"></div>");
                        break;
                    case UiNodeKind.Image:
                        if (UiGlyphs.IsGlyph(n.ImageUrl, out var glyph))   // same shapes as the TV's tinted PNG, as a CSS mask
                            b.Append("background:").Append(Css(n.Fill ?? UiTheme.Text)).Append(";-webkit-mask:url(&quot;").Append(UiGlyphs.SvgDataUrl(glyph)).Append("&quot;) center/100% 100% no-repeat;\"></div>");
                        else if (UiGlyphs.IsShadow(n.ImageUrl, out var shadowRadius, out var shadowSpread))
                            b.Append("\"><div style=\"").Append(ShadowCss(shadowRadius, shadowSpread, n.Fill)).Append("\"></div></div>");
                        else if (n.Contain)   // logos: fitted by their opaque part once loaded (TrimScript)
                            b.Append("\"><img crossorigin=\"anonymous\" data-ab=\"").Append(n.AnchorBottom ? "1" : "").Append("\" src=\"").Append(Enc(n.ImageUrl))
                                .Append("\" style=\"width:100%;height:100%;object-fit:contain;object-position:left ").Append(n.AnchorBottom ? "bottom" : "center")
                                .Append("\" onload=\"ruFitLogo(this)\" onerror=\"ruLogoError(this)\"></div>");
                        else
                            b.Append("\"><img src=\"").Append(Enc(n.ImageUrl)).Append("\" style=\"width:100%;height:100%;object-fit:cover;object-position:").Append((n.CropX * 100).ToString("0.#", CultureInfo.InvariantCulture)).Append("% ").Append((n.CropY * 100).ToString("0.#", CultureInfo.InvariantCulture)).Append("%\" onerror=\"this.style.display='none'\"></div>");
                        break;
                    case UiNodeKind.Gradient:
                        b.Append("background:linear-gradient(to ").Append(n.Vertical ? "bottom" : "right").Append(',').Append(Css(n.Fill)).Append(',').Append(Css(n.Color)).Append(");\"></div>");
                        break;
                    case UiNodeKind.Trailer:
                        // Static snapshot: a labelled frame where the live page embeds the trailer.
                        b.Append("\"></div>");
                        break;
                    default:
                        var align = n.Align == UiAlign.Center ? "center" : n.Align == UiAlign.End ? "right" : "left";
                        b.Append("font-size:").Append(n.FontSize).Append("px;color:").Append(Css(n.Color)).Append(";font-weight:").Append(n.Bold ? "700" : "400")
                            .Append(n.Stroke == null ? "" : ";-webkit-text-stroke:4px " + Css(n.Stroke) + ";paint-order:stroke fill")
                            .Append(";text-align:").Append(align).Append(";line-height:").Append(n.Wrap ? "1.45" : n.H + "px").Append(';');
                        if (!n.Wrap)
                            b.Append("white-space:nowrap;overflow:hidden;text-overflow:ellipsis;");
                        else if (n.Fill != null || n.Outline)
                            b.Append("white-space:pre-wrap;overflow:hidden;");
                        else   // whole lines only: the box is a multiple of the line height, extra lines end in "…"
                            b.Append("white-space:pre-wrap;overflow:hidden;display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:").Append(UiFormat.LinesIn(n.H, n.FontSize)).Append(';');
                        b.Append("\">");
                        var lines = (n.Text ?? "").Split('\n');
                        for (var i = 0; i < lines.Length; i++)
                        {
                            if (i > 0) b.Append("<br>");
                            if (n.Fill != null || n.Outline)
                            {
                                b.Append("<span style=\"");
                                if (n.Fill != null) b.Append("background:").Append(Css(n.Fill)).Append(";padding:2px 12px;box-decoration-break:clone;-webkit-box-decoration-break:clone;");
                                if (n.Outline) b.Append("text-shadow:-2px -2px 0 #000,2px -2px 0 #000,-2px 2px 0 #000,2px 2px 0 #000,3px 4px 6px rgba(0,0,0,.8);");
                                b.Append("\" dir=\"auto\">").Append(Enc(lines[i])).Append("</span>");
                            }
                            else
                                b.Append(Enc(lines[i]));
                        }
                        b.Append("</div>");
                        break;
                }
            }
            b.Append("</div>");
            return b.ToString();
        }

        /// "#RRGGBBAA" → rgba(); "#RRGGBB" unchanged.
        public static string Css(string color)
        {
            if (string.IsNullOrEmpty(color))
                return "transparent";
            if (color.Length == 9 && color[0] == '#')
            {
                int Hex(int at) => int.Parse(color.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return string.Format(CultureInfo.InvariantCulture, "rgba({0},{1},{2},{3:0.###})", Hex(1), Hex(3), Hex(5), Hex(7) / 255.0);
            }
            return color;
        }

        private static string Enc(string s) => WebUtility.HtmlEncode(s ?? "");
    }
}
