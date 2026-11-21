using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Playback
{
    /// Maps a subtitle's own clock to the playing file's: video = Scale × subtitle + OffsetMs. Kept apart from the
    /// viewer's offset (the Timing control), which shifts on top of it and keeps its ±30 s range.
    public struct SubtitleTiming
    {
        public double Scale;
        public long OffsetMs;
        /// "reference" (aligned to a subtitle made for this release), "offset" (from the viewer's offset settings),
        /// "remembered" (a speed fix learned on another episode of the same release), or null.
        public string Source;

        public static SubtitleTiming Identity => new SubtitleTiming { Scale = 1 };

        public bool IsIdentity => Math.Abs(Scale - 1) < 1e-9 && OffsetMs == 0 || Scale == 0;

        public long ToSubtitle(long videoMs) => Scale == 0 ? videoMs : (long)Math.Round((videoMs - OffsetMs) / Scale);

        public long ToVideo(long subtitleMs) => Scale == 0 ? subtitleMs : (long)Math.Round(subtitleMs * Scale + OffsetMs);

        /// The speed change as a frame-rate pair when it is one ("25 → 23.976"), else null.
        public string SpeedName
        {
            get
            {
                if (Scale == 0 || Math.Abs(Scale - 1) < 0.002) return null;
                foreach (var (from, to) in SubtitleAligner.FrameRates)
                    if (Math.Abs(Scale - from / to) < 0.0015)
                        return Fps(from) + " → " + Fps(to);
                return null;
            }
        }

        private static string Fps(double fps) => Math.Abs(fps - Math.Round(fps)) < 0.001 ? ((int)Math.Round(fps)).ToString() : fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed class AlignResult
    {
        public bool Accepted { get; set; }
        public SubtitleTiming Timing { get; set; } = SubtitleTiming.Identity;
        /// Share of cues whose start lands on a reference start (of the shorter list).
        public double Score { get; set; }
        /// The same measure at offsets that cannot be right: what chance alone gives.
        public double Noise { get; set; }
        /// Best score at a clearly different speed.
        public double RunnerUp { get; set; }
        public string Reason { get; set; }

        public override string ToString() =>
            (Accepted ? "accepted" : "rejected (" + Reason + ")") + $" scale {Timing.Scale:0.#####} offset {Timing.OffsetMs} ms score {Score:0.00} noise {Noise:0.00} runner-up {RunnerUp:0.00}";
    }

    /// Lines a subtitle up with a reference timeline (cue start times of another subtitle made for the playing file,
    /// in any language: lines start when someone starts speaking, whatever the words).
    ///
    /// Releases differ by a shift (a logo, a trimmed recap) or a speed: a 25 fps master played at 23.976 runs 4.3 %
    /// slower, so a subtitle timed for one drifts against the other by about 2.5 s a minute, which no offset fixes.
    /// For each standard frame-rate ratio the offset is found by voting over cue-start differences, refined to the
    /// offset with the most coinciding starts, then fitted by least squares on those pairs. "No change" is the
    /// expected answer: a result is used only when it clearly beats chance, beats every other speed, and holds in
    /// each third of the episode (a release with a different cut fails that and is left alone).
    public static class SubtitleAligner
    {
        public static readonly (double From, double To)[] FrameRates =
        {
            (25, 24000 / 1001.0), (24000 / 1001.0, 25), (24, 24000 / 1001.0), (24000 / 1001.0, 24), (25, 24), (24, 25),
            (30000 / 1001.0, 25), (25, 30000 / 1001.0),
        };

        private const long MaxShiftMs = 10 * 60 * 1000;
        private const long BinMs = 250;
        private const long ToleranceMs = 300;
        public const double MinScore = 0.30;
        public const int MinCues = 60;

        public static AlignResult Align(IReadOnlyList<long> target, IReadOnlyList<long> reference)
        {
            var t = (target ?? Array.Empty<long>()).Where(x => x >= 0).OrderBy(x => x).ToArray();
            var r = (reference ?? Array.Empty<long>()).Where(x => x >= 0).OrderBy(x => x).ToArray();
            if (t.Length < MinCues || r.Length < MinCues)
                return new AlignResult { Reason = "too few cues" };
            var denominator = Math.Min(t.Length, r.Length);
            var ratios = new[] { 1.0 }.Concat(FrameRates.Select(f => f.From / f.To)).ToArray();
            var candidates = new List<(double Scale, long Offset, int Matches)>();
            foreach (var ratio in ratios)
            {
                var offset = Vote(t, r, ratio);
                if (offset == null) continue;
                var (best, matches) = Refine(t, r, ratio, offset.Value);
                var (scale, fitted) = Fit(t, r, ratio, best);
                var fittedMatches = Count(t, r, scale, fitted);
                if (fittedMatches >= matches)
                    candidates.Add((scale, fitted, fittedMatches));
                else
                    candidates.Add((ratio, best, matches));
            }
            if (candidates.Count == 0)
                return new AlignResult { Reason = "no overlap" };
            var top = candidates.OrderByDescending(c => c.Matches).First();
            var score = top.Matches / (double)denominator;
            var runnerUp = candidates.Where(c => Math.Abs(c.Scale / top.Scale - 1) > 0.005).Select(c => c.Matches / (double)denominator).DefaultIfEmpty(0).Max();
            var noise = new[] { -45000L, -20000L, 20000L, 45000L }.Max(d => Count(t, r, top.Scale, top.Offset + d)) / (double)denominator;
            var result = new AlignResult
            {
                Timing = new SubtitleTiming { Scale = top.Scale, OffsetMs = top.Offset, Source = "reference" },
                Score = score, Noise = noise, RunnerUp = runnerUp,
            };
            if (score < MinScore) result.Reason = "weak match";
            else if (score < 2.5 * noise) result.Reason = "not above chance";
            else if (score < runnerUp + 0.12) result.Reason = "speed ambiguous";
            else if (!HoldsThroughout(t, r, top.Scale, top.Offset)) result.Reason = "differs in part of the episode";
            else result.Accepted = true;
            if (!result.Accepted)
                result.Timing = SubtitleTiming.Identity;
            return result;
        }

        /// The most common (reference − ratio × target) difference within ±10 min, by 250 ms bins (with neighbours).
        private static long? Vote(long[] t, long[] r, double ratio)
        {
            var bins = new Dictionary<long, int>();
            var lo = 0;
            foreach (var x in t)
            {
                var at = ratio * x;
                while (lo < r.Length && r[lo] < at - MaxShiftMs) lo++;
                for (var j = lo; j < r.Length && r[j] <= at + MaxShiftMs; j++)
                {
                    var bin = (long)Math.Floor((r[j] - at) / BinMs);
                    bins.TryGetValue(bin, out var n);
                    bins[bin] = n + 1;
                }
            }
            if (bins.Count == 0) return null;
            int Around(long b) => (bins.TryGetValue(b - 1, out var a) ? a : 0) + bins[b] + (bins.TryGetValue(b + 1, out var c) ? c : 0);
            var peak = bins.Keys.OrderByDescending(Around).ThenBy(b => Math.Abs(b)).First();
            return peak * BinMs + BinMs / 2;
        }

        private static (long Offset, int Matches) Refine(long[] t, long[] r, double scale, long around)
        {
            long best = around;
            var most = -1;
            for (var d = -600L; d <= 600; d += 25)
            {
                var n = Count(t, r, scale, around + d);
                if (n > most || n == most && Math.Abs(d) < Math.Abs(best - around)) { most = n; best = around + d; }
            }
            return (best, most);
        }

        /// Least-squares line through the coinciding pairs; the speed stays near the ratio it started from.
        private static (double Scale, long Offset) Fit(long[] t, long[] r, double scale, long offset)
        {
            double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var x in t)
            {
                var y = Nearest(r, scale * x + offset);
                if (y == null || Math.Abs(y.Value - (scale * x + offset)) > ToleranceMs) continue;
                n++; sx += x; sy += y.Value; sxx += (double)x * x; sxy += (double)x * y.Value;
            }
            if (n < MinCues) return (scale, offset);
            var den = n * sxx - sx * sx;
            if (Math.Abs(den) < 1e-6) return (scale, offset);
            var a = (n * sxy - sx * sy) / den;
            var b = (sy - a * sx) / n;
            if (Math.Abs(a / scale - 1) > 0.003) return (scale, offset);
            return (a, (long)Math.Round(b));
        }

        private static int Count(long[] t, long[] r, double scale, long offset)
        {
            var n = 0;
            foreach (var x in t)
            {
                var at = scale * x + offset;
                var y = Nearest(r, at);
                if (y != null && Math.Abs(y.Value - at) <= ToleranceMs) n++;
            }
            return n;
        }

        /// Each third of the target's span must match at least half as well as the whole.
        private static bool HoldsThroughout(long[] t, long[] r, double scale, long offset)
        {
            var overall = Count(t, r, scale, offset) / (double)t.Length;
            var first = t[0];
            var span = t[t.Length - 1] - first;
            for (var part = 0; part < 3; part++)
            {
                var from = first + span * part / 3;
                var to = first + span * (part + 1) / 3;
                var slice = t.Where(x => x >= from && (part == 2 ? x <= to : x < to)).ToArray();
                if (slice.Length < 10) continue;
                if (Count(slice, r, scale, offset) / (double)slice.Length < 0.5 * overall) return false;
            }
            return true;
        }

        private static long? Nearest(long[] sorted, double at)
        {
            if (sorted.Length == 0) return null;
            int lo = 0, hi = sorted.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (sorted[mid] < at) lo = mid + 1; else hi = mid;
            }
            var best = sorted[lo];
            if (lo > 0 && Math.Abs(sorted[lo - 1] - at) < Math.Abs(best - at)) best = sorted[lo - 1];
            return best;
        }
    }
}
