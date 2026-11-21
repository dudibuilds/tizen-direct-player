using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Ru7100.Core;
using Ru7100Probe;

/// M3 proof: the TV's TorrentSourceOpener (MonoTorrent + our uTP) against a uTP-only libtorrent seed on 127.0.0.1,
/// found through a one-peer HTTP tracker run here. Downloads the whole file through the loopback player URL and checks
/// its SHA-256, then keeps the source open for holdSeconds so a uTP-only libtorrent leecher can download from us
/// (incoming uTP through the engine's listener). scripts/utp-engine-check.sh drives it.
///
///   --utp-engine-check <infohash> <seedPort> <listenPort> <expectedSha256> [holdSeconds]
internal static class UtpEngineCheck
{
    public static async Task<int> RunAsync(string infoHash, int seedPort, int listenPort, string expectedSha, double holdSeconds)
    {
        using var tracker = new HttpListener();
        var trackerPort = listenPort + 1000;
        tracker.Prefixes.Add($"http://127.0.0.1:{trackerPort}/");
        tracker.Start();
        _ = Task.Run(async () =>
        {
            while (tracker.IsListening)
            {
                HttpListenerContext context;
                try { context = await tracker.GetContextAsync(); } catch { return; }
                // Compact reply with the one seed: d8:intervali60e5:peers6:<ip><port>e
                var body = new List<byte>();
                body.AddRange(System.Text.Encoding.ASCII.GetBytes("d8:intervali60e5:peers6:"));
                body.AddRange(new byte[] { 127, 0, 0, 1, (byte)(seedPort >> 8), (byte)seedPort });
                body.Add((byte)'e');
                context.Response.ContentType = "text/plain";
                await context.Response.OutputStream.WriteAsync(body.ToArray());
                context.Response.Close();
            }
        });

        var root = Directory.CreateTempSubdirectory("ru7100-utp-engine-").FullName;
        var clock = Stopwatch.StartNew();
        var settings = new TorrentSourceSettings
        {
            RootDirectory = root,
            ListenPorts = new[] { listenPort },
            AllowPortForwarding = false,
            DhtRouters = Array.Empty<string>(),
            EnableUtp = Environment.GetEnvironmentVariable("RU7100_UTP") != "0",
            Log = line => { if (Environment.GetEnvironmentVariable("RU7100_VERBOSE") == "1") Console.Error.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0}] {line}"); },
        };
        using var opener = new TorrentSourceOpener(settings);
        var candidate = new StreamCandidate { InfoHash = infoHash, Trackers = new[] { $"http://127.0.0.1:{trackerPort}/announce" } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var source = await opener.OpenAsync(candidate, deadline.Token);
        Console.Error.WriteLine($"engine-check: opened after {clock.Elapsed.TotalSeconds:0.0} s, length {source.LengthBytes}");

        using var http = new HttpClient();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        const int chunk = 4 * 1024 * 1024;
        for (long position = 0; position < source.LengthBytes; position += chunk)
        {
            var count = (int)Math.Min(chunk, source.LengthBytes - position);
            using var request = new HttpRequestMessage(HttpMethod.Get, source.PlayerUrl);
            request.Headers.Range = new RangeHeaderValue(position, position + count - 1);
            using var response = await http.SendAsync(request, deadline.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            if (bytes.Length != count)
                throw new Exception($"short read at {position}: {bytes.Length}");
            sha.AppendData(bytes);
        }
        var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        var seconds = clock.Elapsed.TotalSeconds;
        Console.Error.WriteLine($"engine-check: downloaded {source.LengthBytes / 1e6:0.0} MB in {seconds:0.0} s, sha256 {(actual == expectedSha ? "matches" : "MISMATCH " + actual)}");
        Console.Error.WriteLine("engine-check: conn " + opener.ConnectionSummary);
        Console.Error.WriteLine("engine-check: net " + opener.NetworkSummary);
        Console.WriteLine("ENGINE-READY");   // the script starts the leecher now
        await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
        Console.Error.WriteLine("engine-check: after hold: conn " + opener.ConnectionSummary);
        return actual == expectedSha ? 0 : 1;
    }
}
