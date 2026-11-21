using System;

namespace Ru7100.Core.Playback
{
    /// Tunables. Defaults are the parity values from docs/parity-checklist.md (G, I).
    public sealed class PlaybackOptions
    {
        public int MaxAttempts { get; set; } = 8;                                        // G1
        public RaceSettings Race { get; set; } = new RaceSettings();                    // G2
        public TimeSpan PrepareBudget { get; set; } = TimeSpan.FromSeconds(30);         // G3
        public int PrepareRetries { get; set; } = 0;                                     // G3 (TV: no retry; a dead swarm cost 2 x 30 s)
        public TimeSpan PlayerOpenBudget { get; set; } = TimeSpan.FromSeconds(60);      // G3 media probe -> player prepare
        /// A native seek that has not landed by then is abandoned and the source reopens at the last confirmed position.
        public TimeSpan SeekBudget { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan StartupInitial { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan StartupExtension { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan StartupMaximum { get; set; } = TimeSpan.FromSeconds(300);
        public TimeSpan StallAfter { get; set; } = TimeSpan.FromSeconds(10);            // G4
        public TimeSpan StallTtl { get; set; } = TimeSpan.FromHours(6);                 // G4
        public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromSeconds(5);        // I1
        public TimeSpan SubtitleInterval { get; set; } = TimeSpan.FromMilliseconds(200);
        /// Runs the 5 s monitor and the subtitle pump on the clock. Tests turn it off and call TickAsync.
        public bool AutoMonitor { get; set; } = true;
        public long ProgressSaveEveryMs { get; set; } = 60000;                           // E2
        public double CompletionRatio { get; set; } = 0.9;                               // E3
        public int MaxSwitches { get; set; } = 3;                                        // I8
        public TimeSpan SwitchCooldown { get; set; } = TimeSpan.FromMinutes(5);         // I8
        /// A stall that can no longer be replaced (attempts used up or cooling down) ends the session after this long.
        public TimeSpan GiveUpAfter { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan ThroughputSampleInterval { get; set; } = TimeSpan.FromSeconds(30); // I6
        public TimeSpan ExternalSubtitleShortWait { get; set; } = TimeSpan.FromMilliseconds(1500); // F14
        public TimeSpan ExternalSubtitleBudget { get; set; } = TimeSpan.FromSeconds(15);  // F14
        /// One external subtitle file, download and parse, outside the session gate.
        public TimeSpan ExternalSubtitleLoadBudget { get; set; } = TimeSpan.FromSeconds(25);
    }

    public sealed class PlaybackServices
    {
        public ICatalog Catalog { get; set; }
        public ILibrary Library { get; set; }
        public ISourceResolver Resolver { get; set; }
        public ISourceOpener Opener { get; set; }
        public IPlayerBackend Player { get; set; }
        public IProgressSink Progress { get; set; }
        public IKeyValueStore Store { get; set; }
        public IClock Clock { get; set; } = SystemClock.Instance;
        public ISubtitleLoader Subtitles { get; set; }
        public Action<string> Log { get; set; }
    }
}
