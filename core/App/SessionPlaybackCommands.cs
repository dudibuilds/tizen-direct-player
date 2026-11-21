using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Api;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using ApiPlayRequest = Ru7100.Core.Api.PlayRequest;
using ApiQueueItemRequest = Ru7100.Core.Api.QueueItemRequest;
using ApiQueueResult = Ru7100.Core.Api.QueueResult;
using ApiQueueState = Ru7100.Core.Api.QueueState;
using ApiSession = Ru7100.Core.Api.PlaybackSession;
using ApiSourceFailure = Ru7100.Core.Api.SourceFailure;
using EnginePlayRequest = Ru7100.Core.Playback.PlayRequest;
using EnginePlaybackMode = Ru7100.Core.Playback.PlaybackMode;
using EngineQueueItemRequest = Ru7100.Core.Playback.QueueItemRequest;
using EngineQueueResult = Ru7100.Core.Playback.QueueResult;
using EngineSession = Ru7100.Core.Playback.PlaybackSession;

namespace Ru7100.Core.App
{
    /// The agent API's player commands (core/Api IPlaybackCommands) over the same PlaybackSession the remote UI uses.
    /// Results keep the old stremio_* shapes. Expected failures become ApiException codes:
    /// no_active_playback / no_queue / no_active_queue (409), not_found (404), playback_failed (502).
    /// Subtitle size is a PlayerSettings value (shared with the UI); audio and subtitle choices are also written to
    /// PlayerSettings' per-show memory so the overlay does not undo an agent's choice on the next episode.
    public sealed class SessionPlaybackCommands : IPlaybackCommands
    {
        private readonly EngineSession session;
        private readonly PlayerSettings settings;

        public SessionPlaybackCommands(EngineSession session, PlayerSettings settings)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async Task<PlayOutcome> PlayAsync(ApiPlayRequest request, CancellationToken token)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var parsed = TitleResolver.ParseEpisode(request.Query);
            var query = string.IsNullOrWhiteSpace(parsed.Query) ? request.Query : parsed.Query;
            var result = await session.PlayAsync(new EnginePlayRequest
            {
                Query = query,
                Season = request.Season ?? parsed.Season,
                Episode = request.Episode ?? parsed.Episode,
                Confirmed = request.Confirmed,
                Mode = request.Mode == "binge" ? EnginePlaybackMode.Binge : EnginePlaybackMode.Single,
                Quality = string.IsNullOrEmpty(request.Quality) ? "auto" : request.Quality,
                MaxSizeGb = request.MaxSizeGb,
                StartMs = request.PositionSeconds.HasValue ? (long?)(long)(request.PositionSeconds.Value * 1000) : null,
            }, token).ConfigureAwait(false);
            return ToOutcome(result, query);
        }

        public Task<ControlResult> PauseAsync(CancellationToken token) => ControlAsync("pause", t => session.PauseAsync(t), token);
        public Task<ControlResult> ResumeAsync(CancellationToken token) => ControlAsync("resume", t => session.ResumeAsync(t), token);

        public async Task<ControlResult> StopAsync(CancellationToken token)
        {
            await session.StopAsync(token).ConfigureAwait(false);
            var status = session.GetStatus();
            var last = status.LastSession;
            return new ControlResult
            {
                Action = "stop", State = PlayerState.Stopped, PositionMs = last?.PositionMs ?? 0, DurationMs = last?.DurationMs ?? 0,
                Mode = last?.Mode, Session = MapSession(status, last, false),
            };
        }

        public Task<ControlResult> SeekAsync(double positionSeconds, CancellationToken token) =>
            ControlAsync("seek", t => session.SeekAsync((long)(Math.Max(0, positionSeconds) * 1000), t), token);

        public Task<ControlResult> SetBingeAsync(bool enabled, CancellationToken token) =>
            ControlAsync(enabled ? "enable_binge" : "disable_binge", t => session.SetBingeAsync(enabled, t), token);

        public Task<ControlResult> SetStopAfterCurrentAsync(bool enabled, CancellationToken token) =>
            ControlAsync("stop_after_current", t => session.StopAfterCurrentAsync(enabled, t), token);

        public Task<ControlResult> SelectAudioAsync(int index, CancellationToken token) =>
            ControlAsync("select_audio", async t =>
            {
                var status = session.GetStatus();
                var tracks = status.Tracks?.Audio ?? Array.Empty<TrackInfo>();
                var track = tracks.FirstOrDefault(x => x.Index == index)
                    ?? throw ApiException.Invalid("No audio track " + index + "; the session has " + tracks.Count);
                await session.SelectAudioAsync(index, t).ConfigureAwait(false);
                settings.RememberTracks(status.Session?.MetaId, PlayerSettings.Normalize(track.Language), null);
            }, token);

