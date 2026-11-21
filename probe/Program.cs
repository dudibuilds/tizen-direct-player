// RU7100 Direct Player: the TV app. Composes the remote UI (core/Ui on ElmSharp), the playback session engine
// (core/Playback on the Tizen hardware player and the on-TV torrent engine) and the agent API (core/Api) through
// core/App's AppHost, so remote keys and MCP commands drive the same session.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ElmSharp;
using Ru7100.Core.Api;
using Ru7100.Core.App;
using Ru7100.Core.Ui;
using Ru7100.TvUi;
using Tizen.Applications;

namespace Ru7100Probe
{
    internal sealed class Program : CoreUIApplication
    {
        private const string LogTag = "RU7100";
        /// Development transport: loopback only, reached through `sdb forward tcp:45551 tcp:45551`.
        private const string ApiPrefix = "http://127.0.0.1:45551/";
        private const string KeysResource = "Ru7100Probe.api-keys.json";
        /// Idle torrent data kept for quick re-opens (binge, failover back, restore); older idle torrents are evicted
        /// first. Conservative because the TV's app storage is small (the installer has failed with storage error 116).
        /// The file being played may still grow to its full size (6.5 GB cap).
        private const long IdleCacheBudgetBytes = 512L * 1024 * 1024;
        private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(3);

        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Window window;
        private EcoreDispatcher dispatcher;
        private TizenPlayerBackend player;
        private DeferredSourceOpener openerSource;
        /// The torrent engine once built (it is built on first use or by the warm-up after Home shows), else null.
        private TorrentSourceOpener opener => openerSource?.IfCreated;
        /// The app's start-up reads (settings, sign-in, library, playback), started as soon as the services exist.
        private Task init;
        private AppHost app;
        private TvUiHost ui;
        private Ru7100Probe.Trailer.TizenTrailerPlayer trailers;
        private ApiHttpServer api;

        private static string tracePath;

        /// Startup trace in shared/trusted (readable with `sdb pull`), because retail TVs block dlog.
        /// Start-up lines with the time since the process started, kept until the app's log exists and then copied
        /// into it, so dev.diagnostics shows where launch time goes (sdb pull of startup.log is refused on retail TVs).
        private static readonly System.Collections.Generic.List<string> early = new System.Collections.Generic.List<string>();
        private static Action<string> appLog;
        private static readonly DateTime processStart = SafeProcessStart();

        private static DateTime SafeProcessStart()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch (Exception) { return DateTime.UtcNow; }
        }

