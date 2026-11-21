using System;
using System.Collections.Generic;

namespace Ru7100.Core.Ui
{
    /// Box, Text, Image, or Trailer (a muted/unmuted trailer surface; painters without video show nothing,
    /// so layouts always draw the still image underneath).
    /// Gradient: one smooth element from Fill (start) to Color (end), top-to-bottom or left-to-right.
    public enum UiNodeKind { Box, Text, Image, Trailer, Gradient }

    public enum UiAlign { Start, Center, End }

    /// How a node appears when it is new in a frame (UiAnimator).
    public enum UiEnter { Fade, None, SlideUp, SlideLeft, Grow }

    /// One absolutely positioned element on the 1920x1080 stage. Painters (ElmSharp on the TV, HTML in the
    /// browser preview) draw nodes in list order; later nodes are on top. Key is stable across frames, so the
    /// animator can glide a node from its old geometry to its new one.
    public sealed class UiNode
    {
        public string Key { get; set; }
        public UiNodeKind Kind { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        /// 0..1, multiplied into the node's colours.
        public double Opacity { get; set; } = 1;
        /// Box: fill colour. Text: optional backing behind the glyphs. "#RRGGBB" or "#RRGGBBAA".
        public string Fill { get; set; }
        public string Text { get; set; }
        public int FontSize { get; set; } = 32;
        public string Color { get; set; } = UiTheme.Text;
        public bool Bold { get; set; }
        public UiAlign Align { get; set; }
        /// Wrap onto several lines (otherwise one line with ellipsis).
        public bool Wrap { get; set; }
        /// Black outline + drop shadow around glyphs (subtitles).
        public bool Outline { get; set; }
        /// Text drawn as an outline in this colour around the fill colour (Top 10 numerals). Null = none.
        public string Stroke { get; set; }
        public string ImageUrl { get; set; }
        /// Image: fit inside the box, anchored left (logos), instead of covering it.
        public bool Contain { get; set; }
        /// With Contain: place the fitted image at the box's bottom-left corner (logos), instead of left-centre.
        public bool AnchorBottom { get; set; }
        /// Cover images: which part of the picture survives the crop, 0 = left/top edge, 0.5 = centre, 1 = right/bottom
        /// (CSS object-position). Backdrops keep the top, where key art puts faces.
        public double CropX { get; set; } = 0.5;
        public double CropY { get; set; } = 0.5;
        /// Corner radius in px at 1080p (Box and Image). Painters without border-radius use 9-patch images.
        public int Radius { get; set; }
        /// Image with Radius on a painter that can't clip: colour of what lies behind the corners (corner masks).
        public string CornerMask { get; set; }
        /// Gradient direction: true = top to bottom, false = left to right.
        public bool Vertical { get; set; } = true;
        /// Lowered nodes (UiFrameLowering): image is a 9-patch with this border, or is stretched to the box.
        public int NinePatch { get; set; }
        public bool Stretch { get; set; }
        /// Trailer: YouTube video id, and whether it plays muted.
        public string VideoId { get; set; }
        /// Trailer: the IMDb title id (full-length trailers come from IMDb; VideoId is the YouTube fallback).
        public string TitleId { get; set; }
        public bool Muted { get; set; } = true;

        // Animation hints (UiAnimator).
        public UiEnter Enter { get; set; } = UiEnter.Fade;
        /// Duration of moves/resizes/opacity changes; 0 = jump.
        public int MoveMs { get; set; } = 180;
        public int EnterMs { get; set; } = 180;
        /// Fade-out time once the node leaves the frame; 0 = remove at once.
        public int ExitMs { get; set; }

        public UiNode Clone() => (UiNode)MemberwiseClone();

        /// Sets the corner radius (UiTheme.Radius* tokens; RadiusPill = half the height). Returns the node.
        public UiNode R(int radius)
        {
            Radius = radius;
            return this;
        }

        /// Radius in pixels after resolving pills and clamping to half the smaller side.
        public int EffectiveRadius
        {
            get
            {
                var max = Math.Max(0, Math.Min(W, H) / 2);
                return Radius < 0 ? max : Math.Min(Radius, max);
            }
        }
    }

    public sealed class UiFrame
    {
        public const int Width = 1920;
        public const int Height = 1080;

        /// Null means transparent, so the hardware video plane shows through (player screen).
        public string Background { get; set; } = UiTheme.Background;
        /// Changes when the visible screen changes; the animator plays a screen transition.
        public string ScreenKey { get; set; }
        public List<UiNode> Nodes { get; } = new List<UiNode>();

        public UiNode Box(string key, int x, int y, int w, int h, string fill)
        {
            var node = new UiNode { Key = key, Kind = UiNodeKind.Box, X = x, Y = y, W = w, H = h, Fill = fill };
            Nodes.Add(node);
            return node;
        }

        public UiNode Text(string key, int x, int y, int w, int h, string text, int size, string color = null, bool bold = false, UiAlign align = UiAlign.Start, bool wrap = false)
        {
            var node = new UiNode
            {
                Key = key, Kind = UiNodeKind.Text, X = x, Y = y, W = w, H = h, Text = text ?? "", FontSize = size,
                Color = color ?? UiTheme.Text, Bold = bold, Align = align, Wrap = wrap
            };
            Nodes.Add(node);
            return node;
        }

        /// Wrapped text clamped to `maxLines` whole lines with "…". The box is exactly maxLines line heights tall,
        /// so no painter ever shows a line cut in half.
        public UiNode Paragraph(string key, int x, int y, int w, int maxLines, string text, int size, string color = null, bool bold = false, UiAlign align = UiAlign.Start) =>
            Text(key, x, y, w, maxLines * UiFormat.LineH(size), UiFormat.ClampLines(text, w, size, maxLines), size, color, bold, align, wrap: true);

