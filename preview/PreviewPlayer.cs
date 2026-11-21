using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Ru7100Probe;

namespace Ru7100.Preview
{
    /// Fake IPlayerControls. The server keeps the authoritative clock; the browser's <video> element follows
    /// (play/pause/seek, looping the trailer) when the sample clip loads, or a placeholder shows when it can't. Subtitle cues come from
    /// the probe's .srt files plus generated demo lines and are drawn by the app subtitle layer, never by the browser.
    internal sealed class PreviewPlayer : IPlayerControls, IVideoFollower
    {
        // Public H.264 MP4 (Blender Foundation, CC BY). Looped under the simulated timeline.
        public const string SampleVideoUrl = "https://download.blender.org/durian/trailer/sintel_trailer-720p.mp4";
        public const long ClipDurationMs = 52_000;
        public const long SampleDurationMs = 596_000;

        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PreviewLibrary library;
        private readonly Dictionary<string, TextSubtitles> subtitleTracks;
        private PlayerSnapshot state = new PlayerSnapshot();
        private long basePositionMs;
        private long baseClockMs;
        private long readyAtMs = -1;
        private long lastSavedMs;
        private int seekSequence;
        private CancellationTokenSource session;

        public PreviewPlayer(PreviewLibrary library, PlayerSettings settings)
        {
            this.library = library;
            Settings = settings;
            subtitleTracks = new Dictionary<string, TextSubtitles>
            {
                ["embedded:eng"] = new TextSubtitles("English", Resource("probe.en.srt") + "\n\n" + DemoCues(false)),
                ["external:spa"] = new TextSubtitles("Español", DemoCues(true))
            };
        }

        public PlayerSettings Settings { get; }

        /// What the browser's video element should do.
        public object VideoState
        {
            get
            {
                lock (gate)
                {
                    var s = Compute();
                    return new
                    {
                        active = s.SessionActive,
                        url = s.SessionActive && s.State != PlayerState.Preparing ? SampleVideoUrl : null,
                        playing = s.State == PlayerState.Playing,
                        positionMs = s.PositionMs,
                        clipMs = ClipDurationMs,
                        seekSeq = seekSequence
                    };
                }
            }
        }

        public PlayerSnapshot Snapshot
        {
            get
            {
                lock (gate)
                    return Compute();
            }
        }

        private long Now => clock.ElapsedMilliseconds;
        private long prepareMs = 1;

        private PlayerSnapshot Compute()
        {
            if (!state.SessionActive)
                return Copy(state);
            if (state.State == PlayerState.Preparing && readyAtMs >= 0 && Now >= readyAtMs)
            {
                state.State = PlayerState.Playing;
                state.StatusText = null;
                state.StartStep = 0;
                baseClockMs = Now;
            }
            else if (state.State == PlayerState.Preparing && readyAtMs >= 0)
            {
                // Same stages and wording as SessionPlayerControls: sources, connecting, starting.
                var left = readyAtMs - Now;
                state.StartStep = left > 2 * prepareMs / 3 ? 1 : left > prepareMs / 3 ? 2 : 3;
                state.StatusText = state.StartStep == 1 ? "Finding sources" : state.StartStep == 2 ? "Checking 7 sources…" : "Starting playback";
            }
            var position = basePositionMs + (state.State == PlayerState.Playing ? Now - baseClockMs : 0);
            if (position >= state.DurationMs && state.DurationMs > 0)
            {
                Ended();
                return Copy(state);
            }
            state.PositionMs = position;
            if (Math.Abs(position - lastSavedMs) >= 10_000 && state.Meta != null)
            {
                lastSavedMs = position;
                library.SaveProgress(state.Meta.Summary.Id, state.Video?.Id ?? state.Meta.Summary.Id, position, state.DurationMs);
            }
            var selected = state.SelectedSubtitleId != null && subtitleTracks.TryGetValue(state.SelectedSubtitleId, out var track) ? track : null;
            state.SubtitleText = selected?.At(position - state.SubtitleOffsetMs);
            return Copy(state);
        }

        private void Ended()
        {
            if (state.Meta != null)
                library.SaveProgress(state.Meta.Summary.Id, state.Video?.Id ?? state.Meta.Summary.Id, state.DurationMs, state.DurationMs);
            var next = state.Video == null ? null : state.Meta.Videos.SkipWhile(v => v.Id != state.Video.Id).Skip(1).FirstOrDefault();
            if (state.Binge && !state.StopAfterCurrent && next != null)
            {
                Begin(state.Meta, next, 0, 500);
                return;
            }
            state.SessionActive = false;
            state.State = PlayerState.Completed;
        }

        private static PlayerSnapshot Copy(PlayerSnapshot s) => (PlayerSnapshot)typeof(PlayerSnapshot)
            .GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(s, null);

        private void Begin(MetaDetail meta, VideoInfo video, long resumeMs, int prepareMs)
        {
            var binge = state.Binge;
            state = new PlayerSnapshot
            {
                SessionActive = true,
                State = PlayerState.Preparing,
                StatusText = "Finding sources",
                StartStep = 1,
                Meta = meta,
                Video = video,
                Title = meta.Summary.Name,
                EpisodeLabel = video == null ? meta.Summary.ReleaseInfo : UiFormat.Episode(video) + " · " + video.Title,
                DurationMs = SimulatedDuration(meta, video),
                AudioTracks = new[]
                {
                    new TrackInfo { Index = 0, Language = "eng", Label = "Stereo AAC" },
                    new TrackInfo { Index = 1, Language = "jpn", Label = "5.1 EAC3" }
                },
                SelectedAudio = 0,
                Subtitles = new[]
                {
                    new SubtitleOption { Id = "embedded:eng", Language = "eng", Label = "English" },
                    new SubtitleOption { Id = "external:spa", Language = "spa", Label = "Spanish", External = true }
                },
                SelectedSubtitleId = "embedded:eng",
                SubtitleOffsetMs = state.SubtitleOffsetMs,
                Binge = binge,
                SourceLabel = "1080p · 2.3 GB · Torrentio"
            };
            basePositionMs = Math.Min(resumeMs, state.DurationMs - 5000);
            lastSavedMs = basePositionMs;
            baseClockMs = Now;
            readyAtMs = Now + prepareMs;
            this.prepareMs = Math.Max(1, prepareMs);
            seekSequence++;
        }

