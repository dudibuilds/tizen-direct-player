using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ru7100Probe.Utp;

/// uTP interop harness (M2): talks to another uTP implementation (libutp's ucat) over real UDP, optionally through a
/// lossy, reordering relay. Not part of the test run; scripts/utp-interop.sh drives it.
///
///   --utp-cat connect <host:port> <sendFile> <recvFile>   connect, send the file, half-close, read until the peer's FIN
///   --utp-cat listen <port> <sendFile> <recvFile>         accept one connection, same exchange
///   --udp-relay <listenPort> <targetPort> <loss> <jitterMs> [seconds]   127.0.0.1 relay: drops and delays datagrams
internal static class UtpInterop
{
    public static async Task<int> CatAsync(string mode, string address, string sendFile, string recvFile)
    {
        var send = File.ReadAllBytes(sendFile);
        var listen = mode == "listen";
        using var socket = new UtpUdpSocket(new IPEndPoint(IPAddress.Any, listen ? int.Parse(address) : 0));
        var accepted = new TaskCompletionSource<UtpConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.Context.Accepted += c => accepted.TrySetResult(c);
        socket.Context.AcceptIncoming = listen;
        socket.Start();
        var clock = Stopwatch.StartNew();
        UtpConnection connection;
        if (listen)
        {
            Console.Error.WriteLine($"utp-cat: listening on {socket.LocalEndPoint}");
            connection = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        else
        {
            var parts = address.Split(':');
            connection = socket.Context.Connect(new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            await connection.ConnectTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        Console.Error.WriteLine($"utp-cat: connected {connection.RemoteEndPoint} after {clock.ElapsedMilliseconds} ms");
        var discard = recvFile == "/dev/null";   // benchmark: count, don't keep
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var wall0 = clock.Elapsed;
        long total = 0;

        var writer = Task.Run(async () =>
        {
            for (var offset = 0; offset < send.Length; offset += 64 * 1024)
                await connection.WriteAsync(send.AsMemory(offset, Math.Min(64 * 1024, send.Length - offset)));
            connection.Close();   // FIN after the data: the peer sees end of stream
        });
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var n = await connection.ReadAsync(buffer).WaitAsync(TimeSpan.FromSeconds(60));
            if (n == 0)
                break;
            total += n;
            if (!discard)
                output.Write(buffer, 0, n);
        }
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        var wall = (clock.Elapsed - wall0).TotalSeconds;
        Console.Error.WriteLine($"utp-cat: cpu {cpu:0} ms for {total / 1e6:0.0} MB in {wall:0.00} s = {cpu / Math.Max(1e-9, total / 1e6):0.00} ms CPU per MB received, " +
            $"{total / 1e6 / wall:0.0} MB/s");
        await writer.WaitAsync(TimeSpan.FromSeconds(60));
        // Wait for our FIN (and the data before it) to be acknowledged before tearing the socket down.
        var drain = Stopwatch.StartNew();
        while (connection.BytesInFlight > 0 && drain.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(20);
        var seconds = clock.Elapsed.TotalSeconds;
        if (!discard)
            File.WriteAllBytes(recvFile, output.ToArray());
        Console.Error.WriteLine($"utp-cat: sent {send.Length} B, received {output.Length} B in {seconds:0.00} s; " +
            $"sha256 sent={Hash(send)} recv={Hash(output.ToArray())}");
        Console.Error.WriteLine($"utp-cat: {connection} fast={connection.FastResends} dup={connection.DuplicatePackets} " +
            $"udp: sent={socket.DatagramsSent} recv={socket.DatagramsReceived} senderr={socket.SendErrors} resets={socket.Context.ResetsReceived}");
        connection.Dispose();
        await Task.Delay(200);
        return 0;
    }

    public static async Task RelayAsync(int listenPort, int targetPort, double loss, int jitterMs, double seconds)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
        var target = new IPEndPoint(IPAddress.Loopback, targetPort);
        IPEndPoint client = null;
        var random = new Random(7);
        long forwarded = 0, dropped = 0;
        var stop = Stopwatch.StartNew();
        Console.Error.WriteLine($"udp-relay: 127.0.0.1:{listenPort} <-> {target} loss={loss} jitter={jitterMs} ms");
        Task<UdpReceiveResult> receive = null;
        while (stop.Elapsed.TotalSeconds < seconds)
        {
            receive ??= udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(1000)) != receive)
                continue;
            var result = receive.Result;
            receive = null;
            IPEndPoint to;
            if (result.RemoteEndPoint.Equals(target))
                to = client;
            else
            {
                client = result.RemoteEndPoint;
                to = target;
            }
            if (to == null)
                continue;
            if (random.NextDouble() < loss)
            {
                dropped++;
                continue;
            }
            forwarded++;
            var delay = jitterMs > 0 ? random.Next(jitterMs) : 0;
            var data = result.Buffer;
            if (delay == 0)
                await udp.SendAsync(data, data.Length, to);
            else
                _ = Task.Delay(delay).ContinueWith(_ => udp.SendAsync(data, data.Length, to));
        }
        Console.Error.WriteLine($"udp-relay: forwarded={forwarded} dropped={dropped}");
    }

    /// Baseline for the CPU figure: the same receive loop over a plain TCP socket (NetworkStream.ReadAsync).
    public static async Task TcpSinkAsync(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buffer = new byte[64 * 1024];
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
            total += n;
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.Error.WriteLine($"tcp-sink: cpu {cpu:0} ms for {total / 1e6:0.0} MB in {clock.Elapsed.TotalSeconds:0.00} s = {cpu / Math.Max(1e-9, total / 1e6):0.00} ms CPU per MB received");
        listener.Stop();
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16];
}
