using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Player layouts: the start card (and its failure and end variants), the overlay (gradients over the picture, one
    /// row of buttons above the transport), its right-edge sheets, the pause strip and the next-episode card.
    public static partial class UiLayout
    {
        private static void Player(UiFrame f, PlayerViewModel vm)
        {
            f.Background = null;   // video plane shows through
            var s = vm.Snapshot;

            if (vm.Card != PlayerCard.None)
            {
                PlayerCardScreen(f, vm, s);
                return;
            }
            if (vm.Loading)
            {
                // Nothing else is drawn over the start card: no transport, no track rows, no "Playing".
                PlayerLoading(f, vm, s);
                return;
            }
            if (s.State == PlayerState.Buffering || s.State == PlayerState.Preparing || s.State == PlayerState.Error)
            {
                // Mid-playback (buffering, switching source): a small card over the video, which stays visible.
                var status = s.Error ?? s.StatusText ?? (s.State == PlayerState.Buffering ? "Buffering " + s.BufferingPercent + "%" : "Starting…");
                // Centred on the screen, or in the space left of an open side panel.
                var panelX = SidePanelX(vm);
                var right = panelX > 0 ? panelX - 48 : UiFrame.Width - M;
                var w = Math.Min(1100, right - M);
                var x = panelX > 0 ? M + (right - M - w) / 2 : (UiFrame.Width - w) / 2;
                f.Box("player:status:bg", x, 466, w, 148, UiTheme.Scrim).R(UiTheme.RadiusPanel);
                LineIn(f, "player:status", x + 32, 466, 148, w - 64, status, UiScale.Heading, align: UiAlign.Center);
            }
            else if (vm.ResumeFlashVisible && !vm.OverlayVisible)
            {
                // Resuming: a play glyph answers the key for a moment.
                UiIcons.Circle(f, "player:resumed", UiFrame.Width / 2, UiFrame.Height / 2, 72, UiTheme.Scrim);
                f.GlyphIn("player:resumed:icon", UiGlyphs.Play, UiFrame.Width / 2 - 72 + 4, UiFrame.Height / 2 - 72, 144, 144, 84, UiTheme.Text);
            }

            if (vm.PauseStripVisible)
            {
                // Paused by the Play/Pause key: where you are, what is playing and how much is left.
                f.VerticalGradient("seek:bg", 0, BottomFadeY, UiFrame.Width, UiFrame.Height - BottomFadeY, "#000000", 0, BottomFadeAlpha);
                var title = UiFormat.Facts(s.Title ?? vm.Title, s.EpisodeLabel).Replace("  ·  ", " · ");
                Line(f, "pause:title", M, TransportRowY - UiScale.LineH(UiScale.Prose) - 16, CW, title, UiScale.Prose, bold: true);
                Transport(f, vm, s, false, RightEdge);
            }
            else if (vm.SeekIndicatorVisible)
            {
                f.VerticalGradient("seek:bg", 0, BottomFadeY + 120, UiFrame.Width, UiFrame.Height - BottomFadeY - 120, "#000000", 0, BottomFadeAlpha);
                Transport(f, vm, s, false, RightEdge);
            }

            if (vm.OverlayVisible)
                Overlay(f, vm, s);
            else if (vm.CreditsCardVisible)
                CreditsCard(f, vm);
            Subtitles(f, vm);   // on top of everything, so the style preview stays visible
        }

        /// Before the first picture (or after a start failed) the player shows a title card instead of a black screen.
        internal static bool LoadingScreen(PlayerSnapshot s) =>
            s.StartStep > 0 || !s.SessionActive || s.State == PlayerState.Idle || (s.State == PlayerState.Preparing && s.PositionMs <= 0);

        internal static readonly string[] StartSteps = { "Finding sources", "Connecting", "Starting" };

        /// Backdrop (dimmed), logo or title and the episode line shared by the start, failure and end cards.
        /// Returns the y where the card's own content starts.
        private static int PlayerCardBackdrop(UiFrame f, PlayerViewModel vm, PlayerSnapshot s)
        {
            var meta = s.Meta ?? vm.Request?.Meta;
            var id = meta?.Summary?.Id;
            f.Box("player:loading:bg", 0, 0, UiFrame.Width, UiFrame.Height, UiTheme.Background).Enter = UiEnter.None;
            if (id != null)
            {
                var art = f.Image("player:loading:art", 0, 0, UiFrame.Width, UiFrame.Height, meta.Background ?? UiFormat.BackgroundFor(id));
                if (art != null) { art.EnterMs = 600; art.Opacity = 0.6; }
                f.HorizontalGradient("player:loading:fade-left", 0, 0, 1200, UiFrame.Height, UiTheme.BackgroundRgb, 250, 0);
                f.VerticalGradient("player:loading:fade-bottom", 0, UiFrame.Height - 480, UiFrame.Width, 480, UiTheme.BackgroundRgb, 0, 255);
            }
            const int logoY = 430, logoH = 150;
            var logo = id == null ? null : f.Image("player:loading:logo", M, logoY, 620, logoH, UiFormat.LogoFor(id));
            if (logo != null) { logo.Contain = true; logo.AnchorBottom = true; }
            else LineIn(f, "player:loading:title", M, logoY, logoH, CW, s.Title ?? vm.Title ?? "", UiScale.Display, bold: true);
            var y = logoY + logoH + 10;
            var video = s.Video ?? vm.Request?.Video;
            var episode = !string.IsNullOrEmpty(s.EpisodeLabel) ? s.EpisodeLabel
                : video == null ? null : UiFormat.Episode(video) + (string.IsNullOrEmpty(video.Title) ? "" : " · " + video.Title);
            if (!string.IsNullOrEmpty(episode))
            {
                Line(f, "player:loading:episode", M, y, 1300, episode, UiScale.Prose);
                y += UiScale.LineH(UiScale.Body);
            }
            return y + 36;
        }

        private const int StartLineW = 620;   // the progress line is as wide as the logo box

        /// Start card: one status line (the current stage in white, the detail and elapsed time in grey) over one thin
        /// line that fills by stage; after a few seconds it names the two keys that work here.
        private static void PlayerLoading(UiFrame f, PlayerViewModel vm, PlayerSnapshot s)
        {
            var y = PlayerCardBackdrop(f, vm, s);
            var step = Math.Max(1, Math.Min(StartSteps.Length, s.StartStep));
            var stage = StartSteps[step - 1];
            var stageW = UiScale.TextW(stage, UiScale.Meta) + 8;
            Line(f, "player:loading:step:" + (step - 1), M, y, stageW, stage, UiScale.Meta, bold: true);
            var seconds = vm.StartElapsedMs / 1000;
            var detail = s.StatusText == stage || s.StatusText == stage + "…" ? null : s.StatusText;
            var status = UiFormat.Facts(detail, seconds >= 3 ? seconds + " s" : null).Replace("  ·  ", " · ");
            if (!string.IsNullOrEmpty(status))
            {
                Line(f, "player:loading:sep", M + stageW + 8, y, 24, "·", UiScale.Meta, UiTheme.TextFaint, align: UiAlign.Center);
                Line(f, "player:status", M + stageW + 40, y, 1300 - stageW - 40, status, UiScale.Meta, UiTheme.TextDim);
            }
            // Indeterminate progress: a line that fills by stage (no invented percentage within a stage).
            var barY = y + UiScale.LineH(UiScale.Meta) + 14;
            f.Box("player:loading:bar:track", M, barY, StartLineW, 4, "#FFFFFF2E").R(2);
            f.Box("player:loading:bar:fill", M, barY, (int)Math.Round(StartLineW * Math.Min(1.0, (step - 0.5) / StartSteps.Length)), 4, UiTheme.Text).R(2).MoveMs = 400;
            if (vm.StartHintVisible)
                Line(f, "player:loading:hint", M, barY + 28, 1300,
                    vm.CanChooseSource ? "Back: cancel  ·  OK: choose a source" : "Back: cancel", UiScale.Hint, UiTheme.TextFaint).Enter = UiEnter.Fade;
        }

        /// Failure card (reason in plain words, then Try again / Choose another source / Back) and end card
        /// (Next episode / Episodes / Back). The focused button uses the app's one focus style.
        private static void PlayerCardScreen(UiFrame f, PlayerViewModel vm, PlayerSnapshot s)
        {
            var y = PlayerCardBackdrop(f, vm, s);
            if (vm.Card == PlayerCard.Failed)
            {
                f.Paragraph("player:loading:error", M, y, 1300, 2, vm.CardMessage ?? "Playback stopped.", UiScale.Body, "#FF8A80");
                y += 2 * UiScale.LineH(UiScale.Body);
                if (!string.IsNullOrEmpty(vm.CardDetail))
                    Line(f, "player:loading:detail", M, y, 1500, vm.CardDetail, UiScale.Hint, UiTheme.TextFaint);
                y += UiScale.LineH(UiScale.Hint) + 20;
            }
            else
            {
                Line(f, "player:ended", M, y, 1300, "Episode finished", UiScale.Prose, UiTheme.TextDim);
                y += UiScale.LineH(UiScale.Body) + 20;
            }
            var x = M;
            for (var i = 0; i < vm.CardButtons.Count; i++)
            {
                var label = vm.CardButtons[i].Label;
                var w = ButtonWidth(label);
                GlassButton(f, "player:card:" + vm.CardButtons[i].Action, x, y, w, UiScale.ButtonH, label, vm.CardIndex == i);
                x += w + 20;
            }
        }

        // Player overlay geometry. Chrome melts into the picture: a gradient down from the top (title) and one up from
        // the bottom (button row, transport row, seek bar). Panels are sheets on the right edge over one dim layer.
        private const int TopFadeH = 260;
        private const int TopFadeAlpha = 219;                 // black 86% at the top edge
        private const int BottomFadeY = 620;
        private const int BottomFadeAlpha = 235;              // black 92% at the bottom edge
        private const int ActionsY = 796;                     // the button row
        private const int ActionH = UiScale.IconButton;       // 72
        private const int TransportRowY = 888;                // play/pause, elapsed and remaining time
        private const int BarY = 990;                         // centre line of the seek bar
        private const int SheetX = 1100;                      // More and Subtitle settings
        private const int EpisodesSheetX = 1000;
        private const int TracksSheetX = 640;                 // two columns: audio and subtitles
        private const int SheetLead = 200;                    // gradient lead-in left of a sheet
        private const int SheetPad = 24;
        private const int SheetTitleY = UiScale.SafeTop + 8;
        private const int SheetListY = 150;
        private const int SheetRowGap = 8;

        private static string ActionGlyph(OverlayRow row)
        {
            switch (row)
            {
                case OverlayRow.Episodes: return UiGlyphs.Episodes;
                case OverlayRow.NextEpisode: return UiGlyphs.Next;
                case OverlayRow.AudioSubtitles: return UiGlyphs.Subtitles;
                case OverlayRow.Sources: return UiGlyphs.Sources;
                case OverlayRow.More: return UiGlyphs.Dots;
                default: return null;
            }
        }

        /// Pill button over video or art: glass at rest, light fill with an ink label when focused. Optional glyph.
        private static void GlassButton(UiFrame f, string key, int x, int y, int w, int h, string label, bool focused, string glyph = null)
        {
            f.Box(key + ":bg", x, y, w, h, focused ? UiTheme.FocusFill : UiTheme.Glass).R(UiTheme.RadiusPill).MoveMs = 150;
            var color = focused ? UiTheme.TextOnFocus : UiTheme.Text;
            if (glyph == null)
            {
                LineIn(f, key + ":label", x + 12, y, h, w - 24, label, UiScale.Button, color, focused, UiAlign.Center);
                return;
            }
            f.GlyphIn(key + ":glyph", glyph, x + GlassPadX, y, UiScale.GlyphSize, h, UiScale.GlyphSize, color);
            var tx = x + GlassPadX + UiScale.GlyphSize + 14;
            LineIn(f, key + ":label", tx, y, h, x + w - 12 - tx, label, UiScale.Button, color, focused);
        }

        private const int GlassPadX = 28;

        private static int GlassButtonWidth(string label, bool glyph) =>
            glyph ? GlassPadX + UiScale.GlyphSize + 14 + (int)Math.Ceiling(label.Length * UiScale.Button * 0.53) + GlassPadX : ButtonWidth(label);

        /// Dim layer, gradient lead-in and body of a right-edge sheet that starts at `x`. Returns the content x.
        private static int Sheet(UiFrame f, int x, string title, string count = null)
        {
            f.Box("sheet:dim", 0, 0, UiFrame.Width, UiFrame.Height, UiTheme.SheetDim).Enter = UiEnter.Fade;
            var start = f.Nodes.Count;
            f.HorizontalGradient("sheet:lead", x - SheetLead, 0, SheetLead, UiFrame.Height, UiTheme.BackgroundRgb, 0, 242);
            f.Box("sheet:bg", x, 0, UiFrame.Width - x, UiFrame.Height, UiTheme.Sheet);
            var cx = x + SheetPad;
            if (title != null)
                Line(f, "sheet:title", cx + SheetPad, SheetTitleY, RightEdge - cx - SheetPad - (count == null ? 0 : 160), title, UiScale.Head, bold: true);
            if (count != null)
                Line(f, "sheet:count", RightEdge - 160 - SheetPad, SheetTitleY + 10, 160, count, UiScale.Hint, UiTheme.TextFaint, align: UiAlign.End);
            Animate(f, start, UiEnter.SlideLeft, 160);
            return cx;
        }

        private static void Overlay(UiFrame f, PlayerViewModel vm, PlayerSnapshot s)
        {
            var sheetX = SidePanelX(vm);
            var top = f.Nodes.Count;
            f.VerticalGradient("overlay:top", 0, 0, UiFrame.Width, TopFadeH, "#000000", TopFadeAlpha, 0);
            var sourceW = string.IsNullOrEmpty(s.SourceLabel) || sheetX > 0 ? 0 : 560;
            var titleW = (sheetX > 0 ? sheetX - SheetLead / 2 : RightEdge) - M - sourceW - 40;
            Line(f, "overlay:title", M, UiScale.SafeTop, titleW, s.Title ?? vm.Title, UiScale.Title, bold: true);
            Line(f, "overlay:episode", M, UiScale.SafeTop + UiScale.LineH(UiScale.Title), titleW, s.EpisodeLabel ?? "", UiScale.Prose);
            if (sourceW > 0)
                Line(f, "overlay:source", RightEdge - sourceW, UiScale.SafeTop + 20, sourceW, s.SourceLabel, UiScale.Meta, UiTheme.TextDim, align: UiAlign.End);
            Animate(f, top, UiEnter.Fade, 200);

            var bottom = f.Nodes.Count;
            f.VerticalGradient("overlay:bottom", 0, BottomFadeY, UiFrame.Width, UiFrame.Height - BottomFadeY, "#000000", 0, BottomFadeAlpha);
            // The transport stays under the dim layer, shortened to end before the sheet; the wide tracks sheet
            // leaves no room for it, so it steps aside and the subtitle cue has the free area to itself.
            var transportRight = sheetX > 0 ? sheetX - SheetLead / 2 - 36 : RightEdge;
            if (vm.Panel != PlayerPanel.Tracks)
                Transport(f, vm, s, vm.Panel == PlayerPanel.None && vm.FocusedRow == OverlayRow.Transport, transportRight);
            if (vm.Panel == PlayerPanel.None)
                Actions(f, vm);
            Animate(f, bottom, UiEnter.SlideUp, 200);

            switch (vm.Panel)
            {
                case PlayerPanel.Tracks: TracksPanel(f, vm); break;
                case PlayerPanel.Episodes: EpisodesPanel(f, vm); break;
                case PlayerPanel.Style: StylePanel(f, vm); break;
                case PlayerPanel.More: MorePanel(f, vm); break;
            }
        }

        /// One row of buttons above the transport; the focused one says what it shows or changes.
        private static void Actions(UiFrame f, PlayerViewModel vm)
        {
            var actions = vm.Actions;
            var x = M;
            for (var i = 0; i < actions.Count; i++)
            {
                var label = PlayerViewModel.RowLabel(actions[i]);
                var glyph = ActionGlyph(actions[i]);
                var w = GlassButtonWidth(label, glyph != null);
                var focused = vm.FocusedRow == actions[i];
                GlassButton(f, "action:" + actions[i], x, ActionsY, w, ActionH, label, focused, glyph);
                x += w + 16;
            }
            var hint = vm.ExitHintVisible ? "Press Back again to exit"
                : vm.FocusedRow == OverlayRow.Transport ? "" : vm.RowValue(vm.FocusedRow);
            if (!string.IsNullOrEmpty(hint))
                Line(f, "overlay:hint", M, ActionsY - UiScale.LineH(UiScale.Meta) - 14, 1300, hint, UiScale.Meta, vm.ExitHintVisible ? UiTheme.Text : UiTheme.TextDim, vm.ExitHintVisible);
            if (vm.FocusedRow == OverlayRow.NextEpisode && vm.NextVideo != null)
                NextEpisodeCard(f, vm, vm.NextVideo, RightEdge - NextCardW, ActionsY - 24 - NextCardH, "overlay:next", null);   // right edge: clear of the subtitle cue
        }

        private static void MorePanel(UiFrame f, PlayerViewModel vm)
        {
            var x = Sheet(f, SheetX, "More");
            var start = f.Nodes.Count;
            var rows = vm.MoreRows;
            for (var i = 0; i < rows.Count; i++)
                SettingRow(f, "setting:" + rows[i], x, SheetListY + i * UiScale.PanelRowH, RightEdge - x, UiScale.PanelRowH - SheetRowGap, PlayerViewModel.MoreLabel(rows[i]), vm.MoreValue(rows[i]), vm.FocusedMoreRow == rows[i]);
            Animate(f, start, UiEnter.SlideLeft, 160);
        }

        private static void StylePanel(UiFrame f, PlayerViewModel vm)
        {
            var rows = PlayerViewModel.StyleRows;
            var focus = Math.Max(0, rows.ToList().IndexOf(vm.FocusedStyleRow));
            var x = Sheet(f, SheetX, "Subtitle settings", (focus + 1) + " / " + rows.Count);
            var start = f.Nodes.Count;
            const int rowH = UiScale.PanelRowH;
            var hintH = UiScale.LineH(UiScale.Hint);
            var visible = Math.Min(rows.Count, (UiScale.Bottom - hintH - 16 - SheetListY) / rowH);
            var first = Window(focus, rows.Count, visible);
            for (var i = first; i < first + visible; i++)
            {
                var row = rows[i];
                SettingRow(f, "style:" + row, x, SheetListY + (i - first) * rowH, RightEdge - x, rowH - SheetRowGap, PlayerViewModel.StyleLabel(row), vm.StyleValue(row), vm.FocusedStyleRow == row, true);
            }
            Line(f, "style:hint", x + SheetPad, UiScale.Bottom - hintH, RightEdge - x - 2 * SheetPad, PlayerViewModel.StyleHint(vm.FocusedStyleRow), UiScale.Hint, UiTheme.TextFaint);
            Animate(f, start, UiEnter.SlideLeft, 160);
        }

        private const int NextCardW = 640;
        private const int NextCardPad = 20;
        private const int NextStillH = (NextCardW - 2 * NextCardPad) * 9 / 16;

        private static int NextCardH => NextCardPad + NextStillH + 12 + UiScale.LineH(UiScale.Meta) + UiScale.LineH(UiScale.Prose) + NextCardPad;

        /// One card: still, heading and title of the next episode (preview on focus, and the next-episode card).
        private static void NextEpisodeCard(UiFrame f, PlayerViewModel vm, VideoInfo next, int x, int y, string key, string heading)
        {
            var meta = vm.Snapshot.Meta ?? vm.Request?.Meta;
            var extras = vm.ExtrasFor(next);
            const int w = NextCardW, pad = NextCardPad;
            var h = NextCardH;
            f.Shadow(key + ":shadow", x, y, w, h, UiTheme.RadiusPanel);
            f.Box(key + ":bg", x, y, w, h, UiTheme.Panel + "F5").R(UiTheme.RadiusPanel);
            f.Image(key + ":still", x + pad, y + pad, w - 2 * pad, NextStillH, extras?.Thumbnail ?? UiFormat.EpisodeStill(meta?.Summary?.Id, next.Season, next.Episode), UiTheme.Placeholder);
            var ty = y + pad + NextStillH + 12;
            Line(f, key + ":heading", x + pad, ty, w - 2 * pad, heading ?? "Next episode", UiScale.Meta, UiTheme.TextDim);
            Line(f, key + ":title", x + pad, ty + UiScale.LineH(UiScale.Meta), w - 2 * pad, UiFormat.EpisodeColon(next) + " · " + (next.Title ?? "Episode " + next.Episode), UiScale.Prose, bold: true);
        }

        /// Wide sheet with two columns: audio ([Original] first) and subtitles (Off, preferred languages, divider, the
        /// rest; "Timing, size & style" above them all). Left/Right switch column; long lists show a window around the focused row.
        private static void TracksPanel(UiFrame f, PlayerViewModel vm)
        {
            var x = Sheet(f, TracksSheetX, "Audio & subtitles");
            var start = f.Nodes.Count;
            const int gap = 28, rowH = 72, headY = SheetListY - 6, dividerH = 18;
            var colW = (RightEdge - x - gap) / 2;
            var listY = headY + UiScale.LineH(UiScale.Meta) + 12;
            var audio = vm.AudioChoices;
            var subs = vm.SubtitleChoices;
            var hintH = UiScale.LineH(UiScale.Hint);
            var maxRows = (UiScale.Bottom - hintH - 16 - listY - 2 * dividerH) / rowH;
            var rows = Math.Max(1, Math.Min(maxRows, Math.Max(audio.Count, subs.Count)));
            var x2 = x + colW + gap;
            Line(f, "tracks:audio", x + SheetPad, headY, colW - SheetPad, "Audio", UiScale.Meta, vm.TracksColumn == 0 ? UiTheme.Text : UiTheme.TextDim, vm.TracksColumn == 0);
            Line(f, "tracks:subs", x2 + SheetPad, headY, colW - SheetPad, "Subtitles", UiScale.Meta, vm.TracksColumn == 1 ? UiTheme.Text : UiTheme.TextDim, vm.TracksColumn == 1);
            if (audio.Count == 0)
                Line(f, "tracks:a:none", x + SheetPad, listY + 14, colW - SheetPad, "One audio track", UiScale.Meta, UiTheme.TextFaint);
            var firstAudio = Window(vm.AudioIndex, audio.Count, rows);
            for (var i = firstAudio; i < audio.Count && i < firstAudio + rows; i++)
                TrackRow(f, "tracks:a:" + i, x, listY + (i - firstAudio) * rowH, colW, rowH - SheetRowGap, audio[i], vm.TracksColumn == 0 && vm.AudioIndex == i);
            var firstSub = Window(vm.SubtitleIndex, subs.Count, rows);
            var yy = listY;
            for (var i = firstSub; i < subs.Count && i < firstSub + rows; i++)
            {
                if (subs[i].Divider && i > firstSub)
                {
                    f.Box("tracks:divider:" + (subs[i].IsOff ? "settings" : "rest"), x2 + SheetPad, yy + 4, colW - 2 * SheetPad, 1, "#FFFFFF24");
                    yy += dividerH;
                }
                TrackRow(f, "tracks:s:" + i, x2, yy, colW, rowH - SheetRowGap, subs[i], vm.TracksColumn == 1 && vm.SubtitleIndex == i);
                yy += rowH;
            }
            Line(f, "tracks:hint", x + SheetPad, UiScale.Bottom - hintH, RightEdge - x - 2 * SheetPad, "Left/Right: column  ·  OK: choose  ·  Back: close", UiScale.Hint, UiTheme.TextFaint);
            Animate(f, start, UiEnter.SlideLeft, 160);
        }

        /// List row of a sheet: soft light fill and a bold label when focused; an amber tick marks the selected item.
        private static void TrackRow(UiFrame f, string key, int x, int y, int w, int h, TrackChoice choice, bool focused)
        {
            f.Box(key + ":bg", x, y, w, h, focused ? UiTheme.FocusRow : "#FFFFFF00").R(UiTheme.RadiusRow).MoveMs = 150;
            if (choice.Selected)
                f.GlyphIn(key + ":check", UiGlyphs.Check, x + SheetPad, y, UiScale.GlyphSize, h, UiScale.GlyphSize, UiTheme.Accent);
            var tx = x + SheetPad + UiScale.GlyphSize + 16;
            LineIn(f, key + ":label", tx, y, h, x + w - SheetPad - tx, choice.Label, UiScale.Prose, UiTheme.Text, focused);
        }

        /// Sheet: season (Left/Right) and its episodes with stills; the playing episode carries an amber play mark.
        private static void EpisodesPanel(UiFrame f, PlayerViewModel vm)
        {
            var season = vm.EpisodesSeason <= 0 ? "Specials" : "Season " + vm.EpisodesSeason;
            var x = Sheet(f, EpisodesSheetX, null);
            var start = f.Nodes.Count;
            const int rowH = 148, stillW = 224, stillH = 126, chevron = 30;
            var w = RightEdge - x;
            var current = vm.Snapshot.Video ?? vm.Request?.Video;
            var meta = vm.Snapshot.Meta ?? vm.Request?.Meta;
            // Season header: the name between two quiet chevrons (Left/Right change season).
            var headH = UiScale.LineH(UiScale.Head);
            var seasonW = UiScale.TextW(season, UiScale.Head) + 8;
            f.GlyphIn("episodes:season:prev", UiGlyphs.Left, x + SheetPad - 6, SheetTitleY, chevron, headH, chevron, "#FFFFFF99");
            Line(f, "episodes:season", x + SheetPad + chevron + 8, SheetTitleY, seasonW, season, UiScale.Head, bold: true);
            f.GlyphIn("episodes:season:next", UiGlyphs.Right, x + SheetPad + chevron + 16 + seasonW, SheetTitleY, chevron, headH, chevron, "#FFFFFF99");
            var list = vm.PanelEpisodes;
            var visible = (UiScale.Bottom + SheetRowGap - SheetListY) / rowH;
            var first = Window(vm.EpisodesIndex, list.Count, visible);
            for (var i = first; i < list.Count && i < first + visible; i++)
            {
                var v = list[i];
                var ry = SheetListY + (i - first) * rowH;
                var focused = i == vm.EpisodesIndex;
                var key = "episodes:" + v.Id;
                var isCurrent = v.Id == current?.Id;
                var extras = vm.ExtrasFor(v);
                f.Box(key + ":bg", x, ry, w, rowH - SheetRowGap, focused ? UiTheme.FocusRow : "#FFFFFF00").R(UiTheme.RadiusRow).MoveMs = 150;
                var sy = ry + (rowH - SheetRowGap - stillH) / 2;
                f.Image(key + ":still", x + 12, sy, stillW, stillH, UiFormat.ArtAtWidth(extras?.Thumbnail ?? UiFormat.EpisodeStill(meta?.Summary?.Id, v.Season, v.Episode), stillW), UiTheme.Placeholder);
                if (isCurrent)
                {
                    f.Box(key + ":now:bg", x + 12 + 10, sy + stillH - 10 - 44, 44, 44, UiTheme.Accent).R(UiTheme.RadiusPill);
                    f.GlyphIn(key + ":now:glyph", UiGlyphs.Play, x + 12 + 10, sy + stillH - 10 - 44, 44, 44, 24, UiTheme.TextOnAccent);
                }
                var tx = x + 12 + stillW + 24;
                var tw = x + w - SheetPad - tx;
                var title = v.Episode + ". " + (v.Title ?? "Episode " + v.Episode);
                var titleH = UiScale.LineH(UiScale.Prose);
                var overview = extras?.Overview;
                if (focused && !string.IsNullOrEmpty(overview))
                {
                    var metaH = UiScale.LineH(UiScale.Meta);
                    var top = ry + (rowH - SheetRowGap - titleH - 2 * metaH) / 2;
                    Line(f, key + ":title", tx, top, tw, title, UiScale.Prose, bold: true);
                    f.Paragraph(key + ":overview", tx, top + titleH, tw, 2, overview, UiScale.Meta, UiTheme.TextDim);
                }
                else if (isCurrent)
                {
                    var top = ry + (rowH - SheetRowGap - titleH - UiScale.LineH(UiScale.Meta)) / 2;
                    Line(f, key + ":title", tx, top, tw, title, UiScale.Prose, bold: focused);
                    Line(f, key + ":now", tx, top + titleH, tw, "Now playing", UiScale.Meta, UiTheme.Accent);
                }
                else
                    LineIn(f, key + ":title", tx, ry, rowH - SheetRowGap, tw, title, UiScale.Prose, bold: focused);
            }
            Animate(f, start, UiEnter.SlideLeft, 160);
        }

        /// Near the end: the next episode. With binge on it counts down to the automatic advance; with binge off it
        /// waits. OK plays it now, Back puts the card away.
        private static void CreditsCard(UiFrame f, PlayerViewModel vm)
        {
            var next = vm.NextVideo;
            if (next == null) return;
            var start = f.Nodes.Count;
            var seconds = (int)Math.Ceiling(vm.CreditsRemainingMs / 1000.0);
            const int x = UiScale.Right - NextCardW, buttonH = UiScale.IconButton;
            var hintH = UiScale.LineH(UiScale.Hint);
            var y = UiScale.Bottom - hintH - 8 - buttonH - 16 - NextCardH;
            NextEpisodeCard(f, vm, next, x, y, "credits", vm.CreditsAutoplay ? "Next episode in " + seconds + " s" : "Next episode");
            // The focused action as a real button; the keys stay named in one quiet line under it.
            f.Box("credits:play:bg", x, y + NextCardH + 16, NextCardW, buttonH, UiTheme.FocusFill).R(UiTheme.RadiusPill);
            var label = vm.CreditsAutoplay ? "Play now" : "Play next episode";
            var labelW = UiScale.TextW(label, UiScale.Button);
            var gx = x + (NextCardW - labelW - UiScale.GlyphSize - 12) / 2;
            f.GlyphIn("credits:play:glyph", UiGlyphs.Play, gx, y + NextCardH + 16, UiScale.GlyphSize, buttonH, 30, UiTheme.TextOnFocus);
            LineIn(f, "credits:play", gx + UiScale.GlyphSize + 12, y + NextCardH + 16, buttonH, labelW + 16, label, UiScale.Button, UiTheme.TextOnFocus, true);
            Line(f, "credits:hint", x, y + NextCardH + 16 + buttonH + 8, NextCardW, vm.CreditsAutoplay ? "OK: play now  ·  Back: hide" : "OK: play  ·  Back: hide", UiScale.Hint, UiTheme.TextDim, align: UiAlign.Center);
            Animate(f, start, UiEnter.SlideLeft, 200);
        }

        /// Sheet row: label left, value right. Focus is the soft light fill with a bold label; with `stepper` the
        /// focused value sits between two quiet chevrons (Left/Right change it).
        private static void SettingRow(UiFrame f, string key, int x, int y, int w, int h, string label, string value, bool focused, bool stepper = false)
        {
            f.Box(key + ":bg", x, y, w, h, focused ? UiTheme.FocusRow : "#FFFFFF00").R(UiTheme.RadiusRow).MoveMs = 150;
            var labelW = Math.Min(w - 240, UiScale.TextW(label, UiScale.Prose) + 8);   // the label never cuts; the value takes the rest
            LineIn(f, key + ":label", x + SheetPad, y, h, labelW, label, UiScale.Prose, UiTheme.Text, focused);
            if (string.IsNullOrEmpty(value))
                return;
            const int chevron = 24;
            var arrows = focused && stepper;
            var right = x + w - SheetPad - (arrows ? chevron + 10 : 0);
            var valueX = x + SheetPad + labelW + 16 + (arrows ? chevron + 10 : 0);
            var valueW = Math.Min(right - valueX, UiScale.TextW(value, UiScale.Meta) + 8);
            LineIn(f, key + ":value", right - valueW, y, h, valueW, value, UiScale.Meta, focused ? UiTheme.Text : UiTheme.TextDim, align: UiAlign.End);
            if (!arrows)
                return;
            f.GlyphIn(key + ":less", UiGlyphs.Left, right - valueW - 10 - chevron, y, chevron, h, chevron, "#FFFFFF99");
            f.GlyphIn(key + ":more", UiGlyphs.Right, right + 10, y, chevron, h, chevron, "#FFFFFF99");
        }

        /// Transport: a round play/pause button that shows the action OK performs (pause bars while playing, a play
        /// triangle while paused) between the -10 / +10 cues for Left and Right, with the elapsed time beside it and
        /// the time remaining at the far end; under them the seek bar on its own line with a playhead. While
        /// seeking, the target time rides above the playhead. `right` is where the bar ends (a sheet shortens it).
        private static void Transport(UiFrame f, PlayerViewModel vm, PlayerSnapshot s, bool focused, int right)
        {
            var position = vm.DisplayPositionMs;
            var ratio = s.DurationMs > 0 ? Math.Max(0, Math.Min(1, position / (double)s.DurationMs)) : 0;
            const int cueW = 76, button = UiScale.IconButton, clockW = 190, y = TransportRowY;
            LineIn(f, "transport:back", M, y, button, cueW, "−10", UiScale.Meta, UiTheme.TextDim, align: UiAlign.Center);
            var bx = M + cueW + 4;
            f.Box("transport:state:bg", bx, y, button, button, focused ? UiTheme.FocusFill : UiTheme.Glass).R(UiTheme.RadiusPill).MoveMs = 150;
            var paused = s.State == PlayerState.Paused;
            f.GlyphIn(paused ? "transport:state:play" : "transport:state:pause:0", paused ? UiGlyphs.Play : UiGlyphs.Pause, bx, y, button, button, UiScale.GlyphSize, focused ? UiTheme.TextOnFocus : UiTheme.Text);
            LineIn(f, "transport:forward", bx + button + 4, y, button, cueW, "+10", UiScale.Meta, UiTheme.TextDim, align: UiAlign.Center);
            var clockX = bx + button + 4 + cueW + 16;
            LineIn(f, "transport:position", clockX, y, button, clockW, UiFormat.Clock(position), UiScale.Prose, bold: true);
            if (s.DurationMs > 0)
                LineIn(f, "transport:duration", right - clockW, y, button, clockW, "−" + UiFormat.Clock(Math.Max(0, s.DurationMs - position)), UiScale.Prose, bold: true, align: UiAlign.End);

            // Seek bar: amber played range on a faint track, white playhead (larger while seeking).
            var barW = right - M;
            var barH = focused ? 10 : UiScale.ProgressH;
            f.Box("transport:bar:track", M, BarY - barH / 2, barW, barH, "#FFFFFF33").R(UiTheme.RadiusPill).MoveMs = 150;
            var cx = M + (int)Math.Round(barW * ratio);
            if (cx > M)
                f.Box("transport:bar:fill", M, BarY - barH / 2, cx - M, barH, UiTheme.Accent).R(UiTheme.RadiusPill).MoveMs = 150;
            var head = vm.PendingSeekMs != null ? 36 : UiScale.Playhead;
            f.Box("transport:bar:head", cx - head / 2, BarY - head / 2, head, head, UiTheme.Text).R(UiTheme.RadiusPill).MoveMs = 150;
            if (vm.PendingSeekMs != null)
            {
                const int bubbleW = 180, bubbleH = 60;
                var lo = clockX + clockW + 16;
                var hi = Math.Max(lo, right - clockW - 16 - bubbleW);
                var bubbleX = Math.Max(lo, Math.Min(cx - bubbleW / 2, hi));
                var by = y + (button - bubbleH) / 2;
                f.Box("transport:seek:bg", bubbleX, by, bubbleW, bubbleH, UiTheme.FocusFill).R(UiTheme.RadiusPill).MoveMs = 120;
                LineIn(f, "transport:seek", bubbleX, by, bubbleH, bubbleW, UiFormat.Clock(position), UiScale.Prose, UiTheme.TextOnFocus, true, UiAlign.Center).MoveMs = 120;
            }
        }

        private const int SubtitleWidth = 1280;   // about 42 characters at the default size: no head-turn reading

        /// Left edge of the right-edge sheet the overlay shows now, or 0 for none.
        internal static int SidePanelX(PlayerViewModel vm)
        {
            if (!vm.OverlayVisible)
                return 0;
            switch (vm.Panel)
            {
                case PlayerPanel.Episodes: return EpisodesSheetX;
                case PlayerPanel.Tracks: return TracksSheetX;
                case PlayerPanel.Style:
                case PlayerPanel.More: return SheetX;
                default: return 0;
            }
        }

        /// The single app-drawn subtitle layer: every cue (external or embedded) gets the user's style.
        public static void Subtitles(UiFrame f, PlayerViewModel vm)
        {
            var text = vm.SubtitleText;
            if (string.IsNullOrEmpty(text))
                return;
            var style = vm.Settings.SubtitleStyle;
            var lines = UiFormat.Lines(text).ToList();
            var lineH = (int)Math.Ceiling(style.Size * 1.35);
            // Estimate wrapping (about 0.52 em per glyph) so long cues grow upward instead of spilling off screen.
            // Sheets: the cue moves to the centre of the free area left of them (inside the safe margin), so a
            // change of track or style can be judged while the sheet is open.
            var width = SubtitleWidth;
            var left = (UiFrame.Width - SubtitleWidth) / 2;
            var bottom = vm.SubtitleBottom;
            var panelX = SidePanelX(vm);
            if (panelX > 0)
            {
                width = Math.Min(SubtitleWidth, panelX - M - 24);
                left = M + (panelX - M - 24 - width) / 2;
            }
            var perLine = Math.Max(10, (int)(width / (style.Size * 0.52)));
            var visualLines = lines.Sum(l => Math.Max(1, (l.Length + perLine - 1) / perLine));
            var h = Math.Max(1, visualLines) * lineH + 16;
            var y = UiFrame.Height - bottom - h;
            var node = f.Text("subtitle", left, y, width, h, string.Join("\n", lines), style.Size, style.TextColor, style.Bold, UiAlign.Center, wrap: true);
            node.Outline = style.Outline;
            node.Fill = style.BoxColor;
        }
    }
}
