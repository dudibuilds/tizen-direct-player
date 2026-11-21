using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Addons
{
    /// JSON GET with the B5 retry rules: 3 tries, per-try timeout, retry on 429/5xx/timeout/network error,
    /// waiting 250 ms x attempt in between.
    public sealed class AddonHttp : IDisposable
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
        private const long MaxDocumentBytes = 8 * 1024 * 1024;
        private readonly HttpClient client;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;

        public AddonHttp(HttpMessageHandler handler = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            client = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = Timeout.InfiniteTimeSpan;
            this.delay = delay ?? ((wait, token) => Task.Delay(wait, token));
        }

        public int MaxAttempts { get; set; } = 3;

        public Task<JObject> GetJsonAsync(string url, CancellationToken token) => GetJsonAsync(url, DefaultTimeout, token);

        private readonly Dictionary<string, Task<JObject>> shared = new Dictionary<string, Task<JObject>>(StringComparer.Ordinal);

        /// GetJsonAsync where callers asking for the same URL at the same time share one request and one parsed
        /// document (the title page asks Cinemeta for the same meta document twice: details and extras). Callers must
        /// only read the result. The request outlives a caller's cancel; the caller just stops waiting.
        public Task<JObject> GetJsonSharedAsync(string url, CancellationToken token)
        {
            Task<JObject> task;
            lock (shared)
            {
                if (!shared.TryGetValue(url, out task))
                {
                    task = GetJsonAsync(url, CancellationToken.None);
                    shared[url] = task;
                    task.ContinueWith(_ => { lock (shared) shared.Remove(url); },
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            return TaskUtil.WithCancellation(task, token);
        }

        public async Task<JObject> GetJsonAsync(string url, TimeSpan timeout, CancellationToken token)
        {
            Exception lastError = null;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var last = attempt == MaxAttempts - 1;
                using (var perTry = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    perTry.CancelAfter(timeout);
                    try
                    {
                        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, perTry.Token).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            if (response.IsSuccessStatusCode)
                                return await ReadJsonAsync(response, url, perTry.Token).ConfigureAwait(false);
                            var error = new AddonRequestException("GET " + url + " failed: HTTP " + status, status);
                            if (!IsRetryableStatus(status) || last) throw error;
                            lastError = error;
                        }
                    }
                    catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                    {
                        lastError = new AddonRequestException("GET " + url + " timed out.", null, ex);
                        if (last) throw lastError;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                    {
                        lastError = new AddonRequestException("GET " + url + " failed: " + ex.Message, null, ex);
                        if (last) throw lastError;
                    }
                }
                await delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            throw lastError ?? new AddonRequestException("GET " + url + " failed.");
        }

        public static bool IsRetryableStatus(int status) => status == 429 || status >= 500;

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage response, string url, CancellationToken token)
        {
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                throw new AddonRequestException("GET " + url + ": response too large.");
            using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[16384];
                int count;
                while ((count = await body.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > MaxDocumentBytes)
                        throw new AddonRequestException("GET " + url + ": response too large.");
                    buffer.Write(bytes, 0, count);
                }
                buffer.Position = 0;
                try
                {
                    using (var text = new StreamReader(buffer))
                    using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
                    {
                        var token2 = JToken.ReadFrom(reader);
                        if (token2 is JObject obj) return obj;
                        throw new AddonRequestException("GET " + url + ": expected a JSON object.");
                    }
                }
                catch (JsonException ex)
                {
                    throw new AddonRequestException("GET " + url + ": invalid JSON.", null, ex);
                }
            }
        }

        public void Dispose() => client.Dispose();
    }

    /// B6: one in-flight load shared per key, TTL decided per value, failures not cached, bounded size.
    public sealed class AsyncCache<T>
    {
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly int limit;
        private readonly Func<DateTimeOffset> clock;

        public AsyncCache(int limit = 256, Func<DateTimeOffset> clock = null)
        {
            this.limit = limit;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        public int Count { get { lock (entries) return entries.Count; } }

        /// Shared loads do not observe a caller's token; callers stop waiting via their own token.
        public Task<T> GetOrLoadAsync(string key, Func<Task<T>> loader, Func<T, TimeSpan> ttl, CancellationToken token = default(CancellationToken))
        {
            Entry entry;
            var start = false;
            lock (entries)
            {
                var now = clock();
                if (!entries.TryGetValue(key, out entry) || (!entry.Pending && entry.ExpiresAt <= now))
                {
                    entry = new Entry();
                    entries[key] = entry;
                    start = true;
                    Prune(now);
                }
            }
            if (start) _ = CompleteAsync(key, entry, loader, ttl);
            return TaskUtil.WithCancellation(entry.Source.Task, token);
        }

        public void Clear() { lock (entries) entries.Clear(); }

        private async Task CompleteAsync(string key, Entry entry, Func<Task<T>> loader, Func<T, TimeSpan> ttl)
        {
            try
            {
                var value = await loader().ConfigureAwait(false);
                lock (entries)
                {
                    entry.Pending = false;
                    entry.ExpiresAt = clock() + ttl(value);
                }
                entry.Source.TrySetResult(value);
            }
            catch (Exception ex)
            {
                lock (entries)
                    if (entries.TryGetValue(key, out var current) && current == entry)
                        entries.Remove(key);
                if (ex is OperationCanceledException) entry.Source.TrySetCanceled();
                else entry.Source.TrySetException(ex);
            }
        }

        private void Prune(DateTimeOffset now)
        {
            var stale = new List<string>();
            foreach (var pair in entries)
                if (!pair.Value.Pending && pair.Value.ExpiresAt <= now) stale.Add(pair.Key);
            foreach (var key in stale) entries.Remove(key);
            while (entries.Count > limit)
            {
                string oldest = null;
                var oldestAt = DateTimeOffset.MaxValue;
                foreach (var pair in entries)
                    if (!pair.Value.Pending && pair.Value.ExpiresAt < oldestAt) { oldest = pair.Key; oldestAt = pair.Value.ExpiresAt; }
                if (oldest == null) break;
                entries.Remove(oldest);
            }
        }

        private sealed class Entry
        {
            public readonly TaskCompletionSource<T> Source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Pending = true;
            public DateTimeOffset ExpiresAt = DateTimeOffset.MaxValue;
        }
    }

    internal static class TaskUtil
    {
        public static async Task WithCancellation(Task task, CancellationToken token)
        {
            if (!token.CanBeCanceled || task.IsCompleted) { await task.ConfigureAwait(false); return; }
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled(token)))
            {
                var done = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                await done.ConfigureAwait(false);
            }
        }

        public static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken token)
        {
            if (!token.CanBeCanceled || task.IsCompleted) return await task.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled(token)))
            {
                var done = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                return await done.ConfigureAwait(false);
            }
        }
    }
}
