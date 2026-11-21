using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using EnginePlayRequest = Ru7100.Core.Playback.PlayRequest;
using EnginePlaybackMode = Ru7100.Core.Playback.PlaybackMode;
using UiPlayRequest = Ru7100.Core.Ui.PlayRequest;
using UiPlaybackMode = Ru7100.Core.Ui.PlaybackMode;

namespace Ru7100.Core.App
{
    /// The remote UI's player and queue (core/Ui IPlayerControls + IQueueControls) over the one PlaybackSession that
    /// the agent API also drives. Snapshot never blocks: it reads the engine's status (lock-free reads plus direct
    /// player properties) and the last subtitle cue. The void commands run in the background; failures are logged.
    public sealed class SessionPlayerControls : IPlayerControls, IQueueControls, ICreditsControls, IDisposable
    {
        private readonly PlaybackSession session;
        private readonly Func<DateTimeOffset> now;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private string cueText;
        private DateTimeOffset cueAt;
        private long cueDurationMs;
        private bool cueEmbedded;
        private string cueVideo;
        private MetaDetail pendingMeta;
        private VideoInfo pendingVideo;
        private int lastAudio = -1;
        private string lastAudioVideo;

        public SessionPlayerControls(PlaybackSession session, PlayerSettings settings, Func<DateTimeOffset> now = null, Action<string> log = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            this.log = log;
            session.SubtitleCue += OnCue;
        }

        public PlayerSettings Settings { get; }

        public PlayerSnapshot Snapshot
        {
            get
            {
                var status = session.GetStatus();
                var at = now();
                var active = status.Active;
                var sessionActive = PlaybackMapping.IsSessionActive(status, at);
                var meta = session.CurrentMeta;
                var video = session.CurrentVideo;
                lock (gate)
                {
                    if (meta == null && sessionActive)
                    {
                        meta = pendingMeta;
                        video = pendingVideo;
                    }
                }
                var current = status.Session;
                var snapshot = new PlayerSnapshot
                {
                    SessionActive = sessionActive,
                    State = MapState(status, sessionActive),
                    PositionMs = current?.PositionMs ?? 0,
                    DurationMs = current?.DurationMs ?? 0,
                    Meta = meta,
                    Video = meta?.Summary?.Type == MediaType.Series ? video : null,
                    Title = meta?.Summary?.Name ?? current?.Name,
                    Binge = current != null && (current.Mode == "binge" || current.Mode == "queue"),
                    StopAfterCurrent = current?.StopAfterCurrent ?? false,
                    SubtitleOffsetMs = status.Tracks?.SubtitleOffsetMs ?? 0,
                    SubtitleSync = PlaybackMapping.SyncLabel(status.Tracks?.SubtitleSync ?? SubtitleTiming.Identity),
                    StatusText = StatusText(status, sessionActive),
                    StartStep = StartStep(status, sessionActive),
                    Error = sessionActive ? null : status.LastError,
                };
                if (snapshot.Video != null)
                    snapshot.EpisodeLabel = UiFormat.Episode(snapshot.Video) + (string.IsNullOrEmpty(snapshot.Video.Title) ? "" : " · " + snapshot.Video.Title);
                if (active)
                {
                    var tracks = status.Tracks;
                    snapshot.AudioTracks = tracks?.Audio ?? Array.Empty<TrackInfo>();
                    lock (gate)
                        snapshot.SelectedAudio = tracks?.SelectedAudio ?? (lastAudioVideo == current?.VideoId ? lastAudio : -1);
                    var menu = PlaybackMapping.SubtitleMenu(status, session.ExternalSubtitles);
                    snapshot.Subtitles = menu.Select(e => new SubtitleOption { Id = e.Id, Label = e.Label, Language = e.Language, External = e.External }).ToList();
                    snapshot.SelectedSubtitleId = PlaybackMapping.SelectedSubtitleId(tracks?.Subtitle);
                    snapshot.SubtitleText = snapshot.SelectedSubtitleId == null ? null : CurrentCue(current?.VideoId, at);
                    snapshot.SourceLabel = PlaybackMapping.SourceLabel(session.CurrentCandidate, status.Source?.SizeBytes ?? 0);
                }
                return snapshot;
            }
        }

