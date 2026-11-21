// Recommendation feed pushed from the Mac (scheduled deterministic code) to the TV. Scope: recommendations.
// The TV persists the last good snapshot in IKeyValueStore so the For You rows render with the Mac asleep.
// The TV records viewing and feedback events through IRecommendationsStore; the Mac pulls new ones with an ack cursor.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Api
{
    public sealed class RecommendationItem
    {
        public string Id { get; set; }        // IMDb id
        public string Type { get; set; }      // movie | series
        public string Name { get; set; }
        public string Poster { get; set; }    // optional http(s) URL
        public string Reason { get; set; }    // short text shown on the TV, may be empty
        public double? Score { get; set; }
    }

    public sealed class RecommendationRow
    {
        public string Id { get; set; }        // slug, e.g. for-you
        public string Title { get; set; }     // "For You", "Because you watched Dark"
        public IReadOnlyList<RecommendationItem> Items { get; set; } = Array.Empty<RecommendationItem>();
    }

    public sealed class RecommendationSnapshot
    {
        public long Version { get; set; }               // assigned by the TV, +1 per accepted push; 0 = none yet
        public string GeneratedAt { get; set; }         // as sent by the publisher (ISO text, not parsed)
        public string Source { get; set; }              // free text, e.g. "mac/recommendations.py"
        public DateTimeOffset? PublishedAt { get; set; } // when the TV accepted it
        public IReadOnlyList<RecommendationRow> Rows { get; set; } = Array.Empty<RecommendationRow>();
    }

    /// Viewing and feedback event kinds recorded on the TV for the Mac's deterministic learner.
    public static class FeedbackKinds
    {
        public const string PlayStarted = "play_started";
        public const string Progress = "progress";
        public const string Completed = "completed";
        public const string Abandoned = "abandoned";
        public const string ThumbsUp = "thumbs_up";
        public const string ThumbsDown = "thumbs_down";
        public const string NotInterested = "not_interested";
        public const string AddedToLibrary = "added_to_library";
        public const string Removed = "removed";      // removed from the library
        public static readonly string[] All = { PlayStarted, Progress, Completed, Abandoned, ThumbsUp, ThumbsDown, NotInterested, AddedToLibrary, Removed };
    }

    public sealed class FeedbackEvent
    {
        public long Sequence { get; set; }    // assigned by the store; strictly increasing, never reused
        public string ImdbId { get; set; }    // tt id of the movie or series
        public string VideoId { get; set; }   // episode id (tt..:S:E) when relevant
        public string Kind { get; set; }      // FeedbackKinds
        public long? PositionMs { get; set; }
        public long? DurationMs { get; set; }
        public DateTimeOffset At { get; set; }
    }

    public sealed class FeedbackPage
    {
        public IReadOnlyList<FeedbackEvent> Events { get; set; } = Array.Empty<FeedbackEvent>();
        public long LastSequence { get; set; }       // last sequence in Events, or the cursor used when empty
        public long AckedSequence { get; set; }      // cursor stored on the TV by recommendations.feedback.ack
        public long LatestSequence { get; set; }     // newest sequence the TV has assigned
        public bool HasMore { get; set; }
        /// True when events after the cursor were dropped by the retention cap before anyone read them.
        public bool Gap { get; set; }
    }

    public interface IRecommendationsStore
    {
        /// The last accepted snapshot, or an empty one with Version 0.
        Task<RecommendationSnapshot> GetAsync(CancellationToken token);
        /// Stores an already validated snapshot and returns it with Version and PublishedAt set.
        /// expectedVersion (optional) must equal the current version or the call fails with version_conflict.
        Task<RecommendationSnapshot> ReplaceAsync(RecommendationSnapshot snapshot, long? expectedVersion, CancellationToken token);
        /// Called by the TV (player, UI). Validates, assigns Sequence (and At when unset), applies the retention cap.
        Task<FeedbackEvent> RecordFeedbackAsync(FeedbackEvent feedback, CancellationToken token);
        /// Events after afterSequence, or after the stored ack cursor when afterSequence is null.
        Task<FeedbackPage> ListFeedbackAsync(long? afterSequence, int limit, CancellationToken token);
        /// Moves the ack cursor forward (never back) and drops acknowledged events. Returns the stored cursor.
        Task<long> AckFeedbackAsync(long sequence, CancellationToken token);
    }

    public static class RecommendationLimits
    {
        public const int MaxRows = 20;
        public const int MaxItemsPerRow = 50;
        public const int MaxItems = 300;
        public const int MaxTitle = 80;
        public const int MaxName = 200;
        public const int MaxReason = 140;
        public const int MaxUrl = 500;
        public const int MaxFeedbackKept = 2000;     // unacknowledged events kept on the TV; oldest dropped first
    }

    /// Strict validation of a recommendations.set payload. Throws ApiException (invalid_argument and friends).
    public static class RecommendationValidator
    {
        internal static readonly Regex Imdb = new Regex("^tt\\d{5,10}$", RegexOptions.CultureInvariant);
        private static readonly Regex Slug = new Regex("^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant);

        public static IReadOnlyList<RecommendationRow> ParseRows(JArray rows)
        {
            if (rows == null) throw ApiException.Missing("rows");
            if (rows.Count == 0) throw ApiException.Invalid("rows must hold at least one row");
            if (rows.Count > RecommendationLimits.MaxRows) throw ApiException.Invalid("rows may hold at most " + RecommendationLimits.MaxRows + " rows");
            var result = new List<RecommendationRow>();
            var rowIds = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;
            for (var r = 0; r < rows.Count; r++)
            {
                var where = "rows[" + r + "]";
                if (!(rows[r] is JObject rowObject)) throw ApiException.Invalid(where + " must be an object");
                var row = new ApiArgs(rowObject);
                Prefixed(where, () => row.AssertAllowed(new[] { "id", "title", "items" }));
                var id = Prefixed(where, () => row.RequireString("id"));
                if (!Slug.IsMatch(id)) throw ApiException.Invalid(where + ".id must be a lowercase slug (a-z, 0-9, - or _, up to 40 characters)");
                if (!rowIds.Add(id)) throw ApiException.Invalid(where + ".id duplicates another row");
                var title = Prefixed(where, () => row.RequireString("title"));
                if (title.Length > RecommendationLimits.MaxTitle) throw ApiException.Invalid(where + ".title may be at most " + RecommendationLimits.MaxTitle + " characters");
                var items = Prefixed(where, () => row.OptionalArray("items")) ?? throw ApiException.Missing(where + ".items");
                if (items.Count == 0) throw ApiException.Invalid(where + ".items must hold at least one item");
                if (items.Count > RecommendationLimits.MaxItemsPerRow) throw ApiException.Invalid(where + ".items may hold at most " + RecommendationLimits.MaxItemsPerRow + " items");
                total += items.Count;
                if (total > RecommendationLimits.MaxItems) throw ApiException.Invalid("rows may hold at most " + RecommendationLimits.MaxItems + " items in total");
                var parsed = new List<RecommendationItem>();
                var itemIds = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < items.Count; i++)
                {
                    var at = where + ".items[" + i + "]";
                    if (!(items[i] is JObject itemObject)) throw ApiException.Invalid(at + " must be an object");
                    var item = new ApiArgs(itemObject);
                    var value = Prefixed(at, () => new RecommendationItem
                    {
                        Id = CheckId(item),
                        Type = item.Choice("type", new[] { "movie", "series" }, null),
                        Name = item.RequireString("name"),
                        Poster = item.OptionalString("poster"),
                        Reason = item.OptionalString("reason") ?? "",
                        Score = item.OptionalNumber("score"),
                    });
                    if (value.Name.Length > RecommendationLimits.MaxName) throw ApiException.Invalid(at + ".name may be at most " + RecommendationLimits.MaxName + " characters");
                    if (value.Reason.Length > RecommendationLimits.MaxReason) throw ApiException.Invalid(at + ".reason may be at most " + RecommendationLimits.MaxReason + " characters");
                    if (value.Poster != null && (value.Poster.Length > RecommendationLimits.MaxUrl
                        || !Uri.TryCreate(value.Poster, UriKind.Absolute, out var poster) || (poster.Scheme != "https" && poster.Scheme != "http")))
                        throw ApiException.Invalid(at + ".poster must be an http(s) URL up to " + RecommendationLimits.MaxUrl + " characters");
                    if (!itemIds.Add(value.Id)) throw ApiException.Invalid(at + ".id duplicates another item in the row");
                    parsed.Add(value);
                }
                result.Add(new RecommendationRow { Id = id, Title = title, Items = parsed });
            }
            return result;
        }

        private static string CheckId(ApiArgs item)
        {
            item.AssertAllowed(new[] { "id", "type", "name", "poster", "reason", "score" });
            var id = item.RequireString("id");
            if (!Imdb.IsMatch(id)) throw ApiException.Invalid("id must be an IMDb id like tt0903747");
            return id;
        }

        private static T Prefixed<T>(string where, Func<T> read)
        {
            try { return read(); }
            catch (ApiException error) { throw new ApiException(error.Code, where + ": " + error.Message, error.StatusCode); }
        }

        private static void Prefixed(string where, Action read) => Prefixed<object>(where, () => { read(); return null; });
    }

    /// IRecommendationsStore over IKeyValueStore. The snapshot write is the store's atomic replace; the previous
    /// good snapshot is kept under a second key and served if the current one cannot be read.
    public sealed class KeyValueRecommendationsStore : IRecommendationsStore
    {
        public const string SnapshotKey = "recommendations-snapshot";
        public const string PreviousKey = "recommendations-snapshot-previous";
        public const string FeedbackKey = "recommendations-feedback";
        private readonly IKeyValueStore store;
        private readonly Func<DateTimeOffset> clock;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        public KeyValueRecommendationsStore(IKeyValueStore store, Func<DateTimeOffset> clock = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public async Task<RecommendationSnapshot> GetAsync(CancellationToken token)
        {
            return await ReadSnapshotAsync(SnapshotKey, token).ConfigureAwait(false)
                ?? await ReadSnapshotAsync(PreviousKey, token).ConfigureAwait(false)
                ?? new RecommendationSnapshot();
        }

        public async Task<RecommendationSnapshot> ReplaceAsync(RecommendationSnapshot snapshot, long? expectedVersion, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = await GetAsync(token).ConfigureAwait(false);
                if (expectedVersion != null && expectedVersion.Value != current.Version)
                    throw ApiException.Conflict("version_conflict", "The TV holds version " + current.Version + ", not " + expectedVersion.Value);
                var stored = new RecommendationSnapshot
                {
                    Version = current.Version + 1,
                    GeneratedAt = snapshot.GeneratedAt,
                    Source = snapshot.Source,
                    PublishedAt = clock(),
                    Rows = snapshot.Rows ?? Array.Empty<RecommendationRow>(),
                };
                if (current.Version > 0)
                    await store.WriteAsync(PreviousKey, ApiJson.Serialize(current), token).ConfigureAwait(false);
                await store.WriteAsync(SnapshotKey, ApiJson.Serialize(stored), token).ConfigureAwait(false);
                return stored;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<FeedbackEvent> RecordFeedbackAsync(FeedbackEvent feedback, CancellationToken token)
        {
            if (feedback == null) throw new ArgumentNullException(nameof(feedback));
            if (feedback.ImdbId == null || !RecommendationValidator.Imdb.IsMatch(feedback.ImdbId))
                throw ApiException.Invalid("imdbId must be an IMDb id like tt0903747");
            if (Array.IndexOf(FeedbackKinds.All, feedback.Kind) < 0)
                throw ApiException.Invalid("kind must be one of: " + string.Join(", ", FeedbackKinds.All));
            if (feedback.PositionMs < 0 || feedback.DurationMs < 0) throw ApiException.Invalid("positionMs and durationMs must not be negative");
            if (feedback.VideoId != null && feedback.VideoId.Length > 64) throw ApiException.Invalid("videoId may be at most 64 characters");
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var log = await ReadFeedbackAsync(token).ConfigureAwait(false);
                var entry = new FeedbackEvent
                {
                    Sequence = ++log.LatestSequence,
                    ImdbId = feedback.ImdbId,
                    VideoId = feedback.VideoId,
                    Kind = feedback.Kind,
                    PositionMs = feedback.PositionMs,
                    DurationMs = feedback.DurationMs,
                    At = feedback.At == default(DateTimeOffset) ? clock() : feedback.At,
                };
                log.Events.Add(entry);
                if (log.Events.Count > RecommendationLimits.MaxFeedbackKept)
                    log.Events.RemoveRange(0, log.Events.Count - RecommendationLimits.MaxFeedbackKept);
                await WriteFeedbackAsync(log, token).ConfigureAwait(false);
                return entry;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<FeedbackPage> ListFeedbackAsync(long? afterSequence, int limit, CancellationToken token)
        {
            var log = await ReadFeedbackAsync(token).ConfigureAwait(false);
            var after = afterSequence ?? log.AckedSequence;
            var pending = log.Events.Where(e => e.Sequence > after).ToList();
            var page = pending.Take(Math.Max(1, limit)).ToList();
            var oldest = log.Events.Count == 0 ? log.LatestSequence + 1 : log.Events[0].Sequence;
            return new FeedbackPage
            {
                Events = page,
                LastSequence = page.Count == 0 ? after : page[page.Count - 1].Sequence,
                AckedSequence = log.AckedSequence,
                LatestSequence = log.LatestSequence,
                HasMore = pending.Count > page.Count,
                Gap = after >= log.AckedSequence && after < log.LatestSequence && oldest > after + 1,
            };
        }

        public async Task<long> AckFeedbackAsync(long sequence, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var log = await ReadFeedbackAsync(token).ConfigureAwait(false);
                if (sequence > log.LatestSequence)
                    throw ApiException.Invalid("sequence " + sequence + " is newer than the latest event " + log.LatestSequence);
                if (sequence > log.AckedSequence)
                {
                    log.AckedSequence = sequence;
                    log.Events.RemoveAll(e => e.Sequence <= sequence);
                    await WriteFeedbackAsync(log, token).ConfigureAwait(false);
                }
                return log.AckedSequence;
            }
            finally
            {
                gate.Release();
            }
        }

        private sealed class FeedbackLog
        {
            public long LatestSequence { get; set; }
            public long AckedSequence { get; set; }
            public List<FeedbackEvent> Events { get; set; } = new List<FeedbackEvent>();
        }

        private async Task<RecommendationSnapshot> ReadSnapshotAsync(string key, CancellationToken token)
        {
            try
            {
                var text = await store.ReadAsync(key, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(text)) return null;
                var snapshot = ApiJson.Parse(text).ToObject<RecommendationSnapshot>(ApiJson.Serializer);
                return snapshot != null && snapshot.Version > 0 && snapshot.Rows != null ? snapshot : null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        private async Task<FeedbackLog> ReadFeedbackAsync(CancellationToken token)
        {
            try
            {
                var text = await store.ReadAsync(FeedbackKey, token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(text)) return new FeedbackLog();
                var log = ApiJson.Parse(text).ToObject<FeedbackLog>(ApiJson.Serializer) ?? new FeedbackLog();
                if (log.Events == null) log.Events = new List<FeedbackEvent>();
                return log;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return new FeedbackLog(); }
        }

        private Task WriteFeedbackAsync(FeedbackLog log, CancellationToken token) =>
            store.WriteAsync(FeedbackKey, ApiJson.Serialize(log), token);
    }
}
