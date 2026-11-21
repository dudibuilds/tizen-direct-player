using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Account;
using Ru7100.Core.Account.Trakt;
using Ru7100.Core.Addons;
using Ru7100.Core.Api;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using EngineSession = Ru7100.Core.Playback.PlaybackSession;

namespace Ru7100.Core.App
{
    /// Platform pieces the composition root needs. The TV passes the Tizen player and the torrent opener; the Mac
    /// host passes a simulated player and opener; tests pass fakes, and may also replace the network-backed services.
    public sealed class AppPlatform
    {
        public IKeyValueStore Store { get; set; }
        public IPlayerBackend Player { get; set; }
        public ISourceOpener Opener { get; set; }
        /// Shared by add-on, Stremio API and subtitle requests. Null: a default HttpClientHandler.
        public HttpMessageHandler Http { get; set; }
        public IClock Clock { get; set; } = Playback.SystemClock.Instance;
        public IUiDispatcher Dispatcher { get; set; } = new ImmediateDispatcher();
        public Action<string> Log { get; set; }
        public PlaybackOptions PlaybackOptions { get; set; }
        /// Extra lines for the admin diagnostics command (torrent engine state and so on). Cheap: no storage scans and no
        /// waits on the UI thread, so it is safe to poll during playback.
        public Func<object> Diagnostics { get; set; }
        /// The expensive version (storage scan, UI stacking dump), only for dev.diagnostics {"full": true}. Null: Diagnostics.
        public Func<object> FullDiagnostics { get; set; }

        // Optional replacements for the network-backed services (tests). Null: the real implementation.
        public ICatalog Catalog { get; set; }
        public ISourceResolver Sources { get; set; }
        public ILibrary Library { get; set; }
        public IStremioAccount Account { get; set; }
        public IAddonRegistry Addons { get; set; }
        public IProgressSink Progress { get; set; }
        public ISubtitleLoader Subtitles { get; set; }
        public ICatalogRowSource CatalogRows { get; set; }
    }

    /// COMPOSITION ROOT for the TV app and the Mac host. One instance owns one PlaybackSession, one PlayerSettings
    /// and one recommendations store, and hands the same objects to the remote UI (Ui), the agent API (Router) and
    /// the feedback recorder, so a remote key and an MCP command act on the same session.
    ///
    ///   AddonRegistry (local installs + Stremio account collection + Cinemeta/OpenSubtitles defaults)
    ///     -> StremioCatalog (ICatalog), SourceResolver (ISourceResolver), AddonCatalogRows (Home rows)
    ///   StremioAccount -> StremioLibrary (ILibrary, outbox) -> StremioProgressSink + TraktProgressSink (IProgressSink)
    ///   PlaybackSession(catalog, library, resolver, opener, player, progress, store)
    ///     -> SessionPlayerControls (UI IPlayerControls + IQueueControls)
    ///     -> SessionPlaybackCommands (API IPlaybackCommands)
    ///     -> FeedbackRecorder (viewing events) -> KeyValueRecommendationsStore <- recommendations.* API commands
    ///   FeedbackLibrary(library) records added_to_library / removed for both front ends.
    public sealed class AppHost : IDisposable
    {
        public const int LogLines = 300;
        private readonly AppPlatform platform;
        private readonly Queue<string> logTail = new Queue<string>();
        private readonly List<IDisposable> owned = new List<IDisposable>();
        private TvShell shell;
        private bool wasActive;
        private bool initialized;

