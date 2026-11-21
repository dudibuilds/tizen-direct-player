using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Ru7100.Core.Playback
{
    public sealed class HealthSample
    {
        public DateTimeOffset At { get; set; }
        public long PositionMs { get; set; }
    }

    /// Optional on IOpenedSource: how much media is ready to play from where the player reads. The health check
    /// trusts this over the engine's download rate, which can rise on pieces nowhere near the playhead.
    public interface IPlayheadSource
    {
        /// Contiguous verified bytes from the player's read position (0 when the next byte is missing); -1 unknown.
        long ContiguousBytesAhead { get; }
        /// The first missing piece at or after the read position; -1 when none (within the scan) or unknown.
        int FirstMissingPiece { get; }
        /// File offset up to which media is ready without a gap from the read position (read position plus
        /// ContiguousBytesAhead); -1 unknown. It only grows while data arrives at the playhead, even when the player
        /// consumes each piece as soon as it lands, so it is the "feeding" signal.
        long ReadyThroughBytes { get; }
    }

    /// Stream health from playhead progress (I5), tuned for the TV: a frozen playhead with no media arriving at it
    /// fails over in well under a minute, while one whose contiguous buffer is growing gets time to catch up.
    /// Throughput (I6) is only the fallback signal for sources that cannot report contiguous bytes.
    public sealed class StreamHealth
    {
        public static readonly TimeSpan Window = TimeSpan.FromSeconds(120);
        /// From the first sample after a start, seek or resume: buffering here is expected.
        public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);
        /// A playhead that has not moved this long is stalled.
        public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(20);
        /// Stalled (or repeatedly buffering) this long with nothing arriving at the playhead: replace the source.
        public static readonly TimeSpan RecoveryGrace = TimeSpan.FromSeconds(20);
        /// The longest a stall is tolerated while media keeps arriving at the playhead.
        public static readonly TimeSpan MaxRecoveryWait = TimeSpan.FromSeconds(180);
        /// Contiguous bytes growing within this window count as "feeding".
        public static readonly TimeSpan FeedingWindow = TimeSpan.FromSeconds(20);
        public const long FeedingStepBytes = 256 * 1024;
        public static readonly TimeSpan StarvedStop = TimeSpan.FromSeconds(10);

        public DateTimeOffset StartedAt { get; set; }
        public List<HealthSample> Samples { get; set; } = new List<HealthSample>();
        public DateTimeOffset? BadSince { get; set; }
        public DateTimeOffset? ImprovingAt { get; set; }
        public DateTimeOffset? LastProgressAt { get; set; }
        public double PlaybackRatio { get; set; } = 1;
        public double? ThroughputBytesPerSecond { get; set; }
        /// Last ready-frontier sample (IPlayheadSource.ReadyThroughBytes, -1 unknown) and when it last grew.
        public long ContiguousAhead { get; set; } = -1;
        public DateTimeOffset? FeedingAt { get; set; }
        public string Action { get; set; } = "wait";   // wait | replace
        public string Reason { get; set; }

        public StreamHealth Clone() => new StreamHealth
        {
            StartedAt = StartedAt,
            Samples = Samples.Select(s => new HealthSample { At = s.At, PositionMs = s.PositionMs }).ToList(),
            BadSince = BadSince, ImprovingAt = ImprovingAt, LastProgressAt = LastProgressAt, PlaybackRatio = PlaybackRatio,
            ThroughputBytesPerSecond = ThroughputBytesPerSecond, ContiguousAhead = ContiguousAhead, FeedingAt = FeedingAt,
            Action = Action, Reason = Reason,
        };

        /// paused: user pause. state: the player state at this sample. readyThrough: IPlayheadSource.ReadyThroughBytes
        /// (-1/null unknown).
        public static StreamHealth Observe(StreamHealth previous, DateTimeOffset now, long positionMs, PlayerState state,
            bool paused, double? throughputBytesPerSecond = null, long? readyThrough = null)
        {
            if (positionMs < 0)
                return previous;
            var health = previous?.Clone() ?? new StreamHealth { StartedAt = now };
            var last = health.Samples.LastOrDefault();
            if (paused || state == PlayerState.Paused || state == PlayerState.Stopped || state == PlayerState.Idle
                || (last != null && (positionMs < last.PositionMs - 3000
                    || positionMs > last.PositionMs + (long)(now - last.At).TotalMilliseconds + 15000
                    || now - last.At > TimeSpan.FromSeconds(30))))
                return new StreamHealth { StartedAt = now, Reason = "inactive_or_seek", Action = "wait" };
            if (state != PlayerState.Playing && state != PlayerState.Buffering && state != PlayerState.Preparing)
            {
                health.Action = "wait";
                health.Reason = "unknown_player_state";
                return health;
            }
            health.Samples.Add(new HealthSample { At = now, PositionMs = positionMs });
            health.Samples = health.Samples.Where(sample => now - sample.At <= Window).ToList();
            var first = health.Samples[0];
            var elapsed = (now - first.At).TotalMilliseconds;
            var advanced = Math.Max(0, positionMs - first.PositionMs);
            health.PlaybackRatio = elapsed > 0 ? Math.Min(1, advanced / elapsed) : 1;
            var moving = last != null && positionMs - last.PositionMs >= Math.Min(2000, (now - last.At).TotalMilliseconds * 0.5);
            if (moving)
                health.LastProgressAt = now;
            if (health.LastProgressAt == null)
                health.LastProgressAt = now;
            var stalled = now - health.LastProgressAt.Value >= StallAfter;
            var intermittent = elapsed >= 90000 && health.PlaybackRatio < 0.65;

            var playheadKnown = readyThrough.HasValue && readyThrough.Value >= 0;
            if (playheadKnown)
            {
                // Feeding: the ready frontier at the playhead moved forward (data arrived where the player needs it).
                if (health.ContiguousAhead >= 0 && readyThrough.Value >= health.ContiguousAhead + FeedingStepBytes)
                    health.FeedingAt = now;
                health.ContiguousAhead = readyThrough.Value;
            }
            else if (throughputBytesPerSecond.HasValue && throughputBytesPerSecond.Value >= 0
                && health.ThroughputBytesPerSecond.HasValue
                && throughputBytesPerSecond.Value > Math.Max(16384, health.ThroughputBytesPerSecond.Value * 1.25))
                health.ImprovingAt = now;
            if (throughputBytesPerSecond.HasValue && throughputBytesPerSecond.Value >= 0)
                health.ThroughputBytesPerSecond = throughputBytesPerSecond;

            if (!stalled && !intermittent)
            {
                health.BadSince = null;
                health.ImprovingAt = null;
                health.Action = "wait";
                health.Reason = "playing";
                return health;
            }
            if (health.BadSince == null)
                health.BadSince = now;
            var badFor = now - health.BadSince.Value;
            var warming = badFor < MaxRecoveryWait && (playheadKnown
                ? health.FeedingAt != null && now - health.FeedingAt.Value < FeedingWindow
                : health.ImprovingAt != null && now - health.ImprovingAt.Value < TimeSpan.FromSeconds(60));
            var grace = now - health.StartedAt < StartupGrace || badFor < RecoveryGrace;
            health.Action = grace || warming ? "wait" : "replace";
            health.Reason = grace ? "recovery_grace" : warming ? "source_improving" : stalled ? "persistent_stall" : "repeated_buffering";
            return health;
        }

        /// I7: playhead frozen at least 10 s and more than 30 s from the end.
        public static bool IsStarvedStop(StreamHealth health, DateTimeOffset now, long positionMs, long durationMs)
        {
            if (health == null || health.Samples.Count == 0 || health.LastProgressAt == null)
                return false;
            if (durationMs > 0 && durationMs - positionMs < 30000)
                return false;
            return now - health.LastProgressAt.Value >= StarvedStop;
        }
    }

    /// G4: an infoHash whose preparation failed after 10 s or more is avoided for 6 h. Persisted.
    public sealed class StallBlacklist
    {
        public const string StoreKey = "playback/stalled-sources";
        private readonly IKeyValueStore store;
        private readonly IClock clock;
        private readonly TimeSpan ttl;
        private readonly object gate = new object();
        private Dictionary<string, DateTimeOffset> entries = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private Task tail = Task.CompletedTask;

        public StallBlacklist(IKeyValueStore store, IClock clock, TimeSpan? ttl = null)
        {
            this.store = store;
            this.clock = clock;
            this.ttl = ttl ?? TimeSpan.FromHours(6);
        }

        public async Task LoadAsync(CancellationToken token)
        {
            string text = null;
            try { text = await store.ReadAsync(StoreKey, token).ConfigureAwait(false); }
            catch (Exception error) when (!(error is OperationCanceledException)) { }
            Dictionary<string, DateTimeOffset> loaded = null;
            if (!string.IsNullOrEmpty(text))
            {
                try { loaded = JsonConvert.DeserializeObject<Dictionary<string, DateTimeOffset>>(text); }
                catch (JsonException) { }
            }
            lock (gate)
                entries = new Dictionary<string, DateTimeOffset>(loaded ?? new Dictionary<string, DateTimeOffset>(), StringComparer.Ordinal);
        }

        public bool IsStalled(string infoHash)
        {
            if (string.IsNullOrEmpty(infoHash))
                return false;
            var key = infoHash.ToLowerInvariant();
            lock (gate)
            {
                if (!entries.TryGetValue(key, out var expires))
                    return false;
                if (expires > clock.UtcNow)
                    return true;
                entries.Remove(key);
                return false;
            }
        }

        public void Remember(string infoHash)
        {
            if (string.IsNullOrEmpty(infoHash))
                return;
            lock (gate)
                entries[infoHash.ToLowerInvariant()] = clock.UtcNow + ttl;
            Persist();
        }

        public void Forget(string infoHash)
        {
            if (string.IsNullOrEmpty(infoHash))
                return;
            bool removed;
            lock (gate)
                removed = entries.Remove(infoHash.ToLowerInvariant());
            if (removed)
                Persist();
        }

        public IReadOnlyDictionary<string, DateTimeOffset> Snapshot()
        {
            lock (gate)
                return new Dictionary<string, DateTimeOffset>(entries);
        }

        /// Completes when all pending writes are done (tests).
        public Task Flushed { get { lock (gate) return tail; } }

        private void Persist()
        {
            lock (gate)
            {
                var now = clock.UtcNow;
                foreach (var key in entries.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList())
                    entries.Remove(key);
                var text = JsonConvert.SerializeObject(entries);
                tail = tail.ContinueWith(_ => store.WriteAsync(StoreKey, text, CancellationToken.None),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap()
                    .ContinueWith(t => { var _ = t.Exception; }, TaskScheduler.Default);
            }
        }
    }
}
