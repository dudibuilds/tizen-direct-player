using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Interpolates between layout frames so focus, scrolling, overlays and screens glide instead of jumping.
    /// Only geometry and opacity are animated; text, colours and images switch at once, so a painter only
    /// moves/resizes/fades objects it already has (cheap on the 2019 TV: no per-frame decode or re-layout).
    /// Host loop: Step(target, now) whenever the layout changes and on every tick while IsAnimating.
    public sealed class UiAnimator
    {
        public const int ScreenTransitionMs = 260;
        public const int ScreenSlidePx = 48;

        private sealed class Track
        {
            public UiNode Target;
            public double FromX, FromY, FromW, FromH, FromOpacity;
            public double X, Y, W, H, Opacity;
            public long StartMs;
            public int DurationMs;
            public bool Exiting;
            /// The node emitted last frame; reused while the track rests, so a settled node costs no allocation
            /// and reaches the painter as the same object (nothing to compare).
            public UiNode Emitted;
            public UiNode EmittedFor;
        }

        private readonly Dictionary<string, Track> tracks = new Dictionary<string, Track>();
        private List<string> order = new List<string>();
        private UiFrame lastTarget;
        private string lastScreen;

        /// Reduced motion: every change applies immediately.
        public bool ReducedMotion { get; set; }
        public bool IsAnimating { get; private set; }

        public UiFrame Step(UiFrame target, long nowMs)
        {
            if (target != lastTarget)
                Retarget(target, nowMs);
            return Sample(nowMs);
        }

        private void Retarget(UiFrame target, long now)
        {
            var screenChanged = lastScreen != null && target.ScreenKey != lastScreen;
            lastScreen = target.ScreenKey;
            lastTarget = target;
            var seen = new HashSet<string>();
            var newOrder = new List<string>(target.Nodes.Count);
            var previousOrder = order;

            foreach (var node in target.Nodes)
            {
                if (node.Key == null || !seen.Add(node.Key))
                    continue;
                newOrder.Add(node.Key);
                if (tracks.TryGetValue(node.Key, out var track) && !screenChanged)
                {
                    var moved = track.Target.X != node.X || track.Target.Y != node.Y || track.Target.W != node.W
                        || track.Target.H != node.H || Math.Abs(track.Target.Opacity - node.Opacity) > 0.001 || track.Exiting;
                    if (moved)
                        Begin(track, track.X, track.Y, track.W, track.H, track.Opacity, now, node.MoveMs);
                    track.Exiting = false;
                    track.Target = node;
                    continue;
                }

                track = new Track { Target = node };
                tracks[node.Key] = track;
                double x = node.X, y = node.Y, w = node.W, h = node.H, opacity = 0;
                var duration = node.EnterMs;
                var enter = screenChanged ? UiEnter.SlideLeft : node.Enter;
                if (screenChanged)
                    duration = ScreenTransitionMs;
                switch (enter)
                {
                    case UiEnter.None: opacity = node.Opacity; duration = 0; break;
                    case UiEnter.SlideUp: y += 30; break;
                    case UiEnter.SlideLeft: x += ScreenSlidePx; break;
                    case UiEnter.Grow:
                        x += node.W * 0.04; y += node.H * 0.04; w *= 0.92; h *= 0.92;
                        break;
                }
                Begin(track, x, y, w, h, opacity, now, duration);
            }

            // Nodes that left the frame fade out (or vanish), keeping their place in the stacking order.
            var merged = new List<string>(newOrder);
            for (var i = 0; i < previousOrder.Count; i++)
            {
                var key = previousOrder[i];
                if (seen.Contains(key) || !tracks.TryGetValue(key, out var track))
                    continue;
                if (screenChanged || track.Target.ExitMs <= 0 || ReducedMotion || track.Exiting && track.Opacity <= 0.001)
                {
                    tracks.Remove(key);
                    continue;
                }
                if (!track.Exiting)
                {
                    track.Exiting = true;
                    Begin(track, track.X, track.Y, track.W, track.H, track.Opacity, now, track.Target.ExitMs);
                }
                // Insert before the next surviving node that followed it.
                var at = merged.Count;
                for (var j = i + 1; j < previousOrder.Count; j++)
                {
                    var index = merged.IndexOf(previousOrder[j]);
                    if (index >= 0 && seen.Contains(previousOrder[j]))
                    {
                        at = index;
                        break;
                    }
                }
                merged.Insert(at, key);
            }
            order = merged;
        }

        private void Begin(Track track, double x, double y, double w, double h, double opacity, long now, int duration)
        {
            track.FromX = track.X = x;
            track.FromY = track.Y = y;
            track.FromW = track.W = w;
            track.FromH = track.H = h;
            track.FromOpacity = track.Opacity = opacity;
            track.StartMs = now;
            track.DurationMs = ReducedMotion ? 0 : duration;
        }

        private UiFrame Sample(long now)
        {
            var frame = new UiFrame { Background = lastTarget?.Background, ScreenKey = lastTarget?.ScreenKey };
            var animating = false;
            var gone = new List<string>();
            foreach (var key in order)
            {
                if (!tracks.TryGetValue(key, out var track))
                    continue;
                var target = track.Target;
                double tx = target.X, ty = target.Y, tw = target.W, th = target.H, to = track.Exiting ? 0 : target.Opacity;
                var t = track.DurationMs <= 0 ? 1 : Math.Min(1, (now - track.StartMs) / (double)track.DurationMs);
                if (t >= 1 && !track.Exiting && track.EmittedFor == target)
                {
                    frame.Nodes.Add(track.Emitted);   // at rest since the last frame
                    continue;
                }
                // Two curves: things that travel or scale use the move curve; fades ease out when appearing and
                // ease in when leaving.
                var e = EaseMove(t);
                var fade = to >= track.FromOpacity ? EaseOutCubic(t) : EaseIn(t);
                track.X = track.FromX + (tx - track.FromX) * e;
                track.Y = track.FromY + (ty - track.FromY) * e;
                track.W = track.FromW + (tw - track.FromW) * e;
                track.H = track.FromH + (th - track.FromH) * e;
                track.Opacity = track.FromOpacity + (to - track.FromOpacity) * fade;
                if (t < 1)
                    animating = true;
                else if (track.Exiting)
                {
                    gone.Add(key);
                    continue;
                }
                var node = target.Clone();
                track.Emitted = node;
                track.EmittedFor = t >= 1 ? target : null;
                node.X = (int)Math.Round(track.X);
                node.Y = (int)Math.Round(track.Y);
                node.W = Math.Max(0, (int)Math.Round(track.W));
                node.H = Math.Max(0, (int)Math.Round(track.H));
                node.Opacity = Math.Round(Math.Max(0, Math.Min(1, track.Opacity)), 3);
                frame.Nodes.Add(node);
            }
            foreach (var key in gone)
            {
                tracks.Remove(key);
                order.Remove(key);
            }
            IsAnimating = animating;
            return frame;
        }

        public static double EaseOutCubic(double t)
        {
            var u = 1 - t;
            return 1 - u * u * u;
        }

        public static double EaseIn(double t) => t * t;

        /// The move curve, cubic-bezier(0.2, 0, 0, 1): quick off the mark with a long soft landing. Read from a
        /// table built once, so a frame costs one lookup per moving node.
        public static double EaseMove(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            var at = t * (MoveCurve.Length - 1);
            var i = (int)at;
            return MoveCurve[i] + (MoveCurve[i + 1] - MoveCurve[i]) * (at - i);
        }

        private static readonly double[] MoveCurve = Bezier(0.2, 0, 0, 1, 256);

        /// y of a CSS cubic-bezier(x1, y1, x2, y2) at `steps` + 1 evenly spaced x.
        private static double[] Bezier(double x1, double y1, double x2, double y2, int steps)
        {
            double At(double a, double b, double u) => 3 * a * u * (1 - u) * (1 - u) + 3 * b * u * u * (1 - u) + u * u * u;
            var table = new double[steps + 1];
            for (var i = 0; i <= steps; i++)
            {
                var x = i / (double)steps;
                double lo = 0, hi = 1;
                for (var n = 0; n < 40; n++)
                {
                    var mid = (lo + hi) / 2;
                    if (At(x1, x2, mid) < x) lo = mid; else hi = mid;
                }
                table[i] = At(y1, y2, (lo + hi) / 2);
            }
            table[0] = 0;
            table[steps] = 1;
            return table;
        }
    }

    /// Small icons. Each is one or two nodes: a rounded box for a disc and a glyph (UiGlyphs) for the mark, so an
    /// icon costs the painter one or two objects instead of the dozens of little rectangles it used to be built from.
    public static class UiIcons
    {
        public static void Circle(UiFrame f, string key, int cx, int cy, int radius, string fill, int band = 3) =>
            f.Box(key, cx - radius, cy - radius, 2 * radius, 2 * radius, fill).R(UiTheme.RadiusPill).Enter = UiEnter.None;

        /// Filled circle with a check mark: the watched badge. Same colours focused or not.
        public static void Check(UiFrame f, string key, int x, int y, int size, string fill = UiTheme.Watched, string mark = "#0B2512")
        {
            Circle(f, key, x + size / 2, y + size / 2, size / 2, fill);
            f.GlyphIn(key + ":m0", UiGlyphs.Check, x, y, size, size, size * 7 / 10, mark);
        }

        /// Disc with a play triangle: the resume badge.
        public static void Resume(UiFrame f, string key, int x, int y, int size, string fill = "#FFFFFF", string mark = UiTheme.TextOnFocus)
        {
            Circle(f, key, x + size / 2, y + size / 2, size / 2, fill);
            f.GlyphIn(key + ":t", UiGlyphs.Play, x + size / 24, y, size, size, size * 6 / 10, mark);
        }

        /// Right-pointing triangle `height` px tall with its top-left corner at (x, y).
        public static void PlayTriangle(UiFrame f, string key, int x, int y, int height, string fill)
        {
            var size = (int)Math.Round(height * 24 / 15.2);
            f.Glyph(key, UiGlyphs.Play, x - (int)Math.Round(7.6 * size / 24), y - (int)Math.Round(4.4 * size / 24), size, fill);
        }

        public static void Plus(UiFrame f, string key, int x, int y, int size, string fill) => f.Glyph(key, UiGlyphs.Plus, x, y, size, fill);

        /// Check mark in a size x size box (no circle): "in My List", the current choice of a list.
        public static void Tick(UiFrame f, string key, int x, int y, int size, string fill) => f.Glyph(key + ":m0", UiGlyphs.Check, x, y, size, fill);

        /// Magnifier in a size x size box.
        public static void Search(UiFrame f, string key, int x, int y, int size, string fill, string hole = null) => f.Glyph(key, UiGlyphs.Search, x, y, size, fill);

        /// Down chevron in a size x size box.
        public static void Chevron(UiFrame f, string key, int x, int y, int size, string fill) => f.Glyph(key, UiGlyphs.Down, x, y, size, fill);

        /// Thumbs-up in a size x size box.
        public static void Thumb(UiFrame f, string key, int x, int y, int size, string fill) => f.Glyph(key, UiGlyphs.Thumb, x, y, size, fill);

        public const int HealthW = 76;
        public const int HealthH = 20;

        /// Three health dots (HealthW x HealthH): 2 = healthy (3 green), 1 = unknown (2 amber), 0 = weak (1 red).
        public static void Health(UiFrame f, string key, int x, int y, int band)
        {
            var color = band >= 2 ? UiTheme.Watched : band == 1 ? UiTheme.Warn : UiTheme.Bad;
            var lit = band >= 2 ? 3 : band == 1 ? 2 : 1;
            for (var i = 0; i < 3; i++)
                Circle(f, key + ":" + i, x + 10 + i * 28, y + 10, 10, i < lit ? color : "#FFFFFF33");
        }
    }
}
