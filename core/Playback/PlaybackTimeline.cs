using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Ru7100.Core.Playback
{
    /// A bounded, timestamped playback timeline for soak tests: HTTP ranges, seeks, buffering, recovery decisions,
    /// cache and throttle state, memory samples and listener lifecycle on one monotonic clock. Process-wide, cheap to
    /// write (a lock and a ring slot), and read by dev.diagnostics without touching the UI thread.
    public sealed class PlaybackTimeline
    {
        public static readonly PlaybackTimeline Shared = new PlaybackTimeline(600);

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly object gate = new object();
        private readonly Entry[] ring;
        private long written;

        public PlaybackTimeline(int capacity)
        {
            ring = new Entry[Math.Max(16, capacity)];
        }

        public struct Entry
        {
            /// Milliseconds since the process's first timeline use (monotonic).
            public long Ms;
            public DateTime Utc;
            public string Kind;
            public string Detail;

            public override string ToString() =>
                Ms.ToString(CultureInfo.InvariantCulture) + " " + Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + Kind + " " + Detail;
        }

        public long Count { get { lock (gate) return written; } }

        public void Add(string kind, string detail)
        {
            var entry = new Entry { Ms = Clock.ElapsedMilliseconds, Utc = DateTime.UtcNow, Kind = kind ?? "", Detail = detail ?? "" };
            lock (gate)
            {
                ring[written % ring.Length] = entry;
                written++;
            }
        }

        /// The newest entries, oldest first. kind: only that kind (null: all).
        public IReadOnlyList<Entry> Snapshot(int max = 200, string kind = null)
        {
            var result = new List<Entry>();
            lock (gate)
            {
                var available = (int)Math.Min(written, ring.Length);
                for (var i = 0; i < available; i++)
                {
                    var entry = ring[(written - 1 - i) % ring.Length];
                    if (kind != null && entry.Kind != kind)
                        continue;
                    result.Add(entry);
                    if (result.Count >= max)
                        break;
                }
            }
            result.Reverse();
            return result;
        }

        public IReadOnlyList<string> Lines(int max = 200, string kind = null)
        {
            var lines = new List<string>();
            foreach (var entry in Snapshot(max, kind))
                lines.Add(entry.ToString());
            return lines;
        }
    }
}
