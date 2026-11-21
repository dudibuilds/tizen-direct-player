using System;
using System.Collections.Generic;
using System.Net;

namespace Ru7100Probe.Utp
{
    /// Monotonic microsecond clock. The protocol core reads time only through this, so tests can run it on a
    /// simulated clock; uTP timestamps on the wire are its low 32 bits.
    internal interface IUtpClock
    {
        long NowMicros { get; }
    }

    /// Where a context's datagrams go: the UDP socket, or a simulated link in tests. Must not keep the buffer.
    internal interface IUtpDatagramSink
    {
        void Send(byte[] buffer, int offset, int count, IPEndPoint remote);
    }

    internal sealed class UtpOptions
    {
        /// Payload bytes per DATA packet. 1380 + 20 header = 1400 B datagrams: under every common path MTU
        /// (Ethernet 1500, PPPoE 1492, most tunnels) after IPv4 + UDP headers, so no fragmentation.
        public int MaxPayload { get; set; } = 1380;
        /// Receive buffer per connection, advertised as the window. Memory is used only while the application lags
        /// behind the network: delivered data is copied straight into a waiting read.
        public int ReceiveBufferBytes { get; set; } = 1024 * 1024;
        /// Upper bound for the congestion window (bytes in flight).
        public int MaxSendWindowBytes { get; set; } = 1024 * 1024;
        public int InitialWindowPackets { get; set; } = 4;
        public long InitialRtoMicros { get; set; } = 1000_000;
        public long MinRtoMicros { get; set; } = 500_000;
        public long MaxRtoMicros { get; set; } = 60_000_000;
        /// SYN retransmissions before a connect fails (1 + 2 + 4 s with the default RTO; the engine's own connect
        /// timeout usually ends the attempt first).
        public int SynRetries { get; set; } = 3;
        /// Consecutive retransmission timeouts before an established connection is declared dead.
        public int DataRetries { get; set; } = 6;
        /// LEDBAT target queuing delay (BEP 29 / libutp: 100 ms).
        public long TargetDelayMicros { get; set; } = 100_000;
        /// LEDBAT gain: the most the window grows per round trip when there is no queuing delay.
        public int MaxCwndIncreaseBytesPerRtt { get; set; } = 3000;
        /// Idle connections send a STATE this often so NAT mappings stay open (libutp: 29 s).
        public long KeepAliveMicros { get; set; } = 29_000_000;
        /// In-order DATA packets acknowledged together at most. Below that, ACKs go out at the end of a receive batch
        /// when two or more are pending; a single one waits up to DelayedAckMicros for a second packet (like TCP's
        /// delayed ACK; LEDBAT is unaffected because the delay sample is taken on arrival, not when the ACK leaves).
        public int AckEvery { get; set; } = 8;
        /// Longest wait for a lone ACK, and for a partly filled read to be handed to its reader.
        public long DelayedAckMicros { get; set; } = 10_000;
        /// An accepted connection is dropped if nothing but its SYN arrives within this long.
        public long SynReceivedTimeoutMicros { get; set; } = 5_000_000;
        /// A connection the peer has sent nothing on for this long is reset (BitTorrent peers send a keepalive
        /// at least every two minutes; uTP idle keepalives every 29 s).
        public long IdleTimeoutMicros { get; set; } = 300_000_000;
        /// A closed connection waits this long at most for its FIN to be acknowledged.
        public long LingerMicros { get; set; } = 10_000_000;
        public int MaxConnections { get; set; } = 1024;
        /// Tests: fixed initial sequence number (wrap-around coverage). Null: random.
        public int? InitialSeqNr { get; set; }
    }

    /// The sans-IO uTP endpoint: one per UDP socket. Demultiplexes datagrams to connections by (remote, connection id),
    /// accepts incoming SYNs, answers strays with RESET, and drives retransmission timers from Tick.
    internal sealed class UtpContext
    {
        private readonly Dictionary<ConnectionKey, UtpConnection> connections = new Dictionary<ConnectionKey, UtpConnection>();
        private readonly List<UtpConnection> flushQueue = new List<UtpConnection>();
        private readonly List<UtpConnection> flushing = new List<UtpConnection>();
        private readonly List<UtpConnection> ticking = new List<UtpConnection>();
        private readonly Stack<UtpConnection.OutPacket> packetPool = new Stack<UtpConnection.OutPacket>();
        private readonly byte[] resetBuffer = new byte[UtpWire.HeaderSize];
        private readonly Random random;

