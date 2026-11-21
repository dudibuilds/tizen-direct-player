using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections.Peer;

// MonoTorrent main's engine with uTP as the only transport: download a torrent from one libtorrent seed (uTP only),
// check the file's SHA-256, print ENGINE-READY, then keep seeding on <listenPort> so a uTP-only libtorrent leecher can
// download it back over incoming uTP. Mirrors desktop/UtpEngineCheck.cs for our engine.
//
//   UtpCompare.MtEngine <torrentFile> <seedPort> <listenPort> <expectSha256> <workDir> [seedSeconds]
var torrentPath = args[0];
var seedPort = int.Parse(args[1]);
var listenPort = int.Parse(args[2]);
var expectSha = args[3];
var work = args[4];
var seedSeconds = args.Length > 5 ? int.Parse(args[5]) : 120;

var settings = new EngineSettings
{
    AllowedTransports = ImmutableArray.Create(PeerTransport.Utp),
    AllowLocalPeerDiscovery = false,
    AllowPortForwarding = false,
    AutoSaveLoadDhtCache = false,
    AutoSaveLoadFastResume = false,
    AutoSaveLoadMagnetLinkMetadata = false,
    CacheDirectory = Path.Combine(work, "cache"),
    ListenEndPoints = ImmutableDictionary<string, IPEndPoint>.Empty.Add("ipv4", new IPEndPoint(IPAddress.Loopback, listenPort)),
    EnableDht = false,
};
using var engine = new ClientEngine(settings);
var torrent = await Torrent.LoadAsync(torrentPath);
var manager = await engine.AddAsync(torrent, Path.Combine(work, "download"));
var clock = Stopwatch.StartNew();
await manager.StartAsync();
await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seedPort}")));
double firstPieceMs = -1;
manager.PieceHashed += (_, e) => { if (e.HashPassed && firstPieceMs < 0) firstPieceMs = clock.Elapsed.TotalMilliseconds; };
while (manager.State != TorrentState.Seeding && clock.Elapsed < TimeSpan.FromSeconds(120))
{
    await Task.Delay(500);
    if (manager.Peers.Available == 0 && manager.OpenConnections == 0)
        await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{seedPort}")));
    Console.Error.WriteLine($"mt-engine: {manager.State} {manager.Progress:0.0}% open={manager.OpenConnections} down={manager.Monitor.DownloadRate / 1e6:0.00} MB/s");
}
var downloadSeconds = clock.Elapsed.TotalSeconds;
if (manager.State != TorrentState.Seeding)
{
    Console.WriteLine($"ENGINE-FAIL state={manager.State} progress={manager.Progress:0.0}%");
    return 1;
}
var file = Path.Combine(work, "download", torrent.Files[0].Path);
string sha;
using (var stream = File.OpenRead(file))
    sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
if (sha != expectSha)
{
    Console.WriteLine($"ENGINE-FAIL sha mismatch {sha}");
    return 1;
}
Console.WriteLine($"ENGINE-READY download_s={downloadSeconds:0.00} first_piece_ms={firstPieceMs:0} " +
    $"MBps={torrent.Size / 1e6 / downloadSeconds:0.00} sha=ok");
var seedClock = Stopwatch.StartNew();
while (seedClock.Elapsed < TimeSpan.FromSeconds(seedSeconds))
    await Task.Delay(1000);
await manager.StopAsync();
return 0;
