using System;
using System.Diagnostics;
using System.Linq;
using ElmSharp;
using Ru7100.Core.Ui;

namespace Ru7100.TvUi
{
    /// Posts onto the EFL main loop.
    internal sealed class EcoreDispatcher : IUiDispatcher
    {
        /// While true (the launch intro's animated beats), work posted from other threads waits in a queue and runs
        /// once it turns false (TvUiHost drains it each tick), so data arriving mid-animation never stalls a frame.
        public static Func<bool> Hold;
        private static readonly System.Collections.Generic.Queue<Action> held = new System.Collections.Generic.Queue<Action>();

        public void Post(Action action)
        {
            if (EcoreMainloop.IsMainThread)
                action();
            else
                EcoreMainloop.PostAndWakeUp(() =>
                {
                    if (Hold != null && Hold()) { held.Enqueue(action); return; }
                    Run(action);
                });
        }

        private static void Run(Action action) =>
            LaunchProfile.Timed("post " + action.Method.DeclaringType?.Name + "." + action.Method.Name, action);

        /// Main loop: runs work held during the intro's beats, once the hold is over.
        public static void DrainHeld()
        {
            if (held.Count == 0 || (Hold != null && Hold())) return;
            while (held.Count > 0) Run(held.Dequeue());
        }
    }

    /// No-op trailer player (the painter's default when none is passed): the billboard keeps its still image.
    /// The real one is Ru7100Probe.Trailer.TizenTrailerPlayer, passed in by Program.
    internal sealed class StillImageTrailerPlayer : ITrailerPlayer
    {
        public void Show(string titleId, string youTubeId, int x, int y, int w, int h, bool muted) { }
        public void SetMuted(bool muted) { }
        public void Hide() { }
    }

    /// Hosts the TV UI in an ElmSharp window: remote keys go to the shell, a ~30 fps timer ticks it, and the
    /// painter draws animator frames (only while something moves, or when the shell's Version changes).
    ///
    /// Wiring from Program.OnCreate (the lead owns Program.cs):
    ///   var services = new UiServices { Catalog = ..., Library = ..., Addons = ..., Account = ..., Sources = ...,
    ///       Extras = new CinemetaExtras(), Search = new AddonSearchService(registry, library),
    ///       SourceCatalog = new AddonSourceCatalog(resolver), Recommendations = ..., Queue = ...,
    ///       Player = playerControlsAdapter, Dispatcher = new EcoreDispatcher(), Clock = new SystemClock() };
    ///   host = new TvUiHost(window, services, Path.Combine(DirectoryInfo.Data, "posters"), Exit);
    ///   host.Start(new HomeViewModel());
    /// and route EcoreEvent KeyDown names to host.OnKey(args.KeyName) instead of the probe's OnRemoteKey
    /// (pass subscribeKeys: false then).
    internal sealed class TvUiHost : IDisposable, Ru7100.Core.App.IUiDevTools
    {
        /// 30 fps cap while animating; the 2019 TV CPU can't sustain more with EFL objects.
        public const double TickSeconds = 1.0 / 30;
        private const int ShellTickEveryMs = 100;

        private readonly ElmFramePainter painter;
        private readonly UiAnimator animator = new UiAnimator();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Action exitApp;
        private IntPtr timer;
        private int builtVersion = -1;
        private long lastShellTick;
        private UiFrame target;
        private EcoreEvent<EcoreKeyEventArgs> keys;
        private readonly UiAssets assets;
        private readonly PosterCache posters;

        /// Poster cache counters (downloads, failures, last error) for diagnostics.
        public object PosterStats => posters.Stats;