        private static int SafePid()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch (Exception) { return -1; }
        }

        internal static void Trace(string line)
        {
            try
            {
                Ru7100.TvUi.LaunchProfile.Mark(line);
                if (line == "intro: done")
                {
                    Ru7100.TvUi.LaunchProfile.StopAfter(4000);
                    System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
                }
                line = "+" + (long)(DateTime.UtcNow - processStart).TotalMilliseconds + " ms " + line;
                if (appLog != null) appLog(line);
                else lock (early) early.Add(line);
                Tizen.Log.Info(LogTag, line);
                // Only fatal lines go to startup.log: a synchronous file write per line cost the main loop at launch.
                if (tracePath != null && line.IndexOf("FATAL", StringComparison.Ordinal) >= 0)
                    File.AppendAllText(tracePath, DateTime.UtcNow.ToString("O") + " " + line + "\n");
            }
            catch (Exception) { }
        }

        private static void Main(string[] args)
        {
            // Start-up runs many small reads on pool threads at once; the older runtime grows the pool slowly, so give
            // it enough threads up front instead of queueing them.
            ThreadPool.SetMinThreads(12, 12);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace("FATAL unhandled (terminating=" + e.IsTerminating + " pid=" + SafePid() + "): " + e.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved();
                if (!CrashFilter.IsExpected(e.Exception))
                    Trace("unobserved task: " + e.Exception);
            };
            try
            {
                using (var application = new Program())
                    application.Run(args);
            }
            catch (Exception error)
            {
                Trace("FATAL main: " + error);
                throw;
            }
        }

        protected override void OnCreate()
        {
            Ru7100.TvUi.LaunchProfile.ProcessMs = () => (long)(DateTime.UtcNow - processStart).TotalMilliseconds;
            base.OnCreate();
            try
            {
                var trusted = Current.DirectoryInfo.SharedTrusted;
                Directory.CreateDirectory(trusted);
                tracePath = Path.Combine(trusted, "startup.log");
                File.WriteAllText(tracePath, "");
            }
            catch (Exception) { }
            Trace("create: begin");
            // Newtonsoft's first typed read and the HTTP stack each load many assemblies on first use: do that now on an
            // idle core, so the services and the start-up reads don't pay for it on their critical path.
            Task.Run(() =>
            {
                try
                {
                    Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>("{\"a\":\"b\"}");
                    Newtonsoft.Json.Linq.JObject.Parse("{\"a\":[1]}");
                    using (new System.Net.Http.HttpClientHandler()) { }
                    Trace("warm-up: json + http loaded");
                }
                catch (Exception) { }
            });
            try { Create(); Trace("create: done"); }
            catch (Exception error)
            {
                Trace("FATAL create: " + error);
                ShowFatal(error);
            }
        }

        /// Keeps the app open with the error on screen instead of silently returning to the TV's Apps screen.
        private static AppHost NewAppHost(AppPlatform platform)
        {
            var host = new AppHost(platform);
            appLog = host.Log;
            lock (early)
            {
                foreach (var line in early) host.Log(line);
                early.Clear();
            }
            return host;
        }

        private void ShowFatal(Exception error)
        {
            try
            {
                if (window == null) { window = new Window("Ru7100") { BackgroundColor = Color.Black }; window.Show(); }
                var platformJson = "unknown";
                try { platformJson = Assembly.Load(new AssemblyName("Newtonsoft.Json")).FullName; }
                catch (Exception probe)
                {
                    try { platformJson = AssemblyName.GetAssemblyName("/usr/share/dotnet.tizen/framework/Newtonsoft.Json.dll").FullName; }
                    catch (Exception) { platformJson = "load failed: " + probe.Message; }
                }
                Trace("platform Newtonsoft.Json: " + platformJson);
                var label = new Label(window) { Geometry = new Rect(60, 60, 1800, 960), LineWrapType = WrapType.Mixed };
                label.Text = "<color=#ffffff><font_size=26>Startup error<br>Platform Newtonsoft.Json: " +
                    System.Net.WebUtility.HtmlEncode(platformJson) + "<br>" +
                    System.Net.WebUtility.HtmlEncode(error.ToString()).Replace("\n", "<br>") + "</font_size></color>";
                label.Show();
            }
            catch (Exception) { }
        }

        private EvasImage splash;

        /// Start-up, lean first: the window and a static splash (the intro's first frame: the amber front chevron,
        /// centred) paint right away, and everything heavy is built after that first frame (CreateRest), so the TV's
        /// loading spinner goes away as early as possible.
        private void Create()
        {
            window = new Window("Ru7100") { BackgroundColor = new Color(0x0B, 0x0E, 0x14, 255) };   // the ink page: launch never flashes black before the intro
            window.Show();
            dispatcher = new EcoreDispatcher();
            Trace("create: window");
            ShowSplash();
            CreateRest();
        }

        private void ShowSplash()
        {
            try
            {
                var file = Path.Combine(DirectoryInfo.SharedResource, "ui", "glyph-" + UiGlyphs.MarkFront + "-" + UiGlyphs.RasterSize(UiGlyphs.MarkFront) + ".png");
                if (!File.Exists(file)) return;
                var size = (int)Math.Round(UiWordmark.MarkSize * LaunchIntro.Scale);
                splash = new EvasImage(window) { IsSmoothScaled = true, File = file, IsFilled = true, Color = new Color(0xFF, 0xB2, 0x3E, 255) };
                splash.Geometry = new Rect((UiFrame.Width - size) / 2, (UiFrame.Height - size) / 2, size, size);
                splash.Show();
                Trace("create: splash");
            }
            catch (Exception error) { Trace("splash: " + error.Message); }
        }

        private IntPtr breathTimer = IntPtr.Zero;

        /// While the services are built, the splash chevron breathes (a slow fade between 70% and 100%) so the launch
        /// never looks frozen; it ends at full strength, the intro's first frame.
        private void BreatheSplash()
        {
            if (splash == null) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            breathTimer = EcoreMainloop.AddTimer(1.0 / 30, () =>
            {
                if (splash == null) { breathTimer = IntPtr.Zero; return false; }
                var k = 0.85 + 0.15 * Math.Cos(clock.Elapsed.TotalSeconds * 2 * Math.PI / 1.6);
                splash.Color = new Color((int)(0xFF * k), (int)(0xB2 * k), (int)(0x3E * k), (int)(255 * k));
                return true;
            });
        }

        private static void LaunchProfileTimed(string what, Action action) => Ru7100.TvUi.LaunchProfile.Timed(what, action);

        private void StopBreathing()
        {
            if (breathTimer != IntPtr.Zero) { EcoreMainloop.RemoveTimer(breathTimer); breathTimer = IntPtr.Zero; }
            try { if (splash != null) splash.Color = new Color(0xFF, 0xB2, 0x3E, 255); } catch (Exception) { }
        }

        private void HideSplash()
        {
            if (breathTimer != IntPtr.Zero) { EcoreMainloop.RemoveTimer(breathTimer); breathTimer = IntPtr.Zero; }
            try { splash?.Hide(); splash?.Unrealize(); } catch (Exception) { }
            splash = null;
        }

        private void CreateRest()
        {
            var data = DirectoryInfo.Data;
            WatchForCrashes(data);
            StartRunLog(data);
            Trace("create: player");
            player = new TizenPlayerBackend(window, dispatcher.Post, line => Tizen.Log.Warn(LogTag, "player: " + line));
#if REMOTE_UI
            CreateServices(data);
            CreateUi(data);
#else
            // The torrent engine and the app's services are plain .NET: build them off the main loop, so the splash
            // keeps breathing, then come back to the main loop for the UI (EFL objects must be made there).
            BreatheSplash();
            Task.Run(() =>
            {
                try
                {
                    ResetCachesOnce(data);
                    RemoveLegacyProbeData(data);
                    CreateServices(data);
                }
                catch (Exception error) { Trace("FATAL services: " + error); dispatcher.Post(() => ShowFatal(error)); return; }
                dispatcher.Post(() =>
                {
                    try { CreateUi(data); Trace("create: ui done"); }
                    catch (Exception error) { Trace("FATAL create ui: " + error); ShowFatal(error); }
                });
            });
#endif
        }

        private void CreateServices(string data)
        {
            // The torrent engine is built on first use or by the warm-up after Home shows (CreateUi), not on the launch path.
            // The media endpoint AVPlay reads from (process-long, MiniHttpServer): listener lifecycle and connection faults.
            MediaEndpoint.Log = line => Trace("http " + line);
            var utp = !File.Exists(Path.Combine(data, "utp-off"));   // an empty "utp-off" file in the data folder turns uTP off (A/B on the TV)
            openerSource = new DeferredSourceOpener(() =>
            {
                Trace("create: opener");
                var built = new TorrentSourceOpener(new TorrentSourceSettings
                {
                    RootDirectory = Path.Combine(data, "media"),
                    IdleCacheBudgetBytes = IdleCacheBudgetBytes,
                    EnableUtp = utp,
                    // One sink: AppHost.Log keeps the tail and writes the Tizen log itself (this used to write each line twice).
                    Log = line => { var host = app; if (host != null) host.Log("torrent: " + line); else Tizen.Log.Info(LogTag, "torrent: " + line); },
                });
                Trace("create: opener done");
                return built;
            });
#if REMOTE_UI
            // Dev package (scripts/tv-dev.sh --remote-ui): the app runs on the Mac dev host; this is a thin client.
            var remoteConfig = Ru7100.RemoteUi.RemoteUiConfig.Parse(ReadResource(RemoteUiResource) ?? throw new InvalidOperationException("No remote UI config embedded"));
            Trace("create: remote ui client for " + remoteConfig.Host + ":" + remoteConfig.Port);
            remoteUi = new RemoteUiClient(window, player, openerSource.Value, data, remoteConfig, Exit, Trace);
            remoteUi.Start();
            return;
#else
            Trace("create: apphost");
            app = NewAppHost(new AppPlatform
            {
                Store = new FileKeyValueStore(Path.Combine(data, "store")),
                Player = player,
                Opener = openerSource,
                Dispatcher = dispatcher,
                // One HTTP handler for add-ons, the Stremio API, subtitles and extras (connections and TLS are reused).
                Http = new System.Net.Http.HttpClientHandler(),
                Log = line => Tizen.Log.Info(LogTag, line),
                // Cheap by default (safe to poll during playback); {"full": true} adds the storage scan and the UI stacking dump.
                Diagnostics = () => new { mediaHttp = MediaEndpoint.StatsOf(TorrentMediaServer.Port), apiHttp = api?.Stats, cacheMb = (opener?.CacheAllocatedBytes ?? 0) / (1024 * 1024), activeTorrents = opener?.ActiveTorrents ?? 0, torrentNetwork = opener?.NetworkSummary, torrentNet = opener?.NetworkHealth, torrentConnections = opener?.ConnectionSummary, dataDirectory = data, lastCrash = ReadLastCrash(data), previousExit, expectedUnobserved = Interlocked.Read(ref expectedUnobserved), rssMb = ReadRssMb(), launchProfile = Ru7100.TvUi.LaunchProfile.Snapshot(), ui = ui == null ? null : TvUiDiagnostics.Snapshot(ui, stacking: false) },
                FullDiagnostics = () => new { activeTorrents = opener?.ActiveTorrents ?? 0, torrentNetwork = opener?.NetworkSummary, torrentNet = opener?.NetworkHealth, torrentConnections = opener?.ConnectionSummary, dataDirectory = data, storage = StorageReport(data), lastCrash = ReadLastCrash(data), previousExit, expectedUnobserved = Interlocked.Read(ref expectedUnobserved), rssMb = ReadRssMb(), launchProfile = Ru7100.TvUi.LaunchProfile.Snapshot(), ui = ui == null ? null : TvUiDiagnostics.Snapshot(ui) },
            });
            Trace("create: apphost done");
            // Start-up reads begin now, while the UI is being built on the main loop.
            init = Task.Run(async () =>
            {
                try { await app.InitializeAsync(lifetime.Token).ConfigureAwait(false); }
                catch (Exception error) { Trace("init: " + error); }
                Trace("init: done");
            });
#endif
        }

        private void CreateUi(string data)
        {
#if !REMOTE_UI
            StopBreathing();   // building the UI holds the main loop ~0.6 s: settle the splash first so it never hitches
            Trace("create: ui");
            trailers = new Ru7100Probe.Trailer.TizenTrailerPlayer(window, dispatcher.Post, () => player.IsBusy, player.ReleaseIfIdle,
                id => app?.Ui.ReportTrailerEnded(id), line => Trace(line), id => app?.Ui.ReportTrailerStarted(id));
            // Packaged images (shared/res), loaded straight from the package: no network, nothing to fail.
            app.Ui.ResourceUrl = relative => "file://" + Path.Combine(DirectoryInfo.SharedResource, relative);
            // Off the UI thread: the first use loads Tizen's audio library (~0.2 s), which must not hold the intro's frames.
            app.Ui.LaunchSound = () => Task.Run(() => LaunchSound.Play());
            ui = new TvUiHost(window, app.Ui, Path.Combine(data, "posters"), ToBackground, trailers: trailers);
            app.AttachShell(ui.Shell);
            UiDevCommands.Register(app.Router, ui);

            // Home and the intro start now; Home loads its rows once the start-up reads (begun with the services) are in.
            try
            {
                // Start the intro with a clean heap (fewer collections mid-animation) and keep blocking collections
                // rare until it is done (restored on "intro: done").
                GC.Collect();
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
                ui.Start(new HomeViewModel().WithLaunchIntro(
                    () => init.ContinueWith(_ => dispatcher.Post(() => { if (!lifetime.IsCancellationRequested) app.OfferRestore(); }), TaskScheduler.Default),
                    init));
                // One screen tick now, while the splash is still static: the first tick compiles Home's tick paths
                // (~0.2-0.5 s on the TV), which otherwise lands on the intro's first beat. The intro hasn't started
                // yet (it waits for the first frame on screen), so this only warms the code.
                LaunchProfileTimed("warm tick", () => ui.Shell.Tick());
                ui.Repaint();
                HideSplash();
                Trace("ui: home shown");
                // Not needed for the first frames: the agent API, then the torrent engine and its warm-up once the intro
                // has had time to hand over.
                Task.Run(() => { Trace("create: api"); StartApi(); });
                StartSoakSampler();
                Task.Delay(TimeSpan.FromSeconds(4), lifetime.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    try { openerSource.Value.Warm(); } catch (Exception error) { Trace("warm: " + error.Message); }
                }, TaskScheduler.Default);
            }
            catch (Exception error) { Trace("FATAL ui start: " + error); ShowFatal(error); }
