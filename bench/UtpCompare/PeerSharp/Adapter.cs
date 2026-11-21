using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PeerSharp.Config;
using PeerSharp.Internals.Framework;
using PeerSharp.Internals.Network;
using PeerSharp.Internals.Utp;

internal static class Adapter
{
    public const string Name = "peersharp";
    public static IUtpCandidate Create() => new Candidate();

    private sealed class Candidate : IUtpCandidate
    {
        private readonly Settings settings = new();
        private UdpListener listener;
        private UtpManager manager;

        private async Task StartAsync(int port)
        {
            settings.Dht.Enabled = false;
            listener = new UdpListener(port, new UdpSocketFactory(), settings, NullLoggerFactory.Instance, TimeProvider.System);
            await listener.StartAsync();
            manager = new UtpManager(TimeProvider.System, NullLoggerFactory.Instance, settings.Connection);
        }

        public async Task<IUtpCatConnection> ConnectAsync(IPEndPoint remote)
        {
            await StartAsync(0);
            manager.Start(listener);
            var stream = manager.CreateStream(remote);
            if (!await stream.ConnectAsync(TimeSpan.FromSeconds(15)))
                throw new IOException("uTP connect failed");
            return new Connection(stream);
        }

        public async Task<IUtpCatConnection> AcceptAsync(int port)
        {
            await StartAsync(port);
            var accepted = new TaskCompletionSource<UtpStream>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.OnNewConnection = s => accepted.TrySetResult(s);
            manager.Start(listener);
            return new Connection(await accepted.Task);
        }

        public void Dispose()
        {
            manager?.Stop();
            listener?.StopAsync().GetAwaiter().GetResult();
        }
    }

    private sealed class Connection : IUtpCatConnection
    {
        private readonly UtpStream stream;
        public Connection(UtpStream stream) => this.stream = stream;
        public Task WriteAsync(ReadOnlyMemory<byte> data) => stream.WriteAsync(data).AsTask();
        public Task<int> ReadAsync(Memory<byte> buffer) => stream.ReadAsync(buffer).AsTask();
        // Packets, not bytes: only "zero or not" matters to the driver.
        public long BytesInFlight => stream.SentPacketsCount;
        public int MaxWriteChunk => int.MaxValue;
        public string Counters() => $"rtt_us={stream.Rtt} state={stream.State}";
        public void Dispose() => stream.Dispose();
    }
}
