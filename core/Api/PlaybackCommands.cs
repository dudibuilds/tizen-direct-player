// The player command surface shared by the remote UI and the agent API.
// The playback session engine implements IPlaybackCommands; the API router only validates and forwards.
// Result classes serialize (camelCase, nulls kept) to the JSON shapes of samsung-stremio-spotify-mcp's stremio_* tools.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Api
{
    public interface IPlaybackCommands
    {
        /// Resolves the title (query text or tt id, may contain "S2E5"), applies the confirmation and resume rules
        /// (parity H1-H3), and starts playback. Returns status ambiguous | confirmation_required | caught_up | playing.
        Task<PlayOutcome> PlayAsync(PlayRequest request, CancellationToken token);
        Task<ControlResult> PauseAsync(CancellationToken token);
        Task<ControlResult> ResumeAsync(CancellationToken token);
        Task<ControlResult> StopAsync(CancellationToken token);
        Task<ControlResult> SeekAsync(double positionSeconds, CancellationToken token);
        /// enable_binge / disable_binge (parity H4).
        Task<ControlResult> SetBingeAsync(bool enabled, CancellationToken token);
        /// Stop when the current item ends instead of advancing (parity H6).
        Task<ControlResult> SetStopAfterCurrentAsync(bool enabled, CancellationToken token);
        Task<ControlResult> SelectAudioAsync(int index, CancellationToken token);
        /// index -1 turns subtitles off.
        Task<ControlResult> SelectSubtitleAsync(int index, CancellationToken token);
        Task<ControlResult> SetSubtitleSizeAsync(int size, CancellationToken token);
        Task<ControlResult> SetSubtitleOffsetAsync(int offsetMs, CancellationToken token);
        /// Replaces the saved queue (status ready). Returns status ambiguous with itemIndex when an item is unclear.
        Task<QueueResult> SetQueueAsync(QueueRequest request, CancellationToken token);
        /// Starts the saved queue from its first item. The router calls SetQueueAsync first when items are given.
        Task<QueueResult> StartQueueAsync(CancellationToken token);
        Task<QueueResult> ClearQueueAsync(CancellationToken token);
        Task<QueueResult> SkipQueueAsync(CancellationToken token);
        Task<PlaybackSnapshot> GetStatusAsync(CancellationToken token);
    }

    public static class PlaybackLimits
    {
        public const int SubtitleSizeMin = 24;          // matches the probe overlay
        public const int SubtitleSizeMax = 72;
        public const int SubtitleOffsetMaxMs = 30000;   // parity F14 clamps external subtitle offsets to ±30 s
        public const double SizeCapGb = 6.5;            // parity F10
    }

    public sealed class PlayRequest
    {
        public string Query { get; set; }           // title text or tt id
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public bool Confirmed { get; set; }
        public string Mode { get; set; } = "single"; // single | binge
        public string Quality { get; set; } = "auto"; // auto | 4k | 1080p
        public double? MaxSizeGb { get; set; }      // only lowers the 6.5 GB cap
        public double? PositionSeconds { get; set; } // explicit start position; overrides resume
    }

    public sealed class QueueItemRequest
    {
        public string Query { get; set; }           // show title or tt id; may contain S/E
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

    public sealed class QueueRequest
    {
        public IReadOnlyList<QueueItemRequest> Items { get; set; } = Array.Empty<QueueItemRequest>();
        public string Quality { get; set; } = "auto";
        public double? MaxSizeGb { get; set; }
    }

    public sealed class PlayOutcome
    {
        public string Status { get; set; }          // playing | starting | confirmation_required | ambiguous | caught_up
        public string Series { get; set; }
        public string Proposal { get; set; }        // "Resume S2E5 at 12:04?"
        public VideoInfo Video { get; set; }
        public long ResumeMs { get; set; }
        public string Mode { get; set; }
        public IReadOnlyList<SearchHit> Candidates { get; set; }
        public VideoInfo NextScheduled { get; set; }
        // Set when playback started (old playResolved result: selected, fallbacksTried, warning, tv.state).
        public StreamChoice Selected { get; set; }
        public IReadOnlyList<SourceFailure> FallbacksTried { get; set; }
        public string Warning { get; set; }
        public PlayerState? State { get; set; }
        public PlaybackSession Session { get; set; }
    }

    public sealed class SourceFailure
    {
        public StreamChoice Stream { get; set; }
        public string Error { get; set; }
    }

    public sealed class ControlResult
    {
        public bool Ok { get; set; } = true;
        public string Action { get; set; }
        public PlayerState State { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public string Mode { get; set; }
        public PlaybackSession Session { get; set; }
    }

    public sealed class PlaybackSession
    {
        public string Id { get; set; }              // meta id
        public string Name { get; set; }
        public string VideoId { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string Mode { get; set; }            // single | binge | queue
        public bool StopAfterCurrent { get; set; }
        public PlayerState State { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string EndReason { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public bool MarkedWatched { get; set; }
        public string Quality { get; set; }
        public double? MaxSizeGb { get; set; }
        public StreamCandidate Stream { get; set; }
        public IReadOnlyList<TrackInfo> AudioTracks { get; set; } = Array.Empty<TrackInfo>();
        public IReadOnlyList<TrackInfo> SubtitleTracks { get; set; } = Array.Empty<TrackInfo>();
        public int SelectedAudio { get; set; }
        public int SelectedSubtitle { get; set; } = -1;
        public int SubtitleSize { get; set; } = 40;
        public int SubtitleOffsetMs { get; set; }
        /// How the external subtitle was fitted to the file; null when it is used as it came.
        public ApiSubtitleSync SubtitleSync { get; set; }
        public string HistoryError { get; set; }
        public string TraktError { get; set; }
        public RecoveryInfo Recovery { get; set; } = new RecoveryInfo();
    }

    public sealed class ApiSubtitleSync
    {
        public double Scale { get; set; }
        public long OffsetMs { get; set; }
        public string Source { get; set; }       // reference | offset | remembered
        public string Speed { get; set; }        // "25 → 23.976" or null
        public string Label { get; set; }
    }

    public sealed class RecoveryInfo
    {
        public int Switches { get; set; }
        public string LastError { get; set; }
    }

    public sealed class PlaybackSnapshot
    {
        public bool Active { get; set; }
        public PlaybackSession Session { get; set; }
        public PlaybackSession LastSession { get; set; }
        public QueueState Queue { get; set; }
    }

    public sealed class QueueEntry
    {
        public string MetaId { get; set; }
        public MediaType MediaType { get; set; }
        public string Name { get; set; }
        public string VideoId { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
        public string Title { get; set; }
        public int Position { get; set; }           // 1-based
        public bool Current { get; set; }
    }

    public sealed class QueueState
    {
        public string Id { get; set; }              // queue:<ts>
        public string Status { get; set; }          // ready | starting | playing | paused | completed | stopped | interrupted | error
        public int CurrentIndex { get; set; }
        public int CurrentPosition => CurrentIndex + 1;
        public int Total => Items?.Count ?? 0;
        public string Quality { get; set; }
        public double MaxSizeGb { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string LastError { get; set; }
        public string LastStopReason { get; set; }
        public IReadOnlyList<QueueEntry> Items { get; set; } = Array.Empty<QueueEntry>();
    }

    public sealed class QueueResult
    {
        public bool Ok { get; set; } = true;
        public string Action { get; set; }
        // null for set/status/clear; ambiguous; or for start/skip the started item's play status
        // (playing | starting) or queue_complete when skip ran past the last item.
        public string Status { get; set; }
        public int? ItemIndex { get; set; }
        public string Query { get; set; }
        public IReadOnlyList<SearchHit> Candidates { get; set; }
        public bool? Skipped { get; set; }
        // start/skip: the old result merged the play result into the queue result; these carry its fields.
        public string Series { get; set; }
        public VideoInfo Video { get; set; }
        public long? ResumeMs { get; set; }
        public string Mode { get; set; }
        public StreamChoice Selected { get; set; }
        public IReadOnlyList<SourceFailure> FallbacksTried { get; set; }
        public string Warning { get; set; }
        public PlayerState? State { get; set; }
        public QueueState Queue { get; set; }
        public PlaybackSession Session { get; set; }
    }
}