        public Task<ControlResult> SelectSubtitleAsync(int index, CancellationToken token) =>
            ControlAsync("select_subtitle", async t =>
            {
                var status = session.GetStatus();
                SubtitleDecision decision;
                string remembered;
                if (index < 0)
                {
                    decision = new SubtitleDecision { Kind = SubtitleKind.None };
                    remembered = "off";
                }
                else
                {
                    var menu = PlaybackMapping.SubtitleMenu(status, session.ExternalSubtitles);
                    if (index >= menu.Count) throw ApiException.Invalid("No subtitle track " + index + "; the session has " + menu.Count);
                    decision = menu[index].Decision;
                    remembered = PlayerSettings.Normalize(menu[index].Language);
                }
                await session.SelectSubtitleAsync(decision, t).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(remembered))
                    settings.RememberTracks(status.Session?.MetaId, null, remembered);
            }, token);

        public Task<ControlResult> SetSubtitleSizeAsync(int size, CancellationToken token)
        {
            settings.UpdateSubtitleStyle(style => style.Size = size);
            return Task.FromResult(Snapshot("subtitle_size"));
        }

        public Task<ControlResult> SetSubtitleOffsetAsync(int offsetMs, CancellationToken token)
        {
            session.SetSubtitleOffset(offsetMs);
            return Task.FromResult(Snapshot("subtitle_offset"));
        }

        public async Task<ApiQueueResult> SetQueueAsync(QueueRequest request, CancellationToken token)
        {
            var items = Items(request);
            var result = await session.QueueSetAsync(items, request.Quality, request.MaxSizeGb, token).ConfigureAwait(false);
            if (result.Status == PlayStatus.Ambiguous)
                return Ambiguous("set", result, request);
            if (result.Status == "error")
                throw ApiException.Invalid(result.Error ?? "The queue could not be set.");
            return new ApiQueueResult { Action = "set", Queue = MapQueue(result.Queue) };
        }

        public async Task<ApiQueueResult> StartQueueAsync(CancellationToken token)
        {
            await session.InitializeAsync(token).ConfigureAwait(false);
            var queue = session.GetQueue();
            if (queue == null || queue.Items.Count == 0)
                throw ApiException.Conflict("no_queue", "No Stremio queue. Call queue set first.");
            var result = await session.QueueStartAsync(null, null, null, token).ConfigureAwait(false);
            return Started("start", result, false);
        }

        public async Task<ApiQueueResult> ClearQueueAsync(CancellationToken token)
        {
            await session.QueueClearAsync(token).ConfigureAwait(false);
            return new ApiQueueResult { Action = "clear", Queue = null };
        }

        public async Task<ApiQueueResult> SkipQueueAsync(CancellationToken token)
        {
            var queue = session.GetQueue();
            if (queue == null || queue.Status != QueueStatuses.Playing)
                throw ApiException.Conflict("no_active_queue", "No active Stremio queue");
            var result = await session.QueueSkipAsync(token).ConfigureAwait(false);
            if (result.Status == "error")
                throw ApiException.Conflict("no_active_queue", result.Error ?? "No active Stremio queue");
            if (result.Status == "queue_complete")
                return new ApiQueueResult { Action = "skip", Status = "queue_complete", Skipped = true, Queue = MapQueue(result.Queue) };
            return Started("skip", result, true);
        }

        public Task<PlaybackSnapshot> GetStatusAsync(CancellationToken token)
        {
            var status = session.GetStatus();
            return Task.FromResult(new PlaybackSnapshot
            {
                Active = status.Active,
                Session = status.Active ? MapSession(status, status.Session, true) : null,
                LastSession = status.Active ? null : MapSession(status, status.LastSession, false),
                Queue = MapQueue(status.Queue),
            });
        }

        // ---------------- helpers ----------------

        private async Task<ControlResult> ControlAsync(string action, Func<CancellationToken, Task> apply, CancellationToken token)
        {
            if (!session.GetStatus().Active)
                throw ApiException.Conflict("no_active_playback", "No active Stremio playback");
            try { await apply(token).ConfigureAwait(false); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("No active playback", StringComparison.Ordinal))
            {
                throw ApiException.Conflict("no_active_playback", "No active Stremio playback");
            }
            return Snapshot(action);
        }

        private ControlResult Snapshot(string action)
        {
            var status = session.GetStatus();
            var current = status.Session;
            return new ControlResult
            {
                Action = action,
                State = status.Active ? (current?.UserPaused == true ? PlayerState.Paused : status.PlayerState) : PlayerState.Stopped,
                PositionMs = current?.PositionMs ?? 0,
                DurationMs = current?.DurationMs ?? 0,
                Mode = current?.Mode,
                Session = status.Active ? MapSession(status, current, true) : null,
            };
        }

        private PlayOutcome ToOutcome(PlayResult result, string query)
        {
            switch (result.Status)
            {
                case PlayStatus.Ambiguous:
                    return new PlayOutcome { Status = "ambiguous", Candidates = Hits(result.Candidates, query) };
                case PlayStatus.NotFound:
                    throw ApiException.NotFound(result.Error ?? "No title matched the request.");
                case PlayStatus.Failed:
                    throw new ApiException("playback_failed", result.Error ?? "Playback failed.", 502);
                case PlayStatus.Superseded:
                    return new PlayOutcome { Status = "superseded", Mode = PlaybackModes.ToWire(result.Mode) };
            }
            var series = session.CurrentMeta?.Summary?.Type == MediaType.Series || result.Video?.Season != null ? result.Name : null;
            var outcome = new PlayOutcome
            {
                Status = result.Status,
                Series = series,
                Proposal = result.Proposal,
                Video = ToVideo(result.Video),
                ResumeMs = result.ResumeMs,
                Mode = PlaybackModes.ToWire(result.Mode),
                NextScheduled = ToVideo(result.NextScheduled),
            };
            if (result.Status == PlayStatus.Playing)
            {
                var status = session.GetStatus();
                outcome.Selected = Choice(session.CurrentCandidate);
                outcome.FallbacksTried = Failures(result.FallbacksTried);
                outcome.Warning = result.Warning;
                outcome.State = status.PlayerState;
                outcome.Session = MapSession(status, status.Session, true);
            }
            return outcome;
        }

        private ApiQueueResult Started(string action, EngineQueueResult result, bool skipped)
        {
            if (result.Status == "error" && result.Play == null)
                throw ApiException.Conflict("no_queue", result.Error ?? "No Stremio queue. Call queue set first.");
            var play = result.Play;
            if (play != null && play.Status == PlayStatus.Failed)
                throw new ApiException("playback_failed", play.Error ?? result.Error ?? "Playback failed.", 502);
            var status = session.GetStatus();
            var queue = MapQueue(result.Queue ?? status.Queue);
            var item = queue != null && queue.CurrentIndex < queue.Items.Count ? queue.Items[queue.CurrentIndex] : null;
            return new ApiQueueResult
            {
                Action = action,
                Status = play?.Status == PlayStatus.Superseded ? "superseded" : "playing",
                Skipped = skipped ? true : (bool?)null,
                Series = item?.Name ?? play?.Name,
                Video = item == null ? ToVideo(play?.Video) : new VideoInfo { Id = item.VideoId, Season = item.Season, Episode = item.Episode, Title = item.Title },
                ResumeMs = play?.ResumeMs ?? 0,
                Mode = "queue",
                Selected = Choice(session.CurrentCandidate),
                FallbacksTried = Failures(play?.FallbacksTried),
                Warning = play?.Warning,
                State = status.Active ? status.PlayerState : (PlayerState?)null,
                Queue = queue,
                Session = status.Active ? MapSession(status, status.Session, true) : null,
            };
        }

        private ApiQueueResult Ambiguous(string action, EngineQueueResult result, QueueRequest request)
        {
            var index = result.ItemIndex ?? 0;
            var query = request.Items != null && index < request.Items.Count ? request.Items[index].Query : null;
            return new ApiQueueResult { Action = action, Status = "ambiguous", ItemIndex = index, Query = query, Candidates = Hits(result.Candidates, query) };
        }

        private static IReadOnlyList<EngineQueueItemRequest> Items(QueueRequest request)
        {
            var items = new List<EngineQueueItemRequest>();
            foreach (var item in request?.Items ?? Array.Empty<ApiQueueItemRequest>())
            {
                var parsed = TitleResolver.ParseEpisode(item.Query);
                items.Add(new EngineQueueItemRequest
                {
                    Query = string.IsNullOrWhiteSpace(parsed.Query) ? item.Query : parsed.Query,
                    Season = item.Season ?? parsed.Season,
                    Episode = item.Episode ?? parsed.Episode,
                });
            }
            return items;
        }

        private ApiSession MapSession(PlaybackStatus status, SessionStatus s, bool live)
        {
            if (s == null) return null;
            var menu = live ? PlaybackMapping.SubtitleMenu(status, session.ExternalSubtitles) : Array.Empty<SubtitleMenuEntry>();
            var tracks = live ? status.Tracks : null;
            var candidate = live ? session.CurrentCandidate : null;
            return new ApiSession
            {
                Id = s.MetaId,
                Name = s.Name,
                VideoId = s.VideoId,
                Season = s.Season,
                Episode = s.Episode,
                Mode = s.Mode,
                StopAfterCurrent = s.StopAfterCurrent,
                State = live ? (s.UserPaused ? PlayerState.Paused : status.PlayerState) : PlayerState.Stopped,
                StartedAt = s.StartedAt,
                EndedAt = s.EndedAt,
                EndReason = s.EndReason,
                PositionMs = s.PositionMs,
                DurationMs = s.DurationMs,
                MarkedWatched = s.MarkedWatched,
                Stream = candidate,
                AudioTracks = tracks?.Audio ?? Array.Empty<TrackInfo>(),
                SubtitleTracks = menu.Select((e, i) => new TrackInfo { Index = i, Language = e.Language, Label = e.Label + (e.External ? " (external)" : "") }).ToList(),
                SelectedAudio = tracks?.SelectedAudio ?? 0,
                SelectedSubtitle = live ? PlaybackMapping.SelectedSubtitleIndex(menu, tracks?.Subtitle) : -1,
                SubtitleSize = settings.SubtitleStyle.Size,
                SubtitleOffsetMs = status.Tracks?.SubtitleOffsetMs ?? 0,
                SubtitleSync = SyncOf(status.Tracks?.SubtitleSync ?? SubtitleTiming.Identity),
                HistoryError = s.HistoryError,
                Recovery = new RecoveryInfo { Switches = s.RecoverySwitches, LastError = s.RecoveryError },
            };
        }

        private static ApiSubtitleSync SyncOf(SubtitleTiming timing)
        {
            var label = PlaybackMapping.SyncLabel(timing);
            return label == null ? null : new ApiSubtitleSync
            {
                Scale = Math.Round(timing.Scale, 6), OffsetMs = timing.OffsetMs, Source = timing.Source, Speed = timing.SpeedName, Label = label,
            };
        }

        public static ApiQueueState MapQueue(QueueStatus queue)
        {
            if (queue == null) return null;
            var items = (queue.Items ?? new List<QueueItem>()).Select((item, i) => new QueueEntry
            {
                MetaId = item.MetaId, MediaType = item.MediaType, Name = item.Name, VideoId = item.VideoId, Season = item.Season,
                Episode = item.Episode, Title = item.Title, Position = i + 1, Current = i == queue.CurrentIndex,
            }).ToList();
            return new ApiQueueState
            {
                Id = queue.Id, Status = queue.Status, CurrentIndex = queue.CurrentIndex, Quality = queue.Quality,
                MaxSizeGb = queue.MaxSizeGb ?? PlaybackLimits.SizeCapGb, CreatedAt = queue.CreatedAt, CompletedAt = queue.CompletedAt,
                LastError = queue.LastError, LastStopReason = queue.LastStopReason, Items = items,
            };
        }

        public static StreamChoice Choice(StreamCandidate c) => c == null ? null : new StreamChoice
        {
            Addon = c.AddonId, Name = c.Name, Title = c.Title, Filename = c.Filename, Quality = c.Quality, Seeders = c.Seeders,
            SizeBytes = c.SizeBytes, Codecs = new CodecInfo { Video = c.VideoCodec, Audio = c.AudioCodec }, Score = c.Score,
        };

        private static IReadOnlyList<ApiSourceFailure> Failures(IEnumerable<Playback.SourceFailure> failures) =>
            (failures ?? Enumerable.Empty<Playback.SourceFailure>()).Select(f => new ApiSourceFailure
            {
                Stream = new StreamChoice { Name = f.Name },
                Error = f.Error,
            }).ToList();

        private static IReadOnlyList<SearchHit> Hits(IReadOnlyList<MetaSummary> candidates, string query) =>
            (candidates ?? Array.Empty<MetaSummary>()).Select(m => new SearchHit
            {
                Id = m.Id, Type = m.Type, Name = m.Name, ReleaseInfo = m.ReleaseInfo, Poster = m.Poster,
                MatchScore = Math.Round(TitleResolver.TitleSimilarity(query ?? "", m.Name ?? ""), 3),
            }).ToList();

        private static VideoInfo ToVideo(VideoRef video) => video == null ? null : new VideoInfo
        {
            Id = video.Id, Season = video.Season ?? 0, Episode = video.Episode ?? 0, Title = video.Title, Released = video.Released,
        };
    }
}
