// Remote UI dev mode, TV side (packages built with -p:RemoteUi=true only; see docs/remote-ui.md).
// The app (AppHost, TvShell, view-models, layout) runs on the Mac dev host (devhost/). This client dials the Mac,
// paints the target frames it receives with the TV's own UiAnimator, lowering and ElmFramePainter, sends remote keys
// back, and executes player and source-opener calls on the TV's real TizenPlayerBackend and TorrentSourceOpener.
// Media still flows from the source to the TV; the Mac only sends frames and commands.
#if REMOTE_UI
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ElmSharp;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using Ru7100.RemoteUi;
using Ru7100.TvUi;

namespace Ru7100Probe
{
    internal sealed class RemoteUiClient : IDisposable
    {
        private const double TickSeconds = 1.0 / 30;
        private readonly Window window;
        private readonly TizenPlayerBackend player;
        private readonly TorrentSourceOpener opener;
        private readonly RemoteUiConfig config;
        private readonly Action exitApp;
        private readonly Action<string> trace;
        private readonly string dataDirectory;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly UiAnimator animator = new UiAnimator();
        private readonly ConcurrentDictionary<long, CancellationTokenSource> rpcs = new ConcurrentDictionary<long, CancellationTokenSource>();
        private readonly ConcurrentDictionary<string, IOpenedSource> sources = new ConcurrentDictionary<string, IOpenedSource>();
        private readonly ConcurrentDictionary<int, long> keySentAt = new ConcurrentDictionary<int, long>();
        private readonly List<long> latencies = new List<long>();   // main thread
        private ElmFramePainter painter;
        private UiAssets assets;
        private EcoreEvent<EcoreKeyEventArgs> keys;
        private IntPtr timer;
        private volatile RemoteUiChannel channel;
        private UiFrame remote;          // last target frame from the Mac (main thread)
        private UiFrame target;          // what the animator heads for: remote, or the waiting screen
        private UiFrame painted;
        private int keySeq;
        private int paintsThisSecond, peakPaintsPerSecond;
        private long secondStartMs, paintTicks, paintCount;
        private string lastSnapshot;

        public RemoteUiClient(Window window, TizenPlayerBackend player, TorrentSourceOpener opener, string dataDirectory,
            RemoteUiConfig config, Action exitApp, Action<string> trace)
        {
            this.window = window;
            this.player = player;
            this.opener = opener;
            this.dataDirectory = dataDirectory;
            this.config = config;
            this.exitApp = exitApp;
            this.trace = trace;
        }

        /// Main thread.
        public void Start()
        {
            var posters = new PosterCache(Path.Combine(dataDirectory, "posters")) { OnError = TvUiDiagnostics.DownloadFailed };
            painter = new ElmFramePainter(window, posters);
            assets = new UiAssets(Path.Combine(dataDirectory, "ui-assets"));
            var warm = assets;
            System.Threading.Tasks.Task.Run(() => { try { warm.Prewarm(); } catch (Exception) { /* generated on demand instead */ } });
            ShowWaiting("Connecting…");
            timer = EcoreMainloop.AddTimer(TickSeconds, () => { Repaint(); return true; });
            keys = new EcoreEvent<EcoreKeyEventArgs>(EcoreEventType.KeyDown, EcoreKeyEventArgs.Create);
            keys.On += (_, args) => OnKey(args.KeyName);
            player.StateChanged += (_, state) => SendSnapshot(true);
            player.SubtitleCue += (_, cue) => Send(new JObject
            {
                ["t"] = "cue", ["text"] = cue.Text, ["startMs"] = cue.StartMs, ["durationMs"] = cue.DurationMs, ["embedded"] = cue.Embedded,
            });
            new Thread(ConnectLoop) { IsBackground = true, Name = "remote-ui" }.Start();
            _ = Task.Run(ReportLoop);
        }

        public void OnLifecycle(string state) => Send(new JObject { ["t"] = "lifecycle", ["state"] = state });

        public void Dispose()
        {
            lifetime.Cancel();
            try { channel?.Dispose(); } catch (Exception) { }
            if (timer != IntPtr.Zero) EcoreMainloop.RemoveTimer(timer);
            timer = IntPtr.Zero;
            keys?.Dispose();
            ReleaseMedia();
            painter?.Dispose();
        }

        // ---- connection ----

