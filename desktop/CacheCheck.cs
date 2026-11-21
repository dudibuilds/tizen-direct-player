using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100Probe;

/// Rolling-cache check: tiny cache, stream far past it, verify disk stays bounded and a seek back re-fetches
/// identical bytes. Usage: RU7100_STREAM_ADDON=<add-on base URL> RU7100_CACHE_TITLE=series/<imdb id>:1:1
/// dotnet run --project desktop/TransportCheck.csproj -- --cache-check
internal static class CacheCheck
{
    public static async Task RunAsync()
    {
        using var http = new HttpClient();
        var streams = JObject.Parse(await http.GetStringAsync(StreamAddon.Base + "/stream/" + (Environment.GetEnvironmentVariable("RU7100_CACHE_TITLE") ?? throw new InvalidOperationException("Set RU7100_CACHE_TITLE, e.g. series/<imdb id>:1:1")) + ".json"))["streams"]!
            .Where(s => s["infoHash"] != null).ToList();
        var pick = streams.OrderByDescending(s => SeedersOf((string)s["title"] ?? "")).First();
        Console.WriteLine("source: " + ((string)pick["title"])?.Split('\n')[0] + " seeders=" + SeedersOf((string)pick["title"] ?? ""));
        var root = Directory.CreateTempSubdirectory("ru7100-cache-check-").FullName;
        var sw = Stopwatch.StartNew();
        using var opener = new TorrentSourceOpener(new TorrentSourceSettings
        {
            RootDirectory = root, StreamCacheBytes = 96L * 1024 * 1024, StreamCacheInMemory = Environment.GetEnvironmentVariable("CACHE_DISK") != "1", RewindBytes = 16L * 1024 * 1024,
            FreeSpaceReserveBytes = 0, MinimumCacheBytes = 32L * 1024 * 1024,
            Log = line => { if (line.Contains("cache") || line.Contains("buffer")) Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:0.0}] {line}"); },
        });
        var candidate = new StreamCandidate { InfoHash = (string)pick["infoHash"], FileIndex = (int?)pick["fileIdx"] };
        using var source = await opener.OpenAsync(candidate, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
        Console.WriteLine($"opened in {sw.Elapsed.TotalSeconds:0.0}s length={source.LengthBytes / (1024 * 1024)} MB");

        async Task<byte[]> Range(long start, int count)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, source.PlayerUrl);
            req.Headers.Range = new RangeHeaderValue(start, start + count - 1);
            using var res = await http.SendAsync(req);
            return await res.Content.ReadAsByteArrayAsync();
        }
        var head = await Range(0, 2 * 1024 * 1024);
        var headHash = Convert.ToHexString(SHA1.HashData(head));
        Console.WriteLine($"head sha1 {headHash[..12]} ({head.Length} bytes)");
        long maxDisk = 0;
        const int chunk = 4 * 1024 * 1024;
        for (long pos = 8L * 1024 * 1024; pos < 250L * 1024 * 1024 && pos < source.LengthBytes; pos += chunk)
        {
            var data = await Range(pos, chunk);
            if (data.Length != chunk) throw new Exception($"short read at {pos}: {data.Length}");
            maxDisk = Math.Max(maxDisk, DiskUse(root));
            if ((pos / chunk) % 8 == 0)
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:0.0}] read {pos / (1024 * 1024)} MB, disk {DiskUse(root) / (1024 * 1024)} MB");
        }
        Console.WriteLine($"max disk during stream: {maxDisk / (1024 * 1024)} MB (cache 96 MB)");
        var again = await Range(0, 2 * 1024 * 1024);
        var againHash = Convert.ToHexString(SHA1.HashData(again));
        Console.WriteLine($"seek back to start: sha1 {againHash[..12]} {(againHash == headHash ? "MATCH" : "MISMATCH")} after {sw.Elapsed.TotalSeconds:0.0}s");
        if (againHash != headHash) throw new Exception("re-fetched bytes differ");
        if (maxDisk > 110L * 1024 * 1024) throw new Exception("cache exceeded its bound on disk");
        Console.WriteLine("ROLLING CACHE OK");
    }

    private static int SeedersOf(string title)
    {
        var i = title.IndexOf("👤", StringComparison.Ordinal);
        if (i < 0) return 0;
        var digits = new string(title.Skip(i + 2).SkipWhile(c => c == ' ').TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    /// Actual blocks used (sparse files count only written data).
    private static long DiskUse(string root)
    {
        var psi = new ProcessStartInfo("du", $"-sk \"{root}\"") { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return long.Parse(output.Split('\t')[0]) * 1024;
    }
}
