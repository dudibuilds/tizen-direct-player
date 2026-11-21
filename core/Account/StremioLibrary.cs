using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    public sealed class NewEpisode
    {
        public string SeriesId { get; set; }
        public string SeriesName { get; set; }
        public VideoInfo Video { get; set; }
    }

    /// Stremio library on api.strem.io (C1-C9, D1-D5).
    ///
    /// Every write (library edits and playback progress) goes through one ordered, persisted outbox
    /// (E5). Reads come from the server when reachable and fall back to the last cached copy; queued
    /// writes that have not reached the server yet are overlaid on what is returned, so the TV shows
    /// its own changes while offline. Raw libraryItem JSON is kept, so fields Stremio adds survive.
    public sealed class StremioLibrary : ILibrary
    {
        public const int NewEpisodeConcurrency = 4;
        public const int DefaultNewEpisodeLimit = 20;

        readonly StremioApiClient api;
        readonly IStremioAuthKeyProvider auth;
        readonly IKeyValueStore store;
        readonly ICatalog catalog;
        readonly Func<DateTimeOffset> now;
        readonly OutboxQueue outbox;
        readonly object cacheSync = new object();
        readonly SemaphoreSlim cacheWriteGate = new SemaphoreSlim(1, 1);
        Dictionary<string, JObject> cache = new Dictionary<string, JObject>(StringComparer.Ordinal);
        List<string> cacheOrder = new List<string>();
        bool loaded;

        public StremioLibrary(StremioApiClient api, IStremioAuthKeyProvider auth, IKeyValueStore store, ICatalog catalog, Func<DateTimeOffset> now = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.auth = auth ?? throw new ArgumentNullException(nameof(auth));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.catalog = catalog;
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            outbox = new OutboxQueue(store, AccountKeys.StremioOutbox, ApplyRemoteAsync, LibraryOps.CanReplace);
            if (auth is StremioAccount account) account.SignedOut += (sender, args) => ResetMemory();
        }

        /// Drops the cached library and every queued write (memory and store).
        public async Task ResetAsync(CancellationToken token)
        {
            ResetMemory();
            await outbox.ClearAsync(token).ConfigureAwait(false);
            await PersistCacheAsync(token).ConfigureAwait(false);
        }

        void ResetMemory()
        {
            lock (cacheSync)
            {
                cache = new Dictionary<string, JObject>(StringComparer.Ordinal);
                cacheOrder = new List<string>();
                loaded = true;
            }
            outbox.ClearInMemory();
        }

        /// Latest sync failure (E5 historyError). Writes stay queued while this is a transient error.
        public string LastSyncError => outbox.LastError;
        public int PendingWrites => outbox.Count;
        public IReadOnlyList<JObject> PendingOperations => outbox.Snapshot();

        public async Task InitializeAsync(CancellationToken token)
        {
            await outbox.LoadAsync(token).ConfigureAwait(false);
            await LoadCacheAsync(token).ConfigureAwait(false);
        }

        /// Replays queued writes. Call on startup, when the network returns, and periodically.
        /// Returns true when everything has been written.
        public async Task<bool> FlushAsync(CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            return await outbox.DrainAsync(token).ConfigureAwait(false);
        }

        /// Queues a write and tries to send it (and anything before it). Never throws for sync errors.
        public async Task<bool> EnqueueAsync(JObject operation, CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            return await outbox.EnqueueAsync(operation, token).ConfigureAwait(false);
        }

        // ---- reads ----

        /// C1: all movie/series items that are not removed (temp items, i.e. continue-watching, included).
        public async Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token)
        {
            var items = await GetAllRawAsync(token).ConfigureAwait(false);
            return items.Select(LibraryRules.ToEntry).Where(e => e != null && !e.Removed).ToList();
        }

        /// Raw libraryItem objects (removed ones included), with pending writes overlaid.
        private Task launchRefresh;
        private bool fetchedThisRun;

        public async Task<IReadOnlyList<JObject>> GetAllRawAsync(CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            if (!fetchedThisRun)
            {
                // Launch: serve the saved library at once (Continue watching shows without waiting for Stremio) and
                // refresh it from Stremio behind it; later calls see the refreshed copy.
                var cached = Overlay(null);
                if (cached.Count > 0)
                {
                    if (launchRefresh == null)
                        launchRefresh = Task.Run(async () =>
                        {
                            try { await FetchAllSharedAsync(CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception) { /* offline or failed: the cached copy stays */ }
                        });
                    return cached;
                }
            }
            await FetchAllSharedAsync(token).ConfigureAwait(false);
            return Overlay(null);
        }

        private Task fetchAllInFlight;

        /// One full-library fetch at a time: Home, Search and the recommendations asking together share it (each
        /// used to send its own datastoreGet and rewrite the whole cache). A caller's cancel only stops its wait.
        private Task FetchAllSharedAsync(CancellationToken token)
        {
            Task task;
            lock (cacheSync)
            {
                if (fetchAllInFlight == null || fetchAllInFlight.IsCompleted)
                    fetchAllInFlight = Task.Run(() => FetchAllAsync(CancellationToken.None));
                task = fetchAllInFlight;
            }
            return Ru7100.Core.Addons.TaskUtil.WithCancellation(task, token);
        }

        private async Task FetchAllAsync(CancellationToken token)
        {
            try
            {
                var key = await auth.GetAuthKeyAsync(token).ConfigureAwait(false);
                var result = await api.PostAsync("datastoreGet", new JObject
                {
                    ["authKey"] = key,
                    ["collection"] = "libraryItem",
                    ["ids"] = new JArray(),
                    ["all"] = true,
                }, token).ConfigureAwait(false);
                var items = (result as JArray ?? new JArray()).OfType<JObject>().ToList();
                await ReplaceCacheAsync(items, token).ConfigureAwait(false);
                fetchedThisRun = true;
            }
            catch (Exception error) when (OutboxQueue.IsTransient(error) && !token.IsCancellationRequested)
            {
                // Offline: serve the cached copy.
            }
        }

        public async Task<LibraryEntry> GetAsync(string id, CancellationToken token)
        {
            var raw = await GetRawAsync(id, token).ConfigureAwait(false);
            return LibraryRules.ToEntry(raw);
        }

        public async Task<JObject> GetRawAsync(string id, CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            try
            {
                var item = await FetchRemoteAsync(id, token).ConfigureAwait(false);
                await UpdateCacheAsync(id, item, token).ConfigureAwait(false);
            }
            catch (Exception error) when (OutboxQueue.IsTransient(error) && !token.IsCancellationRequested)
            {
            }
            return Overlay(id).FirstOrDefault();
        }

        // ---- edits (D3-D5) ----

        public Task AddAsync(MetaDetail meta, CancellationToken token)
        {
            RequireMeta(meta);
            return EnqueueAsync(LibraryOps.Create(LibraryOps.Add, meta, false, now()), token);
        }

        public async Task RemoveAsync(string id, CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            var known = Overlay(id).FirstOrDefault();
            var op = known != null
                ? LibraryOps.CreateFromItem(LibraryOps.Remove, known, now())
                : LibraryOps.CreateForId(LibraryOps.Remove, id, now());
            await EnqueueAsync(op, token).ConfigureAwait(false);
        }

        /// D3: noNotif = !enabled. Like the original, this also (re)adds the item to the library.
        public async Task SetNotificationsAsync(string id, bool enabled, CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            var known = Overlay(id).FirstOrDefault();
            MetaDetail meta = null;
            if (known == null && catalog != null)
            {
                try { meta = await catalog.GetMetaAsync(MediaType.Series, id, token).ConfigureAwait(false); }
                catch (Exception) when (!token.IsCancellationRequested) { }
            }
            var op = known != null ? LibraryOps.CreateFromItem(LibraryOps.Notifications, known, now())
                : meta != null ? LibraryOps.Create(LibraryOps.Notifications, meta, false, now())
                : LibraryOps.CreateForId(LibraryOps.Notifications, id, now());
            op["enabled"] = enabled;
            await EnqueueAsync(op, token).ConfigureAwait(false);
        }

        /// D4/D5. For series, videoId must be one of meta.Videos.
        public Task MarkWatchedAsync(MetaDetail meta, string videoId, bool watched, CancellationToken token)
        {
            RequireMeta(meta);
            var op = LibraryOps.Create(LibraryOps.Watched, meta, meta.Summary.Type == MediaType.Series, now());
            if (meta.Summary.Type == MediaType.Series)
            {
                var video = LibraryRules.FindVideo(meta, videoId);
                if (video == null) throw new ArgumentException("Episode not found: " + videoId, nameof(videoId));
                op["videoId"] = video.Id;
                op["season"] = video.Season;
                op["episode"] = video.Episode;
                op["released"] = video.Released.HasValue ? IsoTime.Format(video.Released.Value) : null;
            }
            else
            {
                op["videoId"] = meta.Summary.Id;
            }
            op["watched"] = watched;
            return EnqueueAsync(op, token);
        }

        // ---- rules ----

        public bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId)
        {
            if (entry == null) return false;
            if (entry.Type == MediaType.Series)
                return WatchedIds(meta, entry.State?.Watched).Contains(videoId ?? "");
            return (entry.State?.FlaggedWatched ?? 0) > 0 || (entry.State?.TimesWatched ?? 0) > 0;
        }

        /// The watched episode ids of one series, decoded once per (metadata, watched string) instead of once per
        /// episode row: every IsWatched call used to sort the whole series and inflate its bitfield again.
        sealed class WatchedMemo
        {
            public MetaDetail Meta;
            public int VideoCount;
            public string Watched;
            public HashSet<string> Ids;
        }

        WatchedMemo watchedMemo;

        HashSet<string> WatchedIds(MetaDetail meta, string watched)
        {
            var count = meta?.Videos?.Count ?? 0;
            var memo = Volatile.Read(ref watchedMemo);
            if (memo != null && ReferenceEquals(memo.Meta, meta) && memo.VideoCount == count && string.Equals(memo.Watched, watched, StringComparison.Ordinal))
                return memo.Ids;
            var state = LibraryRules.EpisodeState(meta, watched);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = state.VideoIds;
            for (var i = 0; i < all.Count; i++)
            {
                var id = all[i] ?? "";
                if (seen.Add(id) && state.Get(i))   // GetVideo reads the first index of an id
                    ids.Add(id);
            }
            Volatile.Write(ref watchedMemo, new WatchedMemo { Meta = meta, VideoCount = count, Watched = watched, Ids = ids });
            return ids;
        }

        /// C5. Null for movies and when the series has nothing left to watch.
        public VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta)
        {
            if (meta?.Summary?.Type != MediaType.Series) return null;
            return LibraryRules.NextLibraryVideo(meta, entry?.State?.VideoId, entry?.State?.Watched, now());
        }

        /// C4. meta may be null for movies (and yields Library for series whose metadata failed).
        public LibraryStatus GetStatus(LibraryEntry entry, MetaDetail meta) => LibraryRules.Status(entry, meta, now());

        public static double ProgressPercent(LibraryEntry entry) => LibraryRules.ProgressPercent(entry?.State);

        public static bool NotificationsEnabled(LibraryEntry entry) => !(entry?.State?.NoNotif ?? false);

        /// C9.
        public bool IsNewEpisode(VideoInfo video, LibraryEntry entry) => LibraryRules.IsNewEpisode(video, entry);

        // ---- new episodes (C8) ----

        /// C8. isAvailable must return true only when streams exist and at least one survives ranking
        /// (episodic, anime check from F11). Results are sorted newest first. With acknowledge, the
        /// reported ids are stored as announced so sinceLastCheck skips them next time.
        public async Task<IReadOnlyList<NewEpisode>> GetNewEpisodesAsync(
            Func<MetaDetail, VideoInfo, CancellationToken, Task<bool>> isAvailable,
            CancellationToken token,
            string query = null,
            bool sinceLastCheck = false,
            bool acknowledge = false,
            int limit = DefaultNewEpisodeLimit)
        {
            if (isAvailable == null) throw new ArgumentNullException(nameof(isAvailable));
            if (catalog == null) throw new InvalidOperationException("A catalog is required for new episodes.");
            var alerts = await ReadAlertsAsync(token).ConfigureAwait(false);
            var announced = (JObject)alerts["announced"];
            var entries = (await GetAllAsync(token).ConfigureAwait(false))
                .Where(e => e.Type == MediaType.Series && !e.Removed && !e.Temp && !(e.State?.NoNotif ?? false)
                    && (query == null || (e.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            var found = new List<NewEpisode>();
            var foundSync = new object();
            bool exhausted = false;
            var current = now();

            using (var gate = new SemaphoreSlim(NewEpisodeConcurrency, NewEpisodeConcurrency))
            {
                var tasks = entries.Select(async entry =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref exhausted)) return;
                        MetaDetail meta;
                        try { meta = await catalog.GetMetaAsync(MediaType.Series, entry.Id, token).ConfigureAwait(false); }
                        catch (Exception) when (!token.IsCancellationRequested) { return; }
                        if (meta == null) return;
                        var watched = LibraryRules.EpisodeState(meta, entry.State?.Watched);
                        var baseline = entry.State?.LastWatched ?? entry.Created;
                        foreach (var video in LibraryRules.ReleasedVideos(meta, current))
                        {
                            lock (foundSync)
                            {
                                if (found.Count >= limit) { exhausted = true; return; }
                            }
                            if (watched.GetVideo(video.Id) || (video.Released.HasValue && video.Released.Value <= baseline)) continue;
                            if (sinceLastCheck) { lock (foundSync) { if (announced[video.Id] != null) continue; } }
                            bool available;
                            try { available = await isAvailable(meta, video, token).ConfigureAwait(false); }
                            catch (Exception) when (!token.IsCancellationRequested) { available = false; }
                            if (!available) continue;
                            lock (foundSync)
                            {
                                found.Add(new NewEpisode { SeriesId = meta.Summary?.Id ?? entry.Id, SeriesName = meta.Summary?.Name ?? entry.Name, Video = video });
                                if (acknowledge) announced[video.Id] = IsoTime.Format(now());
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var sorted = found.OrderByDescending(f => f.Video.Released?.ToUnixTimeMilliseconds() ?? 0).ToList();
            if (acknowledge) await StoreJson.WriteAsync(store, AccountKeys.StremioAlerts, alerts, token).ConfigureAwait(false);
            return sorted;
        }

        /// Marks video ids as announced (stremio-alerts.json equivalent).
        public async Task AcknowledgeAsync(IEnumerable<string> videoIds, CancellationToken token)
        {
            var alerts = await ReadAlertsAsync(token).ConfigureAwait(false);
            var announced = (JObject)alerts["announced"];
            foreach (var id in videoIds) announced[id] = IsoTime.Format(now());
            await StoreJson.WriteAsync(store, AccountKeys.StremioAlerts, alerts, token).ConfigureAwait(false);
        }

        public async Task<IReadOnlyCollection<string>> GetAnnouncedAsync(CancellationToken token)
        {
            var alerts = await ReadAlertsAsync(token).ConfigureAwait(false);
            return ((JObject)alerts["announced"]).Properties().Select(p => p.Name).ToList();
        }

        async Task<JObject> ReadAlertsAsync(CancellationToken token)
        {
            var alerts = await StoreJson.ReadAsync(store, AccountKeys.StremioAlerts, token).ConfigureAwait(false) as JObject ?? new JObject();
            if (!(alerts["announced"] is JObject)) alerts["announced"] = new JObject();
            return alerts;
        }

        // ---- remote writes (D1, D2) ----

        async Task<JObject> FetchRemoteAsync(string id, CancellationToken token)
        {
            var key = await auth.GetAuthKeyAsync(token).ConfigureAwait(false);
            var result = await api.PostAsync("datastoreGet", new JObject
            {
                ["authKey"] = key,
                ["collection"] = "libraryItem",
                ["ids"] = new JArray(id),
                ["all"] = false,
            }, token).ConfigureAwait(false);
            return (result as JArray)?.OfType<JObject>().FirstOrDefault();
        }

        async Task ApplyRemoteAsync(JObject op, CancellationToken token)
        {
            string id = LibraryOps.MetaId(op);
            JObject existing;
            try
            {
                existing = await FetchRemoteAsync(id, token).ConfigureAwait(false);
            }
            catch (StremioApiException error) when (!error.IsTransient)
            {
                // datastoreGet depends only on the auth key, never on the op: a rejected session must
                // not use up the op's attempts. Keep it queued until the account works again.
                throw new StremioApiException(error.Message, true, error.HttpStatus, error);
            }
            var merged = LibraryOps.ApplyTo(existing, op, now());
            if (merged == null)
            {
                await UpdateCacheAsync(id, existing, token).ConfigureAwait(false);
                return;
            }
            var key = await auth.GetAuthKeyAsync(token).ConfigureAwait(false);
            await api.PostAsync("datastorePut", new JObject
            {
                ["authKey"] = key,
                ["collection"] = "libraryItem",
                ["changes"] = new JArray(merged),
            }, token).ConfigureAwait(false);
            await UpdateCacheAsync(id, merged, token).ConfigureAwait(false);
        }

        // ---- cache + overlay ----

        async Task EnsureLoadedAsync(CancellationToken token)
        {
            if (loaded) return;
            await InitializeAsync(token).ConfigureAwait(false);
        }

        async Task LoadCacheAsync(CancellationToken token)
        {
            var saved = await StoreJson.ReadAsync(store, AccountKeys.StremioLibraryCache, token).ConfigureAwait(false) as JArray;
            lock (cacheSync)
            {
                if (loaded) return;
                if (saved != null) SetCache(saved.OfType<JObject>());
                loaded = true;
            }
        }

        void SetCache(IEnumerable<JObject> items)
        {
            cache = new Dictionary<string, JObject>(StringComparer.Ordinal);
            cacheOrder = new List<string>();
            foreach (var item in items)
            {
                var id = LibraryRules.StringOrNull(item["_id"]);
                if (id == null) continue;
                if (!cache.ContainsKey(id)) cacheOrder.Add(id);
                cache[id] = item;
            }
        }

        async Task ReplaceCacheAsync(IReadOnlyList<JObject> items, CancellationToken token)
        {
            lock (cacheSync) SetCache(items);
            await PersistCacheAsync(token).ConfigureAwait(false);
        }

        async Task UpdateCacheAsync(string id, JObject item, CancellationToken token)
        {
            lock (cacheSync)
            {
                if (item == null)
                {
                    if (cache.Remove(id)) cacheOrder.Remove(id);
                }
                else
                {
                    if (!cache.ContainsKey(id)) cacheOrder.Add(id);
                    cache[id] = (JObject)item.DeepClone();
                }
            }
            await PersistCacheAsync(token).ConfigureAwait(false);
        }

        async Task PersistCacheAsync(CancellationToken token)
        {
            await cacheWriteGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                JArray copy;
                lock (cacheSync) copy = new JArray(cacheOrder.Select(id => cache[id].DeepClone()));
                await StoreJson.WriteAsync(store, AccountKeys.StremioLibraryCache, copy, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                cacheWriteGate.Release();
            }
        }

        /// Cached items (all, or just onlyId) with queued operations applied in order.
        List<JObject> Overlay(string onlyId)
        {
            var items = new Dictionary<string, JObject>(StringComparer.Ordinal);
            var order = new List<string>();
            lock (cacheSync)
            {
                foreach (var id in cacheOrder)
                {
                    if (onlyId != null && id != onlyId) continue;
                    items[id] = (JObject)cache[id].DeepClone();
                    order.Add(id);
                }
            }
            var pending = outbox.Snapshot();
            var at = now();
            foreach (var op in pending)
            {
                var id = LibraryOps.MetaId(op);
                if (id == null || (onlyId != null && id != onlyId)) continue;
                items.TryGetValue(id, out var existing);
                JObject updated;
                try { updated = LibraryOps.ApplyTo(existing, op, at); }
                catch (Exception) { continue; }
                if (updated == null) continue;
                if (existing == null) order.Add(id);
                items[id] = updated;
            }
            return order.Select(id => items[id]).ToList();
        }

        static void RequireMeta(MetaDetail meta)
        {
            if (meta?.Summary?.Id == null) throw new ArgumentException("meta.Summary.Id is required", nameof(meta));
        }
    }
}
