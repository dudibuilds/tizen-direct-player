using System.Net;
using bzTorrent.IO;

internal static class Adapter
{
    public const string Name = "bztorrent";
    public static IUtpCandidate Create() => new Candidate();

    private sealed class Candidate : IUtpCandidate
    {
        private UTPListener listener;

        public Task<IUtpCatConnection> ConnectAsync(IPEndPoint remote)
        {
            // Connect only sends the SYN; data written before the STATE reply is queued by UTPConnection.
            var connection = new UTPConnection();
            connection.Connect(remote);
            return Task.FromResult<IUtpCatConnection>(new Connection(connection));
        }

        public async Task<IUtpCatConnection> AcceptAsync(int port)
        {
            listener = new UTPListener();
            listener.Bind(new IPEndPoint(IPAddress.Any, port));
            listener.Listen(1);
            var accepted = await Task.Run(() => (UTPConnection)listener.Accept());
            return new Connection(accepted);
        }

        public void Dispose() => listener?.Dispose();
    }

    private sealed class Connection : IUtpCatConnection
    {
        private readonly UTPConnection connection;
        private readonly byte[] scratch = new byte[64 * 1024];
        public Connection(UTPConnection connection) => this.connection = connection;
        // Send() puts the whole buffer into one datagram, so the driver writes at most one packet's payload per call.
        public Task WriteAsync(ReadOnlyMemory<byte> data) => Task.Run(() => connection.Send(data.ToArray()));
        public Task<int> ReadAsync(Memory<byte> buffer) => Task.Run(() =>
        {
            // Receive() copies a whole datagram into the buffer; give it a full-size one and hand back what fits.
            var n = connection.Receive(scratch, 0, scratch.Length);
            if (n > buffer.Length)
                throw new InvalidOperationException("payload larger than the read buffer");
            scratch.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        });
        public long BytesInFlight => -1;
        public int MaxWriteChunk => 1380;
        public string Counters() => "";
        public void Dispose() => connection.Dispose();
    }
}