        public UiNode Image(string key, int x, int y, int w, int h, string url, string placeholder = null)
        {
            if (placeholder != null)
                Box(key + ":ph", x, y, w, h, placeholder);
            if (string.IsNullOrEmpty(url))
                return null;
            var node = new UiNode { Key = key, Kind = UiNodeKind.Image, X = x, Y = y, W = w, H = h, ImageUrl = url };
            Nodes.Add(node);
            return node;
        }

        public UiNode Trailer(string key, int x, int y, int w, int h, string videoId, bool muted, string titleId = null)
        {
            var node = new UiNode { Key = key, Kind = UiNodeKind.Trailer, X = x, Y = y, W = w, H = h, VideoId = videoId, TitleId = titleId, Muted = muted, EnterMs = 600, ExitMs = 250 };
            Nodes.Add(node);
            return node;
        }

        /// One smooth vertical gradient element: alpha from `fromAlpha` (top) to `toAlpha` (bottom).
        /// (`bands` is ignored; kept so call sites read the same.) The TV paints it as a stretched ramp image.
        public UiNode VerticalGradient(string key, int x, int y, int w, int h, string rgb, int fromAlpha, int toAlpha, int bands = 0) =>
            Gradient(key, x, y, w, h, rgb, fromAlpha, toAlpha, true);

        /// One smooth horizontal gradient element: alpha from `fromAlpha` (left) to `toAlpha` (right).
        public UiNode HorizontalGradient(string key, int x, int y, int w, int h, string rgb, int fromAlpha, int toAlpha, int bands = 0) =>
            Gradient(key, x, y, w, h, rgb, fromAlpha, toAlpha, false);

        private UiNode Gradient(string key, int x, int y, int w, int h, string rgb, int fromAlpha, int toAlpha, bool vertical)
        {
            if (w <= 0 || h <= 0)
                return null;
            var node = new UiNode
            {
                Key = key, Kind = UiNodeKind.Gradient, X = x, Y = y, W = w, H = h, Vertical = vertical,
                Fill = rgb + Math.Max(0, Math.Min(255, fromAlpha)).ToString("X2"), Color = rgb + Math.Max(0, Math.Min(255, toAlpha)).ToString("X2"),
                Enter = UiEnter.None
            };
            Nodes.Add(node);
            return node;
        }
    }

    /// Design language (2026-11-19): blue-black ink with the artwork carrying the colour, white for focus, and one
    /// warm amber for what belongs to the viewer (watch progress, the selected option, the primary Play button).
    /// Chips, badges and quality labels are neutral. Sized for reading from 3 m on a 55" 1080p panel.
    public static class UiTheme
    {
        public const string Background = "#0B0E14";  // ink: the page, scrims, sheet base
        public const string BackgroundRgb = "#0B0E14";
        public const string CardPanel = "#151A22";   // cards before their art loads, chips
        public const string Panel = "#1E2530";       // raised: buttons at rest on the page, pop-out card panel, dialogs
        public const string PanelFocused = "#2A3340";
        public const string Line = "#2A3340";        // hairlines
        public const string Accent = "#FFB23E";      // amber: progress, selected, primary Play. Never focus.
        public const string TextOnAccent = "#0B0E14";
        public const string Focus = "#FFFFFF";       // card ring
        public const string FocusFill = "#F2F2F2";   // focused buttons and chips (label in TextOnFocus)
        public const string FocusRow = "#FFFFFF26";  // focused list row, over video or ink (label stays white, bold)
        public const string Glass = "#FFFFFF1F";     // buttons and badges at rest over art or video
        public const string GlassEdge = "#FFFFFF55"; // their outline
        public const string Glow = "#FFFFFF33";
        public const string Shadow = "#0000008C";    // tint of the soft shadow (UiGlyphs.Shadow)
        public const int ShadowSpread = 48;
        public const int ShadowDrop = 16;
        public const string Text = "#FFFFFF";
        public const string TextDim = "#A9B1BD";     // meta: facts, captions, values (~9:1 on ink)
        public const string TextFaint = "#7E8794";   // column headers, separators, hints (~5:1 on ink)
        public const string TextOnFocus = "#0B0E14";
        public const string Scrim = "#000000B3";
        public const string ScrimLight = "#00000080";
        public const string SheetDim = "#00000066";  // one dim layer under any open player panel
        public const string Sheet = "#0B0E14F2";     // right-edge sheet body
        public const string Placeholder = "#151A22";
        public const string Watched = "#5AD1A0";     // ready / healthy / watched
        public const string Warn = "#F5A623";
        public const string Bad = "#E5484D";
        public const string Rating = "#F5C518";

        // Sizes come from UiScale (10-foot tokens).
        public const int Margin = UiScale.SafeX;
        public const int TitleSize = UiScale.Title;
        public const int HeadingSize = UiScale.Heading;
        public const int BodySize = UiScale.Body;
        public const int SmallSize = UiScale.Small;
        public const int FocusBorder = UiScale.FocusRing;

        // Radius tokens shared by every painter (px at 1080p).
        public const int RadiusCard = 14;
        public const int RadiusPanel = 16;
        public const int RadiusRow = 12;
        public const int RadiusChip = UiScale.ChipH / 2;
        public const int RadiusBadge = 8;
        public const int RadiusPill = -1;     // -1 = half the element's height
    }
}
