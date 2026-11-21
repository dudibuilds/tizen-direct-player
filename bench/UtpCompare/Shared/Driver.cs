using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

// uTP comparison driver (docs/utp-comparison.md). One project per candidate implementation compiles this file plus an
// Adapter.cs that maps the candidate's own socket/connection API onto IUtpCandidate. scripts/utp-compare.sh runs it.
//
//   cat connect <host:port> <sendFile|-> <expectBytes> <recvFile|/dev/null>
//   cat listen <port> <sendFile|-> <expectBytes> <recvFile|/dev/null>
//       Send the file and, at the same time, read exactly expectBytes. No FIN/half-close is involved: the transfer
//       ends by byte count (as BitTorrent does), then waits until our sent data is acknowledged before closing.
//   relay <listenPort> <targetPort> <loss> <jitterMs> <seconds> <statsFile>
//       127.0.0.1 UDP relay that drops and delays datagrams (same seeded logic as desktop/UtpInterop.cs) and counts
//       wire bytes per direction.

public interface IUtpCandidate : IDisposable
{
    Task<IUtpCatConnection> ConnectAsync(IPEndPoint remote);
    Task<IUtpCatConnection> AcceptAsync(int port);
}

public interface IUtpCatConnection : IDisposable
{
    Task WriteAsync(ReadOnlyMemory<byte> data);
    Task<int> ReadAsync(Memory<byte> buffer);
    /// Bytes sent but not yet acknowledged; -1 when the implementation does not expose it.
    long BytesInFlight { get; }
    /// Largest write the implementation accepts in one call (bzTorrent sends one datagram per call).
    int MaxWriteChunk { get; }
    string Counters();
}