        public TvUiHost(Window window, UiServices services, string posterCacheDirectory, Action exitApp, bool subscribeKeys = true, ITrailerPlayer trailers = null)
        {
            Shell = new TvShell(services);
            this.exitApp = exitApp;
            // Without a real trailer player the trailer view would only slide the rows away and show a sound hint
            // over still art, so trailers are off unless one is passed.
            TvUiDevFlags.Load(System.IO.Path.GetDirectoryName(posterCacheDirectory.TrimEnd('/')) ?? posterCacheDirectory);
            TrailersSupported = trailers != null;
            services.TrailerPlayback = TrailersSupported;
            posters = new PosterCache(posterCacheDirectory, webp: false, migrateInBackground: true) { OnError = TvUiDiagnostics.DownloadFailed };
            painter = new ElmFramePainter(window, posters, trailers);
            // Fewer pictures reach the toolkit per frame while keys arrive or something glides (decodes land in renders).
            painter.Navigating = () => animator.IsAnimating || clock.ElapsedMilliseconds - lastKeyAt < RestBeforeBigFetchMs;
            Trailers = trailers;
            if (trailers is Ru7100Probe.Trailer.TizenTrailerPlayer video)
            {
                // The video started or stopped showing, or a crossfade stepped: repaint the current frame (queued,
                // never inside the paint that hid it) so the painter updates the hole for the video plane.
                video.VideoChanged += () => EcoreMainloop.PostAndWakeUp(() =>
                {
                    forcePaint = true;
                    Repaint();
                });
            }
            services.LaunchTrace = line => Ru7100Probe.Program.Trace(line);
            services.FetchOffUiThread = true;
            try
            {
                window.RenderPost += (_, __) =>
                {
                    Perf.Rendered();
                    if (!firstRenderTraced) { firstRenderTraced = true; Ru7100Probe.Program.Trace("ui: first render"); }
                    if (firstFramePainted && !introFrameRendered) { introFrameRendered = true; Ru7100Probe.Program.Trace("ui: first ui frame on screen"); }
                };
            }
            catch (Exception error) { Ru7100Probe.Program.Trace("ui: no render-post event: " + error.Message); }
            Ru7100Probe.Program.Trace("ui: poster cache at " + posterCacheDirectory + " (migrated " + posters.Migrated + ", purged " + posters.Purged + " legacy files), elm scale " + Elementary.GetScale());
            // Rounded corners and smooth gradients as small generated images (ElmSharp has neither). The folder
            // name carries the generator version, so a new build never reuses files written by an older encoder.
            AssetDirectory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(posterCacheDirectory.TrimEnd('/')) ?? posterCacheDirectory, "ui-assets-v" + UiAssets.Version);
            assets = new UiAssets(AssetDirectory);
            SeedShippedAssets(AssetDirectory);
            // Shape, glyph and shadow images are generated off the UI thread at launch, so the first focus ring,
            // pop-out card or sheet never renders a PNG in the middle of navigation.
            var warm = assets;
            System.Threading.Tasks.Task.Run(() => { try { warm.Prewarm(); } catch (Exception) { /* generated on demand instead */ } });
            // The launch intro starts (with its sound) only once the logo parts exist, so its first frame is the first
            // thing you see and hear together; Prewarm makes them first.
            // ...and only after the first frame has actually reached the screen (the first render after Start takes
            // ~0.5 s on the TV): the timeline and sound then start with the picture, not before it.
            services.LaunchIntroReady = () => introFrameRendered && UiGlyphs.Names.Where(UiGlyphs.IsLogo).All(warm.HasGlyph);
            // The logo flies into Home once the art Home is showing has been decoded (Home itself needs its first row,
            // HomeViewModel.ReadyForReveal), so the flight has the frames to itself without waiting for every row.
            // Strict: Home has been painted under the cover (more than the intro's ~10 nodes), its images are decoded,
            // and the main loop has been quiet for 300 ms, so nothing heavy lands during the logo's flight.
            // Just before the flight, while the logo is still, collect garbage once (then wait for the quiet stretch
            // again), so a collection doesn't land in the middle of the flight.
            services.LaunchHandoffReady = () =>
            {
                if (painter.PendingImages != 0 || lastPaintedNodes <= 30 || LaunchProfile.MsSinceHeavy < 300)
                    return false;
                if (!preFlightCollected)
                {
                    preFlightCollected = true;
                    LaunchProfile.Timed("pre-flight gc", () => GC.Collect(1, GCCollectionMode.Forced, true));
                    LaunchProfile.HeavyAt = LaunchProfile.NowMs;
                    return false;
                }
                return true;
            };
            EcoreDispatcher.Hold = () => Shell.Current is HomeViewModel h && h.Intro != null && h.Intro.Started && !h.Intro.ShowHome(h.LayoutNowMs);
            if (TvUiDevFlags.Probe)
            {
                try
                {
                    probe = new TvUiProbe(window, assets, posterCacheDirectory);
                }
                catch (Exception error)
                {
                    Ru7100Probe.Program.Trace("ui: probe strip failed: " + error);
                }
            }
            if (subscribeKeys)
            {
                keys = new EcoreEvent<EcoreKeyEventArgs>(EcoreEventType.KeyDown, EcoreKeyEventArgs.Create);
                keys.On += (_, args) => OnKey(args.KeyName);
            }
        }

        public TvShell Shell { get; }

        private TvUiProbe probe;

