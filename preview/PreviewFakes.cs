using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Addons;
using Ru7100.Core.Ui;

namespace Ru7100.Preview
{
    /// In-memory data for the preview: a snapshot of real Cinemeta metadata (preview/fixtures/cinemeta.json:
    /// titles, episode names, overviews, stills, ratings, trailers). Images load from the public metahub host.
    internal static class Fixtures
    {
        private static JArray raw;

        private static JArray Raw()
        {
            if (raw != null) return raw;
            using (var stream = typeof(Fixtures).Assembly.GetManifestResourceStream("cinemeta.json"))
            using (var reader = new System.IO.StreamReader(stream ?? throw new InvalidOperationException("Missing cinemeta.json")))
                raw = JArray.Parse(reader.ReadToEnd());
            return raw;
        }

        public static List<MetaDetail> All() => Raw().OfType<JObject>().Select(CinemetaJson.ParseMeta).ToList();

        public static Dictionary<string, MetaExtras> Extras() =>
            Raw().OfType<JObject>().ToDictionary(m => (string)m["id"], CinemetaJson.ParseExtras);
    }

    internal sealed class PreviewExtras : IMetaExtras
    {
        private readonly Dictionary<string, MetaExtras> extras = Fixtures.Extras();

        public async Task<MetaExtras> GetAsync(MediaType type, string id, CancellationToken token)
        {
            await Task.Delay(120, token).ConfigureAwait(false);
            return extras.TryGetValue(id, out var e) ? e : new MetaExtras();
        }
    }

    internal sealed class PreviewCatalog : ICatalog
    {
        private readonly List<MetaDetail> metas;

        public PreviewCatalog(List<MetaDetail> metas) => this.metas = metas;