        private void ConnectLoop()
        {
            var endpoint = config.Host + ":" + config.Port;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        var connect = tcp.ConnectAsync(config.Host, config.Port);
                        if (!connect.Wait(TimeSpan.FromSeconds(3)) || !tcp.Connected)
                            throw new TimeoutException("no answer from " + endpoint);
                        tcp.NoDelay = true;
                        var ch = new RemoteUiChannel(tcp.GetStream());
                        ch.Send(new JObject { ["t"] = "hello", ["token"] = config.Token, ["version"] = RemoteUiProtocol.Version });
                        var welcome = ch.ReceiveAsync(lifetime.Token).GetAwaiter().GetResult();
                        if ((string)welcome?["t"] != "welcome")
                            throw new InvalidOperationException("the Mac refused the connection: " + ((string)welcome?["error"] ?? "closed"));
                        trace("remote ui: connected to " + endpoint);
                        channel = ch;
                        SendSnapshot(true);
                        while (!lifetime.IsCancellationRequested)
                        {
                            var message = ch.ReceiveAsync(lifetime.Token).GetAwaiter().GetResult();
                            if (message == null) throw new IOException("the Mac closed the connection");
                            Handle(message);
                        }
                    }
                }
                catch (Exception error)
                {
                    var wasConnected = channel != null;
                    channel = null;
                    if (lifetime.IsCancellationRequested) return;
                    var reason = error is AggregateException aggregate ? aggregate.GetBaseException().Message : error.Message;
                    if (wasConnected)
                    {
                        trace("remote ui: disconnected: " + reason);
                        ReleaseMedia();
                    }
                    EcoreMainloop.PostAndWakeUp(() => ShowWaiting(reason));
                }
                Thread.Sleep(1000);
            }
        }

        private void Handle(JObject message)
        {
            switch ((string)message["t"])
            {
                case "frame":
                    var diff = (JObject)message["diff"];
                    var ack = (int?)message["ack"];
                    var reduced = (bool?)message["reducedMotion"] ?? false;
                    var frameId = (long?)message["fid"];
                    EcoreMainloop.PostAndWakeUp(() =>
                    {
                        var started = Stopwatch.GetTimestamp();
                        remote = UiFrameDiff.Apply(remote, diff);
                        target = remote;
                        animator.ReducedMotion = reduced;
                        Repaint();
                        if (frameId.HasValue)
                        {
                            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                            Send(new JObject { ["t"] = "painted", ["fid"] = frameId.Value, ["applyPaintMs"] = Math.Round(ms, 1) });
                        }
                        if (ack.HasValue && keySentAt.TryRemove(ack.Value, out var sentAt))
                            latencies.Add(clock.ElapsedMilliseconds - sentAt);
                    });
                    break;
                case "rpc":
                    _ = Task.Run(() => RunRpcAsync(message));
                    break;
                case "cancel":
                    if (rpcs.TryGetValue((long)message["id"], out var cancel)) cancel.Cancel();
                    break;
                case "exit":
                    EcoreMainloop.PostAndWakeUp(() => exitApp());
                    break;
            }
        }

        private void Send(JObject message)
        {
            var ch = channel;
            if (ch == null) return;
            try { ch.Send(message); }
            catch (Exception) { }   // the read loop notices the broken connection
        }

        // ---- player and source opener RPC ----

        private async Task RunRpcAsync(JObject message)
        {
            var id = (long)message["id"];
            var op = (string)message["op"];
            var args = message["args"] as JObject ?? new JObject();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            rpcs[id] = cts;
            var reply = new JObject { ["t"] = "result", ["id"] = id };
            try
            {
                JToken value = null;
                switch (op)
                {
                    case "player.open": await player.OpenAsync((string)args["url"], (long)args["startMs"], cts.Token).ConfigureAwait(false); break;
                    case "player.play": player.Play(); break;
                    case "player.pause": player.Pause(); break;
                    case "player.stop": player.Stop(); break;
                    case "player.seek": await player.SeekAsync((long)args["positionMs"], cts.Token).ConfigureAwait(false); break;
                    case "player.audio": player.SelectAudio((int)args["index"]); break;
                    case "player.subtitle": player.SelectEmbeddedSubtitle((int?)args["index"]); break;
                    case "player.subtitleOffset": player.SetEmbeddedSubtitleOffset((int)args["offsetMs"]); break;
                    case "player.fullscreen": player.SetFullscreen(); break;
                    case "player.area": player.SetVideoArea((int)args["x"], (int)args["y"], (int)args["w"], (int)args["h"]); break;
                    case "opener.open":
                        var candidate = args["candidate"].ToObject<StreamCandidate>();
                        var source = await opener.OpenAsync(candidate, cts.Token).ConfigureAwait(false);
                        var sourceId = Guid.NewGuid().ToString("N");
                        sources[sourceId] = source;
                        value = new JObject { ["id"] = sourceId, ["url"] = source.PlayerUrl, ["length"] = source.LengthBytes };
                        break;
                    case "source.dispose":
                        if (sources.TryRemove((string)args["id"], out var opened)) opened.Dispose();
                        break;
                    default: throw new NotSupportedException("Unknown remote call " + op);
                }
                reply["ok"] = true;
                if (value != null) reply["value"] = value;
            }
            catch (Exception error)
            {
                reply["ok"] = false;
                reply["cancelled"] = error is OperationCanceledException;
                reply["error"] = error.GetType().Name + ": " + error.Message;
            }
            finally
            {
                rpcs.TryRemove(id, out _);
                cts.Dispose();
            }
            if (op.StartsWith("player.", StringComparison.Ordinal))
                reply["ps"] = PlayerWire.Snapshot(player);
            Send(reply);
        }

        private void SendSnapshot(bool force)
        {
            var snapshot = PlayerWire.Snapshot(player);
            var text = snapshot.ToString(Newtonsoft.Json.Formatting.None);
            if (!force && text == lastSnapshot) return;
            lastSnapshot = text;
            Send(snapshot);
        }

        /// Disconnected: nothing the Mac started may keep playing or downloading.
        private void ReleaseMedia()
        {
            foreach (var rpc in rpcs.Values)
                try { rpc.Cancel(); } catch (Exception) { }
            try
            {
                if (player.State != PlayerState.Idle && player.State != PlayerState.Stopped) player.Stop();
            }
            catch (Exception error) { trace("remote ui: stop: " + error.Message); }
            foreach (var id in sources.Keys.ToList())
                if (sources.TryRemove(id, out var source))
                    try { source.Dispose(); } catch (Exception) { }
        }

        /// Player position every 250 ms while it changes, source stats every second, latency and fps every 5 s.
        private async Task ReportLoop()
        {
            var tick = 0;
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (channel == null) continue;
                tick++;
                try { SendSnapshot(false); } catch (Exception) { }
                if (tick % 4 == 0)
                    foreach (var entry in sources)
                        Send(new JObject { ["t"] = "src", ["id"] = entry.Key, ["rate"] = entry.Value.DownloadBytesPerSecond, ["peers"] = entry.Value.Peers });
                if (tick % 20 == 0)
                    EcoreMainloop.PostAndWakeUp(SendStats);
            }
        }

        private void SendStats()
        {
            var sorted = latencies.OrderBy(v => v).ToList();
            latencies.Clear();
            var stats = new JObject
            {
                ["t"] = "stats",
                ["keys"] = sorted.Count,
                ["peakPaintsPerSecond"] = peakPaintsPerSecond,
                ["avgPaintMs"] = paintCount == 0 ? 0 : Math.Round(paintTicks * 1000.0 / Stopwatch.Frequency / paintCount, 1),
            };
            if (sorted.Count > 0)
            {
                stats["keyToPaintMs"] = new JObject
                {
                    ["avg"] = Math.Round(sorted.Average()), ["p50"] = sorted[sorted.Count / 2],
                    ["p95"] = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))], ["max"] = sorted[sorted.Count - 1],
                };
            }
            peakPaintsPerSecond = 0;
            paintTicks = 0;
            paintCount = 0;
            Send(stats);
        }

        // ---- painting and keys (main thread) ----

        private void Repaint()
        {
            if (target == null || (target == painted && !animator.IsAnimating)) return;
            try
            {
                var started = Stopwatch.GetTimestamp();
                painted = target;
                painter.Paint(UiFrameLowering.Lower(animator.Step(target, clock.ElapsedMilliseconds), assets));
                paintTicks += Stopwatch.GetTimestamp() - started;
                paintCount++;
                var now = clock.ElapsedMilliseconds;
                if (now - secondStartMs >= 1000)
                {
                    secondStartMs = now;
                    paintsThisSecond = 0;
                }
                peakPaintsPerSecond = Math.Max(peakPaintsPerSecond, ++paintsThisSecond);
            }
            catch (Exception error)
            {
                trace("remote ui: paint failed: " + error);
            }
        }

        private void OnKey(string name)
        {
            if (channel == null)
            {
                if (RemoteKeys.FromTizen(name) == RemoteKey.Back) exitApp();
                return;
            }
            var seq = ++keySeq;
            keySentAt[seq] = clock.ElapsedMilliseconds;
            Send(new JObject { ["t"] = "key", ["name"] = name, ["seq"] = seq });
        }

        private void ShowWaiting(string detail)
        {
            remote = null;   // the next connection starts with a full frame
            var frame = new UiFrame { Background = "#0B0B10", ScreenKey = "remote-ui-waiting" };
            frame.Text("remote-ui:title", 160, 430, 1600, 80, "Waiting for Mac dev server…", 56, "#FFFFFF", true, UiAlign.Center);
            frame.Text("remote-ui:where", 160, 530, 1600, 50, config.Host + ":" + config.Port + "  (dotnet run --project devhost)", 32, "#A0A0AA", false, UiAlign.Center);
            frame.Text("remote-ui:detail", 160, 600, 1600, 50, detail ?? "", 28, "#70707A", false, UiAlign.Center);
            frame.Text("remote-ui:back", 160, 940, 1600, 40, "Back closes the app", 26, "#70707A", false, UiAlign.Center);
            target = frame;
            Repaint();
        }
    }
}
#endif
