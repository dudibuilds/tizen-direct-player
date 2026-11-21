using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Playback;
using Tizen.Multimedia;
using CorePlayerState = Ru7100.Core.PlayerState;
using CoreTrackInfo = Ru7100.Core.TrackInfo;
using TizenPlayer = Tizen.Multimedia.Player;
using TizenPlayerState = Tizen.Multimedia.PlayerState;

namespace Ru7100Probe
{
    /// IPlayerBackend over Tizen.Multimedia.Player (the TV's hardware player).
    /// - Player mutations run on the UI main loop through `post` (as Program.cs does with its pending queue).
    /// - Embedded text subtitles are never drawn natively: the selected track's cues are raised through
    ///   SubtitleCue (Player.SubtitleUpdated) for the app-drawn subtitle layer. Their timing offset uses
    ///   Player.SetSubtitleOffset, because an app-side offset could only delay cues that arrive at presentation time.
    ///   External subtitle files go through the core SubtitleCuePump instead.
    /// - The video fills the screen (letterboxed) unless SetVideoArea sets a region of interest.
    public sealed class TizenPlayerBackend : IPlayerBackend, ISubtitleCapablePlayer, IVideoAreaPlayer, IInterruptiblePlayer, IVideoFormatSource, IDisposable
    {
        private readonly ElmSharp.Window window;
        private readonly Action<Action> post;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private TizenPlayer player;
        private CorePlayerState? overrideState;
        private bool buffering;
        private int durationMs;
        private IReadOnlyList<CoreTrackInfo> audioTracks = Array.Empty<CoreTrackInfo>();
        private IReadOnlyList<CoreTrackInfo> subtitleTracks = Array.Empty<CoreTrackInfo>();
        private int? selectedSubtitle;
        private int subtitleOffsetMs;
        private int? videoFps;

        /// Frames per second of the opened video as the player reports it (whole numbers: 23 for 23.976).
        public int? VideoFps => videoFps;
        private Tizen.Multimedia.Rectangle? roi;
        private bool disposed;
        private string interruption;
        private int lastBufferingBucket = -1;

        public string TakeInterruption()
        {
            lock (gate)
            {
                var reason = interruption;
                interruption = null;
                return reason;
            }
        }

        /// window: the app's main window. post: runs an action on the UI main loop (null = call directly).
        public TizenPlayerBackend(ElmSharp.Window window, Action<Action> post = null, Action<string> log = null)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            this.post = post;
            this.log = log;
        }

        public event EventHandler<CorePlayerState> StateChanged;
        public event EventHandler<SubtitleCue> SubtitleCue;

        public CorePlayerState State
        {
            get
            {
                lock (gate)
                {
                    if (overrideState.HasValue)
                        return overrideState.Value;
                    if (player == null)
                        return CorePlayerState.Idle;
                }
                try
                {
                    switch (player.State)
                    {
                        case TizenPlayerState.Idle: return CorePlayerState.Idle;
                        case TizenPlayerState.Preparing: return CorePlayerState.Preparing;
                        case TizenPlayerState.Ready: return CorePlayerState.Paused;
                        case TizenPlayerState.Paused: return CorePlayerState.Paused;
                        case TizenPlayerState.Playing: return buffering ? CorePlayerState.Buffering : CorePlayerState.Playing;
                        default: return CorePlayerState.Idle;
                    }
                }
                catch (ObjectDisposedException) { return CorePlayerState.Idle; }
            }
        }

        public long PositionMs
        {
            get
            {
                var current = player;
                if (current == null)
                    return 0;
                try
                {
                    var state = current.State;
                    return state == TizenPlayerState.Playing || state == TizenPlayerState.Paused || state == TizenPlayerState.Ready
                        ? current.GetPlayPosition() : 0;
                }
                catch (Exception error) when (error is InvalidOperationException || error is ObjectDisposedException) { return 0; }
            }
        }

        public long DurationMs => durationMs;
        public IReadOnlyList<CoreTrackInfo> AudioTracks => audioTracks;
        public IReadOnlyList<CoreTrackInfo> EmbeddedSubtitleTracks => subtitleTracks;

