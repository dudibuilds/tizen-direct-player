using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;

namespace Ru7100.Preview
{
    /// IPlayerBackend backed by the preview page's <video> element (dev-only, Mac). The real PlaybackSession opens a
    /// source through the TV's TorrentSourceOpener; this backend hands the loopback URL to the page (proxied through
    /// the preview server for same-origin range requests) and mirrors what the page reports back.
    /// The TV keeps playing straight from the source with AVPlay; nothing here is on that path.
    internal sealed class BrowserPlayer : IPlayerBackend, ISubtitleCapablePlayer, IVideoFollower
    {
        public const int OpenTimeoutMs = 120_000;

        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private PlayerState state = PlayerState.Idle;
        private string url;
        private bool wantPlaying;
        private long reportedPositionMs;
        private long reportedAtMs;
        private long durationMs;
        private int seekSeq;
        private long seekTargetMs;
        private int openSeq;
        private string error;
        private TaskCompletionSource<bool> ready;

        public event EventHandler<PlayerState> StateChanged;
        public event EventHandler<SubtitleCue> SubtitleCue { add { } remove { } }

        public PlayerState State { get { lock (gate) return state; } }
        public long DurationMs { get { lock (gate) return durationMs; } }
        public long PositionMs
        {
            get
            {
                lock (gate)
                {
                    var extra = state == PlayerState.Playing ? clock.ElapsedMilliseconds - reportedAtMs : 0;
                    var position = reportedPositionMs + Math.Max(0, Math.Min(extra, 1500));
                    return durationMs > 0 ? Math.Min(position, durationMs) : position;
                }
            }
        }

        // Browsers don't expose a stream's audio or embedded subtitle tracks (Chrome); external add-on subtitles
        // still render through the app subtitle layer.
        public IReadOnlyList<TrackInfo> AudioTracks { get; } = Array.Empty<TrackInfo>();
        public IReadOnlyList<TrackInfo> EmbeddedSubtitleTracks { get; } = Array.Empty<TrackInfo>();
        public int? SelectedAudio => null;
        public string Error { get { lock (gate) return error; } }
        /// The last source Chrome could not play, kept across the session's switch to the next source (OpenAsync clears
        /// Error) so the start card can say why it is trying another. Cleared once a picture plays.
        public string LastFailure { get { lock (gate) return lastFailure; } }
        public void ClearLastFailure() { lock (gate) lastFailure = null; }
        private string lastFailure;

        public async Task OpenAsync(string sourceUrl, long startMs, CancellationToken token)
        {
            TaskCompletionSource<bool> wait;
            lock (gate)
            {
                url = sourceUrl;
                error = null;
                durationMs = 0;
                reportedPositionMs = startMs;
                reportedAtMs = clock.ElapsedMilliseconds;
                seekTargetMs = startMs;
                seekSeq++;
                openSeq++;
                wantPlaying = false;
                ready = wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Set(PlayerState.Preparing);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(OpenTimeoutMs);
                using (timeout.Token.Register(() => wait.TrySetCanceled()))
                {
                    try
                    {
                        await wait.Task.ConfigureAwait(false);
                    }
                    catch (TaskCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new TimeoutException("The browser did not start the stream (is the preview page open?)");
                    }
                }
            }
            string failure;
            lock (gate) failure = error;
            if (failure != null)
            {
                Set(PlayerState.Error);
                throw new InvalidOperationException(failure);
            }
            Set(PlayerState.Paused);
        }

        public void Play()
        {
            lock (gate) { Rebase(); wantPlaying = true; }
            Set(PlayerState.Playing);
        }

        public void Pause()
        {
            lock (gate) { Rebase(); wantPlaying = false; }
            Set(PlayerState.Paused);
        }

        public Task SeekAsync(long positionMs, CancellationToken token)
        {
            lock (gate)
            {
                seekTargetMs = Math.Max(0, positionMs);
                reportedPositionMs = seekTargetMs;
                reportedAtMs = clock.ElapsedMilliseconds;
                seekSeq++;
            }
            return Task.CompletedTask;
        }

        public void Stop()
        {
            lock (gate)
            {
                url = null;
                wantPlaying = false;
            }
            Set(PlayerState.Stopped);
        }

        public void SelectAudio(int index) { }
        public void SelectEmbeddedSubtitle(int? index) { }
        public void SetEmbeddedSubtitleOffset(int offsetMs) { }

        /// The page's report: position/duration while playing, or an error it can't decode.
        public void Report(int forOpen, double positionSeconds, double durationSeconds, bool ended, string failure)
        {
            TaskCompletionSource<bool> wake = null;
            var completed = false;
            var failedWhilePlaying = false;
            lock (gate)
            {
                if (forOpen != openSeq || url == null)
                    return;
                if (!string.IsNullOrEmpty(failure))
                {
                    if (error == failure)
                        return;   // already reported
                    error = failure;
                    lastFailure = failure;
                    wake = ready;
                    // After the source opened (e.g. silent Dolby audio): report an error so the session switches source.
                    failedWhilePlaying = state == PlayerState.Playing || state == PlayerState.Paused;
                }
                else
                {
                    if (durationSeconds > 0 && !double.IsInfinity(durationSeconds))
                        durationMs = (long)(durationSeconds * 1000);
                    reportedPositionMs = (long)(positionSeconds * 1000);
                    reportedAtMs = clock.ElapsedMilliseconds;
                    // A live stream reports -1: it has no duration, so its first played frames count as ready.
                    if (durationMs > 0 || durationSeconds < 0)
                        wake = ready;
                    completed = ended && state == PlayerState.Playing;
                }
            }
            wake?.TrySetResult(true);
            if (completed)
                Set(PlayerState.Completed);
            if (failedWhilePlaying)
                Set(PlayerState.Error);
        }

