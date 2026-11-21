using System;

namespace Ru7100.Core.Ui
{
    /// 10-foot sizing tokens for the 1920x1080 stage: the viewer sits ~3 m from a 55" panel, so text, cards and
    /// focus are sized like Netflix / Apple TV on 1080p. Every layout reads its sizes from here (through UiTheme
    /// and UiLayout), so the whole UI can be retuned in one place.
    public static class UiScale
    {
        // ---- safe area (TV overscan) ----
        public const int SafeX = 96;                                  // left and right margin
        public const int SafeTop = 54;
        public const int SafeBottom = 54;
        public const int Right = UiFrame.Width - SafeX;               // 1824
        public const int Bottom = UiFrame.Height - SafeBottom;        // 1026
        public const int ContentW = UiFrame.Width - 2 * SafeX;        // 1728

        // ---- spacing (8 px grid) ----
        public const int Gap = 16;
        public const int GapLarge = 32;

        // ---- type ramp (px at 1080p); nothing on screen is smaller than Hint ----
        public const int Display = 72;      // hero / title-page title when there is no logo
        public const int Title = 56;        // screen titles, search query
        public const int Heading = 44;      // dialog and panel titles, player status
        public const int RowTitle = 40;     // Home row titles
        public const int Body = 34;         // synopsis, list items, primary text
        public const int Button = 32;       // button and menu labels
        public const int Facts = 34;        // title facts line under a hero or details logo (read from ~3 m on the TV)
        public const int Secondary = 30;    // other facts lines, settings rows, card captions
        public const int Small = 28;        // tertiary text (file names, cast, chips)
        public const int Hint = 26;         // hints, badges, column headers: the floor
        public const int KeyCap = 40;       // single-character keyboard keys

        // Design ramp (2026-11-19): four working steps with visible jumps, Title 56 / Head 40 / Prose 34 / Meta 28.
        // Display stays for logo-less hero titles and Label is for badges only. New layouts use these names.
        public const int Head = 40;         // row titles, sheet and panel titles
        public const int Prose = 34;        // synopsis, list rows, button labels
        public const int Meta = 28;         // facts, captions, values
        public const int Label = 26;        // badges: the floor

        /// Generous average glyph advance (bold included) for sizing pills and buttons around a label.
        /// UiFormat.GlyphWidth (0.52) is the wrapping estimate; this one is a little wider so labels never clip.
        public const double LabelGlyph = 0.56;

        /// Estimated width of a one-line label.
        public static int TextW(string text, int size) => (int)Math.Ceiling((text ?? "").Length * size * LabelGlyph);

        /// Height of a one-line text box: the TV's line height (~1.45 x font size), so the painter never clips.
        public static int LineH(int size) => UiFormat.LineH(size);

        // ---- controls ----
        public const int ButtonH = 80;
        public const int ButtonPadX = 36;
        public const int ButtonMinW = 180;
        public const int ChipH = 64;
        public const int BadgeH = 44;
        public const int FocusRing = 4;                // ring thickness around focused cards and fields
        public const double PosterGrow = 0.06;          // focused poster scale-up
        public const double LandscapeGrow = 0.05;       // focused landscape card scale-up

        // ---- cards ----
        public const int LandW = 464;                   // landscape cards: ~3.5 per row (16:9)
        public const int LandH = 261;
        public const int PosterW = 288;                 // portrait posters (2:3)
        public const int PosterH = 432;
        public const int CardGap = 32;

        // ---- lists and panels ----
        public const int ListRowH = 96;
        public const int PanelRowH = 76;                // player settings rows

        // ---- design-language controls ----
        public const int IconButton = 72;               // round icon buttons
        public const int PlayH = 88;                    // the primary Play button
        public const int GlyphSize = 34;                // glyph inside a button or row
        public const int ProgressH = 6;                 // watch progress and the transport bar at rest
        public const int Playhead = 28;                 // transport playhead (36 while seeking)
    }
}
