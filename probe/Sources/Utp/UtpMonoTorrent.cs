using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Connections.Peer;
using ReusableTasks;

namespace Ru7100Probe.Utp
{
    /// uTP for the MonoTorrent engine: one UDP socket on the engine's fixed port number (the TCP listen port), shared
    /// with the DHT. Peers learn a single port from trackers and the DHT and try both TCP and uTP on it (libtorrent
    /// and uTorrent do), so uTP must sit on the TCP port number; the DHT already owns UDP on that number, hence one
    /// socket demultiplexed by the first byte: bencoded DHT messages start with 'd', uTP has version 1 in the low
    /// nibble. One socket also means one UPnP UDP mapping (the engine's DHT mapping) covers both.
    internal sealed class UtpTransport : IDisposable
    {
        private readonly ConnectionDiagnostics diagnostics;
        private readonly UtpUdpSocket socket;
        private UtpDhtListener dht;
        private int listeners;

        /// When uTP connects first, how long the race still waits for TCP to the same peer (TCP then wins). Zero: the
        /// first transport to connect wins.
        public TimeSpan TcpGrace { get; set; }
        /// When TCP to a peer failed, how long (from the start of the attempt) uTP alone may still try. Zero: until the
        /// engine's connect timeout.
        public TimeSpan UtpAloneBudget { get; set; } = TimeSpan.FromSeconds(2);

        public UtpTransport(IPEndPoint bind, ConnectionDiagnostics diagnostics, UtpOptions options = null)
        {
            this.diagnostics = diagnostics;
            socket = new UtpUdpSocket(bind, options);
            socket.Context.AcceptIncoming = false;
            socket.Context.Accepted += OnAccepted;
            socket.OtherDatagram = (buffer, offset, count, from) => dht?.Deliver(buffer, offset, count, from);
            socket.Start();
        }

        public IPEndPoint LocalEndPoint => socket.LocalEndPoint;
        public UtpContext Context => socket.Context;
        /// Transports that failed a handshake with a given address (shared by every outgoing race).
        internal PeerTransportMemory Memory { get; } = new PeerTransportMemory();
        internal UtpUdpSocket Socket => socket;
        internal event Action<UtpConnection> Incoming;

        /// The engine's DHT listener: same socket, bencoded datagrams only.
        public IDhtListener CreateDhtListener() => dht = new UtpDhtListener(this);

        public UtpConnection Connect(IPEndPoint remote) => socket.Context.Connect(remote);

        internal void ListenerStarted()
        {
            if (Interlocked.Increment(ref listeners) == 1)
                socket.Context.AcceptIncoming = true;
        }

        internal void ListenerStopped()
        {
            if (Interlocked.Decrement(ref listeners) <= 0)
            {
                listeners = 0;
                socket.Context.AcceptIncoming = false;
            }
        }

        private void OnAccepted(UtpConnection connection)
        {
            diagnostics?.UtpIncoming();
            var handler = Incoming;
            if (handler == null)
                connection.Abort();
            else
                handler(connection);
        }

        internal void Send(byte[] buffer, int offset, int count, IPEndPoint remote) => socket.Send(buffer, offset, count, remote);

        public string Describe()
        {
            var context = socket.Context;
            // Live connections only: received payload, duplicates (the peer resent what we had: lost ACKs or a
            // spurious resend), and our own retransmissions.
            long received = 0, duplicates = 0, retransmits = 0;
            foreach (var connection in context.Snapshot())
            {
                received += connection.BytesReceived;
                duplicates += connection.DuplicatePackets;
                retransmits += connection.Retransmits;
            }
            return $"utp-socket={socket.LocalEndPoint.Port} conns={context.ConnectionCount} live-rx={received / 1e6:0.0}MB dup={duplicates} rexmit={retransmits} " +
                $"udp in={socket.DatagramsReceived} out={socket.DatagramsSent} " +
                $"senderr={socket.SendErrors} rst out={context.ResetsSent} in={context.ResetsReceived} stray={context.StrayPackets}" +
                (socket.LastError.Length > 0 ? " last=" + socket.LastError : "");
        }

        public void Dispose() => socket.Dispose();
    }

    /// The DHT's view of the shared socket. Start/Stop only gate delivery: the socket belongs to UtpTransport, so
    /// the engine stopping its DHT never closes uTP connections.
    internal sealed class UtpDhtListener : IDhtListener
    {
        private readonly UtpTransport transport;
        private ListenerStatus status = ListenerStatus.NotListening;

