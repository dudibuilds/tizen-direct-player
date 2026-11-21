using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Addons
{
    /// Source discovery across every stream add-on (F1, F13) plus ranking (StreamRanking) and
    /// external subtitles (F14).
    public sealed class SourceResolver : ISourceResolver
    {
        private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan EmptyStreamCacheTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan SubtitleTimeout = TimeSpan.FromSeconds(15);
        private const int DiscoveryLimit = 256;

        private readonly AddonRegistry registry;
        private readonly AddonHttp http;
        private readonly Func<DateTimeOffset> clock;
        private readonly Dictionary<string, Discovery> discoveries = new Dictionary<string, Discovery>(StringComparer.Ordinal);
        private readonly AsyncCache<IReadOnlyList<SubtitleCandidate>> subtitleCache;

        public SourceResolver(AddonRegistry registry, AddonHttp http = null, Func<DateTimeOffset> clock = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.http = http ?? new AddonHttp();
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            subtitleCache = new AsyncCache<IReadOnlyList<SubtitleCandidate>>(256, clock);
        }

        /// F1: overall discovery deadline and per-add-on fetch timeout.
        public TimeSpan DiscoveryDeadline { get; set; } = TimeSpan.FromSeconds(30);

        public DeviceCaps Device { get; set; } = DeviceCaps.Ru7100;

        public Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token) =>
            GetRankedAsync(type, videoId, options, null, token);

        /// With meta, anime is detected per F11 (including the ambiguous-animation wait); otherwise options.IsAnime.
        public async Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options,
            MetaDetail meta, CancellationToken token)
        {
            options = options ?? new SourceOptions();
            var streams = await DiscoverAsync(type, videoId, current => IsViable(current, type, options, meta), token).ConfigureAwait(false);
            var rankOptions = ToRankOptions(type, options, AnimeFor(options, meta, streams));
            rankOptions.EnglishOriginal = options.EnglishOriginal || SourceOptions.IsEnglishOriginal(meta);
            return StreamRanking.Rank(streams, rankOptions);
        }

        /// F13: a still-untried candidate is healthy (band 2) and top tier (anime 5; general 7, or 6 when the viewer
        /// prefers English subtitles, since tier 7 needs subtitles in another preferred language).
        public bool IsViable(IReadOnlyList<AddonStreamItem> streams, MediaType type, SourceOptions options, MetaDetail meta)
        {
            var anime = AnimeFor(options, meta, streams);
            if (!anime && meta != null && AnimeDetection.IsAmbiguousAnimation(meta)) return false;
            var top = anime ? 5 : Ru7100.Core.Playback.Languages.HasPreferredSubtitles ? 7 : 6;
            return StreamRanking.RankAnalyses(streams, ToRankOptions(type, options, anime))
                .Any(entry => entry.HealthBand == 2 && entry.LanguageTier(anime) == top);
        }

        private static bool AnimeFor(SourceOptions options, MetaDetail meta, IEnumerable<AddonStreamItem> streams) =>
            options.IsAnime || (meta != null && AnimeDetection.PrefersJapaneseAudio(meta, streams));

        private RankOptions ToRankOptions(MediaType type, SourceOptions options, bool anime) => new RankOptions
        {
            Episodic = options.Episodic || type == MediaType.Series,
            Anime = anime,
            EnglishOriginal = options.EnglishOriginal,
            Quality = options.Quality,
            MaxSizeGb = options.MaxSizeGb,
            ExcludeIdentities = options.ExcludeIdentities,
            Device = Device,
        };

        /// F1: shared discovery per type:videoId. Returns the deduplicated streams seen so far as soon as
        /// `viable` holds for them, or everything once all add-ons answered or the deadline passed.
        public async Task<IReadOnlyList<AddonStreamItem>> DiscoverAsync(MediaType type, string videoId,
            Func<IReadOnlyList<AddonStreamItem>, bool> viable, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("videoId is required", nameof(videoId));
            token.ThrowIfCancellationRequested();
            var discovery = GetOrStartDiscovery(type, videoId);
            var early = new TaskCompletionSource<IReadOnlyList<AddonStreamItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action check = () =>
            {
                if (early.Task.IsCompleted || viable == null) return;
                var snapshot = discovery.Snapshot();
                bool ok;
                try { ok = snapshot.Count > 0 && viable(snapshot); }
                catch (Exception) { ok = false; }
                if (ok) early.TrySetResult(snapshot);
            };
            discovery.AddListener(check);
            try
            {
                check();
                var first = await TaskUtil.WithCancellation(
                    Task.WhenAny(early.Task, discovery.Completion), token).ConfigureAwait(false);
                return await first.ConfigureAwait(false);
            }
            finally
            {
                discovery.RemoveListener(check);
            }
        }

        private Discovery GetOrStartDiscovery(MediaType type, string videoId)
        {
            var key = AddonTypes.ToStremio(type) + ":" + videoId;
            Discovery discovery;
            lock (discoveries)
            {
                var now = clock();
                if (discoveries.TryGetValue(key, out discovery) && (discovery.Pending || discovery.ExpiresAt > now))
                    return discovery;
                discovery = new Discovery();
                discoveries[key] = discovery;
                var stale = discoveries.Where(pair => !pair.Value.Pending && (pair.Value.ExpiresAt <= now || discoveries.Count > DiscoveryLimit))
                    .Select(pair => pair.Key).Where(k => k != key).ToList();
                foreach (var old in stale) discoveries.Remove(old);
                // RunDiscoveryAsync yields before doing any work, so starting it under the lock is cheap.
                discovery.Completion = RunDiscoveryAsync(discovery, type, videoId);
            }
            return discovery;
        }

        private async Task<IReadOnlyList<AddonStreamItem>> RunDiscoveryAsync(Discovery discovery, MediaType type, string videoId)
        {
            await Task.Yield();
            using (var deadline = new CancellationTokenSource(DiscoveryDeadline))
            {
                try
                {
                    var addons = await TaskUtil.WithCancellation(registry.GetForResourceAsync("stream", type, CancellationToken.None), deadline.Token)
                        .ConfigureAwait(false);
                    await Task.WhenAll(addons.Where(addon => !AddonRegistry.IsLocalCastAddon(addon))
                        .Select(addon => FetchAddonAsync(discovery, addon, type, videoId, deadline.Token))).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Deadline: keep whatever arrived.
                }
                catch (Exception)
                {
                    // Registry failure: no add-ons answered.
                }
                finally
                {
                    lock (discoveries)
                    {
                        discovery.Pending = false;
                        discovery.ExpiresAt = clock() + (discovery.Count > 0 ? StreamCacheTtl : EmptyStreamCacheTtl);
                    }
                }
            }
            return discovery.Snapshot();
        }

        private async Task FetchAddonAsync(Discovery discovery, AddonDescriptor addon, MediaType type, string videoId, CancellationToken deadline)
        {
            try
            {
                var url = AddonUrls.Resource(addon.TransportUrl, "stream", AddonTypes.ToStremio(type), videoId);
                var payload = await http.GetJsonAsync(url, DiscoveryDeadline, deadline).ConfigureAwait(false);
                discovery.Add(StreamRanking.ParseStreams(payload, addon));
            }
            catch (Exception)
            {
                // One add-on failing never fails discovery (Promise.allSettled in the original).
            }
        }

        // ---- F14 subtitles ----

        /// External subtitles from every subtitle add-on (OpenSubtitles v3 by default), preferred language first, then English,
        /// then the rest. Use SubtitleSelection for anime ordering and embedded-track rules.
        public async Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(videoId)) throw new ArgumentException("videoId is required", nameof(videoId));
            var extra = SubtitleExtra(source);
            var key = AddonTypes.ToStremio(type) + ":" + videoId + "|" + (extra ?? "");
            var all = await subtitleCache.GetOrLoadAsync(key, async () =>
            {
                var addons = await registry.GetForResourceAsync("subtitles", type, CancellationToken.None).ConfigureAwait(false);
                var lists = await Task.WhenAll(addons.Select(async addon =>
                {
                    try
                    {
                        var url = AddonUrls.Resource(addon.TransportUrl, "subtitles", AddonTypes.ToStremio(type), videoId, extra);
                        var payload = await http.GetJsonAsync(url, SubtitleTimeout, CancellationToken.None).ConfigureAwait(false);
                        var items = payload["subtitles"] as Newtonsoft.Json.Linq.JArray;
                        if (items == null) return new List<SubtitleCandidate>();
                        return items.OfType<Newtonsoft.Json.Linq.JObject>()
                            .Select(item => ParseSubtitle(item, addon.Id))
                            .Where(item => AddonUrls.IsWebUrl(item.Url))
                            .ToList();
                    }
                    catch (Exception)
                    {
                        return new List<SubtitleCandidate>();
                    }
                })).ConfigureAwait(false);
                return (IReadOnlyList<SubtitleCandidate>)lists.SelectMany(list => list).ToList();
            }, value => StreamCacheTtl, token).ConfigureAwait(false);
            return SubtitleSelection.Order(all, false);
        }

        /// Stremio's subtitle extras for the opened file: videoHash, videoSize and filename (only those known).
        public static string SubtitleExtra(StreamCandidate source)
        {
            if (source == null) return null;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(source.FileHash))
                parts.Add("videoHash=" + Uri.EscapeDataString(source.FileHash));
            if (source.SizeBytes > 0)
                parts.Add("videoSize=" + source.SizeBytes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(source.Filename))
                parts.Add("filename=" + Uri.EscapeDataString(source.Filename));
            return parts.Count == 0 ? null : string.Join("&", parts);
        }

        /// One subtitle from an add-on answer, with the release facts OpenSubtitles v3 adds when it has them.
        public static SubtitleCandidate ParseSubtitle(Newtonsoft.Json.Linq.JObject item, string addonId)
        {
            string Text(string key) => item[key]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)item[key] : null;
            var fps = item["fpsMilli"];
            return new SubtitleCandidate
            {
                Url = Text("url"),
                Language = Text("lang"),
                AddonId = addonId,
                Release = Text("movieReleaseName") ?? Text("subtitleFileName"),
                ReleaseGroup = Text("releaseGroup"),
                ReleaseFormat = Text("releaseFormat"),
                FpsMilli = fps != null && (fps.Type == Newtonsoft.Json.Linq.JTokenType.Integer || fps.Type == Newtonsoft.Json.Linq.JTokenType.Float) && (double)fps > 0 ? (int?)(int)(double)fps : null,
                HashMatch = Text("m") == "h",
                MovieHash = Text("moviehash") ?? Text("movieHash"),
            };
        }

        private sealed class Discovery
        {
            private readonly object gate = new object();
            private readonly List<AddonStreamItem> streams = new List<AddonStreamItem>();
            private readonly HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<Action> listeners = new List<Action>();
            public Task<IReadOnlyList<AddonStreamItem>> Completion;
            public bool Pending = true;
            public DateTimeOffset ExpiresAt = DateTimeOffset.MaxValue;

            public int Count { get { lock (gate) return streams.Count; } }

            public IReadOnlyList<AddonStreamItem> Snapshot()
            {
                lock (gate) return streams.ToList();
            }

            public void Add(IEnumerable<AddonStreamItem> items)
            {
                Action[] notify;
                lock (gate)
                {
                    foreach (var item in items)
                    {
                        var identity = item.Identity;
                        if (identity == null || !identities.Add(identity)) continue;
                        streams.Add(item);
                    }
                    notify = listeners.ToArray();
                }
                foreach (var listener in notify) listener();
            }

            public void AddListener(Action listener) { lock (gate) listeners.Add(listener); }
            public void RemoveListener(Action listener) { lock (gate) listeners.Remove(listener); }
        }
    }
}