        public AppHost(AppPlatform platform)
        {
            this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
            if (platform.Store == null) throw new ArgumentException("A store is required.", nameof(platform));
            if (platform.Player == null) throw new ArgumentException("A player is required.", nameof(platform));
            if (platform.Opener == null) throw new ArgumentException("A source opener is required.", nameof(platform));
            Func<DateTimeOffset> now = () => platform.Clock.UtcNow;
            Store = platform.Store;

            // ---- add-ons, catalog, sources ----
            if (platform.Catalog == null || platform.Sources == null || platform.Addons == null)
            {
                Http = new AddonHttp(platform.Http);
                owned.Add(Http);
            }
            StremioApi = platform.Account == null || platform.Library == null ? new StremioApiClient(platform.Http) : null;
            StremioAccount = platform.Account == null ? new StremioAccount(StremioApi, Store, now) : null;
            var accountSource = platform.Account ?? StremioAccount;
            Registry = platform.Addons == null
                ? new AddonRegistry(Store, Http, token => accountSource.IsSignedIn ? accountSource.GetAddonCollectionAsync(token) : Task.FromResult<IReadOnlyList<AddonDescriptor>>(Array.Empty<AddonDescriptor>()), now)
                : null;
            Addons = platform.Addons ?? Registry;
            Catalog = platform.Catalog ?? new StremioCatalog(Registry ?? new AddonRegistry(Store, Http, null, now), Http, now);
            Sources = platform.Sources ?? new SourceResolver(Registry ?? new AddonRegistry(Store, Http, null, now), Http, now);
            Account = Registry != null ? new RegistryAwareAccount(accountSource, Registry) : accountSource;

            // ---- library and progress sync ----
            StremioLibrary = platform.Library == null ? new StremioLibrary(StremioApi, StremioAccount ?? (IStremioAuthKeyProvider)accountSource, Store, Catalog, now) : null;
            var library = platform.Library ?? StremioLibrary;
            if (platform.Progress != null)
                Progress = platform.Progress;
            else if (StremioLibrary != null)
                Progress = new CompositeProgressSink(new StremioProgressSink(StremioLibrary, now), new TraktProgressSink(new TraktClient(platform.Http, Store, now), Store, now));

            // ---- recommendations and feedback ----
            Recommendations = new KeyValueRecommendationsStore(Store, now);
            Feedback = new FeedbackRecorder(Recommendations, Store, now, Log);
            Library = new FeedbackLibrary(library, Feedback);

            // ---- playback ----
            Settings = new PlayerSettings(Store);
            var subtitles = platform.Subtitles;
            if (subtitles == null)
            {
                var loader = new HttpSubtitleLoader(platform.Http);
                owned.Add(loader);
                subtitles = loader;
            }
            Session = new EngineSession(new PlaybackServices
            {
                Catalog = Catalog, Library = Library, Resolver = Sources, Opener = platform.Opener, Player = platform.Player,
                Progress = Progress, Store = Store, Clock = platform.Clock, Subtitles = subtitles, Log = line => Log("playback: " + line),
            }, platform.PlaybackOptions);
            Feedback.Attach(Session);
            PlayerControls = new SessionPlayerControls(Session, Settings, now, Log);
            PlaybackCommands = new SessionPlaybackCommands(Session, Settings);

            // ---- front ends ----
            Ui = new UiServices
            {
                Catalog = Catalog,
                Library = Library,
                Addons = Addons,
                Account = Account,
                Sources = Sources,
                Recommendations = new RecommendationsFeed(Recommendations, Feedback),
                Queue = PlayerControls,
                Player = PlayerControls,
                CatalogRows = platform.CatalogRows ?? (Registry != null ? (ICatalogRowSource)new AddonCatalogRows(Registry, Log) : StaticCatalogRows.CinemetaDefaults()),
                Dispatcher = platform.Dispatcher ?? new ImmediateDispatcher(),
                Clock = new EngineUiClock(platform.Clock),
                Feedback = Feedback,
            };
            if (platform.Catalog == null)
            {
                // Network-backed extras only with the real catalog (tests that pass fakes stay offline).
                Ui.Extras = Http != null ? new CinemetaExtras(Http) : new CinemetaExtras(platform.Http);   // shares the meta document with Catalog
                Ui.Browse = new CinemetaBrowse(platform.Http);
                Ui.ServiceCatalog = new FallbackServiceCatalog(new JustWatchCatalog(platform.Http), new StreamingCatalogsAddon(Http ?? new AddonHttp(platform.Http)));
                if (Registry != null)
                    Ui.Search = new AddonSearchService(Registry, Library, platform.Http);
            }
            if (Sources is SourceResolver resolver)
                Ui.SourceCatalog = new AddonSourceCatalog(resolver);
            Router = new ApiRouter(new ApiServices
            {
                Playback = PlaybackCommands,
                Catalog = Catalog,
                Library = Library,
                Sources = Sources,
                Addons = Addons,
                Account = Account,
                Store = Store,
                Recommendations = Recommendations,
                Clock = now,
            });
            // Preview (trailer) settings: read with no arguments, change with autoplay and/or sound.
            Router.Register(new ApiCommand("ui.previews", new[] { ApiScopes.Playback }, new[] { "autoplay", "sound" },
                (args, token) =>
                {
                    var autoplay = args.OptionalBool("autoplay");
                    var sound = args.OptionalBool("sound");
                    if (autoplay.HasValue) Settings.SetAutoplayPreviews(autoplay.Value);
                    if (sound.HasValue) Settings.SetPreviewSound(sound.Value);
                    return Task.FromResult<object>(new { autoplay = Settings.AutoplayPreviews, sound = Settings.PreviewSound });
                }));
            Router.Register(new ApiCommand("dev.diagnostics", new[] { ApiScopes.Admin }, new[] { "full" },
                (args, token) => Task.FromResult<object>(Diagnostics(args.OptionalBool("full") ?? false))));
            // The playback timeline alone (soak tests poll this): newest `max` entries (default 300), optionally one kind
            // (http, seek, buffering, health, recovery, throttle, sample, process, listener, lifecycle, memory, ...).
            Router.Register(new ApiCommand("dev.timeline", new[] { ApiScopes.Admin }, new[] { "max", "kind" },
                (args, token) =>
                {
                    var max = (int)(args.OptionalNumber("max", 1, 600) ?? 300);
                    var timeline = Playback.PlaybackTimeline.Shared;
                    return Task.FromResult<object>(new { total = timeline.Count, entries = timeline.Lines(max, args.OptionalString("kind")) });
                }));
            Session.StatusChanged += OnStatus;
        }

