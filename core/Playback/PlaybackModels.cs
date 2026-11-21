using System;
using System.Collections.Generic;

namespace Ru7100.Core.Playback
{
    public enum PlaybackMode { Single, Binge, Queue }

    public static class PlaybackModes
    {
        public static string ToWire(PlaybackMode mode) => mode == PlaybackMode.Binge ? "binge" : mode == PlaybackMode.Queue ? "queue" : "single";
    }

    /// A play command from the remote UI or the agent API.
    public sealed class PlayRequest
    {
        /// IMDb id (tt...). When set, Query is ignored.
        public string MetaId { get; set; }
        /// Optional type hint for MetaId; otherwise series then movie are tried.
        public MediaType? Type { get; set; }
        /// Free-text title (B2 resolution with ambiguity), used when MetaId is empty.
        public string Query { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        /// H2/H3: a bare series title or a movie needs Confirmed = true to start.
        public bool Confirmed { get; set; }
        public PlaybackMode Mode { get; set; } = PlaybackMode.Single;
        public string Quality { get; set; } = "auto";
        public double? MaxSizeGb { get; set; }
        /// Optional explicit start position; overrides the library resume rule.
        public long? StartMs { get; set; }
        /// A source the user picked in the source list: only this candidate is tried (no ranking or failover).
        public StreamCandidate Source { get; set; }
    }

    public static class PlayStatus
    {
        public const string Starting = "starting";
        public const string Playing = "playing";
        public const string ConfirmationRequired = "confirmation_required";
        public const string CaughtUp = "caught_up";
        public const string Ambiguous = "ambiguous";
        public const string Superseded = "superseded";
        public const string Failed = "failed";
        public const string NotFound = "not_found";
    }

    public sealed class VideoRef
    {
        public string Id { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string Title { get; set; }
        public DateTimeOffset? Released { get; set; }

        public static VideoRef From(VideoInfo video) => video == null ? null : new VideoRef
        {
            Id = video.Id, Season = video.Season, Episode = video.Episode, Title = video.Title, Released = video.Released,
        };
    }

    public sealed class SourceFailure
    {
        public string Identity { get; set; }
        public string Name { get; set; }
        public string Error { get; set; }
    }

    public sealed class PlayResult
    {
        public string Status { get; set; }
        public string MetaId { get; set; }
        public string Name { get; set; }
        public VideoRef Video { get; set; }
        public long ResumeMs { get; set; }
        public PlaybackMode Mode { get; set; }
        /// H2/H3 proposal text, e.g. "Resume S1E3 at 12:04?".
        public string Proposal { get; set; }
        public VideoRef NextScheduled { get; set; }
        public IReadOnlyList<MetaSummary> Candidates { get; set; }
        public string Error { get; set; }
        public SourceStatus Source { get; set; }
        public List<SourceFailure> FallbacksTried { get; set; } = new List<SourceFailure>();
        public string Warning { get; set; }
        public QueueStatus Queue { get; set; }
    }

