using System.Net;
using MonoTorrent.Connections.Peer.Utp;

internal static class Adapter
{
    public const string Name = "monotorrent-main";
    public static IUtpCandidate Create() => new Candidate();

    private sealed class Candidate : IUtpCandidate
    {
        private UtpPeerConnectionListener listener;

        public async Task<IUtpCatConnection> ConnectAsync(IPEndPoint remote)
        {
            listener = new UtpPeerConnectionListener(new IPEndPoint(IPAddress.Any, 0));
            listener.Start();
            var connection = new UtpPeerConnection(listener, remote, (ushort)Random.Shared.Next(1, ushort.MaxValue));
            if (!await connection.ConnectAsync())
                throw new IOException("uTP connect failed");
            return new Connection(connection);
        }

        public async Task<IUtpCatConnection> AcceptAsync(int port)
        {
            listener = new UtpPeerConnectionListener(new IPEndPoint(IPAddress.Any, port));
            var accepted = new TaskCompletionSource<UtpPeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionReceived += (_, e) => accepted.TrySetResult((UtpPeerConnection)e.Connection);
            listener.Start();
            return new Connection(await accepted.Task);
        }

        public void Dispose() => listener?.Stop();
    }

    private sealed class Connection : IUtpCatConnection
    {
        private readonly UtpPeerConnection connection;
        public Connection(UtpPeerConnection connection) => this.connection = connection;
        public async Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            while (!data.IsEmpty)
            {
                var n = await connection.SendAsync(data);
                if (n <= 0)
                    throw new IOException("SendAsync returned 0");
                data = data.Slice(n);
            }
        }
        public async Task<int> ReadAsync(Memory<byte> buffer) => await connection.ReceiveAsync(buffer);
        public long BytesInFlight => connection.BytesInFlightForTests;
        public int MaxWriteChunk => int.MaxValue;
        public string Counters()
        {
            var d = connection.DiagnosticSnapshot;
            return $"rtt_us={d.RttMicroseconds} rto_us={d.RetransmitTimeoutMicroseconds} mtu={d.CurrentMtuBytes} window={d.SendWindowBytes} state={d.State}";
        }
        public void Dispose() => connection.Dispose();
    }
}
