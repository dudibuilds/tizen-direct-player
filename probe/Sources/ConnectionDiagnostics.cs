using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace Ru7100Probe
{
    /// Counts outgoing peer connection attempts and why they fail, incoming connections and how many became peers, plus
    /// the listen socket's bind result, so a TV that never opens a peer connection can be diagnosed over the API.
    /// MonoTorrent reports failures (ConnectionAttemptFailed) for outgoing connections only; an incoming connection it
    /// rejects (unknown or stopped torrent, failed handshake, connection limit) is closed silently, so incoming shows as
    /// received vs accepted. "retried" counts outgoing peers that failed the encryption or handshake step and then
    /// connected on MonoTorrent's immediate retry with the other encryption, keyed by the encryption that worked.
    internal sealed class ConnectionDiagnostics
    {
        private readonly ConcurrentDictionary<string, int> failures = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentDictionary<string, string> listeners = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<Uri, ConnectionFailureReason> negotiationFailed = new ConcurrentDictionary<Uri, ConnectionFailureReason>();
        private readonly ConcurrentDictionary<string, int> retried = new ConcurrentDictionary<string, int>();
        private int attempts;
        private int attemptsV6;
        private int connected;
        private int handshakes;
        private int incoming;
        private int incomingAccepted;
        // Addresses that connected in, and outgoing handshake failures by address: an overlap means the peer already had
        // a connection from/to us and dropped the duplicate (BitTorrent clients keep one connection per peer).
        private readonly ConcurrentDictionary<string, byte> incomingFrom = new ConcurrentDictionary<string, byte>();
        private readonly ConcurrentDictionary<string, byte> handshakeFailedTo = new ConcurrentDictionary<string, byte>();
        private int utpAttempts;
        private int utpConnected;
        private int utpIncoming;
        private int raceTcp;
        private int raceUtp;
        private int lostRace;
        private int tcpPreferred;
        private int utpLostRace;
        private int tcpPeers;
        private int utpPeers;
        private long tcpBytes;
        private long utpBytes;
        private string lastError = "";

        /// Per-peer sets above are bounded: the engine lives as long as the app on a TV with little RAM. A set that reaches
        /// this size is emptied and starts over (the overlap and retry counts then miss pairs split by the reset; the
        /// cumulative counters are unaffected). 512 entries is well above one start's peers (~100-300 attempts).
        internal const int MaximumTrackedPeers = 512;

        internal static void Remember<TKey, TValue>(ConcurrentDictionary<TKey, TValue> set, TKey key, TValue value)
        {
            if (set.Count >= MaximumTrackedPeers && !set.ContainsKey(key))
                set.Clear();
            set[key] = value;
        }

        public void Attempt(Uri uri)
        {
            Interlocked.Increment(ref attempts);
            if (uri.HostNameType == UriHostNameType.IPv6)
                Interlocked.Increment(ref attemptsV6);
        }

        public void SocketConnected() => Interlocked.Increment(ref connected);
        public void PeerConnected() => Interlocked.Increment(ref handshakes);

        public void Failure(string reason, Exception error = null)
        {
            failures.AddOrUpdate(reason, 1, (_, count) => count + 1);
            if (error != null)
                lastError = reason + ": " + error.GetType().Name + " " + error.Message;
        }

        public void Listener(string name, string state) => listeners[name] = state;

        public void UtpAttempt() => Interlocked.Increment(ref utpAttempts);
        public void UtpConnected() => Interlocked.Increment(ref utpConnected);
        public void UtpIncoming() => Interlocked.Increment(ref utpIncoming);
        public void LostRace() => Interlocked.Increment(ref lostRace);
        public void TcpPreferred() => Interlocked.Increment(ref tcpPreferred);
        public void UtpLostRace() => Interlocked.Increment(ref utpLostRace);
        public void RaceWon(bool utp) => Interlocked.Increment(ref utp ? ref raceUtp : ref raceTcp);

        /// Bytes received on a peer connection; handshake marks its first 68 bytes (a BitTorrent handshake).
        public void Received(bool utp, int count, bool handshake)
        {
            Interlocked.Add(ref utp ? ref utpBytes : ref tcpBytes, count);
            if (handshake)
                Interlocked.Increment(ref utp ? ref utpPeers : ref tcpPeers);
        }

        /// Peers that completed a handshake, per transport (connections through the metered wrappers).
        public int TcpPeers => tcpPeers;
        public int UtpPeers => utpPeers;
        public long TcpBytes => Interlocked.Read(ref tcpBytes);
        public long UtpBytes => Interlocked.Read(ref utpBytes);
        public int UtpConnects => utpConnected;
        public int UtpIncomingCount => utpIncoming;

        public string Describe() =>
            $"attempts={attempts} v6={attemptsV6} tcp={connected} in={incoming} inPeers={incomingAccepted} utp={utpConnected}/{utpAttempts} utpIn={utpIncoming} " +
            $"won=tcp:{raceTcp}/utp:{raceUtp} tcp-grace={tcpPreferred} lost-race=tcp:{lostRace}/utp:{utpLostRace} peers={handshakes} hs=tcp:{tcpPeers}/utp:{utpPeers} " +
            $"MB=tcp:{TcpBytes / 1e6:0.0}/utp:{UtpBytes / 1e6:0.0} hsFailedAlsoIncoming={HandshakeFailedAlsoIncoming()} failures=[" +
            string.Join(", ", failures.OrderByDescending(p => p.Value).Select(p => p.Key + ":" + p.Value)) + "] retried=[" +
            string.Join(", ", retried.OrderByDescending(p => p.Value).Select(p => p.Key + ":" + p.Value)) + "] listen=[" +
            string.Join(", ", listeners.Select(p => p.Key + ":" + p.Value)) + "]" +
            (lastError.Length > 0 ? " last=" + lastError : "") + " " + Threads();

        private int HandshakeFailedAlsoIncoming() => handshakeFailedTo.Keys.Count(incomingFrom.ContainsKey);

        private static string Threads()
        {
            ThreadPool.GetAvailableThreads(out var worker, out var io);
            ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
            ThreadPool.GetMinThreads(out var minWorker, out var minIo);
            return $"pool=busy {maxWorker - worker}/min {minWorker} io {maxIo - io}/min {minIo} cpus={Environment.ProcessorCount}";
        }

        public void Watch(TorrentManager manager)
        {
            // When magnet metadata arrives MonoTorrent closes every connection of the torrent (TorrentManager.SetMetadata),
            // so handshakes still in flight then fail; they are counted apart as HandshakeFailed@metadata.
            var metadataAt = DateTime.MinValue;
            manager.TorrentStateChanged += (_, args) =>
            {
                if (args.OldState == TorrentState.Metadata)
                    metadataAt = DateTime.UtcNow;
            };
            manager.ConnectionAttemptFailed += (_, args) =>
            {
                var atMetadata = args.Reason == ConnectionFailureReason.HandshakeFailed && (DateTime.UtcNow - metadataAt).TotalSeconds < 1.5;
                Failure("peer:" + args.Reason + (atMetadata ? "@metadata" : ""));
                if (args.Reason == ConnectionFailureReason.HandshakeFailed || args.Reason == ConnectionFailureReason.EncryptionNegiotiationFailed)
                    Remember(negotiationFailed, args.Peer.ConnectionUri, args.Reason);
                if (args.Reason == ConnectionFailureReason.HandshakeFailed)
                    Remember(handshakeFailedTo, args.Peer.ConnectionUri.Host, (byte)0);
            };
            manager.PeerConnected += (_, args) =>
            {
                PeerConnected();
                if (args.Direction == Direction.Incoming)
                    Interlocked.Increment(ref incomingAccepted);
                else if (negotiationFailed.TryRemove(args.Peer.Uri, out var reason))
                    retried.AddOrUpdate(reason + "->" + args.Peer.EncryptionType, 1, (_, count) => count + 1);
            };
        }

        public PeerConnectionListener CreateListener(IPEndPoint endPoint)
        {
            var listener = new PeerConnectionListener(endPoint);
            var name = endPoint.ToString();
            Listener(name, "created");
            // Incoming TCP connections: proof that the fixed port / UPnP mapping makes us reachable.
            listener.ConnectionReceived += (_, args) =>
            {
                Interlocked.Increment(ref incoming);
                var host = args.Connection?.Uri?.Host;
                if (host != null)
                    Remember(incomingFrom, host, (byte)0);
            };
            listener.StatusChanged += (_, __) =>
                Listener(name, listener.Status + (listener.LocalEndPoint != null ? "@" + listener.LocalEndPoint.Port : ""));
            return listener;
        }
    }

    /// Wraps MonoTorrent's socket connector to record each outgoing TCP attempt and its outcome.
    internal sealed class CountingSocketConnector : ISocketConnector
    {
        private readonly ISocketConnector inner;
        private readonly ConnectionDiagnostics diagnostics;
        private readonly Utp.RaceFlag race;

        public CountingSocketConnector(ISocketConnector inner, ConnectionDiagnostics diagnostics, Utp.RaceFlag race = null)
        {
            this.inner = inner;
            this.diagnostics = diagnostics;
            this.race = race;
        }

        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            diagnostics.Attempt(uri);
            try
            {
                var socket = await inner.ConnectAsync(uri, token);
                diagnostics.SocketConnected();
                return socket;
            }
            catch (SocketException error)
            {
                diagnostics.Failure("socket:" + error.SocketErrorCode, error);
                throw;
            }
            catch (OperationCanceledException error)
            {
                if (race != null && race.Lost)
                    diagnostics.LostRace();   // uTP connected first; this attempt was dropped, not timed out
                else
                    diagnostics.Failure("timeout", error);
                throw;
            }
            catch (Exception error)
            {
                diagnostics.Failure("error:" + error.GetType().Name, error);
                throw;
            }
        }
    }
}
