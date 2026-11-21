using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100Probe;
using Xunit;
using Xunit.Abstractions;

namespace Ru7100.Tests.Playback
{
    /// Runs only with RU7100_NET_TESTS=1 (real trackers and peers).
    public sealed class NetworkFactAttribute : FactAttribute
    {
        public NetworkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RU7100_NET_TESTS") != "1")
                Skip = "Set RU7100_NET_TESTS=1 to run network tests.";
        }
    }

    public class TorrentSourceOpenerTests
    {
        private readonly ITestOutputHelper output;
        public TorrentSourceOpenerTests(ITestOutputHelper output) { this.output = output; }

        private static string TempRoot() => Path.Combine(Path.GetTempPath(), "ru7100-opener-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task DirectUrlsPassThroughAndRootIsSwept()
        {
            var root = TempRoot();
            Directory.CreateDirectory(Path.Combine(root, "torrents", "stale"));
            File.WriteAllText(Path.Combine(root, "torrents", "stale", "x.mkv"), "old");
            using (var opener = new TorrentSourceOpener(new TorrentSourceSettings { RootDirectory = root }))
            {
                Assert.False(Directory.Exists(Path.Combine(root, "torrents", "stale")));
                using (var source = await opener.OpenAsync(new StreamCandidate { Url = "https://cdn.example/v.mp4", SizeBytes = 42 }, CancellationToken.None))
                {
                    Assert.Equal("https://cdn.example/v.mp4", source.PlayerUrl);
                    Assert.Equal(42, source.LengthBytes);
                }
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    opener.OpenAsync(new StreamCandidate { InfoHash = "nothex" }, CancellationToken.None));
            }
            Directory.Delete(root, true);
        }


        [Fact]
        public async Task WarmEngineIsAnchoredSoItKeepsRunningWithoutTorrents()
        {
            var root = TempRoot();
            var log = new List<string>();
            var port = 40000 + new Random().Next(5000);
            var settings = new TorrentSourceSettings
            {
                RootDirectory = root, AllowPortForwarding = false, DhtRouters = Array.Empty<string>(), ListenPorts = new[] { port },
                Log = line => { lock (log) log.Add(line); },
            };
            using (var opener = new TorrentSourceOpener(settings))
            {
                opener.Warm();
                for (var i = 0; i < 100 && !Logged("anchor:"); i++)
                    await Task.Delay(100);
                Assert.True(Logged("anchor: engine held running"), string.Join("\n", log));
                Assert.True(opener.EngineRunning);
                Assert.Equal(0, opener.ActiveTorrents);
            }
            Directory.Delete(root, true);

            bool Logged(string text) { lock (log) return log.Any(l => l.Contains(text)); }
        }

        [Fact]
        public void ConnectionDiagnosticsPeerSetsStayBounded()
        {
            var set = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
            for (var i = 0; i < ConnectionDiagnostics.MaximumTrackedPeers * 5; i++)
                ConnectionDiagnostics.Remember(set, "10.0." + (i / 256) + "." + (i % 256), (byte)0);
            Assert.InRange(set.Count, 1, ConnectionDiagnostics.MaximumTrackedPeers);
            ConnectionDiagnostics.Remember(set, "10.0.0.0", (byte)1);   // an existing key never resets the set
            Assert.InRange(set.Count, 1, ConnectionDiagnostics.MaximumTrackedPeers);
        }

        [Fact]
        public void TorrentMetadataAndDhtNodesSurviveTheSweepWithinTheirLimits()
        {
            var root = TempRoot();
            var metadata = Path.Combine(root, "cache", "metadata");
            Directory.CreateDirectory(metadata);
            Directory.CreateDirectory(Path.Combine(root, "cache", "fastresume"));
            File.WriteAllText(Path.Combine(root, "cache", "dht_nodes.cache"), "nodes");
            for (var i = 0; i < 5; i++)
            {
                var file = Path.Combine(metadata, i + ".torrent");
                File.WriteAllBytes(file, new byte[100]);
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(i));
            }
            using (new TorrentSourceOpener(new TorrentSourceSettings { RootDirectory = root, MetadataCacheFiles = 3 }))
            {
                Assert.True(File.Exists(Path.Combine(root, "cache", "dht_nodes.cache")));
                Assert.False(Directory.Exists(Path.Combine(root, "cache", "fastresume")));
                Assert.Equal(new[] { "2.torrent", "3.torrent", "4.torrent" },
                    Directory.GetFiles(metadata).Select(Path.GetFileName).OrderBy(n => n));
            }
            Directory.Delete(root, true);
        }

        [Fact]
        public void TrackersMergeCandidateAndDefaultsAndMagnetHasNone()
        {
            var hash = new string('a', 40);
            var own = TorrentSourceOpener.TrackersFor(new[] { "tracker:udp://one.example:80/announce", "dht:" + hash, "ftp://bad" });
            Assert.Equal(new[] { "udp://one.example:80/announce" }.Concat(AddonStream.DefaultTrackers), own);
            Assert.Equal(AddonStream.DefaultTrackers, TorrentSourceOpener.TrackersFor(null));
            // Trackers are added one per tier after the magnet is added, so the magnet itself carries none.
            Assert.Empty(TorrentSourceOpener.Magnet(hash).AnnounceUrls);
            Assert.True(TorrentSourceOpener.IsVideo("Show.S01E01.mkv", 10));
            Assert.False(TorrentSourceOpener.IsVideo("sample.nfo", 10));
        }

        private static readonly Regex Seeders = new Regex(@"👤\s*(\d+)");

        /// The best-seeded torrents for one episode from the stream add-on in RU7100_STREAM_ADDON, title in
        /// RU7100_LIVE_TITLE (e.g. series/<imdb id>:1:1). Live network test, skipped by default.
        private static async Task<StreamCandidate[]> TopCandidatesAsync(int count)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                var json = JObject.Parse(await http.GetStringAsync(Environment.GetEnvironmentVariable("RU7100_STREAM_ADDON")?.TrimEnd('/') + "/stream/" + Environment.GetEnvironmentVariable("RU7100_LIVE_TITLE") + ".json"));
                return json["streams"].Select(s => new
                {
                    hash = (string)s["infoHash"], index = (int?)s["fileIdx"], title = (string)s["title"] ?? "",
                    sources = s["sources"]?.Select(x => (string)x).ToList() ?? new List<string>(),
                })
                .Where(s => s.hash != null)
                .Select(s => new { s, seeders = Seeders.Match(s.title) is var m && m.Success ? int.Parse(m.Groups[1].Value) : 0 })
                .OrderByDescending(x => x.seeders).Take(count)
                .Select(x => new StreamCandidate
                {
                    InfoHash = x.s.hash, FileIndex = x.s.index, Name = x.s.title.Split('\n')[0] + " seeders=" + x.seeders, Trackers = x.s.sources,
                }).ToArray();
            }
        }

        [NetworkFact]
        public async Task ParallelCandidatesGetMetadataQuicklyAndServeRanges()
        {
            var candidates = await TopCandidatesAsync(5);
            var root = TempRoot();
            using (var opener = new TorrentSourceOpener(new TorrentSourceSettings { RootDirectory = root, Log = output.WriteLine }))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                // Like the playback race: every candidate on the shared engine at once, first ready wins.
                var started = DateTime.UtcNow;
                var opens = candidates.Select(async candidate =>
                {
                    var source = await opener.OpenAsync(candidate, timeout.Token);
                    output.WriteLine($"{(DateTime.UtcNow - started).TotalSeconds:0.0} s ready: {candidate.Name} peers={source.Peers}");
                    return source;
                }).ToList();
                var first = await Task.WhenAny(opens);
                var winnerSeconds = (DateTime.UtcNow - started).TotalSeconds;
                Assert.True(first.Status == TaskStatus.RanToCompletion, first.Exception?.ToString());
                Assert.InRange(winnerSeconds, 0, 30);
                using (var source = first.Result)
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    Assert.StartsWith("http://localhost:", source.PlayerUrl);
                    foreach (var offset in new[] { 0L, source.LengthBytes / 2 })
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, source.PlayerUrl);
                        request.Headers.Range = new RangeHeaderValue(offset, offset + 65535);
                        using (var response = await http.SendAsync(request, timeout.Token))
                        {
                            Assert.Equal(206, (int)response.StatusCode);
                            Assert.Equal(65536, (await response.Content.ReadAsByteArrayAsync()).Length);
                            output.WriteLine($"range {offset} ok, rate={source.DownloadBytesPerSecond:0} B/s");
                        }
                    }
                }
                // Give the others a moment, then release them all (losers are cleaned up like in the race).
                await Task.WhenAny(Task.WhenAll(opens), Task.Delay(TimeSpan.FromSeconds(15)));
                var ready = opens.Count(t => t.Status == TaskStatus.RanToCompletion);
                output.WriteLine($"winner after {winnerSeconds:0.0} s; {ready}/{opens.Count} candidates ready within ~15 s more");
                timeout.Cancel();
                foreach (var open in opens)
                {
                    try { (await open).Dispose(); } catch (Exception) { }
                }

                // Re-open the winner's hash: the paused torrent resumes and serves again.
                var winner = candidates[opens.IndexOf(first)];
                using (var again = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                using (var source = await opener.OpenAsync(winner, again.Token))
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, source.PlayerUrl);
                    request.Headers.Range = new RangeHeaderValue(0, 1023);
                    using (var response = await http.SendAsync(request, again.Token))
                        Assert.Equal(1024, (await response.Content.ReadAsByteArrayAsync()).Length);
                    output.WriteLine("re-open ok");
                }
            }
            Assert.False(Directory.Exists(Path.Combine(root, "torrents"))
                && Directory.EnumerateFiles(Path.Combine(root, "torrents"), "*", SearchOption.AllDirectories).Any());
        }
    }
}