        // ---------------- IPlayerControls ----------------

        public async Task PlayAsync(UiPlayRequest request, CancellationToken token)
        {
            if (request?.Meta?.Summary == null)
                throw new ArgumentException("A title is required.", nameof(request));
            var meta = request.Meta;
            var video = meta.Summary.Type == MediaType.Series ? request.Video : null;
            lock (gate)
            {
                pendingMeta = meta;
                pendingVideo = video;
            }
            var result = await session.PlayAsync(new EnginePlayRequest
            {
                MetaId = meta.Summary.Id,
                Type = meta.Summary.Type,
                Season = video?.Season,
                Episode = video?.Episode,
                Confirmed = true,
                Mode = request.Mode == UiPlaybackMode.Binge ? EnginePlaybackMode.Binge : EnginePlaybackMode.Single,
                Quality = string.IsNullOrEmpty(request.Quality) ? "auto" : request.Quality,
                StartMs = Math.Max(0, request.ResumeMs),
                Source = request.Source,
            }, token).ConfigureAwait(false);
            ThrowUnlessStarted(result);
        }

        public void Play() => Fire("resume", t => session.ResumeAsync(t));
        public void Pause() => Fire("pause", t => session.PauseAsync(t));

        public void TogglePause()
        {
            var status = session.GetStatus();
            var paused = status.Session?.UserPaused == true || status.PlayerState == PlayerState.Paused;
            if (paused) Play(); else Pause();
        }

        public void Stop()
        {
            lock (gate)
            {
                pendingMeta = null;
                pendingVideo = null;
            }
            Fire("stop", t => session.StopAsync(t));
        }

        public void SeekTo(long positionMs) => Fire("seek", t => session.SeekAsync(Math.Max(0, positionMs), t));

        public void SelectAudio(int trackIndex)
        {
            lock (gate)
            {
                lastAudio = trackIndex;
                lastAudioVideo = session.CurrentVideo?.Id;
            }
            Fire("audio", t => session.SelectAudioAsync(trackIndex, t));
        }

        public void SelectSubtitle(string subtitleId)
        {
            SubtitleDecision decision;
            if (subtitleId == null)
                decision = new SubtitleDecision { Kind = SubtitleKind.None };
            else
            {
                var entry = PlaybackMapping.SubtitleMenu(session.GetStatus(), session.ExternalSubtitles).FirstOrDefault(e => e.Id == subtitleId);
                if (entry == null)
                {
                    log?.Invoke("subtitle: unknown option " + subtitleId);
                    return;
                }
                decision = entry.Decision;
            }
            lock (gate)
                cueText = null;
            Fire("subtitle", t => session.SelectSubtitleAsync(decision, t));
        }

        public void ResetSubtitleTiming() => session.ResetSubtitleTiming();

        public void SetSubtitleOffset(int offsetMs) =>
            session.SetSubtitleOffset(Math.Max(-PlayerSettings.MaxSubtitleOffsetMs, Math.Min(PlayerSettings.MaxSubtitleOffsetMs, offsetMs)));

        public void SetBinge(bool enabled) => Fire("binge", t => session.SetBingeAsync(enabled, t));

        public void SetStopAfterCurrent(bool enabled) => Fire("stop-after", t => session.StopAfterCurrentAsync(enabled, t));

        public void KeepWatchingCredits() => Fire("keep-watching-credits", t => session.HoldAdvanceAsync(t));

        public async Task SwitchSourceAsync(StreamCandidate candidate, CancellationToken token)
        {
            var result = await session.SwitchSourceAsync(candidate, token).ConfigureAwait(false);
            ThrowUnlessStarted(result);
        }

