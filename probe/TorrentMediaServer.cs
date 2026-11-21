using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Net;

namespace Ru7100Probe
{
    /// Serves one torrent file to AVPlay over loopback (GET/HEAD with byte ranges). All instances share one
    /// process-long endpoint per port (MediaEndpoint) and are told apart by the path /media/<id>/.
    internal sealed class TorrentMediaServer : IDisposable
    {
        private readonly SemaphoreSlim readLock = new SemaphoreSlim(1);
        private readonly Stream stream;
        private readonly Func<long, bool> isCached;
        private readonly ConcurrentQueue<string> recentRanges = new ConcurrentQueue<string>();
        private readonly object requestGate = new object();
        private readonly string id;
        private readonly Action<long, bool> onRange;
        private CancellationTokenSource currentRequest;
        private long rangeCounter;
        private long lastUncachedRange = -1;
        private int disposed;

        public long LastUncachedRange => Interlocked.Read(ref lastUncachedRange);
        public string Diagnostics => string.Join("\n", recentRanges.ToArray());

        public string Url { get; }

        /// No byte delivered for this long while a range waits for data: the transfer ends (the connection closes), so a
        /// dead piece never holds the read lock indefinitely. Health recovery normally acts well before this.
        public static TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromSeconds(90);
        /// Short reads after an eviction raced the bitfield (MonoTorrent throws): retried this many times per range.
        public const int ReadMissRetries = 8;

        /// Loopback port of the media server: 45550 on the TV. A second process on the same machine cannot bind it, so dev
        /// hosts running side by side set RU7100_MEDIA_PORT, and the Mac harness may set Port directly.
        private static int? port;
        public static int Port
        {
            get
            {
                if (port.HasValue) return port.Value;
                var value = Environment.GetEnvironmentVariable("RU7100_MEDIA_PORT");
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 1024 && p < 65536 ? p : 45550;
            }
            set => port = value;
        }

        /// onRange(start, cached): called when a range starts, before it waits for data (the cache moves its
        /// playhead and lifts a download throttle at once instead of on its next once-a-second pass).
        public TorrentMediaServer(Stream stream, Func<long, bool> isCached, Action<long, bool> onRange = null)
        {
            this.stream = stream;
            this.isCached = isCached;
            this.onRange = onRange;
            id = Guid.NewGuid().ToString("N");
            var listenPort = Port;
            // AVPlay has always opened "localhost"; the endpoint listens on 127.0.0.1 and, where available, ::1.
            Url = "http://localhost:" + listenPort.ToString(CultureInfo.InvariantCulture) + "/media/" + id + "/";
            MediaEndpoint.Register(listenPort, id, this);
        }

        /// One request routed here by MediaEndpoint. Runs on its connection's task; faults are caught there.
        internal async Task HandleAsync(MiniHttpRequest request, MiniHttpResponse response)
        {
            if (request.Method == "HEAD")
            {
                response.ContentType = "video/mp4";
                response.SetHeader("Accept-Ranges", "bytes");
                response.ContentLength = stream.Length;
                return;
            }
            if (request.Method != "GET")
            {
                response.StatusCode = 405;
                return;
            }

            // Validate first: an unsatisfiable or malformed range must not interrupt the transfer that is playing.
            var length = stream.Length;
            var range = request.Header("Range");
            if (!TryRange(range, length, out var start, out var end))
            {
                response.StatusCode = 416;
                response.SetHeader("Content-Range", $"bytes */{length}");
                Trace($"range={range} rejected (416)");
                return;
            }
            // The newest valid range takes over the shared stream (AVPlay's seeks open a new range; the old one would
            // otherwise hold the read lock).
            var transfer = new CancellationTokenSource();
            lock (requestGate)
            {
                currentRequest?.Cancel();
                currentRequest = transfer;
            }
            await SendAsync(response, transfer, range != null, length, start, end).ConfigureAwait(false);
        }

