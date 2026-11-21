using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using Ru7100.Core;
using Ru7100.Core.Playback;

namespace Ru7100Probe
{
    public sealed class TorrentSourceSettings
    {
        /// Holds torrents/<hash>/ (downloaded data) and cache/ (engine metadata). Swept on start.
        public string RootDirectory { get; set; }
        /// Idle (unleased) torrent data above this is evicted before a new torrent is added. The file being
        /// played can still grow to its full size; this does not bound a single active download.
        public long IdleCacheBudgetBytes { get; set; } = 1L * 1024 * 1024 * 1024;
        /// How long an unleased torrent is kept for a quick re-open (binge, failover back, restore).
        public TimeSpan IdleLinger { get; set; } = TimeSpan.FromMinutes(2);
        /// RAM held by all stream caches together. Each torrent is budgeted StreamCacheBytes, so an outgoing episode,
        /// race losers and lingering torrents could hold several buffers at once; above this, idle (unleased) torrents
        /// give their cache memory back at once, oldest first. Null: StreamCacheBytes plus 32 MB.
        public long? AggregateCacheBytes { get; set; }
        /// After its linger, the most recently played torrent is stopped instead of removed: its connections close and its
        /// cached pieces and cache memory are released, but it stays registered with its metadata and known peers, so a
        /// replay, resume or the next episode of the same pack reconnects at once. One torrent at most; the next played
        /// torrent replaces it. Costs a few KB (peer list, bitfield) and no sockets, CPU or cache memory.
        public bool KeepPlayedTorrent { get; set; } = true;
        /// Upper bound for magnet metadata even if the caller's token allows longer.
        public TimeSpan MetadataBudget { get; set; } = TimeSpan.FromMinutes(2);
        /// A torrent with no peer available and none connected after this long is treated as dead and given up, so
        /// the race moves on instead of waiting out its prepare budget (Torrentio seed counts are often stale).
        public TimeSpan DeadSwarmAfter { get; set; } = TimeSpan.FromSeconds(8);
        public int MaximumConnections { get; set; } = 60;
        /// Engine-wide. Parallel race candidates share it, and most swarm addresses are dead or behind NAT: with the
        /// MonoTorrent default (8) and a 10 s connect timeout, live peers waited behind dead ones for 15-30 s.
        public int MaximumHalfOpenConnections { get; set; } = 40;
        /// Outgoing TCP connect + handshake budget. 4 s cut off live but distant peers: about 40% of the TV's ~300 connect
        /// attempts per torrent ended in a timeout. 10 s (MonoTorrent's default) let dead addresses hold half-open slots
        /// for too long, and other on-TV MonoTorrent / torrent-stream clients settled on 5 s after finding 10 s worse.
        /// 6 s keeps slow-but-alive peers (a cross-continent SYN-ACK plus the BitTorrent handshake) without that cost.
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(6);
        /// A peer holding our requests without sending a block for this long is disconnected and its requests go to other
        /// peers. MonoTorrent's default (40 s) lets one stalled peer hold blocks of the head piece for a long time.
        public TimeSpan StaleRequestTimeout { get; set; } = TimeSpan.FromSeconds(40);
        /// Peer wire encryption accepted, in order of preference. MonoTorrent's default tries RC4 first; peers without
        /// encryption support then fail the negotiation (20 of ~300 attempts per torrent on the TV) and every RC4
        /// handshake costs a Diffie-Hellman exchange on the TV's weak CPU. Plain text first, RC4 still accepted for
        /// peers that insist on it (incoming connections and retries).
        public IReadOnlyList<EncryptionType> AllowedEncryption { get; set; } = new[] { EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full };
        /// DHT bootstrap routers; MonoTorrent's built-in router.bittorrent.com does not answer from here.
        public IReadOnlyList<string> DhtRouters { get; set; } = new[] { "dht.transmissionbt.com", "router.utorrent.com", "dht.libtorrent.org" };
        /// Fixed listen ports, tried in order (TCP on IPv4 and IPv6, UDP for the DHT); the first free one is used. A fixed
        /// port lets a UPnP mapping, the DHT and the trackers all advertise one stable address, so peers that learned it
        /// can connect in. Below 32768 so no outgoing connection's ephemeral port can take it (Linux uses
        /// 32768-60999, IANA 49152-65535), and away from 6881-6889 / 51413, the BitTorrent defaults some ISPs throttle.
        public IReadOnlyList<int> ListenPorts { get; set; } = new[] { 27492, 29317, 31846 };
        /// UPnP / NAT-PMP mapping of the listen ports on the home router (Mono.Nat). Failures only cost the mapping.
        public bool AllowPortForwarding { get; set; } = true;
        /// uTP (BEP 29, probe/Sources/Utp) beside TCP: outgoing IPv4 connects race TCP against uTP, and the listen port
        /// also accepts uTP, on one UDP socket shared with the DHT. Many swarm peers (uTorrent, libtorrent behind NAT)
        /// are reachable only over uTP. Off: TCP only, MonoTorrent's own DHT socket.
        public bool EnableUtp { get; set; } = true;
        /// When the uTP leg of an outgoing race connects first, TCP to the same peer still wins if it connects within
        /// this long (see RacingPeerConnection). Zero: first to connect wins.
        public TimeSpan UtpTcpGrace { get; set; } = TimeSpan.FromMilliseconds(500);
        /// uTP receive window per connection: what a peer may have in flight to us, and what we hold in managed memory
        /// while MonoTorrent does not read (paused: it reads ~1 KB/s). 256 KB x 60 peers bounds that at 15 MB; it
        /// allows ~1.3 MB/s per peer at a 200 ms round trip, above what a single uTP peer usually sends.
        public int UtpReceiveBufferBytes { get; set; } = 256 * 1024;
        /// 0 = unlimited. Program.cs capped the probe at 1.2 MB/s, below a 6.5 GB episode's bitrate.
        public int MaximumDownloadRate { get; set; }
        /// Upload cap. Peers unchoke those who upload to them (tit-for-tat), so 200 KB/s held back our own download; the
        /// home line has ~100 Mbit/s up, and serving cached pieces from RAM costs the TV little CPU. 0 = unlimited.
        public int MaximumUploadRate { get; set; } = 4 * 1024 * 1024;
        public int DiskCacheBytes { get; set; } = 8 * 1024 * 1024;
        /// Rolling cache per torrent (RollingPieceWriter): disk used by one stream never exceeds this, whatever the
        /// movie's size. Shrunk to fit free space; null disables the rolling cache (whole-file storage).
        public long? StreamCacheBytes { get; set; } = 160L * 1024 * 1024;
        /// Keep the rolling cache in RAM (default). The RU7100's app partition is ~946 MB with ~100 MB free, so a
        /// disk cache does not fit; RAM buffering is also what streaming apps do. Disk mode is for devices with room.
        public bool StreamCacheInMemory { get; set; } = true;
        /// Kept behind the playhead for quick rewinds (part of StreamCacheBytes).
        public long RewindBytes { get; set; } = 32L * 1024 * 1024;
        /// Left free on the device for the system and other apps.
        public long FreeSpaceReserveBytes { get; set; } = 400L * 1024 * 1024;
        /// Below this the source is refused with a clear "not enough storage" error instead of failing mid-movie.
        public long MinimumCacheBytes { get; set; } = 192L * 1024 * 1024;
        /// Container index kept at both ends of the file being played.
        public long PinnedEdgeBytes { get; set; } = 8L * 1024 * 1024;
        /// The file's last bytes, requested at high priority together with the head during prebuffer: AVPlay reads the
        /// MKV cues / MP4 moov there during prepare (it took 1-4.5 s on the Mac when fetched on demand). At least one piece.
        public long TailPrefetchBytes { get; set; } = 2L * 1024 * 1024;
        /// Once the head is in, prebuffer waits at most this long for the tail before handing the source to the player
        /// (which then fetches the rest on demand): a slow tail must not hold back a start that could already begin.
        public TimeSpan TailWaitAfterHead { get; set; } = TimeSpan.FromSeconds(3);
        /// Request each peer's whole pipeline for the playhead window and the edges (EdgeStreamingPieceRequester).
        public bool StreamFullPipeline { get; set; } = true;
        /// Torrent metadata (.torrent files fetched from magnets) kept across plays and app restarts: a replay or the next
        /// episode of a season pack skips the 1-11 s metadata exchange. Oldest files go first past either limit; a file is
        /// tens to a few hundred KB, so the defaults stay within ~8 MB of the TV's ~100 MB free.
        public int MetadataCacheFiles { get; set; } = 150;
        public long MetadataCacheBytes { get; set; } = 8L * 1024 * 1024;
        /// Next-episode prefetch (PrefetchAsync): head bytes fetched ahead (at least one piece), the time allowed for head
        /// and tail, and how long a prefetched torrent is held (paused) waiting to be opened.
        public long PrefetchHeadBytes { get; set; } = 8L * 1024 * 1024;
        public TimeSpan PrefetchBudget { get; set; } = TimeSpan.FromSeconds(90);
        public TimeSpan PrefetchHold { get; set; } = TimeSpan.FromMinutes(10);
        public Action<string> Log { get; set; }
    }