    /// H8 session record, persisted for I10 restore.
    public sealed class SessionRecord
    {
        public string Id { get; set; }
        public string MetaId { get; set; }
        public MediaType MediaType { get; set; }
        public string Name { get; set; }
        public bool IsAnime { get; set; }
        public string VideoId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string VideoTitle { get; set; }
        public PlaybackMode Mode { get; set; }
        public bool StopAfterCurrent { get; set; }
        /// The viewer chose to keep watching the credits (Back on the next-episode card): no hand-over at the cut-off
        /// point; the item still advances when it ends.
        public bool CreditsHeld { get; set; }
        public string Quality { get; set; } = "auto";
        public double? MaxSizeGb { get; set; }
        public string SourceIdentity { get; set; }
        public string SourceName { get; set; }
        public string SourceInfoHash { get; set; }
        public long SourceSizeBytes { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public long ResumeMs { get; set; }
        public long LastPositionMs { get; set; }
        public long DurationMs { get; set; }
        public long LastSavedMs { get; set; }
        public int CredibleSamples { get; set; }
        public bool MarkedWatched { get; set; }
        public bool UserPaused { get; set; }
        public string AudioLanguage { get; set; }
        public SubtitleDecision Subtitle { get; set; }
        public RecoveryState Recovery { get; set; } = new RecoveryState();
        public StreamHealth Health { get; set; }
        public DateTimeOffset? LastThroughputSampleAt { get; set; }
        public string HistoryError { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string EndReason { get; set; }
    }

    public sealed class RecoveryState
    {
        public List<string> Attempted { get; set; } = new List<string>();
        public int Switches { get; set; }
        /// Replacement attempts that failed; they count against MaxSwitches like successful ones.
        public int Failures { get; set; }
        public DateTimeOffset? RetryAfter { get; set; }
        public string LastError { get; set; }
    }

    // ---- Queue (J1) ----

    public static class QueueStatuses
    {
        public const string Ready = "ready";
        public const string Starting = "starting";
        public const string Playing = "playing";
        public const string Paused = "paused";
        public const string Stopped = "stopped";
        public const string Interrupted = "interrupted";
        public const string Error = "error";
        public const string Completed = "completed";
    }

    public sealed class QueueItem
    {
        public string MetaId { get; set; }
        public MediaType MediaType { get; set; } = MediaType.Series;
        public string Name { get; set; }
        public string VideoId { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
        public string Title { get; set; }
    }

    public sealed class QueueState
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public int CurrentIndex { get; set; }
        public string Quality { get; set; } = "auto";
        public double? MaxSizeGb { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string LastError { get; set; }
        public string LastStopReason { get; set; }
        public List<QueueItem> Items { get; set; } = new List<QueueItem>();
    }

    public sealed class QueueItemRequest
    {
        public string MetaId { get; set; }
        public string Query { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

    public sealed class QueueResult
    {
        public string Status { get; set; }      // ok | ambiguous | queue_complete | error
        public int? ItemIndex { get; set; }
        public IReadOnlyList<MetaSummary> Candidates { get; set; }
        public string Error { get; set; }
        public QueueStatus Queue { get; set; }
        public PlayResult Play { get; set; }
    }

    public sealed class QueueStatus
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public int CurrentIndex { get; set; }
        public int CurrentPosition { get; set; }
        public int Total { get; set; }
        public string Quality { get; set; }
        public double? MaxSizeGb { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string LastError { get; set; }
        public string LastStopReason { get; set; }
        public List<QueueItem> Items { get; set; } = new List<QueueItem>();

        public static QueueStatus From(QueueState queue) => queue == null ? null : new QueueStatus
        {
            Id = queue.Id, Status = queue.Status, CurrentIndex = queue.CurrentIndex, CurrentPosition = queue.CurrentIndex + 1,
            Total = queue.Items.Count, Quality = queue.Quality, MaxSizeGb = queue.MaxSizeGb, CreatedAt = queue.CreatedAt,
            CompletedAt = queue.CompletedAt, LastError = queue.LastError, LastStopReason = queue.LastStopReason,
            Items = new List<QueueItem>(queue.Items),
        };
    }

    // ---- Tracks and subtitles ----

    public enum SubtitleKind { None, Embedded, External }

    /// F14 decision the player/UI layer applies. Embedded: select EmbeddedIndex on the backend.
    /// External: load External.Url and feed cues through the app subtitle layer.
    public sealed class SubtitleDecision
    {
        public SubtitleKind Kind { get; set; }
        public int? EmbeddedIndex { get; set; }
        public SubtitleCandidate External { get; set; }
        public string Language { get; set; }   // eng | jpn | other code
        public string Reason { get; set; }
    }

    /// One subtitle cue for the app-drawn subtitle layer. StartMs is media time (offset not applied).
    public sealed class SubtitleCue
    {
        public string Text { get; set; }
        public long StartMs { get; set; }
        public long DurationMs { get; set; }
        public bool Embedded { get; set; }
        public static readonly SubtitleCue Clear = new SubtitleCue { Text = "" };
    }

    /// Per-show language memory (scope addition): applied to the next episode of the same show.
    public sealed class TrackPreference
    {
        public string AudioLanguage { get; set; }      // normalized code, e.g. eng, jpn
        public string SubtitleLanguage { get; set; }   // normalized code, or "off"
        /// Subtitle speed fixes learned for this show, by the playing file's release (ReleaseInfo.Key, "rarbg|bluray").
        public Dictionary<string, RememberedTiming> SubtitleTimings { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public sealed class RememberedTiming
    {
        public double Scale { get; set; }
        public long OffsetMs { get; set; }
    }

    // ---- Status DTO ----

    public sealed class SourceStatus
    {
        public string Identity { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Quality { get; set; }
        public long SizeBytes { get; set; }
        public double DownloadBytesPerSecond { get; set; }
        public int Peers { get; set; }
    }

    public sealed class HealthStatus
    {
        public string Action { get; set; }
        public string Reason { get; set; }
        public double PlaybackRatio { get; set; }
        public double? ThroughputBytesPerSecond { get; set; }
    }

    public sealed class TrackStatus
    {
        public IReadOnlyList<TrackInfo> Audio { get; set; } = Array.Empty<TrackInfo>();
        public int? SelectedAudio { get; set; }
        public IReadOnlyList<TrackInfo> EmbeddedSubtitles { get; set; } = Array.Empty<TrackInfo>();
        public SubtitleDecision Subtitle { get; set; }
        public int SubtitleOffsetMs { get; set; }
        /// The external subtitle's timing against the file (PlaybackSession subtitle sync); identity when untouched.
        public SubtitleTiming SubtitleSync { get; set; } = SubtitleTiming.Identity;
    }

    public sealed class SessionStatus
    {
        public string Id { get; set; }
        public string MetaId { get; set; }
        public string Name { get; set; }
        public string VideoId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string VideoTitle { get; set; }
        public string Mode { get; set; }
        public bool StopAfterCurrent { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public bool MarkedWatched { get; set; }
        public bool UserPaused { get; set; }
        public int RecoverySwitches { get; set; }
        public int RecoveryFailures { get; set; }
        public string RecoveryError { get; set; }
        public string HistoryError { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string EndReason { get; set; }

        public static SessionStatus From(SessionRecord s) => s == null ? null : new SessionStatus
        {
            Id = s.Id, MetaId = s.MetaId, Name = s.Name, VideoId = s.VideoId, Season = s.Season, Episode = s.Episode,
            VideoTitle = s.VideoTitle, Mode = PlaybackModes.ToWire(s.Mode), StopAfterCurrent = s.StopAfterCurrent,
            StartedAt = s.StartedAt, PositionMs = s.LastPositionMs, DurationMs = s.DurationMs, MarkedWatched = s.MarkedWatched,
            UserPaused = s.UserPaused, RecoverySwitches = s.Recovery?.Switches ?? 0, RecoveryFailures = s.Recovery?.Failures ?? 0, RecoveryError = s.Recovery?.LastError,
            HistoryError = s.HistoryError, EndedAt = s.EndedAt, EndReason = s.EndReason,
        };
    }

    /// Snapshot returned by PlaybackSession.GetStatus(); the remote UI and agent API both read this.
    public sealed class PlaybackStatus
    {
        /// idle | resolving | starting | playing | paused | buffering | recovering | ended
        public string Phase { get; set; }
        public bool Active { get; set; }
        public long Revision { get; set; }
        public PlayerState PlayerState { get; set; }
        public SessionStatus Session { get; set; }
        public SessionStatus LastSession { get; set; }
        public SourceStatus Source { get; set; }
        public HealthStatus Health { get; set; }
        public TrackStatus Tracks { get; set; }
        public QueueStatus Queue { get; set; }
        /// What is being started right now (Phase starting/recovering), if anything.
        public string Pending { get; set; }
        /// Where a start stands (Phase starting/recovering): sources (asking the add-ons), connecting (racing
        /// candidates for metadata, peers and the first pieces), opening (the player is preparing the winner).
        public string Step { get; set; }
        /// Ranked sources found, and which try this is (the first race is try 1; later races follow failures).
        public int StepSources { get; set; }
        public int StepTry { get; set; }
        /// Candidates being opened in parallel right now (the race), while Step is connecting.
        public int StepRacing { get; set; }
        public string LastError { get; set; }
    }
}
