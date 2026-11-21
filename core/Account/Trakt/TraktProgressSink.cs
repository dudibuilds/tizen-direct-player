using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account.Trakt
{
    /// Optional Trakt scrobbling (K4) on its own ordered outbox, so a Trakt outage never holds up
    /// Stremio writes. Scrobbles are skipped while Trakt is not authenticated. Queued start/pause
    /// scrobbles older than StaleAfter are dropped on replay ("watching now" would be false); stop
    /// scrobbles are always replayed so offline viewing still reaches Trakt history.
    public sealed class TraktProgressSink : IProgressSink
    {
        public const long StartResendIntervalMs = 60000;
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        readonly TraktClient client;
        readonly Func<DateTimeOffset> now;
        readonly OutboxQueue outbox;
        readonly object sync = new object();
        string sessionKey;
        long lastSentMs;
        long lastPositionMs;
        long durationMs;
        bool paused;
        bool markedWatched;

        public TraktProgressSink(TraktClient client, IKeyValueStore store, Func<DateTimeOffset> now = null)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            outbox = new OutboxQueue(store, AccountKeys.TraktOutbox, ApplyAsync);
        }

        public string LastError => outbox.LastError;
        public int PendingWrites => outbox.Count;
        public Task<bool> FlushAsync(CancellationToken token) => outbox.DrainAsync(token);

        public Task PlaybackStartedAsync(MetaDetail meta, VideoInfo video, long resumeMs, CancellationToken token)
        {
            lock (sync)
            {
                sessionKey = Key(meta, video);
                lastSentMs = resumeMs;
                lastPositionMs = resumeMs;
                durationMs = 0;
                paused = false;
                markedWatched = false;
            }
            return EnqueueAsync("start", meta, video, 0, token);
        }

        /// Re-sends start every 60 s of progress, and immediately after a pause (resume).
        public Task ProgressAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token)
        {
            double progress;
            lock (sync)
            {
                Track(meta, video, positionMs, durationMs);
                bool resumed = paused;
                paused = false;
                if (!resumed && positionMs - lastSentMs < StartResendIntervalMs) return Task.CompletedTask;
                lastSentMs = positionMs;
                progress = Percent();
            }
            return EnqueueAsync("start", meta, video, progress, token);
        }

        public Task PausedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token)
        {
            double progress;
            lock (sync)
            {
                Track(meta, video, positionMs, durationMs);
                paused = true;
                progress = Percent();
            }
            return EnqueueAsync("pause", meta, video, progress, token);
        }

        public Task CompletedAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            lock (sync)
            {
                if (sessionKey != Key(meta, video)) Track(meta, video, 0, 0);
                markedWatched = true;
            }
            return Task.CompletedTask;
        }

        /// stop with 100 when marked watched, else the actual percentage.
        public Task StoppedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, bool markedWatched, CancellationToken token)
        {
            double progress;
            lock (sync)
            {
                Track(meta, video, positionMs, durationMs);
                progress = markedWatched || this.markedWatched ? 100 : Percent();
                sessionKey = null;
            }
            return EnqueueAsync("stop", meta, video, progress, token);
        }

        void Track(MetaDetail meta, VideoInfo video, long positionMs, long duration)
        {
            var key = Key(meta, video);
            if (sessionKey != key)
            {
                sessionKey = key;
                lastSentMs = 0;
                paused = false;
                markedWatched = false;
                durationMs = 0;
            }
            lastPositionMs = positionMs;
            if (duration > 0) durationMs = duration;
        }

        double Percent() => durationMs > 0 ? Math.Max(0, Math.Min(100, (double)lastPositionMs / durationMs * 100)) : 0;

        async Task EnqueueAsync(string action, MetaDetail meta, VideoInfo video, double progress, CancellationToken token)
        {
            if (!await client.IsAuthenticatedAsync(token).ConfigureAwait(false)) return;
            var summary = meta.Summary;
            var op = new JObject
            {
                ["action"] = action,
                ["at"] = IsoTime.Format(now()),
                ["progress"] = progress,
                ["type"] = LibraryRules.TypeName(summary.Type),
                ["imdb"] = summary.Id,
                ["title"] = summary.Name,
                ["season"] = video?.Season ?? 0,
                ["episode"] = video?.Episode ?? 0,
            };
            await outbox.EnqueueAsync(op, token).ConfigureAwait(false);
        }

        async Task ApplyAsync(JObject op, CancellationToken token)
        {
            var action = (string)op["action"];
            var at = IsoTime.Parse(op["at"]) ?? now();
            if (action != "stop" && now() - at > StaleAfter) return;
            var target = new TraktScrobbleTarget
            {
                Type = (string)op["type"] == "series" ? MediaType.Series : MediaType.Movie,
                ImdbId = (string)op["imdb"],
                Title = (string)op["title"],
                Season = (int)(op["season"] ?? 0),
                Episode = (int)(op["episode"] ?? 0),
            };
            await client.ScrobbleAsync(action, target, (double)op["progress"], token).ConfigureAwait(false);
        }

        static string Key(MetaDetail meta, VideoInfo video) => (meta?.Summary?.Id ?? "") + "|" + (video?.Id ?? "");
    }
}