        public int? SelectedAudio
        {
            get
            {
                try { return audioTracks.Count == 0 ? (int?)null : player?.AudioTrackInfo.Selected; }
                catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException || error is ObjectDisposedException) { return null; }
            }
        }

        public Task OpenAsync(string url, long startMs, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("A media URL is required.", nameof(url));
            return OnMainAsync(async () =>
            {
                // A request superseded while it waited for the main loop (a newer channel, a newer source) touches
                // nothing: no trailer teardown, no release of the player that is showing now.
                token.ThrowIfCancellationRequested();
                // The TV has one hardware video decoder: the hero trailer gives it up before the main player is created.
                try { Ru7100Probe.Trailer.TrailerGate.BeforeMainPlayback?.Invoke(); }
                catch (Exception error) { Log("trailer release: " + error.Message); }
                ReleasePlayer();
                token.ThrowIfCancellationRequested();
                var created = new TizenPlayer();
                lock (gate)
                {
                    player = created;
                    interruption = null;
                    overrideState = CorePlayerState.Preparing;
                    buffering = false;
                    durationMs = 0;
                    audioTracks = Array.Empty<CoreTrackInfo>();
                    subtitleTracks = Array.Empty<CoreTrackInfo>();
                    selectedSubtitle = null;
                }
                RaiseState();
                try
                {
                    created.Display = new Display(window);
                    created.ErrorOccurred += OnError;
                    created.PlaybackCompleted += OnCompleted;
                    created.BufferingProgressChanged += OnBuffering;
                    created.SubtitleUpdated += OnSubtitle;
                    created.PlaybackInterrupted += OnInterrupted;
                    created.SetSource(new MediaUriSource(url));
                    // A trailer still mid-prepare can hold the single hardware decoder for a moment after the gate
                    // released it; the main prepare then fails with InvalidOperation. Release it again and retry.
                    for (var attempt = 0; ; attempt++)
                    {
                        try
                        {
                            await created.PrepareAsync().ConfigureAwait(true);
                            break;
                        }
                        catch (InvalidOperationException error) when (attempt < 2 && !token.IsCancellationRequested && player == created)
                        {
                            Log($"prepare busy ({error.Message}), retrying after releasing the trailer");
                            try { Ru7100Probe.Trailer.TrailerGate.BeforeMainPlayback?.Invoke(); } catch (Exception) { }
                            await Task.Delay(1000, token).ConfigureAwait(true);
                            try { if (created.State != TizenPlayerState.Idle) created.Unprepare(); } catch (Exception) { }
                        }
                    }
                    if (token.IsCancellationRequested || player != created)
                    {
                        if (player == created)
                            ReleasePlayer();
                        token.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("A newer source replaced this one.");
                    }
                    try { durationMs = created.StreamInfo.GetDuration(); }
                    catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { durationMs = 0; }
                    try { var fps = created.StreamInfo.GetVideoProperties().Fps; videoFps = fps > 0 ? (int?)fps : null; Log("video fps " + fps); }
                    catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { videoFps = null; Log("video fps: " + error.Message); }
                    audioTracks = ReadTracks(created.AudioTrackInfo);
                    subtitleTracks = ReadTracks(created.SubtitleTrackInfo);
                    ApplyDisplay(created);
                    if (startMs > 0 && (durationMs <= 0 || startMs < durationMs - 1000))
                        await created.SetPlayPositionAsync((int)Math.Min(int.MaxValue, startMs), false).ConfigureAwait(true);
                    lock (gate)
                        overrideState = null;
                    RaiseState();
                }
                catch
                {
                    if (player == created)
                        ReleasePlayer();
                    throw;
                }
            });
        }

        public void Play() => OnMain(() =>
        {
            var current = player;
            if (current == null)
                return;
            var state = current.State;
            if (state == TizenPlayerState.Ready || state == TizenPlayerState.Paused)
            {
                try { current.Start(); }
                catch (Exception error) when (error is InvalidOperationException || error is ObjectDisposedException || error is NotSupportedException)
                {
                    // Play is not acknowledged by the native player: report Error so the session recovers or ends
                    // instead of showing a paused, black screen as playing.
                    Log("start failed: " + error.Message);
                    lock (gate)
                        overrideState = CorePlayerState.Error;
                    RaiseState();
                    return;
                }
                lock (gate)
                    overrideState = null;
                ApplySubtitleOffset(current);
                RaiseState();
            }
        });

        public void Pause() => OnMain(() =>
        {
            var current = player;
            if (current?.State == TizenPlayerState.Playing)
            {
                current.Pause();
                RaiseState();
            }
        });

        public Task SeekAsync(long positionMs, CancellationToken token) => OnMainAsync(async () =>
        {
            var current = player ?? throw new InvalidOperationException("No media is open.");
            var state = current.State;
            if (state != TizenPlayerState.Ready && state != TizenPlayerState.Playing && state != TizenPlayerState.Paused)
                throw new InvalidOperationException("The player cannot seek in state " + state + ".");
            var target = (int)Math.Max(0, Math.Min(positionMs, durationMs > 0 ? durationMs - 1000 : positionMs));
            // Pausing around the seek matches the probe, which avoided the player racing ahead of the new range.
            var resume = state == TizenPlayerState.Playing;
            if (resume)
                current.Pause();
            // The native seek has no deadline of its own: the session bounds it with the token. An abandoned seek leaves
            // the player paused (never restarted here) for the session to reopen or stop.
            var landed = false;
            try
            {
                var seek = current.SetPlayPositionAsync(target, false);
                if (token.CanBeCanceled && !seek.IsCompleted)
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (token.Register(() => cancelled.TrySetResult(true)))
                    {
                        if (await Task.WhenAny(seek, cancelled.Task).ConfigureAwait(true) != seek)
                        {
                            _ = seek.ContinueWith(t => { var _ = t.Exception; }, TaskScheduler.Default);
                            Log($"seek to {target} ms abandoned");
                            throw new OperationCanceledException(token);
                        }
                    }
                }
                await seek.ConfigureAwait(true);
                landed = true;
            }
            finally
            {
                if (landed && resume && player == current && current.State == TizenPlayerState.Paused)
                    current.Start();
            }
            lock (gate)
                if (overrideState == CorePlayerState.Completed)
                    overrideState = null;
        });

        public void Stop() => OnMain(() =>
        {
            if (player == null)
                return;
            // Fully release (stop + unprepare + dispose): a stopped-but-prepared AVPlay keeps the TV's audio output and
            // decoder, so a trailer player started afterwards played video with no sound.
            ReleasePlayer();
            lock (gate)
                overrideState = CorePlayerState.Stopped;
            RaiseState();
        });

        public void SelectAudio(int index) => OnMain(() =>
        {
            var current = player;
            if (current == null || index < 0 || index >= audioTracks.Count)
                return;
            try { current.AudioTrackInfo.Selected = index; }
            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { Log("audio: " + error.Message); }
        });

        public void SelectEmbeddedSubtitle(int? index) => OnMain(() =>
        {
            lock (gate)
                selectedSubtitle = index;
            var current = player;
            if (current == null || !index.HasValue || index.Value < 0 || index.Value >= subtitleTracks.Count)
            {
                // Tizen has no "subtitles off": cues of an unselected track are simply not forwarded.
                SubtitleCue?.Invoke(this, Ru7100.Core.Playback.SubtitleCue.Clear);
                return;
            }
            try { current.SubtitleTrackInfo.Selected = index.Value; }
            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { Log("subtitle: " + error.Message); }
        });

        public void SetEmbeddedSubtitleOffset(int offsetMs) => OnMain(() =>
        {
            subtitleOffsetMs = TrackRules.ClampSubtitleOffset(offsetMs);
            var current = player;
            if (current != null)
                ApplySubtitleOffset(current);
        });

        public void SetFullscreen() => OnMain(() =>
        {
            roi = null;
            if (player != null)
                ApplyDisplay(player);
        });

        public void SetVideoArea(int x, int y, int width, int height) => OnMain(() =>
        {
            roi = new Tizen.Multimedia.Rectangle(x, y, width, height);
            if (player != null)
                ApplyDisplay(player);
        });

        // ---------------- internals ----------------

        private void ApplyDisplay(TizenPlayer current)
        {
            try
            {
                if (roi.HasValue)
                {
                    current.DisplaySettings.SetRoi(roi.Value);
                    current.DisplaySettings.Mode = PlayerDisplayMode.Roi;
                }
                else
                    current.DisplaySettings.Mode = PlayerDisplayMode.LetterBox;
                current.DisplaySettings.IsVisible = true;
            }
            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException || error is ArgumentException)
            {
                Log("display: " + error.Message);
            }
        }

        private void ApplySubtitleOffset(TizenPlayer current)
        {
            try
            {
                var state = current.State;
                if (state == TizenPlayerState.Playing || state == TizenPlayerState.Paused)
                    current.SetSubtitleOffset(subtitleOffsetMs);
            }
            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { Log("subtitle offset: " + error.Message); }
        }

        private IReadOnlyList<CoreTrackInfo> ReadTracks(PlayerTrackInfo info)
        {
            try
            {
                var count = info.GetCount();
                var tracks = new List<CoreTrackInfo>(count);
                for (var index = 0; index < count; index++)
                {
                    string code = null;
                    try { code = info.GetLanguageCode(index); }
                    catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException) { }
                    tracks.Add(new CoreTrackInfo { Index = index, Language = code, Label = Label(code, index) });
                }
                return tracks;
            }
            catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException)
            {
                Log("tracks: " + error.Message);
                return Array.Empty<CoreTrackInfo>();
            }
        }

        private static string Label(string code, int index)
        {
            if (string.IsNullOrWhiteSpace(code) || code == "und")
                return "Track " + (index + 1);
            try { return CultureInfo.GetCultureInfo(code).NativeName; }
            catch (CultureNotFoundException) { return code; }
        }

        private void OnError(object sender, PlayerErrorOccurredEventArgs args)
        {
            if (sender != player)
                return;
            Log("player error: " + args.Error);
            lock (gate)
                overrideState = CorePlayerState.Error;
            RaiseState();
        }

        /// The platform paused or released the player for another app (resource conflict). Runs on a player thread.
        private void OnInterrupted(object sender, PlaybackInterruptedEventArgs args)
        {
            if (sender != player)
                return;
            Log("playback interrupted: " + args.Reason);
            lock (gate)
                interruption = args.Reason.ToString();
            RaiseState();
        }

        private void OnCompleted(object sender, EventArgs args)
        {
            if (sender != player)
                return;
            lock (gate)
                overrideState = CorePlayerState.Completed;
            RaiseState();
        }

        private void OnBuffering(object sender, BufferingProgressChangedEventArgs args)
        {
            if (sender != player)
                return;
            var now = args.Percent < 100;
            if (now != buffering || (now && args.Percent / 25 != lastBufferingBucket))
                PlaybackTimeline.Shared.Add("buffering", args.Percent + "%" + (now != buffering ? (now ? " start" : " end") : ""));
            lastBufferingBucket = args.Percent / 25;
            if (now == buffering)
                return;
            buffering = now;
            RaiseState();
        }

        private void OnSubtitle(object sender, SubtitleUpdatedEventArgs args)
        {
            // Runs on a player-internal thread.
            if (sender != player || !selectedSubtitle.HasValue)
                return;
            long duration;
            try { duration = Convert.ToInt64(args.Duration, CultureInfo.InvariantCulture); }
            catch (Exception) { duration = 0; }
            SubtitleCue?.Invoke(this, new SubtitleCue
            {
                Text = args.Text ?? "", StartMs = PositionMs, DurationMs = duration, Embedded = true,
            });
        }

        private void ReleasePlayer()
        {
            TizenPlayer old;
            lock (gate)
            {
                old = player;
                player = null;
            }
            if (old == null)
                return;
            old.ErrorOccurred -= OnError;
            old.PlaybackCompleted -= OnCompleted;
            old.BufferingProgressChanged -= OnBuffering;
            old.SubtitleUpdated -= OnSubtitle;
            old.PlaybackInterrupted -= OnInterrupted;
            try
            {
                var state = old.State;
                if (state == TizenPlayerState.Playing || state == TizenPlayerState.Paused)
                    old.Stop();
                if (old.State != TizenPlayerState.Idle)
                    old.Unprepare();
            }
            catch (Exception error) when (error is InvalidOperationException || error is ObjectDisposedException) { Log("release: " + error.Message); }
            try { old.Dispose(); }
            catch (Exception error) { Log("dispose: " + error.Message); }
        }

        /// Main loop only. True while the main player is starting or holds media the user may resume (playing,
        /// paused, buffering): a trailer must not take the decoder then.
        public bool IsBusy
        {
            get
            {
                var state = State;
                return state == CorePlayerState.Preparing || state == CorePlayerState.Playing || state == CorePlayerState.Paused || state == CorePlayerState.Buffering;
            }
        }

        /// Main loop only. Frees the hardware decoder held by a stopped, finished or failed player (the reported state
        /// stays Stopped/Completed/Error), so a trailer can use it. No-op while busy.
        public void ReleaseIfIdle()
        {
            if (player == null || IsBusy)
                return;
            ReleasePlayer();
        }

        private void RaiseState()
        {
            try { StateChanged?.Invoke(this, State); }
            catch (Exception error) { Log("state handler: " + error.Message); }
        }

        private void OnMain(Action action)
        {
            void Run()
            {
                if (disposed)
                    return;
                try { action(); }
                catch (Exception error) { Log("player: " + error.GetType().Name + " " + error.Message); }
            }
            if (post == null)
                Run();
            else
                post(Run);
        }

        private Task OnMainAsync(Func<Task> work)
        {
            if (post == null)
                return work();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() =>
            {
                if (disposed)
                {
                    done.TrySetException(new ObjectDisposedException(nameof(TizenPlayerBackend)));
                    return;
                }
                Task task;
                try { task = work(); }
                catch (Exception error)
                {
                    done.TrySetException(error);
                    return;
                }
                task.ContinueWith(t =>
                {
                    if (t.IsCanceled) done.TrySetCanceled();
                    else if (t.IsFaulted) done.TrySetException(t.Exception.InnerExceptions);
                    else done.TrySetResult(true);
                }, TaskScheduler.Default);
            });
            return done.Task;
        }

        private void Log(string message)
        {
            if (log != null)
                log(message);
            else
                Tizen.Log.Warn("RU7100Player", message);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            void Release()
            {
                ReleasePlayer();
                disposed = true;
            }
            if (post == null)
                Release();
            else
                post(Release);
        }
    }
}
