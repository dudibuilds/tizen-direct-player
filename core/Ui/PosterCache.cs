using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Ui
{
    /// Small on-disk LRU cache for poster/background/logo images. Files are named by a hash of the URL plus the
    /// extension of the actual format (EFL picks its image loader by extension, so ".jpg"/".png"/".webp" matter).
    ///
    /// The calling (UI) thread never touches the disk: an in-memory index, loaded once at start-up, answers lookups,
    /// and a hit only bumps the entry's recency in memory. A worker writes those recencies back as file times (so
    /// the LRU order survives a restart), evicts the least recently used files beyond the budget and deletes them.
    /// The index lock guards memory only and is never held across file IO.
    public sealed class PosterCache
    {
        /// The only extensions the cache ever stores or returns. WebP is left out for painters that cannot decode
        /// it (Tizen 5's EFL has no WebP loader); the browser preview keeps it.
        private readonly string[] Extensions;
        private readonly bool webp;
        private int webpRetries;
        private int webpRejected;
        private readonly string directory;
        private readonly HttpClient http;
        private readonly long maxBytes;
        private readonly int maxFiles;
        private readonly object gate = new object();
        private readonly Dictionary<string, Task<string>> inflight = new Dictionary<string, Task<string>>();
        /// Speculative downloads still allowed to wait for a speculative slot, by URL (guarded by `gate`).
        private readonly Dictionary<string, TaskCompletionSource<bool>> queuedSpeculative = new Dictionary<string, TaskCompletionSource<bool>>();
        /// Orders file writes against file deletes (workers only; never taken on the UI thread).
        private readonly object fileGate = new object();
        /// Four download slots in all; speculative requests (prefetch) hold at most two of them, so art the painter
        /// is waiting for always finds a slot without queueing behind titles focus may never reach.
        private readonly SemaphoreSlim downloads = new SemaphoreSlim(4);
        private readonly SemaphoreSlim speculativeSlots = new SemaphoreSlim(2);
        private int downloaded;
        private int failed;
        private int hits;
        private int evicted;
        private string lastError;

        /// One cached picture: its file, size (with derived copies), in-memory recency and derived copies (logos
        /// cut to card size, "<file>.fit…png"), which are deleted with it.
        private sealed class Item
        {
            public string Path;
            public long Bytes;
            public long Used;
            public List<string> Derived;
        }

        /// Index by URL hash (the file name without extension). Guarded by `gate`.
        private readonly Dictionary<string, Item> index = new Dictionary<string, Item>();
        /// URL -> hash memo, so a card that repaints never hashes its URL again. Guarded by `gate`.
        private readonly Dictionary<string, string> hashes = new Dictionary<string, string>();
        private long totalBytes;
        private long useCounter;
        /// Files whose recency changed since the last write-back. Guarded by `gate`.
        private readonly HashSet<string> touched = new HashSet<string>();
        private const int TouchFlushBatch = 32;
        private int pendingTouches;
        /// Index load (legacy migration plus one directory scan). Lookups made before it finishes wait on it off the
        /// calling thread instead of treating the cache as empty (which would download everything again).
        private readonly Task ready;
        private Task maintenance = Task.CompletedTask;
        private bool maintenanceQueued;
        private DateTime lastTempSweep = DateTime.MinValue;

        /// webp: false for painters without a WebP decoder. Requests then ask for JPEG/PNG, a WebP answer is retried
        /// once with a format hint, and a WebP that still arrives is a failure (never cached), so the card shows
        /// its text title instead of nothing. Cached .webp files are purged on startup.
        /// `migrateInBackground`: run the legacy-file scan and the index load on a worker (the TV's start-up, where
        /// they would hold the main loop).
        public PosterCache(string directory, HttpMessageHandler handler = null, long maxBytes = 48L * 1024 * 1024, int maxFiles = 400, bool webp = true, bool migrateInBackground = false)
        {
            this.directory = directory;
            this.webp = webp;
            Extensions = webp ? new[] { ".jpg", ".png", ".webp" } : new[] { ".jpg", ".png" };
            this.maxBytes = maxBytes;
            this.maxFiles = maxFiles;
            http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(20);
            if (migrateInBackground)
                ready = Task.Run(() => LoadIndex());
            else
            {
                LoadIndex();
                ready = Task.CompletedTask;
            }
        }

        /// Whether the start-up index load has finished (diagnostics, tests).
        public bool IndexReady => ready.IsCompleted;

        /// Files renamed to their real format on startup (diagnostics).
        public int Migrated { get; private set; }
        /// Legacy files of an unknown format deleted on startup (diagnostics).
        public int Purged { get; private set; }

        private void LoadIndex()
        {
            try
            {
                Directory.CreateDirectory(directory);
                MigrateLegacyFiles();
                ScanIndex();
            }
            catch (Exception)
            {
                // An unreadable folder leaves the index empty: pictures download again and are written as usual.
            }
        }

        /// Reads the folder once into the index; file times give the initial recency order.
        private void ScanIndex()
        {
            var files = new DirectoryInfo(directory).GetFiles();
            var derived = new List<FileInfo>();
            var items = new List<KeyValuePair<string, FileInfo>>();
            foreach (var file in files)
            {
                var name = file.Name;
                if (name.EndsWith(".tmp", StringComparison.Ordinal))
                    continue;
                if (name.IndexOf(".fit", StringComparison.Ordinal) > 0)
                {
                    derived.Add(file);
                    continue;
                }
                if (!Extensions.Contains(file.Extension.ToLowerInvariant()))
                    continue;
                items.Add(new KeyValuePair<string, FileInfo>(Path.GetFileNameWithoutExtension(name), file));
            }
            var orphans = new List<string>();
            lock (gate)
            {
                foreach (var entry in items.OrderBy(i => i.Value.LastWriteTimeUtc))
                {
                    if (index.ContainsKey(entry.Key))
                        continue;
                    var item = new Item { Path = entry.Value.FullName, Bytes = entry.Value.Length, Used = ++useCounter };
                    index[entry.Key] = item;
                    totalBytes += item.Bytes;
                }
                foreach (var file in derived)
                {
                    // "<hash>.png.fit320x120.png" belongs to "<hash>.png".
                    var name = file.Name;
                    if (index.TryGetValue(name.Substring(0, name.IndexOf('.')), out var owner) && name.StartsWith(Path.GetFileName(owner.Path) + ".", StringComparison.Ordinal))
                    {
                        (owner.Derived ?? (owner.Derived = new List<string>())).Add(file.FullName);
                        owner.Bytes += file.Length;
                        totalBytes += file.Length;
                    }
                    else
                        orphans.Add(file.FullName);   // its source is gone
                }
            }
            foreach (var orphan in orphans)
                TryDelete(orphan);
            ScheduleMaintenance();   // the budget may be smaller than what an earlier build left behind
        }

        /// Earlier builds cached everything as "<hash>.img", which EFL cannot decode. On startup every file with
        /// an extension outside the supported set is renamed to its real format (sniffed from its first bytes),
        /// or deleted when the format is unknown, so a stale file is never served again.
        private void MigrateLegacyFiles()
        {
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(directory).GetFiles();
            }
            catch (IOException)
            {
                return;
            }
            foreach (var file in files)
            {
                var ext = file.Extension.ToLowerInvariant();
                if (Extensions.Contains(ext) || ext == ".tmp")
                    continue;
                try
                {
                    var head = new byte[16];
                    int read;
                    using (var stream = file.OpenRead())
                        read = stream.Read(head, 0, head.Length);
                    Array.Resize(ref head, read);
                    var real = ExtensionFor(head);
                    if (real != null && !Extensions.Contains(real))
                        real = null;   // a format this painter cannot decode (WebP on the TV)
                    var target = real == null ? null : Path.Combine(directory, Path.GetFileNameWithoutExtension(file.Name) + real);
                    if (target != null && !File.Exists(target))
                    {
                        file.MoveTo(target);
                        Migrated++;
                    }
                    else
                    {
                        file.Delete();
                        Purged++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// Called with a message whenever a download fails (diagnostics).
        public Action<string> OnError { get; set; }

        /// Counters for diagnostics (dev.diagnostics / startup trace).
        public object Stats
        {
            get { lock (gate) return new { downloaded, failed, hits, evicted, files = index.Count, bytes = totalBytes, indexReady = ready.IsCompleted, lastError, directory, migrated = Migrated, purged = Purged, webp, webpRetries, webpRejected }; }
        }

        public int Failed { get { lock (gate) return failed; } }
        public int Downloaded { get { lock (gate) return downloaded; } }
        public string LastError { get { lock (gate) return lastError; } }
        /// Files and bytes the index holds (tests, diagnostics).
        public int FileCount { get { lock (gate) return index.Count; } }
        public long TotalBytes { get { lock (gate) return totalBytes; } }

        private static string Hash(string url)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        private bool TryKnownHash(string url, out string hash)
        {
            lock (gate)
                return hashes.TryGetValue(url, out hash);
        }

        /// The URL's hash, memoised (a repainting card asks for the same URL many times).
        private string HashOf(string url)
        {
            if (TryKnownHash(url, out var known))
                return known;
            var hash = Hash(url);
            lock (gate)
            {
                if (hashes.Count >= 4000)
                    hashes.Clear();
                hashes[url] = hash;
            }
            return hash;
        }

        /// The cached file for url, or null when not cached. Memory only (no disk access); before the start-up
        /// index load finishes it answers null.
        public string PathFor(string url)
        {
            if (string.IsNullOrEmpty(url))
                return null;
            var hash = HashOf(url);
            lock (gate)
                return index.TryGetValue(hash, out var item) ? item.Path : null;
        }

        /// Local file path for the image, downloading it when missing. Concurrent requests share one download.
        /// Never blocks on disk or on eviction: a hit completes at once from the in-memory index.
        /// `speculative`: prefetch of art not yet on screen; it never takes more than half the download slots.
        public Task<string> GetAsync(string url, CancellationToken token, bool speculative = false)
        {
            if (string.IsNullOrEmpty(url))
                return Task.FromResult<string>(null);
            if (!ready.IsCompleted)
                return ready.ContinueWith(_ => GetAsync(url, token, speculative), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            // A URL seen for the first time is hashed on a worker; from then on the memo answers.
            if (!TryKnownHash(url, out var hash))
                return Task.Run(() => { HashOf(url); return GetAsync(url, token, speculative); });
            string path;
            lock (gate)
            {
                if (index.TryGetValue(hash, out var item))
                {
                    item.Used = ++useCounter;
                    hits++;
                    path = item.Path;
                    touched.Add(path);
                    if (++pendingTouches < TouchFlushBatch)
                        return Task.FromResult(path);
                }
                else
                {
                    if (inflight.TryGetValue(url, out var running))
                    {
                        // Art the painter waits for joins a prefetch still queued behind the speculative slots: it
                        // is promoted, so it takes the next free slot like any visible request.
                        if (!speculative && queuedSpeculative.TryGetValue(url, out var queued))
                            queued.TrySetResult(true);
                        return running;
                    }
                    // Task.Run: the download (and everything before its first await) starts on a worker, never on
                    // the caller's thread or its synchronization context (the TV's main loop).
                    var basePath = Path.Combine(directory, hash);
                    var promote = speculative ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                    if (promote != null)
                        queuedSpeculative[url] = promote;
                    var task = Task.Run(() => DownloadAsync(url, basePath, hash, token, promote));
                    inflight[url] = task;
                    task.ContinueWith(_ => { lock (gate) { inflight.Remove(url); queuedSpeculative.Remove(url); } }, TaskScheduler.Default);
                    return task;
                }
            }
            ScheduleMaintenance();   // a batch of recencies to write back
            return Task.FromResult(path);
        }

        /// Forgets a cached file the painter could not decode or found missing, so the next request downloads it
        /// again instead of handing the same dead path over forever. The file is deleted on the worker.
        public void Invalidate(string url)
        {
            if (string.IsNullOrEmpty(url))
                return;
            var hash = HashOf(url);
            Item item;
            lock (gate)
            {
                if (!index.TryGetValue(hash, out item))
                    return;
                index.Remove(hash);
                totalBytes -= item.Bytes;
                touched.Remove(item.Path);
            }
            Task.Run(() => DeleteItem(item));
        }

        /// Records a copy derived from a cached file (a logo cut to card size), so its bytes count against the budget
        /// and it is deleted together with its source. Any thread; the size is read by the caller (off the UI thread).
        public void AttachDerived(string sourcePath, string derivedPath, long bytes)
        {
            if (sourcePath == null || derivedPath == null)
                return;
            var hash = Path.GetFileNameWithoutExtension(sourcePath);
            lock (gate)
            {
                if (!index.TryGetValue(hash, out var item) || item.Path != sourcePath)
                    return;
                if (item.Derived == null)
                    item.Derived = new List<string>();
                else if (item.Derived.Contains(derivedPath))
                    return;
                item.Derived.Add(derivedPath);
                item.Bytes += bytes;
                totalBytes += bytes;
            }
            ScheduleMaintenance();
        }

        /// File extension for the image bytes (magic numbers), or null for a format the TV cannot decode.
        public static string ExtensionFor(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return ".webp";
            return null;
        }

        /// `promote`: set for a speculative download; completing it (a visible request joined) lifts the
        /// speculative limit while the download still waits for it.
        private async Task<string> DownloadAsync(string url, string basePath, string hash, CancellationToken token, TaskCompletionSource<bool> promote)
        {
            var speculative = false;   // holds one of the speculative slots (released at the end)
            if (promote != null)
            {
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var wait = speculativeSlots.WaitAsync(stop.Token);
                    if (await Task.WhenAny(wait, promote.Task).ConfigureAwait(false) != wait)
                        stop.Cancel();
                    try
                    {
                        await wait.ConfigureAwait(false);
                        speculative = true;
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        // promoted before a speculative slot came free
                    }
                }
            }
            try
            {
                await downloads.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                if (speculative)
                    speculativeSlots.Release();
                throw;
            }
            try
            {
                // Without WebP: metahub posters and backgrounds get the format hint up front (one request instead
                // of a WebP download and a retry); every other host is retried only when it answers with WebP.
                var hinted = !webp && NeedsFormatHint(url);
                var bytes = await FetchAsync(hinted ? WithFormatHint(url) : url, token).ConfigureAwait(false);
                var extension = ExtensionFor(bytes);
                if (extension == ".webp" && !webp && !hinted)
                {
                    // Image CDNs (metahub) answer from a shared cache whatever the Accept header says. A format hint
                    // in the query makes a fresh cache key, so the request is negotiated again and returns JPEG.
                    lock (gate) webpRetries++;
                    bytes = await FetchAsync(WithFormatHint(url), token).ConfigureAwait(false);
                    extension = ExtensionFor(bytes);
                }
                if (extension == ".webp" && !webp)
                {
                    lock (gate) webpRejected++;
                    throw new InvalidDataException("WebP is not supported on this device (" + bytes.Length + " bytes)");
                }
                if (extension == null)
                    throw new InvalidDataException("Unsupported image format (" + bytes.Length + " bytes)");
                var path = basePath + extension;
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, bytes);
                // The fresh file always wins (an older copy may be on its way out), and it is in the index before
                // an eviction or invalidation can look at it again (both check the index under the same lock).
                lock (fileGate)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(temp, path);
                    lock (gate)
                    {
                        downloaded++;
                        if (index.TryGetValue(hash, out var existing))
                            totalBytes -= existing.Bytes;
                        index[hash] = new Item { Path = path, Bytes = bytes.Length, Used = ++useCounter, Derived = existing?.Derived };
                        totalBytes += bytes.Length;
                    }
                }
                ScheduleMaintenance();
                return path;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                var message = error.GetType().Name + ": " + (error.GetBaseException().Message ?? "") + " (" + url + ")";
                lock (gate)
                {
                    failed++;
                    lastError = message;
                }
                OnError?.Invoke(message);
                throw;
            }
            finally
            {
                downloads.Release();
                if (speculative)
                    speculativeSlots.Release();
            }
        }

        private async Task<byte[]> FetchAsync(string url, CancellationToken token)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!webp)
                    request.Headers.TryAddWithoutValidation("Accept", "image/jpeg,image/png;q=0.9,*/*;q=0.1");
                using (var response = await http.SendAsync(request, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException("HTTP " + (int)response.StatusCode + " for " + url);
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        /// Metahub (Cinemeta's image host) serves posters and backgrounds as WebP from a shared edge cache unless
        /// the URL carries a format hint. Logos come back as PNG and are left alone.
        public static bool NeedsFormatHint(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            var host = uri.Host;
            if (!host.Equals("metahub.space", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".metahub.space", StringComparison.OrdinalIgnoreCase))
                return false;
            if (uri.Query.IndexOf("format=", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return uri.AbsolutePath.StartsWith("/poster/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/background/", StringComparison.OrdinalIgnoreCase);
        }

        /// The same URL with "format=jpg" added to its query.
        public static string WithFormatHint(string url)
        {
            var fragment = url.IndexOf('#');
            var head = fragment < 0 ? url : url.Substring(0, fragment);
            return head + (head.IndexOf('?') < 0 ? "?" : "&") + "format=jpg" + (fragment < 0 ? "" : url.Substring(fragment));
        }

        // ---- maintenance (worker only) ----

        /// Queues one maintenance pass on a worker (write back recencies, evict beyond the budget, sweep stale
        /// temp files). Passes never overlap; a request made during a pass runs another one after it.
        private void ScheduleMaintenance()
        {
            lock (gate)
            {
                if (maintenanceQueued)
                    return;
                maintenanceQueued = true;
                maintenance = maintenance.ContinueWith(_ => RunMaintenance(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// Completes when every maintenance pass queued so far has run (tests; shutdown).
        public Task FlushAsync()
        {
            lock (gate)
                return maintenance;
        }

        /// Test seam: deletes one file during eviction. Runs on the maintenance worker.
        internal Action<string> DeleteFile = path => File.Delete(path);

        private void RunMaintenance()
        {
            string[] touches;
            List<Item> victims = null;
            lock (gate)
            {
                maintenanceQueued = false;
                touches = touched.Count == 0 ? null : touched.ToArray();
                touched.Clear();
                pendingTouches = 0;
                if (index.Count > maxFiles || totalBytes > maxBytes)
                {
                    // In memory only (a few hundred entries): the newest stay while they fit both budgets.
                    var byRecency = index.ToList();
                    byRecency.Sort((a, b) => b.Value.Used.CompareTo(a.Value.Used));
                    long kept = 0;
                    for (var i = 0; i < byRecency.Count; i++)
                    {
                        kept += byRecency[i].Value.Bytes;
                        if (i < maxFiles && kept <= maxBytes)
                            continue;
                        (victims ?? (victims = new List<Item>())).Add(byRecency[i].Value);
                        index.Remove(byRecency[i].Key);
                        totalBytes -= byRecency[i].Value.Bytes;
                        evicted++;
                    }
                }
            }
            if (touches != null)
            {
                var now = DateTime.UtcNow;
                foreach (var path in touches)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.SetLastWriteTimeUtc(path, now);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            if (victims != null)
                foreach (var victim in victims)
                    DeleteItem(victim);
            SweepTemp();
        }

        /// Deletes an evicted or invalidated file and its derived copies, unless a new download has put the same
        /// file back in the index meanwhile. Workers only: `fileGate` is never taken by a lookup.
        private void DeleteItem(Item item)
        {
            lock (fileGate)
            {
                lock (gate)
                    if (index.TryGetValue(Path.GetFileNameWithoutExtension(item.Path), out var current) && current.Path == item.Path)
                        return;
                TryDelete(item.Path);
                if (item.Derived != null)
                    foreach (var copy in item.Derived)
                        TryDelete(copy);
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                DeleteFile(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// Temp files left by a crash mid-write, at most every five minutes.
        private void SweepTemp()
        {
            var now = DateTime.UtcNow;
            if (now - lastTempSweep < TimeSpan.FromMinutes(5))
                return;
            lastTempSweep = now;
            try
            {
                foreach (var stale in new DirectoryInfo(directory).GetFiles("*.tmp").Where(f => f.LastWriteTimeUtc < now.AddMinutes(-5)))
                {
                    try
                    {
                        stale.Delete();
                    }
                    catch (IOException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
