using System.Net;
using System.Net.Http.Headers;
using MonoTorrent;
using MonoTorrent.Client;
using Ru7100Probe;

if (args.Length >= 3 && args[0] == "--start-check")
{
    await StartCheck.RunAsync(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 0, args.Length > 4 ? args[4] : null);
    return;
}
if (args.Length == 5 && args[0] == "--utp-cat")
{
    Environment.Exit(await UtpInterop.CatAsync(args[1], args[2], args[3], args[4]));
}
if (args.Length == 2 && args[0] == "--tcp-sink")
{
    await UtpInterop.TcpSinkAsync(int.Parse(args[1]));
    return;
}
if (args.Length >= 5 && args[0] == "--utp-engine-check")
{
    Environment.Exit(await UtpEngineCheck.RunAsync(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4],
        args.Length > 5 ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 30));
}
if (args.Length >= 5 && args[0] == "--udp-relay")
{
    await UtpInterop.RelayAsync(int.Parse(args[1]), int.Parse(args[2]), double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
        int.Parse(args[4]), args.Length > 5 ? double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 120);
    return;
}
if (args.Length == 1 && args[0] == "--cache-check")
{
    await CacheCheck.RunAsync();
    return;
}
await AddonCheck.RunAsync();
if (args.Length == 4 && args[0] == "--magnet")
{
    await MagnetCheck.RunAsync(args[1], args[2], int.Parse(args[3]));
    return;
}
var storage = Directory.CreateTempSubdirectory("ru7100-transport-");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    using var metadata = typeof(Program).Assembly.GetManifestResourceStream("sintel.torrent")
        ?? throw new InvalidOperationException("Sintel torrent metadata is missing.");
    var torrent = Torrent.Load(metadata);
    var settings = new EngineSettingsBuilder
    {
        CacheDirectory = Path.Combine(storage.FullName, "engine"),
        AllowLocalPeerDiscovery = false,
        AllowPortForwarding = false,
        WebSeedDelay = TimeSpan.FromDays(1)
    };
    using var engine = new ClientEngine(settings.ToSettings());
    var manager = await engine.AddStreamingAsync(torrent, storage.FullName);
    var video = manager.Files.Single(file => file.Path.EndsWith("Sintel.mp4", StringComparison.Ordinal));
    foreach (var file in manager.Files.Where(file => file != video))
        await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
    await manager.StartAsync();
    var peers = await manager.StreamProvider.CreateStreamAsync(video, false, deadline.Token);
    var seed = new WebSeedStream(peers, manager, video);
    using var server = new TorrentMediaServer(seed, seed.IsCached);

    using var http = new HttpClient();
    using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.Url), deadline.Token);
    if (head.Content.Headers.ContentLength != video.Length)
        throw new InvalidOperationException("The media length was missing from HEAD.");
    foreach (var start in new[] { 0L, video.Length - 600214, 65_000_000L })
    {
        const int count = 65_536;
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
        request.Headers.Range = new RangeHeaderValue(start, start + count - 1);
        using var response = await http.SendAsync(request, deadline.Token);
        if (response.StatusCode != HttpStatusCode.PartialContent
            || (await response.Content.ReadAsByteArrayAsync(deadline.Token)).Length != count)
            throw new InvalidOperationException($"The local server did not deliver the requested bytes at {start}.");
    }
    if (seed.WebPieces == 0)
        throw new InvalidOperationException("No hashed web seed pieces were exercised in this run.");
    Console.WriteLine(server.Diagnostics);
    Console.WriteLine($"Verified web seed pieces: {seed.WebPieces}");

    if (args.Contains("--serve"))
    {
        Console.WriteLine($"Open {server.Url} on this Mac. Press Enter to stop.");
        Console.ReadLine();
    }
    await manager.StopAsync();
}
finally
{
    storage.Delete(recursive: true);
}
