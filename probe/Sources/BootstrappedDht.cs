using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;

namespace Ru7100Probe
{
    /// Wraps MonoTorrent's DHT so it starts from extra bootstrap routers. Without this the DHT stays
    /// Initialising/0 nodes: its built-in router.bittorrent.com does not answer from this network.
    /// The routers are given as compact nodes (random ids, port 6881); their replies carry real ids.
    internal sealed class BootstrappedDht : IDhtEngine
    {
        private readonly IDhtEngine inner;
        private readonly IReadOnlyList<string> routers;
        private readonly Action<string> log;

        public BootstrappedDht(IDhtEngine inner, IReadOnlyList<string> routers, Action<string> log)
        {
            this.inner = inner;
            this.routers = routers;
            this.log = log;
        }

        public TimeSpan AnnounceInterval => inner.AnnounceInterval;
        public bool Disposed => inner.Disposed;
        public ITransferMonitor Monitor => inner.Monitor;
        public TimeSpan MinimumAnnounceInterval => inner.MinimumAnnounceInterval;
        public int NodeCount => inner.NodeCount;
        public DhtState State => inner.State;

        public event EventHandler<PeersFoundEventArgs> PeersFound
        {
            add => inner.PeersFound += value;
            remove => inner.PeersFound -= value;
        }

        public event EventHandler StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) => inner.Add(nodes);
        public void Announce(InfoHash infoHash, int port) => inner.Announce(infoHash, port);
        public void GetPeers(InfoHash infoHash) => inner.GetPeers(infoHash);
        public Task<ReadOnlyMemory<byte>> SaveNodesAsync() => inner.SaveNodesAsync();
        public Task SetListenerAsync(IDhtListener listener) => inner.SetListenerAsync(listener);
        public Task StartAsync() => StartAsync(ReadOnlyMemory<byte>.Empty);

        public async Task StartAsync(ReadOnlyMemory<byte> initialNodes)
        {
            var nodes = new List<byte>(initialNodes.ToArray());
            var random = new Random();
            // Resolved in parallel: one slow or dead resolver answer no longer delays the others (the engine start,
            // and with it the first torrent add on the TV, used to wait for all three in turn).
            var lookups = routers.Select(async host =>
            {
                try { return await Dns.GetHostAddressesAsync(host).ConfigureAwait(false); }
                catch (Exception error) when (error is SocketException || error is ArgumentException)
                {
                    log?.Invoke("dht router " + host + ": " + error.Message);
                    return Array.Empty<IPAddress>();
                }
            }).ToList();
            foreach (var addresses in await Task.WhenAll(lookups).ConfigureAwait(false))
            {
                foreach (var address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(4))
                {
                    var id = new byte[20];
                    random.NextBytes(id);
                    nodes.AddRange(id);
                    nodes.AddRange(address.GetAddressBytes());
                    nodes.Add(6881 >> 8);
                    nodes.Add(6881 & 0xFF);
                }
            }
            await inner.StartAsync(nodes.ToArray()).ConfigureAwait(false);
        }

        public Task StopAsync() => inner.StopAsync();
        public void Dispose() => inner.Dispose();
    }
}