        public async Task PlayAsync(PlayRequest request, CancellationToken token)
        {
            lock (gate)
            {
                session?.Cancel();
                session = new CancellationTokenSource();
                state.Binge = request.Mode == PlaybackMode.Binge;
                state.StopAfterCurrent = false;
                Begin(request.Meta, request.Video, request.ResumeMs, 1500);
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void Rebase()
        {
            basePositionMs = Compute().PositionMs;
            baseClockMs = Now;
        }

        public void Play()
        {
            lock (gate)
            {
                if (state.State != PlayerState.Paused) return;
                Rebase();
                state.State = PlayerState.Playing;
            }
        }

        public void Pause()
        {
            lock (gate)
            {
                if (state.State != PlayerState.Playing) return;
                Rebase();
                state.State = PlayerState.Paused;
            }
        }

        public void TogglePause()
        {
            PlayerState current;
            lock (gate)
                current = state.State;
            if (current == PlayerState.Playing) Pause(); else Play();
        }

        public void Stop()
        {
            lock (gate)
            {
                if (state.SessionActive && state.Meta != null)
                    library.SaveProgress(state.Meta.Summary.Id, state.Video?.Id ?? state.Meta.Summary.Id, Compute().PositionMs, state.DurationMs);
                state.SessionActive = false;
                state.State = PlayerState.Stopped;
            }
        }

        public void SeekTo(long positionMs)
        {
            lock (gate)
            {
                basePositionMs = Math.Max(0, Math.Min(positionMs, state.DurationMs));
                baseClockMs = Now;
                seekSequence++;
            }
        }

        public void SelectAudio(int trackIndex)
        {
            lock (gate) state.SelectedAudio = trackIndex;
        }

        public void SelectSubtitle(string subtitleId)
        {
            lock (gate) state.SelectedSubtitleId = subtitleId;
        }

        public void SetSubtitleOffset(int offsetMs)
        {
            lock (gate) state.SubtitleOffsetMs = Math.Max(-PlayerSettings.MaxSubtitleOffsetMs, Math.Min(PlayerSettings.MaxSubtitleOffsetMs, offsetMs));
        }

        public void ResetSubtitleTiming() => SetSubtitleOffset(0);

        public void SetBinge(bool enabled)
        {
            lock (gate) state.Binge = enabled;
        }

        public void SetStopAfterCurrent(bool enabled)
        {
            lock (gate) state.StopAfterCurrent = enabled;
        }

        public Task SwitchSourceAsync(StreamCandidate candidate, CancellationToken token)
        {
            lock (gate)
            {
                var position = Compute().PositionMs;
                var label = (candidate.Quality ?? "") + " · " + (UiFormat.Size(candidate.SizeBytes) ?? "direct") + " · " + (candidate.Name ?? "").Split('\n')[0];
                Begin(state.Meta, state.Video, position, 1200);
                state.SourceLabel = label;
            }
            return Task.CompletedTask;
        }

        public Task PlayNextAsync(CancellationToken token)
        {
            lock (gate)
            {
                var next = state.Video == null ? null : state.Meta.Videos.SkipWhile(v => v.Id != state.Video.Id).Skip(1).FirstOrDefault();
                if (next != null)
                    Begin(state.Meta, next, 0, 1200);
            }
            return Task.CompletedTask;
        }

        /// Timeline length shown in the UI. The browser loops the sample clip under it (position modulo clip length).
        private static long SimulatedDuration(MetaDetail meta, VideoInfo video)
        {
            switch (meta.Summary.Id)
            {
                case "tt1254207": return SampleDurationMs;
                case "tt1727587": return 888_000;
                default: return video != null ? 2_820_000 : 8_880_000;
            }
        }

        private static string Resource(string name)
        {
            using (var stream = typeof(PreviewPlayer).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Missing resource " + name), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        /// Cues every 6 s between the probe's own cues so styling is always visible while previewing.
        private static string DemoCues(bool spanish)
        {
            string[] es = { "Esta es una línea de subtítulos de ejemplo", "Dos líneas de texto\npara comprobar el ajuste", "Los subtítulos los dibuja la app", "Cambia el color, el contorno y la posición" };
            string[] en = { "This is a sample subtitle line", "Two lines of text\nto check wrapping", "Subtitles are drawn by the app layer", "Change colour, outline, box and position in the overlay" };
            var lines = spanish ? es : en;
            var b = new StringBuilder();
            var n = 100;
            for (var t = 22_000L; t < 9_000_000; t += 6000)
            {
                if (t >= 410_000 && t <= 440_000)
                    continue;   // leave room for the probe's 7:00 seek-check cue
                b.Append(n++).Append('\n').Append(Srt(t)).Append(" --> ").Append(Srt(t + 4000)).Append('\n')
                    .Append(lines[(int)(t / 6000) % lines.Length]).Append("\n\n");
            }
            return b.ToString();
        }

        private static string Srt(long ms)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return string.Format("{0:00}:{1:00}:{2:00},{3:000}", (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
        }
    }
}
