// Stand-ins for the TV's hardware player and torrent engine, used by `--services real`. The real PlaybackSession
// drives them exactly as it drives the TV: open, play, pause, seek, tracks, end of stream.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Playback;

namespace Ru7100.Host
{
    /// Simulated IPlayerBackend: the position advances with the wall clock while playing; every source is a
    /// 45-minute file with English and Japanese audio and one embedded English subtitle track.
    public sealed class SimulatedPlayer : IPlayerBackend, ISubtitleCapablePlayer
    {
        public const long SimulatedDurationMs = 45 * 60 * 1000;
        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private PlayerState state = PlayerState.Idle;
        private long basePositionMs;
        private long baseClockMs;
        private long durationMs;
        private int? selectedAudio;

        public event EventHandler<PlayerState> StateChanged;
        public event EventHandler<SubtitleCue> SubtitleCue { add { } remove { } }

        public PlayerState State
        {
            get
            {
                lock (gate)
                {
                    if (state == PlayerState.Playing && Position() >= durationMs && durationMs > 0)
                    {
                        basePositionMs = durationMs;
                        state = PlayerState.Completed;
                    }
                    return state;
                }
            }
        }

        public long PositionMs { get { lock (gate) return Math.Min(Position(), durationMs > 0 ? durationMs : long.MaxValue); } }
        public long DurationMs { get { lock (gate) return durationMs; } }
        public IReadOnlyList<TrackInfo> AudioTracks { get; private set; } = Array.Empty<TrackInfo>();
        public IReadOnlyList<TrackInfo> EmbeddedSubtitleTracks { get; private set; } = Array.Empty<TrackInfo>();
        public int? SelectedAudio { get { lock (gate) return selectedAudio; } }

        public async Task OpenAsync(string url, long startMs, CancellationToken token)
        {
            Set(PlayerState.Preparing);
            await Task.Delay(200, token).ConfigureAwait(false);
            lock (gate)
            {
                durationMs = SimulatedDurationMs;
                basePositionMs = Math.Max(0, Math.Min(startMs, durationMs - 1000));
                baseClockMs = clock.ElapsedMilliseconds;
                selectedAudio = 0;
                AudioTracks = new[]
                {
                    new TrackInfo { Index = 0, Language = "eng", Label = "English" },
                    new TrackInfo { Index = 1, Language = "jpn", Label = "Japanese" },
                };
                EmbeddedSubtitleTracks = new[] { new TrackInfo { Index = 0, Language = "eng", Label = "English" } };
            }
            Set(PlayerState.Paused);
        }

        public void Play()
        {
            lock (gate) { Rebase(); state = PlayerState.Playing; }
            StateChanged?.Invoke(this, PlayerState.Playing);
        }

        public void Pause()
        {
            lock (gate) { Rebase(); if (state == PlayerState.Playing) state = PlayerState.Paused; }
            StateChanged?.Invoke(this, PlayerState.Paused);
        }

        public Task SeekAsync(long positionMs, CancellationToken token)
        {
            lock (gate)
            {
                basePositionMs = Math.Max(0, Math.Min(positionMs, durationMs));
                baseClockMs = clock.ElapsedMilliseconds;
            }
            return Task.CompletedTask;
        }

        public void Stop() => Set(PlayerState.Stopped);
        public void SelectAudio(int index) { lock (gate) selectedAudio = index; }
        public void SelectEmbeddedSubtitle(int? index) { }
        public void SetEmbeddedSubtitleOffset(int offsetMs) { }

        private long Position() => basePositionMs + (state == PlayerState.Playing ? clock.ElapsedMilliseconds - baseClockMs : 0);

        private void Rebase()
        {
            basePositionMs = Position();
            baseClockMs = clock.ElapsedMilliseconds;
        }

        private void Set(PlayerState value)
        {
            lock (gate) { Rebase(); state = value; }
            StateChanged?.Invoke(this, value);
        }
    }

    /// Simulated ISourceOpener: accepts every ranked candidate after a short delay without contacting peers or
    /// downloading anything. The reported size is the add-on's size (or 1.5 GB), so the size cap still applies.
    public sealed class SimulatedOpener : ISourceOpener
    {
        public async Task<IOpenedSource> OpenAsync(StreamCandidate candidate, CancellationToken token)
        {
            await Task.Delay(300, token).ConfigureAwait(false);
            return new Opened("simulated://" + Uri.EscapeDataString(candidate.Identity ?? "source"), candidate.SizeBytes ?? 1_500_000_000L, candidate.Seeders ?? 8);
        }

        private sealed class Opened : IOpenedSource
        {
            public Opened(string url, long length, int peers) { PlayerUrl = url; LengthBytes = length; Peers = peers; }
            public string PlayerUrl { get; }
            public long LengthBytes { get; }
            public double DownloadBytesPerSecond => 4_000_000;
            public int Peers { get; }
            public void Dispose() { }
        }
    }
}