        public IKeyValueStore Store { get; }
        public AddonHttp Http { get; }
        public AddonRegistry Registry { get; }
        public IAddonRegistry Addons { get; }
        public ICatalog Catalog { get; }
        public ISourceResolver Sources { get; }
        public StremioApiClient StremioApi { get; }
        public StremioAccount StremioAccount { get; }
        public IStremioAccount Account { get; }
        public StremioLibrary StremioLibrary { get; }
        public FeedbackLibrary Library { get; }
        public IProgressSink Progress { get; }
        public KeyValueRecommendationsStore Recommendations { get; }
        public FeedbackRecorder Feedback { get; }
        public PlayerSettings Settings { get; }
        public EngineSession Session { get; }
        public SessionPlayerControls PlayerControls { get; }
        public SessionPlaybackCommands PlaybackCommands { get; }
        public UiServices Ui { get; }
        public ApiRouter Router { get; }
        public IFeedbackSink FeedbackSink => Feedback;

        /// Loads persisted state (settings, Stremio sign-in, library cache and outbox, unfinished session).
        /// Each step is independent: a failure is logged and the app starts with what it has.
        public async Task InitializeAsync(CancellationToken token)
        {
            if (initialized) return;
            initialized = true;
            // Independent reads run together (they use separate store keys): settings, the account and then its library,
            // and the playback session. On the TV each read is mostly first-use cost, so overlapping them shortens launch.
            async Task AccountAndLibrary()
            {
                if (StremioAccount != null)
                    await Step("account", () => StremioAccount.InitializeAsync(token)).ConfigureAwait(false);
                if (StremioLibrary != null)
                {
                    await Step("library", () => StremioLibrary.InitializeAsync(token)).ConfigureAwait(false);
                    _ = Step("library sync", () => StremioLibrary.FlushAsync(CancellationToken.None));
                }
            }
            await Task.WhenAll(
                Step("settings", () => Settings.LoadAsync(token)),
                AccountAndLibrary(),
                Step("playback", () => Session.InitializeAsync(token))).ConfigureAwait(false);
        }

        /// The new-episodes availability check (parity C8) wired to this app's resolver.
        public Func<MetaDetail, VideoInfo, CancellationToken, Task<bool>> NewEpisodeCheck => NewEpisodeAvailability.For(Sources);

        /// Connects the remote UI shell: sessions started elsewhere (agent API, queue) open the player screen.
        public void AttachShell(TvShell tvShell)
        {
            shell = tvShell;
            wasActive = Session.GetStatus().Active;
        }

        /// I10: offers to resume the session the app was playing when it last closed. "Not now" or Back forgets it.
        public bool OfferRestore()
        {
            var restorable = Session.Restorable;
            var target = shell;
            if (restorable == null || target == null) return false;
            var what = restorable.Name ?? "your last title";
            if (restorable.Season.HasValue && restorable.Episode.HasValue)
                what += " " + UiFormat.Episode(restorable.Season.Value, restorable.Episode.Value);
            if (restorable.PositionMs > 0)
                what += " at " + UiFormat.Clock(restorable.PositionMs);
            Action discard = () => target.RunDetached(t => Session.DiscardRestorableAsync(t));
            target.ShowDialog(new ConfirmDialog("Continue watching?", what,
                new DialogButton("Resume", () => Restore(target)),
                new DialogButton("Not now", discard)) { OnCancel = discard });
            return true;
        }

