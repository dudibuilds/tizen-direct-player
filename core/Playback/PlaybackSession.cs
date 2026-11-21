using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Playback
{
    /// Native Stremio playback engine (parity sections G, H, I, J). One instance per app; the remote UI and the
    /// agent API call the same command surface. All timers run on the injected IClock.
    public sealed partial class PlaybackSession : IDisposable
    {
        private const string UserStop = "user_stop";
        private static readonly HashSet<string> QueueTransitionReasons = new HashSet<string>
            { "queue_skip", "stream_recovery", "episode_advance", "queue_completed", "stop_after" };

        private readonly PlaybackServices services;
        private readonly PlaybackOptions options;
        private readonly IClock clock;
        private readonly IPlayerBackend player;
        private readonly ISubtitleCapablePlayer subtitlePlayer;
        private readonly StallBlacklist blacklist;
        private readonly TrackPreferenceStore preferences;
        private readonly SubtitleCuePump pump = new SubtitleCuePump();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly object revisionGate = new object();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object historyGate = new object();
        private Task historyTail = Task.CompletedTask;
        // Session checkpoints: serialized under the gate, written in order by one background writer (latest wins), so
        // a slow fsync on the TV's flash never holds the gate that Stop, pause and recovery wait for.
        private readonly object checkpointGate = new object();
        private string checkpointPending;
        private bool checkpointActive;
        private Task checkpointWriter = Task.CompletedTask;

        private long revision;
        private CancellationTokenSource operation = new CancellationTokenSource();
        private SessionRecord active;
        private MetaDetail activeMeta;
        private VideoInfo activeVideo;
        private StreamCandidate activeCandidate;
        private IOpenedSource activeSource;
        private SessionRecord lastSession;
        private SessionRecord restorable;
        private QueueState queue;
        private bool queueLoaded;
        private IReadOnlyList<SubtitleCandidate> externalSubtitles = Array.Empty<SubtitleCandidate>();
        private int subtitleOffsetMs;
        private volatile string phase = "idle";
        private volatile string pending;
        /// While starting: sources | connecting | opening (see PlaybackStatus.Step), and the try count.
        private volatile string step;
        private volatile int stepSources, stepTry, stepRacing;
        private volatile string lastError;
        private int monitorBusy;
        private bool initialized;
        /// The app is behind another app or the TV's Home: anything that commits now starts paused.
        private volatile bool background;

        public PlaybackSession(PlaybackServices services, PlaybackOptions options = null)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            this.options = options ?? new PlaybackOptions();
            clock = services.Clock ?? SystemClock.Instance;
            player = services.Player ?? throw new ArgumentNullException(nameof(services.Player));
            subtitlePlayer = player as ISubtitleCapablePlayer;
            blacklist = new StallBlacklist(services.Store, clock, this.options.StallTtl);
            preferences = new TrackPreferenceStore(services.Store, clock);
            pump.CueChanged += (_, cue) => SubtitleCue?.Invoke(this, cue);
            if (subtitlePlayer != null)
                subtitlePlayer.SubtitleCue += OnEmbeddedCue;
        }

        /// Cues for the app-drawn subtitle layer (embedded and external alike). Text "" clears.
        public event EventHandler<SubtitleCue> SubtitleCue;
        /// Raised after any state change. Handlers must not block.
        public event EventHandler<PlaybackStatus> StatusChanged;

        public StallBlacklist Blacklist => blacklist;
        /// Background subtitle decision for the current session (tests await it).
        public Task PendingSubtitleWork { get; private set; } = Task.CompletedTask;
        /// A session left unfinished by a previous app run (I10); null when none.
        public SessionStatus Restorable => SessionStatus.From(restorable);
        /// Metadata, video and source of the active session (null when nothing is playing). For UI adapters.
        public MetaDetail CurrentMeta => active != null ? activeMeta : null;
        public VideoInfo CurrentVideo => active != null ? activeVideo : null;
        public StreamCandidate CurrentCandidate => active != null ? activeCandidate : null;

        // ================= lifecycle =================

        public async Task InitializeAsync(CancellationToken token)
        {
            if (initialized)
                return;
            initialized = true;
            await blacklist.LoadAsync(token).ConfigureAwait(false);
            await EnsureQueueAsync(token).ConfigureAwait(false);
            var saved = await JsonStore.ReadAsync<SessionRecord>(services.Store, PlaybackKeys.Session, token).ConfigureAwait(false);
            if (saved != null && saved.EndedAt == null)
                restorable = saved;
            else
                lastSession = saved;
            if (options.AutoMonitor)
            {
                _ = MonitorLoop();
                _ = SubtitleLoop();
            }
        }

        /// I10: resumes the session the app was playing before it was closed, at its last position.
        public async Task<PlayResult> RestoreAsync(CancellationToken token)
        {
            var saved = restorable;
            if (saved == null)
                return new PlayResult { Status = PlayStatus.NotFound, Error = "Nothing to restore." };
            restorable = null;
            ClearBackground();
            var (rev, opToken) = UserChange();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(opToken, token))
            {
                try
                {
                    var meta = await services.Catalog.GetMetaAsync(saved.MediaType, saved.MetaId, linked.Token).ConfigureAwait(false);
                    var video = saved.MediaType == MediaType.Series && saved.Season.HasValue && saved.Episode.HasValue
                        ? PlayResolver.EpisodeForRequest(meta, saved.Season.Value, saved.Episode.Value)
                        : new VideoInfo { Id = saved.VideoId, Title = meta.Summary.Name };
                    var plan = new StartPlan
                    {
                        Meta = meta, Video = video, Mode = saved.Mode, ResumeMs = saved.LastPositionMs, Quality = saved.Quality,
                        MaxSizeGb = saved.MaxSizeGb, StopAfterCurrent = saved.StopAfterCurrent, Reason = "restore",
                        PreferIdentity = saved.SourceIdentity,
                    };
                    return await StartAsync(plan, rev, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!IsCurrent(rev) || token.IsCancellationRequested)
                {
                    return new PlayResult { Status = PlayStatus.Superseded };
                }
                catch (Exception error)
                {
                    return Fail(error.Message);
                }
            }
        }

        /// Forgets the unfinished session from the previous run without playing it.
        public async Task DiscardRestorableAsync(CancellationToken token)
        {
            var saved = restorable;
            restorable = null;
            if (saved == null)
                return;
            saved.EndedAt = clock.UtcNow;
            saved.EndReason = "app_restart";
            lastSession = saved;
            Checkpoint(saved);
            await FlushCheckpointsAsync().ConfigureAwait(false);
        }

        // ================= commands =================

        public async Task<PlayResult> PlayAsync(PlayRequest request, CancellationToken token)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            ClearBackground();   // a viewer asking to play is looking at the screen
            var (rev, opToken) = UserChange();
            restorable = null;
            SetPhase("resolving", request.MetaId ?? request.Query);
            var timing = System.Diagnostics.Stopwatch.StartNew();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(opToken, token))
            {
                try
                {
                    var meta = await ResolveMetaAsync(request.MetaId, request.Query, request.Type, linked.Token).ConfigureAwait(false);
                    Log($"timing: meta {timing.ElapsedMilliseconds} ms");
                    if (meta.Ambiguous != null)
                    {
                        RestorePhase(rev);
                        return new PlayResult { Status = PlayStatus.Ambiguous, Candidates = meta.Ambiguous };
                    }
                    if (meta.Meta == null)
                    {
                        RestorePhase(rev);
                        return new PlayResult { Status = PlayStatus.NotFound, Error = "No title matched the request." };
                    }
                    var detail = meta.Meta;
                    var existing = await services.Library.GetAsync(detail.Summary.Id, linked.Token).ConfigureAwait(false);
                    Log($"timing: library {timing.ElapsedMilliseconds} ms");
                    var resolution = PlayResolver.Resolve(detail, existing, services.Library, request.Season, request.Episode,
                        request.Confirmed, clock.UtcNow);
                    var mode = request.Mode == PlaybackMode.Binge ? PlaybackMode.Binge : PlaybackMode.Single;
                    if (resolution.Status != PlayStatus.Playing)
                    {
                        RestorePhase(rev);
                        return new PlayResult
                        {
                            Status = resolution.Status, MetaId = detail.Summary.Id, Name = detail.Summary.Name,
                            Video = VideoRef.From(resolution.Video), ResumeMs = resolution.ResumeMs, Proposal = resolution.Proposal,
                            NextScheduled = VideoRef.From(resolution.NextScheduled), Mode = mode,
                        };
                    }
                    var plan = new StartPlan
                    {
                        Meta = detail, Video = resolution.Video, Mode = mode,
                        ResumeMs = request.StartMs ?? resolution.ResumeMs,
                        Quality = string.IsNullOrEmpty(request.Quality) ? "auto" : request.Quality,
                        MaxSizeGb = request.MaxSizeGb, Reason = "user_play", Forced = request.Source,
                    };
                    return await StartAsync(plan, rev, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!IsCurrent(rev) || token.IsCancellationRequested)
                {
                    return new PlayResult { Status = PlayStatus.Superseded };
                }
                catch (ArgumentException error)
                {
                    RestorePhase(rev);
                    return new PlayResult { Status = PlayStatus.Failed, Error = error.Message };
                }
                catch (Exception error)
                {
                    if (!IsCurrent(rev))
                        return new PlayResult { Status = PlayStatus.Superseded };
                    SetError(error.Message);
                    RestorePhase(rev);
                    return Fail(error.Message);
                }
            }
        }

        public async Task PauseAsync(CancellationToken token)
        {
            await WithSessionAsync(async session =>
            {
                player.Pause();
                session.UserPaused = true;
                session.Health = null;
                session.LastPositionMs = Math.Max(0, player.PositionMs);
                var snapshot = Snapshot(session);
                Enqueue(session, t => services.Progress.PausedAsync(snapshot.Meta, snapshot.Video, snapshot.Position, snapshot.Duration, t));
                await PersistAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }

        public async Task ResumeAsync(CancellationToken token)
        {
            ClearBackground();
            await WithSessionAsync(async session =>
            {
                player.Play();
                session.UserPaused = false;
                session.Health = null;
                var snapshot = Snapshot(session);
                // K4: Trakt "start" is sent again on resume; the sink owns what that means for the library.
                Enqueue(session, t => services.Progress.PlaybackStartedAsync(snapshot.Meta, snapshot.Video, snapshot.Position, t));
                await PersistAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }

        /// Seeks within SeekBudget. Stop (or any newer user change) abandons a seek in progress, so a native seek that
        /// never lands cannot hold the session: on timeout the source reopens at the last confirmed position, and the
        /// caller gets a TimeoutException.
        public async Task SeekAsync(long positionMs, CancellationToken token)
        {
            long rev;
            CancellationToken opToken;
            lock (revisionGate) { rev = revision; opToken = operation.Token; }
            SessionRecord hung = null;
            long target = 0;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, opToken))
            {
                await WithSessionAsync(async session =>
                {
                    var duration = player.DurationMs > 0 ? player.DurationMs : session.DurationMs;
                    target = Math.Max(0, duration > 0 ? Math.Min(positionMs, duration - 1000) : positionMs);
                    Timeline("seek", $"target={target} from={session.LastPositionMs}");
                    using (var budget = TaskHelpers.Budget(clock, options.SeekBudget, linked.Token))
                    {
                        try { await player.SeekAsync(target, budget.Token).WithCancellation(budget.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!linked.Token.IsCancellationRequested)
                        {
                            // The confirmed position stays; the monitor resumes from a clean slate.
                            session.Health = null;
                            hung = session;
                            return;
                        }
                    }
                    session.LastPositionMs = target;
                    session.Health = null;
                    pump.Update(target);
                    Timeline("seek", $"landed={SafePlayer(() => player.PositionMs, -1L)} target={target}");
                    await PersistAsync(token).ConfigureAwait(false);
                }, linked.Token).ConfigureAwait(false);
            }
            if (hung == null)
                return;
            var message = $"The TV player did not finish seeking to {target / 1000} s within {options.SeekBudget.TotalSeconds:0} s; reopening at {hung.LastPositionMs / 1000} s";
            Log(message);
            Timeline("seek", "timeout target=" + target + " reopen=" + hung.LastPositionMs);
            _ = ReopenCurrentSourceAsync(hung, rev, "seek_recovery", "seek_failed");
            throw new TimeoutException(message);
        }

        /// The app went to the background (true) or came back (false). In the background nothing that commits starts
        /// playing.
        public void SetBackground(bool value)
        {
            background = value;
            Timeline("lifecycle", value ? "background" : "foreground");
        }

        /// A host that never reports the return (an older remote-UI client) must not leave every later start paused.
        private void ClearBackground()
        {
            if (background)
                SetBackground(false);
        }

        /// Back in the foreground: when the TV took the decoder away while the app was hidden (an interruption) or the
        /// native player lost its media, the current source reopens at the saved position, paused. Otherwise nothing.
        public async Task ReconcileAfterForegroundAsync(CancellationToken token)
        {
            long rev;
            lock (revisionGate) rev = revision;
            var session = active;
            if (session == null)
                return;
            var interrupted = (player as IInterruptiblePlayer)?.TakeInterruption();
            var state = SafePlayer(() => player.State, PlayerState.Idle);
            var lost = state == PlayerState.Idle || state == PlayerState.Stopped || state == PlayerState.Error;
            Timeline("lifecycle", $"reconcile state={state} interrupted={interrupted ?? "-"} phase={phase}");
            if (interrupted == null && !lost)
                return;
            if (phase == "starting" || phase == "recovering")
                return;   // a start in progress replaces the media anyway
            Log("foreground: the player " + (interrupted != null ? "was interrupted (" + interrupted + ")" : "lost its media (" + state + ")")
                + "; reopening at " + session.LastPositionMs / 1000 + " s");
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (active != session)
                    return;
                session.UserPaused = true;   // the viewer comes back to a paused picture, like after Home
                session.Health = null;
            }
            finally { gate.Release(); }
            await ReopenCurrentSourceAsync(session, rev, "foreground_reopen", "stream_failed").ConfigureAwait(false);
        }

        /// Reopens the current source at the session's last confirmed position (after a seek that never landed, or a
        /// decoder lost in the background); ends the session with endReason when that fails, so the viewer never sits
        /// on a frozen frame reported as playing.
        private async Task ReopenCurrentSourceAsync(SessionRecord session, long rev, string reason, string endReason)
        {
            try
            {
                CancellationToken token;
                lock (revisionGate)
                {
                    if (revision != rev)
                        return;
                    token = operation.Token;
                }
                var candidate = activeCandidate;
                if (active != session || candidate == null)
                    return;
                var plan = new StartPlan
                {
                    Meta = activeMeta, Video = activeVideo, Mode = session.Mode, ResumeMs = session.LastPositionMs, Quality = session.Quality,
                    MaxSizeGb = session.MaxSizeGb, StopAfterCurrent = session.StopAfterCurrent, Reason = reason, Forced = candidate,
                    RecoveringSession = session, Recovery = new RecoveryState { Switches = session.Recovery?.Switches ?? 0 },
                };
                var result = await StartAsync(plan, rev, token).ConfigureAwait(false);
                if (result.Status == PlayStatus.Playing || result.Status == PlayStatus.Superseded)
                    return;
                session.Recovery = session.Recovery ?? new RecoveryState();
                session.Recovery.LastError = result.Error;
                await EndIfCurrentAsync(session, rev, endReason).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log(reason + ": " + error.Message); }
        }

        public Task SeekRelativeAsync(long deltaMs, CancellationToken token) =>
            SeekAsync(Math.Max(0, player.PositionMs + deltaMs), token);

        /// Replaces the active session's source with one the user picked, resuming at the current position.
        public async Task<PlayResult> SwitchSourceAsync(StreamCandidate candidate, CancellationToken token)
        {
            if (candidate?.Identity == null)
                throw new ArgumentException("A source with an info hash or URL is required.", nameof(candidate));
            ClearBackground();
            var (rev, opToken) = UserChange();
            StartPlan plan;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var session = active ?? throw new InvalidOperationException("No active playback.");
                var position = SafePlayer(() => player.PositionMs, 0L);
                if (position > 0)
                    session.LastPositionMs = position;
                plan = new StartPlan
                {
                    Meta = activeMeta, Video = activeVideo, Mode = session.Mode, ResumeMs = session.LastPositionMs, Quality = session.Quality,
                    MaxSizeGb = session.MaxSizeGb, StopAfterCurrent = session.StopAfterCurrent, Reason = "user_play", Forced = candidate,
                    RecoveringSession = session,
                    // A recovery-style switch: a queue is not interrupted, and a failed pick leaves the current source playing.
                    Recovery = new RecoveryState { Switches = session.Recovery?.Switches ?? 0 },
                };
            }
            finally { gate.Release(); }
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(opToken, token))
            {
                try { return await StartAsync(plan, rev, linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!IsCurrent(rev) || token.IsCancellationRequested)
                {
                    return new PlayResult { Status = PlayStatus.Superseded };
                }
            }
        }

        /// Saves the active session's position without ending it (app pause/terminate), so it stays restorable.
        public async Task SaveAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var session = active;
                if (session == null)
                    return;
                var position = SafePlayer(() => player.PositionMs, 0L);
                if (position > 0)
                    session.LastPositionMs = position;
                await PersistAsync(token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
            await FlushCheckpointsAsync().ConfigureAwait(false);
        }

        /// Stops playback and cancels any start in progress.
        public async Task StopAsync(CancellationToken token)
        {
            UserChange();
            restorable = null;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (active != null)
                    await FinalizeLockedAsync(UserStop).ConfigureAwait(false);
                else
                    SafeStop();   // a start cancelled mid-prepare may still hold the decoder
                SetPhase("idle", null);
            }
            finally { gate.Release(); }
            await FlushCheckpointsAsync().ConfigureAwait(false);
        }

        /// H4. enable: attach to the queue when the current item is the queue's current item; otherwise binge.
        /// disable: back to single; a playing queue is paused.
        public async Task<SessionStatus> SetBingeAsync(bool enable, CancellationToken token)
        {
            SessionStatus result = null;
            await WithSessionAsync(async session =>
            {
                var q = await EnsureQueueAsync(token).ConfigureAwait(false);
                var queueCurrent = q != null && q.CurrentIndex < q.Items.Count ? q.Items[q.CurrentIndex] : null;
                if (enable && q != null && (q.Status == QueueStatuses.Paused || q.Status == QueueStatuses.Playing)
                    && queueCurrent?.VideoId == session.VideoId)
                {
                    q.Status = QueueStatuses.Playing;
                    session.Mode = PlaybackMode.Queue;
                    await PersistQueueAsync(token).ConfigureAwait(false);
                }
                else
                {
                    if (!enable && session.Mode == PlaybackMode.Queue && q != null)
                    {
                        q.Status = QueueStatuses.Paused;
                        await PersistQueueAsync(token).ConfigureAwait(false);
                    }
                    session.Mode = enable ? PlaybackMode.Binge : PlaybackMode.Single;
                }
                await PersistAsync(token).ConfigureAwait(false);
                result = SessionStatus.From(session);
            }, token).ConfigureAwait(false);
            return result;
        }

        /// New (H6): end the session when the current item finishes, whatever the mode. The flag clears when it
        /// fires and is false for every newly started item. In a queue, the queue is left paused at the next item.
        public async Task<SessionStatus> StopAfterCurrentAsync(bool enabled, CancellationToken token)
        {
            SessionStatus result = null;
            await WithSessionAsync(async session =>
            {
                session.StopAfterCurrent = enabled;
                await PersistAsync(token).ConfigureAwait(false);
                result = SessionStatus.From(session);
            }, token).ConfigureAwait(false);
            return result;
        }

        /// Back on the next-episode card: keep watching the credits. Binge and queue skip the hand-over at the cut-off
        /// for this item and advance only when it ends.
        public async Task HoldAdvanceAsync(CancellationToken token)
        {
            await WithSessionAsync(async session =>
            {
                session.CreditsHeld = true;
                Timeline("handover", "held for the credits: " + session.VideoId);
                await PersistAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }

        public async Task SelectAudioAsync(int index, CancellationToken token)
        {
            string metaId = null;
            string language = null;
            await WithSessionAsync(session =>
            {
                player.SelectAudio(index);
                var track = player.AudioTracks.FirstOrDefault(t => t.Index == index);
                language = TrackRules.Normalize(track?.Language, track?.Label);
                session.AudioLanguage = language;
                metaId = session.MetaId;
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
            if (language != null)
                await preferences.UpdateAsync(metaId, language, null, token).ConfigureAwait(false);
        }

        /// Applies a subtitle choice from the user and remembers its language for the show.
        public async Task SelectSubtitleAsync(SubtitleDecision choice, CancellationToken token)
        {
            choice = choice ?? new SubtitleDecision { Kind = SubtitleKind.None };
            string metaId = null;
            SessionRecord chosenFor = null;
            await WithSessionAsync(async session =>
            {
                if (choice.Kind == SubtitleKind.Embedded && choice.Language == null)
                {
                    var track = player.EmbeddedSubtitleTracks.FirstOrDefault(t => t.Index == choice.EmbeddedIndex);
                    choice.Language = TrackRules.Normalize(track?.Language, track?.Label);
                }
                if (choice.Kind == SubtitleKind.External && choice.Language == null)
                    choice.Language = TrackRules.Normalize(choice.External?.Language);
                choice.Reason = "user";
                session.Subtitle = choice;
                metaId = session.MetaId;
                chosenFor = session;
                ApplyEmbeddedLocked(choice);
                await PersistAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            // The download and parse run off the session gate, so a slow subtitle server never holds Stop or recovery.
            await LoadExternalAsync(chosenFor, choice, token).ConfigureAwait(false);
            var remembered = choice.Kind == SubtitleKind.None ? "off" : choice.Language;
            if (remembered != null)
                await preferences.UpdateAsync(metaId, null, remembered, token).ConfigureAwait(false);
        }

        /// Subtitle timing offset, clamped to ±30 s. External cues shift app-side; embedded cues via the backend.
        public int SetSubtitleOffset(int offsetMs)
        {
            subtitleOffsetMs = TrackRules.ClampSubtitleOffset(offsetMs);
            pump.OffsetMs = subtitleOffsetMs;
            try { subtitlePlayer?.SetEmbeddedSubtitleOffset(subtitleOffsetMs); }
            catch (InvalidOperationException) { }
            var position = player.PositionMs;
            pump.Update(position);
            NoteOffsetPoint(position);
            RaiseStatus();
            return subtitleOffsetMs;
        }

        /// External subtitles found for the current item (for the subtitle menu).
        public IReadOnlyList<SubtitleCandidate> ExternalSubtitles => externalSubtitles;

        // ---- queue (J) ----

        public QueueStatus GetQueue() => QueueStatus.From(queue);

        public async Task<QueueResult> QueueSetAsync(IReadOnlyList<QueueItemRequest> items, string quality, double? maxSizeGb,
            CancellationToken token) => await QueueBuildAsync(items, quality, maxSizeGb, false, token).ConfigureAwait(false);

        /// items null: start (or resume) the saved queue. Otherwise replace the queue and start at item 1.
        public async Task<QueueResult> QueueStartAsync(IReadOnlyList<QueueItemRequest> items, string quality, double? maxSizeGb,
            CancellationToken token)
        {
            ClearBackground();
            if (items != null)
                return await QueueBuildAsync(items, quality, maxSizeGb, true, token).ConfigureAwait(false);
            var (rev, opToken) = UserChange();
            restorable = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(opToken, token))
            {
                var q = await EnsureQueueAsync(linked.Token).ConfigureAwait(false);
                if (q == null || q.Items.Count == 0)
                    return new QueueResult { Status = "error", Error = "No queue is set." };
                var index = q.Status == QueueStatuses.Completed || q.CurrentIndex >= q.Items.Count ? 0 : q.CurrentIndex;
                if (q.Status == QueueStatuses.Completed)
                    q.CompletedAt = null;
                return await StartQueueItemAsync(index, rev, linked.Token).ConfigureAwait(false);
            }
        }

        /// Appends one episode to the saved queue (creating a ready queue when there is none).
        public async Task<QueueResult> QueueAddAsync(QueueItem item, CancellationToken token)
        {
            if (item?.MetaId == null || item.VideoId == null)
                throw new ArgumentException("A queue item needs a meta id and a video id.", nameof(item));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var q = await EnsureQueueAsync(token).ConfigureAwait(false);
                if (q == null || q.Status == QueueStatuses.Completed)
                {
                    q = queue = new QueueState
                    {
                        Id = "queue:" + clock.UtcNow.ToUnixTimeMilliseconds(), Status = QueueStatuses.Ready, CurrentIndex = 0,
                        Quality = "auto", MaxSizeGb = EffectiveMaxSizeGb(null), CreatedAt = clock.UtcNow,
                    };
                    queueLoaded = true;
                }
                q.Items.Add(item);
                await PersistQueueAsync(token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
            RaiseStatus();
            return new QueueResult { Status = "ok", Queue = QueueStatus.From(queue) };
        }

        /// Removes one queue item. The item playing right now cannot be removed (skip it instead).
        public async Task<QueueResult> QueueRemoveAtAsync(int index, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var q = await EnsureQueueAsync(token).ConfigureAwait(false);
                if (q == null || index < 0 || index >= q.Items.Count)
                    return new QueueResult { Status = "error", Error = "Queue item not found." };
                var playing = q.Status == QueueStatuses.Playing || q.Status == QueueStatuses.Starting;
                if (playing && index == q.CurrentIndex)
                    throw new InvalidOperationException("The playing item cannot be removed; skip it instead.");
                q.Items.RemoveAt(index);
                if (index < q.CurrentIndex)
                    q.CurrentIndex--;
                if (q.CurrentIndex >= q.Items.Count)
                    q.CurrentIndex = Math.Max(0, q.Items.Count - 1);
                if (q.Items.Count == 0)
                    queue = null;
                await PersistQueueAsync(token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
            RaiseStatus();
            return new QueueResult { Status = "ok", Queue = QueueStatus.From(queue) };
        }

        public async Task<QueueResult> QueueClearAsync(CancellationToken token)
        {
            UserChange();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                queue = null;
                queueLoaded = true;
                await PersistQueueAsync(token).ConfigureAwait(false);
                if (active?.Mode == PlaybackMode.Queue)
                {
                    active.Mode = PlaybackMode.Single;
                    await PersistAsync(token).ConfigureAwait(false);
                }
            }
            finally { gate.Release(); }
            RaiseStatus();
            return new QueueResult { Status = "ok" };
        }

        public async Task<QueueResult> QueueSkipAsync(CancellationToken token)
        {
            var (rev, opToken) = UserChange();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(opToken, token))
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    var q = await EnsureQueueAsync(linked.Token).ConfigureAwait(false);
                    if (q == null || q.Status != QueueStatuses.Playing)
                        return new QueueResult { Status = "error", Error = "No active queue." };
                    if (active?.Mode == PlaybackMode.Queue)
                        await FinalizeLockedAsync("queue_skip").ConfigureAwait(false);
                }
                finally { gate.Release(); }
                return await AdvanceQueueAsync(rev, linked.Token).ConfigureAwait(false);
            }
        }

        // ---- status ----

        /// A session is playing or starting (GetStatus().Active, or phase resolving/starting), without building the
        /// status: no native player calls, no copies. For pollers that only need this one answer.
        public bool IsBusy
        {
            get
            {
                var current = phase;
                return active != null || current == "resolving" || current == "starting";
            }
        }

        public PlaybackStatus GetStatus()
        {
            var session = active;
            var source = activeSource;
            var candidate = activeCandidate;
            var status = new PlaybackStatus
            {
                Phase = phase,
                Active = session != null,
                Revision = Interlocked.Read(ref revision),
                PlayerState = SafePlayer(() => player.State, PlayerState.Idle),
                Session = SessionStatus.From(session),
                LastSession = SessionStatus.From(lastSession),
                Queue = QueueStatus.From(queue),
                Pending = pending,
                Step = step,
                StepSources = stepSources,
                StepTry = stepTry,
                StepRacing = stepRacing,
                LastError = lastError,
            };
            if (session != null)
            {
                var position = SafePlayer(() => player.PositionMs, session.LastPositionMs);
                if (position > 0)
                    status.Session.PositionMs = position;
                status.Source = new SourceStatus
                {
                    Identity = session.SourceIdentity, Name = candidate?.Name, Title = candidate?.Title, Quality = candidate?.Quality,
                    SizeBytes = source?.LengthBytes > 0 ? source.LengthBytes : session.SourceSizeBytes,
                    DownloadBytesPerSecond = SafePlayer(() => source?.DownloadBytesPerSecond ?? 0, 0),
                    Peers = SafePlayer(() => source?.Peers ?? 0, 0),
                };
                if (session.Health != null)
                    status.Health = new HealthStatus
                    {
                        Action = session.Health.Action, Reason = session.Health.Reason, PlaybackRatio = session.Health.PlaybackRatio,
                        ThroughputBytesPerSecond = session.Health.ThroughputBytesPerSecond,
                    };
                status.Tracks = new TrackStatus
                {
                    Audio = SafePlayer(() => player.AudioTracks, Array.Empty<TrackInfo>()),
                    SelectedAudio = subtitlePlayer?.SelectedAudio,
                    EmbeddedSubtitles = SafePlayer(() => player.EmbeddedSubtitleTracks, Array.Empty<TrackInfo>()),
                    Subtitle = session.Subtitle,
                    SubtitleOffsetMs = subtitleOffsetMs,
                    SubtitleSync = session.Subtitle?.Kind == SubtitleKind.External ? SubtitleSync : SubtitleTiming.Identity,
                };
            }
            return status;
        }

        // ================= monitor (I1-I3, I5-I8) =================

        /// One monitor pass. Runs every 5 s when AutoMonitor is on; tests call it directly.
        public async Task TickAsync()
        {
            if (Interlocked.Exchange(ref monitorBusy, 1) == 1)
                return;
            SettleOffsetPoint();
            try { await TickCoreAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log("monitor: " + error.Message); }
            finally { Interlocked.Exchange(ref monitorBusy, 0); }
        }

        private enum TickAction { None, EndOfItem, Recover }

        private async Task TickCoreAsync()
        {
            var session = active;
            if (session == null || gate.CurrentCount == 0)
                return;
            long rev;
            CancellationToken token;
            lock (revisionGate) { rev = revision; token = operation.Token; }
            var state = player.State;
            var position = player.PositionMs;
            var duration = player.DurationMs;
            var now = clock.UtcNow;
            var action = TickAction.None;
            string endReason = null;

            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (active != session || !IsCurrent(rev))
                    return;
                var credible = duration > 0 && position >= 0
                    && (session.DurationMs <= 0 || Math.Abs(duration - session.DurationMs) < 5000)
                    && position + 3000 >= session.LastPositionMs;
                if (credible)
                {
                    session.CredibleSamples++;
                    session.DurationMs = duration;
                    session.LastPositionMs = position;
                    var ratio = (double)position / duration;
                    if (session.CredibleSamples >= 2 && ratio >= options.CompletionRatio && !session.MarkedWatched)
                        MarkWatched(session);
                    else if (position - session.LastSavedMs >= options.ProgressSaveEveryMs)
                    {
                        session.LastSavedMs = position;
                        var snapshot = Snapshot(session);
                        Enqueue(session, t => services.Progress.ProgressAsync(snapshot.Meta, snapshot.Video, snapshot.Position, snapshot.Duration, t));
                    }
                    var advancing = (session.Mode == PlaybackMode.Binge || session.Mode == PlaybackMode.Queue) && !session.StopAfterCurrent;
                    // The cut-off hand-over waits while the viewer has paused or chose to keep watching the credits; the
                    // end of the file still advances.
                    var handOff = EpisodeHandOff.Reached(position, duration) && !session.UserPaused && !session.CreditsHeld;
                    if (advancing && session.MarkedWatched
                        && (handOff || state == PlayerState.Stopped || state == PlayerState.Completed))
                        action = TickAction.EndOfItem;
                }
                if (action == TickAction.None && state == PlayerState.Completed)
                {
                    // The TV player reached end of stream. Count it as watched when the last credible sample was near the end.
                    var last = session.DurationMs > 0 ? (double)session.LastPositionMs / session.DurationMs : 0;
                    if (session.MarkedWatched || last >= 0.8 || (session.DurationMs > 0 && session.DurationMs - session.LastPositionMs <= 60000))
                    {
                        if (!session.MarkedWatched)
                            MarkWatched(session);
                        action = TickAction.EndOfItem;
                    }
                    else
                        action = TickAction.Recover;
                }
                else if (action == TickAction.None && (state == PlayerState.Stopped || state == PlayerState.Error))
                {
                    if (!session.MarkedWatched && !session.UserPaused
                        && (state == PlayerState.Error || StreamHealth.IsStarvedStop(session.Health, now, position, session.DurationMs)))
                        action = TickAction.Recover;
                    else if (session.StopAfterCurrent || session.Mode == PlaybackMode.Single)
                        endReason = session.StopAfterCurrent && session.MarkedWatched ? "stop_after" : "ended";
                    else if (session.Mode == PlaybackMode.Queue)
                    {
                        if (session.MarkedWatched)
                            action = TickAction.EndOfItem;
                        else
                            endReason = "ended";
                    }
                    else if (session.Mode == PlaybackMode.Binge && session.MarkedWatched && state == PlayerState.Error)
                        action = TickAction.EndOfItem;   // an error in the credits must not strand the binge
                    // Binge ignores a plain stop (stoppedPlaybackAction).
                }
                if (action == TickAction.EndOfItem && (session.StopAfterCurrent || session.Mode == PlaybackMode.Single))
                {
                    endReason = session.StopAfterCurrent ? "stop_after" : "ended";
                    action = TickAction.None;
                }
                if (endReason != null)
                {
                    if (endReason == "stop_after" && session.Mode == PlaybackMode.Queue)
                        await ParkQueueAfterCurrentAsync(token).ConfigureAwait(false);
                    await FinalizeLockedAsync(endReason).ConfigureAwait(false);
                    return;
                }
                await PersistAsync(token).ConfigureAwait(false);
            }
            finally { gate.Release(); }

            if (action == TickAction.EndOfItem)
            {
                if (session.Mode == PlaybackMode.Binge)
                    await AdvanceBingeAsync(session, rev, token).ConfigureAwait(false);
                else
                    await AdvanceQueueAsync(rev, token).ConfigureAwait(false);
                return;
            }
            if (action == TickAction.Recover)
            {
                await ReplaceSourceAsync(session, true, rev, token).ConfigureAwait(false);
                return;
            }
            SampleTimeline(state, position);
            await ObserveHealthAsync(session, state, position, now, rev, token).ConfigureAwait(false);
            RaiseStatus();
        }

        private async Task ObserveHealthAsync(SessionRecord session, PlayerState state, long position, DateTimeOffset now,
            long rev, CancellationToken token)
        {
            if (session.UserPaused)
            {
                session.Health = null;
                return;
            }
            double? throughput = null;
            if (session.Health?.BadSince != null
                && (session.LastThroughputSampleAt == null || now - session.LastThroughputSampleAt.Value >= options.ThroughputSampleInterval))
            {
                session.LastThroughputSampleAt = now;
                // I6 on the TV: the engine's own download rate replaces the 128 KB HTTP range probe.
                throughput = SafePlayer(() => activeSource?.DownloadBytesPerSecond, (double?)null);
            }
            // What is ready at the playhead: trusted over the aggregate rate, which can rise on unrelated pieces.
            var contiguous = SafePlayer(() => (activeSource as IPlayheadSource)?.ContiguousBytesAhead, (long?)null);
            var frontier = SafePlayer(() => (activeSource as IPlayheadSource)?.ReadyThroughBytes, (long?)null);
            var before = session.Health?.Action;
            session.Health = StreamHealth.Observe(session.Health, now, position, state, session.UserPaused, throughput, frontier);
            var health = session.Health;
            if (health != null && health.Reason != "playing" && health.Reason != "inactive_or_seek" && health.Action != before)
                Timeline("health", $"{health.Action} {health.Reason} pos={position} ahead={contiguous?.ToString() ?? "?"} bad={(health.BadSince.HasValue ? (now - health.BadSince.Value).TotalSeconds : 0):0}s");
            if (health?.Action != "replace" || active != session || !IsCurrent(rev))
                return;
            var nearEnd = session.DurationMs > 0 && session.DurationMs - position < 30000;
            if (nearEnd && session.MarkedWatched)
            {
                // A stall in the closing credits of a watched item: finish it (advance or end) instead of waiting forever
                // or racing a new source for the last seconds.
                Timeline("recovery", "stall near the end of a watched item: finishing it");
                await FinishStalledItemAsync(session, rev, token).ConfigureAwait(false);
                return;
            }
            await ReplaceSourceAsync(session, false, rev, token).ConfigureAwait(false);
        }

        /// One line per monitor pass for soak tests: playhead, player state, data ready at the playhead, engine rate.
        private void SampleTimeline(PlayerState state, long position)
        {
            var source = activeSource;
            var playhead = source as IPlayheadSource;
            var ahead = SafePlayer(() => playhead?.ContiguousBytesAhead ?? -1, -1L);
            var missing = SafePlayer(() => playhead?.FirstMissingPiece ?? -1, -1);
            var rate = SafePlayer(() => source?.DownloadBytesPerSecond ?? 0, 0);
            var peers = SafePlayer(() => source?.Peers ?? 0, 0);
            Timeline("sample", $"pos={position} state={state} ahead={(ahead < 0 ? "?" : (ahead / 1024).ToString() + "KiB")} firstMissing={missing} rate={rate / 1024:0}KiB/s peers={peers}");
        }

        private async Task FinishStalledItemAsync(SessionRecord session, long rev, CancellationToken token)
        {
            var advancing = (session.Mode == PlaybackMode.Binge || session.Mode == PlaybackMode.Queue) && !session.StopAfterCurrent;
            if (advancing)
            {
                if (session.Mode == PlaybackMode.Binge)
                    await AdvanceBingeAsync(session, rev, token).ConfigureAwait(false);
                else
                    await AdvanceQueueAsync(rev, token).ConfigureAwait(false);
                return;
            }
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (active != session || !IsCurrent(rev))
                    return;
                if (session.StopAfterCurrent && session.Mode == PlaybackMode.Queue)
                    await ParkQueueAfterCurrentAsync(token).ConfigureAwait(false);
                await FinalizeLockedAsync(session.StopAfterCurrent ? "stop_after" : "ended").ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        /// I8: replace a struggling source, at most MaxSwitches attempts (successful or failed) with a cooldown,
        /// resuming at the last position. A stall that can no longer be replaced ends the session after GiveUpAfter,
        /// so recovery always reaches an outcome instead of looping.
        private async Task ReplaceSourceAsync(SessionRecord session, bool starved, long rev, CancellationToken token)
        {
            var now = clock.UtcNow;
            var recovery = session.Recovery ?? (session.Recovery = new RecoveryState());
            var exhausted = recovery.Switches + recovery.Failures >= options.MaxSwitches;
            if (exhausted || (recovery.RetryAfter.HasValue && now < recovery.RetryAfter.Value))
            {
                // Only a frozen playhead is given up on; repeated buffering that still moves keeps playing.
                var frozenSince = session.Health?.LastProgressAt;
                if (starved)
                    await EndIfCurrentAsync(session, rev, "stream_failed").ConfigureAwait(false);
                else if (frozenSince.HasValue && now - frozenSince.Value >= options.GiveUpAfter)
                {
                    Timeline("recovery", $"giving up: switches={recovery.Switches} failures={recovery.Failures} frozen for {(now - frozenSince.Value).TotalSeconds:0}s");
                    await EndIfCurrentAsync(session, rev, "stream_failed").ConfigureAwait(false);
                }
                return;
            }
            recovery.RetryAfter = now + options.SwitchCooldown;
            if (session.SourceIdentity != null && !recovery.Attempted.Contains(session.SourceIdentity))
                recovery.Attempted.Add(session.SourceIdentity);
            Timeline("recovery", $"replace {(starved ? "starved" : "stalled")} source {session.SourceIdentity} at {session.LastPositionMs} (switches={recovery.Switches} failures={recovery.Failures})");
            var meta = activeMeta;
            var video = activeVideo;
            var plan = new StartPlan
            {
                Meta = meta, Video = video, Mode = session.Mode, ResumeMs = session.LastPositionMs, Quality = session.Quality,
                MaxSizeGb = session.MaxSizeGb, StopAfterCurrent = session.StopAfterCurrent, Reason = "stream_recovery",
                Recovery = new RecoveryState { Attempted = new List<string>(recovery.Attempted), Switches = recovery.Switches + 1, Failures = recovery.Failures, RetryAfter = recovery.RetryAfter },
                RecoveringSession = session, Starved = starved,
            };
            var result = await StartAsync(plan, rev, token).ConfigureAwait(false);
            if (result.Status == PlayStatus.Playing || result.Status == PlayStatus.Superseded)
                return;
            recovery.LastError = result.Error;
            recovery.Failures++;
            Timeline("recovery", "replacement failed: " + result.Error);
            if (starved)
                await EndIfCurrentAsync(session, rev, "stream_failed").ConfigureAwait(false);
            else if (active == session)
            {
                // StartAsync restored the session's own source (or ended it) when the attempt had replaced it.
                SetPhase(session.UserPaused ? "paused" : "playing", null);
                await PersistAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task EndIfCurrentAsync(SessionRecord session, long rev, string reason)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (active == session && IsCurrent(rev))
                {
                    lastError = reason;
                    await FinalizeLockedAsync(reason).ConfigureAwait(false);
                }
            }
            finally { gate.Release(); }
        }

        private async Task AdvanceBingeAsync(SessionRecord previous, long rev, CancellationToken token)
        {
            MetaDetail meta;
            try { meta = await services.Catalog.GetMetaAsync(previous.MediaType, previous.MetaId, token).ConfigureAwait(false); }
            catch (Exception error) when (!(error is OperationCanceledException)) { meta = activeMeta; }
            var current = activeVideo ?? new VideoInfo { Id = previous.VideoId, Season = previous.Season ?? 0, Episode = previous.Episode ?? 0 };
            var next = PlayResolver.NextReleasedVideoAfter(meta, current, clock.UtcNow);
            if (next == null || next.Id == previous.VideoId)
            {
                await EndIfCurrentAsync(previous, rev, "binge_complete").ConfigureAwait(false);
                return;
            }
            var plan = new StartPlan
            {
                Meta = meta, Video = next, Mode = PlaybackMode.Binge, ResumeMs = 0, Quality = previous.Quality,
                MaxSizeGb = previous.MaxSizeGb, Reason = "episode_advance",
            };
            var result = await StartAsync(plan, rev, token).ConfigureAwait(false);
            if (result.Status == PlayStatus.Failed)
                await EndIfCurrentAsync(previous, rev, "advance_failed").ConfigureAwait(false);
        }

        private async Task<QueueResult> AdvanceQueueAsync(long rev, CancellationToken token)
        {
            var q = await EnsureQueueAsync(token).ConfigureAwait(false);
            if (q == null)
            {
                var session = active;
                if (session != null)
                    await EndIfCurrentAsync(session, rev, "queue_missing").ConfigureAwait(false);
                return new QueueResult { Status = "error", Error = "queue_missing" };
            }
            var nextIndex = q.CurrentIndex + 1;
            if (nextIndex >= q.Items.Count)
            {
                var session = active;
                if (session != null)
                    await EndIfCurrentAsync(session, rev, "queue_completed").ConfigureAwait(false);
                q.Status = QueueStatuses.Completed;
                q.CompletedAt = clock.UtcNow;
                await PersistQueueAsync(CancellationToken.None).ConfigureAwait(false);
                RaiseStatus();
                return new QueueResult { Status = "queue_complete", Queue = QueueStatus.From(q) };
            }
            return await StartQueueItemAsync(nextIndex, rev, token).ConfigureAwait(false);
        }

        private async Task<QueueResult> StartQueueItemAsync(int index, long rev, CancellationToken token)
        {
            var q = await EnsureQueueAsync(token).ConfigureAwait(false);
            if (q == null || index < 0 || index >= q.Items.Count)
                return new QueueResult { Status = "error", Error = "Queue item not found." };
            // The cursor moves only once the item plays (or definitively fails): a failed start must not leave it on
            // an item the monitor would then advance past.
            var previous = active;
            q.LastError = null;
            var item = q.Items[index];
            PlayResult result;
            try
            {
                var meta = await services.Catalog.GetMetaAsync(item.MediaType, item.MetaId, token).ConfigureAwait(false);
                var video = PlayResolver.EpisodeForRequest(meta, item.Season, item.Episode);
                result = await StartAsync(new StartPlan
                {
                    Meta = meta, Video = video, Mode = PlaybackMode.Queue, ResumeMs = 0, Quality = q.Quality, MaxSizeGb = q.MaxSizeGb,
                    Reason = "episode_advance",
                }, rev, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!IsCurrent(rev))
            {
                return new QueueResult { Status = PlayStatus.Superseded, Queue = QueueStatus.From(q) };
            }
            catch (Exception error)
            {
                result = new PlayResult { Status = PlayStatus.Failed, Error = error.Message };
            }
            if (result.Status == PlayStatus.Playing && IsCurrent(rev))
            {
                q.CurrentIndex = index;
                q.Status = QueueStatuses.Playing;
                await PersistQueueAsync(CancellationToken.None).ConfigureAwait(false);
                Timeline("handover", "queue item " + (index + 1) + " playing");
            }
            else if (result.Status == PlayStatus.Failed && IsCurrent(rev))
            {
                // The finished item must not stay active: the next monitor pass would advance again and skip this one.
                if (previous != null && active == previous && previous.Mode == PlaybackMode.Queue)
                    await EndIfCurrentAsync(previous, rev, "advance_failed").ConfigureAwait(false);
                q.CurrentIndex = index;   // a resume retries the item that failed
                q.Status = QueueStatuses.Error;
                q.LastError = result.Error;
                await PersistQueueAsync(CancellationToken.None).ConfigureAwait(false);
                Timeline("handover", "queue item " + (index + 1) + " failed: " + result.Error);
            }
            result.Queue = QueueStatus.From(q);
            RaiseStatus();
            return new QueueResult
            {
                Status = result.Status == PlayStatus.Playing ? "ok" : result.Status, Play = result, Queue = result.Queue,
                Error = result.Error,
            };
        }

        /// Stop-after inside a queue: leave the queue paused at the next item (or completed after the last).
        private async Task ParkQueueAfterCurrentAsync(CancellationToken token)
        {
            var q = await EnsureQueueAsync(token).ConfigureAwait(false);
            if (q == null)
                return;
            if (q.CurrentIndex + 1 >= q.Items.Count)
            {
                q.Status = QueueStatuses.Completed;
                q.CompletedAt = clock.UtcNow;
            }
            else
            {
                q.CurrentIndex++;
                q.Status = QueueStatuses.Paused;
            }
            q.LastStopReason = "stop_after";
            await PersistQueueAsync(token).ConfigureAwait(false);
        }

        private async Task<QueueResult> QueueBuildAsync(IReadOnlyList<QueueItemRequest> items, string quality, double? maxSizeGb,
            bool start, CancellationToken token)
        {
            if (items == null || items.Count == 0)
                return new QueueResult { Status = "error", Error = "items must contain at least one episode" };
            var (rev, opToken) = UserChange();
            restorable = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(opToken, token))
            {
                var resolved = new List<QueueItem>();
                try
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        var meta = await ResolveMetaAsync(item.MetaId, item.Query, MediaType.Series, linked.Token).ConfigureAwait(false);
                        if (meta.Ambiguous != null)
                            return new QueueResult { Status = PlayStatus.Ambiguous, ItemIndex = i, Candidates = meta.Ambiguous };
                        if (meta.Meta == null || meta.Meta.Summary.Type != MediaType.Series)
                            return new QueueResult { Status = "error", ItemIndex = i, Error = $"Queue item {i + 1} is not a series." };
                        VideoInfo video;
                        if (item.Season.HasValue && item.Episode.HasValue)
                            video = PlayResolver.EpisodeForRequest(meta.Meta, item.Season.Value, item.Episode.Value);
                        else
                        {
                            var existing = await services.Library.GetAsync(meta.Meta.Summary.Id, linked.Token).ConfigureAwait(false);
                            video = existing != null ? services.Library.NextVideo(existing, meta.Meta) : null;
                            if (video == null)
                                return new QueueResult
                                {
                                    Status = "error", ItemIndex = i,
                                    Error = $"Queue item {i + 1}: no unwatched or resumable episodes found for {meta.Meta.Summary.Name}",
                                };
                        }
                        resolved.Add(new QueueItem
                        {
                            MetaId = meta.Meta.Summary.Id, MediaType = MediaType.Series, Name = meta.Meta.Summary.Name, VideoId = video.Id,
                            Season = video.Season, Episode = video.Episode, Title = video.Title,
                        });
                    }
                }
                catch (OperationCanceledException) when (!IsCurrent(rev))
                {
                    return new QueueResult { Status = PlayStatus.Superseded };
                }
                queue = new QueueState
                {
                    Id = "queue:" + clock.UtcNow.ToUnixTimeMilliseconds(),
                    Status = start ? QueueStatuses.Starting : QueueStatuses.Ready,
                    CurrentIndex = 0,
                    Quality = string.IsNullOrEmpty(quality) ? "auto" : quality,
                    MaxSizeGb = EffectiveMaxSizeGb(maxSizeGb),
                    CreatedAt = clock.UtcNow,
                    Items = resolved,
                };
                queueLoaded = true;
                await PersistQueueAsync(linked.Token).ConfigureAwait(false);
                RaiseStatus();
                if (!start)
                    return new QueueResult { Status = "ok", Queue = QueueStatus.From(queue) };
                return await StartQueueItemAsync(0, rev, linked.Token).ConfigureAwait(false);
            }
        }

        // ================= startup (G1-G4) =================

        private sealed class StartPlan
        {
            public MetaDetail Meta;
            public VideoInfo Video;
            public PlaybackMode Mode;
            public long ResumeMs;
            public string Quality = "auto";
            public double? MaxSizeGb;
            public bool StopAfterCurrent;
            public string Reason;
            public RecoveryState Recovery;
            public SessionRecord RecoveringSession;
            public string PreferIdentity;
            /// A source the user picked: only this candidate is tried.
            public StreamCandidate Forced;
            /// Set once a commit attempt replaced the previous session's media in the player.
            public bool PlayerReplaced;
            /// Set once a commit attempt opened anything in the player (a failed start must release it).
            public bool PlayerTouched;
            /// A recovery from a starved player: its caller ends the session on failure, so nothing is restored.
            public bool Starved;
        }

        private sealed class OpenFailure : Exception
        {
            public OpenFailure(string message) : base(message) { }
        }

        private static int Validity(StreamCandidate c) => c.LanguageTier < 0 ? 0 : 1;

        private static bool SameGroup(StreamCandidate a, StreamCandidate b) =>
            Validity(a) == Validity(b) && a.HealthBand == b.HealthBand && a.LanguageTier == b.LanguageTier;

        private async Task<PlayResult> StartAsync(StartPlan plan, long rev, CancellationToken token)
        {
            var meta = plan.Meta;
            var video = plan.Video;
            var anime = PlayResolver.IsJapaneseAnime(meta);
            SetPhase(plan.Recovery != null ? "recovering" : "starting", Describe(meta, video));
            var failures = new List<SourceFailure>();
            var tried = new HashSet<string>(plan.Recovery?.Attempted ?? new List<string>());
            // A recovery never races another file of the torrent it replaces: opening it would take over (preempt) the
            // stream still playing before the replacement has proven itself. A hand-picked source may.
            var keepHash = plan.Recovery != null && plan.Forced == null ? plan.RecoveringSession?.SourceInfoHash : null;
            bool Allowed(StreamCandidate c) => keepHash == null || !string.Equals(c.InfoHash, keepHash, StringComparison.OrdinalIgnoreCase);
            using (var deadline = new ProgressDeadline(clock, token, options.StartupInitial, options.StartupExtension, options.StartupMaximum))
            {
                var signal = deadline.Token;
                try
                {
                    var subtitlesTask = FetchSubtitlesAsync(meta, video, token);
                    var sourceOptions = new SourceOptions
                    {
                        Quality = plan.Quality ?? "auto", MaxSizeGb = plan.MaxSizeGb, IsAnime = anime,
                        Episodic = meta.Summary.Type == MediaType.Series, ExcludeIdentities = new HashSet<string>(tried),
                        EnglishOriginal = SourceOptions.IsEnglishOriginal(meta),
                    };
                    SetStep(rev, "sources");
                    var resolveTimer = System.Diagnostics.Stopwatch.StartNew();
                    List<StreamCandidate> ranked;
                    if (plan.Forced?.Identity != null)
                    {
                        // A source picked by hand is the only candidate: its ranked list was thrown away anyway, so
                        // don't wait for every add-on (up to the discovery deadline) before opening it.
                        ranked = new List<StreamCandidate> { plan.Forced };
                        Log("timing: sources skipped (source chosen)");
                    }
                    else
                    {
                        ranked = (await services.Resolver.GetRankedAsync(meta.Summary.Type, video.Id, sourceOptions, signal).ConfigureAwait(false)
                            ?? Array.Empty<StreamCandidate>()).Where(c => c?.Identity != null).ToList();
                        Log($"timing: sources {resolveTimer.ElapsedMilliseconds} ms, {ranked.Count} ranked");
                    }
                    if (plan.Forced?.Identity == null && plan.PreferIdentity != null)
                    {
                        var preferred = ranked.FindIndex(c => c.Identity == plan.PreferIdentity);
                        if (preferred > 0 && SameGroup(ranked[preferred], ranked[0]))
                        {
                            var item = ranked[preferred];
                            ranked.RemoveAt(preferred);
                            ranked.Insert(0, item);
                        }
                    }
                    var attempts = 0;
                    while (attempts < options.MaxAttempts)
                    {
                        signal.ThrowIfCancellationRequested();
                        if (!IsCurrent(rev))
                            throw new OperationCanceledException("Superseded playback request.");
                        var available = ranked.Where(c => !tried.Contains(c.Identity) && Allowed(c)).ToList();
                        var responsive = available.Where(c => !blacklist.IsStalled(c.InfoHash)).ToList();
                        var pool = responsive.Count > 0 ? responsive : available;
                        if (pool.Count == 0)
                            break;
                        var top = pool[0];
                        // One candidate per info hash in a race: the torrent engine has one stream per torrent, so two
                        // files of the same hash would preempt each other. Duplicates stay in the pool for later rounds.
                        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        bool FirstOfHash(StreamCandidate c) => c.InfoHash == null || hashes.Add(c.InfoHash);
                        var group = pool.Where(c => SameGroup(c, top)).Where(FirstOfHash).Take(options.MaxAttempts - attempts).ToList();
                        var reserve = pool.Where(c => !group.Contains(c) && Validity(c) == Validity(top) && c.HealthBand == top.HealthBand)
                            .Where(FirstOfHash).Take(Math.Max(0, options.MaxAttempts - attempts - group.Count)).ToList();
                        SetStep(rev, "connecting", ranked.Count, attempts + 1);
                        var race = await CandidateRace.RunAsync<StreamCandidate, IOpenedSource>(group,
                            (candidate, lead, candidateToken) => PrepareCandidateAsync(candidate, plan, deadline, lead, candidateToken),
                            clock, options.Race, reserve, (candidate, opened) => SafeDispose(opened), signal,
                            racing => { if (IsCurrent(rev) && step == "connecting") { stepRacing = racing; RaiseStatus(); } }).ConfigureAwait(false);
                        if (race.Cut.Count > 0)
                            Log($"race: {race.Leader?.Identity} led after metadata with a healthy swarm; stopped {race.Cut.Count} other candidates");
                        foreach (var failure in race.Failures)
                        {
                            tried.Add(failure.Candidate.Identity);
                            failures.Add(new SourceFailure { Identity = failure.Candidate.Identity, Name = failure.Candidate.Name, Error = failure.Error.Message });
                            attempts++;
                        }
                        if (race.Failures.Count > 0)
                            deadline.Progress();
                        if (!race.HasWinner)
                            continue;
                        var winner = race.WinnerCandidate;
                        tried.Add(winner.Identity);
                        attempts++;
                        try
                        {
                            SetStep(rev, "opening", ranked.Count, attempts);
                            var result = await CommitAsync(plan, winner, race.Winner, anime, tried, subtitlesTask, rev, signal).ConfigureAwait(false);
                            result.FallbacksTried = failures;
                            return result;
                        }
                        catch (Exception error) when (!signal.IsCancellationRequested && IsCurrent(rev) && !(error is OperationCanceledException))
                        {
                            failures.Add(new SourceFailure { Identity = winner.Identity, Name = winner.Name, Error = error.Message });
                            deadline.Progress();
                        }
                    }
                    var quality = plan.Quality == null || plan.Quality == "auto" ? "" : " " + plan.Quality;
                    var message = failures.Count == 0
                        ? $"No viable{quality} streams found for {meta.Summary.Name}"
                        : $"All{quality} candidate streams failed: " + string.Join("; ", failures.Select(f => f.Error));
                    await AfterFailedStartAsync(plan, rev, message).ConfigureAwait(false);
                    if (IsCurrent(rev))
                    {
                        SetError(message);
                        if (active == null)
                            SetPhase(lastSession != null ? "ended" : "idle", null);
                        else
                            SetPhase(active.UserPaused ? "paused" : "playing", null);
                    }
                    return new PlayResult
                    {
                        Status = PlayStatus.Failed, Error = message, FallbacksTried = failures, MetaId = meta.Summary.Id,
                        Name = meta.Summary.Name, Video = VideoRef.From(video), Mode = plan.Mode,
                    };
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.Expired)
                {
                    var message = "Stream startup deadline exceeded"
                        + (failures.Count > 0 ? ": " + string.Join("; ", failures.Select(f => f.Error)) : "");
                    await AfterFailedStartAsync(plan, rev, message).ConfigureAwait(false);
                    if (IsCurrent(rev))
                    {
                        SetError(message);
                        SetPhase(active == null ? "idle" : "playing", null);
                    }
                    return new PlayResult { Status = PlayStatus.Failed, Error = message, FallbacksTried = failures, Mode = plan.Mode };
                }
                catch (OperationCanceledException)
                {
                    return new PlayResult { Status = PlayStatus.Superseded, FallbacksTried = failures, Mode = plan.Mode };
                }
            }
        }

        /// A start that failed after it touched the player. The TV has one decoder, so a replacement cannot be validated
        /// before the old media is released; when it fails, the session it replaced gets its source back at its last
        /// position, or ends with a terminal error. Never a black screen reported as playing.
        private async Task AfterFailedStartAsync(StartPlan plan, long rev, string message)
        {
            if (!IsCurrent(rev))
                return;
            var session = active;
            if (plan.PlayerReplaced && session != null)
            {
                if (plan.Recovery == null && plan.Reason != "episode_advance")
                    await EndIfCurrentAsync(session, rev, "start_failed").ConfigureAwait(false);
                else if (plan.Recovery != null && !plan.Starved)
                    await RestoreOrEndAsync(session, rev, message).ConfigureAwait(false);
                // An advance that failed is ended by its caller (advance_failed).
                return;
            }
            if (session == null && plan.PlayerTouched)
            {
                // Nothing plays: release a player a failed or abandoned prepare may still hold (decoder, audio output).
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (active == null && IsCurrent(rev))
                        SafeStop();
                }
                finally { gate.Release(); }
            }
        }

        /// Reopens the session's own source at its last confirmed position after a failed replacement released it.
        private async Task RestoreOrEndAsync(SessionRecord session, long rev, string why)
        {
            CancellationToken opToken;
            lock (revisionGate) opToken = operation.Token;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (active != session || !IsCurrent(rev))
                    return;
                var source = activeSource;
                Timeline("recovery", "restore previous source at " + session.LastPositionMs + " after: " + why);
                if (source != null && !(source is IPreemptibleSource preemptible && preemptible.Preempted))
                {
                    try
                    {
                        // Stop (or any newer user change) abandons the restore at once: the source it reopens is the one
                        // that was struggling, and its prepare must not hold the gate for the whole open budget.
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, opToken))
                        using (var budget = TaskHelpers.Budget(clock, options.PlayerOpenBudget, linked.Token))
                            await player.OpenAsync(source.PlayerUrl, session.LastPositionMs, budget.Token).WithCancellation(budget.Token).ConfigureAwait(false);
                        if (!session.UserPaused)
                            player.Play();
                        session.Health = null;
                        Log("recovery: replacement failed; the previous source is back at " + session.LastPositionMs / 1000 + " s");
                        Timeline("recovery", "restored");
                        return;
                    }
                    catch (OperationCanceledException) when (opToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                    {
                        Timeline("recovery", "restore abandoned (stop or newer command)");
                        return;   // Stop ends the session itself
                    }
                    catch (Exception error) { Log("restore: " + error.Message); }
                }
                session.Recovery = session.Recovery ?? new RecoveryState();
                session.Recovery.LastError = why;
                lastError = "stream_failed";
                Timeline("recovery", "restore failed, ending");
                await FinalizeLockedAsync("stream_failed").ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private async Task<IOpenedSource> PrepareCandidateAsync(StreamCandidate candidate, StartPlan plan, ProgressDeadline deadline,
            RaceLead lead, CancellationToken token)
        {
            var started = clock.UtcNow;
            try
            {
                IOpenedSource opened = null;
                for (var attempt = 0; ; attempt++)
                {
                    using (var budget = TaskHelpers.Budget(clock, options.PrepareBudget, token))
                    {
                        // Openers that report progress let a clearly healthy candidate claim the lead (race cut).
                        var openTask = services.Opener is IReportingSourceOpener reporting && lead != null
                            ? reporting.OpenAsync(candidate, progress => { if (options.Race.IsHealthyLead(progress)) lead.Claim(); }, budget.Token)
                            : services.Opener.OpenAsync(candidate, budget.Token);
                        try
                        {
                            opened = await openTask.WithCancellation(budget.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (OperationCanceledException) when (DisposeWhenDone(openTask))
                        {
                            throw;
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < options.PrepareRetries)
                        {
                            Log($"prepare budget expired for {candidate.Identity}; retrying once");
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new OpenFailure($"Source did not become ready within {options.PrepareBudget.TotalSeconds * (options.PrepareRetries + 1):0} s");
                        }
                    }
                }
                var maxSizeGb = EffectiveMaxSizeGb(plan.MaxSizeGb).Value;
                if (opened.LengthBytes > maxSizeGb * 1e9)
                {
                    SafeDispose(opened);
                    throw new OpenFailure("Source exceeds the requested size limit");
                }
                blacklist.Forget(candidate.InfoHash);
                deadline.Progress();
                return opened;
            }
            catch (Exception)
            {
                // G4: a failure after 10 s or more of preparation marks the swarm as stalled, unless the whole start
                // was cancelled (superseded or deadline) or the race cut this candidate for another one's lead.
                if (!deadline.Token.IsCancellationRequested && !(lead?.WasCut ?? false) && clock.UtcNow - started >= options.StallAfter)
                    blacklist.Remember(candidate.InfoHash);
                throw;
            }
        }

        private async Task<PlayResult> CommitAsync(StartPlan plan, StreamCandidate candidate, IOpenedSource opened, bool anime,
            HashSet<string> tried, Task<IReadOnlyList<SubtitleCandidate>> subtitlesTask, long rev, CancellationToken token)
        {
            var meta = plan.Meta;
            var video = plan.Video;
            var committed = false;
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (!IsCurrent(rev))
                        throw new OperationCanceledException("Superseded playback request.");
                    if (plan.RecoveringSession != null && active != plan.RecoveringSession)
                        throw new OperationCanceledException("The recovering session already ended.");
                    var resumeMs = plan.ResumeMs;
                    if (plan.RecoveringSession != null)
                        resumeMs = Math.Max(0, plan.RecoveringSession.LastPositionMs);
                    var preference = await preferences.GetAsync(meta.Summary.Id, token).ConfigureAwait(false);
                    var previous = active;
                    var previousSource = activeSource;
                    // The previous session stays active until the new source has opened and passed the audio check,
                    // so a failed replacement or advance is still ended by its own handler (stream_failed, advance_failed).
                    if (previous != null)
                        plan.PlayerReplaced = true;
                    plan.PlayerTouched = true;
                    {
                        using (var budget = TaskHelpers.Budget(clock, options.PlayerOpenBudget, token))
                        {
                            var playerTimer = System.Diagnostics.Stopwatch.StartNew();
                            Log($"timing: player open start (resume {resumeMs / 1000} s)");
                            try
                            {
                                await player.OpenAsync(opened.PlayerUrl, resumeMs, budget.Token).WithCancellation(budget.Token).ConfigureAwait(false);
                                Log($"timing: player opened {playerTimer.ElapsedMilliseconds} ms");
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            {
                                throw new OpenFailure($"The TV player did not open the source within {options.PlayerOpenBudget.TotalSeconds:0} s");
                            }
                        }
                        var hints = LanguageHints.FromCandidate(candidate, anime);
                        var audio = TrackRules.DecideAudio(SafePlayer(() => player.AudioTracks, Array.Empty<TrackInfo>()), anime, hints,
                            preference?.AudioLanguage);
                        if (!audio.Accept)
                        {
                            SafeStop();
                            throw new OpenFailure(audio.Reason);
                        }
                        if (audio.SelectIndex.HasValue)
                        {
                            try { player.SelectAudio(audio.SelectIndex.Value); }
                            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { Log("audio select: " + error.Message); }
                        }
                        // Nothing starts playing unseen (a replacement or advance that lands while the app is in the
                        // background), and a recovery of a paused session stays paused.
                        var startPaused = background || (plan.RecoveringSession?.UserPaused ?? false);
                        if (previous != null && active == previous)
                        {
                            await FinalizeLockedAsync(plan.Recovery != null ? "stream_recovery" : TransitionReason(plan.Reason), disposeSource: false)
                                .ConfigureAwait(false);
                            plan.RecoveringSession = null;
                        }
                        if (startPaused)
                            Log("timing: opened paused" + (background ? " (app in the background)" : ""));
                        else
                        {
                            player.Play();
                            Log("timing: play issued");
                        }
                        var session = new SessionRecord
                        {
                            Id = meta.Summary.Id + ":" + clock.UtcNow.ToUnixTimeMilliseconds(),
                            MetaId = meta.Summary.Id, MediaType = meta.Summary.Type, Name = meta.Summary.Name, IsAnime = anime,
                            VideoId = video.Id, Season = meta.Summary.Type == MediaType.Series ? (int?)video.Season : null,
                            Episode = meta.Summary.Type == MediaType.Series ? (int?)video.Episode : null, VideoTitle = video.Title,
                            Mode = plan.Mode, StopAfterCurrent = plan.StopAfterCurrent, Quality = plan.Quality ?? "auto",
                            MaxSizeGb = EffectiveMaxSizeGb(plan.MaxSizeGb), SourceIdentity = candidate.Identity, SourceName = candidate.Name,
                            SourceInfoHash = candidate.InfoHash?.ToLowerInvariant(),
                            SourceSizeBytes = opened.LengthBytes > 0 ? opened.LengthBytes : candidate.SizeBytes ?? 0,
                            StartedAt = clock.UtcNow, ResumeMs = resumeMs, LastPositionMs = resumeMs, LastSavedMs = resumeMs,
                            AudioLanguage = audio.Language, UserPaused = startPaused,
                            Recovery = new RecoveryState
                            {
                                // Like the old server, the cooldown stays on the session whose replacement failed;
                                // a successful switch starts with only the attempted list and the switch count.
                                Attempted = tried.ToList(), Switches = plan.Recovery?.Switches ?? 0, Failures = plan.Recovery?.Failures ?? 0,
                            },
                        };
                        active = session;
                        activeMeta = meta;
                        activeVideo = video;
                        activeCandidate = candidate;
                        activeSource = opened;
                        committed = true;
                        externalSubtitles = Array.Empty<SubtitleCandidate>();
                        pump.SetSource((Func<long, SubtitleCue>)null);
                        ResetSubtitleSync();
                        lastError = null;
                        if (previousSource != null && !ReferenceEquals(previousSource, opened))
                            SafeDispose(previousSource);
                        var snapshot = Snapshot(session);
                        Enqueue(session, t => services.Progress.PlaybackStartedAsync(snapshot.Meta, snapshot.Video, resumeMs, t));
                        await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                        SetPhase(startPaused ? "paused" : "playing", null);
                        PendingSubtitleWork = SubtitleWorkAsync(session, preference?.SubtitleLanguage, subtitlesTask, candidate, opened);
                        return new PlayResult
                        {
                            Status = PlayStatus.Playing, MetaId = meta.Summary.Id, Name = meta.Summary.Name, Video = VideoRef.From(video),
                            ResumeMs = resumeMs, Mode = plan.Mode, Source = GetStatus().Source, Queue = QueueStatus.From(queue),
                        };
                    }
                }
                finally { gate.Release(); }
            }
            finally
            {
                if (!committed)
                    SafeDispose(opened);
            }
        }

        /// An abandoned open may still succeed later; make sure its source is released. Always returns false.
        private bool DisposeWhenDone(Task<IOpenedSource> openTask)
        {
            openTask.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion)
                    SafeDispose(t.Result);
                else
                    TaskHelpers.Observe(t);
            }, TaskScheduler.Default);
            return false;
        }

        private static string TransitionReason(string reason) =>
            reason == "episode_advance" ? "episode_advance" : reason == "restore" ? "restore" : "user_play";

        // ================= subtitles (F14) =================

        private async Task<IReadOnlyList<SubtitleCandidate>> FetchSubtitlesAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            try
            {
                using (var budget = TaskHelpers.Budget(clock, options.ExternalSubtitleBudget, token))
                    return await services.Resolver.GetSubtitlesAsync(meta.Summary.Type, video.Id, null, budget.Token)
                        .WithCancellation(budget.Token).ConfigureAwait(false) ?? Array.Empty<SubtitleCandidate>();
            }
            catch (Exception error)
            {
                Log("subtitles: " + error.Message);
                return Array.Empty<SubtitleCandidate>();
            }
        }

        private async Task DecideSubtitlesAsync(SessionRecord session, string preferredLanguage,
            Task<IReadOnlyList<SubtitleCandidate>> subtitlesTask)
        {
            try
            {
                var embedded = SafePlayer(() => player.EmbeddedSubtitleTracks, Array.Empty<TrackInfo>());
                IReadOnlyList<SubtitleCandidate> external;
                if (TrackRules.EmbeddedSatisfiesFirstChoice(embedded, session.IsAnime, preferredLanguage))
                    external = subtitlesTask.IsCompleted ? subtitlesTask.Result : Array.Empty<SubtitleCandidate>();
                else if (TrackRules.EmbeddedHasAcceptable(embedded, session.IsAnime, preferredLanguage))
                {
                    var wait = clock.Delay(options.ExternalSubtitleShortWait, lifetime.Token);
                    await Task.WhenAny(subtitlesTask, wait).ConfigureAwait(false);
                    external = subtitlesTask.IsCompleted ? subtitlesTask.Result : Array.Empty<SubtitleCandidate>();
                }
                else
                    external = await subtitlesTask.ConfigureAwait(false);
                var decision = TrackRules.DecideSubtitle(embedded, external, session.IsAnime, preferredLanguage);
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (active != session || session.Subtitle?.Reason == "user")
                        return;
                    session.Subtitle = decision;
                    ApplyEmbeddedLocked(decision);
                    await PersistAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally { gate.Release(); }
                await LoadExternalAsync(session, decision, lifetime.Token).ConfigureAwait(false);
                // Keep the full list for the subtitle menu once it arrives.
                var all = await subtitlesTask.ConfigureAwait(false);
                if (active == session)
                    externalSubtitles = all;
                RaiseStatus();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log("subtitle decision: " + error.Message); }
        }

        /// Must hold the gate. Selects the embedded track (or none) and clears external cues until a file loads.
        private void ApplyEmbeddedLocked(SubtitleDecision decision)
        {
            try { subtitlePlayer?.SelectEmbeddedSubtitle(decision.Kind == SubtitleKind.Embedded ? decision.EmbeddedIndex : null); }
            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { Log("embedded subtitle: " + error.Message); }
            pump.SetSource((Func<long, SubtitleCue>)null);
        }

        /// Downloads and parses an external subtitle outside the gate (bounded by ExternalSubtitleLoadBudget), then
        /// applies it only if it is still the session's current choice.
        private async Task LoadExternalAsync(SessionRecord session, SubtitleDecision decision, CancellationToken token)
        {
            if (session == null || decision.Kind != SubtitleKind.External || decision.External == null || services.Subtitles == null)
                return;
            SubtitleText parsed;
            try
            {
                using (var budget = TaskHelpers.Budget(clock, options.ExternalSubtitleLoadBudget, token))
                {
                    try
                    {
                        var text = await services.Subtitles.LoadTextAsync(decision.External.Url, budget.Token).WithCancellation(budget.Token).ConfigureAwait(false);
                        parsed = new SubtitleText(text);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException($"did not load within {options.ExternalSubtitleLoadBudget.TotalSeconds:0} s");
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                Log("external subtitle: " + error.Message);
                decision.Reason = "load_failed: " + error.Message;
                RaiseStatus();
                return;
            }
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (active != session || !ReferenceEquals(session.Subtitle, decision))
                    return;   // a newer choice or another session won while this loaded
                pump.SetSource(parsed);
                pump.Timing = SubtitleTiming.Identity;
                pump.Update(SafePlayer(() => player.PositionMs, 0L));
                lock (syncGate) subtitleTiming = SubtitleTiming.Identity;
                OnExternalApplied(session, decision, parsed);
            }
            finally { gate.Release(); }
        }

        private void OnEmbeddedCue(object sender, SubtitleCue cue)
        {
            if (active?.Subtitle?.Kind == SubtitleKind.Embedded)
                SubtitleCue?.Invoke(this, cue);
        }

        // ================= finalize / persistence / helpers =================

        private void MarkWatched(SessionRecord session)
        {
            session.MarkedWatched = true;
            session.LastSavedMs = session.LastPositionMs;
            var snapshot = Snapshot(session);
            Enqueue(session, t => services.Progress.CompletedAsync(snapshot.Meta, snapshot.Video, t));
        }

        /// Must hold the gate.
        private async Task FinalizeLockedAsync(string reason, bool disposeSource = true)
        {
            var session = active;
            if (session == null)
                return;
            var snapshot = Snapshot(session);
            var marked = session.MarkedWatched;
            var finalProgress = session.CredibleSamples >= 2 && session.DurationMs > 0 && !marked;
            Enqueue(session, async t =>
            {
                if (finalProgress)
                    await services.Progress.ProgressAsync(snapshot.Meta, snapshot.Video, snapshot.Position, snapshot.Duration, t).ConfigureAwait(false);
                await services.Progress.StoppedAsync(snapshot.Meta, snapshot.Video, snapshot.Position, snapshot.Duration, marked, t).ConfigureAwait(false);
            });
            session.EndedAt = clock.UtcNow;
            session.EndReason = reason;
            active = null;
            lastSession = session;
            pump.SetSource((Func<long, SubtitleCue>)null);
            if (reason != "stream_recovery" && reason != "episode_advance" && reason != "user_play" && reason != "restore")
                SafeStop();
            if (disposeSource)
            {
                var source = activeSource;
                activeSource = null;
                activeCandidate = null;
                SafeDispose(source);
            }
            Checkpoint(session);
            if (session.Mode == PlaybackMode.Queue && !QueueTransitionReasons.Contains(reason))
            {
                var q = await EnsureQueueAsync(CancellationToken.None).ConfigureAwait(false);
                if (q != null && q.Status == QueueStatuses.Playing)
                {
                    q.Status = reason == UserStop ? QueueStatuses.Stopped : QueueStatuses.Interrupted;
                    q.LastStopReason = reason;
                    await PersistQueueAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            SetPhase("ended", null);
        }

        private async Task WithSessionAsync(Func<SessionRecord, Task> action, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var session = active ?? throw new InvalidOperationException("No active playback.");
                await action(session).ConfigureAwait(false);
                if (session == active)
                    SetPhase(session.UserPaused ? "paused" : "playing", null);
            }
            finally { gate.Release(); }
        }

        private (long, CancellationToken) UserChange()
        {
            lock (revisionGate)
            {
                revision++;
                var old = operation;
                operation = new CancellationTokenSource();
                try { old.Cancel(); } catch (AggregateException) { }
                old.Dispose();
                return (revision, operation.Token);
            }
        }

        private bool IsCurrent(long rev) => Interlocked.Read(ref revision) == rev;

        private async Task<QueueState> EnsureQueueAsync(CancellationToken token)
        {
            if (!queueLoaded)
            {
                var loaded = await JsonStore.ReadAsync<QueueState>(services.Store, PlaybackKeys.Queue, token).ConfigureAwait(false);
                if (!queueLoaded)
                {
                    queue = loaded;
                    queueLoaded = true;
                }
            }
            return queue;
        }

        private Task PersistQueueAsync(CancellationToken token) => JsonStore.WriteAsync(services.Store, PlaybackKeys.Queue, queue, token);

        /// Queues a checkpoint of the active session (the write happens off the gate; see Checkpoint).
        private Task PersistAsync(CancellationToken token)
        {
            var session = active;
            if (session != null)
                Checkpoint(session);
            return Task.CompletedTask;
        }

        /// Serializes now (callers hold the gate, so the record is consistent) and hands the text to the ordered writer.
        private void Checkpoint(SessionRecord record)
        {
            if (services.Store == null || record == null)
                return;
            string json;
            try { json = JsonStore.Serialize(record); }
            catch (Exception error) { Log("persist: " + error.Message); return; }
            lock (checkpointGate)
            {
                checkpointPending = json;
                if (checkpointActive)
                    return;
                checkpointActive = true;
                checkpointWriter = Task.Run(WriteCheckpointsAsync);
            }
        }

        private async Task WriteCheckpointsAsync()
        {
            while (true)
            {
                string json;
                lock (checkpointGate)
                {
                    json = checkpointPending;
                    checkpointPending = null;
                    if (json == null)
                    {
                        checkpointActive = false;
                        return;
                    }
                }
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try { await services.Store.WriteAsync(PlaybackKeys.Session, json, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { Log("persist: " + error.Message); }
                if (timer.ElapsedMilliseconds > 500)
                    Timeline("storage", "session checkpoint took " + timer.ElapsedMilliseconds + " ms");
            }
        }

        /// Completes when every queued session checkpoint is on storage (Stop, Save, tests).
        public Task FlushCheckpointsAsync()
        {
            lock (checkpointGate)
                return checkpointWriter;
        }

        /// E5: history writes run one after another; errors are kept on the session.
        private void Enqueue(SessionRecord session, Func<CancellationToken, Task> work)
        {
            if (services.Progress == null)
                return;
            lock (historyGate)
            {
                historyTail = historyTail.ContinueWith(async _ =>
                {
                    try { await work(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { session.HistoryError = error.Message; }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        /// Completes when every queued progress/history write has run.
        public Task FlushHistoryAsync()
        {
            lock (historyGate)
                return historyTail;
        }

        private struct HistorySnapshot
        {
            public MetaDetail Meta;
            public VideoInfo Video;
            public long Position;
            public long Duration;
        }

        private HistorySnapshot Snapshot(SessionRecord session) => new HistorySnapshot
        {
            Meta = activeMeta, Video = activeVideo, Position = session.LastPositionMs, Duration = session.DurationMs,
        };

        private sealed class MetaLookup
        {
            public MetaDetail Meta;
            public IReadOnlyList<MetaSummary> Ambiguous;
        }

        private async Task<MetaLookup> ResolveMetaAsync(string metaId, string query, MediaType? type, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(metaId) && !string.IsNullOrWhiteSpace(query)
                && System.Text.RegularExpressions.Regex.IsMatch(query.Trim(), @"^tt\d+$"))
                metaId = query.Trim();
            if (!string.IsNullOrWhiteSpace(metaId))
            {
                var types = type.HasValue ? new[] { type.Value } : new[] { MediaType.Series, MediaType.Movie };
                Exception last = null;
                foreach (var candidate in types)
                {
                    try
                    {
                        var meta = await services.Catalog.GetMetaAsync(candidate, metaId, token).ConfigureAwait(false);
                        if (meta?.Summary != null)
                            return new MetaLookup { Meta = meta };
                    }
                    catch (Exception error) when (!(error is OperationCanceledException)) { last = error; }
                }
                if (last != null)
                    throw last;
                return new MetaLookup();
            }
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("A title id or query is required.");
            var match = await PlayResolver.ResolveTitleAsync(services.Catalog, query, type, token).ConfigureAwait(false);
            if (match.Ambiguous != null)
                return new MetaLookup { Ambiguous = match.Ambiguous };
            if (match.Match == null)
                return new MetaLookup();
            return new MetaLookup { Meta = await services.Catalog.GetMetaAsync(match.Match.Type, match.Match.Id, token).ConfigureAwait(false) };
        }

        private static double? EffectiveMaxSizeGb(double? requested) =>
            requested > 0 ? Math.Min(requested.Value, 6.5) : 6.5;

        private static string Describe(MetaDetail meta, VideoInfo video) =>
            meta.Summary.Type == MediaType.Series ? $"{meta.Summary.Name} S{video.Season:00}E{video.Episode:00}" : meta.Summary.Name;

        private PlayResult Fail(string message) => new PlayResult { Status = PlayStatus.Failed, Error = message };

        private void SetError(string message)
        {
            lastError = message;
            Log(message);
        }

        private void SetPhase(string value, string what)
        {
            phase = value;
            pending = what;
            step = null;
            stepSources = stepTry = stepRacing = 0;
            RaiseStatus();
        }

        private void SetStep(long rev, string value, int sources = 0, int attempt = 0)
        {
            if (!IsCurrent(rev))
                return;
            step = value;
            stepSources = sources;
            stepTry = attempt;
            stepRacing = 0;
            RaiseStatus();
        }

        private void RestorePhase(long rev)
        {
            if (!IsCurrent(rev))
                return;
            var session = active;
            SetPhase(session == null ? (lastSession != null ? "ended" : "idle") : session.UserPaused ? "paused" : "playing", null);
        }

        private void RaiseStatus()
        {
            var handler = StatusChanged;
            if (handler == null)
                return;
            try { handler(this, GetStatus()); }
            catch (Exception error) { Log("status handler: " + error.Message); }
        }

        private void SafeStop()
        {
            try
            {
                var state = player.State;
                if (state != PlayerState.Idle && state != PlayerState.Stopped)
                    player.Stop();
            }
            catch (Exception error) { Log("player stop: " + error.Message); }
        }

        private void SafeDispose(IDisposable value)
        {
            if (value == null)
                return;
            try { value.Dispose(); }
            catch (Exception error) { Log("dispose: " + error.Message); }
        }

        private T SafePlayer<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }

        private void Log(string message) => services.Log?.Invoke(message);

        private static void Timeline(string kind, string detail) => PlaybackTimeline.Shared.Add(kind, detail);

        private async Task MonitorLoop()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await clock.Delay(options.MonitorInterval, lifetime.Token).ConfigureAwait(false);
                    await TickAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task SubtitleLoop()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await clock.Delay(options.SubtitleInterval, lifetime.Token).ConfigureAwait(false);
                    if (active != null)
                    {
                        SettleOffsetPoint();
                        pump.Update(SafePlayer(() => player.PositionMs, 0L));
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            try { lifetime.Cancel(); } catch (AggregateException) { }
            lock (revisionGate)
            {
                try { operation.Cancel(); } catch (AggregateException) { }
            }
            if (subtitlePlayer != null)
                subtitlePlayer.SubtitleCue -= OnEmbeddedCue;
            SafeDispose(activeSource);
            activeSource = null;
        }
    }
}
