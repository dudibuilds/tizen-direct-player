using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ElmSharp;
using Ru7100.Core.Trailers;
using Ru7100.Core.Ui;
using Tizen.Multimedia;
using TizenPlayer = Tizen.Multimedia.Player;
using TizenPlayerState = Tizen.Multimedia.PlayerState;

namespace Ru7100Probe.Trailer
{
    /// Lets the main player take the hardware decoder from the trailer before it creates its own player.
    internal static class TrailerGate
    {
        /// Called on the main loop at the top of TizenPlayerBackend.OpenAsync.
        public static Action BeforeMainPlayback;
    }

    /// A trailer player that tells the painter when its video is on the video plane, so the painter opens a hole
    /// in the UI only then (never a black hero while resolving or after a failure), and how much the still art
    /// should cover it (crossfades in and out).
    internal interface IVideoHoleSource
    {
        /// The video plane shows trailer frames inside HoleRect.
        bool ShowingVideo { get; }
        /// 1: the art under the trailer is fully drawn over the video; 0: the video shows through entirely.
        double CoverOpacity { get; }
        int HoleX { get; }
        int HoleY { get; }
        int HoleW { get; }
        int HoleH { get; }
    }

    /// Hero trailer previews on the RU7100: resolves the title's official trailer from IMDb on the TV
    /// (ImdbTrailerResolver, full-length H.264 + AAC MP4) and plays it with a second Tizen.Multimedia.Player in a
    /// region of interest. Media goes from IMDb's CDN straight to the TV's player.
    ///
    /// Behaviour (Netflix-style): once frames show, the picture crossfades in from the still art (300 ms) and the
    /// sound fades in over 1.5 s to the preview level (a fraction of full player volume, AVPlay Player.Volume, not the
    /// system volume). Hide fades picture and sound out over 300 ms, then releases the player. The preview plays
    /// once; at its end it fades out the same way and reports the end to the UI (UiServices.ReportTrailerEnded).
    ///
    /// Main loop only (the painter calls Show/SetMuted/Hide while painting). Resolve and prepare run asynchronously
    /// under a generation number: a Hide or newer Show discards stale work and releases a late-prepared player.
    /// The decoder is shared with the main player: Show refuses while the main player is busy, frees a stopped main
    /// player first, and TrailerGate releases the trailer at once (no fade) before main playback starts.
    internal sealed class TizenTrailerPlayer : ITrailerPlayer, IVideoHoleSource, IDisposable
    {
        private const double PollSeconds = 0.25;
        private const double FadeTickSeconds = 1.0 / 30;
        public const int PictureFadeMs = 300;
        public const int SoundFadeInMs = 1500;
        public const int FadeOutMs = 300;
        /// Preview sound level as a fraction of full player volume.
        public const float DefaultPreviewLevel = 0.6f;

        private readonly Window window;
        private readonly Action<Action> post;
        private readonly Func<bool> mainBusy;
        private readonly Action releaseIdleMain;
        private readonly Action<string> ended;
        private readonly Action<string> started;
        private readonly Action<string> log;
        private readonly ImdbTrailerResolver imdb = new ImdbTrailerResolver();
        private ImdbTrailer direct;   // the IMDb trailer playing, when IMDb had one
        private TizenPlayer player;
        /// Cancels the current preview's resolve requests (a Hide, a newer Show or main playback abandons it).
        private CancellationTokenSource resolving;
        /// Players already released while their prepare was still running: the prepare's late completion must not
        /// release them a second time (exactly-once teardown).
        private readonly System.Collections.Generic.HashSet<TizenPlayer> released = new System.Collections.Generic.HashSet<TizenPlayer>();
        private readonly System.Collections.Generic.HashSet<TizenPlayer> preparing = new System.Collections.Generic.HashSet<TizenPlayer>();
        private int generation;
        private bool muted;
        private Tizen.Multimedia.Rectangle area;   // hero rect from the layout
        private Tizen.Multimedia.Rectangle roi;    // video rect inside it (aspect kept)
        private IntPtr poll;
        private IntPtr fadeTimer;
        private readonly Stopwatch clock = new Stopwatch();
        private bool disposed;

        // Fades: linear ramps of cover opacity and volume, driven by fadeTimer.
        private readonly Stopwatch fadeClock = new Stopwatch();
        private double coverFrom = 1, coverTo = 1, coverMs = 1;
        private double volumeFrom, volumeTo, volumeMs = 1;
        private double volume;
        private bool fadingOut;
        private string fadeOutReason;

        // Diagnostics (read from any thread; written on the main loop).
        private string videoId;
        private string state = "idle";
        private string lastError;
        private long resolveMs = -1, prepareMs = -1, firstFrameMs = -1;
        private int shows, failures;
        private string lastEnd;
        private long failedAtMs = -1;