        /// What the page should do with its <video> element.
        public object VideoState
        {
            get
            {
                lock (gate)
                {
                    var src = url == null ? null : Proxy(url);
                    return new { mode = "real", active = url != null, url = src, open = openSeq, playing = wantPlaying, positionMs = seekTargetMs, seekSeq, error };
                }
            }
        }

        /// Loopback torrent streams go through the preview server (same origin, range requests); other URLs play directly.
        private static string Proxy(string source) =>
            (source.StartsWith("http://127.0.0.1", StringComparison.Ordinal) || source.StartsWith("http://localhost", StringComparison.Ordinal)) ? "/media?u=" + Uri.EscapeDataString(source) : source;

        private void Rebase()
        {
            var extra = state == PlayerState.Playing ? clock.ElapsedMilliseconds - reportedAtMs : 0;
            reportedPositionMs += Math.Max(0, Math.Min(extra, 1500));
            reportedAtMs = clock.ElapsedMilliseconds;
        }

        private void Set(PlayerState value)
        {
            lock (gate) state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    /// Preview-only ranking preference: sources a desktop browser can decode (H.264 with AAC/Opus/MP3/FLAC or
    /// unknown audio) move first, keeping the core/Addons order otherwise. Not used on the TV.
    internal sealed class BrowserPreferringResolver : ISourceResolver
    {
        private readonly ISourceResolver inner;

        public BrowserPreferringResolver(ISourceResolver inner) => this.inner = inner;

        public static int Penalty(StreamCandidate c)
        {
            if (c.InfoHash == null && c.Url != null) return 0;
            var video = (c.VideoCodec ?? "unknown").ToLowerInvariant();
            var audio = (c.AudioCodec ?? "unknown").ToLowerInvariant();
            var v = video == "h264" ? 0 : video == "unknown" ? 1 : 3;          // HEVC/AV1: Chrome on most Macs can't
            var a = audio == "aac" || audio == "opus" || audio == "flac" || audio == "mp3" ? 0 : audio == "unknown" ? 1 : 2;   // EAC3/AC3/DTS
            return v + a;
        }

        public async Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token)
        {
            var ranked = await inner.GetRankedAsync(type, videoId, options, token).ConfigureAwait(false);
            return ranked.Select((c, i) => new { c, i }).OrderBy(x => Penalty(x.c)).ThenBy(x => x.i).Select(x => x.c).ToList();
        }

        public Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token) =>
            inner.GetSubtitlesAsync(type, videoId, source, token);
    }

    /// Preview only: the app's start card is the one message while Chrome skips sources it cannot decode. The page
    /// hides its own placeholder on a failure, and this folds the reason into the card's status line.
    internal sealed class PreviewStatusPlayer : IPlayerControls
    {
        private readonly IPlayerControls inner;
        private readonly BrowserPlayer browser;

        public PreviewStatusPlayer(IPlayerControls inner, BrowserPlayer browser)
        {
            this.inner = inner;
            this.browser = browser;
        }

        public PlayerSnapshot Snapshot
        {
            get
            {
                var s = inner.Snapshot;
                if (s.StartStep == 0 && s.State == PlayerState.Playing)
                    browser.ClearLastFailure();
                else if (browser.LastFailure != null && (s.StartStep > 0 || s.State == PlayerState.Error) && s.Error == null)
                    s.StatusText = "Chrome can\u2019t decode that source, trying another\u2026" + (string.IsNullOrEmpty(s.StatusText) || s.StatusText == "Finding sources" ? "" : " \u00b7 " + s.StatusText);
                if (!s.SessionActive)
                    browser.ClearLastFailure();
                return s;
            }
        }

        public PlayerSettings Settings => inner.Settings;
        public Task PlayAsync(Ru7100.Core.Ui.PlayRequest request, CancellationToken token) { browser.ClearLastFailure(); return inner.PlayAsync(request, token); }
        public void Play() => inner.Play();
        public void Pause() => inner.Pause();
        public void TogglePause() => inner.TogglePause();
        public void Stop() => inner.Stop();
        public void SeekTo(long positionMs) => inner.SeekTo(positionMs);
        public void SelectAudio(int trackIndex) => inner.SelectAudio(trackIndex);
        public void SelectSubtitle(string subtitleId) => inner.SelectSubtitle(subtitleId);
        public void SetSubtitleOffset(int offsetMs) => inner.SetSubtitleOffset(offsetMs);
        public void ResetSubtitleTiming() => inner.ResetSubtitleTiming();
        public void SetBinge(bool enabled) => inner.SetBinge(enabled);
        public void SetStopAfterCurrent(bool enabled) => inner.SetStopAfterCurrent(enabled);
        public Task SwitchSourceAsync(StreamCandidate candidate, CancellationToken token) => inner.SwitchSourceAsync(candidate, token);
        public Task PlayNextAsync(CancellationToken token) => inner.PlayNextAsync(token);
    }
}