        /// Stacking and visibility of every image and box the painter holds, plus the probe strip's results.
        /// Safe from any thread: it hops to the main loop and waits up to 2 s.
        private static int stackingBusy;

        public object StackingDump()
        {
            // One at a time: concurrent polls each parked a pool thread for up to 2 s waiting on the main loop.
            if (System.Threading.Interlocked.CompareExchange(ref stackingBusy, 1, 0) != 0)
                return new { error = "another stacking dump is running" };
            try { return StackingDumpCore(); }
            finally { System.Threading.Interlocked.Exchange(ref stackingBusy, 0); }
        }

        private object StackingDumpCore()
        {
            object result = null;
            Exception failure = null;
            Action read = () =>
            {
                try
                {
                    // perf: frame intervals, per-stage times and the main-loop heartbeat (late ticks, longest stall).
                    result = new { painter = painter.Dump(), probe = probe?.Report, perf = Perf.Snapshot(clock.ElapsedMilliseconds) };
                }
                catch (Exception error)
                {
                    failure = error;
                }
            };
            if (EcoreMainloop.IsMainThread)
                read();
            else
            {
                using (var done = new System.Threading.ManualResetEventSlim())
                {
                    EcoreMainloop.PostAndWakeUp(() =>
                    {
                        read();
                        done.Set();
                    });
                    if (!done.Wait(2000))
                        return new { error = "main loop did not answer within 2 s" };
                }
            }
            return failure != null ? new { error = failure.ToString() } : result;
        }

        /// Where the generated shape images live (diagnostics).
        public string AssetDirectory { get; }

        /// Whether a real trailer player was supplied (diagnostics).
        public bool TrailersSupported { get; }

        /// The trailer player handed to the painter (null: none).
        public ITrailerPlayer Trailers { get; }

        public void Start(Screen root)
        {
            Shell.Start(root);
            Repaint();
            timer = EcoreMainloop.AddTimer(TickSeconds, () =>
            {
                var now = clock.ElapsedMilliseconds;
                // Heartbeat: a late tick is a main-loop stall, traced to what was done just before it.
                // Pictures handed over since the last tick are decoded by the render that follows the hand-over, which
                // may be before this tick or after it: a late tick is reported with both.
                var rebuilt = Shell.Version != builtVersion;
                var loaded = painter.TakeHandedOver();
                var note = loaded != null ? "images: " + loaded : rebuilt ? "layout rebuild" : null;
                Perf.Heartbeat(stageClock.Elapsed.TotalMilliseconds, TickSeconds * 1000, beatNote == null ? note : note == null ? beatNote : beatNote + " | " + note);
                beatNote = note;
                // Focus has rested: now fetch the large files of the titles one press away (see Prefetch).
                if (!restedFetchDone && now - lastKeyAt >= RestBeforeBigFetchMs)
                {
                    restedFetchDone = true;
                    Prefetch();
                }
                if (now - lastShellTick >= ShellTickEveryMs)
                {
                    lastShellTick = now;
                    LaunchProfile.Timed("tick " + Shell.Current?.Kind, Shell.Tick);
                }
                EcoreDispatcher.DrainHeld();
                Repaint();
                if (LaunchProfile.Recording)
                {
                    string intro = null;
                    if (Shell.Current is HomeViewModel home && home.Intro != null)
                        intro = "intro " + home.Intro.Elapsed(home.LayoutNowMs) + (home.Intro.HandingOff ? " handoff" : "") + (home.Intro.Started ? "" : " waiting");
                    LaunchProfile.Tick(lastSample, lastPainted, painter.PendingImages, intro);
                    lastPainted = false;
                }
                return true;
            });
        }

        private UiFrameSample lastSample;
        private int lastPaintedNodes;
        private bool preFlightCollected;
        private bool lastPainted;

        public void OnKey(string tizenKeyName)
        {
            var key = RemoteKeys.FromTizen(tizenKeyName);
            if (key == RemoteKey.None)
                return;
            lastKeyAt = clock.ElapsedMilliseconds;
            var handled = Shell.HandleKey(key);
            if (!handled && key == RemoteKey.Back)
                exitApp?.Invoke();
            Repaint();
        }

        private bool firstFrameTraced;
        private bool firstRenderTraced;
        private volatile bool firstFramePainted, introFrameRendered;