#endif
        }

#if REMOTE_UI
        private const string RemoteUiResource = "Ru7100Probe.remote-ui.json";
        private RemoteUiClient remoteUi;

        private static string ReadResource(string name)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (resource == null) return null;
                using (var reader = new StreamReader(resource))
                    return reader.ReadToEnd();
            }
        }
#endif

        /// TV Home or Source pressed: nothing should keep playing unseen.
        // ---- Staying resident (instant relaunch, like Netflix) ----
        // Back on Home sends the app to the background instead of quitting, so the next launch is a resume with no TV
        // spinner. After TrimDelay in the background it releases what other apps need (decoders) and what is cheap to
        // rebuild (prefetches, garbage), and it trims at once when the TV reports low memory.
        private static readonly TimeSpan TrimDelay = TimeSpan.FromSeconds(30);
        private bool paused;
        private IntPtr trimTimer = IntPtr.Zero;
        private bool trimmed;

        private void ToBackground()
        {
            Trace("back: to background");
            RunEvent("back: to background");
            try { window.Lower(); }
            catch (Exception error) { Trace("back: lower failed: " + error.Message); RunEvent("back: exit (lower failed)"); Exit(); return; }
            // If the platform did not take us to the background, quit as before rather than leave Back dead.
            EcoreMainloop.AddTimer(1.0, () =>
            {
                if (!paused) { Trace("back: still in front, exiting"); RunEvent("back: exit (still in front)"); Exit(); }
                return false;
            });
        }

        /// Main loop. Releases what is idle first (trailer, a finished player's decoder, prefetches, idle torrent caches).
        /// During playback that is all: a forced compacting collection stops every managed thread (media delivery
        /// included) and waiting for finalizers here would hold the main loop, so the collection is a background one.
        /// With nothing playing, the full compaction runs off the main loop.
        private void Trim(string why)
        {
            try
            {
                var before = ReadRssMb();
                trailers?.Hide();
                player?.ReleaseIfIdle();
                opener?.ReleasePrefetches();
                var reclaimed = opener?.ReclaimIdleMemory(why) ?? 0;
                trimmed = true;
                var playing = player?.IsBusy == true || app?.Session?.IsBusy == true;
                Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("memory", "trim (" + why + ") rss=" + before + " MB playing=" + playing + " reclaimed=" + reclaimed / (1024 * 1024) + " MB");
                if (playing)
                {
                    GC.Collect(2, GCCollectionMode.Optimized, false);
                    Trace("trim (" + why + ") during playback: idle resources released, no forced compaction; rss " + before + " MB");
                    return;
                }
                Task.Run(() =>
                {
                    try
                    {
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        GC.WaitForPendingFinalizers();
                        Trace("trim (" + why + "): rss " + before + " -> " + ReadRssMb() + " MB");
                    }
                    catch (Exception error) { Trace("trim: " + error.Message); }
                });
            }
            catch (Exception error) { Trace("trim: " + error.Message); }
        }

        /// Resident memory of this process in MB (/proc/self/status VmRSS), or -1.
        internal static int ReadRssMb()
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/self/status"))
                    if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                        return int.Parse(line.Substring(6).Trim().Split(' ')[0]) / 1024;
            }
            catch (Exception) { }
            return -1;
        }

        protected override void OnLowMemory(LowMemoryEventArgs e)
        {
            base.OnLowMemory(e);
            Trace("low memory: " + e.LowMemoryStatus);
            RunEvent("low memory: " + e.LowMemoryStatus);
            Trim("low memory");
        }

        protected override void OnPause()
        {
            base.OnPause();
            paused = true;
            RunEvent("background rss=" + ReadRssMb() + "MB");
            trimmed = false;
            if (trimTimer != IntPtr.Zero) EcoreMainloop.RemoveTimer(trimTimer);
            trimTimer = EcoreMainloop.AddTimer(TrimDelay.TotalSeconds, () =>
            {
                trimTimer = IntPtr.Zero;
                if (paused) Trim("background");
                return false;
            });
            trailers?.Hide();   // no trailer sound behind the TV's Home screen
            if (app != null)
                _ = app.PauseForBackgroundAsync(CancellationToken.None);
#if REMOTE_UI
            remoteUi?.OnLifecycle("pause");
#endif
        }

        protected override void OnResume()
        {
            base.OnResume();
            paused = false;
            RunEvent("foreground");
            if (trimTimer != IntPtr.Zero) { EcoreMainloop.RemoveTimer(trimTimer); trimTimer = IntPtr.Zero; }
            Trace("resume" + (trimmed ? " (after trim)" : "") + ": rss " + ReadRssMb() + " MB");
            ui?.Repaint();
            if (app != null)
                _ = app.ResumeFromBackgroundAsync(CancellationToken.None);
#if REMOTE_UI
            remoteUi?.OnLifecycle("resume");
#endif
        }

        protected override void OnTerminate()
        {
            Trace("terminate");
            RunEvent("terminate");
            lifetime.Cancel();
            try { soakTimer?.Dispose(); } catch (Exception) { }
            try { api?.Dispose(); } catch (Exception) { }
            if (app != null)
            {
                // Save the session (it stays restorable) and flush pending writes. Bounded: a start in progress may hold
                // the engine while it waits for the main loop this method is blocking.
                try { Task.Run(() => app.ShutdownAsync(CancellationToken.None)).Wait(ShutdownBudget); }
                catch (Exception error) { Tizen.Log.Warn(LogTag, "shutdown: " + error.Message); }
            }
            ui?.Dispose();
            trailers?.Dispose();
            app?.Dispose();
#if REMOTE_UI
            remoteUi?.Dispose();
#endif
            player?.Dispose();    // on the main thread, so the release runs inline
            opener?.Dispose();     // stops the torrent engine
            base.OnTerminate();
        }

        /// Free space and the app's own disk use per data subfolder (MB), to see what fills the TV's storage.
        // ---- run log: why each run ended ----
        // One line per lifecycle event, appended as it happens (start, background, foreground, Back, low memory,
        // terminate, crash). The TV can end a backgrounded or memory-starved app with no callback, so a run whose
        // last line is not "terminate" was ended without notice. The previous run's file is kept for diagnostics.
        private const string RunLogFile = "run-log.txt";
        private const string PreviousRunLogFile = "run-log-previous.txt";
        private static string runLogPath;
        private static Ru7100.Core.App.ExitHistory.Summary previousExit;

        private static void StartRunLog(string data)
        {
            try
            {
                runLogPath = Path.Combine(data, RunLogFile);
                var previousPath = Path.Combine(data, PreviousRunLogFile);
                if (File.Exists(runLogPath))
                {
                    if (File.Exists(previousPath)) File.Delete(previousPath);
                    File.Move(runLogPath, previousPath);
                }
                var lines = File.Exists(previousPath) ? File.ReadAllLines(previousPath) : Array.Empty<string>();
                previousExit = Ru7100.Core.App.ExitHistory.Summarize(lines);
                Trace("previous run: " + previousExit.Kind + " - " + previousExit.Text);
            }
            catch (Exception error) { Trace("run log: " + error.Message); }
            RunEvent("start pid=" + SafePid());
        }

        private static void RunEvent(string what)
        {
            var path = runLogPath;
            if (path == null) return;
            try { File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + what + "\n"); }
            catch (Exception) { }
        }

        private const string CrashFile = "last-crash.txt";
        private static long expectedUnobserved;

        /// Writes unhandled exceptions from any thread to the data folder (a crash on a background thread kills the app
        /// before ShowFatal can draw anything); dev.diagnostics shows the file after the next launch.
        private static void WatchForCrashes(string data)
        {
            void Write(string kind, object error)
            {
                try
                {
                    var text = DateTime.UtcNow.ToString("O") + " " + kind + ": " + error + "\n";
                    // The playback timeline lives in RAM only: keep its tail with a fatal crash for the soak test.
                    if (kind.StartsWith("unhandled", StringComparison.Ordinal))
                        RunEvent("crash " + kind);
                    if (kind.StartsWith("unhandled", StringComparison.Ordinal))
                        text += "timeline:\n  " + string.Join("\n  ", Ru7100.Core.Playback.PlaybackTimeline.Shared.Lines(60)) + "\n";
                    var path = Path.Combine(data, CrashFile);
                    var old = File.Exists(path) ? File.ReadAllText(path) : "";
                    if (old.Length > 16000) old = old.Substring(old.Length - 16000);
                    File.WriteAllText(path, old + text);
                }
                catch (Exception) { }
                Trace("FATAL " + kind + ": " + error);
            }
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Write("unhandled (terminating=" + e.IsTerminating + " pid=" + SafePid() + ")", e.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                e.SetObserved();
                if (CrashFilter.IsExpected(e.Exception))
                {
                    // Expected teardown (an aborted socket call, a cancellation): counted, kept out of the crash file.
                    Interlocked.Increment(ref expectedUnobserved);
                    return;
                }
                Write("unobserved task", e.Exception);
            };
        }

        private static string ReadLastCrash(string data)
        {
            try { var path = Path.Combine(data, CrashFile); return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception error) { return "unreadable: " + error.Message; }
        }

        /// One-time recovery (2026-11-19): the app crashed at every launch with all builds, most likely on damaged saved
        /// torrent state. Clears caches once (torrent metadata/DHT cache, posters, generated UI assets); keeps the store
        /// (Stremio sign-in, add-ons, preferences).
        private static void ResetCachesOnce(string data)
        {
            var marker = Path.Combine(data, "cache-reset-20261119");
            if (File.Exists(marker)) return;
            foreach (var name in new[] { "media", "posters", "ui-assets", "ui-assets-v2", "ui-assets-v3", "dev-payload", "native-spike" })
            {
                try { var dir = Path.Combine(data, name); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch (Exception error) { Trace("cache reset " + name + ": " + error.Message); }
            }
            try { File.WriteAllText(marker, DateTime.UtcNow.ToString("O")); } catch (Exception) { }
            Trace("cache reset: done");
        }

        private static object StorageReport(string data)
        {
            long Size(string dir)
            {
                try { return new System.IO.DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
                catch (Exception) { return -1; }
            }
            try
            {
                var folders = Directory.GetDirectories(data).ToDictionary(Path.GetFileName, d => Size(d) / (1024 * 1024));
                var files = Directory.GetFiles(data).Sum(f => new FileInfo(f).Length) / (1024 * 1024);
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.TotalSize > 0)
                    .Select(d => new { root = d.RootDirectory.FullName, freeMb = d.AvailableFreeSpace / (1024 * 1024), totalMb = d.TotalSize / (1024 * 1024) }).ToList();
                return new { freeMb = TorrentSourceOpener.FreeBytes(data) / (1024 * 1024), dataFoldersMb = folders, dataFilesMb = files, drives };
            }
            catch (Exception error) { return new { error = error.Message }; }
        }

        // ---- soak-test sampler (dev.timeline kind "process") ----
        private static long heartbeatMs = -1;
        private static readonly System.Diagnostics.Stopwatch uptime = System.Diagnostics.Stopwatch.StartNew();
        private System.Threading.Timer soakTimer;
        private long soakMaxGap;

        /// Every 10 s while a session is active (every 60 s otherwise): RSS, GC collections, pool threads, stream cache
        /// RAM, media server counters and the longest gap between 250 ms main-loop heartbeats. Nothing here waits on the
        /// main loop; the heartbeat is a timer that only stores a timestamp.
        private void StartSoakSampler()
        {
            EcoreMainloop.AddTimer(0.25, () =>
            {
                var now = uptime.ElapsedMilliseconds;
                var last = Interlocked.Exchange(ref heartbeatMs, now);
                if (last >= 0 && now - last > Interlocked.Read(ref soakMaxGap))
                    Interlocked.Exchange(ref soakMaxGap, now - last);
                return !lifetime.IsCancellationRequested;
            });
            var quietTicks = 0;
            soakTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var busy = app?.Session?.IsBusy == true;
                    if (!busy && ++quietTicks % 6 != 0)
                        return;
                    var gap = Interlocked.Exchange(ref soakMaxGap, 0);
                    var sinceBeat = uptime.ElapsedMilliseconds - Interlocked.Read(ref heartbeatMs);
                    ThreadPool.GetAvailableThreads(out var workers, out var io);
                    ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
                    var media = MediaEndpoint.StatsOf(TorrentMediaServer.Port);
                    var cacheMb = opener == null ? 0 : opener.CacheAllocatedBytes / (1024 * 1024);
                    Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("process",
                        $"rss={ReadRssMb()}MB gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} heap={GC.GetTotalMemory(false) / (1024 * 1024)}MB " +
                        $"cache={cacheMb}MB pool={maxWorkers - workers}/{maxIo - io} eflGapMax={gap}ms eflSinceBeat={sinceBeat}ms " +
                        (media == null ? "media=off" : $"media open={media.Open} req={media.Requests} faults={media.Faults} timeouts={media.Timeouts} acceptErrors={media.AcceptErrors}") +
                        $" unobservedExpected={Interlocked.Read(ref expectedUnobserved)}");
                }
                catch (Exception error) { Trace("soak sampler: " + error.Message); }
            }, null, 10000, 10000);
        }

        private void StartApi()
        {
            string keysJson;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(KeysResource))
            {
                if (resource == null)
                {
                    Trace("agent API disabled: the package has no API keys (build with scripts/package-probe.sh)");
                    return;
                }
                using (var reader = new StreamReader(resource))
                    keysJson = reader.ReadToEnd();
            }
            try
            {
                var keys = ApiKey.ParseKeyFile(keysJson);
                api = new ApiHttpServer(app.Router, new ApiAuthenticator(keys), new[] { ApiPrefix });
                api.Log += line => Tizen.Log.Info(LogTag, "api: " + line);
                api.Start();
                Trace("agent API on " + ApiPrefix + " (" + keys.Count + " keys)");
            }
            catch (Exception error)
            {
                api = null;
                Trace("agent API unavailable: " + error);
            }
        }

        /// The Sintel probe kept its runs and position here; the app does not use them.
        private static void RemoveLegacyProbeData(string data)
        {
            var legacy = Path.Combine(data, "direct-playback-probe");
            if (!Directory.Exists(legacy)) return;
            Task.Run(() =>
            {
                try { Directory.Delete(legacy, true); }
                catch (Exception error) { Tizen.Log.Warn(LogTag, "legacy cleanup: " + error.Message); }
            });
        }
    }
}
