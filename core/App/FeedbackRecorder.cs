using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ru7100.Core.Api;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using PlaybackSession = Ru7100.Core.Playback.PlaybackSession;

namespace Ru7100.Core.App
{

    /// Records viewing behaviour into the recommendations feedback log (IRecommendationsStore), which the Mac's
    /// deterministic learner pulls with an ack cursor. Viewing events are derived from PlaybackSession status changes;
    /// one "item" is one video (episode or movie) of one title, so a source switch, recovery or restore does not start
    /// a new item.
    ///
    ///   play_started  the item became active (once per item; a restore after an app restart counts again).
    ///   progress      the furthest position crossed 25 %, 50 % or 75 % of the duration (each once per item).
    ///   completed     the engine marked the item watched (parity E3: at least 90 % with two credible samples, or
    ///                 the player reached the end).
    ///   abandoned     the item ended without completing, by the user's choice (stop, playing something else, skip,
    ///                 a single item stopping) after at least AbandonMinWatchMs (2 min) of watching beyond where it
    ///                 started. Shorter tries and ends caused by failures (stream_failed, start_failed,
    ///                 advance_failed, app_restart) record nothing beyond play_started, so a bad source or an
    ///                 accidental click does not read as dislike.
    public sealed class FeedbackRecorder : IFeedbackSink, IDisposable
    {
        public const string HiddenKey = "app/hidden-titles";
        public const long AbandonMinWatchMs = 120_000;
        public static readonly double[] Milestones = { 0.25, 0.5, 0.75 };
        private static readonly Regex Imdb = new Regex("^tt\\d{5,10}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> FailureEnds = new HashSet<string>(StringComparer.Ordinal)
            { "stream_failed", "start_failed", "advance_failed", "app_restart", "queue_missing" };
        /// Ends followed by the same video on a new source; the item continues.
        private static readonly HashSet<string> ContinuingEnds = new HashSet<string>(StringComparer.Ordinal)
            { "stream_recovery", "restore" };

        private readonly IRecommendationsStore store;
        private readonly IKeyValueStore kv;
        private readonly Func<DateTimeOffset> now;
        private readonly Action<string> log;
        private readonly object chainGate = new object();
        private readonly SemaphoreSlim hiddenGate = new SemaphoreSlim(1, 1);
        private Task tail = Task.CompletedTask;
        private PlaybackSession session;
        private Item item;
        private HashSet<string> hidden;

        public FeedbackRecorder(IRecommendationsStore store, IKeyValueStore kv = null, Func<DateTimeOffset> now = null, Action<string> log = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.kv = kv;
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            this.log = log;
        }

        private sealed class Item
        {
            public string MetaId;
            public string VideoId;
            public long StartPositionMs;
            public long MaxPositionMs;
            public long LastPositionMs;
            public long DurationMs;
            public bool Completed;
            public readonly HashSet<double> Reached = new HashSet<double>();
        }

        public void Attach(PlaybackSession playback)
        {
            if (session != null) throw new InvalidOperationException("Already attached.");
            session = playback ?? throw new ArgumentNullException(nameof(playback));
            session.StatusChanged += OnStatus;
        }

        /// Completes when every event observed so far has been written (tests, shutdown).
        public Task FlushAsync()
        {
            lock (chainGate) return tail;
        }

        /// Feeds one status snapshot. Called by StatusChanged; tests may call it directly.
        public void Observe(PlaybackStatus status)
        {
            if (status == null) return;
            lock (chainGate)
                tail = tail.ContinueWith(_ => ProcessAsync(status), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }

        public Task ThumbsUpAsync(string metaId, CancellationToken token) => ExplicitAsync(metaId, FeedbackKinds.ThumbsUp, false, token);
        public Task ThumbsDownAsync(string metaId, CancellationToken token) => ExplicitAsync(metaId, FeedbackKinds.ThumbsDown, true, token);
        public Task NotInterestedAsync(string metaId, CancellationToken token) => ExplicitAsync(metaId, FeedbackKinds.NotInterested, true, token);

        /// Library edits (from the remote or the agent API) go through FeedbackLibrary, which calls this.
        public Task LibraryChangedAsync(string metaId, bool added, CancellationToken token) =>
            RecordAsync(new FeedbackEvent { ImdbId = metaId, Kind = added ? FeedbackKinds.AddedToLibrary : FeedbackKinds.Removed }, token);

        public async Task<IReadOnlyCollection<string>> GetHiddenAsync(CancellationToken token)
        {
            await hiddenGate.WaitAsync(token).ConfigureAwait(false);
            try { return (await LoadHiddenAsync(token).ConfigureAwait(false)).ToList(); }
            finally { hiddenGate.Release(); }
        }

        public void Dispose()
        {
            if (session != null) session.StatusChanged -= OnStatus;
        }

        // ---------------- derivation ----------------

        private void OnStatus(object sender, PlaybackStatus status) => Observe(status);

        private async Task ProcessAsync(PlaybackStatus status)
        {
            try
            {
                var events = Derive(status);
                foreach (var feedback in events)
                    await RecordAsync(feedback, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                log?.Invoke("feedback: " + error.Message);
            }
        }

        /// Pure state machine over status snapshots; returns the events to record.
        private List<FeedbackEvent> Derive(PlaybackStatus status)
        {
            var events = new List<FeedbackEvent>();
            var current = status.Session;
            if (status.Active && current != null)
            {
                if (item != null && (item.MetaId != current.MetaId || item.VideoId != current.VideoId))
                {
                    // Another video took over: the previous one ended for the reason recorded on it.
                    var previous = status.LastSession;
                    var reason = previous != null && previous.VideoId == item.VideoId ? previous.EndReason : "user_play";
                    Finish(events, reason);
                }
                if (item == null)
                {
                    item = new Item
                    {
                        MetaId = current.MetaId, VideoId = current.VideoId, StartPositionMs = Math.Max(0, current.PositionMs),
                        MaxPositionMs = Math.Max(0, current.PositionMs), LastPositionMs = Math.Max(0, current.PositionMs), DurationMs = current.DurationMs,
                    };
                    events.Add(Event(FeedbackKinds.PlayStarted, item));
                }
                Update(events, current);
                return events;
            }
            if (item == null) return events;
            var last = status.LastSession;
            if (last == null || last.MetaId != item.MetaId || last.VideoId != item.VideoId || last.EndedAt == null)
                return events;
            if (last.EndReason != null && ContinuingEnds.Contains(last.EndReason))
                return events;
            Update(events, last);
            Finish(events, last.EndReason);
            return events;
        }

        private void Update(List<FeedbackEvent> events, SessionStatus s)
        {
            if (s.DurationMs > 0) item.DurationMs = s.DurationMs;
            if (s.PositionMs > 0)
            {
                item.LastPositionMs = s.PositionMs;
                item.MaxPositionMs = Math.Max(item.MaxPositionMs, s.PositionMs);
            }
            if (item.DurationMs > 0)
            {
                var ratio = (double)item.MaxPositionMs / item.DurationMs;
                foreach (var milestone in Milestones)
                    if (ratio >= milestone && item.Reached.Add(milestone) && !item.Completed && milestone > (double)item.StartPositionMs / item.DurationMs)
                        events.Add(Event(FeedbackKinds.Progress, item));
            }
            if (s.MarkedWatched && !item.Completed)
            {
                item.Completed = true;
                events.Add(Event(FeedbackKinds.Completed, item));
            }
        }

        private void Finish(List<FeedbackEvent> events, string reason)
        {
            var ended = item;
            item = null;
            if (ended == null || ended.Completed) return;
            if (reason != null && FailureEnds.Contains(reason)) return;
            var watched = ended.MaxPositionMs - ended.StartPositionMs;
            var ratio = ended.DurationMs > 0 ? (double)ended.MaxPositionMs / ended.DurationMs : 0;
            if (watched >= AbandonMinWatchMs && ratio < 0.9)
                events.Add(Event(FeedbackKinds.Abandoned, ended));
        }

        private FeedbackEvent Event(string kind, Item source) => new FeedbackEvent
        {
            ImdbId = source.MetaId,
            VideoId = source.VideoId != source.MetaId ? source.VideoId : null,
            Kind = kind,
            PositionMs = kind == FeedbackKinds.PlayStarted ? source.StartPositionMs : source.LastPositionMs,
            DurationMs = source.DurationMs > 0 ? source.DurationMs : (long?)null,
            At = now(),
        };

        // ---------------- writing ----------------

        private async Task ExplicitAsync(string metaId, string kind, bool hide, CancellationToken token)
        {
            if (metaId == null || !Imdb.IsMatch(metaId))
                throw new ArgumentException("An IMDb id like tt0903747 is required.", nameof(metaId));
            await RecordAsync(new FeedbackEvent { ImdbId = metaId, Kind = kind }, token).ConfigureAwait(false);
            await hiddenGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var set = await LoadHiddenAsync(token).ConfigureAwait(false);
                var changed = hide ? set.Add(metaId) : set.Remove(metaId);
                if (changed && kv != null)
                    await kv.WriteAsync(HiddenKey, JsonConvert.SerializeObject(set.OrderBy(x => x, StringComparer.Ordinal)), token).ConfigureAwait(false);
            }
            finally { hiddenGate.Release(); }
        }

        private async Task RecordAsync(FeedbackEvent feedback, CancellationToken token)
        {
            // The store accepts IMDb ids only; other catalogs' ids (kitsu:, tmdb:) are skipped, not errors.
            if (feedback.ImdbId == null || !Imdb.IsMatch(feedback.ImdbId)) return;
            if (feedback.VideoId != null && feedback.VideoId.Length > 64) feedback.VideoId = null;
            if (feedback.At == default(DateTimeOffset)) feedback.At = now();
            try { await store.RecordFeedbackAsync(feedback, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { log?.Invoke("feedback " + feedback.Kind + ": " + error.Message); }
        }

        private async Task<HashSet<string>> LoadHiddenAsync(CancellationToken token)
        {
            if (hidden != null) return hidden;
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (kv != null)
            {
                try
                {
                    var text = await kv.ReadAsync(HiddenKey, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(text))
                        foreach (var id in JsonConvert.DeserializeObject<List<string>>(text) ?? new List<string>())
                            if (id != null) set.Add(id);
                }
                catch (JsonException) { }
            }
            hidden = set;
            return set;
        }
    }

    /// ILibrary decorator: every add or remove (remote UI or agent API) also records added_to_library / removed.
    /// Playback progress writes go through IProgressSink, not AddAsync, so they never count as an add.
    public sealed class FeedbackLibrary : ILibrary
    {
        private readonly ILibrary inner;
        private readonly FeedbackRecorder recorder;

        public FeedbackLibrary(ILibrary inner, FeedbackRecorder recorder)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        }

        public ILibrary Inner => inner;

        public Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token) => inner.GetAllAsync(token);
        public Task<LibraryEntry> GetAsync(string id, CancellationToken token) => inner.GetAsync(id, token);

        public async Task AddAsync(MetaDetail meta, CancellationToken token)
        {
            await inner.AddAsync(meta, token).ConfigureAwait(false);
            await recorder.LibraryChangedAsync(meta?.Summary?.Id, true, token).ConfigureAwait(false);
        }

        public async Task RemoveAsync(string id, CancellationToken token)
        {
            await inner.RemoveAsync(id, token).ConfigureAwait(false);
            await recorder.LibraryChangedAsync(id, false, token).ConfigureAwait(false);
        }

        public Task SetNotificationsAsync(string id, bool enabled, CancellationToken token) => inner.SetNotificationsAsync(id, enabled, token);
        public Task MarkWatchedAsync(MetaDetail meta, string videoId, bool watched, CancellationToken token) => inner.MarkWatchedAsync(meta, videoId, watched, token);
        public bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId) => inner.IsWatched(entry, meta, videoId);
        public VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta) => inner.NextVideo(entry, meta);
    }
}