public static class Driver
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args[0] == "cat")
                return await CatAsync(args[1], args[2], args[3], long.Parse(args[4]), args[5]);
            if (args[0] == "relay")
            {
                await RelayAsync(int.Parse(args[1]), int.Parse(args[2]), double.Parse(args[3]), int.Parse(args[4]),
                    double.Parse(args[5]), args[6]);
                return 0;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"RESULT cand={Adapter.Name} error=\"{e.GetType().Name}: {e.Message.Replace('"', '\'')}\"");
            return 2;
        }
        Console.Error.WriteLine("usage: cat connect|listen <addr> <sendFile|-> <expectBytes> <recvFile> | relay ...");
        return 1;
    }

    private static async Task<int> CatAsync(string mode, string address, string sendFile, long expect, string recvFile)
    {
        var send = sendFile == "-" ? Array.Empty<byte>() : File.ReadAllBytes(sendFile);
        using var candidate = Adapter.Create();
        var clock = Stopwatch.StartNew();
        IUtpCatConnection connection;
        if (mode == "listen")
            connection = await candidate.AcceptAsync(int.Parse(address)).WaitAsync(TimeSpan.FromSeconds(60));
        else
        {
            var parts = address.Split(':');
            connection = await candidate.ConnectAsync(new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1])))
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        var connectMs = clock.Elapsed.TotalMilliseconds;
        var process = Process.GetCurrentProcess();
        var cpu0 = process.TotalProcessorTime;
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        var t0 = clock.Elapsed;

        var chunk = Math.Min(16 * 1024, connection.MaxWriteChunk);
        var sendDone = TimeSpan.Zero;
        // An accepting BitTorrent peer reads the handshake before it writes; PeerSharp's accepted stream even rejects
        // writes until the peer's first data has arrived. So in listen mode the writer starts after the first read.
        var firstData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (mode != "listen" || send.Length == 0)
            firstData.TrySetResult();
        var writer = Task.Run(async () =>
        {
            await firstData.Task;
            for (var offset = 0; offset < send.Length; offset += chunk)
                await connection.WriteAsync(send.AsMemory(offset, Math.Min(chunk, send.Length - offset)));
            sendDone = clock.Elapsed;
        });

        var discard = recvFile == "/dev/null";
        using var output = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        double firstMbMs = -1;
        string end = "complete";
        while (total < expect)
        {
            var read = connection.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expect - total)));
            if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(30))) != read)
            {
                end = "stalled";
                break;
            }
            var n = await read;
            if (n <= 0)
            {
                end = "eof";
                break;
            }
            total += n;
            firstData.TrySetResult();
            hash.AppendData(buffer, 0, n);
            if (!discard)
                output.Write(buffer, 0, n);
            if (firstMbMs < 0 && total >= 1_000_000)
                firstMbMs = (clock.Elapsed - t0).TotalMilliseconds;
        }
        var recvSeconds = (clock.Elapsed - t0).TotalSeconds;
        var cpuRecv = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        var allocRecv = GC.GetTotalAllocatedBytes(true) - alloc0;

        firstData.TrySetResult();
        var writerDone = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(60))) == writer;
        var writerOk = writerDone && writer.Status == TaskStatus.RanToCompletion;
        var writerState = !writerDone ? "timeout" : writerOk ? "ok" :
            "\"" + writer.Exception?.GetBaseException().Message.Replace('"', '\'') + "\"";
        var drain = Stopwatch.StartNew();
        while (connection.BytesInFlight > 0 && drain.Elapsed < TimeSpan.FromSeconds(30))
            await Task.Delay(10);
        if (connection.BytesInFlight < 0)
            await Task.Delay(2000);   // no in-flight counter: give the last ACKs time instead
        var drained = connection.BytesInFlight <= 0;
        var bothSeconds = (clock.Elapsed - t0).TotalSeconds - (connection.BytesInFlight < 0 ? 2.0 : 0);
        if (!discard)
            File.WriteAllBytes(recvFile, output.ToArray());
        var mb = total / 1e6;
        Console.Error.WriteLine(
            $"RESULT cand={Adapter.Name} mode={mode} end={end} connect_ms={connectMs:0} recv={total} sent={send.Length} " +
            $"first_mb_ms={firstMbMs:0} recv_s={recvSeconds:0.000} recv_MBps={mb / recvSeconds:0.00} " +
            $"send_s={(writerOk ? (sendDone - t0).TotalSeconds : -1):0.000} writer={writerState} both_s={bothSeconds:0.000} drained={drained} " +
            $"cpu_ms_per_MB={cpuRecv / Math.Max(1e-9, mb):0.00} alloc_KB_per_MB={allocRecv / 1024.0 / Math.Max(1e-9, mb):0.0} " +
            $"recv_sha={Convert.ToHexString(hash.GetHashAndReset())[..16]} {connection.Counters()}");
        connection.Dispose();
        await Task.Delay(200);
        return end == "complete" && writerOk ? 0 : 3;
    }

    private static async Task RelayAsync(int listenPort, int targetPort, double loss, int jitterMs, double seconds, string statsFile)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, listenPort));
        udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        udp.Client.SendBufferSize = 4 * 1024 * 1024;
        var target = new IPEndPoint(IPAddress.Loopback, targetPort);
        IPEndPoint client = null;
        var random = new Random(7);
        long forwarded = 0, dropped = 0, bytesToTarget = 0, bytesToClient = 0, packetsToTarget = 0, packetsToClient = 0;
        var stop = Stopwatch.StartNew();
        var lastWrite = Stopwatch.StartNew();
        void Write() => File.WriteAllText(statsFile,
            $"relay_forwarded={forwarded} relay_dropped={dropped} wire_to_target={bytesToTarget} wire_to_client={bytesToClient} " +
            $"pkts_to_target={packetsToTarget} pkts_to_client={packetsToClient}\n");
        Task<UdpReceiveResult> receive = null;
        while (stop.Elapsed.TotalSeconds < seconds)
        {
            receive ??= udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(500)) != receive)
            {
                Write();
                continue;
            }
            var result = receive.Result;
            receive = null;
            IPEndPoint to;
            if (result.RemoteEndPoint.Equals(target))
            {
                to = client;
                bytesToClient += result.Buffer.Length;
                packetsToClient++;
            }
            else
            {
                client = result.RemoteEndPoint;
                to = target;
                bytesToTarget += result.Buffer.Length;
                packetsToTarget++;
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
            if (lastWrite.ElapsedMilliseconds > 500)
            {
                Write();
                lastWrite.Restart();
            }
        }
        Write();
    }
}