        public async Task PlayNextAsync(CancellationToken token)
        {
            var status = session.GetStatus();
            if (status.Session?.Mode == "queue")
            {
                var skipped = await session.QueueSkipAsync(token).ConfigureAwait(false);
                if (skipped.Status == "error" || skipped.Status == PlayStatus.Failed)
                    throw new InvalidOperationException(skipped.Error ?? "Could not skip.");
                return;
            }
            var meta = session.CurrentMeta ?? throw new InvalidOperationException("Nothing is playing.");
            var video = session.CurrentVideo;
            if (meta.Summary.Type != MediaType.Series || video == null)
                throw new InvalidOperationException("Only a series has a next episode.");
            var next = PlayResolver.NextReleasedVideoAfter(meta, video, now());
            if (next == null || next.Id == video.Id)
                throw new InvalidOperationException("This is the latest episode.");
            lock (gate)
            {
                pendingMeta = meta;
                pendingVideo = next;
            }
            var result = await session.PlayAsync(new EnginePlayRequest
            {
                MetaId = meta.Summary.Id, Type = meta.Summary.Type, Season = next.Season, Episode = next.Episode, Confirmed = true,
                Mode = status.Session?.Mode == "binge" ? EnginePlaybackMode.Binge : EnginePlaybackMode.Single, StartMs = 0,
            }, token).ConfigureAwait(false);
            ThrowUnlessStarted(result);
        }

        // ---------------- IQueueControls ----------------

        public Task<QueueSnapshot> GetAsync(CancellationToken token) => Task.FromResult(MapQueue(session.GetQueue()));

        public async Task AddAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            if (meta?.Summary == null || video == null)
                throw new ArgumentException("An episode is required.");
            await session.InitializeAsync(token).ConfigureAwait(false);
            await session.QueueAddAsync(new QueueItem
            {
                MetaId = meta.Summary.Id, MediaType = meta.Summary.Type, Name = meta.Summary.Name, VideoId = video.Id,
                Season = video.Season, Episode = video.Episode, Title = video.Title,
            }, token).ConfigureAwait(false);
        }

        public async Task RemoveAtAsync(int index, CancellationToken token)
        {
            var result = await session.QueueRemoveAtAsync(index, token).ConfigureAwait(false);
            if (result.Status == "error")
                throw new InvalidOperationException(result.Error);
        }

        public async Task StartAsync(CancellationToken token)
        {
            var result = await session.QueueStartAsync(null, null, null, token).ConfigureAwait(false);
            if (result.Status == "error" || result.Status == PlayStatus.Failed)
                throw new InvalidOperationException(result.Error ?? result.Play?.Error ?? "The queue could not start.");
        }

        public async Task SkipAsync(CancellationToken token)
        {
            var result = await session.QueueSkipAsync(token).ConfigureAwait(false);
            if (result.Status == "error" || result.Status == PlayStatus.Failed)
                throw new InvalidOperationException(result.Error ?? "Could not skip.");
        }

        public Task ClearAsync(CancellationToken token) => session.QueueClearAsync(token);

        public void Dispose() => session.SubtitleCue -= OnCue;

        // ---------------- mapping ----------------

        public static QueueSnapshot MapQueue(QueueStatus queue)
        {
            if (queue == null || queue.Items == null || queue.Items.Count == 0)
                return new QueueSnapshot { Status = "empty" };
            return new QueueSnapshot
            {
                Status = PlaybackMapping.QueueStatusForUi(queue.Status),
                CurrentIndex = queue.CurrentIndex,
                LastError = queue.LastError,
                Items = queue.Items.Select(i => new QueueItemView
                {
                    MetaId = i.MetaId, Type = i.MediaType, Name = i.Name, VideoId = i.VideoId, Season = i.Season, Episode = i.Episode, Title = i.Title,
                }).ToList(),
            };
        }

        private static PlayerState MapState(PlaybackStatus status, bool sessionActive)
        {
            if (!status.Active)
                return sessionActive ? PlayerState.Preparing : status.PlayerState == PlayerState.Completed ? PlayerState.Completed : PlayerState.Stopped;
            // An active session keeps its real state while the next item or another source starts in the background:
            // the current video is still on screen and the transport must stay usable.
            if (status.Phase == "buffering")
                return PlayerState.Buffering;
            if (status.Session?.UserPaused == true)
                return PlayerState.Paused;
            return status.PlayerState;
        }