        public UtpContext(IUtpClock clock, IUtpDatagramSink sink, UtpOptions options = null, Random random = null)
        {
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Sink = sink ?? throw new ArgumentNullException(nameof(sink));
            Options = options ?? new UtpOptions();
            this.random = random ?? new Random();
        }

        public IUtpClock Clock { get; }
        public UtpOptions Options { get; }
        internal IUtpDatagramSink Sink { get; }
        internal UtpBufferPool Buffers { get; } = new UtpBufferPool();

        /// Raised on the receive path for each accepted incoming connection (already connected).
        public event Action<UtpConnection> Accepted;
        public bool AcceptIncoming { get; set; } = true;

        public long ResetsSent;
        public long ResetsReceived;
        public long StrayPackets;
        public long IncomingAccepted;
        public long IncomingRefused;

        public int ConnectionCount
        {
            get { lock (connections) return connections.Count; }
        }

        public long NowMicros => Clock.NowMicros;

        /// Opens an outgoing connection: sends the SYN and returns at once; await ConnectTask for the outcome.
        public UtpConnection Connect(IPEndPoint remote)
        {
            if (remote == null)
                throw new ArgumentNullException(nameof(remote));
            UtpConnection connection;
            lock (connections)
            {
                ushort recvId;
                var guard = 0;
                do
                {
                    recvId = (ushort)Next(0x10000);
                    if (++guard > 1000)
                        throw new InvalidOperationException("No free uTP connection id for " + remote);
                }
                while (connections.ContainsKey(new ConnectionKey(remote, recvId)));
                var seq = Options.InitialSeqNr.HasValue ? (ushort)Options.InitialSeqNr.Value : (ushort)Next(0x10000);
                connection = new UtpConnection(this, remote, recvId, (ushort)(recvId + 1), seq, incoming: false);
                connections.Add(new ConnectionKey(remote, recvId), connection);
            }
            connection.StartConnect(Clock.NowMicros);
            return connection;
        }

        /// Handles one datagram. Returns false when it is not a uTP packet (the caller may hand it to the DHT).
        /// The buffer is only read during the call.
        public bool Receive(byte[] buffer, int offset, int count, IPEndPoint from)
        {
            if (!UtpHeader.TryParse(buffer, offset, count, out var header))
                return false;
            var now = Clock.NowMicros;
            UtpConnection connection;
            if (header.Type == UtpWire.TypeSyn)
            {
                var recvId = (ushort)(header.ConnectionId + 1);
                var key = new ConnectionKey(from, recvId);
                bool created = false;
                lock (connections)
                {
                    if (!connections.TryGetValue(key, out connection))
                    {
                        if (!AcceptIncoming || connections.Count >= Options.MaxConnections)
                            connection = null;
                        else
                        {
                            var seq = Options.InitialSeqNr.HasValue ? (ushort)Options.InitialSeqNr.Value : (ushort)Next(0x10000);
                            connection = new UtpConnection(this, from, recvId, header.ConnectionId, seq, incoming: true);
                            connections.Add(key, connection);
                            created = true;
                        }
                    }
                }
                if (connection == null)
                {
                    IncomingRefused++;
                    SendReset(from, header.ConnectionId, header.SeqNr);
                    return true;
                }
                if (created)
                {
                    IncomingAccepted++;
                    connection.AcceptSyn(header, now);
                    Accepted?.Invoke(connection);
                }
                else
                    connection.Receive(header, buffer, now);
                return true;
            }

            lock (connections)
            {
                if (!connections.TryGetValue(new ConnectionKey(from, header.ConnectionId), out connection) && header.Type == UtpWire.TypeReset)
                {
                    // A RESET carries the id of the packet it answers, which may be our send id (recv id +/- 1).
                    if (!(connections.TryGetValue(new ConnectionKey(from, (ushort)(header.ConnectionId - 1)), out connection) && connection.SendId == header.ConnectionId)
                        && !(connections.TryGetValue(new ConnectionKey(from, (ushort)(header.ConnectionId + 1)), out connection) && connection.SendId == header.ConnectionId))
                        connection = null;
                }
            }
            if (connection != null)
            {
                if (header.Type == UtpWire.TypeReset)
                    ResetsReceived++;
                connection.Receive(header, buffer, now);
                return true;
            }
            StrayPackets++;
            if (header.Type != UtpWire.TypeReset)
                SendReset(from, header.ConnectionId, header.SeqNr);
            return true;
        }