        private async Task SendAsync(MiniHttpResponse response, CancellationTokenSource transfer, bool partial, long length, long start, long end)
        {
            var rangeId = Interlocked.Increment(ref rangeCounter);
            long delivered = 0;
            string outcome = "done";
            try
            {
                // A newer GET preempts this one (as HttpListener's response.Abort did), and a client that left ends it.
                using var idle = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(transfer.Token, response.Aborted, idle.Token);
                var cancellation = linked.Token;
                using var abort = transfer.Token.Register(() => response.Abort("preempted by a newer range"));
                try
                {
                    response.StatusCode = partial ? 206 : 200;
                    response.ContentType = "video/mp4";
                    response.SetHeader("Accept-Ranges", "bytes");
                    if (partial)
                        response.SetHeader("Content-Range", $"bytes {start}-{end}/{length}");
                    response.ContentLength = end - start + 1;

                    await readLock.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var cached = isCached(start);
                        Trace($"range#{rangeId}={start}-{end} cached={cached}");
                        if (!cached)
                            Interlocked.Exchange(ref lastUncachedRange, start);
                        try { onRange?.Invoke(start, cached); }
                        catch (Exception error) { Trace("range hook: " + error.Message); }
                        var buffer = new byte[64 * 1024];
                        var total = end - start + 1;
                        var misses = 0;
                        stream.Seek(start, SeekOrigin.Begin);
                        while (delivered < total)
                        {
                            idle.CancelAfter(ReadIdleTimeout);
                            int count;
                            try
                            {
                                count = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, total - delivered), cancellation)
                                    .ConfigureAwait(false);
                            }
                            catch (InvalidOperationException error) when (misses < ReadMissRetries && !cancellation.IsCancellationRequested)
                            {
                                // The piece was evicted after the engine's bitfield said it was there: its bit is being
                                // cleared, so the next read waits for it to download again instead of cutting the range.
                                misses++;
                                Trace($"range#{rangeId} read miss at {start + delivered} ({error.Message}); retry {misses}");
                                await Task.Delay(250, cancellation).ConfigureAwait(false);
                                stream.Seek(start + delivered, SeekOrigin.Begin);
                                continue;
                            }
                            if (count == 0)
                                break;
                            cancellation.ThrowIfCancellationRequested();
                            // The idle deadline covers waiting for data only: a paused AVPlay stops reading and this
                            // write may block for as long as the viewer pauses.
                            idle.CancelAfter(Timeout.Infinite);
                            await response.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                            if (delivered == 0)
                                Trace($"range#{rangeId}={start} firstBytes={count}");
                            delivered += count;
                        }
                        if (delivered < total)
                            outcome = "short";
                    }
                    finally { readLock.Release(); }
                }
                catch (OperationCanceledException)
                {
                    outcome = idle.IsCancellationRequested ? "idle " + ReadIdleTimeout.TotalSeconds + "s" : transfer.IsCancellationRequested ? "preempted" : (response.CloseReason ?? "cancelled");
                    response.Abort(outcome);
                }
                catch (IOException) { outcome = "io: " + (response.CloseReason ?? "write failed"); response.Abort("io"); }
                catch (System.Net.Sockets.SocketException) { outcome = "socket"; response.Abort("socket"); }
                catch (ObjectDisposedException) { outcome = "disposed"; response.Abort("disposed"); }
                catch (InvalidOperationException error) { outcome = "invalid: " + error.Message; response.Abort("invalid"); }
            }
            finally
            {
                lock (requestGate)
                {
                    if (currentRequest == transfer)
                        currentRequest = null;
                    transfer.Dispose();
                }
                Trace($"range#{rangeId} end {outcome} bytes={delivered}");
                Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("http", $"range#{rangeId} {start}-{end} {outcome} bytes={delivered}");
            }
        }

        public void Trace(string message)
        {
            recentRanges.Enqueue($"{DateTimeOffset.UtcNow:O} {message}");
            while (recentRanges.Count > 30)
                recentRanges.TryDequeue(out _);
        }

        private static bool TryRange(string header, long length, out long start, out long end)
        {
            start = 0;
            end = length - 1;
            if (header == null)
                return length > 0;
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                return false;

            var parts = header.Substring(6).Split('-');
            if (parts.Length != 2)
                return false;
            if (parts[0].Length == 0)
            {
                if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                    return false;
                start = Math.Max(0, length - suffix);
                return length > 0;
            }

            if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= length)
                return false;
            if (parts[1].Length > 0 &&
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end))
                return false;

            end = Math.Min(end, length - 1);
            return end >= start;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1)
                return;
            MediaEndpoint.Unregister(id, this);
            lock (requestGate)
                currentRequest?.Cancel();
            stream.Dispose();
        }
    }

    /// The process-long loopback HTTP endpoint behind every TorrentMediaServer, one per port. Created on first use and
    /// kept for the life of the process, so source handoffs never start or stop a listener. A failed bind is not
    /// cached: the next open tries again.
    internal static class MediaEndpoint
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<int, MiniHttpServer> Servers = new Dictionary<int, MiniHttpServer>();
        private static readonly ConcurrentDictionary<string, TorrentMediaServer> Routes = new ConcurrentDictionary<string, TorrentMediaServer>();

        /// Listener lifecycle and connection faults (the TV app routes it to its trace).
        public static Action<string> Log;

        public static MiniHttpStats StatsOf(int port)
        {
            lock (Gate)
                return Servers.TryGetValue(port, out var server) ? server.Stats : null;
        }

        public static void Register(int port, string id, TorrentMediaServer server)
        {
            Routes[id] = server;
            try { Ensure(port); }
            catch
            {
                Routes.TryRemove(id, out _);
                throw;
            }
        }

        public static void Unregister(string id, TorrentMediaServer server)
        {
            if (Routes.TryGetValue(id, out var current) && ReferenceEquals(current, server))
                Routes.TryRemove(id, out _);
        }

        private static void Ensure(int port)
        {
            lock (Gate)
            {
                if (Servers.ContainsKey(port))
                    return;
                var options = new MiniHttpOptions
                {
                    Name = "media:" + port.ToString(CultureInfo.InvariantCulture),
                    // AVPlay stops reading while the viewer pauses, so a stalled write is never a timeout here; the next
                    // range preempting it or the client closing ends a transfer instead.
                    WriteTimeout = null,
                    IdleTimeout = TimeSpan.FromSeconds(120),
                    MaxConnections = 48,
                };
                var server = new MiniHttpServer(MiniHttpServer.Loopback(port), Route, options, line => Log?.Invoke(line));
                server.Start();
                Servers[port] = server;
            }
        }

        private static Task Route(MiniHttpRequest request, MiniHttpResponse response)
        {
            // /media/<id>/ (anything after the id's slash is ignored, as the HttpListener prefix did)
            var path = request.Path;
            const string prefix = "/media/";
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var slash = path.IndexOf('/', prefix.Length);
                if (slash > prefix.Length && Routes.TryGetValue(path.Substring(prefix.Length, slash - prefix.Length), out var server))
                    return server.HandleAsync(request, response);
            }
            response.StatusCode = 404;
            return Task.CompletedTask;
        }
    }
}