    public sealed class TorrentStats
    {
        public bool HasMetadata { get; set; }
        public int PieceLength { get; set; }
        public long DataBytesReceived { get; set; }
        public int VerifiedPieces { get; set; }
        public int OpenConnections { get; set; }
        public int Interested { get; set; }
        public int UnchokedBy { get; set; }
    }

    /// ISourceOpener for the TV: one shared MonoTorrent engine, a loopback HTTP server per opened file, and
    /// per-hash leases so a torrent shared by several candidates or sessions is never torn down under a stream.
    /// Direct HTTP URLs pass straight through to the player.
    public sealed class TorrentSourceOpener : IReportingSourceOpener, IDisposable
    {
        private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".m4v", ".mov", ".ts", ".webm", ".avi" };
        private readonly TorrentSourceSettings settings;
        private readonly SemaphoreSlim engineGate = new SemaphoreSlim(1, 1);
        private string gateHolder = "";
        private DateTime gateSince;
        private async Task EnterGateAsync(string who, CancellationToken token = default)
        {
            var waitStart = DateTime.UtcNow;
            var holder = gateHolder;
            await engineGate.WaitAsync(token).ConfigureAwait(false);
            var waited = (DateTime.UtcNow - waitStart).TotalMilliseconds;
            if (waited > 1000)
                Log($"gate: {who} waited {waited / 1000:0.0} s (held by {holder})");
            gateHolder = who;
            gateSince = DateTime.UtcNow;
        }
        private void LeaveGate()
        {
            var held = (DateTime.UtcNow - gateSince).TotalMilliseconds;
            if (held > 1000)
                Log($"gate: {gateHolder} held {held / 1000:0.0} s");
            gateHolder = "";
            engineGate.Release();
        }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private ClientEngine engine;
        private RollingPieceWriter writer;
        private SafePortForwarder forwarder;
        private MonoTorrent.Dht.IDhtEngine dht;
        private ListenPorts ports;
        private TorrentManager anchor;
        private int anchoring;
        /// Hash of the most recently played torrent (the last opened source to close): kept stopped, not removed.
        private string keptHash;
        private Utp.UtpTransport utp;
        private readonly ConnectionDiagnostics connections = new ConnectionDiagnostics();
        private readonly NetDiagnostics net = new NetDiagnostics();
        /// Network link, measured peak / sustained torrent download rate and CPU use (dev.diagnostics).
        public string NetworkHealth => net.Describe();
        /// Best 10 s average download rate seen on this device (0 until a stream has run 10 s).
        public double SustainedDownloadBytesPerSecond => net.SustainedBytesPerSecond;
        /// Per-transport counters (handshakes and bytes over TCP / uTP).
        internal ConnectionDiagnostics Connections => connections;
        /// Outgoing peer connection counters and listener state (also part of every swarm log line).
        public string ConnectionSummary => connections.Describe();
        /// The router's external address as reported over UPnP / NAT-PMP, when a router answered.
        public IPAddress RouterExternalAddress => forwarder?.ExternalAddress;
        /// Listen ports and the router's port-mapping outcome.
        public string NetworkSummary => ports == null ? "engine not started" : $"port={ports} nat={forwarder?.Describe() ?? "off"} {utp?.Describe() ?? "utp=off"}";
        private bool disposed;