        public async Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token)
        {
            await Task.Delay(250, token).ConfigureAwait(false);
            var q = query.Trim();
            return metas.Where(m => (type == null || m.Summary.Type == type) &&
                    (m.Summary.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (m.Description ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .Take(limit).Select(m => m.Summary).ToList();
        }

        public async Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token)
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            return metas.FirstOrDefault(m => m.Summary.Id == id) ?? throw new InvalidOperationException("Unknown title " + id);
        }

        public async Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token)
        {
            await Task.Delay(200, token).ConfigureAwait(false);
            return metas.Where(m => m.Summary.Type == type).Skip(skip).Select(m => m.Summary).ToList();
        }
    }
    /// Library with a watched set per video id (stands in for the Stremio watched bitfield).
    internal sealed class PreviewLibrary : ILibrary
    {
        private readonly ConcurrentDictionary<string, LibraryEntry> entries = new ConcurrentDictionary<string, LibraryEntry>();
        private readonly ConcurrentDictionary<string, bool> watched = new ConcurrentDictionary<string, bool>();

        public PreviewLibrary(List<MetaDetail> metas)
        {
            var now = DateTimeOffset.UtcNow;
            void Add(string id, string videoId, long offset, long duration, int daysAgo, params string[] watchedIds)
            {
                var meta = metas.First(m => m.Summary.Id == id);
                entries[id] = new LibraryEntry
                {
                    Id = id, Name = meta.Summary.Name, Type = meta.Summary.Type, Poster = meta.Summary.Poster,
                    Created = now.AddDays(-200), Modified = now.AddDays(-daysAgo),
                    State = new LibraryState { VideoId = videoId, TimeOffsetMs = offset, DurationMs = duration, LastWatched = now.AddDays(-daysAgo) }
                };
                foreach (var w in watchedIds)
                    watched[w] = true;
            }
            Add("tt0903747", "tt0903747:2:3", 1_260_000, 2_820_000, 1, "tt0903747:1:1", "tt0903747:1:2", "tt0903747:1:3", "tt0903747:1:4", "tt0903747:1:5", "tt0903747:1:6", "tt0903747:1:7", "tt0903747:1:8", "tt0903747:2:1", "tt0903747:2:2");
            Add("tt1727587", "tt1727587", 312_000, 888_000, 2);
            Add("tt14452776", "tt14452776:4:4", 0, 0, 10, "tt14452776:4:1", "tt14452776:4:2", "tt14452776:4:3");
            Add("tt11280740", "tt11280740:2:7", 0, 0, 9, "tt11280740:2:6");
            Add("tt5753856", "tt5753856:1:1", 0, 0, 30);
            // Watched before the latest seasons aired, so "New episodes" has something to show.
            entries["tt14452776"].State.LastWatched = new DateTimeOffset(2022, 6, 1, 0, 0, 0, TimeSpan.Zero);
            entries["tt11280740"].State.LastWatched = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        }

        public Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<LibraryEntry>>(entries.Values.ToList());
        public Task<LibraryEntry> GetAsync(string id, CancellationToken token) => Task.FromResult(entries.TryGetValue(id, out var e) ? e : null);

        public Task AddAsync(MetaDetail meta, CancellationToken token)
        {
            var now = DateTimeOffset.UtcNow;
            var e = entries.GetOrAdd(meta.Summary.Id, _ => new LibraryEntry { Id = meta.Summary.Id, Created = now });
            e.Name = meta.Summary.Name;
            e.Type = meta.Summary.Type;
            e.Poster = meta.Summary.Poster;
            e.Removed = false;
            e.Modified = now;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string id, CancellationToken token)
        {
            if (entries.TryGetValue(id, out var e))
                e.Removed = true;
            return Task.CompletedTask;
        }

        public Task SetNotificationsAsync(string id, bool enabled, CancellationToken token)
        {
            if (entries.TryGetValue(id, out var e))
                e.State.NoNotif = !enabled;
            return Task.CompletedTask;
        }

        public Task MarkWatchedAsync(MetaDetail meta, string videoId, bool isWatched, CancellationToken token)
        {
            if (isWatched)
                watched[videoId] = true;
            else
                watched.TryRemove(videoId, out _);
            return Task.CompletedTask;
        }

        public bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId) => watched.ContainsKey(videoId);

        public VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta)
        {
            var now = DateTimeOffset.UtcNow;
            var current = meta.Videos.FirstOrDefault(v => v.Id == entry.State.VideoId);
            if (current != null && !watched.ContainsKey(current.Id))
                return current;
            return meta.Videos.FirstOrDefault(v => v.Season > 0 && !watched.ContainsKey(v.Id) && (v.Released == null || v.Released <= now));
        }

        /// Progress written by the preview player (stands in for IProgressSink).
        public void SaveProgress(string metaId, string videoId, long positionMs, long durationMs)
        {
            if (!entries.TryGetValue(metaId, out var e))
                return;
            e.State.VideoId = videoId;
            e.State.TimeOffsetMs = positionMs;
            e.State.DurationMs = durationMs;
            e.State.LastWatched = DateTimeOffset.UtcNow;
            if (durationMs > 0 && positionMs >= 0.9 * durationMs)
            {
                watched[videoId] = true;
                e.State.TimeOffsetMs = 0;
            }
        }
    }

    internal sealed class PreviewAccount : IStremioAccount
    {
        private DateTimeOffset approveAt;

        public bool IsSignedIn { get; set; } = true;

        public Task<(string Code, string Link)> BeginLinkAsync(CancellationToken token)
        {
            approveAt = DateTimeOffset.UtcNow.AddSeconds(12);   // the preview "approves" after 12 s
            return Task.FromResult(("K7Q2", "https://www.stremio.com/link"));
        }

        public Task<bool> CompleteLinkAsync(string code, CancellationToken token)
        {
            if (DateTimeOffset.UtcNow >= approveAt)
                IsSignedIn = true;
            return Task.FromResult(IsSignedIn);
        }

        public Task SignOutAsync(CancellationToken token)
        {
            IsSignedIn = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AddonDescriptor>> GetAddonCollectionAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AddonDescriptor>>(Array.Empty<AddonDescriptor>());
    }

    internal sealed class PreviewAddons : IAddonRegistry
    {
        private readonly List<AddonDescriptor> installed = new List<AddonDescriptor>
        {
            new AddonDescriptor { Id = "com.linvo.cinemeta", Name = "Cinemeta", TransportUrl = "https://v3-cinemeta.strem.io/manifest.json", Resources = new[] { "catalog", "meta" }, Types = new[] { "movie", "series" } },
            new AddonDescriptor { Id = "com.stremio.torrentio.addon", Name = "Torrentio", TransportUrl = "https://torrentio.strem.fun/sort=qualitysize|qualityfilter=480p,scr,cam/manifest.json", Resources = new[] { "stream" }, Types = new[] { "movie", "series" } },
            new AddonDescriptor { Id = "org.stremio.opensubtitlesv3", Name = "OpenSubtitles v3", TransportUrl = "https://opensubtitles-v3.strem.io/manifest.json", Resources = new[] { "subtitles" }, Types = new[] { "movie", "series" } }
        };

        public Task<IReadOnlyList<AddonDescriptor>> GetInstalledAsync(CancellationToken token)
        {
            lock (installed)
                return Task.FromResult<IReadOnlyList<AddonDescriptor>>(installed.ToList());
        }

        public async Task<AddonDescriptor> InstallAsync(string manifestUrl, CancellationToken token)
        {
            await Task.Delay(400, token).ConfigureAwait(false);
            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || !manifestUrl.EndsWith("manifest.json", StringComparison.Ordinal))
                throw new InvalidOperationException("That is not a manifest.json URL");
            var addon = new AddonDescriptor { Id = uri.Host, Name = uri.Host, TransportUrl = manifestUrl, Resources = new[] { "stream" } };
            lock (installed)
                installed.Add(addon);
            return addon;
        }

        public Task RemoveAsync(string addonId, CancellationToken token)
        {
            lock (installed)
                installed.RemoveAll(a => a.Id == addonId);
            return Task.CompletedTask;
        }
    }

    /// Torrentio-style streams run through the real core/Addons ranking and hard filters.
    internal sealed class PreviewSources : ISourceResolver, ISourceCatalog
    {
        private static IReadOnlyList<AddonStreamItem> Streams(MetaDetail meta, VideoInfo video)
        {
            var name = (meta.Summary.Name ?? "Title").Replace(' ', '.').Replace(":", "");
            var tag = video == null ? (meta.Summary.ReleaseInfo ?? "2020").Split('–')[0] : "S" + video.Season.ToString("00") + "E" + video.Episode.ToString("00");
            AddonStreamItem T(string quality, string rest, int seeds, string size, string flags, string hash, int? idx = 1, string source = "ThePirateBay") => new AddonStreamItem
            {
                AddonId = "com.stremio.torrentio.addon", AddonName = "Torrentio", Name = "Torrentio\n" + quality,
                Title = name + "." + tag + "." + rest + "\n\U0001F464 " + seeds + " \U0001F4BE " + size + " ⚙️ " + source + (flags == null ? "" : "\n" + flags),
                InfoHash = hash, FileIdx = idx, Filename = name + "." + tag + "." + rest
            };
            return new List<AddonStreamItem>
            {
                T("1080p", "1080p.WEB-DL.DDP5.1.H.264-NTb.mkv", 412, "2.3 GB", "\U0001F1EC\U0001F1E7 / \U0001F1EA\U0001F1F8 Spanish subs", "1111111111111111111111111111111111111111"),
                T("1080p", "1080p.BluRay.x264.AAC.2.0-RARBG.mp4", 188, "1.9 GB", null, "2222222222222222222222222222222222222222"),
                T("4k", "2160p.WEB-DL.HDR10.DDP5.1.HEVC-FLUX.mkv", 96, "5.8 GB", "\U0001F1EC\U0001F1E7", "3333333333333333333333333333333333333333"),
                T("720p", "720p.HDTV.x264.AAC-GRP.mkv", 31, "650 MB", "\U0001F1EC\U0001F1E7 / \U0001F1EE\U0001F1F9 / \U0001F1EA\U0001F1F8 Multi Audio ESub", "4444444444444444444444444444444444444444", null),
                T("1080p", "1080p.WEBRip.HEVC.10bit.EAC3.5.1-Mixed.mkv", 9, "1.1 GB", "\U0001F1F7\U0001F1FA", "5555555555555555555555555555555555555555"),
                T("4k", "2160p.BluRay.REMUX.DV.TrueHD.7.1.Atmos-FGT.mkv", 54, "38.2 GB", null, "6666666666666666666666666666666666666666"),
                T("1080p", "1080p.WEB.AV1.Opus-AV1R.mkv", 77, "900 MB", null, "7777777777777777777777777777777777777777"),
                T("1080p", "1080p.BluRay.DTS-HD.MA.5.1.x264.mkv", 140, "4.4 GB", null, "8888888888888888888888888888888888888888"),
                new AddonStreamItem { AddonId = "direct", AddonName = "Direct HTTP", Name = "Direct\n720p", Title = "Sintel trailer 720p H.264 AAC", Url = PreviewPlayer.SampleVideoUrl }
            };
        }

        public async Task<IReadOnlyList<SourceEntry>> GetAsync(MetaDetail meta, VideoInfo video, SourceOptions options, CancellationToken token)
        {
            await Task.Delay(900, token).ConfigureAwait(false);
            return AddonSourceCatalog.FromStreams(Streams(meta, video), meta, video, options, DeviceCaps.Ru7100);
        }

        public Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token)
        {
            var meta = new MetaDetail { Summary = new MetaSummary { Id = videoId, Type = type, Name = "Title" } };
            var video = type == MediaType.Series ? new VideoInfo { Id = videoId, Season = 1, Episode = 1 } : null;
            return Task.FromResult<IReadOnlyList<StreamCandidate>>(AddonSourceCatalog.FromStreams(Streams(meta, video), meta, video, options, DeviceCaps.Ru7100)
                .Where(e => e.Playable).Select(e => e.Candidate).ToList());
        }

        public Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SubtitleCandidate>>(Array.Empty<SubtitleCandidate>());
    }
    internal sealed class PreviewRecommendations : IRecommendationsFeed
    {
        private readonly List<MetaDetail> metas;

        public PreviewRecommendations(List<MetaDetail> metas) => this.metas = metas;

        public Task<IReadOnlyList<RecommendationItem>> GetAsync(CancellationToken token)
        {
            string[] ids = { "tt14452776", "tt5753856", "tt0816692", "tt2560140", "tt1375666", "tt15398776", "tt0944947", "tt1160419" };
            return Task.FromResult<IReadOnlyList<RecommendationItem>>(ids.Select(id => metas.First(m => m.Summary.Id == id))
                .Select(m => new RecommendationItem { Meta = m.Summary, Reason = "Because you watched Breaking Bad", Score = 0.8 }).ToList());
        }
    }

    internal sealed class PreviewQueue : IQueueControls
    {
        private readonly PreviewPlayer player;
        private readonly object gate = new object();
        private QueueSnapshot state = new QueueSnapshot();
        private readonly List<(MetaDetail Meta, VideoInfo Video)> items = new List<(MetaDetail, VideoInfo)>();

        public PreviewQueue(PreviewPlayer player) => this.player = player;

        public Task<QueueSnapshot> GetAsync(CancellationToken token)
        {
            lock (gate)
            {
                if (state.Status == "playing" && !player.Snapshot.SessionActive)
                    state.Status = "stopped";
                return Task.FromResult(new QueueSnapshot { Status = state.Status, CurrentIndex = state.CurrentIndex, Items = state.Items.ToList() });
            }
        }

        public Task AddAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            lock (gate)
            {
                items.Add((meta, video));
                state.Items = state.Items.Concat(new[] { new QueueItemView { MetaId = meta.Summary.Id, Type = meta.Summary.Type, Name = meta.Summary.Name, VideoId = video.Id, Season = video.Season, Episode = video.Episode, Title = video.Title } }).ToList();
                if (state.Status == "empty")
                    state.Status = "ready";
            }
            return Task.CompletedTask;
        }

        public Task RemoveAtAsync(int index, CancellationToken token)
        {
            lock (gate)
            {
                items.RemoveAt(index);
                var list = state.Items.ToList();
                list.RemoveAt(index);
                state.Items = list;
                if (index < state.CurrentIndex)
                    state.CurrentIndex--;
                if (list.Count == 0)
                    state = new QueueSnapshot();
            }
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken token)
        {
            (MetaDetail Meta, VideoInfo Video) item;
            lock (gate)
            {
                if (items.Count == 0)
                    return Task.CompletedTask;
                state.CurrentIndex = Math.Min(state.CurrentIndex, items.Count - 1);
                state.Status = "playing";
                item = items[state.CurrentIndex];
            }
            return player.PlayAsync(new PlayRequest { Meta = item.Meta, Video = item.Video }, token);
        }

        public Task SkipAsync(CancellationToken token)
        {
            lock (gate)
                state.CurrentIndex = Math.Min(state.CurrentIndex + 1, Math.Max(0, items.Count - 1));
            return state.Status == "playing" ? StartAsync(token) : Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken token)
        {
            lock (gate)
            {
                items.Clear();
                state = new QueueSnapshot();
            }
            return Task.CompletedTask;
        }
    }

    internal sealed class MemoryKeyValueStore : IKeyValueStore
    {
        private readonly ConcurrentDictionary<string, string> values = new ConcurrentDictionary<string, string>();
        public Task<string> ReadAsync(string key, CancellationToken token) => Task.FromResult(values.TryGetValue(key, out var v) ? v : null);
        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            values[key] = value;
            return Task.CompletedTask;
        }
    }

    /// Files under a directory, one per key (the preview persists subtitle style across restarts).
    internal sealed class FileKeyValueStore : IKeyValueStore
    {
        private readonly string directory;

        public FileKeyValueStore(string directory)
        {
            this.directory = directory;
            System.IO.Directory.CreateDirectory(directory);
        }

        private string PathFor(string key) => System.IO.Path.Combine(directory, key + ".json");

        public Task<string> ReadAsync(string key, CancellationToken token)
        {
            var path = PathFor(key);
            return Task.FromResult(System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null);
        }

        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            var path = PathFor(key);
            var temp = path + ".tmp";
            System.IO.File.WriteAllText(temp, value);
            if (System.IO.File.Exists(path))
                System.IO.File.Replace(temp, path, null);
            else
                System.IO.File.Move(temp, path);
            return Task.CompletedTask;
        }
    }
}
