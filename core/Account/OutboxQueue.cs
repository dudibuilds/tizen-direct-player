using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    /// An ordered, persisted queue of writes (parity E5 enqueueHistory, plus an offline outbox).
    /// Every operation is saved to the IKeyValueStore before it is sent, so writes made while the
    /// network is down survive an app restart and are replayed in order by DrainAsync.
    ///
    /// Only the head is ever applied, one at a time. A transient failure (network, timeout, 429/5xx,
    /// not signed in) stops the drain and keeps the operation. A permanent failure is retried on later
    /// drains and the operation is dropped after MaxAttempts. Errors never throw out of Enqueue/Drain;
    /// they are exposed through LastError.
    public sealed class OutboxQueue
    {
        public const int DefaultMaxEntries = 500;
        public const int MaxAttempts = 5;

        readonly IKeyValueStore store;
        readonly string key;
        readonly Func<JObject, CancellationToken, Task> apply;
        readonly Func<JObject, JObject, bool> canReplace;
        readonly int maxEntries;
        readonly object sync = new object();
        readonly SemaphoreSlim persistGate = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim drainGate = new SemaphoreSlim(1, 1);
        readonly List<JObject> entries = new List<JObject>();
        JObject inFlight;
        bool loaded;

        /// apply sends one operation. canReplace(previousTail, next) lets a newer operation replace the
        /// queued tail (e.g. consecutive progress saves for the same video); it never replaces the head
        /// that is being sent.
        public OutboxQueue(IKeyValueStore store, string key, Func<JObject, CancellationToken, Task> apply,
            Func<JObject, JObject, bool> canReplace = null, int maxEntries = DefaultMaxEntries)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.key = key;
            this.apply = apply ?? throw new ArgumentNullException(nameof(apply));
            this.canReplace = canReplace;
            this.maxEntries = maxEntries;
        }

        public string LastError { get; private set; }
        public DateTimeOffset? LastErrorAt { get; private set; }

        public int Count { get { lock (sync) return entries.Count; } }

        public IReadOnlyList<JObject> Snapshot()
        {
            lock (sync) return entries.Select(e => (JObject)e.DeepClone()).ToList();
        }

        public async Task LoadAsync(CancellationToken token)
        {
            var saved = await StoreJson.ReadAsync(store, key, token).ConfigureAwait(false);
            lock (sync)
            {
                if (loaded) return;
                entries.Clear();
                if (saved is JArray array) entries.AddRange(array.OfType<JObject>());
                loaded = true;
            }
        }

        /// Drops every queued operation (memory and store), e.g. on sign-out.
        public async Task ClearAsync(CancellationToken token)
        {
            ClearInMemory();
            await PersistAsync(token).ConfigureAwait(false);
        }

        /// Drops every queued operation in memory only (the caller already cleared the store).
        public void ClearInMemory()
        {
            lock (sync)
            {
                entries.Clear();
                loaded = true;
            }
        }

        /// Persists the operation, then drains. Returns true when the queue is empty afterwards.
        public async Task<bool> EnqueueAsync(JObject operation, CancellationToken token)
        {
            if (!loaded) await LoadAsync(token).ConfigureAwait(false);
            lock (sync)
            {
                var tail = entries.Count > 0 ? entries[entries.Count - 1] : null;
                if (tail != null && !ReferenceEquals(tail, inFlight) && canReplace != null && canReplace(tail, operation))
                    entries[entries.Count - 1] = operation;
                else
                    entries.Add(operation);
                while (entries.Count > maxEntries && !ReferenceEquals(entries[0], inFlight)) entries.RemoveAt(0);
            }
            await PersistAsync(token).ConfigureAwait(false);
            return await DrainAsync(token).ConfigureAwait(false);
        }

        /// Sends queued operations in order until empty or a failure. Returns true when empty.
        public async Task<bool> DrainAsync(CancellationToken token)
        {
            if (!loaded) await LoadAsync(token).ConfigureAwait(false);
            await drainGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    JObject head;
                    lock (sync)
                    {
                        if (entries.Count == 0) { inFlight = null; return true; }
                        head = entries[0];
                        inFlight = head;
                    }
                    try
                    {
                        await apply(head, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        lock (sync) inFlight = null;
                        throw;
                    }
                    catch (Exception error)
                    {
                        RecordError(error);
                        if (IsTransient(error))
                        {
                            lock (sync) inFlight = null;
                            return false;
                        }
                        int attempts = (int)(head["attempts"] ?? 0) + 1;
                        bool drop = attempts >= MaxAttempts;
                        lock (sync)
                        {
                            inFlight = null;
                            if (drop) entries.Remove(head);
                            else head["attempts"] = attempts;
                        }
                        await PersistAsync(token).ConfigureAwait(false);
                        if (!drop) return false;
                        continue;
                    }
                    lock (sync)
                    {
                        entries.Remove(head);
                        inFlight = null;
                    }
                    await PersistAsync(token).ConfigureAwait(false);
                    LastError = null;
                }
            }
            finally
            {
                drainGate.Release();
            }
        }

        void RecordError(Exception error)
        {
            LastError = error.Message;
            LastErrorAt = DateTimeOffset.UtcNow;
        }

        async Task PersistAsync(CancellationToken token)
        {
            await persistGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                JArray copy;
                lock (sync) copy = new JArray(entries.Select(e => e.DeepClone()));
                await StoreJson.WriteAsync(store, key, copy, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                persistGate.Release();
            }
        }

        public static bool IsTransient(Exception error)
        {
            switch (error)
            {
                case StremioApiException api: return api.IsTransient;
                case Trakt.TraktApiException trakt: return trakt.IsTransient;
                case StremioNotSignedInException _: return true;
                case HttpRequestException _: return true;
                case WebException _: return true;
                case IOException _: return true;
                case TimeoutException _: return true;
                case OperationCanceledException _: return true;  // timeouts inside the apply delegate
                case AggregateException aggregate: return aggregate.InnerExceptions.Any(IsTransient);
                default: return false;
            }
        }
    }
}