        /// mainBusy: true while the main player is starting or holds resumable media. releaseIdleMain: frees a
        /// stopped main player's decoder. ended(videoId): the preview finished by itself (end or failure). All run on
        /// the main loop. started(videoId): the trailer's first frames are on screen (the UI turns the trailer view compact).
        public TizenTrailerPlayer(Window window, Action<Action> post, Func<bool> mainBusy, Action releaseIdleMain, Action<string> ended, Action<string> log, Action<string> started = null)
        {
            this.window = window ?? throw new ArgumentNullException(nameof(window));
            this.post = post ?? (a => a());
            this.mainBusy = mainBusy ?? (() => false);
            this.releaseIdleMain = releaseIdleMain ?? (() => { });
            this.ended = ended ?? (_ => { });
            this.started = started ?? (_ => { });
            this.log = log ?? (_ => { });
            TrailerGate.BeforeMainPlayback = () => Stop("main playback");
        }

        /// Preview sound level, 0..1 of full player volume.
        public float PreviewLevel { get; set; } = DefaultPreviewLevel;

        /// Raised on the main loop whenever the painter must repaint: the hole opened or closed, or a fade stepped.
        public event Action VideoChanged;

        public bool ShowingVideo { get; private set; }
        public double CoverOpacity { get; private set; } = 1;
        public int HoleX => area.X;
        public int HoleY => area.Y;
        public int HoleW => area.Width;
        public int HoleH => area.Height;

        public long Position { get; private set; }

