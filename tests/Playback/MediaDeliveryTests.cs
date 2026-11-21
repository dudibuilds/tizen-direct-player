using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Tests.Net;
using Ru7100Probe;
using Xunit;

namespace Ru7100.Tests.Playback
{
    /// Torrent stream delivery to AVPlay: range validation, eviction races, idle ranges and the download throttle.
    [Collection("media-port")]
    public class MediaDeliveryTests
    {
        private static TorrentMediaServer Serve(Stream stream, out int port, Action<long, bool> onRange = null)
        {
            port = MiniHttpServerTests.FreePort();
            TorrentMediaServer.Port = port;
            return new TorrentMediaServer(stream, _ => true, onRange);
        }

        [Fact]
        public async Task AnInvalidRangeDoesNotInterruptTheTransferThatIsPlaying()
        {
            var gate = new GatedStream(Enumerable.Range(0, 1_000_000).Select(i => (byte)i).ToArray());
            using (var media = Serve(gate, out var port))
            using (var http = new HttpClient())
            {
                var playing = new HttpRequestMessage(HttpMethod.Get, media.Url);
                playing.Headers.Range = new RangeHeaderValue(0, 199_999);
                var transfer = http.SendAsync(playing, HttpCompletionOption.ResponseContentRead);
                await gate.Waiting.Task;                                         // mid-transfer, waiting for data
                var bad = new HttpRequestMessage(HttpMethod.Get, media.Url);
                bad.Headers.TryAddWithoutValidation("Range", "bytes=5000000-");
                using (var rejected = await http.SendAsync(bad))
                    Assert.Equal(416, (int)rejected.StatusCode);
                gate.Release();
                using (var response = await transfer)
                {
                    Assert.Equal(206, (int)response.StatusCode);
                    Assert.Equal(200_000, (await response.Content.ReadAsByteArrayAsync()).Length);
                }
            }
        }