        /// The logo images ship pre-rendered in res/ui (identical to what UiAssets would generate), so a first launch
        /// never rasterises them; copied into the asset folder once.
        private static void SeedShippedAssets(string assetDirectory)
        {
            try
            {
                var shipped = System.IO.Path.Combine(Tizen.Applications.Application.Current.DirectoryInfo.SharedResource, "ui");
                if (!System.IO.Directory.Exists(shipped)) return;
                foreach (var file in System.IO.Directory.GetFiles(shipped, "*.png"))
                {
                    var target = System.IO.Path.Combine(assetDirectory, System.IO.Path.GetFileName(file));
                    if (!System.IO.File.Exists(target)) System.IO.File.Copy(file, target);
                }
            }
            catch (Exception error) { Ru7100Probe.Program.Trace("ui: seeding shipped assets failed: " + error.Message); }
        }

        public void Repaint()
        {
            if (!firstFrameTraced)
            {
                firstFrameTraced = true;
                Ru7100Probe.Program.Trace("ui: first frame");
            }
            var changed = Shell.Version != builtVersion;
            var forced = forcePaint && target != null;
            forcePaint = false;
            if (!changed && !animator.IsAnimating && !forced)
            {
                lastPaintMs = -1;   // the animation run ended: the next frame starts a new one
                return;
            }
            try
            {
                var sample = new UiFrameSample { AtMs = clock.ElapsedMilliseconds };
                var started = stageClock.Elapsed.TotalMilliseconds;
                if (lastPaintMs >= 0)
                    sample.IntervalMs = started - lastPaintMs;
                lastPaintMs = started;
                if (changed)
                {
                    builtVersion = Shell.Version;
                    animator.ReducedMotion = Shell.Services.Player?.Settings?.ReducedMotion ?? false;
                    target = UiLayout.Build(Shell);
                    sample.BuildMs = stageClock.Elapsed.TotalMilliseconds - started;
                }
                var t0 = stageClock.Elapsed.TotalMilliseconds;
                var stepped = animator.Step(target, clock.ElapsedMilliseconds);
                var t1 = stageClock.Elapsed.TotalMilliseconds;
                var lowered = UiFrameLowering.Lower(stepped, assets);
                var t2 = stageClock.Elapsed.TotalMilliseconds;
                painter.Paint(lowered);
                // After the paint: the painter asks for what is on screen first, so visible art never starts as a
                // prefetch (prefetch holds at most half the download slots).
                if (changed)
                    Prefetch();
                if (Shell.Current != null)
                    firstFramePainted = true;   // the next render puts the UI's first frame on screen (launch intro start)
                probe?.RaiseAll();
                var t3 = stageClock.Elapsed.TotalMilliseconds;
                sample.StepMs = t1 - t0;
                sample.LowerMs = t2 - t1;
                sample.PaintMs = t3 - t2;
                var c = painter.TakeCounters();
                sample.Nodes = c.Nodes;
                sample.Created = c.Created;
                sample.Removed = c.Removed;
                sample.Updated = c.Updated;
                sample.Moved = c.Moved;
                sample.TextSet = c.TextSet;
                sample.Restacked = c.Restacked;
                sample.FileLoads = c.FileLoads;
                sample.FileLoadMs = c.FileLoadMs;
                Perf.Record(sample);
                lastSample = sample;
                lastPainted = true;
                lastPaintedNodes = sample.Nodes;
            }
            catch (Exception error)
            {
                Tizen.Log.Error("RU7100Ui", "Paint failed: " + error);
            }
        }

        private string beatNote;
        private long lastKeyAt;
        private bool restedFetchDone;
        private const int RestBeforeBigFetchMs = 250;
        private readonly System.Collections.Generic.HashSet<string> prefetched = new System.Collections.Generic.HashSet<string>();

        /// Downloads (never decodes) the art of the titles focus can reach with one key press: the cards left and
        /// right, the same column of the rows above and below, and their billboard art and logos. Moving there then
        /// only loads a file that is already on disk. Network and disk work run on the thread pool; nothing here
        /// touches a toolkit object.
        private void Prefetch()
        {
            if (!(Shell.Current is HomeViewModel home))
                return;
            // While keys keep coming only card-sized art is fetched (what is about to scroll in). The billboard file
            // and logo of the titles one press away wait until focus rests, so flying past titles costs nothing.
            var rested = clock.ElapsedMilliseconds - lastKeyAt >= RestBeforeBigFetchMs;
            restedFetchDone = rested;
            var rows = home.Rows;
            var at = home.FocusedRowIndex;
            for (var r = Math.Max(0, at - 1); r <= at + 2 && r < rows.Count; r++)
            {
                var row = rows[r];
                if (row.IsMenu || row.Items.Count == 0)
                    continue;
                var column = home.FocusedColumn(row);
                for (var c = column - 1; c <= column + (r == at ? 5 : 4); c++)
                {
                    if (c < 0 || c >= row.Items.Count)
                        continue;
                    var tile = row.Items[c];
                    var near = r == at ? Math.Abs(c - column) <= 1 : c == column;
                    // Card art for everything about to scroll in (the exact URL the card draws); the big billboard
                    // file and logo only for the titles one press away.
                    Fetch(UiLayout.HomeCardArt(row.Style, tile));
                    if (near && rested)
                    {
                        Fetch(UiFormat.BackgroundFor(tile.Id));
                        Fetch(tile.Logo);
                    }
                }
            }
        }