        public void Show(string titleId, string youTubeId, int x, int y, int w, int h, bool muted)
        {
            if (disposed)
                return;
            Stop(null);   // a preview still fading out goes at once
            var gen = generation;
            shows++;
            videoId = youTubeId;
            this.muted = muted;
            area = new Tizen.Multimedia.Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
            roi = area;
            direct = null;
            resolveMs = prepareMs = firstFrameMs = failedAtMs = -1;
            lastError = null;
            lastEnd = null;
            if (mainBusy())
            {
                state = "blocked: main playback active";
                return;
            }
            state = "resolving";
            clock.Restart();
            var cts = resolving = new CancellationTokenSource();
            var token = cts.Token;
            Task.Run(async () =>
            {
                try
                {
                    // Full-length 1080p from IMDb; titles without an IMDb trailer show their still art.
                    ImdbTrailer full = null;
                    try { full = await imdb.ResolveAsync(titleId, token).ConfigureAwait(false); }
                    catch (Exception error) when (!token.IsCancellationRequested) { log("trailer: imdb " + titleId + ": " + error.Message); }
                    // Abandoned (focus moved on, main playback): no YouTube fallback, no manifest, nothing posted.
                    token.ThrowIfCancellationRequested();
                    if (full != null)
                    {
                        post(() => StartDirect(gen, full));
                        return;
                    }
                    throw new TrailerUnavailableException(TrailerFailure.Unplayable, "No trailer on IMDb.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Superseded: Stop already moved on.
                }
                catch (Exception error)
                {
                    post(() => Fail(gen, error is TrailerUnavailableException t ? t.Reason + ": " + t.Message : error.GetType().Name + ": " + error.Message));
                }
            });
        }

        private void StartDirect(int gen, ImdbTrailer trailer)
        {
            if (gen != generation || disposed)
                return;
            direct = trailer;
            resolveMs = clock.ElapsedMilliseconds;
            if (mainBusy())
            {
                state = "blocked: main playback active";
                return;
            }
            try
            {
                releaseIdleMain();
                var created = new TizenPlayer();
                player = created;
                state = "preparing";
                created.Display = new Display(window);
                created.ErrorOccurred += OnError;
                created.PlaybackCompleted += OnCompleted;
                created.SetSource(new MediaUriSource(trailer.Url));
                SetVolume(created, 0);
                created.Muted = muted;
                log("trailer: imdb " + trailer.TitleId + " " + trailer.VideoId + " " + (trailer.IsHls ? "hls" : trailer.Height + "p mp4") + " " + trailer.RuntimeSeconds + " s");
                _ = PrepareAsync(gen, created);
            }
            catch (Exception error)
            {
                Fail(gen, "start: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private async Task PrepareAsync(int gen, TizenPlayer created)
        {
            Exception failure = null;
            preparing.Add(created);   // main loop: PrepareAsync is called from Start/StartDirect
            try { await created.PrepareAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            post(() =>
            {
                preparing.Remove(created);
                if (gen != generation || player != created || disposed)
                {
                    // Superseded while preparing. Stop may already have released it (it frees the decoder at once for
                    // main playback); release it here only if it has not been.
                    if (player != created && !released.Remove(created))
                        Release(created);
                    return;
                }
                if (failure != null)
                {
                    Fail(gen, "prepare: " + failure.GetType().Name + ": " + failure.Message);
                    return;
                }
                prepareMs = clock.ElapsedMilliseconds;
                try
                {
                    roi = Fit(area, direct?.Width ?? 0, direct?.Height ?? 0);
                    created.DisplaySettings.SetRoi(roi);
                    created.DisplaySettings.Mode = PlayerDisplayMode.Roi;
                    created.DisplaySettings.IsVisible = true;
                    created.Muted = muted;
                    created.Start();
                    state = "playing";
                    poll = EcoreMainloop.AddTimer(PollSeconds, () => Poll(gen));
                }
                catch (Exception error)
                {
                    Fail(gen, "start: " + error.GetType().Name + ": " + error.Message);
                }
            });
        }

        /// The largest rect of the video's aspect centred in the hero rect (a 2.40:1 trailer is letterboxed inside
        /// it instead of stretched).
        internal static Tizen.Multimedia.Rectangle Fit(Tizen.Multimedia.Rectangle area, int videoW, int videoH)
        {
            if (videoW <= 0 || videoH <= 0 || area.Width <= 0 || area.Height <= 0)
                return area;
            var w = area.Width;
            var h = (int)Math.Round((double)w * videoH / videoW);
            if (h > area.Height)
            {
                h = area.Height;
                w = (int)Math.Round((double)h * videoW / videoH);
            }
            return new Tizen.Multimedia.Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }

        /// Watches the position: the picture and sound fade in once frames advance; the preview fades out just
        /// before the manifest's cap.
        private bool Poll(int gen)
        {
            if (gen != generation || player == null)
                return false;
            long position;
            try { position = player.State == TizenPlayerState.Playing ? player.GetPlayPosition() : 0; }
            catch (Exception) { position = 0; }
            Position = position;
            if (!ShowingVideo && position > 0)
            {
                firstFrameMs = clock.ElapsedMilliseconds;
                ShowingVideo = true;
                Fade(0, PictureFadeMs, PreviewLevel, SoundFadeInMs);
                started(videoId);
            }
            return true;
        }

        public void SetMuted(bool muted)
        {
            if (this.muted == muted)
                return;
            this.muted = muted;
            try
            {
                if (player != null)
                {
                    if (!muted)
                        SetVolume(player, (float)volume);   // the fade skipped volume writes while muted
                    player.Muted = muted;
                }
            }
            catch (Exception error) { lastError = "mute: " + error.Message; }
        }

        public void Hide()
        {
            if (player != null && ShowingVideo && !fadingOut)
            {
                FadeOut(null, false);
                return;
            }
            if (fadingOut)
                return;   // already on its way out
            Stop(null);
            if (videoId != null && state != "ended" && !state.StartsWith("error", StringComparison.Ordinal))
                state = "hidden";
        }

        /// Fades picture and sound out, then releases. `report`: the preview ended by itself (tell the UI).
        private void FadeOut(string reason, bool report)
        {
            generation++;   // pending resolve/prepare work and the position poll stop here
            CancelResolve();
            if (poll != IntPtr.Zero)
            {
                EcoreMainloop.RemoveTimer(poll);
                poll = IntPtr.Zero;
            }
            fadingOut = true;
            fadeOutReason = reason;
            reportOnRelease = report ? videoId : null;
            state = report ? "ending" : "fading out";
            Fade(1, FadeOutMs, 0, FadeOutMs);
        }

        private string reportOnRelease;

        private void Fade(double cover, double coverDurationMs, double vol, double volumeDurationMs)
        {
            coverFrom = CoverOpacity;
            coverTo = cover;
            coverMs = Math.Max(1, coverDurationMs);
            volumeFrom = volume;
            volumeTo = vol;   // Player.Muted is separate: the level ramps either way
            volumeMs = Math.Max(1, volumeDurationMs);
            fadeClock.Restart();
            if (fadeTimer == IntPtr.Zero)
                fadeTimer = EcoreMainloop.AddTimer(FadeTickSeconds, FadeStep);
            FadeStep();
        }

        private bool FadeStep()
        {
            var t = fadeClock.Elapsed.TotalMilliseconds;
            var c = Math.Min(1, t / coverMs);
            var v = Math.Min(1, t / volumeMs);
            var cover = coverFrom + (coverTo - coverFrom) * c;
            var pictureChanged = cover != CoverOpacity;
            CoverOpacity = cover;
            volume = volumeFrom + (volumeTo - volumeFrom) * v;
            // Muted: the level keeps ramping (SetMuted applies it on unmute) without a native write every step.
            if (player != null && !muted)
                SetVolume(player, (float)volume);
            // Only a picture step needs the painter. The sound fade runs ~1.2 s past the picture's and used to force a
            // full repaint (sample, lower, paint) on every 33 ms step.
            if (pictureChanged)
                Changed();
            if (c < 1 || v < 1)
                return true;
            fadeTimer = IntPtr.Zero;
            if (fadingOut)
            {
                var reason = fadeOutReason;
                var report = reportOnRelease;
                Stop(null);
                lastEnd = reason ?? lastEnd;
                state = report != null ? "ended" : "hidden";
                if (report != null)
                    ended(report);
            }
            return false;
        }

        private void SetVolume(TizenPlayer target, float level)
        {
            try { target.Volume = Math.Max(0f, Math.Min(1f, level)); }
            catch (Exception error) when (error is InvalidOperationException || error is ObjectDisposedException || error is ArgumentException) { lastError = "volume: " + error.Message; }
        }

        /// Ends the current preview at once (no fade) and invalidates pending work. Main loop only.
        private void CancelResolve()
        {
            var cts = resolving;
            resolving = null;
            if (cts != null)
            {
                try { cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void Stop(string reason)
        {
            generation++;
            CancelResolve();
            if (poll != IntPtr.Zero)
            {
                EcoreMainloop.RemoveTimer(poll);
                poll = IntPtr.Zero;
            }
            if (fadeTimer != IntPtr.Zero)
            {
                EcoreMainloop.RemoveTimer(fadeTimer);
                fadeTimer = IntPtr.Zero;
            }
            fadingOut = false;
            reportOnRelease = null;
            var old = player;
            player = null;
            if (old != null)
            {
                if (preparing.Contains(old))
                    released.Add(old);   // its prepare is still running: PrepareAsync's completion must not release it again
                Release(old);
                if (reason != null)
                {
                    lastEnd = reason;
                    state = "released: " + reason;
                }
            }
            Position = 0;
            volume = 0;
            var wasShowing = ShowingVideo;
            ShowingVideo = false;
            CoverOpacity = 1;
            if (wasShowing)
                Changed();
        }

        private void Fail(int gen, string message)
        {
            if (gen != generation)
                return;
            failures++;
            failedAtMs = Position;
            var id = videoId;
            Stop(null);
            lastError = message;
            state = "error";
            log("trailer: " + id + " failed: " + message);
            ended(id);   // the page leaves the trailer view instead of waiting over still art
        }

        private void OnError(object sender, PlayerErrorOccurredEventArgs args)
        {
            var gen = generation;
            post(() => { if (sender == player) Fail(gen, "player: " + args.Error); });
        }

        private void OnCompleted(object sender, EventArgs args)
        {
            post(() =>
            {
                if (sender == player && !fadingOut)
                    FadeOut("completed", true);
            });
        }

        private void Changed()
        {
            try { VideoChanged?.Invoke(); }
            catch (Exception error) { log("trailer: repaint: " + error.Message); }
        }

        private void Release(TizenPlayer old)
        {
            old.ErrorOccurred -= OnError;
            old.PlaybackCompleted -= OnCompleted;
            try
            {
                var s = old.State;
                if (s == TizenPlayerState.Playing || s == TizenPlayerState.Paused)
                    old.Stop();
                if (old.State != TizenPlayerState.Idle)
                    old.Unprepare();
            }
            catch (Exception error) when (error is InvalidOperationException || error is ObjectDisposedException) { log("trailer: release: " + error.Message); }
            try { old.Dispose(); }
            catch (Exception error) { log("trailer: dispose: " + error.Message); }
        }

        /// Trailer diagnostics for the admin API (dev.diagnostics → ui.trailer).
        public object Diagnostics
        {
            get
            {
                float? playerVolume = null;
                try { playerVolume = player?.Volume; } catch (Exception) { }
                return new
                {
                    state,
                    videoId,
                    muted,
                    showingVideo = ShowingVideo,
                    coverOpacity = Math.Round(CoverOpacity, 2),
                    volume = Math.Round(volume, 2),
                    playerVolume,
                    previewLevel = PreviewLevel,
                    positionMs = Position,
                    resolveMs,
                    prepareMs,
                    firstFrameMs,
                    lastError,
                    lastEnd,
                    failedAtMs,
                    shows,
                    failures,
                    roi = roi.Width + "x" + roi.Height + "@" + roi.X + "," + roi.Y,
                };
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            Stop(null);
            disposed = true;
            TrailerGate.BeforeMainPlayback = null;
        }
    }
}
