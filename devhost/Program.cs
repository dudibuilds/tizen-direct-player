// Remote UI dev host: the TV app's composition (core/App AppHost + core/Ui TvShell, layout and view-models) running
// on the Mac, with the TV as display, remote and player. See docs/remote-ui.md.
//
//   scripts/tv-dev.sh --remote-ui                 once: installs the TV client (dials this Mac on port 45560)
//   dotnet watch --project devhost run            restarts on every code change; the TV reconnects by itself
//
// Options: --config <file> (default: artifacts/ru7100-remote-ui.json in the main checkout), --data <dir>
// (default: local-data/devhost), --api-port <port> (agent API on 127.0.0.1, default 45553; 0 turns it off).
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Api;
using Ru7100.Core.App;
using Ru7100.Core.Ui;
using Ru7100.RemoteUi;

namespace Ru7100.DevHost
{
    /// One "UI thread": keys, ticks and posted continuations run under one lock, like the EFL main loop on the TV.
    internal sealed class UiLoop : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> posted = new ConcurrentQueue<Action>();
        public readonly object Gate = new object();
        public readonly AutoResetEvent Wake = new AutoResetEvent(false);

        public void Post(Action action)
        {
            posted.Enqueue(action);
            Wake.Set();
        }

        public void Drain()
        {
            while (posted.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception error) { Console.Error.WriteLine("UI action failed: " + error); }
            }
        }
    }

    internal static class Program
    {
        private const int FrameMs = 33;          // at most 30 frames a second
        private const int ShellTickMs = 100;     // same cadence as the TV host

        private static readonly UiLoop loop = new UiLoop();
        private static readonly RemoteTv tv = new RemoteTv();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static AppHost app;
        private static TvShell shell;
        private static RemoteUiChannel client;          // the connected TV (one at a time)
        private static UiFrame sent;                    // last target frame the TV has (null: send a full frame)
        private static int builtVersion = -1;
        private static int pendingAck;
        // Mac-side cost per key: handle + layout + diff + send, and bytes per frame.
        private static long keyWorkTicks, keyWorkCount, frameBytes, frameCount;
        // Frame round trips: Mac sends frame -> TV applies and paints -> TV says painted.
        private static long nextFrameId;
        private static readonly ConcurrentDictionary<long, long> frameSentAt = new ConcurrentDictionary<long, long>();
        private static readonly ConcurrentQueue<double> roundTrips = new ConcurrentQueue<double>();
        private static readonly ConcurrentQueue<double> tvApplyPaint = new ConcurrentQueue<double>();

        public static int Main(string[] args)
        {
            var repo = FindRepo();
            var artifacts = Environment.GetEnvironmentVariable("RU7100_ARTIFACTS") ?? Path.Combine(repo, "artifacts");
            var configPath = Path.Combine(artifacts, "ru7100-remote-ui.json");
            var dataDirectory = Path.Combine(repo, "local-data", "devhost");
            var apiPort = 45553;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else if (args[i] == "--data" && i + 1 < args.Length) dataDirectory = args[++i];
                else if (args[i] == "--api-port" && i + 1 < args.Length) apiPort = int.Parse(args[++i]);
            }
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine("No remote UI config at " + configPath + ". Run scripts/tv-dev.sh --remote-ui once (it creates it and installs the TV client).");
                return 2;
            }
            var config = RemoteUiConfig.Parse(File.ReadAllText(configPath));
            Directory.CreateDirectory(dataDirectory);

            app = new AppHost(new AppPlatform
            {
                Store = new FileKeyValueStore(Path.Combine(dataDirectory, "store")),
                Player = tv.Player,
                Opener = tv.Opener,
                Dispatcher = loop,
                Log = line => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss ") + line),
                Diagnostics = () => new { remoteUi = new { tvConnected = tv.Connected } },
            });
            app.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            lock (loop.Gate)
            {
                shell = new TvShell(app.Ui);
                app.AttachShell(shell);
                shell.Start(new HomeViewModel().WithLaunchIntro(() => app.OfferRestore()));
            }
            StartApi(apiPort, Path.Combine(artifacts, "ru7100-api-keys.json"));

            // LAN interface only: the address the TV dials.
            var listener = new TcpListener(IPAddress.Parse(config.Host), config.Port);
            listener.Start();
            Console.WriteLine("Remote UI dev host on " + config.Host + ":" + config.Port + " (data " + dataDirectory + "). Waiting for the TV…");
            Console.CancelKeyPress += (_, e) => { try { app.ShutdownAsync(CancellationToken.None).Wait(3000); } catch (Exception) { } };
            _ = Task.Run(() => AcceptLoop(listener, config));
            new Thread(ReadConsoleKeys) { IsBackground = true, Name = "console-keys" }.Start();

            long lastTick = 0;
            while (true)
            {
                loop.Wake.WaitOne(FrameMs);
                lock (loop.Gate)
                {
                    loop.Drain();
                    var now = clock.ElapsedMilliseconds;
                    if (now - lastTick >= ShellTickMs)
                    {
                        lastTick = now;
                        shell.Tick();
                    }
                    SendFrame();
                }
            }
        }

        /// Under the gate: sends the layout's target frame as a diff when it changed (or a key needs its ack).
        private static void SendFrame()
        {
            var ch = client;
            if (ch == null) return;
            var started = Stopwatch.GetTimestamp();
            var changed = shell.Version != builtVersion || sent == null;
            if (!changed && pendingAck == 0) return;
            var message = new JObject { ["t"] = "frame", ["reducedMotion"] = shell.Services.Player?.Settings?.ReducedMotion ?? false };
            if (changed)
            {
                builtVersion = shell.Version;
                var target = UiLayout.Build(shell);
                if (target.ScreenKey != sent?.ScreenKey) Console.WriteLine("screen: " + target.ScreenKey);
                var diff = UiFrameDiff.Make(sent, target);
                sent = target;
                if (UiFrameDiff.IsEmpty(diff) && pendingAck == 0) return;
                message["diff"] = diff;
            }
            else message["diff"] = new JObject();
            var ack = pendingAck;
            if (ack > 0) message["ack"] = ack;
            pendingAck = 0;
            var frameId = ++nextFrameId;
            message["fid"] = frameId;
            frameSentAt[frameId] = clock.ElapsedTicks;
            try
            {
                var before = ch.BytesSent;
                ch.Send(message);
                frameBytes += ch.BytesSent - before;
                frameCount++;
            }
            catch (Exception) { }
            if (ack != 0)
            {
                keyWorkTicks += Stopwatch.GetTimestamp() - started;
                keyWorkCount++;
            }
        }

        private static async Task AcceptLoop(TcpListener listener, RemoteUiConfig config)
        {
            while (true)
            {
                var tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => Serve(tcp, config));
            }
        }

        private static async Task Serve(TcpClient tcp, RemoteUiConfig config)
        {
            var remote = tcp.Client.RemoteEndPoint?.ToString();
            tcp.NoDelay = true;
            var ch = new RemoteUiChannel(tcp.GetStream());
            try
            {
                JObject hello;
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    hello = await ch.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if ((string)hello?["t"] != "hello" || !config.TokenMatches((string)hello["token"]))
                {
                    Console.WriteLine("Rejected " + remote + ": bad token");
                    ch.Send(new JObject { ["t"] = "refused", ["error"] = "bad token (reinstall with scripts/tv-dev.sh --remote-ui)" });
                    return;
                }
                if ((int?)hello["version"] != RemoteUiProtocol.Version)
                {
                    ch.Send(new JObject { ["t"] = "refused", ["error"] = "protocol " + (int?)hello["version"] + ", dev host speaks " + RemoteUiProtocol.Version + " (reinstall)" });
                    return;
                }
                ch.Send(new JObject { ["t"] = "welcome" });
                var previous = client;
                lock (loop.Gate)
                {
                    client = ch;
                    sent = null;   // full frame for the new connection
                    tv.Attach(ch);
                }
                previous?.Dispose();
                loop.Wake.Set();
                Console.WriteLine("TV connected from " + remote);

                while (true)
                {
                    var message = await ch.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                    if (message == null) break;
                    switch ((string)message["t"])
                    {
                        case "key":
                            var name = (string)message["name"];
                            var seq = (int)message["seq"];
                            loop.Post(() => OnKey(name, seq));
                            break;
                        case "stats":
                            PrintStats(message);
                            break;
                        case "painted":
                            if (frameSentAt.TryRemove((long)message["fid"], out var sentAt))
                            {
                                roundTrips.Enqueue((clock.ElapsedTicks - sentAt) * 1000.0 / Stopwatch.Frequency);
                                tvApplyPaint.Enqueue((double)message["applyPaintMs"]);
                            }
                            break;
                        case "lifecycle":
                            if ((string)message["state"] == "pause") _ = app.PauseForBackgroundAsync(CancellationToken.None);
                            else if ((string)message["state"] == "resume") _ = app.ResumeFromBackgroundAsync(CancellationToken.None);
                            break;
                        default:
                            tv.OnMessage(message);
                            break;
                    }
                }
            }
            catch (Exception error)
            {
                Console.WriteLine("TV connection " + remote + ": " + error.GetBaseException().Message);
            }
            finally
            {
                var wasCurrent = false;
                lock (loop.Gate)
                {
                    if (client == ch)
                    {
                        wasCurrent = true;
                        client = null;
                        sent = null;
                    }
                }
                if (wasCurrent)
                {
                    tv.Detach(ch);
                    Console.WriteLine("TV disconnected");
                    // The TV stopped its player and closed its sources; end the session here too.
                    if (app.Session.GetStatus().Active) _ = app.Session.StopAsync(CancellationToken.None);
                }
                ch.Dispose();
            }
        }

        /// Under the gate. Same handling as TvUiHost.OnKey on the TV.
        private static void OnKey(string tizenName, int seq)
        {
            var key = RemoteKeys.FromTizen(tizenName);
            if (key != RemoteKey.None)
            {
                var handled = shell.HandleKey(key);
                if (!handled && key == RemoteKey.Back)
                    tv.Send(new JObject { ["t"] = "exit" });
            }
            pendingAck = seq;
            SendFrame();
        }

        private static string Summary(ConcurrentQueue<double> queue)
        {
            var values = new System.Collections.Generic.List<double>();
            while (queue.TryDequeue(out var v)) values.Add(v);
            if (values.Count == 0) return "-";
            values.Sort();
            return "avg " + Math.Round(values.Average()) + " ms, p50 " + Math.Round(values[values.Count / 2]) +
                ", p95 " + Math.Round(values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))]) + ", max " + Math.Round(values[values.Count - 1]) + " (n=" + values.Count + ")";
        }

        /// Dev convenience (and how latency is measured without a person at the remote): each line on stdin is a
        /// Tizen key name, or one of u d l r ok back play menu info; "text" prints the frame's visible text.
        private static void ReadConsoleKeys()
        {
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                var name = line.Trim();
                if (name.StartsWith("probe-player ", StringComparison.Ordinal))
                {
                    _ = ProbePlayerAsync(name.Substring(13).Trim());
                    continue;
                }
                switch (name)
                {
                    case "": continue;
                    case "text":
                        // The visible text of the current frame (what the TV shows), for checks without looking.
                        loop.Post(() => Console.WriteLine("text: " + string.Join(" | ", UiLayout.Build(shell).Nodes
                            .Where(n => n.Kind == UiNodeKind.Text && !string.IsNullOrWhiteSpace(n.Text) && n.Opacity > 0).Select(n => n.Text))));
                        continue;
                    case "u": name = "Up"; break;
                    case "d": name = "Down"; break;
                    case "l": name = "Left"; break;
                    case "r": name = "Right"; break;
                    case "ok": name = "Return"; break;
                    case "back": name = "XF86Back"; break;
                    case "play": name = "XF86PlayBack"; break;
                    case "menu": name = "XF86SimpleMenu"; break;
                    case "info": name = "XF86Info"; break;
                }
                var key = name;
                loop.Post(() => OnKey(key, -1));
            }
        }

        /// "probe-player <url>": plays a direct HTTP URL on the TV's player through the RPC path for a few seconds,
        /// outside any session (stop the session first), and prints what the TV reports.
        private static async Task ProbePlayerAsync(string url)
        {
            var player = tv.Player;
            try
            {
                var clock = Stopwatch.StartNew();
                await player.OpenAsync(url, 0, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("probe: opened in " + clock.ElapsedMilliseconds + " ms, state " + player.State + ", duration " + player.DurationMs + " ms, audio tracks " + player.AudioTracks.Count);
                player.Play();
                await Task.Delay(6000).ConfigureAwait(false);
                Console.WriteLine("probe: after 6 s state " + player.State + ", position " + player.PositionMs + " ms");
                await player.SeekAsync(20000, CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(2000).ConfigureAwait(false);
                Console.WriteLine("probe: after seek to 20 s state " + player.State + ", position " + player.PositionMs + " ms");
                player.Stop();
            }
            catch (Exception error)
            {
                Console.WriteLine("probe: " + error.Message);
            }
        }

        private static void PrintStats(JObject stats)
        {
            Console.WriteLine("frames sent→painted on TV: " + Summary(roundTrips) + "; TV apply+paint " + Summary(tvApplyPaint));
            if ((int)stats["keys"] == 0 && (int)stats["peakPaintsPerSecond"] == 0) return;
            var latency = stats["keyToPaintMs"] as JObject;
            long work, works, bytes, frames;
            lock (loop.Gate)
            {
                work = keyWorkTicks; works = keyWorkCount; bytes = frameBytes; frames = frameCount;
                keyWorkTicks = keyWorkCount = frameBytes = frameCount = 0;
            }
            Console.WriteLine("stats: " + (int)stats["keys"] + " keys" +
                (latency == null ? "" : ", key→paint avg " + latency["avg"] + " ms, p50 " + latency["p50"] + ", p95 " + latency["p95"] + ", max " + latency["max"]) +
                "; TV peak " + stats["peakPaintsPerSecond"] + " paints/s, avg paint " + stats["avgPaintMs"] + " ms" +
                "; Mac per key " + (works == 0 ? "-" : Math.Round(work * 1000.0 / Stopwatch.Frequency / works, 1) + " ms") +
                ", " + frames + " frames, avg " + (frames == 0 ? 0 : bytes / frames) + " B");
        }

        private static void StartApi(int port, string keyFile)
        {
            if (port == 0 || !File.Exists(keyFile)) return;
            try
            {
                var server = new ApiHttpServer(app.Router, new ApiAuthenticator(ApiKey.ParseKeyFile(File.ReadAllText(keyFile))), new[] { "http://127.0.0.1:" + port + "/" });
                server.Start();
                Console.WriteLine("Agent API on http://127.0.0.1:" + port + "/ (RU7100_API_URL for the MCP and scripts)");
            }
            catch (Exception error)
            {
                Console.WriteLine("Agent API off: " + error.Message);
            }
        }

        private static string FindRepo()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))) dir = dir.Parent;
            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