        /// Loading-screen stage (PlayerSnapshot.StartStep); 0 while a video is on screen.
        private static int StartStep(PlaybackStatus status, bool sessionActive)
        {
            if (!sessionActive || (status.Active && status.PlayerState != PlayerState.Error))   // a picture is on screen
                return 0;
            switch (status.Phase)
            {
                case "resolving": return 1;
                case "starting":
                case "recovering":
                    return status.Step == "opening" ? 3 : status.Step == "connecting" ? 2 : 1;
                default: return 0;
            }
        }

        internal static string StatusText(PlaybackStatus status, bool sessionActive)
        {
            if (!sessionActive)
                return null;
            switch (status.Phase)
            {
                case "resolving": return "Finding the title";
                case "starting":
                case "recovering":
                    if (status.Active && status.Phase == "starting")
                        return "Starting next: " + status.Pending;
                    switch (status.Step)
                    {
                        case "connecting":
                            // Several sources open in parallel (the race). The number racing right now goes up and
                            // down within seconds, which read as stuck or failing, so the line only carries numbers
                            // that move one way: how many sources were found and how many have been tried.
                            var found = status.StepSources;
                            if (status.StepTry > 1)
                                return "Trying other sources · " + (status.StepTry - 1) + " tried" + (found > 1 ? " of " + found : "");
                            return found > 1 ? "Trying the best of " + found + " sources" : "Connecting to the source";
                        case "opening": return "Starting playback";
                        default: return status.Phase == "recovering" ? "Switching to another source" : "Finding sources";
                    }
                default:
                    if (status.PlayerState == PlayerState.Buffering || status.Health?.Action == "wait")
                    {
                        var peers = status.Source?.Peers ?? 0;
                        return peers > 0 ? "Buffering · " + peers + " peers" : "Buffering";
                    }
                    return status.Active ? null : "Starting";
            }
        }

        private void OnCue(object sender, SubtitleCue cue)
        {
            lock (gate)
            {
                cueText = string.IsNullOrEmpty(cue?.Text) ? null : cue.Text;
                cueAt = now();
                cueDurationMs = cue?.DurationMs ?? 0;
                cueEmbedded = cue?.Embedded ?? false;
                cueVideo = session.CurrentVideo?.Id;
            }
        }

        /// Embedded cues from the TV player may never get an explicit clear, so an embedded cue with a duration expires by
        /// itself. External cues are cleared by the engine's cue pump (and must stay up while paused).
        private string CurrentCue(string videoId, DateTimeOffset at)
        {
            lock (gate)
            {
                if (cueText == null || cueVideo != videoId)
                    return null;
                if (cueEmbedded && cueDurationMs > 0 && (at - cueAt).TotalMilliseconds > cueDurationMs + 250)
                    return null;
                return cueText;
            }
        }

        private static void ThrowUnlessStarted(PlayResult result)
        {
            if (result == null)
                throw new InvalidOperationException("Playback did not start.");
            switch (result.Status)
            {
                case PlayStatus.Playing:
                case PlayStatus.Starting:
                case PlayStatus.Superseded:
                    return;
                case PlayStatus.CaughtUp:
                    throw new InvalidOperationException("You are caught up on " + (result.Name ?? "this show") + ".");
                default:
                    throw new InvalidOperationException(result.Error ?? result.Proposal ?? "Playback did not start (" + result.Status + ").");
            }
        }

        private void Fire(string what, Func<CancellationToken, Task> action)
        {
            Task task;
            try { task = Task.Run(() => action(CancellationToken.None)); }
            catch (Exception error)
            {
                log?.Invoke(what + ": " + error.Message);
                return;
            }
            task.ContinueWith(t => log?.Invoke(what + ": " + t.Exception?.GetBaseException().Message),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
