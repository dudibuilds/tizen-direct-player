using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Account
{
    /// Playback progress → Stremio library (parity E1-E5).
    ///
    /// Caller contract (the player owns sample credibility, parity I1):
    /// - A sample is credible when duration > 0, position >= 0, duration is within 5 s of the known
    ///   duration, and position is not more than 3 s behind the previous sample.
    /// - Call ProgressAsync with credible samples only (every sample is fine; this sink saves at most
    ///   once per 60 s of playhead progress).
    /// - Call CompletedAsync (after the ProgressAsync call for that sample, so the sink knows the
    ///   position and duration) once at least 2 credible samples were seen and position/duration >= 0.9
    ///   (CompletionThreshold; see ShouldComplete). The sink marks watched once per session.
    /// - Call StoppedAsync at finalize with markedWatched=true if completion already fired. Pass
    ///   durationMs = 0 when fewer than 2 credible samples were seen, which skips the final save.
    ///
    /// Writes go through the library's single ordered outbox, so they never race and survive offline.
    /// Methods never throw for sync failures; see LastError and PendingWrites.
    public sealed class StremioProgressSink : IProgressSink
    {
        public const double CompletionThreshold = 0.9;
        public const long SaveIntervalMs = 60000;

        readonly StremioLibrary library;
        readonly Func<DateTimeOffset> now;
        readonly object sync = new object();
        Session session;

        sealed class Session
        {
            public string MetaId;
            public string VideoId;
            public long LastSavedMs;
            public long LastPositionMs;
            public long DurationMs;
            public bool MarkedWatched;
        }

        public StremioProgressSink(StremioLibrary library, Func<DateTimeOffset> now = null)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public string LastError => library.LastSyncError;
        public int PendingWrites => library.PendingWrites;

        /// E3 rule for callers: at least 2 credible samples and position/duration >= 0.9, once.
        public static bool ShouldComplete(long positionMs, long durationMs, int credibleSamples, bool alreadyMarked)
        {
            return !alreadyMarked && credibleSamples >= 2 && durationMs > 0 && (double)positionMs / durationMs >= CompletionThreshold;
        }

        /// E1: noNotif=false, video_id, timeOffset=max(0,resume); duration/timeWatched kept for the same video.
        public Task PlaybackStartedAsync(MetaDetail meta, VideoInfo video, long resumeMs, CancellationToken token)
        {
            var videoId = VideoId(meta, video);
            lock (sync) session = new Session { MetaId = meta.Summary.Id, VideoId = videoId, LastSavedMs = resumeMs, LastPositionMs = resumeMs };
            var op = LibraryOps.Create(LibraryOps.Started, meta, false, now());
            op["videoId"] = videoId;
            op["resumeMs"] = Math.Max(0, resumeMs);
            return library.EnqueueAsync(op, token);
        }

        /// E2: saves when the playhead is at least 60 s past the last save.
        public Task ProgressAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token)
        {
            var videoId = VideoId(meta, video);
            lock (sync)
            {
                var s = Current(meta, videoId);
                s.LastPositionMs = positionMs;
                if (durationMs > 0) s.DurationMs = durationMs;
                if (positionMs - s.LastSavedMs < SaveIntervalMs) return Task.CompletedTask;
                s.LastSavedMs = positionMs;
            }
            return SaveAsync(meta, videoId, positionMs, durationMs, token);
        }

        /// Not in the original (it only scrobbled Trakt on pause). Saved here because a TV is often
        /// switched off while paused.
        public Task PausedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token)
        {
            var videoId = VideoId(meta, video);
            lock (sync)
            {
                var s = Current(meta, videoId);
                s.LastPositionMs = positionMs;
                if (durationMs > 0) s.DurationMs = durationMs;
                if (s.MarkedWatched || durationMs <= 0) return Task.CompletedTask;
                s.LastSavedMs = positionMs;
            }
            return SaveAsync(meta, videoId, positionMs, durationMs, token);
        }

        /// E3: timeOffset=0 and mark watched (bitfield untouched if the episode is missing from meta). Once per session.
        public Task CompletedAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            var videoId = VideoId(meta, video);
            long position, duration;
            lock (sync)
            {
                var s = Current(meta, videoId);
                if (s.MarkedWatched) return Task.CompletedTask;
                s.MarkedWatched = true;
                s.LastSavedMs = s.LastPositionMs;
                position = s.LastPositionMs;
                duration = s.DurationMs;
            }
            var op = LibraryOps.Create(LibraryOps.Completed, meta, true, now());
            op["videoId"] = videoId;
            op["positionMs"] = position;
            op["durationMs"] = duration;
            return library.EnqueueAsync(op, token);
        }

        /// E4: one final save unless already marked watched or the duration is unknown.
        public Task StoppedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, bool markedWatched, CancellationToken token)
        {
            var videoId = VideoId(meta, video);
            bool marked;
            lock (sync)
            {
                var s = Current(meta, videoId);
                marked = markedWatched || s.MarkedWatched;
                session = null;
            }
            if (marked || durationMs <= 0) return Task.CompletedTask;
            return SaveAsync(meta, videoId, positionMs, durationMs, token);
        }

        Task SaveAsync(MetaDetail meta, string videoId, long positionMs, long durationMs, CancellationToken token)
        {
            var op = LibraryOps.Create(LibraryOps.Progress, meta, false, now());
            op["videoId"] = videoId;
            op["positionMs"] = positionMs;
            op["durationMs"] = durationMs;
            return library.EnqueueAsync(op, token);
        }

        Session Current(MetaDetail meta, string videoId)
        {
            if (session == null || session.MetaId != meta.Summary.Id || session.VideoId != videoId)
                session = new Session { MetaId = meta.Summary.Id, VideoId = videoId };
            return session;
        }

        internal static string VideoId(MetaDetail meta, VideoInfo video)
        {
            if (meta?.Summary?.Id == null) throw new ArgumentException("meta.Summary.Id is required", nameof(meta));
            return video?.Id ?? meta.Summary.Id;
        }
    }
}