        public TorrentSourceOpener(TorrentSourceSettings settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(settings.RootDirectory))
                throw new ArgumentException("A root directory is required.", nameof(settings));
            SweepRoot();
        }

        private string TorrentsDirectory => Path.Combine(settings.RootDirectory, "torrents");
        private string CacheDirectory => Path.Combine(settings.RootDirectory, "cache");

        /// Number of torrents currently registered with the engine (leased, lingering or kept stopped).
        public int ActiveTorrents { get { lock (entries) return entries.Count; } }

        /// Torrents kept stopped for a quick replay (KeepPlayedTorrent).
        public int ParkedTorrents { get { lock (entries) return entries.Values.Count(e => e.Parked); } }

        /// Whether the shared engine (listeners, port mapping, DHT) is running.
        public bool EngineRunning => engine?.IsRunning ?? false;

        /// Start diagnostics for one registered torrent (the Mac start-time harness polls it): piece length (0 before
        /// metadata), payload bytes received, verified pieces and open connections. Null when the hash is not registered.
        public TorrentStats StatsOf(string hash)
        {
            Entry entry;
            lock (entries)
                entries.TryGetValue((hash ?? "").ToLowerInvariant(), out entry);
            var manager = entry?.Manager;
            if (manager == null)
                return null;
            try
            {
                return new TorrentStats
                {
                    HasMetadata = manager.HasMetadata,
                    PieceLength = manager.HasMetadata ? manager.Torrent.PieceLength : 0,
                    DataBytesReceived = manager.Monitor.DataBytesReceived,
                    VerifiedPieces = manager.HasMetadata ? manager.Bitfield.TrueCount : 0,
                    OpenConnections = manager.OpenConnections,
                };
            }
            catch (Exception) { return null; }
        }

        /// StatsOf plus how many connected peers we are interested in, and how many of those unchoke us.
        public async Task<TorrentStats> StatsOfAsync(string hash)
        {
            var stats = StatsOf(hash);
            if (stats == null)
                return null;
            Entry entry;
            lock (entries)
                entries.TryGetValue((hash ?? "").ToLowerInvariant(), out entry);
            try
            {
                var peers = await entry.Manager.GetPeersAsync().ConfigureAwait(false);
                stats.Interested = peers.Count(p => p.AmInterested);
                stats.UnchokedBy = peers.Count(p => p.AmInterested && !p.IsChoking);
            }
            catch (Exception) { }
            return stats;
        }

        public Task<IOpenedSource> OpenAsync(StreamCandidate candidate, CancellationToken token) => OpenAsync(candidate, null, token);

        /// As OpenAsync; <paramref name="progress"/> gets metadata / peers / download rate twice a second until the source
        /// is ready or fails (the playback race uses it to stop the other candidates once one is clearly healthy).
        public async Task<IOpenedSource> OpenAsync(StreamCandidate candidate, Action<OpenProgress> progress, CancellationToken token)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));
            if (disposed)
                throw new ObjectDisposedException(nameof(TorrentSourceOpener));
            if (string.IsNullOrEmpty(candidate.InfoHash))
            {
                if (!StremioAddonClient.IsWebUrl(candidate.Url))
                    throw new InvalidDataException("The source has neither an info hash nor an HTTP URL.");
                return new DirectSource(candidate.Url, candidate.SizeBytes ?? -1);
            }
            var hash = candidate.InfoHash.Trim().ToLowerInvariant();
            if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("The source's info hash is not a BitTorrent v1 hash.");
            if (candidate.FileIndex < 0)
                throw new InvalidDataException("The source's file index is negative.");

            var openStarted = DateTime.UtcNow;
            Log($"open requested {hash}");
            var entry = await AcquireAsync(hash, candidate.Trackers, token).ConfigureAwait(false);
            Log($"acquired {hash} after {(DateTime.UtcNow - openStarted).TotalSeconds:0.0} s");
            CancellationTokenSource prefetched;
            lock (prefetchLeases)
                if (prefetchLeases.TryGetValue(hash, out prefetched))
                    prefetchLeases.Remove(hash);
            prefetched?.Cancel();   // this open holds its own lease now
            var reporting = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                if (progress != null)
                    _ = ReportProgressAsync(entry.Manager, progress, reporting.Token);
                var manager = entry.Manager;
                // Tracker tiers and the torrent start run outside the engine gate (see CreateEntryAsync).
                await entry.Started.WithCancellationCompat(token).ConfigureAwait(false);
                await WaitForMetadataAsync(manager, hash, token).ConfigureAwait(false);
                Log($"metadata {hash} after {(DateTime.UtcNow - openStarted).TotalSeconds:0.0} s: {DescribeSwarm(manager)}");
                var file = SelectVideo(manager.Files, candidate.FileIndex);
                await entry.StreamGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (entry)
                        entry.Wanted[file.Path] = entry.Wanted.TryGetValue(file.Path, out var count) ? count + 1 : 1;
                    // The torrent's requester outlives its streams. On a re-open (replay, next episode of a pack, a kept
                    // torrent) it still points at the previous stream's playhead, and a new stream starts at position 0
                    // without moving it: peers' pipelines filled with pieces around the old position while the head
                    // waited (~35 pieces before the first one on the Mac). Point it at the head before any request.
                    var current = EdgeStreamingPieceRequester.Of(manager);
                    if (current != null)
                    {
                        current.EdgesOnly = false;   // a prefetched torrent streams normally from here
                        current.SeekToPosition(file, 0);
                    }
                    await ApplyPrioritiesAsync(entry).ConfigureAwait(false);
                    // MonoTorrent allows one live stream per torrent: the newest open takes it over (a season pack's
                    // next episode, or a re-open of the same hash). The previous holder's server stops serving.
                    entry.ActiveOwner?.Preempt();
                    Stream stream = null;
                    try
                    {
                        // Created without MonoTorrent's prebuffer and warmed here instead, so a cancelled warm-up can
                        // dispose the stream and free the torrent's single stream slot for the next attempt.
                        stream = await manager.StreamProvider.CreateStreamAsync(file, false, token).ConfigureAwait(false);
                        entry.Cache?.Pin(file, settings.PinnedEdgeBytes);   // before the edges arrive, so they stay
                        var tail = TailPieces(manager, file);
                        var requester = EdgeStreamingPieceRequester.Of(manager);
                        requester?.SetEdges(tail);
                        var timing = await PrebufferAsync(stream, manager, requester == null ? ((int, int)?)null : tail, settings.TailWaitAfterHead, token).ConfigureAwait(false);
                        Log($"prebuffered {hash} after {(DateTime.UtcNow - openStarted).TotalSeconds:0.0} s ({timing}): {DescribeSwarm(manager)}");
                    }
                    catch
                    {
                        try { stream?.Dispose(); } catch (Exception error) { Log("stream cleanup: " + error.Message); }
                        lock (entry)
                            Unwant(entry, file.Path);
                        try { await ApplyPrioritiesAsync(entry).ConfigureAwait(false); }
                        catch (Exception error) { Log("priorities: " + error.Message); }
                        throw;
                    }
                    var pieceLength = manager.Torrent.PieceLength;
                    var offset = file.OffsetInTorrent;
                    OpenedTorrent opened = null;
                    var server = new TorrentMediaServer(stream, position =>
                    {
                        try { return manager.Bitfield[(int)((offset + position) / pieceLength)]; }
                        catch (Exception) { return false; }
                    }, (position, cached) => opened?.OnRange(position, cached));
                    opened = new OpenedTorrent(this, entry, file, stream, server);
                    entry.ActiveOwner = opened;
                    Log($"opened {hash} after {(DateTime.UtcNow - openStarted).TotalSeconds:0.0} s file={file.Path} length={file.Length} {net.Describe()}");
                    return opened;
                }
                finally { entry.StreamGate.Release(); }
            }
            catch
            {
                Release(entry);
                throw;
            }
            finally
            {
                reporting.Cancel();
                reporting.Dispose();
            }
        }

        /// Waits for magnet metadata within MetadataBudget; gives up early on a dead swarm (DeadSwarmAfter).
        private async Task WaitForMetadataAsync(TorrentManager manager, string hash, CancellationToken token)
        {
            if (manager.HasMetadata)
                return;
            using (var metadata = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            {
                metadata.CancelAfter(settings.MetadataBudget);
                try
                {
                    var wait = manager.WaitForMetadataAsync(metadata.Token);
                    var watched = DateTime.UtcNow;
                    while (!wait.IsCompleted)
                    {
                        await Task.WhenAny(wait, Task.Delay(1000, metadata.Token)).ConfigureAwait(false);
                        if (wait.IsCompleted || metadata.IsCancellationRequested)
                            break;
                        if (DateTime.UtcNow - watched >= settings.DeadSwarmAfter && manager.Peers.Available == 0 && manager.OpenConnections == 0)
                        {
                            Log($"dead swarm {hash}: no peers after {settings.DeadSwarmAfter.TotalSeconds:0} s: {DescribeSwarm(manager)}");
                            throw new IOException("No peers for this torrent (dead swarm).");
                        }
                    }
                    await wait.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Log("no metadata for " + hash + ": " + DescribeSwarm(manager));
                    throw new TimeoutException("Torrent metadata did not arrive in time.");
                }
                catch (OperationCanceledException)
                {
                    Log("metadata wait cancelled for " + hash + ": " + DescribeSwarm(manager));
                    throw;
                }
            }
        }

        // ---------------- next-episode prefetch ----------------

        /// Prefetch leases by hash: a prefetched torrent stays registered (paused, peers known, head and tail cached) until
        /// it is opened, another prefetch replaces it, or PrefetchHold passes.
        private readonly Dictionary<string, CancellationTokenSource> prefetchLeases = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        /// Fetches what the next start needs before it is asked for: the torrent's metadata, then the head
        /// (PrefetchHeadBytes, at least one piece) and the tail (TailPrefetchBytes) of the candidate's video file, into the
        /// rolling cache with no stream open. A torrent that is not playing is paused afterwards (connections and peer list
        /// kept) and held for PrefetchHold. The next episode of a playing season pack is fetched beside the playing file
        /// as extra edge ranges; its pieces are pinned in the cache so playback cannot evict them. A later OpenAsync of the
        /// candidate finds the pieces present and prebuffers at once. Returns a short timing summary.
        public async Task<string> PrefetchAsync(StreamCandidate candidate, CancellationToken token)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.InfoHash))
                return "not a torrent";
            if (disposed)
                throw new ObjectDisposedException(nameof(TorrentSourceOpener));
            var hash = candidate.InfoHash.Trim().ToLowerInvariant();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            ReleasePrefetches(except: hash);
            CancellationTokenSource hold;
            lock (prefetchLeases)
            {
                if (prefetchLeases.ContainsKey(hash))
                    return "already prefetched";
                prefetchLeases[hash] = hold = new CancellationTokenSource();
            }
            Entry entry;
            try { entry = await AcquireAsync(hash, candidate.Trackers, token).ConfigureAwait(false); }
            catch
            {
                lock (prefetchLeases)
                    if (prefetchLeases.TryGetValue(hash, out var current) && current == hold)
                        prefetchLeases.Remove(hash);
                throw;
            }
            _ = HoldPrefetchAsync(entry, hash, hold);
            var manager = entry.Manager;
            ITorrentManagerFile file;
            try
            {
                await entry.Started.WithCancellationCompat(token).ConfigureAwait(false);
                await WaitForMetadataAsync(manager, hash, token).ConfigureAwait(false);
                file = SelectVideo(manager.Files, candidate.FileIndex);
            }
            catch
            {
                hold.Cancel();   // a failed prefetch must not hold the torrent
                throw;
            }
            var metadataAt = timer.Elapsed.TotalSeconds;
            var shared = entry.ActiveOwner != null;   // a file of this torrent is playing (season pack)
            var pieceLength = manager.Torrent.PieceLength;
            var headCount = (int)Math.Max(1, (settings.PrefetchHeadBytes + pieceLength - 1) / pieceLength);
            var head = (Start: file.StartPieceIndex, End: Math.Min(file.EndPieceIndex, file.StartPieceIndex + headCount - 1));
            var tail = TailPieces(manager, file);
            var requester = EdgeStreamingPieceRequester.Of(manager);
            lock (entry)
                entry.Wanted[file.Path] = entry.Wanted.TryGetValue(file.Path, out var count) ? count + 1 : 1;
            var completed = false;
            try
            {
                await ApplyPrioritiesAsync(entry).ConfigureAwait(false);
                entry.Cache?.Pin(file, Math.Max(settings.PinnedEdgeBytes, settings.PrefetchHeadBytes), keepExisting: shared);
                if (shared)
                    requester?.AddEdges(head, tail);
                else if (requester != null)
                {
                    requester.SeekToPosition(file, 0);
                    requester.EdgesOnly = true;
                    requester.SetEdges(head, tail);
                }
                if (manager.State == TorrentState.Paused || manager.State == TorrentState.Stopped)
                    await manager.StartAsync().ConfigureAwait(false);
                bool Present((int Start, int End) range)
                {
                    var bits = manager.Bitfield;
                    for (var i = range.Start; i <= range.End; i++)
                        if (!bits[i])
                            return false;
                    return true;
                }
                while (!(Present(head) && Present(tail)))
                {
                    if (timer.Elapsed > settings.PrefetchBudget)
                    {
                        Log($"prefetch {hash}: budget spent, head {(Present(head) ? "in" : "missing")}, tail {(Present(tail) ? "in" : "missing")}");
                        break;
                    }
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                completed = true;
            }
            finally
            {
                if (!completed)
                    hold.Cancel();   // cancelled or failed: do not hold the torrent
                if (shared)
                    requester?.RemoveEdges(head, tail);
                lock (entry)
                    Unwant(entry, file.Path);
                try { await ApplyPrioritiesAsync(entry).ConfigureAwait(false); }
                catch (Exception error) { Log("prefetch priorities: " + error.Message); }
                lock (entry)
                    shared = entry.ActiveOwner != null;
                if (!shared && manager.State == TorrentState.Downloading)
                {
                    try { await manager.PauseAsync().ConfigureAwait(false); }
                    catch (Exception error) { Log("prefetch pause: " + error.Message); }
                }
            }
            var summary = $"metadata {metadataAt:0.0} s, head+tail {timer.Elapsed.TotalSeconds:0.0} s ({head.End - head.Start + 1}+{tail.End - tail.Start + 1} pieces of {pieceLength / 1024} KiB, {(shared ? "beside the playing file" : "own torrent, paused")}) cache={DescribeCache(manager)}";
            Log($"prefetched {hash}: {summary}");
            return summary;
        }

        /// Drops prefetch leases (all, or all but one hash); the torrents then linger and go like any unleased torrent.
        public void ReleasePrefetches(string except = null)
        {
            List<CancellationTokenSource> released;
            lock (prefetchLeases)
            {
                released = prefetchLeases.Where(p => p.Key != except).Select(p => p.Value).ToList();
                foreach (var key in prefetchLeases.Keys.Where(k => k != except).ToList())
                    prefetchLeases.Remove(key);
            }
            foreach (var hold in released)
                hold.Cancel();
        }

        private async Task HoldPrefetchAsync(Entry entry, string hash, CancellationTokenSource hold)
        {
            try { await Task.Delay(settings.PrefetchHold, hold.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            lock (prefetchLeases)
                if (prefetchLeases.TryGetValue(hash, out var current) && current == hold)
                    prefetchLeases.Remove(hash);
            Release(entry);
        }

        private static async Task ReportProgressAsync(TorrentManager manager, Action<OpenProgress> progress, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    progress(new OpenProgress
                    {
                        HasMetadata = manager.HasMetadata, Peers = manager.OpenConnections, DownloadBytesPerSecond = manager.Monitor.DownloadRate,
                    });
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }
        }

        /// Torrent pieces holding the file's last TailPrefetchBytes (at least the last piece).
        private (int Start, int End) TailPieces(TorrentManager manager, ITorrentManagerFile file)
        {
            var start = Math.Max(0, file.Length - Math.Max(1, settings.TailPrefetchBytes));
            var first = manager.Torrent.ByteOffsetToPieceIndex(file.OffsetInTorrent + start);
            return (Math.Max(file.StartPieceIndex, Math.Min(first, file.EndPieceIndex)), file.EndPieceIndex);
        }

        /// Head and tail fetched together. Done when the first piece is verified (proof the swarm delivers) and the tail
        /// pieces are present, or <paramref name="tailWait"/> after the head when the tail is slow. Never seeks or reads
        /// the stream at the tail: that would move the playhead priority there and cancel the head's requests. Without the
        /// edge requester (MonoTorrent internals changed) only the head is awaited, as before.
        private static async Task<string> PrebufferAsync(Stream stream, TorrentManager manager, (int Start, int End)? tail, TimeSpan tailWait,
            CancellationToken token)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var buffer = new byte[1];
            stream.Seek(0, SeekOrigin.Begin);
            var read = stream.ReadAsync(buffer, 0, 1, token);
            await read.WithCancellationCompat(token).ConfigureAwait(false);
            if (await read.ConfigureAwait(false) == 0 && stream.Length > 0)
                throw new IOException("The torrent stream ended early.");
            stream.Seek(0, SeekOrigin.Begin);
            var head = timer.Elapsed.TotalSeconds;
            if (tail == null)
                return $"head {head:0.0} s";
            bool TailPresent()
            {
                var bits = manager.Bitfield;
                for (var i = tail.Value.Start; i <= tail.Value.End; i++)
                    if (!bits[i])
                        return false;
                return true;
            }
            var headDone = timer.Elapsed;
            while (!TailPresent())
            {
                if (timer.Elapsed - headDone >= tailWait)
                    return $"head {head:0.0} s, tail not in after {tailWait.TotalSeconds:0} s more";
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            return $"head {head:0.0} s, tail {timer.Elapsed.TotalSeconds:0.0} s";
        }

        // ---------------- leases ----------------

        private sealed class Entry
        {
            public string Hash;
            public string Directory;
            public TorrentManager Manager;
            public int Leases;
            public DateTimeOffset LastUsed;
            public CancellationTokenSource Linger;
            public Task Removal;
            public bool Removing;
            public readonly SemaphoreSlim StreamGate = new SemaphoreSlim(1, 1);
            public readonly Dictionary<string, int> Wanted = new Dictionary<string, int>(StringComparer.Ordinal);
            public OpenedTorrent ActiveOwner;
            public RollingPieceWriter.Cache Cache;
            /// Trackers added and the manager started; completed for entries that were already running.
            public Task Started = Task.CompletedTask;
            /// Stopping a kept torrent (ParkLocked); a re-open waits for it before restarting the manager.
            public Task Parking = Task.CompletedTask;
            /// Stopped and kept for a quick replay, with its cache released.
            public bool Parked;
        }

        private async Task<Entry> AcquireAsync(string hash, IReadOnlyList<string> trackers, CancellationToken token)
        {
            while (true)
            {
                Task pendingRemoval = null;
                await EnterGateAsync("acquire " + hash, token).ConfigureAwait(false);
                try
                {
                    Entry entry;
                    lock (entries)
                        entries.TryGetValue(hash, out entry);
                    if (entry != null && entry.Removing)
                        pendingRemoval = entry.Removal;
                    else if (entry != null && !entry.Parking.IsCompleted)
                        pendingRemoval = entry.Parking;
                    else if (entry != null)
                    {
                        lock (entry)
                        {
                            entry.Leases++;
                            entry.LastUsed = DateTimeOffset.UtcNow;
                            entry.Linger?.Cancel();
                            entry.Linger = null;
                        }
                        var state = entry.Manager.State;
                        if (state == TorrentState.Paused || state == TorrentState.Stopped)
                        {
                            try
                            {
                                await entry.Manager.StartAsync().ConfigureAwait(false);
                                if (entry.Parked)
                                    Log($"restarted kept {hash}: {entry.Manager.Peers.Available} known peers");
                            }
                            catch (Exception error) { Log("resume: " + error.Message); }
                        }
                        entry.Parked = false;
                        return entry;
                    }
                    else
                    {
                        await EvictIdleAsync().ConfigureAwait(false);
                        var created = await CreateEntryAsync(hash, trackers).ConfigureAwait(false);
                        lock (entries)
                            entries[hash] = created;
                        return created;
                    }
                }
                finally { LeaveGate(); }
                // Re-adding a hash that is still registered fails, so wait for its teardown to finish (or, for a kept
                // torrent, for its stop to finish before restarting it).
                if (pendingRemoval != null)
                    await pendingRemoval.WithCancellationCompat(token).ConfigureAwait(false);
            }
        }

        /// Must hold engineGate, but only for registering the torrent: the first add on the TV held the gate ~1.9 s and
        /// the other race candidates queued behind it. Adding the tracker tiers and starting the manager (which also
        /// starts the engine and its DHT bootstrap if Warm has not) run after the gate is released, as entry.Started.
        private async Task<Entry> CreateEntryAsync(string hash, IReadOnlyList<string> trackers)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var current = EnsureEngine();
            var ensured = timer.ElapsedMilliseconds;
            var directory = Path.Combine(TorrentsDirectory, hash);
            Directory.CreateDirectory(directory);
            var capacity = CacheCapacityFor(hash);
            var manager = await current.AddStreamingAsync(Magnet(hash), directory).ConfigureAwait(false);
            var added = timer.ElapsedMilliseconds;
            connections.Watch(manager);
            RollingPieceWriter.Cache cache = null;
            if (capacity > 0 && writer != null)
                cache = writer.Register(directory, manager, settings.StreamCacheInMemory ? null : Path.Combine(directory, ".stream-cache"), capacity);
            if (cache != null)
                cache.BehindBytes = settings.RewindBytes;
            if (timer.ElapsedMilliseconds > 300)
                Log($"add {hash}: engine {ensured} ms, add {added - ensured} ms, total {timer.ElapsedMilliseconds} ms under the gate");
            var entry = new Entry { Hash = hash, Directory = directory, Manager = manager, Cache = cache, Leases = 1, LastUsed = DateTimeOffset.UtcNow };
            entry.Started = StartEntryAsync(entry, trackers);
            return entry;
        }

        private async Task StartEntryAsync(Entry entry, IReadOnlyList<string> trackers)
        {
            await Task.Yield();   // off the gate holder's stack
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var manager = entry.Manager;
            var hash = entry.Hash;
            // Root cause of the slow starts: magnet trackers all land in ONE tier, and MonoTorrent walks a tier's
            // trackers one after another (~15-45 s per dead or slow tracker). Explicit per-tracker announces
            // (TrackerFanout) queue behind that in-flight tier announce, so they did not help. One tier per tracker
            // makes MonoTorrent announce to every tracker at once, at start and on every re-announce.
            foreach (var tracker in TrackersFor(trackers))
            {
                try { await manager.TrackerManager.AddTrackerAsync(new Uri(tracker)).ConfigureAwait(false); }
                catch (Exception error) { Log("tracker " + tracker + ": " + error.Message); }
            }
            var tiers = timer.ElapsedMilliseconds;
            await manager.StartAsync().ConfigureAwait(false);
            Log($"added {hash} tiers={manager.TrackerManager.Tiers.Count} (trackers {tiers} ms, start {timer.ElapsedMilliseconds - tiers} ms)");
            _ = AnchorEngineAsync();
        }

        private void Release(Entry entry)
        {
            CancellationTokenSource linger = null;
            lock (entry)
            {
                entry.Leases--;
                entry.LastUsed = DateTimeOffset.UtcNow;
                if (entry.Leases <= 0 && !entry.Removing && !disposed)
                    entry.Linger = linger = new CancellationTokenSource();
            }
            if (linger != null)
                _ = LingerThenRemoveAsync(entry, linger.Token);
        }

        private async Task LingerThenRemoveAsync(Entry entry, CancellationToken cancelled)
        {
            // An unleased torrent stops downloading at once (cancelled race losers, closed sessions), and is
            // removed with its data after the linger period unless it is opened again.
            // A torrent that was never played (a race loser, an abandoned candidate) is stopped instead and its cache
            // memory freed: pausing does not hold a torrent that is still fetching its metadata, and on the TV such
            // losers went on to download into fresh 160 MB RAM caches for the whole linger (476 MB of caches, low-memory
            // warnings). Re-opening restarts it like a kept torrent.
            var played = entry.Hash == Volatile.Read(ref keptHash);
            await EnterGateAsync(("linger-pause") + " " + entry.Hash).ConfigureAwait(false);
            try
            {
                if (played && !cancelled.IsCancellationRequested && entry.Manager.State != TorrentState.Paused
                    && entry.Manager.State != TorrentState.Stopped)
                    await entry.Manager.PauseAsync().ConfigureAwait(false);
            }
            catch (Exception error) { Log("pause: " + error.Message); }
            finally { LeaveGate(); }
            if (!played && !cancelled.IsCancellationRequested)
                await StopUnplayedAsync(entry, cancelled).ConfigureAwait(false);
            ReclaimIdleMemory("linger " + entry.Hash);
            try { await Task.Delay(settings.IdleLinger, cancelled).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await EnterGateAsync(("linger-remove") + " " + entry.Hash).ConfigureAwait(false);
            try
            {
                if (cancelled.IsCancellationRequested)
                    return;
                if (ShouldKeep(entry))
                    ParkLocked(entry);
                else
                    await RemoveLockedAsync(entry).ConfigureAwait(false);
            }
            finally { LeaveGate(); }
        }

        /// Stops an unleased torrent that was never played and frees its cache memory (outside the engine gate, like
        /// ParkLocked). Waits for its start first, so a late StartAsync cannot restart it after the stop.
        private async Task StopUnplayedAsync(Entry entry, CancellationToken cancelled)
        {
            try
            {
                await Task.WhenAny(entry.Started, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                lock (entry)
                    if (entry.Leases > 0 || entry.Removing || cancelled.IsCancellationRequested)
                        return;
                if (entry.Manager.State != TorrentState.Stopped)
                    await entry.Manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                lock (entry)
                    if (entry.Leases > 0)
                        return;   // re-opened meanwhile: AcquireAsync restarts it and its cache refills
                var freed = entry.Cache?.AllocatedBytes ?? 0;
                entry.Cache?.Release();
                Log($"stopped unplayed {entry.Hash} for its linger" + (freed > 0 ? $", freed {freed / (1024 * 1024)} MB of cache" : ""));
            }
            catch (Exception error) { Log("stop unplayed: " + error.Message); }
        }

        /// The most recently played torrent is kept (stopped) when the cache can forget its pieces safely.
        private bool ShouldKeep(Entry entry) =>
            settings.KeepPlayedTorrent && !disposed && entry.Hash == Volatile.Read(ref keptHash) && entry.Manager.HasMetadata
            && (entry.Cache == null || RollingPieceWriter.BitfieldAccess.Available);

        /// Must hold engineGate. Stops the torrent in the background (closes its connections, sends the trackers'
        /// "stopped"), then releases its cached pieces and cache memory. It stays registered with its metadata and peer
        /// list; AcquireAsync restarts it. The engine keeps running (AnchorEngineAsync).
        private void ParkLocked(Entry entry)
        {
            lock (entry)
            {
                if (entry.Leases > 0 || entry.Removing || entry.Parked)
                    return;
                entry.Parked = true;
            }
            entry.Parking = Task.Run(async () =>
            {
                try { await entry.Manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception error) { Log("park stop: " + error.Message); }
                entry.Cache?.Release();
                Log($"kept {entry.Hash} stopped for a quick replay: {entry.Manager.Peers.Available} known peers, engine {(EngineRunning ? "running" : "stopped")}");
            });
        }

        /// A newly played torrent replaces the kept one; the old one is removed once it is idle.
        private async Task DropKeptAsync(string hash)
        {
            Entry entry;
            lock (entries)
                entries.TryGetValue(hash, out entry);
            if (entry == null || disposed)
                return;
            await entry.Parking.ConfigureAwait(false);
            await EnterGateAsync("drop-kept " + hash).ConfigureAwait(false);
            try
            {
                if (entry.Parked && hash != Volatile.Read(ref keptHash))
                    await RemoveLockedAsync(entry).ConfigureAwait(false);
            }
            finally { LeaveGate(); }
        }

        /// Must hold engineGate.
        /// Called under engineGate, but only marks the entry: the slow teardown (tracker "stopped" announces, engine
        /// removal, deleting data) runs in the background so it never blocks the next source from starting. On the TV
        /// a serial 10 s stop per raced-and-lost torrent held the gate for ~60 s before a new play could add its sources.
        /// Re-adding the same hash waits for entry.Removal (AcquireAsync).
        private Task RemoveLockedAsync(Entry entry)
        {
            lock (entry)
            {
                if (entry.Leases > 0 || entry.Removing)
                    return Task.CompletedTask;
                entry.Removing = true;
            }
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Removal = done.Task;
            entry.Parked = false;
            writer?.Unregister(entry.Cache);   // frees the RAM cache at once
            _ = Task.Run(async () =>
            {
                try
                {
                    try { await entry.Manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception error) { Log("stop: " + error.Message); }
                    // Downloaded data only: the cached metadata stays for the next play of this torrent.
                    try { await engine.RemoveAsync(entry.Manager, RemoveMode.DownloadedDataOnly).ConfigureAwait(false); }
                    catch (Exception error) { Log("remove: " + error.Message); }
                    DeleteDirectory(entry.Directory);
                    Log($"removed {entry.Hash}");
                }
                finally
                {
                    bool last;
                    lock (entries)
                    {
                        if (entries.TryGetValue(entry.Hash, out var registered) && registered == entry)
                            entries.Remove(entry.Hash);
                        last = entries.Count == 0;
                    }
                    done.TrySetResult(true);
                    // MonoTorrent stops the engine (DHT, listeners, port mappings) with its last torrent. The anchor
                    // torrent prevents that; this restarts it if the anchor could not be added.
                    if (last && !disposed)
                        Warm();
                }
            });
            return Task.CompletedTask;
        }

        /// Total RAM held by every stream cache.
        public long CacheAllocatedBytes
        {
            get
            {
                List<Entry> all;
                lock (entries)
                    all = entries.Values.ToList();
                return all.Sum(e => e.Cache?.AllocatedBytes ?? 0);
            }
        }

        /// Frees the cache memory of idle (unleased, paused or stopped) torrents, oldest first, while all caches together
        /// hold more than AggregateCacheBytes. Their pieces are forgotten (downloaded again if re-opened); the torrents
        /// stay registered for their linger. Also called on low memory.
        public long ReclaimIdleMemory(string why)
        {
            try
            {
                var budget = settings.AggregateCacheBytes ?? (settings.StreamCacheBytes ?? 0) + 32L * 1024 * 1024;
                List<Entry> all;
                lock (entries)
                    all = entries.Values.ToList();
                var total = all.Sum(e => e.Cache?.AllocatedBytes ?? 0);
                long freed = 0;
                var idle = all.Where(e =>
                {
                    lock (e)
                        return e.Leases <= 0 && !e.Removing && e.Cache != null
                            && (e.Manager.State == TorrentState.Paused || e.Manager.State == TorrentState.Stopped);
                }).OrderBy(e => e.LastUsed);
                foreach (var entry in idle)
                {
                    if (total - freed <= budget)
                        break;
                    var bytes = entry.Cache.AllocatedBytes;
                    if (bytes == 0)
                        continue;
                    entry.Cache.Release();
                    freed += bytes;
                    Log($"cache memory: released {bytes / (1024 * 1024)} MB of idle {entry.Hash} ({why}); caches now {(total - freed) / (1024 * 1024)} MB");
                }
                if (freed > 0)
                    PlaybackTimeline.Shared.Add("memory", $"reclaimed {freed / (1024 * 1024)} MB idle cache ({why}); caches {(total - freed) / (1024 * 1024)} MB");
                return freed;
            }
            catch (Exception error)
            {
                Log("reclaim: " + error.Message);
                return 0;
            }
        }

        /// Must hold engineGate. Removes idle torrents, oldest first, while idle data exceeds the budget.
        private async Task EvictIdleAsync()
        {
            List<Entry> idle;
            lock (entries)
                idle = entries.Values.Where(e => { lock (e) return e.Leases <= 0 && !e.Removing; }).OrderBy(e => e.LastUsed).ToList();
            // The RAM cache is not on disk: count its allocated slots too.
            long SizeOf(Entry e) => DirectorySize(e.Directory) + (e.Cache?.AllocatedBytes ?? 0);
            var total = idle.Sum(SizeOf);
            foreach (var entry in idle)
            {
                if (total <= settings.IdleCacheBudgetBytes)
                    break;
                var size = SizeOf(entry);
                lock (entry)
                {
                    entry.Linger?.Cancel();
                    entry.Linger = null;
                }
                await RemoveLockedAsync(entry).ConfigureAwait(false);
                total -= size;
            }
        }

        private async Task ApplyPrioritiesAsync(Entry entry)
        {
            Dictionary<string, int> wanted;
            lock (entry)
                wanted = new Dictionary<string, int>(entry.Wanted);
            foreach (var file in entry.Manager.Files)
            {
                var priority = wanted.ContainsKey(file.Path) ? Priority.Normal : Priority.DoNotDownload;
                if (file.Priority != priority)
                    await entry.Manager.SetFilePriorityAsync(file, priority).ConfigureAwait(false);
            }
        }

        private static void Unwant(Entry entry, string path)
        {
            if (entry.Wanted.TryGetValue(path, out var count))
            {
                if (count <= 1)
                    entry.Wanted.Remove(path);
                else
                    entry.Wanted[path] = count - 1;
            }
        }

        private void Closed(OpenedTorrent opened)
        {
            var entry = opened.Entry;
            // The last opened source to close is the one that was played (race losers close as soon as the race ends).
            var previous = Interlocked.Exchange(ref keptHash, entry.Hash);
            if (previous != null && previous != entry.Hash && !disposed)
                _ = DropKeptAsync(previous);
            lock (entry)
            {
                if (entry.ActiveOwner == opened)
                    entry.ActiveOwner = null;
                Unwant(entry, opened.File.Path);
            }
            if (!disposed)
                _ = ApplyPrioritiesSafeAsync(entry);
            Release(entry);
        }

        private async Task ApplyPrioritiesSafeAsync(Entry entry)
        {
            try
            {
                lock (entry)
                    if (entry.Removing || entry.Leases <= 0)
                        return;
                await ApplyPrioritiesAsync(entry).ConfigureAwait(false);
            }
            catch (Exception error) { Log("priorities: " + error.Message); }
        }

        // ---------------- engine ----------------

        private int warming;

        /// Starts the engine in the background: listeners, UPnP mapping and the DHT bootstrap (router DNS and the first
        /// node replies take seconds on the TV), so the first play does not pay for them. Idempotent and safe to call
        /// from the UI thread: it returns at once. Called at app launch and again whenever the engine stopped because
        /// its last torrent was removed.
        public void Warm()
        {
            if (disposed || Interlocked.Exchange(ref warming, 1) == 1)
                return;
            _ = Task.Run(async () =>
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    ClientEngine current;
                    await EnterGateAsync("warm", lifetime.Token).ConfigureAwait(false);
                    try { current = EnsureEngine(); }
                    finally { LeaveGate(); }
                    var built = timer.ElapsedMilliseconds;
                    var started = await EngineStart.RunAsync(current, Log).ConfigureAwait(false);
                    Log($"warm: engine {(started ? "started" : "not started (internal start missing)")} in {timer.ElapsedMilliseconds} ms (build {built} ms) dht={current.Dht.State}/{current.Dht.NodeCount}");
                    await AnchorEngineAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { Log("warm: " + error.GetType().Name + " " + error.Message); }
                finally { Interlocked.Exchange(ref warming, 0); }
            });
        }

        /// MonoTorrent stops its engine (listeners, UPnP / NAT-PMP mappings, DHT) whenever every torrent is stopped, so
        /// removing the last torrent used to cost the next play a cold engine: no DHT routing table, a port re-mapping and
        /// an empty peer list. The anchor is a tiny private torrent (one 16 KiB file nobody has, no trackers, DHT or
        /// peer exchange, nothing downloaded) held paused for the app's lifetime: a paused torrent counts as running, so
        /// the engine stays up between plays and only real torrents are removed.
        private async Task AnchorEngineAsync()
        {
            if (anchor != null || disposed || engine == null || Interlocked.Exchange(ref anchoring, 1) == 1)
                return;
            try
            {
                var info = new MonoTorrent.BEncoding.BEncodedDictionary
                {
                    { "name", (MonoTorrent.BEncoding.BEncodedString)"ru7100-engine-anchor" },
                    { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(16 * 1024) },
                    { "length", new MonoTorrent.BEncoding.BEncodedNumber(16 * 1024) },
                    { "pieces", new MonoTorrent.BEncoding.BEncodedString(Guid.NewGuid().ToByteArray().Concat(new byte[4]).ToArray()) },
                    { "private", new MonoTorrent.BEncoding.BEncodedNumber(1) },
                };
                var torrent = Torrent.Load(new MonoTorrent.BEncoding.BEncodedDictionary { { "info", info } });
                var anchorSettings = new TorrentSettingsBuilder { AllowDht = false, AllowPeerExchange = false, AllowInitialSeeding = false, MaximumConnections = 1 }.ToSettings();
                var manager = await engine.AddAsync(torrent, Path.Combine(CacheDirectory, "anchor"), anchorSettings).ConfigureAwait(false);
                foreach (var file in manager.Files)
                    await manager.SetFilePriorityAsync(file, Priority.DoNotDownload).ConfigureAwait(false);
                await manager.StartAsync().ConfigureAwait(false);
                for (var i = 0; i < 100 && manager.State != TorrentState.Downloading; i++)
                    await Task.Delay(50).ConfigureAwait(false);
                await manager.PauseAsync().ConfigureAwait(false);
                anchor = manager;
                Log($"anchor: engine held running ({manager.State})");
            }
            catch (Exception error)
            {
                Log("anchor: " + error.GetType().Name + " " + error.Message);
                Interlocked.Exchange(ref anchoring, 0);
            }
        }

        private ClientEngine EnsureEngine()
        {
            if (engine != null)
                return engine;
            Directory.CreateDirectory(CacheDirectory);
            ports = ListenPorts.Choose(settings.ListenPorts, Log);
            var builder = new EngineSettingsBuilder
            {
                CacheDirectory = CacheDirectory,
                AllowPortForwarding = settings.AllowPortForwarding,
                ListenEndPoints = new Dictionary<string, IPEndPoint>
                {
                    { "ipv4", new IPEndPoint(IPAddress.Any, ports.Tcp4) },
                    { "ipv6", new IPEndPoint(IPAddress.IPv6Any, ports.Tcp6) },
                },
                DhtEndPoint = new IPEndPoint(IPAddress.Any, ports.Udp),
                AllowLocalPeerDiscovery = false,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = true,   // bounded by PruneMetadata
                AutoSaveLoadDhtCache = true,             // a few KB; saved when the engine stops
                UsePartialFiles = true,
                WebSeedDelay = TimeSpan.FromDays(1),
                MaximumConnections = settings.MaximumConnections,
                MaximumHalfOpenConnections = settings.MaximumHalfOpenConnections,
                MaximumDownloadRate = settings.MaximumDownloadRate,
                MaximumUploadRate = settings.MaximumUploadRate,
                DiskCacheBytes = settings.DiskCacheBytes,
                ConnectionTimeout = settings.ConnectionTimeout,
                StaleRequestTimeout = settings.StaleRequestTimeout,
            };
            if (settings.AllowedEncryption?.Count > 0)
            {
                builder.AllowedEncryption.Clear();
                builder.AllowedEncryption.AddRange(settings.AllowedEncryption);
            }
            var defaultWriter = Factories.Default.CreatePieceWriter(16);
            writer = new RollingPieceWriter(defaultWriter, Log);
            if (!RollingPieceWriter.BitfieldAccess.Available)
                Log("rolling cache: MonoTorrent internals not found, evicted pieces cannot be re-fetched");
            if (settings.EnableUtp)
            {
                try
                {
                    utp = new Utp.UtpTransport(new IPEndPoint(IPAddress.Any, ports.Udp), connections,
                        new Utp.UtpOptions { ReceiveBufferBytes = settings.UtpReceiveBufferBytes }) { TcpGrace = settings.UtpTcpGrace };
                    Log($"utp: on udp {utp.LocalEndPoint.Port} (shared with the dht)");
                }
                catch (System.Net.Sockets.SocketException error)
                {
                    Log("utp: off, udp port unavailable: " + error.SocketErrorCode);
                    utp = null;
                }
            }
            var transport = utp;
            var factories = Factories.Default
                .WithPieceWriterCreator(maxOpenFiles => writer)
                .WithSocketConnectorCreator(() => new CountingSocketConnector(new CancellableSocketConnector(), connections))
                .WithPeerConnectionCreator("ipv4", uri => transport != null
                    ? new Utp.RacingPeerConnection(uri, transport, connections)
                    : (IPeerConnection)new Utp.MeteredPeerConnection(new SocketPeerConnection(uri, new CountingSocketConnector(new CancellableSocketConnector(), connections)), false, connections))
                .WithPeerConnectionCreator("ipv6", uri => new SocketPeerConnection(uri, new CountingSocketConnector(new CancellableSocketConnector(), connections)))
                .WithPeerConnectionListenerCreator(endPoint => endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? new Utp.DualPeerConnectionListener(connections.CreateListener(endPoint), transport, connections)
                    : (IPeerConnectionListener)connections.CreateListener(endPoint))
                .WithStreamingPieceRequesterCreator(() => new EdgeStreamingPieceRequester { FullPipeline = settings.StreamFullPipeline })
                .WithPortForwarderCreator(() => forwarder = new SafePortForwarder(() => Factories.Default.CreatePortForwarder(), Log));
            if (transport != null)
                factories = factories.WithDhtListenerCreator(endPoint => transport.CreateDhtListener());
            if (settings.DhtRouters?.Count > 0)
                factories = factories.WithDhtCreator(() => dht = new BootstrappedDht(new MonoTorrent.Dht.DhtEngine(), settings.DhtRouters, Log));
            engine = new ClientEngine(builder.ToSettings(), factories);
            Log($"port: listening on {ports}, port forwarding {(settings.AllowPortForwarding ? "on" : "off")}");
            return engine;
        }

        /// Announce URLs for a candidate: its own trackers plus the defaults (more trackers, more peers; they are
        /// announced in parallel, so extra ones cost nothing at start).
        internal static IReadOnlyList<string> TrackersFor(IReadOnlyList<string> trackers) =>
            (trackers ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.StartsWith("tracker:", StringComparison.Ordinal) ? value.Substring("tracker:".Length) : value)
                .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
                    && (uri.Scheme == "udp" || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                .Concat(AddonStream.DefaultTrackers)
                .Distinct(StringComparer.Ordinal).ToList();

        /// A tracker-less magnet: trackers are added one per tier after AddStreamingAsync (see CreateEntryAsync).
        internal static MagnetLink Magnet(string hash) => MagnetLink.Parse("magnet:?xt=urn:btih:" + hash);

        internal static ITorrentManagerFile SelectVideo(IList<ITorrentManagerFile> files, int? fileIndex)
        {
            if (fileIndex.HasValue)
            {
                if (fileIndex.Value >= files.Count || !IsVideo(files[fileIndex.Value].Path, files[fileIndex.Value].Length))
                    throw new InvalidDataException("The add-on's torrent file index does not identify a supported video.");
                return files[fileIndex.Value];
            }
            return files.Where(file => IsVideo(file.Path, file.Length)).OrderByDescending(file => file.Length).FirstOrDefault()
                ?? throw new InvalidDataException("The torrent contains no supported video file.");
        }

        internal static bool IsVideo(string path, long length) =>
            length > 0 && VideoExtensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

        // ---------------- storage ----------------

        private string MetadataDirectory => Path.Combine(CacheDirectory, "metadata");
        private string DhtCachePath => Path.Combine(CacheDirectory, "dht_nodes.cache");

        private void SweepRoot()
        {
            // Downloaded data never survives an app restart: fast resume is off, so it is unusable and only costs storage.
            // Kept: torrent metadata (bounded) and the DHT node cache (a few KB), which make the next start warm.
            DeleteDirectory(TorrentsDirectory);
            try
            {
                if (Directory.Exists(CacheDirectory))
                {
                    foreach (var dir in Directory.GetDirectories(CacheDirectory))
                        if (!string.Equals(dir, MetadataDirectory, StringComparison.Ordinal))
                            DeleteDirectory(dir);
                    foreach (var file in Directory.GetFiles(CacheDirectory))
                        if (!string.Equals(file, DhtCachePath, StringComparison.Ordinal) || new FileInfo(file).Length > 64 * 1024)
                            File.Delete(file);
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Log("sweep cache: " + error.Message); }
            PruneMetadata();
            Directory.CreateDirectory(TorrentsDirectory);
        }

        /// Keeps the newest MetadataCacheFiles .torrent files within MetadataCacheBytes.
        private void PruneMetadata()
        {
            try
            {
                if (!Directory.Exists(MetadataDirectory))
                    return;
                var files = new DirectoryInfo(MetadataDirectory).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).ToList();
                long total = 0;
                var kept = 0;
                var removed = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (file.Extension != ".torrent" || kept >= settings.MetadataCacheFiles || total > settings.MetadataCacheBytes)
                    {
                        file.Delete();
                        removed++;
                    }
                    else
                        kept++;
                }
                Log($"metadata cache: {kept} torrents kept{(removed > 0 ? ", " + removed + " removed" : "")}");
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { Log("metadata cache: " + error.Message); }
        }

        private void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Log("delete " + path + ": " + error.Message);
            }
        }

        /// Bytes for a new torrent's rolling cache: StreamCacheBytes, shrunk to the free space left after the reserve.
        /// 0 = rolling cache disabled. Throws when even the minimum does not fit.
        private long CacheCapacityFor(string hash)
        {
            if (settings.StreamCacheBytes == null)
                return 0;
            if (settings.StreamCacheInMemory)
            {
                Log($"cache for {hash}: {settings.StreamCacheBytes.Value / (1024 * 1024)} MB in memory");
                return settings.StreamCacheBytes.Value;
            }
            var free = FreeBytes(settings.RootDirectory);
            var capacity = settings.StreamCacheBytes.Value;
            if (free >= 0)
                capacity = Math.Min(capacity, free - settings.FreeSpaceReserveBytes);
            if (capacity < settings.MinimumCacheBytes)
                throw new IOException($"Not enough free storage on this device to stream ({Math.Max(0, free) / (1024 * 1024)} MB free, " +
                    $"{(settings.MinimumCacheBytes + settings.FreeSpaceReserveBytes) / (1024 * 1024)} MB needed).");
            Log($"cache for {hash}: {capacity / (1024 * 1024)} MB (free {(free < 0 ? "unknown" : (free / (1024 * 1024)) + " MB")})");
            return capacity;
        }

        /// Free bytes on the volume holding <paramref name="path"/>, or -1 when unknown.
        internal static long FreeBytes(string path)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                var best = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && Path.GetFullPath(path).StartsWith(d.RootDirectory.FullName, StringComparison.Ordinal))
                    .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
                return (best ?? new DriveInfo(root)).AvailableFreeSpace;
            }
            catch (Exception) { return -1; }
        }

        private static long DirectorySize(string path)
        {
            try
            {
                return Directory.Exists(path)
                    ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                    : 0;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { return 0; }
        }

        private void Log(string message) => settings.Log?.Invoke("torrent: " + message);

        private string DescribeCache(TorrentManager manager)
        {
            Entry entry;
            lock (entries)
                entry = entries.Values.FirstOrDefault(e => e.Manager == manager);
            var cache = entry?.Cache;
            if (cache == null)
                return "off";
            var (stored, ahead, slots) = cache.Occupancy();
            return $"{stored}/{slots} slots ahead={ahead}/{cache.AheadSlots} evicted={cache.Evictions} refetch={cache.Refetches} head={cache.ReadPosition}";
        }

        /// Tracker states and peer counts, so a stalled start on the TV can be diagnosed over the API.
        private string DescribeSwarm(TorrentManager manager)
        {
            try
            {
                var trackers = string.Join(", ", manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers)
                    .Select(tracker => tracker.Uri.Host + ":" + tracker.Status +
                        (string.IsNullOrEmpty(tracker.FailureMessage) ? "" : "(" + tracker.FailureMessage + ")")));
                var dht = engine == null ? "none" : engine.Dht.State + "/" + engine.Dht.NodeCount;
                return $"state={manager.State} available={manager.Peers.Available} open={manager.OpenConnections} dht={dht} cache={DescribeCache(manager)} {NetworkSummary} conn={connections.Describe()} trackers=[{trackers}]";
            }
            catch (Exception error) { return "swarm unavailable: " + error.Message; }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            lifetime.Cancel();
            List<Entry> all;
            lock (entries)
                all = entries.Values.ToList();
            foreach (var entry in all)
                entry.ActiveOwner?.Preempt();
            try
            {
                SaveDhtNodes();   // MonoTorrent saves them only when a torrent stops the engine
                engine?.StopAllAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                engine?.Dispose();
                utp?.Dispose();
            }
            catch (Exception error) { Log("dispose: " + error.Message); }
            SweepRoot();
        }

        private void SaveDhtNodes()
        {
            try
            {
                var save = dht?.SaveNodesAsync();
                if (save == null || !save.Wait(TimeSpan.FromSeconds(1)))
                    return;
                var nodes = save.Result;
                if (nodes.Length > 0 && nodes.Length <= 64 * 1024)
                {
                    Directory.CreateDirectory(CacheDirectory);
                    File.WriteAllBytes(DhtCachePath, nodes.ToArray());
                }
            }
            catch (Exception error) { Log("dht cache: " + error.Message); }
        }

        // ---------------- opened sources ----------------

        private sealed class OpenedTorrent : IOpenedSource, IPreemptibleSource, IPlayheadSource, IFileEnds
        {
            private readonly TorrentSourceOpener owner;
            private readonly TorrentMediaServer server;
            private readonly Stream stream;
            private int closed;
            private int stopped;

            private readonly CancellationTokenSource monitor = new CancellationTokenSource();
            private bool throttled;

            public OpenedTorrent(TorrentSourceOpener owner, Entry entry, ITorrentManagerFile file, Stream stream, TorrentMediaServer server)
            {
                this.owner = owner;
                Entry = entry;
                File = file;
                this.stream = stream;
                this.server = server;
                if (entry.Cache != null)
                    _ = MonitorAsync(monitor.Token);
            }

            /// Once a second: tell the cache where the player is reading, and stop downloading while the buffer ahead
            /// is full (paused playback must not keep filling storage), resuming once the playhead has moved on.
            private async Task MonitorAsync(CancellationToken token)
            {
                var cache = Entry.Cache;
                var ticks = 0;
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    try
                    {
                        if (!throttled)
                            owner.net.Sample(Entry.Manager.Monitor.DownloadRate);
                        cache.ReadPosition = File.OffsetInTorrent + stream.Position;
                        var (_, ahead, _) = cache.Occupancy();
                        var limit = cache.AheadSlots;
                        var playhead = Playhead();
                        long pieceLength = Entry.Manager.Torrent?.PieceLength ?? 0;
                        var want = CacheThrottle.Decide(throttled, ahead, limit, playhead.Contiguous, pieceLength);
                        if (want != throttled)
                            await SetThrottleAsync(want, playhead).ConfigureAwait(false);
                        // The aggregate RAM cap, checked while this cache fills (at linger start it is still nearly empty).
                        if (++ticks % 5 == 0)
                            owner.ReclaimIdleMemory("playing " + Entry.Hash);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception error) { owner.Log("cache monitor: " + error.Message); }
                }
                if (throttled)
                {
                    try { await SetThrottleAsync(false).ConfigureAwait(false); } catch (Exception) { }
                }
            }

            private async Task SetThrottleAsync(bool on, (int FirstMissing, long Contiguous)? playhead = null)
            {
                var manager = Entry.Manager;
                var builder = new TorrentSettingsBuilder(manager.Settings) { MaximumDownloadRate = on ? 1024 : 0 };
                await manager.UpdateSettingsAsync(builder.ToSettings()).ConfigureAwait(false);
                throttled = on;
                var where = playhead.HasValue ? $" (contiguous {playhead.Value.Contiguous / 1024} KiB, first missing piece {playhead.Value.FirstMissing})" : "";
                owner.Log((on ? "buffer full, download paused" : "buffer draining, download resumed") + " for " + Entry.Hash + where);
                PlaybackTimeline.Shared.Add("throttle", (on ? "on " : "off ") + Entry.Hash + where);
            }

            /// A range started (media server): move the cache's playhead now, and lift the throttle at once when the
            /// range starts on a piece that is not there.
            public void OnRange(long position, bool cached)
            {
                var cache = Entry.Cache;
                if (cache != null)
                    cache.ReadPosition = File.OffsetInTorrent + position;
                if (!cached && throttled && !Preempted)
                    _ = Task.Run(async () =>
                    {
                        try { await SetThrottleAsync(false).ConfigureAwait(false); }
                        catch (Exception error) { owner.Log("throttle: " + error.Message); }
                    });
            }

            public Entry Entry { get; }
            public ITorrentManagerFile File { get; }
            public string PlayerUrl => server.Url;
            public long LengthBytes => File.Length;
            public double DownloadBytesPerSecond
            {
                get { try { return Entry.Manager.Monitor.DownloadRate; } catch (Exception) { return 0; } }
            }
            public int Peers
            {
                get { try { return Entry.Manager.OpenConnections; } catch (Exception) { return 0; } }
            }
            public bool Preempted => Volatile.Read(ref stopped) == 1;

            /// The file's first and last bytes straight from the RAM cache (pinned pieces), for the OpenSubtitles hash.
            /// Never touches the media server's stream, so the player's reads and the cache window are undisturbed.
            public bool TryReadEnds(int count, out byte[] head, out byte[] tail)
            {
                head = tail = null;
                var cache = Entry.Cache;
                var manager = Entry.Manager;
                if (cache == null || manager?.Torrent == null || File.Length < count || Volatile.Read(ref closed) == 1)
                    return false;
                var torrent = manager.Torrent;
                var bits = manager.Bitfield;
                bool Verified(long from, long to)
                {
                    for (var piece = torrent.ByteOffsetToPieceIndex(File.OffsetInTorrent + from); piece <= torrent.ByteOffsetToPieceIndex(File.OffsetInTorrent + to - 1); piece++)
                        if (!bits[piece]) return false;
                    return true;
                }
                if (!Verified(0, count) || !Verified(File.Length - count, File.Length))
                    return false;
                var first = new byte[count];
                var last = new byte[count];
                if (cache.Read(File, 0, first) != count || cache.Read(File, File.Length - count, last) != count)
                    return false;
                head = first;
                tail = last;
                return true;
            }

            /// Verified bytes in a row from the player's read position (the media server's stream cursor), up to 1024
            /// pieces; 0 when the piece under the playhead is missing, -1 when unknown.
            public long ContiguousBytesAhead => Playhead().Contiguous;
            public int FirstMissingPiece => Playhead().FirstMissing;
            public long ReadyThroughBytes
            {
                get
                {
                    try
                    {
                        var position = stream.Position;
                        var contiguous = Playhead().Contiguous;
                        return contiguous < 0 ? -1 : position + contiguous;
                    }
                    catch (Exception) { return -1; }
                }
            }

            /// The first missing piece at or after the read position (-1 none or unknown) and the contiguous bytes ahead.
            public (int FirstMissing, long Contiguous) Playhead()
            {
                try
                {
                    if (Preempted)
                        return (-1, -1);
                    var manager = Entry.Manager;
                    long pieceLength = manager.Torrent.PieceLength;
                    var absolute = File.OffsetInTorrent + stream.Position;
                    var fileEnd = File.OffsetInTorrent + File.Length;
                    var piece = (int)(absolute / pieceLength);
                    var last = (int)((fileEnd - 1) / pieceLength);
                    var bitfield = manager.Bitfield;
                    var scanned = 0;
                    while (piece <= last && scanned < 1024 && bitfield[piece])
                    {
                        piece++;
                        scanned++;
                    }
                    var readyEnd = Math.Min(fileEnd, piece * pieceLength);
                    return (piece <= last && scanned < 1024 ? piece : -1, Math.Max(0, readyEnd - absolute));
                }
                catch (Exception) { return (-1, -1); }
            }

            /// Stops serving and frees the torrent's single stream slot; the lease stays until Dispose.
            public void Preempt()
            {
                if (Interlocked.Exchange(ref stopped, 1) == 1)
                    return;
                try { monitor.Cancel(); } catch (Exception) { }
                try { server.Dispose(); } catch (Exception) { }
                try { stream.Dispose(); } catch (Exception) { }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref closed, 1) == 1)
                    return;
                Preempt();
                owner.Closed(this);
            }
        }

        private sealed class DirectSource : IOpenedSource
        {
            public DirectSource(string url, long length) { PlayerUrl = url; LengthBytes = length; }
            public string PlayerUrl { get; }
            public long LengthBytes { get; }
            public double DownloadBytesPerSecond => 0;
            public int Peers => 0;
            public void Dispose() { }
        }
    }

    /// When a playing torrent's download is paused because its RAM buffer is full. Counted on contiguous verified bytes
    /// at the read cursor, not on allocated slots: sparse slots (partial pieces, far tail pins) used to keep the 1 KiB/s
    /// cap on while the piece the player needed was missing.
    internal static class CacheThrottle
    {
        /// throttled: current state. ahead/aheadSlots: stored pieces at or after the playhead and the slots available.
        /// contiguous: verified bytes in a row from the cursor (-1 unknown). pieceLength: 0 unknown.
        public static bool Decide(bool throttled, int ahead, int aheadSlots, long contiguous, long pieceLength)
        {
            if (contiguous < 0 || pieceLength <= 0)
                return throttled ? ahead >= aheadSlots * 0.85 : ahead >= aheadSlots;   // unknown: the old rule
            var full = (long)aheadSlots * pieceLength;
            if (contiguous == 0)
                return false;                                    // a hole at the cursor: always download
            if (!throttled)
                return ahead >= aheadSlots && contiguous >= full * 0.75;
            return contiguous >= full * 0.5 && ahead >= aheadSlots * 0.85;
        }
    }

    /// MonoTorrent 3.0.2 starts its engine only from inside a torrent's start (ClientEngine.StartAsync is internal).
    /// Invoked by reflection on the engine's main loop, as MonoTorrent itself does; it is a no-op when already running.
    internal static class EngineStart
    {
        private const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        private static readonly System.Reflection.MethodInfo Start = typeof(ClientEngine).GetMethod("StartAsync", Any, null, Type.EmptyTypes, null);
        private static readonly System.Reflection.FieldInfo MainLoop = typeof(ClientEngine).GetField("MainLoop", Any);

        public static bool Available => Start != null && MainLoop != null && typeof(Task).IsAssignableFrom(Start.ReturnType);

        public static async Task<bool> RunAsync(ClientEngine engine, Action<string> log)
        {
            if (!Available || engine.IsRunning)
                return Available;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!(MainLoop.GetValue(null) is SynchronizationContext loop))
                return false;
            loop.Post(async _ =>
            {
                try
                {
                    if (!engine.IsRunning && !engine.Disposed)
                        await ((Task)Start.Invoke(engine, null)).ConfigureAwait(true);
                    done.TrySetResult(true);
                }
                catch (Exception error)
                {
                    log?.Invoke("warm start: " + (error.InnerException ?? error).Message);
                    done.TrySetResult(false);
                }
            }, null);
            return await done.Task.ConfigureAwait(false);
        }
    }

    internal static class TaskCompat
    {
        public static async Task WithCancellationCompat(this Task task, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                {
                    _ = task.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    token.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
