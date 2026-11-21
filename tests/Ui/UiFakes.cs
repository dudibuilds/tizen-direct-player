using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Ui;

namespace Ru7100.Tests.Ui
{
    public sealed class FakeClock : IUiClock
    {
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);
        public void Advance(int ms) => UtcNow = UtcNow.AddMilliseconds(ms);
    }

    public sealed class MemoryStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public Task<string> ReadAsync(string key, CancellationToken token) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    public sealed class FakePlayer : IPlayerControls, ICreditsControls
    {
        public void KeepWatchingCredits() => Commands.Add("keep-watching-credits");

        public readonly List<string> Commands = new List<string>();
        private PlayerSnapshot snapshot = new PlayerSnapshot();
        /// Reads of Snapshot by the screens (the real one asks the engine for its whole status each time).
        public int SnapshotReads;
        public PlayerSnapshot Snapshot { get { SnapshotReads++; return snapshot; } set => snapshot = value; }
        /// A native seek that has not landed yet: SeekTo records the command but the position stays put.
        public bool SeekLag;
        public PlayerSettings Settings { get; } = new PlayerSettings(new MemoryStore());
        public PlayRequest LastRequest;
        public TaskCompletionSource<bool> PlayGate;

        public Task PlayAsync(PlayRequest request, CancellationToken token)
        {
            LastRequest = request;
            Commands.Add("play-request " + (request.Video?.Id ?? request.Meta?.Summary?.Id));
            Snapshot.SessionActive = true;
            Snapshot.State = PlayerState.Playing;
            Snapshot.Meta = request.Meta;
            Snapshot.Video = request.Video;
            Snapshot.Title = request.Meta?.Summary?.Name;
            Snapshot.PositionMs = request.ResumeMs;
            return PlayGate?.Task ?? Task.CompletedTask;
        }

        public void Play() { Commands.Add("play"); Snapshot.State = PlayerState.Playing; }
        public void Pause() { Commands.Add("pause"); Snapshot.State = PlayerState.Paused; }
        public void TogglePause()
        {
            Commands.Add("toggle");
            Snapshot.State = Snapshot.State == PlayerState.Paused ? PlayerState.Playing : PlayerState.Paused;
        }
        public void Stop() { Commands.Add("stop"); Snapshot.SessionActive = false; Snapshot.State = PlayerState.Stopped; }
        public void SeekTo(long positionMs) { Commands.Add("seek-to " + positionMs); if (!SeekLag) snapshot.PositionMs = positionMs; }
        public void SelectAudio(int trackIndex) { Commands.Add("audio " + trackIndex); Snapshot.SelectedAudio = trackIndex; }
        public void SelectSubtitle(string subtitleId) { Commands.Add("subtitle " + (subtitleId ?? "off")); Snapshot.SelectedSubtitleId = subtitleId; }
        public void SetSubtitleOffset(int offsetMs) { Commands.Add("subtitle-offset " + offsetMs); Snapshot.SubtitleOffsetMs = offsetMs; }
        public void ResetSubtitleTiming() { Commands.Add("subtitle-reset"); Snapshot.SubtitleOffsetMs = 0; Snapshot.SubtitleSync = null; }
        public void SetBinge(bool enabled) { Commands.Add("binge " + enabled); Snapshot.Binge = enabled; }
        public void SetStopAfterCurrent(bool enabled) { Commands.Add("stop-after " + enabled); Snapshot.StopAfterCurrent = enabled; }
        public Task SwitchSourceAsync(StreamCandidate candidate, CancellationToken token) { Commands.Add("switch " + candidate.Identity); return Task.CompletedTask; }
        public Task PlayNextAsync(CancellationToken token) { Commands.Add("next"); return Task.CompletedTask; }
    }

    public sealed class FakeCatalog : ICatalog
    {
        public readonly Dictionary<string, MetaDetail> Metas = new Dictionary<string, MetaDetail>();
        public List<MetaSummary> SearchResults = new List<MetaSummary>();
        public readonly List<string> Searches = new List<string>();
        public TaskCompletionSource<MetaDetail> MetaGate;

        public Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token)
        {
            Searches.Add(query);
            return Task.FromResult<IReadOnlyList<MetaSummary>>(SearchResults.Where(m => m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList());
        }

        public Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token) =>
            MetaGate?.Task ?? Task.FromResult(Metas[id]);

        public Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MetaSummary>>(Metas.Values.Where(m => m.Summary.Type == type).Select(m => m.Summary).ToList());
    }

    public sealed class FakeLibrary : ILibrary
    {
        public readonly Dictionary<string, LibraryEntry> Entries = new Dictionary<string, LibraryEntry>();
        public readonly HashSet<string> WatchedIds = new HashSet<string>();

        public Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<LibraryEntry>>(Entries.Values.ToList());
        public Task<LibraryEntry> GetAsync(string id, CancellationToken token) => Task.FromResult(Entries.TryGetValue(id, out var e) ? e : null);
        public Task AddAsync(MetaDetail meta, CancellationToken token)
        {
            Entries[meta.Summary.Id] = new LibraryEntry { Id = meta.Summary.Id, Name = meta.Summary.Name, Type = meta.Summary.Type };
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string id, CancellationToken token)
        {
            Entries[id].Removed = true;
            return Task.CompletedTask;
        }
        public Task SetNotificationsAsync(string id, bool enabled, CancellationToken token)
        {
            Entries[id].State.NoNotif = !enabled;
            return Task.CompletedTask;
        }
        public Task MarkWatchedAsync(MetaDetail meta, string videoId, bool watched, CancellationToken token)
        {
            if (watched) WatchedIds.Add(videoId); else WatchedIds.Remove(videoId);
            return Task.CompletedTask;
        }
        public bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId) => WatchedIds.Contains(videoId);
        public VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta) => meta.Videos.FirstOrDefault(v => v.Season > 0 && !WatchedIds.Contains(v.Id));
    }

    public sealed class FakeAccount : IStremioAccount
    {
        public bool IsSignedIn { get; set; }
        public int Polls;
        public int ApproveAfter = 2;
        public Task<(string Code, string Link)> BeginLinkAsync(CancellationToken token) => Task.FromResult(("ABCD", "https://www.stremio.com/link"));
        public Task<bool> CompleteLinkAsync(string code, CancellationToken token)
        {
            Polls++;
            if (Polls >= ApproveAfter)
                IsSignedIn = true;
            return Task.FromResult(IsSignedIn);
        }
        public Task SignOutAsync(CancellationToken token) { IsSignedIn = false; return Task.CompletedTask; }
        public Task<IReadOnlyList<AddonDescriptor>> GetAddonCollectionAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AddonDescriptor>>(Array.Empty<AddonDescriptor>());
    }

    public sealed class FakeAddons : IAddonRegistry
    {
        public readonly List<AddonDescriptor> Installed = new List<AddonDescriptor>
        {
            new AddonDescriptor { Id = "com.stremio.torrentio.addon", Name = "Torrentio", TransportUrl = "https://torrentio.strem.fun/manifest.json", Resources = new[] { "stream" } }
        };
        public Task<IReadOnlyList<AddonDescriptor>> GetInstalledAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<AddonDescriptor>>(Installed.ToList());
        public Task<AddonDescriptor> InstallAsync(string manifestUrl, CancellationToken token)
        {
            var a = new AddonDescriptor { Id = "x." + Installed.Count, Name = "Custom", TransportUrl = manifestUrl };
            Installed.Add(a);
            return Task.FromResult(a);
        }
        public Task RemoveAsync(string addonId, CancellationToken token)
        {
            Installed.RemoveAll(a => a.Id == addonId);
            return Task.CompletedTask;
        }
    }

    public sealed class FakeSources : ISourceResolver
    {
        public Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StreamCandidate>>(new[]
            {
                new StreamCandidate { Name = "Torrentio 1080p", InfoHash = "AAAA", FileIndex = 1, Quality = "1080p", SizeBytes = 2_100_000_000, Seeders = 120, LanguageTier = 7 },
                new StreamCandidate { Name = "Torrentio 4k", InfoHash = "BBBB", Quality = "4k", SizeBytes = 6_000_000_000, Seeders = 30, LanguageTier = 4 }
            });
        public Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SubtitleCandidate>>(Array.Empty<SubtitleCandidate>());
    }

    public sealed class FakeQueue : IQueueControls
    {
        public QueueSnapshot State = new QueueSnapshot();
        public Task<QueueSnapshot> GetAsync(CancellationToken token) => Task.FromResult(State);
        public Task AddAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            State.Items = State.Items.Concat(new[] { new QueueItemView { MetaId = meta.Summary.Id, Name = meta.Summary.Name, VideoId = video.Id, Season = video.Season, Episode = video.Episode, Title = video.Title } }).ToList();
            State.Status = "ready";
            return Task.CompletedTask;
        }
        public Task RemoveAtAsync(int index, CancellationToken token)
        {
            var list = State.Items.ToList();
            list.RemoveAt(index);
            State.Items = list;
            return Task.CompletedTask;
        }
        public Task StartAsync(CancellationToken token) { State.Status = "playing"; return Task.CompletedTask; }
        public Task SkipAsync(CancellationToken token) { State.CurrentIndex++; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken token) { State = new QueueSnapshot(); return Task.CompletedTask; }
    }

    public sealed class UiHarness
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly FakePlayer Player = new FakePlayer();
        public readonly FakeCatalog Catalog = new FakeCatalog();
        public readonly FakeLibrary Library = new FakeLibrary();
        public readonly FakeAccount Account = new FakeAccount { IsSignedIn = true };
        public readonly FakeAddons Addons = new FakeAddons();
        public readonly FakeQueue Queue = new FakeQueue();
        public readonly TvShell Shell;

        public static readonly MetaDetail Series = new MetaDetail
        {
            Summary = new MetaSummary { Id = "tt0903747", Type = MediaType.Series, Name = "Breaking Bad", ReleaseInfo = "2008–2013" },
            Genres = new[] { "Drama" },
            Description = "A chemistry teacher turns to crime.",
            Videos = new[]
            {
                new VideoInfo { Id = "tt0903747:1:1", Season = 1, Episode = 1, Title = "Pilot", Released = new DateTimeOffset(2008, 1, 20, 0, 0, 0, TimeSpan.Zero) },
                new VideoInfo { Id = "tt0903747:1:2", Season = 1, Episode = 2, Title = "Cat's in the Bag...", Released = new DateTimeOffset(2008, 1, 27, 0, 0, 0, TimeSpan.Zero) },
                new VideoInfo { Id = "tt0903747:1:3", Season = 1, Episode = 3, Title = "...And the Bag's in the River", Released = new DateTimeOffset(2008, 2, 10, 0, 0, 0, TimeSpan.Zero) },
                new VideoInfo { Id = "tt0903747:2:1", Season = 2, Episode = 1, Title = "Seven Thirty-Seven", Released = new DateTimeOffset(2009, 3, 8, 0, 0, 0, TimeSpan.Zero) }
            }
        };

        public static readonly MetaDetail Movie = new MetaDetail
        {
            Summary = new MetaSummary { Id = "tt1727587", Type = MediaType.Movie, Name = "Sintel", ReleaseInfo = "2010" },
            Genres = new[] { "Animation" }
        };

        public UiHarness()
        {
            Catalog.Metas[Series.Summary.Id] = Series;
            Catalog.Metas[Movie.Summary.Id] = Movie;
            Catalog.Metas["tt5753856"] = new MetaDetail
            {
                Summary = new MetaSummary { Id = "tt5753856", Type = MediaType.Series, Name = "Dark" },
                Videos = new[] { new VideoInfo { Id = "tt5753856:1:1", Season = 1, Episode = 1, Title = "Secrets", Released = new DateTimeOffset(2017, 12, 1, 0, 0, 0, TimeSpan.Zero) } }
            };
            Catalog.SearchResults.Add(Series.Summary);
            Catalog.SearchResults.Add(Movie.Summary);
            Shell = new TvShell(new UiServices
            {
                Catalog = Catalog,
                Library = Library,
                Account = Account,
                Addons = Addons,
                Sources = new FakeSources(),
                Queue = Queue,
                Player = Player,
                Clock = Clock,
                Dispatcher = new ImmediateDispatcher()
            });
        }

        public void Press(params RemoteKey[] keys)
        {
            foreach (var key in keys)
            {
                Shell.HandleKey(key);
                Clock.Advance(500);   // separate taps, not a held key
                Shell.Tick();
            }
        }

        public void Hold(RemoteKey key, int repeats, int intervalMs = 100)
        {
            for (var i = 0; i < repeats; i++)
            {
                Shell.HandleKey(key);
                Clock.Advance(intervalMs);
            }
        }

        public void Wait(int ms)
        {
            for (var t = 0; t < ms; t += 100)
            {
                Clock.Advance(100);
                Shell.Tick();
            }
        }

        /// On a Browse filter pill: opens its dropdown, moves `steps` options down (negative: up) and applies.
        public void PickFilter(int steps)
        {
            Press(RemoteKey.Select);
            for (var i = 0; i < Math.Abs(steps); i++)
                Press(steps > 0 ? RemoteKey.Down : RemoteKey.Up);
            Press(RemoteKey.Select);
        }

        public string Text => UiText.RenderText(Shell);

        /// Waits (real time, at most 2 s) for work that finishes on another thread, such as a failed PlayAsync.
        public void Until(Func<bool> condition)
        {
            for (var i = 0; i < 400 && !condition(); i++)
                System.Threading.Thread.Sleep(5);
        }

        /// Moves overlay focus to `row`: Up from the transport bar to the button row, then along it.
        public void FocusRow(PlayerViewModel vm, OverlayRow row)
        {
            if (row == OverlayRow.Transport)
            {
                if (vm.FocusedRow != row) Press(RemoteKey.Down);
                return;
            }
            if (!vm.OverlayVisible)
                Press(RemoteKey.Up);
            if (vm.FocusedRow == OverlayRow.Transport)
                Press(RemoteKey.Up);
            var actions = vm.Actions.ToList();
            var target = actions.IndexOf(row);
            if (target < 0)
                throw new InvalidOperationException("Row not reachable: " + row);
            for (var i = 0; i < 20 && vm.FocusedRow != row; i++)
                Press(actions.IndexOf(vm.FocusedRow) < target ? RemoteKey.Right : RemoteKey.Left);
            if (vm.FocusedRow != row)
                throw new InvalidOperationException("Row not reachable: " + row);
        }

        /// Overlay → Audio & subtitles → Subtitle settings (the first item of the subtitles column; it opens on Timing).
        public void OpenSubtitleSettings(PlayerViewModel vm)
        {
            if (!vm.OverlayVisible)
                Press(RemoteKey.Select);
            FocusRow(vm, OverlayRow.AudioSubtitles);
            Press(RemoteKey.Select);
            Press(RemoteKey.Right);
            for (var i = vm.SubtitleIndex; i > 0; i--)
                Press(RemoteKey.Up);
            Press(RemoteKey.Select);
            if (vm.Panel != PlayerPanel.Style)
                throw new InvalidOperationException("Subtitle settings did not open");
        }

        /// Starts a series episode with two audio tracks and embedded + external subtitles.
        public PlayerViewModel StartEpisode(long positionMs = 600_000)
        {
            Shell.Start(new HomeViewModel());
            var vm = Shell.StartPlayback(new PlayRequest { Meta = Series, Video = Series.Videos[0], ResumeMs = positionMs });
            Player.Snapshot.DurationMs = 2_800_000;
            Player.Snapshot.AudioTracks = new[] { new TrackInfo { Index = 0, Language = "eng" }, new TrackInfo { Index = 1, Language = "spa" } };
            Player.Snapshot.SelectedAudio = 0;
            Player.Snapshot.Subtitles = new[]
            {
                new SubtitleOption { Id = "embedded:0", Language = "eng" },
                new SubtitleOption { Id = "external:es", Language = "spa", External = true }
            };
            Player.Snapshot.SelectedSubtitleId = "external:es";
            Shell.Tick();
            Player.Commands.Clear();
            return vm;
        }
    }
}