        private void Fetch(string url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.Ordinal) || !prefetched.Add(url))
                return;
            if (prefetched.Count > 2000)
                prefetched.Clear();
            posters.GetAsync(url, System.Threading.CancellationToken.None, speculative: true).ContinueWith(done => { var ignored = done.Exception; }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private readonly Stopwatch stageClock = Stopwatch.StartNew();
        private bool forcePaint;
        private double lastPaintMs = -1;

        /// Paint statistics (main loop only).
        public UiPerfStats Perf { get; } = new UiPerfStats();

        // ---- dev tools (admin API: dev.key, dev.frame) ----

        private static System.Threading.Tasks.Task<T> OnMainLoop<T>(Func<T> read)
        {
            var done = new System.Threading.Tasks.TaskCompletionSource<T>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            EcoreMainloop.PostAndWakeUp(() =>
            {
                try { done.TrySetResult(read()); }
                catch (Exception error) { done.TrySetException(error); }
            });
            return done.Task;
        }

        /// Nothing moving, nothing to rebuild, no data loading and no picture still on its way to the toolkit.
        private bool Settled => !animator.IsAnimating && Shell.Version == builtVersion && (Shell.Current?.PendingLoads ?? 0) == 0 && painter.PendingImages == 0;

        private object FrameReport(bool nodes, bool overflow, bool? settled)
        {
            var now = clock.ElapsedMilliseconds;
            return new
            {
                screen = Shell.Current?.Kind.ToString(),
                version = Shell.Version,
                settled,
                animating = animator.IsAnimating,
                pendingLoads = Shell.Current?.PendingLoads ?? 0,
                objects = painter.ObjectCount,
                text = UiText.RenderText(Shell),
                perf = Perf.Snapshot(now),
                overflow = overflow ? painter.TextOverflow() : null,
                nodes = nodes && target != null
                    ? target.Nodes.Select(n => new { n.Key, kind = n.Kind.ToString(), n.X, n.Y, n.W, n.H, n.Text, n.FontSize, n.Fill, n.Opacity, n.ImageUrl }).ToList()
                    : null,
            };
        }

        public async System.Threading.Tasks.Task<object> KeyAsync(RemoteKey key, int repeat, int gapMs, int settleMs, System.Threading.CancellationToken token)
        {
            for (var i = 0; i < repeat; i++)
            {
                if (i > 0 && gapMs > 0)
                    await System.Threading.Tasks.Task.Delay(gapMs, token).ConfigureAwait(false);
                // Not OnKey: an unhandled Back on Home must not exit the app (and with it the API).
                await OnMainLoop(() =>
                {
                    lastKeyAt = clock.ElapsedMilliseconds;   // as a remote key: prefetch and the image budget see it
                    Shell.HandleKey(key);
                    Repaint();
                    return true;
                }).ConfigureAwait(false);
            }
            var deadline = clock.ElapsedMilliseconds + settleMs;
            var calm = 0;
            while (clock.ElapsedMilliseconds < deadline && calm < 2)
            {
                await System.Threading.Tasks.Task.Delay(60, token).ConfigureAwait(false);
                calm = await OnMainLoop(() => Settled).ConfigureAwait(false) ? calm + 1 : 0;
            }
            return await OnMainLoop(() => FrameReport(false, false, calm >= 2)).ConfigureAwait(false);
        }

        public System.Threading.Tasks.Task<object> FrameAsync(bool nodes, bool overflow, bool reset, System.Threading.CancellationToken token) =>
            OnMainLoop(() =>
            {
                var report = FrameReport(nodes, overflow, Settled);
                if (reset)
                    Perf.Reset();
                return report;
            });

        public void Dispose()
        {
            if (timer != IntPtr.Zero)
                EcoreMainloop.RemoveTimer(timer);
            timer = IntPtr.Zero;
            keys?.Dispose();
            keys = null;
            probe?.Dispose();
            painter.Dispose();
        }
    }
}
