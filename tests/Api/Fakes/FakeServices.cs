// In-memory fakes for the agent API. Used by tests/Api and linked into host/ (the Mac dev host).
// C# 8 so they read like core code.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Api;

namespace Ru7100.Fakes
{
    public sealed class InMemoryStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();

        public Task<string> ReadAsync(string key, CancellationToken token)
        {
            lock (values) return Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
        }

        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            lock (values) values[key] = value;
            return Task.CompletedTask;
        }
    }

    public static class FakeData
    {
        public static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public static MetaDetail Series(string id, string name, int seasons, int episodes, DateTimeOffset firstRelease, params string[] genres)
        {
            var videos = new List<VideoInfo>();
            var release = firstRelease;
            for (var s = 1; s <= seasons; s++)
                for (var e = 1; e <= episodes; e++)
                {
                    videos.Add(new VideoInfo { Id = id + ":" + s + ":" + e, Season = s, Episode = e, Released = release, Title = name + " S" + s + "E" + e });
                    release = release.AddDays(7);
                }
            return new MetaDetail
            {
                Summary = new MetaSummary { Id = id, Type = MediaType.Series, Name = name, ReleaseInfo = firstRelease.Year + "-" },
                Genres = genres,
                Videos = videos,
            };
        }

        public static MetaDetail Movie(string id, string name, int year) => new MetaDetail
        {
            Summary = new MetaSummary { Id = id, Type = MediaType.Movie, Name = name, ReleaseInfo = year.ToString() },
        };

        /// Breaking Bad (old, all watched-able), Space Force + Dog Days (an ambiguous "space dog" search),
        /// Fresh Show (episodes released this month), Sintel (movie).
        public static FakeCatalog Catalog() => new FakeCatalog(new[]
        {
            Series("tt0903747", "Breaking Bad", 2, 3, new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero), "Drama"),
            Series("tt7529770", "Space Force", 1, 3, new DateTimeOffset(2020, 5, 29, 0, 0, 0, TimeSpan.Zero), "Comedy"),
            Movie("tt0100000", "Dog Days", 2001),
            Series("tt9999001", "Fresh Show", 1, 4, Now.AddDays(-21), "Animation"),
            Movie("tt1727587", "Sintel", 2010),
        });
    }

    public sealed class FakeCatalog : ICatalog
    {
        private readonly List<MetaDetail> metas;

        public FakeCatalog(IEnumerable<MetaDetail> metas)
        {
            this.metas = metas.ToList();
        }

        public Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token)
        {
            var words = (query ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            IReadOnlyList<MetaSummary> hits = metas
                .Where(m => type == null || m.Summary.Type == type)
                .Where(m => words.Any(w => m.Summary.Name.ToLowerInvariant().Contains(w)))
                .Select(m => m.Summary)
                .Take(limit)
                .ToList();
            return Task.FromResult(hits);
        }

        public Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token) =>
            Task.FromResult(metas.FirstOrDefault(m => m.Summary.Id == id && m.Summary.Type == type));

        public Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token)
        {
            IReadOnlyList<MetaSummary> list = metas.Where(m => m.Summary.Type == type).Skip(skip).Select(m => m.Summary).ToList();
            return Task.FromResult(list);
        }
    }

    public sealed class FakeLibrary : ILibrary
    {
        private readonly Dictionary<string, LibraryEntry> entries = new Dictionary<string, LibraryEntry>();
        private readonly Dictionary<string, HashSet<string>> watched = new Dictionary<string, HashSet<string>>();
        private readonly Func<DateTimeOffset> clock;

        public FakeLibrary(Func<DateTimeOffset> clock = null)
        {
            this.clock = clock ?? (() => FakeData.Now);
        }

        public LibraryEntry Put(MetaDetail meta, Action<LibraryEntry> change = null, params string[] watchedIds)
        {
            lock (entries)
            {
                var entry = new LibraryEntry
                {
                    Id = meta.Summary.Id, Name = meta.Summary.Name, Type = meta.Summary.Type,
                    Created = FakeData.Now.AddYears(-1), Modified = FakeData.Now.AddYears(-1),
                };
                change?.Invoke(entry);
                entries[entry.Id] = entry;
                watched[entry.Id] = new HashSet<string>(watchedIds);
                return entry;
            }
        }

        public Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token)
        {
            lock (entries) return Task.FromResult<IReadOnlyList<LibraryEntry>>(entries.Values.ToList());
        }

        public Task<LibraryEntry> GetAsync(string id, CancellationToken token)
        {
            lock (entries) return Task.FromResult(entries.TryGetValue(id, out var entry) ? entry : null);
        }

        public Task AddAsync(MetaDetail meta, CancellationToken token)
        {
            lock (entries)
            {
                if (entries.TryGetValue(meta.Summary.Id, out var existing)) { existing.Removed = false; existing.Temp = false; }
                else Put(meta, e => e.Created = clock());
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string id, CancellationToken token)
        {
            lock (entries) if (entries.TryGetValue(id, out var entry)) entry.Removed = true;
            return Task.CompletedTask;
        }

        public Task SetNotificationsAsync(string id, bool enabled, CancellationToken token)
        {
            lock (entries)
            {
                if (!entries.TryGetValue(id, out var entry)) throw new ApiException("not_in_library", "Not in the library: " + id, 404);
                entry.State.NoNotif = !enabled;
            }
            return Task.CompletedTask;
        }

        public Task MarkWatchedAsync(MetaDetail meta, string videoId, bool isWatched, CancellationToken token)
        {
            lock (entries)
            {
                if (!entries.ContainsKey(meta.Summary.Id)) Put(meta);
                var entry = entries[meta.Summary.Id];
                if (isWatched) watched[entry.Id].Add(videoId); else watched[entry.Id].Remove(videoId);
                if (meta.Summary.Type == MediaType.Movie) entry.State.FlaggedWatched = isWatched ? 1 : 0;
                else
                {
                    var next = NextVideo(entry, meta);
                    entry.State.VideoId = next?.Id ?? videoId;
                    entry.State.TimeOffsetMs = 0;
                }
                if (isWatched) entry.State.LastWatched = clock();
            }
            return Task.CompletedTask;
        }

        public bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId)
        {
            lock (entries) return watched.TryGetValue(entry.Id, out var set) && set.Contains(videoId);
        }

        public VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta)
        {
            var videos = meta.Videos ?? Array.Empty<VideoInfo>();
            var current = videos.FirstOrDefault(v => v.Id == entry.State?.VideoId);
            if (current != null && !IsWatched(entry, meta, current.Id)) return current;
            var now = clock();
            return videos.FirstOrDefault(v => (v.Released == null || v.Released <= now) && !IsWatched(entry, meta, v.Id));
        }
    }

    public sealed class FakeSourceResolver : ISourceResolver
    {
        public HashSet<string> Unavailable { get; } = new HashSet<string>();

        public Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token)
        {
            IReadOnlyList<StreamCandidate> list = Unavailable.Contains(videoId)
                ? new StreamCandidate[0]
                : new[]
                {
                    new StreamCandidate
                    {
                        AddonId = "com.stremio.torrentio.addon", Name = "Torrentio\n1080p", Title = videoId + " WEB-DL 1080p",
                        InfoHash = "0123456789abcdef0123456789abcdef01234567", FileIndex = 0, Filename = videoId.Replace(':', '.') + ".mkv",
                        SizeBytes = 1_500_000_000, Seeders = 120, Quality = "1080p", VideoCodec = "h264", AudioCodec = "aac",
                        LanguageTier = 4, HealthBand = 2, Score = 123.456,
                    },
                };
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SubtitleCandidate>>(new SubtitleCandidate[0]);
    }

    public sealed class FakeAddonRegistry : IAddonRegistry
    {
        private readonly List<AddonDescriptor> addons = new List<AddonDescriptor>
        {
            new AddonDescriptor
            {
                Id = "com.stremio.torrentio.addon", Name = "Torrentio", TransportUrl = "https://torrentio.strem.fun/manifest.json",
                Resources = new[] { "stream" }, Types = new[] { "movie", "series" },
            },
        };

        public Task<IReadOnlyList<AddonDescriptor>> GetInstalledAsync(CancellationToken token)
        {
            lock (addons) return Task.FromResult<IReadOnlyList<AddonDescriptor>>(addons.ToList());
        }

        public Task<AddonDescriptor> InstallAsync(string manifestUrl, CancellationToken token)
        {
            var host = new Uri(manifestUrl).Host;
            var addon = new AddonDescriptor
            {
                Id = "fake." + host, Name = host, TransportUrl = manifestUrl,
                Resources = new[] { "subtitles" }, Types = new[] { "movie", "series" },
            };
            lock (addons)
            {
                addons.RemoveAll(a => a.Id == addon.Id);
                addons.Add(addon);
            }
            return Task.FromResult(addon);
        }

        public Task RemoveAsync(string addonId, CancellationToken token)
        {
            lock (addons)
                if (addons.RemoveAll(a => a.Id == addonId) == 0) throw ApiException.NotFound("No add-on with id " + addonId);
            return Task.CompletedTask;
        }
    }

    public sealed class FakeAccount : IStremioAccount
    {
        private int polls;

        public bool IsSignedIn { get; private set; }
        /// Number of link_complete polls answered "pending" before approval (host default 1).
        public int PendingPolls { get; set; } = 1;
        public string Code { get; } = "ABCDE";

        public Task<(string Code, string Link)> BeginLinkAsync(CancellationToken token)
        {
            polls = 0;
            return Task.FromResult((Code, "https://www.stremio.com/link?code=" + Code));
        }

        public Task<bool> CompleteLinkAsync(string code, CancellationToken token)
        {
            if (code != Code) throw new ApiException("link_failed", "Unknown or expired link code");
            if (polls++ < PendingPolls) return Task.FromResult(false);
            IsSignedIn = true;
            return Task.FromResult(true);
        }

        public Task SignOutAsync(CancellationToken token)
        {
            IsSignedIn = false;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AddonDescriptor>> GetAddonCollectionAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AddonDescriptor>>(new AddonDescriptor[0]);
    }
}
