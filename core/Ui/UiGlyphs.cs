using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ru7100.Core.Ui
{
    /// Icon glyphs and soft shadows, drawn the same way by every painter. Each glyph is a short list of shapes in a
    /// 24x24 box; the TV rasterises it once into a white alpha PNG (UiAssets) that the painter tints with the node's
    /// Fill, and the browser preview uses the same shapes as an SVG mask. A glyph or shadow is an ordinary Image node
    /// whose ImageUrl is "glyph:name" or "shadow:radius:spread", so painters need no new node kind.
    public static class UiGlyphs
    {
        public const string Play = "play", Pause = "pause", Rewind = "rew", Forward = "ffw", Next = "next", Previous = "prev";
        public const string Check = "check", Left = "left", Right = "right", Up = "up", Down = "down", Plus = "plus", Dots = "dots";
        public const string Subtitles = "subs", Episodes = "eps", Sources = "layers", Settings = "sliders", Trailer = "film";
        public const string Volume = "vol", Star = "star", Stop = "stop", Binge = "binge", Disc = "disc", Thumb = "thumb", Search = "search", Mark = "mark";
        /// The app logo in parts, so the launch intro can move each piece (UiWordmark): the two chevrons of the mark and
        /// the letters of "direct" (Inter Bold outlines, OFL; em = 20 units, baseline y = 19, left margin 1.5).
        public const string MarkFront = "mark-front", MarkBack = "mark-back";
        public const string WordmarkD = "wm-d", WordmarkI = "wm-i", WordmarkR = "wm-r", WordmarkE = "wm-e", WordmarkC = "wm-c", WordmarkT = "wm-t";

        private const string GlyphScheme = "glyph:";
        private const string ShadowScheme = "shadow:";

        private enum Kind { Circle, Rect, Cap, Poly }

        private struct Shape
        {
            public Kind Kind;
            public bool Cut;
            public double A, B, C, D, E;
            public double[] Points;
            public double MinX, MinY, MaxX, MaxY;   // Poly bounds, so long outlines (the wordmark) skip far pixels cheaply
        }

        private static Shape Circle(double cx, double cy, double r, bool cut = false) => new Shape { Kind = Kind.Circle, A = cx, B = cy, C = r, Cut = cut };
        private static Shape Rect(double x, double y, double w, double h, double r = 0, bool cut = false) => new Shape { Kind = Kind.Rect, A = x, B = y, C = w, D = h, E = r, Cut = cut };
        /// A line with round ends: from (x1, y1) to (x2, y2), `t` thick.
        private static Shape Cap(double x1, double y1, double x2, double y2, double t, bool cut = false) => new Shape { Kind = Kind.Cap, A = x1, B = y1, C = x2, D = y2, E = t, Cut = cut };
        private static Shape Poly(params double[] points) => Bounded(new Shape { Kind = Kind.Poly, Points = points });
        private static Shape CutPoly(params double[] points) => Bounded(new Shape { Kind = Kind.Poly, Points = points, Cut = true });

        private static Shape Bounded(Shape s)
        {
            s.MinX = s.MinY = double.MaxValue;
            s.MaxX = s.MaxY = double.MinValue;
            for (var i = 0; i < s.Points.Length; i += 2)
            {
                s.MinX = Math.Min(s.MinX, s.Points[i]); s.MaxX = Math.Max(s.MaxX, s.Points[i]);
                s.MinY = Math.Min(s.MinY, s.Points[i + 1]); s.MaxY = Math.Max(s.MaxY, s.Points[i + 1]);
            }
            return s;
        }

        // Later shapes paint over earlier ones; a Cut shape removes what is under it.
        private static readonly Dictionary<string, Shape[]> Shapes = new Dictionary<string, Shape[]>
        {
            [Play] = new[] { Poly(7.6, 4.4, 20.4, 12, 7.6, 19.6), Cap(8.4, 5.6, 19, 12, 1.6), Cap(8.4, 18.4, 19, 12, 1.6), Cap(8.4, 5.6, 8.4, 18.4, 1.6) },
            [Pause] = new[] { Rect(5.5, 4.5, 4.6, 15, 1.2), Rect(13.9, 4.5, 4.6, 15, 1.2) },
            [Stop] = new[] { Rect(5.5, 5.5, 13, 13, 2) },
            [Rewind] = new[] { Poly(11.6, 5.2, 11.6, 18.8, 2, 12), Poly(22, 5.2, 22, 18.8, 12.4, 12) },
            [Forward] = new[] { Poly(12.4, 5.2, 12.4, 18.8, 22, 12), Poly(2, 5.2, 2, 18.8, 11.6, 12) },
            [Next] = new[] { Poly(4, 4.8, 15.6, 12, 4, 19.2), Rect(17.2, 4.5, 2.8, 15, 1.2) },
            [Previous] = new[] { Poly(20, 4.8, 8.4, 12, 20, 19.2), Rect(4, 4.5, 2.8, 15, 1.2) },
            [Check] = new[] { Cap(4.6, 12.8, 9.4, 17.6, 2.6), Cap(9.4, 17.6, 19.6, 7.2, 2.6) },
            [Left] = new[] { Cap(14.6, 5.6, 8.4, 12, 2.6), Cap(8.4, 12, 14.6, 18.4, 2.6) },
            [Right] = new[] { Cap(9.4, 5.6, 15.6, 12, 2.6), Cap(15.6, 12, 9.4, 18.4, 2.6) },
            [Up] = new[] { Cap(5.6, 14.6, 12, 8.4, 2.6), Cap(12, 8.4, 18.4, 14.6, 2.6) },
            [Down] = new[] { Cap(5.6, 9.4, 12, 15.6, 2.6), Cap(12, 15.6, 18.4, 9.4, 2.6) },
            [Plus] = new[] { Rect(10.7, 4, 2.6, 16, 1.3), Rect(4, 10.7, 16, 2.6, 1.3) },
            [Dots] = new[] { Circle(5, 12, 2.1), Circle(12, 12, 2.1), Circle(19, 12, 2.1) },
            [Disc] = new[] { Circle(12, 12, 12) },
            [Subtitles] = new[]
            {
                Rect(2, 4, 20, 14, 2.5), Poly(5.6, 17, 11, 17, 5.6, 21.6),
                Cap(6.4, 9.6, 11.6, 9.6, 2, true), Cap(15.2, 9.6, 17.6, 9.6, 2, true), Cap(6.4, 13.4, 8.8, 13.4, 2, true), Cap(12.4, 13.4, 17.6, 13.4, 2, true)
            },
            [Episodes] = new[] { Rect(2.5, 4, 8, 5.2, 1.2), Rect(2.5, 10.6, 8, 5.2, 1.2), Rect(12.5, 5.4, 9, 2.4, 1.2), Rect(12.5, 12, 9, 2.4, 1.2), Rect(2.5, 17.6, 19, 2.4, 1.2) },
            [Sources] = new[] { Poly(12, 2.8, 22, 8.2, 12, 13.6, 2, 8.2), Cap(3.2, 12.6, 12, 17.4, 2), Cap(12, 17.4, 20.8, 12.6, 2), Cap(3.2, 16.8, 12, 21.6, 2), Cap(12, 21.6, 20.8, 16.8, 2) },
            [Settings] = new[]
            {
                Cap(4, 6.5, 20, 6.5, 2), Circle(9, 6.5, 4, true), Circle(9, 6.5, 2.8),
                Cap(4, 12, 20, 12, 2), Circle(15.5, 12, 4, true), Circle(15.5, 12, 2.8),
                Cap(4, 17.5, 20, 17.5, 2), Circle(8, 17.5, 4, true), Circle(8, 17.5, 2.8)
            },
            [Trailer] = new[] { Rect(2, 4, 20, 16, 2.5), CutPoly(9.8, 8.2, 16.4, 12, 9.8, 15.8) },
            [Volume] = new[] { Poly(3, 9.4, 7.2, 9.4, 12.6, 4.8, 12.6, 19.2, 7.2, 14.6, 3, 14.6), Cap(16, 8.8, 17.4, 12, 2), Cap(17.4, 12, 16, 15.2, 2) },
            [Star] = new[] { Poly(12, 2.6, 14.9, 8.6, 21.4, 9.5, 16.7, 14.1, 17.8, 20.6, 12, 17.5, 6.2, 20.6, 7.3, 14.1, 2.6, 9.5, 9.1, 8.6) },
            [Thumb] = new[] { Rect(2.4, 10.4, 4, 10.4, 1), Poly(8, 10.6, 11.4, 3.4, 12.8, 3, 14.2, 4.2, 14.4, 5.8, 13.6, 9.2, 19.6, 9.2, 21.2, 10.8, 21, 12.4, 19.4, 19.4, 17.8, 20.8, 8, 20.8) },
            [Search] = new[] { Circle(10.4, 10.4, 7.4), Circle(10.4, 10.4, 4.9, true), Cap(15.8, 15.8, 20.6, 20.6, 2.8) },
            [Mark] = new[] { Poly(3, 3, 8.6, 3, 14, 12, 8.6, 21, 3, 21, 8.4, 12), Poly(11.6, 3, 16.2, 3, 21.6, 12, 16.2, 21, 11.6, 21, 17, 12) },
            [MarkFront] = new[] { Poly(3, 3, 8.6, 3, 14, 12, 8.6, 21, 3, 21, 8.4, 12) },
            [MarkBack] = new[] { Poly(11.6, 3, 16.2, 3, 21.6, 12, 16.2, 21, 11.6, 21, 17, 12) },
            [WordmarkD] = new[]
            {
                Poly(6.95, 19.18, 7.17, 19.17, 7.37, 19.14, 7.56, 19.11, 7.75, 19.07, 7.92, 19.02, 8.09, 18.95, 8.25, 18.88, 8.4, 18.8, 8.54, 18.72, 8.68, 18.64, 8.81, 18.54, 8.93, 18.45, 9.04, 18.35, 9.15, 18.24, 9.25, 18.14, 9.34, 18.02, 9.43, 17.91, 9.51, 17.8, 9.59, 17.69, 9.66, 17.58, 9.72, 17.47, 9.78, 17.36, 9.83, 17.25, 9.98, 17.25, 9.98, 19, 12.86, 19, 12.86, 4.45, 9.93, 4.45, 9.93, 9.92, 9.83, 9.92, 9.78, 9.81, 9.73, 9.7, 9.66, 9.59, 9.6, 9.48, 9.52, 9.37, 9.44, 9.26, 9.36, 9.14, 9.26, 9.03, 9.16, 8.92, 9.06, 8.81, 8.95, 8.71, 8.82, 8.61, 8.7, 8.51, 8.56, 8.42, 8.42, 8.34, 8.27, 8.26, 8.11, 8.18, 7.94, 8.12, 7.76, 8.07, 7.57, 8.02, 7.38, 7.99, 7.17, 7.96, 6.95, 7.95, 6.72, 7.95, 6.42, 7.96, 6.13, 7.98, 5.85, 8.03, 5.56, 8.1, 5.29, 8.19, 5.02, 8.3, 4.76, 8.42, 4.51, 8.57, 4.26, 8.74, 4.03, 8.92, 3.81, 9.13, 3.6, 9.35, 3.41, 9.6, 3.22, 9.86, 3.05, 10.14, 2.9, 10.45, 2.75, 10.77, 2.63, 11.11, 2.53, 11.47, 2.44, 11.85, 2.38, 12.25, 2.33, 12.67, 2.3, 13.1, 2.29, 13.56, 2.3, 14.01, 2.33, 14.43, 2.37, 14.84, 2.44, 15.24, 2.52, 15.61, 2.62, 15.97, 2.74, 16.31, 2.88, 16.63, 3.03, 16.94, 3.2, 17.22, 3.38, 17.49, 3.57, 17.74, 3.78, 17.96, 3.99, 18.17, 4.23, 18.36, 4.47, 18.54, 4.73, 18.69, 4.99, 18.82, 5.27, 18.93, 5.54, 19.02, 5.83, 19.09, 6.12, 19.14, 6.43, 19.18, 6.73, 19.19),
                CutPoly(7.46, 16.83, 7.28, 16.81, 7.1, 16.77, 6.94, 16.73, 6.78, 16.67, 6.63, 16.6, 6.49, 16.51, 6.36, 16.41, 6.23, 16.3, 6.11, 16.18, 6, 16.05, 5.9, 15.91, 5.8, 15.76, 5.72, 15.6, 5.64, 15.43, 5.57, 15.25, 5.5, 15.06, 5.45, 14.87, 5.4, 14.66, 5.37, 14.45, 5.34, 14.24, 5.32, 14.02, 5.3, 13.79, 5.3, 13.55, 5.3, 13.32, 5.32, 13.09, 5.34, 12.86, 5.36, 12.65, 5.4, 12.44, 5.45, 12.24, 5.5, 12.05, 5.56, 11.86, 5.63, 11.68, 5.71, 11.51, 5.8, 11.36, 5.89, 11.21, 5.99, 11.07, 6.1, 10.94, 6.22, 10.82, 6.35, 10.71, 6.48, 10.61, 6.63, 10.53, 6.78, 10.45, 6.93, 10.39, 7.1, 10.35, 7.27, 10.32, 7.45, 10.3, 7.64, 10.29, 7.83, 10.3, 8, 10.31, 8.17, 10.35, 8.34, 10.39, 8.49, 10.45, 8.64, 10.52, 8.79, 10.6, 8.92, 10.69, 9.05, 10.8, 9.17, 10.92, 9.28, 11.04, 9.38, 11.18, 9.48, 11.33, 9.57, 11.48, 9.65, 11.65, 9.72, 11.83, 9.78, 12.01, 9.84, 12.21, 9.88, 12.41, 9.92, 12.62, 9.95, 12.84, 9.97, 13.07, 9.98, 13.31, 9.99, 13.55, 9.98, 13.79, 9.97, 14.03, 9.95, 14.26, 9.92, 14.47, 9.88, 14.69, 9.83, 14.89, 9.78, 15.09, 9.71, 15.27, 9.64, 15.45, 9.56, 15.62, 9.47, 15.78, 9.38, 15.93, 9.27, 16.07, 9.16, 16.2, 9.04, 16.31, 8.91, 16.42, 8.78, 16.52, 8.63, 16.6, 8.49, 16.67, 8.33, 16.73, 8.17, 16.77, 8, 16.81, 7.82, 16.83, 7.64, 16.83)
            },
            [WordmarkI] = new[]
            {
                Poly(4.38, 6.65, 4.53, 6.63, 4.69, 6.59, 4.83, 6.55, 4.97, 6.48, 5.1, 6.41, 5.23, 6.32, 5.35, 6.22, 5.46, 6.1, 5.56, 5.99, 5.64, 5.86, 5.71, 5.73, 5.76, 5.6, 5.8, 5.45, 5.82, 5.31, 5.83, 5.15, 5.82, 5, 5.8, 4.85, 5.76, 4.7, 5.71, 4.57, 5.64, 4.44, 5.56, 4.31, 5.46, 4.19, 5.35, 4.08, 5.23, 3.98, 5.1, 3.89, 4.97, 3.82, 4.83, 3.76, 4.69, 3.71, 4.53, 3.68, 4.38, 3.66, 4.21, 3.65, 4.05, 3.66, 3.9, 3.68, 3.74, 3.71, 3.6, 3.76, 3.46, 3.82, 3.33, 3.9, 3.2, 3.98, 3.08, 4.09, 2.97, 4.2, 2.88, 4.32, 2.8, 4.44, 2.73, 4.57, 2.68, 4.71, 2.64, 4.85, 2.62, 5, 2.61, 5.15, 2.62, 5.31, 2.64, 5.45, 2.68, 5.6, 2.73, 5.73, 2.8, 5.86, 2.88, 5.99, 2.97, 6.1, 3.08, 6.22, 3.2, 6.32, 3.33, 6.41, 3.46, 6.48, 3.6, 6.55, 3.74, 6.59, 3.9, 6.63, 4.05, 6.65, 4.21, 6.66),
                Poly(5.68, 8.08, 2.75, 8.08, 2.75, 19, 5.68, 19)
            },
            [WordmarkR] = new[]
            {
                Poly(5.68, 12.79, 5.68, 12.62, 5.7, 12.46, 5.72, 12.3, 5.75, 12.15, 5.8, 12, 5.85, 11.86, 5.91, 11.72, 5.98, 11.59, 6.06, 11.47, 6.14, 11.35, 6.23, 11.24, 6.34, 11.14, 6.44, 11.04, 6.56, 10.95, 6.68, 10.87, 6.81, 10.79, 6.95, 10.72, 7.09, 10.67, 7.23, 10.62, 7.38, 10.58, 7.53, 10.54, 7.69, 10.52, 7.85, 10.51, 8.01, 10.5, 8.1, 10.5, 8.18, 10.51, 8.27, 10.51, 8.36, 10.51, 8.44, 10.52, 8.53, 10.53, 8.62, 10.53, 8.72, 10.54, 8.81, 10.55, 8.89, 10.56, 8.97, 10.58, 9.04, 10.59, 9.11, 10.6, 9.17, 10.62, 9.23, 10.63, 9.28, 10.65, 9.28, 8.03, 9.23, 8.02, 9.18, 8.01, 9.13, 8, 9.07, 7.99, 9.01, 7.98, 8.95, 7.97, 8.89, 7.97, 8.82, 7.96, 8.76, 7.95, 8.69, 7.95, 8.63, 7.95, 8.57, 7.94, 8.51, 7.94, 8.45, 7.94, 8.39, 7.94, 8.34, 7.94, 8.11, 7.94, 7.89, 7.97, 7.68, 8.01, 7.47, 8.06, 7.27, 8.14, 7.08, 8.23, 6.89, 8.33, 6.71, 8.45, 6.54, 8.59, 6.38, 8.74, 6.24, 8.91, 6.1, 9.1, 5.98, 9.29, 5.88, 9.51, 5.78, 9.74, 5.7, 9.99, 5.58, 9.99, 5.58, 8.08, 2.75, 8.08, 2.75, 19, 5.68, 19)
            },
            [WordmarkE] = new[]
            {
                Poly(8, 19.21, 8.3, 19.19, 8.6, 19.16, 8.89, 19.12, 9.16, 19.07, 9.43, 19.01, 9.69, 18.93, 9.94, 18.84, 10.18, 18.75, 10.41, 18.64, 10.63, 18.52, 10.84, 18.4, 11.04, 18.26, 11.23, 18.11, 11.41, 17.96, 11.58, 17.79, 11.74, 17.62, 11.88, 17.44, 12.01, 17.25, 12.13, 17.06, 12.24, 16.86, 12.34, 16.65, 12.42, 16.44, 12.5, 16.22, 9.84, 15.72, 9.8, 15.82, 9.75, 15.92, 9.7, 16.02, 9.65, 16.11, 9.59, 16.19, 9.53, 16.27, 9.46, 16.35, 9.39, 16.43, 9.31, 16.5, 9.23, 16.56, 9.15, 16.62, 9.06, 16.68, 8.97, 16.73, 8.87, 16.78, 8.77, 16.82, 8.67, 16.86, 8.56, 16.9, 8.45, 16.93, 8.34, 16.95, 8.22, 16.97, 8.1, 16.99, 7.98, 17, 7.86, 17.01, 7.73, 17.01, 7.54, 17, 7.36, 16.99, 7.19, 16.96, 7.02, 16.93, 6.85, 16.88, 6.69, 16.83, 6.54, 16.76, 6.4, 16.69, 6.26, 16.6, 6.13, 16.5, 6, 16.4, 5.89, 16.28, 5.78, 16.16, 5.68, 16.02, 5.59, 15.87, 5.51, 15.72, 5.43, 15.55, 5.37, 15.38, 5.32, 15.19, 5.27, 15, 5.24, 14.79, 5.21, 14.58, 5.2, 14.36, 5.2, 14.3, 12.66, 14.3, 12.66, 13.48, 12.66, 13.14, 12.64, 12.8, 12.61, 12.48, 12.56, 12.17, 12.51, 11.87, 12.44, 11.58, 12.36, 11.31, 12.27, 11.04, 12.16, 10.79, 12.05, 10.55, 11.93, 10.31, 11.79, 10.09, 11.65, 9.88, 11.5, 9.68, 11.34, 9.49, 11.17, 9.32, 10.99, 9.15, 10.8, 8.99, 10.61, 8.85, 10.41, 8.72, 10.2, 8.59, 9.99, 8.48, 9.77, 8.38, 9.54, 8.29, 9.3, 8.21, 9.06, 8.14, 8.82, 8.08, 8.57, 8.03, 8.32, 7.99, 8.06, 7.97, 7.8, 7.95, 7.54, 7.95, 7.14, 7.96, 6.76, 7.99, 6.4, 8.05, 6.05, 8.12, 5.71, 8.22, 5.38, 8.35, 5.06, 8.49, 4.76, 8.66, 4.47, 8.85, 4.2, 9.05, 3.95, 9.27, 3.71, 9.51, 3.49, 9.77, 3.29, 10.04, 3.1, 10.33, 2.94, 10.64, 2.78, 10.96, 2.65, 11.3, 2.54, 11.65, 2.45, 12.02, 2.38, 12.39, 2.33, 12.78, 2.3, 13.18, 2.29, 13.6, 2.3, 14.02, 2.33, 14.43, 2.38, 14.83, 2.45, 15.21, 2.54, 15.58, 2.66, 15.93, 2.79, 16.27, 2.94, 16.59, 3.11, 16.89, 3.3, 17.18, 3.51, 17.45, 3.73, 17.7, 3.97, 17.94, 4.23, 18.15, 4.51, 18.35, 4.8, 18.53, 5.11, 18.69, 5.43, 18.83, 5.77, 18.95, 6.13, 19.04, 6.49, 19.12, 6.88, 19.17, 7.27, 19.2, 7.68, 19.21),
                CutPoly(5.21, 12.47, 5.21, 12.45, 5.23, 12.27, 5.26, 12.1, 5.3, 11.93, 5.35, 11.77, 5.4, 11.61, 5.46, 11.46, 5.54, 11.31, 5.62, 11.17, 5.71, 11.04, 5.8, 10.92, 5.91, 10.8, 6.02, 10.7, 6.14, 10.6, 6.27, 10.5, 6.41, 10.42, 6.55, 10.35, 6.7, 10.29, 6.86, 10.24, 7.03, 10.2, 7.2, 10.17, 7.38, 10.16, 7.56, 10.15, 7.75, 10.16, 7.93, 10.17, 8.09, 10.2, 8.26, 10.24, 8.41, 10.29, 8.56, 10.35, 8.7, 10.42, 8.83, 10.5, 8.95, 10.59, 9.07, 10.69, 9.18, 10.8, 9.28, 10.92, 9.37, 11.04, 9.46, 11.18, 9.53, 11.32, 9.6, 11.48, 9.66, 11.64, 9.71, 11.8, 9.76, 11.98, 9.79, 12.16, 9.82, 12.35, 9.84, 12.47)
            },
            [WordmarkC] = new[]
            {
                Poly(7.87, 19.21, 8.11, 19.2, 8.34, 19.18, 8.57, 19.15, 8.79, 19.12, 9.01, 19.07, 9.23, 19.02, 9.43, 18.96, 9.64, 18.89, 9.84, 18.82, 10.03, 18.74, 10.21, 18.65, 10.39, 18.55, 10.57, 18.45, 10.73, 18.34, 10.89, 18.22, 11.05, 18.09, 11.2, 17.96, 11.34, 17.83, 11.47, 17.68, 11.6, 17.53, 11.72, 17.38, 11.83, 17.22, 11.94, 17.05, 12.04, 16.88, 12.13, 16.7, 12.21, 16.52, 12.29, 16.33, 12.35, 16.13, 12.41, 15.93, 12.46, 15.72, 12.51, 15.5, 9.78, 15.01, 9.76, 15.11, 9.74, 15.22, 9.71, 15.32, 9.68, 15.42, 9.64, 15.52, 9.6, 15.62, 9.56, 15.71, 9.52, 15.8, 9.47, 15.88, 9.42, 15.97, 9.37, 16.05, 9.31, 16.12, 9.25, 16.2, 9.19, 16.27, 9.13, 16.33, 9.06, 16.4, 8.99, 16.46, 8.92, 16.51, 8.84, 16.57, 8.77, 16.62, 8.69, 16.66, 8.6, 16.71, 8.52, 16.74, 8.43, 16.78, 8.34, 16.81, 8.25, 16.84, 8.16, 16.86, 8.06, 16.88, 7.97, 16.89, 7.87, 16.9, 7.77, 16.91, 7.66, 16.91, 7.47, 16.9, 7.29, 16.88, 7.11, 16.85, 6.94, 16.8, 6.78, 16.74, 6.63, 16.67, 6.49, 16.58, 6.35, 16.48, 6.22, 16.37, 6.1, 16.25, 5.99, 16.11, 5.88, 15.97, 5.79, 15.82, 5.7, 15.65, 5.62, 15.48, 5.55, 15.3, 5.48, 15.11, 5.43, 14.91, 5.38, 14.7, 5.35, 14.49, 5.32, 14.28, 5.3, 14.05, 5.28, 13.82, 5.28, 13.58, 5.28, 13.34, 5.3, 13.1, 5.32, 12.87, 5.35, 12.65, 5.38, 12.44, 5.43, 12.23, 5.48, 12.03, 5.55, 11.84, 5.62, 11.66, 5.7, 11.49, 5.79, 11.32, 5.88, 11.17, 5.99, 11.03, 6.1, 10.9, 6.22, 10.78, 6.35, 10.67, 6.49, 10.57, 6.63, 10.49, 6.78, 10.41, 6.94, 10.35, 7.11, 10.31, 7.29, 10.28, 7.47, 10.26, 7.66, 10.25, 7.77, 10.25, 7.87, 10.26, 7.97, 10.27, 8.06, 10.28, 8.16, 10.3, 8.25, 10.32, 8.34, 10.35, 8.43, 10.38, 8.51, 10.41, 8.6, 10.45, 8.68, 10.49, 8.75, 10.53, 8.83, 10.58, 8.9, 10.63, 8.97, 10.69, 9.04, 10.74, 9.1, 10.8, 9.17, 10.87, 9.23, 10.94, 9.28, 11.01, 9.34, 11.08, 9.39, 11.16, 9.44, 11.24, 9.49, 11.32, 9.53, 11.41, 9.57, 11.49, 9.61, 11.59, 9.65, 11.68, 9.68, 11.78, 9.71, 11.88, 9.74, 11.98, 9.76, 12.09, 12.49, 11.58, 12.44, 11.37, 12.39, 11.17, 12.33, 10.97, 12.27, 10.78, 12.19, 10.59, 12.11, 10.41, 12.02, 10.24, 11.92, 10.06, 11.81, 9.9, 11.7, 9.74, 11.58, 9.59, 11.45, 9.44, 11.32, 9.3, 11.18, 9.17, 11.03, 9.04, 10.88, 8.92, 10.71, 8.81, 10.55, 8.7, 10.37, 8.6, 10.19, 8.51, 10.01, 8.42, 9.82, 8.34, 9.62, 8.27, 9.42, 8.2, 9.21, 8.14, 9, 8.09, 8.78, 8.04, 8.56, 8.01, 8.34, 7.98, 8.11, 7.96, 7.87, 7.95, 7.63, 7.95, 7.23, 7.96, 6.84, 7.99, 6.46, 8.04, 6.1, 8.12, 5.75, 8.22, 5.41, 8.34, 5.09, 8.49, 4.79, 8.65, 4.5, 8.84, 4.22, 9.04, 3.97, 9.26, 3.73, 9.5, 3.5, 9.76, 3.3, 10.03, 3.11, 10.32, 2.94, 10.63, 2.79, 10.95, 2.66, 11.29, 2.54, 11.64, 2.45, 12, 2.38, 12.38, 2.33, 12.77, 2.3, 13.17, 2.29, 13.59, 2.3, 14, 2.33, 14.4, 2.38, 14.79, 2.45, 15.17, 2.54, 15.53, 2.66, 15.88, 2.79, 16.21, 2.94, 16.53, 3.11, 16.84, 3.3, 17.13, 3.5, 17.4, 3.73, 17.66, 3.97, 17.9, 4.22, 18.12, 4.5, 18.32, 4.79, 18.51, 5.09, 18.67, 5.41, 18.82, 5.75, 18.94, 6.1, 19.04, 6.46, 19.12, 6.84, 19.17, 7.23, 19.2, 7.63, 19.21)
            },
            [WordmarkT] = new[]
            {
                Poly(6.69, 19.16, 6.8, 19.15, 6.91, 19.15, 7.03, 19.14, 7.14, 19.13, 7.26, 19.12, 7.38, 19.11, 7.5, 19.1, 7.62, 19.08, 7.74, 19.06, 7.86, 19.04, 7.99, 19.02, 8.11, 18.99, 8.23, 18.96, 8.35, 18.93, 8.47, 18.89, 8.05, 16.7, 8.01, 16.71, 7.97, 16.72, 7.91, 16.73, 7.86, 16.74, 7.8, 16.75, 7.74, 16.76, 7.67, 16.77, 7.59, 16.78, 7.52, 16.79, 7.45, 16.8, 7.39, 16.81, 7.33, 16.81, 7.27, 16.82, 7.22, 16.82, 7.17, 16.82, 7.12, 16.82, 7, 16.82, 6.88, 16.81, 6.77, 16.79, 6.66, 16.76, 6.57, 16.73, 6.49, 16.68, 6.42, 16.63, 6.36, 16.57, 6.3, 16.51, 6.26, 16.43, 6.22, 16.35, 6.19, 16.26, 6.16, 16.16, 6.14, 16.05, 6.13, 15.93, 6.13, 15.81, 6.13, 10.32, 8.17, 10.32, 8.17, 8.08, 6.13, 8.08, 6.13, 5.48, 3.2, 5.48, 3.2, 8.08, 1.7, 8.08, 1.7, 10.32, 3.2, 10.32, 3.2, 16.06, 3.21, 16.42, 3.25, 16.76, 3.32, 17.08, 3.42, 17.38, 3.54, 17.66, 3.69, 17.91, 3.86, 18.14, 4.07, 18.36, 4.3, 18.54, 4.55, 18.71, 4.83, 18.84, 5.13, 18.96, 5.46, 19.04, 5.81, 19.11, 6.18, 19.14, 6.58, 19.16)
            },
            [Binge] = new[] { Poly(3, 5.6, 11, 12, 3, 18.4), Poly(11, 5.6, 19, 12, 11, 18.4), Rect(19.4, 5.6, 2.2, 12.8, 1.1) }
        };

        public static IEnumerable<string> Names => Shapes.Keys;

        /// Raster size for the TV's glyph PNG: icons at 96 px; the logo parts at 192 px, because the launch intro shows
        /// them about 140 px tall and upscaling a 96 px raster would look soft.
        public static int RasterSize(string name) =>
            name == MarkFront || name == MarkBack || name.StartsWith("wm-", StringComparison.Ordinal) ? 192 : 96;

        /// Logo parts first, so the launch intro has them before anything else is generated (UiAssets.Prewarm).
        public static bool IsLogo(string name) => RasterSize(name) > 96;

        public static string Url(string name) => GlyphScheme + name;

        public static bool IsGlyph(string url, out string name)
        {
            name = url != null && url.StartsWith(GlyphScheme, StringComparison.Ordinal) ? url.Substring(GlyphScheme.Length) : null;
            return name != null && Shapes.ContainsKey(name);
        }

        public static string ShadowUrl(int radius, int spread) => ShadowScheme + radius.ToString(CultureInfo.InvariantCulture) + ":" + spread.ToString(CultureInfo.InvariantCulture);

        public static bool IsShadow(string url, out int radius, out int spread)
        {
            radius = spread = 0;
            if (url == null || !url.StartsWith(ShadowScheme, StringComparison.Ordinal))
                return false;
            var parts = url.Substring(ShadowScheme.Length).Split(':');
            return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out radius)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out spread);
        }

        /// A glyph in a size x size box, tinted `color`. Colour changes (focus) re-tint the same image.
        public static UiNode Glyph(this UiFrame f, string key, string name, int x, int y, int size, string color)
        {
            var node = new UiNode { Key = key, Kind = UiNodeKind.Image, X = x, Y = y, W = size, H = size, ImageUrl = Url(name), Fill = color, Stretch = true, Enter = UiEnter.None };
            f.Nodes.Add(node);
            return node;
        }

        /// A glyph centred in the box (x, y, w, h).
        public static UiNode GlyphIn(this UiFrame f, string key, string name, int x, int y, int w, int h, int size, string color) =>
            f.Glyph(key, name, x + (w - size) / 2, y + (h - size) / 2, size, color);

        /// Soft shadow under a rounded box (x, y, w, h, radius): fades to nothing over `spread` px, pushed down by
        /// `dy`. One stretched 9-slice image on the TV; box-shadow in the preview. Draw it before the box.
        public static UiNode Shadow(this UiFrame f, string key, int x, int y, int w, int h, int radius, int spread = UiTheme.ShadowSpread, int dy = UiTheme.ShadowDrop, string color = UiTheme.Shadow)
        {
            var r = radius < 0 ? Math.Min(w, h) / 2 : radius;
            var node = new UiNode
            {
                Key = key, Kind = UiNodeKind.Image, X = x - spread, Y = y - spread + dy, W = w + 2 * spread, H = h + 2 * spread,
                ImageUrl = ShadowUrl(r, spread), Fill = color, Enter = UiEnter.None
            };
            f.Nodes.Add(node);
            return node;
        }

        /// How much of the 24-box point (x, y) the glyph covers: 1 inside, 0 outside.
        public static bool Contains(string name, double x, double y)
        {
            var inside = false;
            foreach (var s in Shapes[name])
                if (Hit(s, x, y))
                    inside = !s.Cut;
            return inside;
        }

        /// Coverage of pixel (px, py) of a size x size raster, 4x4 supersampled.
        public static double Coverage(string name, int px, int py, int size)
        {
            var hits = 0;
            for (var sy = 0; sy < 4; sy++)
                for (var sx = 0; sx < 4; sx++)
                    if (Contains(name, (px + (sx + 0.5) / 4) * 24.0 / size, (py + (sy + 0.5) / 4) * 24.0 / size))
                        hits++;
            return hits / 16.0;
        }

        /// Shadow strength at `distance` px outside the box edge (negative = under the box), for a `spread` px shadow.
        public static double ShadowAlpha(double distance, int spread)
        {
            if (distance <= 0) return 1;
            if (distance >= spread) return 0;
            var t = 1 - distance / spread;
            return t * t * (3 - 2 * t) * t;   // smooth, weighted towards the box
        }

        private static bool Hit(Shape s, double x, double y)
        {
            switch (s.Kind)
            {
                case Kind.Circle:
                    return (x - s.A) * (x - s.A) + (y - s.B) * (y - s.B) <= s.C * s.C;
                case Kind.Rect:
                {
                    if (x < s.A || y < s.B || x > s.A + s.C || y > s.B + s.D) return false;
                    var cx = Math.Max(s.A + s.E, Math.Min(x, s.A + s.C - s.E));
                    var cy = Math.Max(s.B + s.E, Math.Min(y, s.B + s.D - s.E));
                    return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= s.E * s.E + 1e-9;
                }
                case Kind.Cap:
                {
                    double dx = s.C - s.A, dy = s.D - s.B, len = dx * dx + dy * dy;
                    var t = len <= 0 ? 0 : Math.Max(0, Math.Min(1, ((x - s.A) * dx + (y - s.B) * dy) / len));
                    double qx = s.A + t * dx - x, qy = s.B + t * dy - y;
                    return qx * qx + qy * qy <= s.E * s.E / 4;
                }
                default:
                {
                    if (x < s.MinX || x > s.MaxX || y < s.MinY || y > s.MaxY) return false;
                    var p = s.Points;
                    var inside = false;
                    for (int i = 0, j = p.Length - 2; i < p.Length; j = i, i += 2)
                        if ((p[i + 1] > y) != (p[j + 1] > y) && x < (p[j] - p[i]) * (y - p[i + 1]) / (p[j + 1] - p[i + 1]) + p[i])
                            inside = !inside;
                    return inside;
                }
            }
        }

        /// The glyph as a standalone SVG (white on transparent) for CSS masks in the browser preview.
        public static string Svg(string name)
        {
            var b = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><mask id='m'><rect width='24' height='24' fill='black'/>");
            foreach (var s in Shapes[name])
            {
                var paint = s.Cut ? "black" : "white";
                switch (s.Kind)
                {
                    case Kind.Circle:
                        b.Append("<circle cx='").Append(N(s.A)).Append("' cy='").Append(N(s.B)).Append("' r='").Append(N(s.C)).Append("' fill='").Append(paint).Append("'/>");
                        break;
                    case Kind.Rect:
                        b.Append("<rect x='").Append(N(s.A)).Append("' y='").Append(N(s.B)).Append("' width='").Append(N(s.C)).Append("' height='").Append(N(s.D)).Append("' rx='").Append(N(s.E)).Append("' fill='").Append(paint).Append("'/>");
                        break;
                    case Kind.Cap:
                        b.Append("<line x1='").Append(N(s.A)).Append("' y1='").Append(N(s.B)).Append("' x2='").Append(N(s.C)).Append("' y2='").Append(N(s.D)).Append("' stroke-width='").Append(N(s.E)).Append("' stroke-linecap='round' stroke='").Append(paint).Append("'/>");
                        break;
                    default:
                        b.Append("<polygon points='");
                        for (var i = 0; i < s.Points.Length; i++)
                            b.Append(i == 0 ? "" : i % 2 == 0 ? " " : ",").Append(N(s.Points[i]));
                        b.Append("' fill='").Append(paint).Append("'/>");
                        break;
                }
            }
            return b.Append("</mask><rect width='24' height='24' fill='white' mask='url(#m)'/></svg>").ToString();
        }

        /// The SVG as a data: URL (for CSS mask-image).
        public static string SvgDataUrl(string name) => "data:image/svg+xml;utf8," + Uri.EscapeDataString(Svg(name));

        private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