        [Fact]
        public async Task AReadMissAfterAnEvictionIsRetriedInsteadOfCuttingTheRange()
        {
            var data = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 199)).ToArray();
            var missing = new MissOnceStream(data, failAt: 131_072);
            using (var media = Serve(missing, out var port))
            using (var http = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, media.Url);
                request.Headers.Range = new RangeHeaderValue(0, 299_999);
                using (var response = await http.SendAsync(request))
                    Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());
                Assert.Equal(1, missing.Misses);
                Assert.Contains("read miss", media.Diagnostics);
            }
        }

        [Fact]
        public async Task ARangeWithNoDataForTooLongEndsAndFreesTheReadLock()
        {
            var previous = TorrentMediaServer.ReadIdleTimeout;
            TorrentMediaServer.ReadIdleTimeout = TimeSpan.FromMilliseconds(500);
            try
            {
                var gate = new GatedStream(new byte[100_000]);
                using (var media = Serve(gate, out var port))
                using (var http = new HttpClient())
                {
                    var stuck = new HttpRequestMessage(HttpMethod.Get, media.Url);
                    stuck.Headers.Range = new RangeHeaderValue(0, 99_999);
                    await Assert.ThrowsAnyAsync<Exception>(async () =>
                    {
                        using (var response = await http.SendAsync(stuck))
                            await response.Content.ReadAsByteArrayAsync();
                    });
                    Assert.Contains("idle", media.Diagnostics);
                }
            }
            finally { TorrentMediaServer.ReadIdleTimeout = previous; }
        }

        /// A paused AVPlay stops reading: the transfer waits in its write, which the idle deadline must not count.
        [Fact]
        public async Task AReaderPausedLongerThanTheIdleDeadlineStillGetsEveryByte()
        {
            var previous = TorrentMediaServer.ReadIdleTimeout;
            TorrentMediaServer.ReadIdleTimeout = TimeSpan.FromMilliseconds(300);
            try
            {
                var data = new byte[48 * 1024 * 1024];
                new Random(7).NextBytes(data);
                using (var media = Serve(new MemoryStream(data), out var port))
                using (var client = new TcpClient { ReceiveBufferSize = 16 * 1024 })
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    var stream = client.GetStream();
                    var request = Encoding.ASCII.GetBytes($"GET {new Uri(media.Url).AbsolutePath} HTTP/1.1\r\nHost: localhost\r\n\r\n");
                    await stream.WriteAsync(request, 0, request.Length);
                    var chunk = new byte[64 * 1024];
                    long total = await stream.ReadAsync(chunk, 0, chunk.Length);
                    await Task.Delay(1500);                                   // paused: five idle deadlines
                    int read;
                    while ((read = await stream.ReadAsync(chunk, 0, chunk.Length)) > 0)
                    {
                        total += read;
                        if (total >= data.Length + 150)
                            break;
                    }
                    Assert.True(total > data.Length, "received " + total + " of " + data.Length);
                    Assert.DoesNotContain("idle", media.Diagnostics);
                }
            }
            finally { TorrentMediaServer.ReadIdleTimeout = previous; }
        }

        [Fact]
        public async Task EveryRangeReportsItsStartToTheCache()
        {
            var starts = new System.Collections.Concurrent.ConcurrentQueue<long>();
            using (var media = Serve(new MemoryStream(new byte[50_000]), out var port, (start, cached) => starts.Enqueue(start)))
            using (var http = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, media.Url);
                request.Headers.Range = new RangeHeaderValue(40_000, 40_999);
                using (var response = await http.SendAsync(request))
                    await response.Content.ReadAsByteArrayAsync();
                Assert.Contains(40_000L, starts);
            }
        }

        // ---- the download throttle ----

        private const long Piece = 2 * 1024 * 1024;

        [Fact]
        public void AHoleAtTheCursorAlwaysLiftsTheThrottle()
        {
            Assert.False(CacheThrottle.Decide(throttled: true, ahead: 60, aheadSlots: 54, contiguous: 0, pieceLength: Piece));
            Assert.False(CacheThrottle.Decide(throttled: false, ahead: 60, aheadSlots: 54, contiguous: 0, pieceLength: Piece));
        }

        [Fact]
        public void SparseSlotsDoNotThrottle()
        {
            // Every slot ahead is allocated, but only 4 pieces in a row are there from the cursor.
            Assert.False(CacheThrottle.Decide(throttled: false, ahead: 54, aheadSlots: 54, contiguous: 4 * Piece, pieceLength: Piece));
        }

        [Fact]
        public void AFullContiguousBufferThrottlesUntilItDrainsToHalf()
        {
            Assert.True(CacheThrottle.Decide(throttled: false, ahead: 54, aheadSlots: 54, contiguous: 50 * Piece, pieceLength: Piece));
            Assert.True(CacheThrottle.Decide(throttled: true, ahead: 50, aheadSlots: 54, contiguous: 40 * Piece, pieceLength: Piece));
            Assert.False(CacheThrottle.Decide(throttled: true, ahead: 50, aheadSlots: 54, contiguous: 20 * Piece, pieceLength: Piece));
        }

        [Fact]
        public void UnknownContiguityKeepsTheSlotRule()
        {
            Assert.True(CacheThrottle.Decide(throttled: false, ahead: 54, aheadSlots: 54, contiguous: -1, pieceLength: Piece));
            Assert.False(CacheThrottle.Decide(throttled: true, ahead: 40, aheadSlots: 54, contiguous: -1, pieceLength: Piece));
        }

        /// Serves the first 64 KiB, then waits on Release (a piece that has not arrived).
        private sealed class GatedStream : MemoryStream
        {
            private readonly TaskCompletionSource<bool> released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public GatedStream(byte[] data) : base(data) { }
            public void Release() => released.TrySetResult(true);
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Position >= 64 * 1024 && !released.Task.IsCompleted)
                {
                    Waiting.TrySetResult(true);
                    var cancelled = new TaskCompletionSource<bool>();
                    using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                        await Task.WhenAny(released.Task, cancelled.Task);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return await base.ReadAsync(buffer, offset, count, cancellationToken);
            }
        }

        /// Throws once at an offset, as MonoTorrent's LocalStream does when storage misses a piece its bitfield claimed.
        private sealed class MissOnceStream : MemoryStream
        {
            private readonly long failAt;
            public int Misses;
            public MissOnceStream(byte[] data, long failAt) : base(data) { this.failAt = failAt; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (Misses == 0 && Position >= failAt)
                {
                    Misses++;
                    Position += 1000;   // a failed read may leave the cursor anywhere
                    throw new InvalidOperationException("Could not read the requested data from the torrent");
                }
                return base.ReadAsync(buffer, offset, count, cancellationToken);
            }
        }
    }
}
