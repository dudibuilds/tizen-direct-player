using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Ui
{
    // ---- Interfaces the UI needs beyond core/Contracts.cs. The lead adapts real services to these. ----

    /// Posts work back onto the UI thread (TV: EcoreMainloop.PostAndWakeUp; tests: run inline).
    public interface IUiDispatcher
    {
        void Post(Action action);
    }

    public interface IUiClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class UiSessionMemory
    {
        public const int RecentSearchLimit = 6;
        public Dictionary<BrowseFilter, int> BrowseFilters { get; set; }
        public int BrowseIndex { get; set; } = -1;
        public string SearchQuery { get; set; } = "";
        public int SearchIndex { get; set; }
        public bool SearchOnResults { get; set; }
        public List<string> RecentSearches { get; } = new List<string>();

        public void RememberSearch(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length < 2)
                return;
            RecentSearches.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
            RecentSearches.Insert(0, query);
            if (RecentSearches.Count > RecentSearchLimit)
                RecentSearches.RemoveRange(RecentSearchLimit, RecentSearches.Count - RecentSearchLimit);
        }
    }

    public sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    public sealed class SystemClock : IUiClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public sealed class RecommendationItem
    {
        public MetaSummary Meta { get; set; }
        public string Reason { get; set; }       // e.g. "Because you watched Dark"
        public double Score { get; set; }
    }

    /// "For You" row: recommendations computed on the Mac from the Netflix history and synced to the TV.
    public interface IRecommendationsFeed
    {
        Task<IReadOnlyList<RecommendationItem>> GetAsync(CancellationToken token);
    }

    public sealed class CatalogRowSpec
    {
        public string AddonId { get; set; }
        public string CatalogId { get; set; }
        public MediaType Type { get; set; }
        public string Title { get; set; }
    }

    /// Which add-on catalogs appear as Home rows. AddonDescriptor has no catalog list, so this is separate.
    public interface ICatalogRowSource
    {
        Task<IReadOnlyList<CatalogRowSpec>> GetRowsAsync(CancellationToken token);
    }

    public sealed class StaticCatalogRows : ICatalogRowSource
    {
        private readonly IReadOnlyList<CatalogRowSpec> rows;

        public StaticCatalogRows(IReadOnlyList<CatalogRowSpec> rows) => this.rows = rows;

        public static StaticCatalogRows CinemetaDefaults() => new StaticCatalogRows(new[]
        {
            new CatalogRowSpec { AddonId = "com.linvo.cinemeta", CatalogId = "top", Type = MediaType.Series, Title = "Popular series" },
            new CatalogRowSpec { AddonId = "com.linvo.cinemeta", CatalogId = "top", Type = MediaType.Movie, Title = "Popular movies" }
        });

        public Task<IReadOnlyList<CatalogRowSpec>> GetRowsAsync(CancellationToken token) => Task.FromResult(rows);
    }

    public sealed class QueueItemView
    {
        public string MetaId { get; set; }
        public MediaType Type { get; set; }
        public string Name { get; set; }
        public string VideoId { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
        public string Title { get; set; }
    }

    public sealed class QueueSnapshot
    {
        public string Status { get; set; } = "empty";  // empty | ready | playing | paused | completed | stopped | interrupted | error
        public int CurrentIndex { get; set; }
        public string LastError { get; set; }
        public IReadOnlyList<QueueItemView> Items { get; set; } = Array.Empty<QueueItemView>();
    }

    /// Ordered queue (parity J). StartAsync begins playback through the same player as IPlayerControls.
    public interface IQueueControls
    {
        Task<QueueSnapshot> GetAsync(CancellationToken token);
        Task AddAsync(MetaDetail meta, VideoInfo video, CancellationToken token);
        Task RemoveAtAsync(int index, CancellationToken token);
        Task StartAsync(CancellationToken token);
        Task SkipAsync(CancellationToken token);
        Task ClearAsync(CancellationToken token);
    }

    /// Explicit taste signals from the remote UI: the rate actions on the title page and on focused cards.
    /// core/App's FeedbackRecorder implements it (recommendation feedback log); the For You feed hides titles the
    /// user rated down or marked not interested.
    public interface IFeedbackSink
    {
        Task ThumbsUpAsync(string metaId, CancellationToken token);
        Task ThumbsDownAsync(string metaId, CancellationToken token);
        Task NotInterestedAsync(string metaId, CancellationToken token);
        /// Titles the user hid (thumbs down or not interested); the For You feed leaves them out.
        Task<IReadOnlyCollection<string>> GetHiddenAsync(CancellationToken token);
    }

    public enum PlaybackMode { Single, Binge }

    public sealed class PlayRequest
    {
        public MetaDetail Meta { get; set; }
        public VideoInfo Video { get; set; }          // null for a movie
        public long ResumeMs { get; set; }
        public PlaybackMode Mode { get; set; }
        public StreamCandidate Source { get; set; }   // null: engine ranks and races sources
        public string Quality { get; set; }           // null: engine default
    }

    public sealed class SubtitleOption
    {
        public string Id { get; set; }                // stable id: "embedded:2", "external:<url>"
        public string Label { get; set; }
        public string Language { get; set; }
        public bool External { get; set; }
    }

    /// Everything the player overlay shows. Read by polling IPlayerControls.Snapshot from TvShell.Tick.
    public sealed class PlayerSnapshot
    {
        public bool SessionActive { get; set; }
        public PlayerState State { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public MetaDetail Meta { get; set; }
        public VideoInfo Video { get; set; }
        public string Title { get; set; }
        public string EpisodeLabel { get; set; }      // "S1E3 · Pilot"
        public IReadOnlyList<TrackInfo> AudioTracks { get; set; } = Array.Empty<TrackInfo>();
        public int SelectedAudio { get; set; } = -1;  // TrackInfo.Index
        public IReadOnlyList<SubtitleOption> Subtitles { get; set; } = Array.Empty<SubtitleOption>();
        public string SelectedSubtitleId { get; set; } // null = off
        /// Current cue text (offset already applied by the engine), drawn by the app subtitle layer. Null or "" when none.
        public string SubtitleText { get; set; }
        public int SubtitleOffsetMs { get; set; }
        /// How the external subtitle was fitted to the file ("Synced to your file · 25 → 23.976"), or null.
        public string SubtitleSync { get; set; }
        public bool Binge { get; set; }
        public bool StopAfterCurrent { get; set; }
        public string SourceLabel { get; set; }       // "1080p · 2.1 GB · Torrentio"
        public int BufferingPercent { get; set; }
        public string StatusText { get; set; }        // "Finding sources", "Connecting to 12 peers"
        /// Starting stage for the loading screen: 0 none (or mid-playback), 1 finding sources, 2 connecting to a source,
        /// 3 starting the player. StatusText carries the detail ("Checking 7 sources…").
        public int StartStep { get; set; }
        public string Error { get; set; }
    }

    /// Playback commands shared by the remote and the agent API (same verbs as the probe's control commands:
    /// play, pause, toggle, seek-to, audio, subtitle, subtitle-offset, stop). The lead adapts the session engine.
    public interface IPlayerControls
    {
        PlayerSnapshot Snapshot { get; }
        PlayerSettings Settings { get; }              // subtitle style + track preferences; shared with the agent API
        Task PlayAsync(PlayRequest request, CancellationToken token);
        void Play();
        void Pause();
        void TogglePause();
        void Stop();
        void SeekTo(long positionMs);
        void SelectAudio(int trackIndex);
        void SelectSubtitle(string subtitleId);       // null = off
        void SetSubtitleOffset(int offsetMs);         // clamped to ±PlayerSettings.MaxSubtitleOffsetMs
        /// The viewer's reset: offset back to 0 and any automatic shift or speed fix dropped (and forgotten).
        void ResetSubtitleTiming();
        void SetBinge(bool enabled);
        void SetStopAfterCurrent(bool enabled);
        Task SwitchSourceAsync(StreamCandidate candidate, CancellationToken token);
        Task PlayNextAsync(CancellationToken token);
    }

    /// Optional on IPlayerControls: Back on the next-episode card keeps the credits playing (no hand-over at the
    /// cut-off; the item still advances when it ends).
    public interface ICreditsControls
    {
        void KeepWatchingCredits();
    }

    /// Everything the screens use. Optional services (Recommendations, Queue, Sources) may be null; their UI hides.
    /// Partial: optional private extensions add their own services in another file.
    public sealed partial class UiServices
    {
        private ISearchService search;
        private ISourceCatalog sourceCatalog;
        private SourceSignatures signatures;
        private IBrowseSource browse;

        /// Catalog pages for the Browse screen. Defaults to ICatalog's Cinemeta top catalog plus Extras.
        public IBrowseSource Browse
        {
            get => browse ?? (Catalog == null ? null : browse = new CatalogBrowse(Catalog, Extras));
            set => browse = value;
        }

        /// Streaming-service catalogues (JustWatch with the Streaming Catalogs add-on as fallback). Null: no Services
        /// row and Browse's Service filter falls back to the default catalogue.
        public Ru7100.Core.Addons.IServiceCatalog ServiceCatalog
        {
            get => serviceCatalog;
            set { serviceCatalog = value; serviceSummaries = null; }
        }

        /// Cached headline numbers for the Services row (null without a ServiceCatalog).
        public ServiceSummaryCache ServiceSummaries => serviceCatalog == null ? null : serviceSummaries ?? (serviceSummaries = new ServiceSummaryCache(serviceCatalog));

        private Ru7100.Core.Addons.IServiceCatalog serviceCatalog;
        private ServiceSummaryCache serviceSummaries;

        /// False on painters that cannot play trailers: no trailer starts, no trailer view hides the rows, and no
        /// trailer button or sound hint shows. The still art stays. The browser preview and the TV both play them.
        public bool TrailerPlayback { get; set; } = true;

        /// Home rows: the focused title pops out into a Netflix-style card with its trailer once focus rests for
        /// CardPopDelayMs (HomeViewModel). Off: the older full-screen trailer view plays on the billboard instead.
        public bool TrailerCards { get; set; } = true;

        /// How long focus rests on a row title before its card pops out (user: 1-1.5 s after movement stops).
        public int CardPopDelayMs { get; set; } = 1200;

        /// Clock time (ms) of the last remote key, set by TvShell. Netflix-style: previews autoplay only right after the
        /// viewer navigates, never on launch or while the TV sits idle (less distracting, silent when nobody is using it).
        public long LastInteractionMs { get; set; } = long.MinValue;

        /// A preview may autoplay only if a key was pressed within this window (it covers the ~2.5 s dwell).
        public long PreviewInteractionWindowMs { get; set; } = 10_000;

        public bool RecentlyInteracted(long nowMs) =>
            LastInteractionMs != long.MinValue && nowMs - LastInteractionMs <= PreviewInteractionWindowMs;

        /// Last trailer the painter's player finished (played to its end, or failed), and a counter that moves on
        /// every report. Screens compare the counter to the value they saw when the trailer started.
        public string EndedTrailerId { get; private set; }
        public int TrailerEndCount { get; private set; }

        /// Called by the trailer player (on the UI thread) when a trailer ends by itself.
        public void ReportTrailerEnded(string videoId)
        {
            EndedTrailerId = videoId;
            TrailerEndCount++;
        }

        /// Last trailer whose first frames showed, and a counter that moves on every report (same pattern as the end).
        public string StartedTrailerId { get; private set; }
        public int TrailerStartCount { get; private set; }

        /// Called by the trailer player (on the UI thread) once a trailer's picture is actually on screen.
        public void ReportTrailerStarted(string videoId)
        {
            StartedTrailerId = videoId;
            TrailerStartCount++;
        }

        /// Whether previews start by themselves and whether they start with sound (PlayerSettings; on without one).
        public bool AutoplayPreviews => Player?.Settings?.AutoplayPreviews ?? true;
        public bool PreviewSound => Player?.Settings?.PreviewSound ?? true;

        /// Plays the launch intro's sound (set by the TV host; null where the painter has no sound). Called once, on a
        /// cold start, when LaunchSoundEnabled.
        public Action LaunchSound { get; set; }
        public bool LaunchSoundEnabled => Player?.Settings?.LaunchSound ?? true;

        /// Whether the painter can draw the logo parts right now (the TV generates them once, off the UI thread). The
        /// launch intro holds on the plain ink page until this is true, so its timeline and sound start with the
        /// first visible frame. Null: always ready (preview, tests).
        public Func<bool> LaunchIntroReady { get; set; }

        /// Whether the painter has finished Home's pending data and image loads (the TV decodes images on its main
        /// loop): the intro's logo flies into Home only then, so the flight stays smooth. Null: always ready.
        public Func<bool> LaunchHandoffReady { get; set; }

        /// Turns a path inside the app package's shared resources (e.g. "art/backdrop.jpg") into an image URL the
        /// painter can load: file:// on the TV, a preview route in the browser. Null: packaged images are unavailable.
        public Func<string, string> ResourceUrl { get; set; }

        /// Run each screen fetch's synchronous start on a worker (the TV). Off in tests and the preview, where fetches
        /// complete synchronously by design.
        public bool FetchOffUiThread { get; set; }

        /// Start-up trace (the TV's "+N ms" log): the launch intro reports when it starts, hands off and finishes.
        public Action<string> LaunchTrace { get; set; }

        public ICatalog Catalog { get; set; }
        public ILibrary Library { get; set; }
        public IAddonRegistry Addons { get; set; }
        public IStremioAccount Account { get; set; }
        public ISourceResolver Sources { get; set; }
        public IRecommendationsFeed Recommendations { get; set; }
        public IQueueControls Queue { get; set; }
        public IPlayerControls Player { get; set; }
        public ICatalogRowSource CatalogRows { get; set; } = StaticCatalogRows.CinemetaDefaults();
        /// Episode overviews/stills, rating, runtime, trailers. Optional.
        public IMetaExtras Extras { get; set; }
        /// Search-as-you-type across catalog add-ons. Defaults to ICatalog search plus library matches.
        public ISearchService Search
        {
            get => search ?? (Catalog == null ? null : search = new CatalogSearchService(Catalog, Library));
            set => search = value;
        }
        /// Source picker data. Defaults to the ranked list from Sources (no hidden/unplayable entries).
        public ISourceCatalog SourceCatalog
        {
            get => sourceCatalog ?? (Sources == null ? null : sourceCatalog = new RankedSourceCatalog(Sources));
            set => sourceCatalog = value;
        }
        /// Best-playable-source cache for the signature line. Defaults to one over SourceCatalog.
        public SourceSignatures Signatures
        {
            get => signatures ?? (SourceCatalog == null ? null : signatures = new SourceSignatures(SourceCatalog, Sources));
            set => signatures = value;
        }
        /// Rate actions (thumbs up/down, not interested). Optional: the actions hide when null.
        public IFeedbackSink Feedback { get; set; }
        public IUiDispatcher Dispatcher { get; set; } = new ImmediateDispatcher();
        /// What the viewer left behind this session (not persisted): Browse filters and position, the search query,
        /// recent searches. Reopening a screen restores it.
        public UiSessionMemory Memory { get; } = new UiSessionMemory();
        public IUiClock Clock { get; set; } = new SystemClock();
    }
}