        private void Restore(TvShell target)
        {
            var player = target.AttachPlayer();
            target.RunDetached(async t =>
            {
                var result = await Session.RestoreAsync(t).ConfigureAwait(false);
                if (result.Status == PlayStatus.Playing || result.Status == PlayStatus.Superseded) return;
                Ui.Dispatcher.Post(() =>
                {
                    target.ShowToast("Could not resume: " + (result.Error ?? result.Status));
                    if (target.Stack.Contains(player)) target.Pop(player);
                });
            });
        }

        /// App sent to the background (TV Home/Source button): pause and save, so nothing plays unseen.
        public async Task PauseForBackgroundAsync(CancellationToken token)
        {
            Session.SetBackground(true);   // a replacement or advance landing from now on starts paused
            var status = Session.GetStatus();
            if (status.Active && status.Session?.UserPaused != true)
                await Step("pause", () => Session.PauseAsync(token)).ConfigureAwait(false);
            else if (!status.Active && (status.Phase == "resolving" || status.Phase == "starting"))
                await Step("cancel start", () => Session.StopAsync(token)).ConfigureAwait(false);   // would start playing unseen
            await Step("save", () => Session.SaveAsync(token)).ConfigureAwait(false);
        }

        /// Back in the foreground: the session reopens its source if the TV took the decoder away meanwhile.
        public async Task ResumeFromBackgroundAsync(CancellationToken token)
        {
            Session.SetBackground(false);
            await Step("reconcile", () => Session.ReconcileAfterForegroundAsync(token)).ConfigureAwait(false);
        }

        /// App closing: save the session without ending it (so it is offered for restore next launch) and flush
        /// pending writes. Does not touch the player; the platform releases it.
        public async Task ShutdownAsync(CancellationToken token)
        {
            await Step("save", () => Session.SaveAsync(token)).ConfigureAwait(false);
            await Step("history", () => Session.FlushHistoryAsync()).ConfigureAwait(false);
            await Step("settings", () => Settings.FlushAsync()).ConfigureAwait(false);
            await Step("feedback", () => Feedback.FlushAsync()).ConfigureAwait(false);
        }

        public IReadOnlyList<string> RecentLog()
        {
            lock (logTail) return logTail.ToList();
        }

        public void Log(string line)
        {
            var stamped = platform.Clock.UtcNow.ToString("HH:mm:ss") + " " + line;
            lock (logTail)
            {
                logTail.Enqueue(stamped);
                while (logTail.Count > LogLines) logTail.Dequeue();
            }
            try { platform.Log?.Invoke(line); } catch (Exception) { }
        }

        public void Dispose()
        {
            Session.StatusChanged -= OnStatus;
            Feedback.Dispose();
            PlayerControls.Dispose();
            Session.Dispose();
            foreach (var item in owned)
                try { item.Dispose(); } catch (Exception) { }
        }

        private object Diagnostics(bool full)
        {
            object extra = null;
            try { extra = (full ? platform.FullDiagnostics ?? platform.Diagnostics : platform.Diagnostics)?.Invoke(); }
            catch (Exception error) { extra = "diagnostics failed: " + error.Message; }
            return new
            {
                playback = Session.GetStatus(),
                restorable = Session.Restorable,
                library = StremioLibrary == null ? null : new { pendingWrites = StremioLibrary.PendingWrites, lastSyncError = StremioLibrary.LastSyncError },
                account = new { signedIn = Account.IsSignedIn },
                platform = extra,
                timeline = Playback.PlaybackTimeline.Shared.Lines(200),
                log = RecentLog(),
            };
        }

        private void OnStatus(object sender, PlaybackStatus status)
        {
            var active = status.Active;
            var started = active && !wasActive;
            wasActive = active;
            var target = shell;
            if (!started || target == null) return;
            Ui.Dispatcher.Post(() =>
            {
                if (!target.Stack.OfType<PlayerViewModel>().Any())
                    target.AttachPlayer();
            });
        }

        private async Task Step(string what, Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Log(what + ": " + error.Message); }
        }

        private sealed class EngineUiClock : IUiClock
        {
            private readonly IClock clock;
            public EngineUiClock(IClock clock) => this.clock = clock;
            public DateTimeOffset UtcNow => clock.UtcNow;
        }
    }
}