        /// Ends a receive batch: sends the ACKs that are due and completes reads whose data paused. Connections still
        /// holding something back stay queued; call again within DelayedAckMicros (HasDeferred) to release them.
        public void Flush()
        {
            lock (flushing)
            {
                lock (flushQueue)
                {
                    if (flushQueue.Count == 0)
                        return;
                    flushing.AddRange(flushQueue);
                    flushQueue.Clear();
                }
                var now = Clock.NowMicros;
                foreach (var connection in flushing)
                    if (connection.Flush(now))
                        lock (flushQueue)
                            flushQueue.Add(connection);
                flushing.Clear();
            }
        }

        /// Something waits for a deadline (delayed ACK or partial read): flush again soon.
        public bool HasDeferred
        {
            get { lock (flushQueue) return flushQueue.Count > 0; }
        }

        /// Timers: retransmission, keepalive, zero-window probe, linger. Call every 10-100 ms.
        public void Tick()
        {
            Flush();
            lock (ticking)
            {
                lock (connections)
                    ticking.AddRange(connections.Values);
                var now = Clock.NowMicros;
                foreach (var connection in ticking)
                    connection.Tick(now);
                ticking.Clear();
            }
        }

        /// Every live connection (diagnostics and shutdown).
        public List<UtpConnection> Snapshot()
        {
            lock (connections)
                return new List<UtpConnection>(connections.Values);
        }

        internal void ScheduleFlush(UtpConnection connection)
        {
            lock (flushQueue)
                flushQueue.Add(connection);
        }

        internal void Remove(UtpConnection connection)
        {
            lock (connections)
            {
                var key = new ConnectionKey(connection.RemoteEndPoint, connection.ReceiveId);
                if (connections.TryGetValue(key, out var existing) && existing == connection)
                    connections.Remove(key);
            }
        }

        internal void Send(byte[] buffer, int offset, int count, IPEndPoint remote) => Sink.Send(buffer, offset, count, remote);

        internal void SendReset(IPEndPoint remote, ushort connectionId, ushort ackNr)
        {
            lock (resetBuffer)
            {
                UtpHeader.Write(resetBuffer, 0, UtpWire.TypeReset, 0, connectionId, (uint)Clock.NowMicros, 0, 0, (ushort)Next(0x10000), ackNr);
                ResetsSent++;
                Sink.Send(resetBuffer, 0, UtpWire.HeaderSize, remote);
            }
        }

        internal UtpConnection.OutPacket RentPacket()
        {
            UtpConnection.OutPacket packet = null;
            lock (packetPool)
                if (packetPool.Count > 0)
                    packet = packetPool.Pop();
            packet = packet ?? new UtpConnection.OutPacket();
            packet.Buffer = Buffers.Rent();
            return packet;
        }

        internal void ReturnPacket(UtpConnection.OutPacket packet)
        {
            Buffers.Return(packet.Buffer);
            packet.Reset();
            lock (packetPool)
                if (packetPool.Count < 4096)
                    packetPool.Push(packet);
        }

        internal int Next(int max)
        {
            lock (random)
                return random.Next(max);
        }

        private readonly struct ConnectionKey : IEquatable<ConnectionKey>
        {
            private readonly IPEndPoint endPoint;
            private readonly ushort id;

            public ConnectionKey(IPEndPoint endPoint, ushort id)
            {
                this.endPoint = endPoint;
                this.id = id;
            }

            public bool Equals(ConnectionKey other) => id == other.id && endPoint.Equals(other.endPoint);
            public override bool Equals(object obj) => obj is ConnectionKey other && Equals(other);
            public override int GetHashCode() => endPoint.GetHashCode() * 31 + id;
        }
    }
}
