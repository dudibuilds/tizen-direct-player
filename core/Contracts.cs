// Shared contracts for the native Stremio feature set.
// Constraints: compiles into the Tizen 5.0 app (tizen50, C# 8) and the macOS test/desktop projects.
// No Tizen or ElmSharp APIs under core/. JSON via Newtonsoft.Json. HTTP via injectable HttpMessageHandler.
// Parity reference: docs/parity-checklist.md (rules from samsung-stremio-spotify-mcp).
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core
{
    public enum MediaType { Movie, Series }

    public sealed class MetaSummary
    {
        public string Id { get; set; }            // IMDb id, e.g. tt0903747
        public MediaType Type { get; set; }
        public string Name { get; set; }
        public string ReleaseInfo { get; set; }
        public string Poster { get; set; }
    }

    public sealed class VideoInfo
    {
        public string Id { get; set; }            // series: tt..:S:E ; movie: tt..
        public int Season { get; set; }
        public int Episode { get; set; }
        public DateTimeOffset? Released { get; set; }
        public string Title { get; set; }
    }

    public sealed class MetaDetail
    {
        public MetaSummary Summary { get; set; }
        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();
        public string Country { get; set; }
        public string Description { get; set; }
        public string Background { get; set; }
        public IReadOnlyList<VideoInfo> Videos { get; set; } = Array.Empty<VideoInfo>(); // sorted season, episode, released
    }

    public sealed class AddonDescriptor
    {
        public string TransportUrl { get; set; }  // .../manifest.json
        public string Id { get; set; }
        public string Name { get; set; }
        public IReadOnlyList<string> Resources { get; set; } = Array.Empty<string>(); // stream, subtitles, catalog, meta
        public IReadOnlyList<string> Types { get; set; } = Array.Empty<string>();
    }

    /// One playable source after ranking. Either a torrent (InfoHash + optional FileIndex) or a direct HTTP URL.
    public sealed class StreamCandidate
    {
        public string AddonId { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string InfoHash { get; set; }
        public int? FileIndex { get; set; }
        public string Url { get; set; }
        public IReadOnlyList<string> Trackers { get; set; } = Array.Empty<string>();
        public string Filename { get; set; }
        public long? SizeBytes { get; set; }
        /// The opened file's OpenSubtitles hash (sent to subtitle add-ons as videoHash); set after the source opens.
        public string FileHash { get; set; }
        public int? Seeders { get; set; }
        public string Quality { get; set; }       // 4k | 1080p | 720p | unknown
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public int LanguageTier { get; set; }
        public int HealthBand { get; set; }
        public double Score { get; set; }
        public string Identity => InfoHash != null ? InfoHash.ToLowerInvariant() + ":" + (FileIndex?.ToString() ?? "auto") : Url;
    }

    public sealed class SubtitleCandidate
    {
        public string Url { get; set; }
        public string Language { get; set; }      // ISO 639-2/1 as given by the add-on
        public string AddonId { get; set; }
        /// The release the subtitle was made for (OpenSubtitles movieReleaseName or its file name), when the add-on says.
        public string Release { get; set; }
        public string ReleaseGroup { get; set; }
        public string ReleaseFormat { get; set; }
        /// Frame rate the uploader declared, in thousandths (25000, 23976); often wrong, never acted on alone.
        public int? FpsMilli { get; set; }
        /// The add-on matched it by the file's hash (OpenSubtitles "m": "h").
        public bool HashMatch { get; set; }
        public string MovieHash { get; set; }
        /// How well it fits the file being played (SubtitleMatching.Score); set by the session.
        public SubtitleMatch Match { get; set; }
    }

    /// How well an external subtitle fits the file being played, best last. A subtitle made for the same release
    /// shares its timeline; one made for another release (a TV recording, a web version, a frame-rate conversion)
    /// may be shifted or run at another speed.
    public enum SubtitleMatch { None = 0, Source = 1, Group = 2, Release = 3, Hash = 4 }

    public sealed class SourceOptions
    {
        public string Quality { get; set; } = "auto";  // auto | 4k | 1080p
        public double? MaxSizeGb { get; set; }          // only lowers the 6.5 GB cap
        public bool IsAnime { get; set; }
        public bool Episodic { get; set; }
        /// The title was made in English (see SourceOptions.IsEnglishOriginal): a release that names no language is
        /// then English already, so one that only adds a dub does not outrank it.
        public bool EnglishOriginal { get; set; }
        public ISet<string> ExcludeIdentities { get; set; } = new HashSet<string>();

        private static readonly string[] EnglishCountries = { "united states", "usa", "united kingdom", "uk", "canada", "australia", "ireland", "new zealand" };

        /// True when the title's first listed country of origin is English-speaking. Unknown country: false.
        public static bool IsEnglishOriginal(MetaDetail meta)
        {
            var first = (meta?.Country ?? "").Split(',')[0].Trim().ToLowerInvariant();
            return first.Length > 0 && System.Array.IndexOf(EnglishCountries, first) >= 0;
        }
    }

    /// Stremio library item as stored by api.strem.io (libraryItem collection).
    public sealed class LibraryState
    {
        public string VideoId { get; set; }
        public long TimeOffsetMs { get; set; }
        public long DurationMs { get; set; }
        public long TimeWatchedMs { get; set; }
        public long OverallTimeWatchedMs { get; set; }
        public int TimesWatched { get; set; }
        public int FlaggedWatched { get; set; }
        public DateTimeOffset? LastWatched { get; set; }
        public string Watched { get; set; }       // serialized watched bitfield
        public bool NoNotif { get; set; }
    }

    public sealed class LibraryEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MediaType Type { get; set; }
        public string Poster { get; set; }
        public bool Removed { get; set; }
        public bool Temp { get; set; }
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset Modified { get; set; }
        public LibraryState State { get; set; } = new LibraryState();
    }

    // ---- Services. Area owners implement these; others code against them. ----

    public interface IKeyValueStore      // TV: app data dir files; desktop/tests: temp dir
    {
        Task<string> ReadAsync(string key, CancellationToken token);
        Task WriteAsync(string key, string value, CancellationToken token);   // atomic replace
    }

    public interface ICatalog            // area: addons
    {
        Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token);
        Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token);
        Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token);
    }

    public interface IAddonRegistry      // area: addons
    {
        Task<IReadOnlyList<AddonDescriptor>> GetInstalledAsync(CancellationToken token);
        Task<AddonDescriptor> InstallAsync(string manifestUrl, CancellationToken token);
        Task RemoveAsync(string addonId, CancellationToken token);
    }

    public interface ISourceResolver     // area: addons (discovery + ranking)
    {
        Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token);
        Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token);
    }

    public interface IStremioAccount     // area: account
    {
        bool IsSignedIn { get; }
        Task<(string Code, string Link)> BeginLinkAsync(CancellationToken token);
        Task<bool> CompleteLinkAsync(string code, CancellationToken token);   // false while pending
        Task SignOutAsync(CancellationToken token);
        Task<IReadOnlyList<AddonDescriptor>> GetAddonCollectionAsync(CancellationToken token);
    }

    public interface ILibrary            // area: account
    {
        Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token);
        Task<LibraryEntry> GetAsync(string id, CancellationToken token);
        Task AddAsync(MetaDetail meta, CancellationToken token);
        Task RemoveAsync(string id, CancellationToken token);
        Task SetNotificationsAsync(string id, bool enabled, CancellationToken token);
        Task MarkWatchedAsync(MetaDetail meta, string videoId, bool watched, CancellationToken token);
        bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId);
        VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta);              // C5
    }

    public interface IProgressSink       // area: account (Stremio datastore + optional Trakt); ordered writes
    {
        Task PlaybackStartedAsync(MetaDetail meta, VideoInfo video, long resumeMs, CancellationToken token);
        Task ProgressAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token);
        Task PausedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token);
        Task CompletedAsync(MetaDetail meta, VideoInfo video, CancellationToken token);
        Task StoppedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, bool markedWatched, CancellationToken token);
    }

    public enum PlayerState { Idle, Preparing, Playing, Paused, Buffering, Stopped, Completed, Error }

    public sealed class TrackInfo
    {
        public int Index { get; set; }
        public string Language { get; set; }
        public string Label { get; set; }
    }

    /// The device player. TV: Tizen AVPlay via Tizen.Multimedia.Player; desktop: fake or local player.
    public interface IPlayerBackend
    {
        PlayerState State { get; }
        long PositionMs { get; }
        long DurationMs { get; }
        IReadOnlyList<TrackInfo> AudioTracks { get; }
        IReadOnlyList<TrackInfo> EmbeddedSubtitleTracks { get; }
        Task OpenAsync(string url, long startMs, CancellationToken token);
        void Play();
        void Pause();
        Task SeekAsync(long positionMs, CancellationToken token);
        void Stop();
        void SelectAudio(int index);
        event EventHandler<PlayerState> StateChanged;
    }

    /// Turns a candidate into a URL the player can open (torrent engine + loopback server, or direct URL).
    public interface ISourceOpener       // area: playback
    {
        Task<IOpenedSource> OpenAsync(StreamCandidate candidate, CancellationToken token);
    }

    /// Optional on top of ISourceOpener: reports progress while a source opens, so the playback race can stop the other
    /// candidates once one is clearly healthy (race cut). A separate interface, not a default method: the Tizen 5.0
    /// runtime predates default interface methods, and openers without progress (dev host, simulation, fakes) still work.
    public interface IReportingSourceOpener : ISourceOpener   // area: playback
    {
        Task<IOpenedSource> OpenAsync(StreamCandidate candidate, Action<OpenProgress> progress, CancellationToken token);
    }

    /// A snapshot of an opening source: torrent metadata in hand, connected peers, current download rate.
    public sealed class OpenProgress
    {
        public bool HasMetadata { get; set; }
        public int Peers { get; set; }
        public double DownloadBytesPerSecond { get; set; }
    }

    public interface IOpenedSource : IDisposable
    {
        string PlayerUrl { get; }
        long LengthBytes { get; }
        double DownloadBytesPerSecond { get; }
        int Peers { get; }
    }

    /// A source that can hand over the first and last bytes of the playing file without disturbing the player
    /// (the torrent keeps those pieces pinned): used for the file's OpenSubtitles hash.
    public interface IFileEnds
    {
        /// False until both ends are downloaded and verified.
        bool TryReadEnds(int count, out byte[] head, out byte[] tail);
    }

    /// A player that can say the frame rate of the video it opened (whole frames per second, as Tizen reports it).
    public interface IVideoFormatSource
    {
        int? VideoFps { get; }
    }

    /// A source that another open can take over (a torrent's single stream): once preempted its URL no longer serves.
    public interface IPreemptibleSource
    {
        bool Preempted { get; }
    }

    /// Where binge and queue playback hands over to the next episode: the first of 98 % of the runtime or 30 s before
    /// the end. The engine advances there (PlaybackSession's monitor), and the player's next-episode card counts down to
    /// the same point, so the number on the card reaches zero when the next episode starts.
    public static class EpisodeHandOff
    {
        public const double Ratio = 0.98;
        public const long BeforeEndMs = 30_000;

        /// Position of the hand-over in an episode of `durationMs` (0 when the duration is unknown).
        public static long AtMs(long durationMs) =>
            durationMs <= 0 ? 0 : Math.Min((long)Math.Ceiling(Ratio * durationMs), durationMs - BeforeEndMs);

        public static bool Reached(long positionMs, long durationMs) => durationMs > 0 && positionMs >= AtMs(durationMs);

        /// Time left until the hand-over, never below zero.
        public static long RemainingMs(long positionMs, long durationMs) => Math.Max(0, AtMs(durationMs) - positionMs);
    }
}
