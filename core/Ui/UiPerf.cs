using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// One painted frame as the host measured it.
    public struct UiFrameSample
    {
        public long AtMs;
        /// Time since the previous painted frame when that frame was the tick right before this one (an animation
        /// in flight); 0 for the first frame of a run. The painter's own work plus the toolkit's render shows up here.
        public double IntervalMs;
        public double BuildMs, StepMs, LowerMs, PaintMs;
        public int Nodes, Created, Removed, Updated, Moved, TextSet, Restacked, FileLoads;
        public double FileLoadMs;
    }

    /// One late main-loop tick (UiPerfStats.Heartbeat).
    public struct UiStall
    {
        public double AtMs, GapMs;
        public string Note;
    }

    /// Rolling paint statistics for a UI host (the TV painter): frames per second while animating, the worst
    /// frame, and how much work each frame did. Not thread safe: record and read on the UI thread.
    /// Names the screen work about to run on the UI thread (the result of a Load), so a profiler timing the posted
    /// action can say what it was. UI thread only.
    public static class UiWork
    {
        [System.ThreadStatic] public static string Current;
    }

    public sealed class UiPerfStats
    {
        public const int WindowMs = 10000;
        private const int Capacity = 600;
        private readonly Queue<UiFrameSample> recent = new Queue<UiFrameSample>();
        private long frames, created, removed, updated, moved, textSet, restacked, fileLoads;
        private double fileLoadMs, worstIntervalMs, worstPaintMs, animatedMs;
        private long animatedFrames;
        private long sinceMs = -1;

        public void Record(UiFrameSample sample)
        {
            if (sinceMs < 0)
                sinceMs = sample.AtMs;
            recent.Enqueue(sample);
            while (recent.Count > Capacity)
                recent.Dequeue();
            frames++;
            created += sample.Created;
            removed += sample.Removed;
            updated += sample.Updated;
            moved += sample.Moved;
            textSet += sample.TextSet;
            restacked += sample.Restacked;
            fileLoads += sample.FileLoads;
            fileLoadMs += sample.FileLoadMs;
            worstPaintMs = Math.Max(worstPaintMs, sample.PaintMs);
            if (sample.IntervalMs > 0)
            {
                animatedFrames++;
                animatedMs += sample.IntervalMs;
                worstIntervalMs = Math.Max(worstIntervalMs, sample.IntervalMs);
            }
        }

        // ---- main-loop heartbeat ----
        // The host's timer calls Heartbeat on every tick, animating or not. A tick that arrives late means the main
        // loop was busy with something else (an image decoded inside the toolkit's render, a long paint, a GC):
        // the viewer sees that as a frozen frame or a key that answers late. Frame intervals above only cover
        // animation runs; this covers everything.
        public const double StallFactor = 2.5;
        /// A tick later than this many periods missed at least one frame (a hitch too short to count as a stall).
        public const double MissedFrameFactor = 1.5;
        private long beats, stalls, missedFrames;
        private double lastBeatMs = -1, longestStallMs, stalledMs;
        private readonly Queue<UiStall> recentStalls = new Queue<UiStall>();

        /// One timer tick at `nowMs`; `expectedMs` is the timer period. `note` says what the host did since the last
        /// tick (images handed to the toolkit, a layout rebuild), so a stall can be traced to its cause.
        public void Heartbeat(double nowMs, double expectedMs, string note = null)
        {
            beats++;
            if (lastBeatMs >= 0)
            {
                var gap = nowMs - lastBeatMs;
                if (gap > expectedMs * MissedFrameFactor)
                    missedFrames++;
                if (gap > expectedMs * StallFactor)
                {
                    stalls++;
                    stalledMs += gap - expectedMs;
                    longestStallMs = Math.Max(longestStallMs, gap);
                    recentStalls.Enqueue(new UiStall { AtMs = nowMs, GapMs = gap, Note = note });
                    while (recentStalls.Count > 12)
                        recentStalls.Dequeue();
                }
            }
            lastBeatMs = nowMs;
        }

        public long Stalls => stalls;
        /// Ticks that came more than 1.5 periods apart: single missed frames included (stalls are a subset).
        public long MissedFrames => missedFrames;
        public double LongestStallMs => longestStallMs;

        private object MainLoop(double nowMs) => new
        {
            ticks = beats,
            missedFrames,
            stalls,
            longestStallMs = R(longestStallMs),
            stalledMs = R(stalledMs),
            recent = recentStalls.Reverse().Select(s => new { stallMs = R(s.GapMs), agoMs = R(nowMs - s.AtMs), after = s.Note }).ToList()
        };

        /// The toolkit finished drawing a frame to the screen (EFL render-post). Thread safe.
        public void Rendered() => System.Threading.Interlocked.Increment(ref renders);

        private long renders;

        public void Reset()
        {
            System.Threading.Interlocked.Exchange(ref renders, 0);
            recent.Clear();
            beats = stalls = missedFrames = 0;
            lastBeatMs = -1;
            longestStallMs = stalledMs = 0;
            recentStalls.Clear();
            frames = created = removed = updated = moved = textSet = restacked = fileLoads = animatedFrames = 0;
            fileLoadMs = worstIntervalMs = worstPaintMs = animatedMs = 0;
            sinceMs = -1;
        }

        private static double R(double value) => Math.Round(value, 1);

        public object Snapshot(long nowMs)
        {
            var window = recent.Where(s => nowMs - s.AtMs <= WindowMs).ToList();
            var animated = window.Where(s => s.IntervalMs > 0).Select(s => s.IntervalMs).OrderBy(v => v).ToList();
            object last10 = null;
            if (window.Count > 0)
            {
                last10 = new
                {
                    frames = window.Count,
                    animatedFrames = animated.Count,
                    fps = animated.Count == 0 ? 0 : R(1000 * animated.Count / animated.Sum()),
                    frameMsAvg = animated.Count == 0 ? 0 : R(animated.Average()),
                    frameMsP95 = animated.Count == 0 ? 0 : R(animated[Math.Min(animated.Count - 1, (int)(animated.Count * 0.95))]),
                    frameMsWorst = animated.Count == 0 ? 0 : R(animated[animated.Count - 1]),
                    buildMsAvg = R(window.Where(s => s.BuildMs > 0).Select(s => s.BuildMs).DefaultIfEmpty(0).Average()),
                    buildMsMax = R(window.Max(s => s.BuildMs)),
                    stepMsAvg = R(window.Average(s => s.StepMs)),
                    lowerMsAvg = R(window.Average(s => s.LowerMs)),
                    paintMsAvg = R(window.Average(s => s.PaintMs)),
                    paintMsMax = R(window.Max(s => s.PaintMs)),
                    nodesAvg = (int)window.Average(s => s.Nodes),
                    createdPerFrame = R(window.Average(s => s.Created)),
                    updatedPerFrame = R(window.Average(s => s.Updated)),
                    movedPerFrame = R(window.Average(s => s.Moved)),
                    textSetPerFrame = R(window.Average(s => s.TextSet)),
                    restackedPerFrame = R(window.Average(s => s.Restacked)),
                    fileLoads = window.Sum(s => s.FileLoads),
                    fileLoadMs = R(window.Sum(s => s.FileLoadMs)),
                    framesOver50Ms = animated.Count(v => v > 50),
                    framesOver100Ms = animated.Count(v => v > 100),
                    // The slowest frames with the work they did, to tell a decode hitch from a restack or a rebuild.
                    worst = window.Where(s => s.IntervalMs > 0).OrderByDescending(s => s.IntervalMs).Take(6).Select(s => new
                    {
                        frameMs = R(s.IntervalMs), agoMs = nowMs - s.AtMs, buildMs = R(s.BuildMs), paintMs = R(s.PaintMs), s.Created, s.Removed, s.Updated,
                        s.TextSet, s.Restacked, s.FileLoads, fileLoadMs = R(s.FileLoadMs)
                    }).ToList(),
                };
            }
            return new
            {
                sinceReset = new
                {
                    seconds = sinceMs < 0 ? 0 : R((nowMs - sinceMs) / 1000.0),
                    frames,
                    renders = System.Threading.Interlocked.Read(ref renders),
                    animatedFrames,
                    fps = animatedMs <= 0 ? 0 : R(1000 * animatedFrames / animatedMs),
                    frameMsWorst = R(worstIntervalMs),
                    paintMsWorst = R(worstPaintMs),
                    created, removed, updated, moved, textSet, restacked, fileLoads,
                    fileLoadMs = R(fileLoadMs),
                },
                last10s = last10,
                // Main-loop time: late timer ticks, the longest one, and what the host had just done before each.
                mainLoop = MainLoop(nowMs),
            };
        }
    }
}