        public UtpDhtListener(UtpTransport transport) => this.transport = transport;

        public event Action<ReadOnlyMemory<byte>, IPEndPoint> MessageReceived;
        public event EventHandler<EventArgs> StatusChanged;

        public IPEndPoint LocalEndPoint => transport.LocalEndPoint;
        public ListenerStatus Status => status;

        public void Start()
        {
            status = ListenerStatus.Listening;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            status = ListenerStatus.NotListening;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task SendAsync(ReadOnlyMemory<byte> buffer, IPEndPoint endpoint)
        {
            if (status != ListenerStatus.Listening)
                throw new InvalidOperationException("The DHT listener is not started.");
            var copy = buffer.ToArray();
            transport.Send(copy, 0, copy.Length, endpoint);
            return Task.CompletedTask;
        }

        internal void Deliver(byte[] buffer, int offset, int count, IPEndPoint from)
        {
            if (status != ListenerStatus.Listening || count == 0 || buffer[offset] != (byte)'d')
                return;
            // The DHT decodes after an await, so it needs its own copy (MonoTorrent's UdpListener hands it a fresh array).
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            MessageReceived?.Invoke(copy, from);
        }
    }

    /// A uTP connection as MonoTorrent's IPeerConnection. Outgoing ones connect in ConnectAsync; Dispose aborts a
    /// pending connect (the engine's connect timeout disposes the connection).
    internal sealed class UtpPeerConnection : IPeerConnection
    {
        private readonly UtpTransport transport;
        private UtpConnection connection;
        private int disposed;

        public UtpPeerConnection(Uri uri, UtpTransport transport)
        {
            Uri = uri;
            this.transport = transport;
            EndPoint = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
            AddressBytes = EndPoint.Address.GetAddressBytes();
        }

        public UtpPeerConnection(UtpConnection accepted)
        {
            connection = accepted;
            EndPoint = accepted.RemoteEndPoint;
            AddressBytes = EndPoint.Address.GetAddressBytes();
            Uri = new Uri("ipv4://" + EndPoint);
            IsIncoming = true;
        }

        public ReadOnlyMemory<byte> AddressBytes { get; }
        public bool CanReconnect => !IsIncoming;
        public bool Disposed => disposed != 0;
        public IPEndPoint EndPoint { get; }
        public bool IsIncoming { get; }
        public Uri Uri { get; }
        public UtpConnection Connection => connection;

        public async ReusableTask ConnectAsync()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(UtpPeerConnection));
            var outgoing = transport.Connect(EndPoint);
            connection = outgoing;
            if (Disposed)
                outgoing.Dispose();
            await outgoing.ConnectTask.ConfigureAwait(false);
        }

