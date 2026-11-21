using System.Collections.Generic;

namespace Ru7100.Core.Ui
{
    /// The app logo: the amber mark (two chevrons, the back one at 55%) followed by "direct" in white, built from glyph
    /// Image nodes so every painter draws it the same way and the animator can scale it (text cannot animate its size).
    /// Home's menu draws it at scale 1; the launch intro draws the same keys bigger and lands them here, so there is
    /// only ever one logo on screen (docs/design/09-launch.html).
    public static class UiWordmark
    {
        /// Menu-scale geometry (scale 1): the mark is 40 px, the letters sit in 43.2 px glyph boxes (36 px type),
        /// 12 px after the mark, as in docs/design/mock.css .wordmark.
        public const int MarkSize = 40;
        public const double LetterBox = 43.2;
        public const double Gap = 12;
        public const double BackOpacity = 0.55;

        private const double LetterLeft = 1.5;   // UiGlyphs: each letter starts 1.5 units into its 24-unit box
        private static readonly string[] Letters =
        {
            UiGlyphs.WordmarkD, UiGlyphs.WordmarkI, UiGlyphs.WordmarkR, UiGlyphs.WordmarkE, UiGlyphs.WordmarkC, UiGlyphs.WordmarkT
        };
        /// Advance of each letter in glyph units (Inter Bold widths with -0.02 em tracking).
        private static readonly double[] Advances = { 12.207, 5.02, 7.745, 11.514, 11.368, 6.924 };

        public const string BackKey = "menu:mark-back", FrontKey = "menu:mark-front";
        public static string LetterKey(int i) => "menu:wm-" + i;
        public static int LetterCount => Letters.Length;

        /// Where the menu puts it.
        public static int MenuX => UiScale.SafeX;
        public static int MenuY => UiScale.SafeTop + 12;

        /// Width at `scale`.
        public static double Width(double scale)
        {
            var w = MarkSize + Gap - LetterLeft / 24 * LetterBox;
            foreach (var a in Advances)
                w += a / 24 * LetterBox;
            return w * scale;
        }

        public struct Part
        {
            public string Key, Glyph, Color;
            public double X, Y, Size, Opacity;
            public bool IsLetter;
            public int Index;
        }

        /// The parts at `scale` with the mark's top-left at (x, y): back chevron, front chevron, then the letters.
        public static List<Part> Parts(double scale, double x, double y)
        {
            var parts = new List<Part>(2 + Letters.Length)
            {
                new Part { Key = BackKey, Glyph = UiGlyphs.MarkBack, Color = UiTheme.Accent, X = x, Y = y, Size = MarkSize * scale, Opacity = BackOpacity, Index = -2 },
                new Part { Key = FrontKey, Glyph = UiGlyphs.MarkFront, Color = UiTheme.Accent, X = x, Y = y, Size = MarkSize * scale, Opacity = 1, Index = -1 },
            };
            var lx = x + (MarkSize + Gap - LetterLeft / 24 * LetterBox) * scale;
            var ly = y + (MarkSize - LetterBox) / 2 * scale;
            for (var i = 0; i < Letters.Length; i++)
            {
                parts.Add(new Part { Key = LetterKey(i), Glyph = Letters[i], Color = UiTheme.Text, X = lx, Y = ly, Size = LetterBox * scale, Opacity = 1, IsLetter = true, Index = i });
                lx += Advances[i] / 24 * LetterBox * scale;
            }
            return parts;
        }

        /// One part as a node.
        public static UiNode Node(UiFrame f, Part p)
        {
            var size = (int)System.Math.Round(p.Size);
            var node = f.Glyph(p.Key, p.Glyph, (int)System.Math.Round(p.X), (int)System.Math.Round(p.Y), size, p.Color);
            node.Opacity = p.Opacity;
            return node;
        }

        /// The logo at menu scale, where the menu shows it.
        public static void DrawMenu(UiFrame f)
        {
            foreach (var p in Parts(1, MenuX, MenuY))
                Node(f, p);
        }
    }
}
