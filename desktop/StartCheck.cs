using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Ru7100Probe;

/// Start-time benchmark: the TV's TorrentSourceOpener against a real swarm, timed like a TV start.
/// Picks the candidate the app would rank first (or rank N), opens it, then reads the file's tail and head over the
/// loopback server the way AVPlay's prepare does. Prints one summary line per run.
///
/// Usage: dotnet run --project desktop/TransportCheck.csproj -- --start-check series <imdb id>:<season>:<episode> [rank] [root]
///   root: reuse a data directory between runs (warm start); default is a fresh temp directory (cold start).
/// Env (A/B without rebuilding): RU7100_CONNECT_TIMEOUT_S, RU7100_HALF_OPEN, RU7100_PIPELINE=0, RU7100_TAIL_WAIT_S, RU7100_PORT,
/// RU7100_UPLOAD_KBS, RU7100_RACE=N, RU7100_WARM_WAIT_S, RU7100_SUSTAIN_S, RU7100_LINGER_S, RU7100_REOPEN=1|linger, RU7100_REOPEN_AFTER_S, RU7100_KEEP=0,
/// RU7100_ENCRYPTION=rc4first, RU7100_UTP=0 (TCP only), RU7100_UTP_GRACE_MS (TCP grace in the TCP/uTP race), RU7100_LIST=1 (print
/// the top ranked candidates and exit), RU7100_MEDIA_PORT, RU7100_HASH=<hash>[:file], RU7100_NEXT=<id> RU7100_PREFETCH=1 RU7100_PLAY_S
/// RU7100_NEXT_HASH, RU7100_STALE_S, RU7100_DISK_CACHE_KB, RU7100_LOG=<file>, RU7100_VERBOSE=1.
internal static class StartCheck
{
    public static async Task RunAsync(string type, string id, int rank, string root)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("RU7100_MEDIA_PORT"), out var mediaPort))
            TorrentMediaServer.Port = mediaPort;   // 45550 may be held by the TV app or another harness
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (ru7100-start-check)");
        IReadOnlyList<AddonStreamItem> streams = Array.Empty<AddonStreamItem>();
        for (var attempt = 0; attempt < 4 && streams.Count == 0; attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(5 * attempt));   // Torrentio sometimes answers with no streams
            var payload = JObject.Parse(await http.GetStringAsync($"{StreamAddon.Base}/stream/{type}/{id}.json"));
            streams = StreamRanking.ParseStreams(payload, new AddonDescriptor { Id = "torrentio", Name = "Torrentio" });
        }
        var ranked = StreamRanking.Rank(streams, new RankOptions { Episodic = type == "series" }).Where(c => c.InfoHash != null).ToList();
        Console.WriteLine($"{streams.Count} streams, {ranked.Count} ranked torrents");
        if (Environment.GetEnvironmentVariable("RU7100_LIST") == "1")
        {
            foreach (var (c, i) in ranked.Take(10).Select((c, i) => (c, i)))
                Console.WriteLine($"  #{i} {c.InfoHash} seeders={c.Seeders} size={c.SizeBytes / 1_000_000} MB {c.Title?.Split('\n')[0]}");
            return;
        }
        // RU7100_HASH=<info hash>[:file index] pins the source across runs (Torrentio's order changes between calls).
        var pin = Environment.GetEnvironmentVariable("RU7100_HASH");
        if (!string.IsNullOrEmpty(pin))
        {
            var parts = pin.Split(':');
            var pinHash = parts[0].ToLowerInvariant();
            int? pinFile = parts.Length > 1 && int.TryParse(parts[1], out var f) ? f : null;
            var at = ranked.FindIndex(c => c.InfoHash == pinHash && (pinFile == null || c.FileIndex == pinFile));
            if (at < 0)
            {
                ranked.Insert(0, new StreamCandidate { InfoHash = pinHash, FileIndex = pinFile, Title = "pinned " + pinHash, Trackers = StreamRanking.DefaultTrackers });
                at = 0;
            }
            rank = at;
        }
        var pick = ranked[rank];
        Console.WriteLine($"source #{rank}/{ranked.Count}: {pick.Title?.Split('\n')[0]} seeders={pick.Seeders} size={pick.SizeBytes / 1_000_000} MB hash={pick.InfoHash}");

        var cold = string.IsNullOrEmpty(root);
        if (cold) root = Directory.CreateTempSubdirectory("ru7100-start-check-").FullName;
        var sw = Stopwatch.StartNew();
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var log = new List<string>();
        var marks = new Dictionary<string, double>();
        var verbose = Environment.GetEnvironmentVariable("RU7100_VERBOSE") == "1";
        var settings = new TorrentSourceSettings
        {
            RootDirectory = root,
            Log = line =>
            {
                var t = sw.Elapsed.TotalSeconds;
                lock (log) log.Add($"[{t,6:0.0}] {line}");
                if (verbose) Console.WriteLine($"[{t,6:0.0}] {line}");
                foreach (var key in new[] { "acquired", "metadata", "prebuffered", "opened", "port", "warm" })
                    if (Regex.IsMatch(line, @"torrent: " + key + @"\b") && !marks.ContainsKey(key))
                        lock (marks) marks[key] = t;
            },
        };
        if (double.TryParse(Environment.GetEnvironmentVariable("RU7100_CONNECT_TIMEOUT_S"), out var timeout))
            settings.ConnectionTimeout = TimeSpan.FromSeconds(timeout);
        if (int.TryParse(Environment.GetEnvironmentVariable("RU7100_HALF_OPEN"), out var halfOpen))
            settings.MaximumHalfOpenConnections = halfOpen;
        if (Environment.GetEnvironmentVariable("RU7100_KEEP") == "0")
            settings.KeepPlayedTorrent = false;
        if (double.TryParse(Environment.GetEnvironmentVariable("RU7100_LINGER_S"), out var linger))
            settings.IdleLinger = TimeSpan.FromSeconds(linger);
        if (int.TryParse(Environment.GetEnvironmentVariable("RU7100_PORT"), out var port))
            settings.ListenPorts = new[] { port };   // the TV holds 27492 on this LAN's router
        if (Environment.GetEnvironmentVariable("RU7100_PIPELINE") == "0")
            settings.StreamFullPipeline = false;
        if (double.TryParse(Environment.GetEnvironmentVariable("RU7100_TAIL_WAIT_S"), out var tailWait))
            settings.TailWaitAfterHead = TimeSpan.FromSeconds(tailWait);
        if (Environment.GetEnvironmentVariable("RU7100_ENCRYPTION") == "rc4first")   // MonoTorrent's default order (before c370bcb)
            settings.AllowedEncryption = new[] { MonoTorrent.Connections.EncryptionType.RC4Full, MonoTorrent.Connections.EncryptionType.RC4Header, MonoTorrent.Connections.EncryptionType.PlainText };
        if (int.TryParse(Environment.GetEnvironmentVariable("RU7100_UTP_GRACE_MS"), out var graceMs))
            settings.UtpTcpGrace = TimeSpan.FromMilliseconds(graceMs);
        if (Environment.GetEnvironmentVariable("RU7100_UTP") == "0")
            settings.EnableUtp = false;
        if (double.TryParse(Environment.GetEnvironmentVariable("RU7100_STALE_S"), out var stale))
            settings.StaleRequestTimeout = TimeSpan.FromSeconds(stale);
        if (int.TryParse(Environment.GetEnvironmentVariable("RU7100_DISK_CACHE_KB"), out var diskCache))
            settings.DiskCacheBytes = diskCache * 1024;
        if (int.TryParse(Environment.GetEnvironmentVariable("RU7100_UPLOAD_KBS"), out var upload))
            settings.MaximumUploadRate = upload * 1024;

        using var opener = new TorrentSourceOpener(settings);
        // Like the TV app at launch; RU7100_WARM_WAIT_S simulates the time until the user presses Play (0: no warm-up).
        var warmWait = double.TryParse(Environment.GetEnvironmentVariable("RU7100_WARM_WAIT_S"), out var w) ? w : 0;
        if (warmWait > 0)
        {
            opener.Warm();
            await Task.Delay(TimeSpan.FromSeconds(warmWait));
        }
        var openAt = sw.Elapsed.TotalSeconds;
        // Per-run timeline of the picked torrent: first connection, first payload byte, piece length, bytes received.
        double firstPeer = -1, firstData = -1, firstUnchoke = -1;
        int maxUnchoked = 0, maxInterested = 0;
        TorrentStats lastStats = null;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var stats = await opener.StatsOfAsync(pick.InfoHash);
                if (stats != null)
                {
                    var t = sw.Elapsed.TotalSeconds - openAt;
                    if (firstPeer < 0 && stats.OpenConnections > 0) firstPeer = t;
                    if (firstUnchoke < 0 && stats.UnchokedBy > 0) firstUnchoke = t;
                    maxUnchoked = Math.Max(maxUnchoked, stats.UnchokedBy);
                    maxInterested = Math.Max(maxInterested, stats.Interested);
                    if (firstData < 0 && stats.DataBytesReceived > 0) firstData = t;
                    lastStats = stats;
                }
                try { await Task.Delay(100, sampling.Token); } catch (OperationCanceledException) { }
            }
        });
        using var source = await OpenAsync(opener, ranked, rank, Environment.GetEnvironmentVariable("RU7100_RACE"));
        var openedAt = sw.Elapsed.TotalSeconds;
        // RU7100_HOLD_S: keep the opened source serving for N seconds and print its URL (diagnosis with ffprobe).
        if (double.TryParse(Environment.GetEnvironmentVariable("RU7100_HOLD_S"), out var holdS) && holdS > 0)
        {
            Console.WriteLine($"HOLD url={source.PlayerUrl} length={source.LengthBytes}");
            await Task.Delay(TimeSpan.FromSeconds(holdS));
            return;
        }
        var atOpen = opener.ConnectionSummary;
        var peersAtOpen = source.Peers;
        var statsAtOpen = opener.StatsOf(pick.InfoHash) ?? lastStats;

        Task<double> Range(long start, int count, CancellationToken token = default) => RangeOf(source, start, count, token);
        async Task<double> RangeOf(IOpenedSource from, long start, int count, CancellationToken token = default)
        {
            var t0 = sw.Elapsed.TotalSeconds;
            using var req = new HttpRequestMessage(HttpMethod.Get, from.PlayerUrl);
            req.Headers.Range = new RangeHeaderValue(start, start + count - 1);
            using var res = await http.SendAsync(req, token);
            var bytes = await res.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length != count) throw new Exception($"short read at {start}: {bytes.Length}");
            return sw.Elapsed.TotalSeconds - t0;
        }
        // AVPlay's prepare on MKV: header, then a jump to the cues near the end, then back to the start.
        var head = await Range(0, 64 * 1024);
        var tail = await Range(source.LengthBytes - 64 * 1024, 64 * 1024);
        var second = await Range(4L * 1024 * 1024, 1024 * 1024);
        var total = sw.Elapsed.TotalSeconds - openAt;
        sampling.Cancel();
        await sampler;
        // What a start must fetch at least: the head piece and the tail prefetch (whole pieces), against what arrived.
        var pieceBytes = (long)(statsAtOpen?.PieceLength ?? 0);
        var needBytes = pieceBytes == 0 ? 0 : pieceBytes * (1 + Math.Max(1, (long)Math.Ceiling(settings.TailPrefetchBytes / (double)pieceBytes)));
        var startStats = $"piece={pieceBytes / 1024}KiB firstPeer={firstPeer:0.0} firstUnchoke={firstUnchoke:0.0} firstData={firstData:0.0} unchokedMax={maxUnchoked}/{maxInterested} rxAtOpen={(statsAtOpen?.DataBytesReceived ?? 0) / 1e6:0.0}MB need={needBytes / 1e6:0.0}MB pieces={statsAtOpen?.VerifiedPieces ?? 0}";
        // RU7100_SUSTAIN_S: keep reading sequentially (like playback without a player) and report the throughput.
        var sustain = double.TryParse(Environment.GetEnvironmentVariable("RU7100_SUSTAIN_S"), out var ss) ? ss : 0;
        if (sustain > 0)
        {
            var t0 = sw.Elapsed.TotalSeconds;
            var lastOk = t0;
            long position = 8L * 1024 * 1024, read = 0;
            // 1 MB ranges, so a slow swarm ends the window on time instead of failing a big request.
            while (sw.Elapsed.TotalSeconds - t0 < sustain && position + 1024 * 1024 < source.LengthBytes)
            {
                // A 1 MB range that takes 30 s is an engine stall (seen with uTP on and off); report it and stop
                // instead of waiting out the HTTP timeout (it used to cost ~3 minutes per stalled run).
                using var stall = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await Range(position, 1024 * 1024, stall.Token); }
                catch (Exception error)
                {
                    Console.WriteLine($"SUSTAIN stalled: {error.GetType().Name} at {position / 1e6:0} of {source.LengthBytes / 1e6:0} MB; conn {opener.ConnectionSummary}");
                    break;
                }
                position += 1024 * 1024;
                read += 1024 * 1024;
                lastOk = sw.Elapsed.TotalSeconds;
            }
            var seconds = Math.Max(0.001, lastOk - t0);   // up to the last completed read (a stall is reported above)
            Console.WriteLine($"SUSTAIN {read / 1e6:0} MB in {seconds:0.0} s = {read * 8 / 1e6 / seconds:0.0} Mbit/s upload cap {settings.MaximumUploadRate / 1024} KiB/s");
        }
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds;
        var megabytes = (opener.Connections.TcpBytes + opener.Connections.UtpBytes) / 1e6;
        Console.WriteLine($"TRANSPORT utp={(settings.EnableUtp ? "on" : "off")} hs-tcp={opener.Connections.TcpPeers} hs-utp={opener.Connections.UtpPeers} " +
            $"MB-tcp={opener.Connections.TcpBytes / 1e6:0.0} MB-utp={opener.Connections.UtpBytes / 1e6:0.0} cpu={cpu:0.0}s " +
            $"cpu-per-MB={cpu * 1000 / Math.Max(1, megabytes):0.0}ms");
        Console.WriteLine($"RESULT {(cold ? "cold" : "warm")} {type} {id} rank={rank} " +
            $"metadata={Rel(marks, "metadata", openAt)} prebuffered={Rel(marks, "prebuffered", openAt)} opened={openedAt - openAt:0.0} " +
            $"head={head:0.00} tail={tail:0.00} next1MB={second:0.00} total={total:0.0} peers={peersAtOpen} hash={pick.InfoHash} {startStats}");
        Console.WriteLine("conn at open: " + atOpen);
        Console.WriteLine("conn at end:  " + opener.ConnectionSummary);
        Console.WriteLine("net: " + opener.NetworkHealth);
        Console.WriteLine("network: " + opener.NetworkSummary + " " + await CompareWithPublicIp(http, opener));
        // RU7100_REOPEN: close the source, then open the same one again, the TV's "play it again" path.
        //   1 (or "kept"): after the idle linger, when the torrent was kept stopped (KeepPlayedTorrent) or removed
        //   (RU7100_KEEP=0); RU7100_LINGER_S shortens the linger.  "linger": RU7100_REOPEN_AFTER_S (default 5) later,
        //   while the torrent is still paused in its linger.
        var reopenMode = Environment.GetEnvironmentVariable("RU7100_REOPEN");
        if (!string.IsNullOrEmpty(reopenMode) && reopenMode != "0")
        {
            source.Dispose();
            if (reopenMode == "linger")
                await Task.Delay(TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("RU7100_REOPEN_AFTER_S"), out var after) ? after : 5));
            else
            {
                var idleDeadline = sw.Elapsed + settings.IdleLinger + TimeSpan.FromSeconds(30);
                bool Idle() { lock (log) return log.Any(l => l.Contains("torrent: removed ") || l.Contains("stopped for a quick replay")); }
                while (!Idle() && sw.Elapsed < idleDeadline)
                    await Task.Delay(250);
            }
            Console.WriteLine($"REOPEN mode={reopenMode} keep={settings.KeepPlayedTorrent} torrents={opener.ActiveTorrents} kept={opener.ParkedTorrents} engineRunning={opener.EngineRunning}");
            int from;
            lock (log) from = log.Count;
            var again = sw.Elapsed.TotalSeconds;
            using var reopened = await opener.OpenAsync(pick, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            var reopenHead = await RangeOf(reopened, 0, 64 * 1024);
            var reopenTail = await RangeOf(reopened, reopened.LengthBytes - 64 * 1024, 64 * 1024);
            Console.WriteLine($"REOPEN result: opened={sw.Elapsed.TotalSeconds - again - reopenHead - reopenTail:0.0} " +
                $"head={reopenHead:0.00} tail={reopenTail:0.00} total={sw.Elapsed.TotalSeconds - again:0.0} peers={reopened.Peers}");
            lock (log)
                foreach (var line in log.Skip(from).Where(l => Regex.IsMatch(l, @"torrent: (metadata|prebuffered|opened) ")))
                    Console.WriteLine("REOPEN " + line);
        }
        // RU7100_NEXT=<id>: the binge path. Resolve the next episode's sources, keep "watching" the current one for
        // RU7100_PLAY_S seconds (sequential reads), then close it and open the next one, timed like the first start.
        // RU7100_PREFETCH=1 prefetches the next source (metadata, head and tail) while the current one plays.
        // The next source is the current torrent when it also holds the next episode (season pack), else the top
        // ranked one; RU7100_NEXT_HASH pins it.
        var nextId = Environment.GetEnvironmentVariable("RU7100_NEXT");
        if (!string.IsNullOrEmpty(nextId))
        {
            var resolveStart = sw.Elapsed.TotalSeconds;
            var nextPayload = JObject.Parse(await http.GetStringAsync($"{StreamAddon.Base}/stream/{type}/{nextId}.json"));
            var nextRanked = StreamRanking.Rank(StreamRanking.ParseStreams(nextPayload, new AddonDescriptor { Id = "torrentio", Name = "Torrentio" }),
                new RankOptions { Episodic = type == "series" }).Where(c => c.InfoHash != null).ToList();
            var resolveSec = sw.Elapsed.TotalSeconds - resolveStart;
            var nextPin = Environment.GetEnvironmentVariable("RU7100_NEXT_HASH")?.ToLowerInvariant();
            var next = (nextPin != null ? nextRanked.FirstOrDefault(c => c.InfoHash == nextPin) : null)
                ?? nextRanked.FirstOrDefault(c => c.InfoHash == pick.InfoHash)
                ?? nextRanked[0];
            var prefetch = Environment.GetEnvironmentVariable("RU7100_PREFETCH") == "1";
            var playFor = double.TryParse(Environment.GetEnvironmentVariable("RU7100_PLAY_S"), out var pf) ? pf : 20;
            var playStart = sw.Elapsed.TotalSeconds;
            var prefetchSummary = "off";
            double prefetchSec = 0;
            var prefetchTask = prefetch ? opener.PrefetchAsync(next, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token) : Task.FromResult("off");
            long position = 16L * 1024 * 1024;
            while ((sw.Elapsed.TotalSeconds - playStart < playFor || !prefetchTask.IsCompleted) && position + 2 * 1024 * 1024 < source.LengthBytes
                && sw.Elapsed.TotalSeconds - playStart < 180)
            {
                await Range(position, 2 * 1024 * 1024);
                position += 2 * 1024 * 1024;
                await Task.Delay(250);   // about playback pace, not a download race
            }
            try { prefetchSummary = await prefetchTask; prefetchSec = sw.Elapsed.TotalSeconds - playStart; }
            catch (Exception error) { prefetchSummary = "failed: " + error.Message; }
            var playedSec = sw.Elapsed.TotalSeconds - playStart;
            var nextStats = opener.StatsOf(next.InfoHash);
            source.Dispose();
            int nextFrom;
            lock (log) nextFrom = log.Count;
            var nextAt = sw.Elapsed.TotalSeconds;
            using var nextSource = await opener.OpenAsync(next, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            var nextOpened = sw.Elapsed.TotalSeconds - nextAt;
            var nextHead = await RangeOf(nextSource, 0, 64 * 1024);
            var nextTail = await RangeOf(nextSource, nextSource.LengthBytes - 64 * 1024, 64 * 1024);
            var nextSecond = await RangeOf(nextSource, 4L * 1024 * 1024, 1024 * 1024);
            // Past what a prefetch holds: 8 MB from 24 MB on, so a prefetched start that then stalls shows up here.
            double beyond = 0;
            for (long at = 24L * 1024 * 1024; at < 32L * 1024 * 1024 && at + 1024 * 1024 < nextSource.LengthBytes; at += 1024 * 1024)
                beyond += await RangeOf(nextSource, at, 1024 * 1024);
            Console.WriteLine($"NEXT prefetch={(prefetch ? 1 : 0)} sameTorrent={(next.InfoHash == pick.InfoHash ? 1 : 0)} hash={next.InfoHash}:{next.FileIndex} " +
                $"resolve={resolveSec:0.0} played={playedSec:0.0} opened={nextOpened:0.0} head={nextHead:0.00} tail={nextTail:0.00} next1MB={nextSecond:0.00} beyond8MB={beyond:0.00} " +
                $"total={sw.Elapsed.TotalSeconds - nextAt:0.0} peers={nextSource.Peers} prefetchDoneAfter={prefetchSec:0.0} rxBeforeOpen={(nextStats?.DataBytesReceived ?? 0) / 1e6:0.0}MB");
            Console.WriteLine("NEXT prefetch summary: " + prefetchSummary);
            lock (log)
                foreach (var line in log.Skip(nextFrom).Where(l => Regex.IsMatch(l, @"torrent: (metadata|prebuffered|opened|restarted) ")))
                    Console.WriteLine("NEXT " + line);
        }
        if (Environment.GetEnvironmentVariable("RU7100_LOG") is string path)
            File.WriteAllLines(path, log);
        foreach (var line in log.Where(l => l.Contains("port") || l.Contains("warm") || l.Contains("gate") || l.Contains("nat") || l.Contains("removed") || l.Contains("anchor") || l.Contains("kept")))
            Console.WriteLine(line);
    }

    /// RU7100_RACE=N opens ranks rank..rank+N-1 at once on the shared engine (like the playback race without its
    /// cut), keeps the first that prebuffers and closes the rest. Default: the one candidate.
    private static async Task<IOpenedSource> OpenAsync(TorrentSourceOpener opener, List<StreamCandidate> ranked, int rank, string race)
    {
        var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var count = int.TryParse(race, out var n) && n > 1 ? n : 1;
        if (count == 1)
            return await opener.OpenAsync(ranked[rank], deadline.Token);
        var hashes = new HashSet<string>();
        var picks = ranked.Skip(rank).Where(c => hashes.Add(c.InfoHash)).Take(count).ToList();
        var tasks = picks.Select(c => opener.OpenAsync(c, deadline.Token)).ToList();
        var pending = tasks.ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (done.Status != TaskStatus.RanToCompletion)
                continue;
            Console.WriteLine($"race winner #{tasks.IndexOf(done)} of {count}");
            deadline.Cancel();
            foreach (var other in pending)
                _ = other.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); });
            return done.Result;
        }
        throw new Exception("every race candidate failed");
    }

    /// Carrier-grade NAT check: a UPnP mapping only helps when the router's external address is the public one.
    private static async Task<string> CompareWithPublicIp(HttpClient http, TorrentSourceOpener opener)
    {
        var router = opener.RouterExternalAddress;
        if (router == null)
            return "router-ext=unknown";
        try
        {
            var publicIp = (await http.GetStringAsync("https://ifconfig.me/ip")).Trim();
            return publicIp == router.ToString() ? "router-ext=matches public IP" : "router-ext=DIFFERS from public IP (CGNAT or double NAT)";
        }
        catch (Exception error) { return "public IP lookup failed: " + error.Message; }
    }

    private static string Rel(Dictionary<string, double> marks, string key, double origin) =>
        marks.TryGetValue(key, out var t) ? (t - origin).ToString("0.0") : "-";
}

/// The stream add-on the Mac checks query (no add-on is built in): RU7100_STREAM_ADDON, e.g. https://example-addon/config.
internal static class StreamAddon
{
    public static string Base => (Environment.GetEnvironmentVariable("RU7100_STREAM_ADDON") ?? throw new InvalidOperationException("Set RU7100_STREAM_ADDON to a Stremio stream add-on base URL (the manifest URL without /manifest.json).")).TrimEnd('/');
}