        public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer) =>
            await (connection ?? throw new InvalidOperationException("Not connected")).ReadAsync(buffer).ConfigureAwait(false);

        public async ReusableTask<int> SendAsync(Memory<byte> buffer)
        {
            await (connection ?? throw new InvalidOperationException("Not connected")).WriteAsync(buffer).ConfigureAwait(false);
            return buffer.Length;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                connection?.Dispose();
        }

        public override string ToString() => "utp " + Uri;
    }

    /// Outgoing IPv4 peer connection that tries TCP and uTP at once; the first transport to connect wins and the other
    /// attempt is dropped. MonoTorrent's peer list carries no uTP capability flag (PEX flags are not exposed), so a
    /// race is the only way to reach peers that are uTP-only (e.g. behind NAT with only UDP mapped) without paying a
    /// sequential fallback's timeout. A dead address costs one SYN of each kind.
    /// A transport that connected but closed before the BitTorrent handshake (a host that accepts TCP but whose client
    /// only takes uTP, or the reverse) is remembered per address, and the engine's retry uses only the other one.
    internal sealed class RacingPeerConnection : IPeerConnection
    {
        private readonly UtpTransport transport;
        private readonly ConnectionDiagnostics diagnostics;
        private readonly object gate = new object();
        private SocketPeerConnection tcp;
        private UtpPeerConnection utp;
        private RaceFlag tcpFlag;
        private readonly RaceFlag utpFlag = new RaceFlag();
        private IPeerConnection winner;
        private bool disposed;

        public RacingPeerConnection(Uri uri, UtpTransport transport, ConnectionDiagnostics diagnostics)
        {
            Uri = uri;
            this.transport = transport;
            this.diagnostics = diagnostics;
            EndPoint = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
            AddressBytes = EndPoint.Address.GetAddressBytes();
        }

        public ReadOnlyMemory<byte> AddressBytes { get; }
        public bool CanReconnect => true;
        public bool Disposed { get { lock (gate) return disposed; } }
        public IPEndPoint EndPoint { get; }
        public bool IsIncoming => false;
        public Uri Uri { get; }
        /// "tcp" or "utp" once connected.
        public string Transport { get; private set; } = "";

        public async ReusableTask ConnectAsync()
        {
            var memory = transport.Memory.Get(EndPoint);
            var useTcp = (memory & PeerTransportMemory.TcpFailed) == 0 || (memory & PeerTransportMemory.UtpFailed) != 0;
            var useUtp = (memory & PeerTransportMemory.UtpFailed) == 0 || (memory & PeerTransportMemory.TcpFailed) != 0;
            lock (gate)
            {
                if (disposed)
                    throw new ObjectDisposedException(nameof(RacingPeerConnection));
                tcpFlag = new RaceFlag();
                if (useTcp)
                    tcp = new SocketPeerConnection(Uri, new CountingSocketConnector(new CancellableSocketConnector(), diagnostics, tcpFlag));
                if (useUtp)
                    utp = new UtpPeerConnection(Uri, transport);
            }
            var started = System.Diagnostics.Stopwatch.StartNew();
            Task tcpTask = useTcp ? tcp.ConnectAsync().AsTask() : null;
            Task utpTask = null;
            if (useUtp)
            {
                diagnostics?.UtpAttempt();
                utpTask = ConnectUtpAsync(utp);
            }

            ObserveLegs(tcpTask, utpTask);
            Task won = null;
            if (tcpTask != null && utpTask != null)
            {
                var first = await Task.WhenAny(tcpTask, utpTask).ConfigureAwait(false);
                var second = first == tcpTask ? utpTask : tcpTask;
                if (first.Status == TaskStatus.RanToCompletion)
                {
                    won = first;
                    // uTP answered first: give TCP a moment. For a peer reachable both ways TCP is the better pick (the
                    // kernel does the work instead of our managed stack, and uTP senders back off under LEDBAT and
                    // libtorrent's mixed-mode limits), so it only loses when it is slower by more than the grace.
                    if (first == utpTask && transport.TcpGrace > TimeSpan.Zero)
                    {
                        await Task.WhenAny(tcpTask, Task.Delay(transport.TcpGrace)).ConfigureAwait(false);
                        if (tcpTask.Status == TaskStatus.RanToCompletion)
                        {
                            won = tcpTask;
                            diagnostics?.TcpPreferred();
                        }
                    }
                }
                else
                {
                    // TCP failed fast (refused / unreachable): the host is there, but a uTP-only peer answers its SYN
                    // within a round trip or two. Waiting out the engine's connect timeout for uTP held a half-open
                    // slot ~6 s per refused peer and slowed starts (13-19 s vs 9 s TCP-only on the same sources).
                    if (second == utpTask && transport.UtpAloneBudget > TimeSpan.Zero)
                    {
                        var left = transport.UtpAloneBudget - started.Elapsed;
                        if (left > TimeSpan.Zero)
                            await Task.WhenAny(second, Task.Delay(left)).ConfigureAwait(false);
                        if (!second.IsCompleted)
                        {
                            utpFlag.Lost = true;   // counted as a lost race, not a timeout
                            utp.Dispose();
                        }
                    }
                    try { await second.ConfigureAwait(false); } catch (Exception) { }
                    if (second.Status == TaskStatus.RanToCompletion)
                        won = second;
                }
            }
            else
            {
                var only = tcpTask ?? utpTask;
                try { await only.ConfigureAwait(false); } catch (Exception) { }
                if (only.Status == TaskStatus.RanToCompletion)
                    won = only;
            }
            lock (gate)
            {
                if (won == null || disposed)
                {
                    tcpFlag.Lost = true;
                    utpFlag.Lost = true;
                    tcp?.Dispose();
                    utp?.Dispose();
                    if (disposed)
                        throw new ObjectDisposedException(nameof(RacingPeerConnection));
                    // Report TCP's error: it says more (refused, unreachable) than a silent UDP peer's timeout.
                    tcpTask?.GetAwaiter().GetResult();
                    utpTask?.GetAwaiter().GetResult();
                    throw new SocketException((int)SocketError.TimedOut);
                }
                if (won == tcpTask)
                {
                    winner = new MeteredPeerConnection(tcp, false, diagnostics, OnClosed);
                    Transport = "tcp";
                    if (utp != null)
                    {
                        utpFlag.Lost = true;
                        utp.Dispose();
                    }
                }
                else
                {
                    winner = new MeteredPeerConnection(utp, true, diagnostics, OnClosed);
                    Transport = "utp";
                    if (tcp != null)
                    {
                        tcpFlag.Lost = true;
                        tcp.Dispose();
                    }
                }
            }
            if (tcpTask != null && utpTask != null)
                diagnostics?.RaceWon(Transport == "utp");
        }

        /// The losing leg is disposed and faults (OperationAborted); observe it so the finalizer never reports it.
        private static void ObserveLegs(Task tcpTask, Task utpTask)
        {
            foreach (var leg in new[] { tcpTask, utpTask })
                leg?.ContinueWith(t => { var _ = t.Exception; }, System.Threading.CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void OnClosed(bool overUtp, bool handshook) => transport.Memory.Record(EndPoint, overUtp, handshook);

        private async Task ConnectUtpAsync(UtpPeerConnection connection)
        {
            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);
                diagnostics?.UtpConnected();
            }
            catch (SocketException error)
            {
                // Aborted = this attempt was dropped: TCP won the race (counted apart) or the engine's connect
                // timeout disposed the connection (a silent peer: reported as a timeout).
                if (error.SocketErrorCode == SocketError.OperationAborted && utpFlag.Lost)
                    diagnostics?.UtpLostRace();
                else
                    diagnostics?.Failure(error.SocketErrorCode == SocketError.OperationAborted ? "utp:timeout" : "utp:" + error.SocketErrorCode);
                throw;
            }
            catch (Exception error)
            {
                diagnostics?.Failure("utp:" + error.GetType().Name);
                throw;
            }
        }

        public ReusableTask<int> ReceiveAsync(Memory<byte> buffer) => (winner ?? throw new InvalidOperationException("Not connected")).ReceiveAsync(buffer);
        public ReusableTask<int> SendAsync(Memory<byte> buffer) => (winner ?? throw new InvalidOperationException("Not connected")).SendAsync(buffer);

        public void Dispose()
        {
            IPeerConnection connected;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                connected = winner;
            }
            if (connected != null)
                connected.Dispose();
            tcp?.Dispose();
            utp?.Dispose();
        }

        public override string ToString() => (Transport.Length > 0 ? Transport : "tcp|utp") + " " + Uri;
    }

    /// Per-address memory of transports that connected but never completed a handshake. Bounded; forgotten after
    /// ten minutes so a peer that changes its settings is raced again.
    internal sealed class PeerTransportMemory
    {
        public const int TcpFailed = 1;
        public const int UtpFailed = 2;
        private const int Capacity = 4096;
        private static readonly long Lifetime = TimeSpan.FromMinutes(10).Ticks;
        private readonly System.Collections.Generic.Dictionary<IPEndPoint, (int flags, long since)> entries =
            new System.Collections.Generic.Dictionary<IPEndPoint, (int, long)>();

        public int Get(IPEndPoint endPoint)
        {
            lock (entries)
            {
                if (!entries.TryGetValue(endPoint, out var entry))
                    return 0;
                if (DateTime.UtcNow.Ticks - entry.since > Lifetime)
                {
                    entries.Remove(endPoint);
                    return 0;
                }
                return entry.flags;
            }
        }

        public void Record(IPEndPoint endPoint, bool utp, bool handshook)
        {
            var bit = utp ? UtpFailed : TcpFailed;
            lock (entries)
            {
                entries.TryGetValue(endPoint, out var entry);
                var flags = handshook ? entry.flags & ~bit : entry.flags | bit;
                if (flags == 0)
                    entries.Remove(endPoint);
                else
                {
                    if (entries.Count >= Capacity && !entries.ContainsKey(endPoint))
                        entries.Clear();
                    entries[endPoint] = (flags, DateTime.UtcNow.Ticks);
                }
            }
        }
    }

    /// Marks a TCP attempt abandoned because uTP won (its cancellation is then not a timeout).
    internal sealed class RaceFlag
    {
        public volatile bool Lost;
    }

    /// Counts received bytes and BitTorrent handshakes (>= 68 bytes in) per transport.
    internal sealed class MeteredPeerConnection : IPeerConnection
    {
        private readonly IPeerConnection inner;
        private readonly bool utp;
        private readonly ConnectionDiagnostics diagnostics;
        private readonly Action<bool, bool> closed;
        private long received;
        private int disposed;

        /// closed(utp, handshook) runs once on Dispose.
        public MeteredPeerConnection(IPeerConnection inner, bool utp, ConnectionDiagnostics diagnostics, Action<bool, bool> closed = null)
        {
            this.inner = inner;
            this.utp = utp;
            this.diagnostics = diagnostics;
            this.closed = closed;
        }

        public ReadOnlyMemory<byte> AddressBytes => inner.AddressBytes;
        public bool CanReconnect => inner.CanReconnect;
        public bool Disposed => inner.Disposed;
        public IPEndPoint EndPoint => inner.EndPoint;
        public bool IsIncoming => inner.IsIncoming;
        public Uri Uri => inner.Uri;

        public ReusableTask ConnectAsync() => inner.ConnectAsync();

        public async ReusableTask<int> ReceiveAsync(Memory<byte> buffer)
        {
            var n = await inner.ReceiveAsync(buffer).ConfigureAwait(false);
            if (n > 0)
            {
                var before = received;
                received += n;
                diagnostics?.Received(utp, n, before < 68 && received >= 68);
            }
            return n;
        }

        public ReusableTask<int> SendAsync(Memory<byte> buffer) => inner.SendAsync(buffer);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            inner.Dispose();
            closed?.Invoke(utp, received >= 68);
        }
        public override string ToString() => (utp ? "utp " : "tcp ") + inner.Uri;
    }

    /// The IPv4 peer listener: MonoTorrent's TCP listener plus incoming uTP connections from the shared UDP socket
    /// (when transport is not null), raised through the same ConnectionReceived event (no info hash yet; the
    /// handshake identifies the torrent). Both kinds are metered per transport.
    internal sealed class DualPeerConnectionListener : IPeerConnectionListener
    {
        private readonly IPeerConnectionListener tcp;
        private readonly UtpTransport transport;
        private readonly ConnectionDiagnostics diagnostics;
        private bool started;

        public DualPeerConnectionListener(IPeerConnectionListener tcp, UtpTransport transport, ConnectionDiagnostics diagnostics)
        {
            this.tcp = tcp;
            this.transport = transport;
            this.diagnostics = diagnostics;
            tcp.ConnectionReceived += (sender, args) =>
                ConnectionReceived?.Invoke(this, new PeerConnectionEventArgs(new MeteredPeerConnection(args.Connection, false, diagnostics), args.InfoHash));
            tcp.StatusChanged += (sender, args) => StatusChanged?.Invoke(this, args);
        }

        public event EventHandler<PeerConnectionEventArgs> ConnectionReceived;
        public event EventHandler<EventArgs> StatusChanged;

        public IPEndPoint LocalEndPoint => tcp.LocalEndPoint;
        public IPEndPoint PreferredLocalEndPoint => tcp.PreferredLocalEndPoint;
        public ListenerStatus Status => tcp.Status;

        public void Start()
        {
            tcp.Start();
            lock (this)
            {
                if (started)
                    return;
                started = true;
            }
            if (transport != null)
            {
                transport.Incoming += OnIncoming;
                transport.ListenerStarted();
            }
        }

        public void Stop()
        {
            tcp.Stop();
            lock (this)
            {
                if (!started)
                    return;
                started = false;
            }
            if (transport != null)
            {
                transport.Incoming -= OnIncoming;
                transport.ListenerStopped();
            }
        }

        private void OnIncoming(UtpConnection connection)
        {
            var handler = ConnectionReceived;
            if (handler == null)
            {
                connection.Abort();
                return;
            }
            handler(this, new PeerConnectionEventArgs(new MeteredPeerConnection(new UtpPeerConnection(connection), true, diagnostics), null));
        }
    }
}
