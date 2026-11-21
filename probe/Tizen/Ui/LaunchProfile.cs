using System;
using System.Collections.Generic;
using System.Diagnostics;
using Ru7100.Core.Ui;

namespace Ru7100.TvUi
{
    /// Frame-by-frame profile of the launch (the splash, the intro and its hand-off into Home), read back through
    /// dev.diagnostics ("launchProfile"). Every main-loop tick while it is recording adds one line: the time since the
    /// process started, the gap since the previous tick (a gap well over 33 ms is a dropped frame), what the frame cost
    /// (layout build, animation step, lowering, painting, file loads), images still pending, garbage collections,
    /// and the intro's state. Slow work posted to the main loop (image decodes, dispatcher posts, UI construction)
    /// is noted separately with its own duration, so a stall can be traced to its cause.
    internal static class LaunchProfile
    {
        private const int MaxLines = 700;
        private static readonly object Gate = new object();
        private static readonly List<string> Lines = new List<string>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long untilMs = 25_000;   // stops itself (intro done + a few seconds, or this cap)
        private static int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        private static double lastTickMs = -1;

        /// Process-relative milliseconds (the same clock as the "+N ms" start-up trace).
        public static Func<long> ProcessMs = () => Clock.ElapsedMilliseconds;

        /// When the main loop last did heavy work (a frame or a posted action over ~15 ms), on this class's clock.
        /// The launch intro waits for a quiet stretch after it before the logo's flight.
        public static double HeavyAt = -1;
        public static double NowMs => Clock.Elapsed.TotalMilliseconds;
        public static double MsSinceHeavy => HeavyAt < 0 ? double.MaxValue : Clock.Elapsed.TotalMilliseconds - HeavyAt;

        public static bool Recording => ProcessMs() < untilMs && Lines.Count < MaxLines;

        /// Keep recording for `ms` more (called when the intro finishes).
        public static void StopAfter(long ms) => untilMs = Math.Min(untilMs, ProcessMs() + ms);

        public static void Tick(UiFrameSample sample, bool painted, int pendingImages, string intro)
        {
            if (painted && (sample.BuildMs + sample.StepMs + sample.LowerMs + sample.PaintMs >= 15 || sample.Created > 8))
                HeavyAt = Clock.Elapsed.TotalMilliseconds;
            if (!Recording) return;
            var now = Clock.Elapsed.TotalMilliseconds;
            var gap = lastTickMs < 0 ? 0 : now - lastTickMs;
            lastTickMs = now;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var gc = (g0 - gc0) + (g1 - gc1) * 10 + (g2 - gc2) * 100;
            gc0 = g0; gc1 = g1; gc2 = g2;
            var line = "+" + ProcessMs() + " gap " + gap.ToString("0") + (gap > 50 ? " SLOW" : "") +
                (painted
                    ? " build " + sample.BuildMs.ToString("0") + " step " + sample.StepMs.ToString("0") + " lower " + sample.LowerMs.ToString("0") +
                      " paint " + sample.PaintMs.ToString("0") + " nodes " + sample.Nodes + " new " + sample.Created + " text " + sample.TextSet +
                      (sample.FileLoads > 0 ? " files " + sample.FileLoads + "/" + sample.FileLoadMs.ToString("0") + "ms" : "")
                    : " idle") +
                " pending " + pendingImages + (gc > 0 ? " gc " + gc : "") + (intro != null ? " | " + intro : "");
            lock (Gate) Lines.Add(line);
        }

        /// Slow work on the main loop outside the frame itself.
        public static void Note(string what, double ms)
        {
            if (!Recording || ms < 15) return;
            lock (Gate) Lines.Add("+" + ProcessMs() + " MAIN " + ms.ToString("0") + " ms " + what);
        }

        public static void Mark(string what)
        {
            if (!Recording) return;
            lock (Gate) Lines.Add("+" + ProcessMs() + " ** " + what);
        }

        public static string[] Snapshot()
        {
            lock (Gate) return Lines.ToArray();
        }

        /// Runs `action`, noting it when it held the main loop for long.
        public static void Timed(string what, Action action)
        {
            var start = Clock.Elapsed.TotalMilliseconds;
            UiWork.Current = null;
            try { action(); }
            finally
            {
                var ms = Clock.Elapsed.TotalMilliseconds - start;
                if (ms >= 15) HeavyAt = start + ms;
                if (Recording) Note(UiWork.Current != null ? what + " -> " + UiWork.Current : what, ms);
                UiWork.Current = null;
            }
        }
    }
}
