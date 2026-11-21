using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100Probe.Utp
{
    /// One IPv4 UDP socket carrying uTP, plus any other UDP protocol sharing the port (the DHT): datagrams that are
    /// not uTP go to OtherDatagram. A dedicated thread does blocking receives (no per-packet tasks), and ACKs held
    /// back during a burst go out when the socket has nothing more queued. A 50 ms timer drives retransmissions.
    /// Stopwatch ticks to microseconds without overflow: ticks * 1e6 wraps after ~2.5 h of uptime at a 1 GHz
    /// Stopwatch.Frequency (common on ARM Linux), which froze retransmit, keep-alive and linger deadlines.
    internal static class UtpTime
    {
        public static long ToMicros(long ticks, long frequency)
        {
            if (frequency == 1_000_000)
                return ticks;
            var seconds = ticks / frequency;
            var rest = ticks % frequency;
            return seconds * 1_000_000 + rest * 1_000_000 / frequency;
        }
    }

    internal sealed class UtpUdpSocket : IDisposable, IUtpDatagramSink, IUtpClock
    {
        private readonly Socket socket;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Thread receiver;
        private Timer ticker;
        private volatile bool disposed;

        public UtpUdpSocket(IPEndPoint bind, UtpOptions options = null)
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Several hundred KB absorbs a burst while the receive thread is descheduled (the kernel may cap it).
                socket.ReceiveBufferSize = 1024 * 1024;
                socket.SendBufferSize = 512 * 1024;
            }
            catch (SocketException) { }
            socket.Bind(bind);
            // Non-blocking: a send happens under a connection lock, and a blocking send into a full buffer could
            // deadlock two endpoints that each wait for the other to read. A full buffer drops the packet instead
            // (uTP retransmits it), like libutp. The receive thread waits in Poll.
            socket.Blocking = false;
            LocalEndPoint = (IPEndPoint)socket.LocalEndPoint;
            Context = new UtpContext(this, this, options);
            receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "utp-receive" };
        }

        public UtpContext Context { get; }
        public IPEndPoint LocalEndPoint { get; }
        /// Datagrams that are not uTP (e.g. bencoded DHT messages). Called on the receive thread; the buffer is
        /// reused after the call returns.
        public Action<byte[], int, int, IPEndPoint> OtherDatagram { get; set; }
        public long DatagramsReceived;
        public long DatagramsSent;
        public long SendErrors;
        public string LastError = "";

        public long NowMicros => UtpTime.ToMicros(clock.ElapsedTicks, Stopwatch.Frequency);

        public void Start()
        {
            receiver.Start();
            ticker = new Timer(_ => Tick(), null, 50, 50);
        }

        private void Tick()
        {
            if (disposed)
                return;
            try { Context.Tick(); }
            catch (Exception error) { LastError = "tick: " + error.GetType().Name + " " + error.Message; }
        }

        public void Send(byte[] buffer, int offset, int count, IPEndPoint remote)
        {
            if (disposed)
                return;
            try
            {
                socket.SendTo(buffer, offset, count, SocketFlags.None, remote);
                Interlocked.Increment(ref DatagramsSent);
            }
            catch (SocketException error)
            {
                // A full send buffer or an unreachable host: the packet counts as lost and uTP retransmits it.
                Interlocked.Increment(ref SendErrors);
                LastError = "send: " + error.SocketErrorCode;
            }
            catch (ObjectDisposedException) { }
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote)
        {
            // DHT messages are small and rare: one copy keeps the netstandard2.0 socket API usable.
            var copy = data.ToArray();
            Send(copy, 0, copy.Length, remote);
            return Task.CompletedTask;
        }

        private void ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var batch = 0;
            while (!disposed)
            {
                try
                {
                    if (socket.Available == 0)
                    {
                        Context.Flush();
                        batch = 0;
                        // Held-back ACKs / partial reads are due within DelayedAckMicros: wake for them.
                        if (!socket.Poll(Context.HasDeferred ? 5_000 : 250_000, SelectMode.SelectRead))
                        {
                            Context.Flush();
                            continue;
                        }
                    }
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    var count = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref from);
                    DatagramsReceived++;
                    var remote = (IPEndPoint)from;
                    if (UtpWire.LooksLikeUtp(buffer, 0, count))
                    {
                        if (!Context.Receive(buffer, 0, count, remote))
                            OtherDatagram?.Invoke(buffer, 0, count, remote);
                    }
                    else
                        OtherDatagram?.Invoke(buffer, 0, count, remote);
                    if (++batch >= 32)
                    {
                        batch = 0;
                        Context.Flush();
                    }
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                {
                }
                catch (SocketException error)
                {
                    // ICMP port unreachable surfaces here on some platforms; keep receiving.
                    LastError = "receive: " + error.SocketErrorCode;
                    if (disposed)
                        break;
                    try { Context.Flush(); } catch (Exception) { }
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception error)
                {
                    LastError = "receive: " + error.GetType().Name + " " + error.Message;
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ticker?.Dispose();
            foreach (var connection in Context.Snapshot())
                connection.Abort();
            try { socket.Dispose(); } catch (Exception) { }
        }
    }

    /// A Stream over a uTP connection (read/write/close), for code that wants System.IO semantics.
    internal sealed class UtpStream : Stream
    {
        public UtpStream(UtpConnection connection) => Connection = connection ?? throw new ArgumentNullException(nameof(connection));

        public UtpConnection Connection { get; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WithCancellation(Connection.ReadAsync(new Memory<byte>(buffer, offset, count)), cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WithCancellation(Connection.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count)), cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Connection.Dispose();
            base.Dispose(disposing);
        }

        /// Cancelling a uTP read or write ends the connection (like cancelling a socket operation).
        private async Task<T> WithCancellation<T>(Task<T> task, CancellationToken token)
        {
            if (!token.CanBeCanceled || task.IsCompleted)
                return await task.ConfigureAwait(false);
            using (token.Register(() => Connection.Dispose()))
                return await task.ConfigureAwait(false);
        }

        private async Task WithCancellation(Task task, CancellationToken token)
        {
            if (!token.CanBeCanceled || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }
            using (token.Register(() => Connection.Dispose()))
                await task.ConfigureAwait(false);
        }
    }
}
