using System.Net;
using Ru7100Probe.Utp;

internal static class Adapter
{
    public const string Name = "ours";
    public static IUtpCandidate Create() => new Candidate();

    // Experiment knobs (defaults = the shipped values): UTP_ACK_EVERY, UTP_DELAYED_ACK_US.
    private static UtpOptions Options()
    {
        var options = new UtpOptions();
        if (int.TryParse(Environment.GetEnvironmentVariable("UTP_ACK_EVERY"), out var every))
            options.AckEvery = every;
        if (long.TryParse(Environment.GetEnvironmentVariable("UTP_DELAYED_ACK_US"), out var delay))
            options.DelayedAckMicros = delay;
        return options;
    }

    private sealed class Candidate : IUtpCandidate
    {
        private UtpUdpSocket socket;

        public async Task<IUtpCatConnection> ConnectAsync(IPEndPoint remote)
        {
            socket = new UtpUdpSocket(new IPEndPoint(IPAddress.Any, 0), Options());
            socket.Start();
            var connection = socket.Context.Connect(remote);
            await connection.ConnectTask;
            return new Connection(connection, socket);
        }

        public async Task<IUtpCatConnection> AcceptAsync(int port)
        {
            socket = new UtpUdpSocket(new IPEndPoint(IPAddress.Any, port), Options());
            var accepted = new TaskCompletionSource<UtpConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            socket.Context.Accepted += c => accepted.TrySetResult(c);
            socket.Context.AcceptIncoming = true;
            socket.Start();
            return new Connection(await accepted.Task, socket);
        }

        public void Dispose() => socket?.Dispose();
    }

    private sealed class Connection : IUtpCatConnection
    {
        private readonly UtpConnection connection;
        private readonly UtpUdpSocket socket;
        public Connection(UtpConnection connection, UtpUdpSocket socket) { this.connection = connection; this.socket = socket; }
        public Task WriteAsync(ReadOnlyMemory<byte> data) => connection.WriteAsync(data);
        public Task<int> ReadAsync(Memory<byte> buffer) => connection.ReadAsync(buffer);
        public long BytesInFlight => connection.BytesInFlight;
        public int MaxWriteChunk => int.MaxValue;
        public string Counters() =>
            $"retransmits={connection.Retransmits} timeouts={connection.Timeouts} fast={connection.FastResends} dup={connection.DuplicatePackets} " +
            $"udp_sent={socket.DatagramsSent} udp_recv={socket.DatagramsReceived}";
        public void Dispose() => connection.Dispose();
    }
}
