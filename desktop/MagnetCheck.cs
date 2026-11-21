using System.Net;
using System.Net.Http.Headers;
using MonoTorrent;
using MonoTorrent.Client;
using Ru7100Probe;

internal static class MagnetCheck
{
    // Resolves a Torrentio stream, fetches magnet metadata, and reads ranges through the TV's media server code.
    public static async Task RunAsync(string type, string id, int index)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var addons = new StremioAddonClient();
        var streams = (await addons.GetStreamsAsync(new Uri(StreamAddon.Base + "/manifest.json"), type, id, deadline.Token))
            .Where(s => s.IsTorrent).ToArray();
        var stream = streams[index];
        Console.WriteLine($"{streams.Length} torrent streams; using {stream.Hints?.Filename} ({stream.Sources?.Length ?? 0} sources)");
        var storage = Directory.CreateTempSubdirectory("ru7100-magnet-");
        try
        {
            var settings = new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(storage.FullName, "engine"),
                AllowLocalPeerDiscovery = false,
                AllowPortForwarding = false,
                AutoSaveLoadFastResume = false,
            };
            using var engine = new ClientEngine(settings.ToSettings());
            var manager = await engine.AddStreamingAsync(stream.Magnet(), storage.FullName);
            await manager.StartAsync();
            TrackerFanout.AnnounceToAll(manager, deadline.Token);
            var started = DateTime.UtcNow;
            var wait = manager.WaitForMetadataAsync(deadline.Token);
            while (!wait.IsCompleted)
            {
                await Task.WhenAny(wait, Task.Delay(10_000));
                var tiers = string.Join(", ", manager.TrackerManager.Tiers.SelectMany(t => t.Trackers)
                    .Select(t => $"{t.Uri.Host}:{t.Status}/{t.FailureMessage}"));
                Console.WriteLine($"t+{(DateTime.UtcNow - started).TotalSeconds:F0}s state={manager.State} dht={engine.Dht.State}/{engine.Dht.NodeCount} available={manager.Peers.Available} open={manager.OpenConnections} trackers=[{tiers}]");
            }
            await wait;
            Console.WriteLine($"Metadata in {(DateTime.UtcNow - started).TotalSeconds:F1}s, {manager.Files.Count} files");
            var video = stream.SelectVideo(manager.Files);
            foreach (var file in manager.Files.Where(f => f != video))
                await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
            var peers = await manager.StreamProvider.CreateStreamAsync(video, false, deadline.Token);
            var pieceLength = manager.Torrent!.PieceLength;
            using var server = new TorrentMediaServer(peers,
                offset => manager.Bitfield[(int)((video.OffsetInTorrent + offset) / pieceLength)]);
            using var http = new HttpClient();
            foreach (var start in new[] { 0L, video.Length / 2 })
            {
                var t = DateTime.UtcNow;
                using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
                request.Headers.Range = new RangeHeaderValue(start, start + 65_535);
                using var response = await http.SendAsync(request, deadline.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                Console.WriteLine($"Range @{start}: {(int)response.StatusCode} {bytes.Length} bytes in {(DateTime.UtcNow - t).TotalSeconds:F1}s, peers={manager.Peers.Available}/{manager.OpenConnections}");
            }
            await manager.StopAsync();
        }
        finally { storage.Delete(recursive: true); }
    }
}
