using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Net;
using Ru7100Probe;
using Xunit;

namespace Ru7100.Tests.Net
{
    /// The managed HTTP server that replaced HttpListener on the TV (media for AVPlay, agent API, trailer manifests).
    [Collection("media-port")]
    public class MiniHttpServerTests
    {
        internal static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// Sends raw bytes and returns everything the server sent until it closed or went quiet.
        internal static async Task<string> RawAsync(int port, string request, int quietMs = 1500, IPAddress address = null)
        {
            address = address ?? IPAddress.Loopback;
            using (var client = new TcpClient(address.AddressFamily))
            {
                await client.ConnectAsync(address, port);
                var stream = client.GetStream();
                var bytes = Encoding.ASCII.GetBytes(request);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                return await ReadUntilQuietAsync(stream, quietMs);
            }
        }

        internal static async Task<string> ReadUntilQuietAsync(NetworkStream stream, int quietMs)
        {
            var output = new MemoryStream();
            var chunk = new byte[65536];
            while (true)
            {
                var read = stream.ReadAsync(chunk, 0, chunk.Length);
                if (await Task.WhenAny(read, Task.Delay(quietMs)) != read)
                {
                    _ = read.ContinueWith(t => { var _ = t.Exception; });   // the test's own abandoned read
                    break;
                }
                int count;
                try { count = await read; }
                catch (IOException) { break; }
                if (count <= 0)
                    break;
                output.Write(chunk, 0, count);
            }
            return Encoding.Latin1.GetString(output.ToArray());
        }

        /// Reads exactly one response (head plus Content-Length bytes), leaving the connection usable.
        private static async Task<string> ReadOneResponseAsync(NetworkStream stream)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            var one = new byte[1];
            while (bytes.Count < 4 || !(bytes[bytes.Count - 4] == 13 && bytes[bytes.Count - 3] == 10 && bytes[bytes.Count - 2] == 13 && bytes[bytes.Count - 1] == 10))
            {
                if (await stream.ReadAsync(one, 0, 1) <= 0)
                    break;
                bytes.Add(one[0]);
            }
            var head = Encoding.Latin1.GetString(bytes.ToArray());
            var match = System.Text.RegularExpressions.Regex.Match(head, "Content-Length: (\\d+)");
            var length = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            var body = new byte[length];
            for (var read = 0; read < length;)
                read += await stream.ReadAsync(body, read, length - read);
            return head + Encoding.Latin1.GetString(body);
        }

        private static string Head(string response) => response.Substring(0, Math.Max(0, response.IndexOf("\r\n\r\n", StringComparison.Ordinal)));

        private static string[] HeaderNames(string response) =>
            Head(response).Split(new[] { "\r\n" }, StringSplitOptions.None).Skip(1).Select(l => l.Substring(0, l.IndexOf(':'))).ToArray();

        // ---- media server: the headers AVPlay has always seen from HttpListener ----

        [Fact]
        public async Task MediaServerSendsTheSameHeadersAsHttpListener()
        {
            var port = FreePort();
            TorrentMediaServer.Port = port;
            var data = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
            using (var media = new TorrentMediaServer(new MemoryStream(data), _ => true))
            {
                var path = new Uri(media.Url).AbsolutePath;
                Assert.StartsWith("http://localhost:" + port + "/media/", media.Url);

                var head = await RawAsync(port, $"HEAD {path} HTTP/1.1\r\nHost: localhost\r\n\r\n", 500);
                Assert.StartsWith("HTTP/1.1 200 OK\r\n", head);
                Assert.Equal(new[] { "Content-Type", "Accept-Ranges", "Server", "Date", "Content-Length" }, HeaderNames(head));
                Assert.Contains("Content-Type: video/mp4\r\n", head);
                Assert.Contains("Server: Microsoft-NetCore/2.0\r\n", head);
                Assert.Contains("Content-Length: 300000\r\n", head);
                Assert.EndsWith("\r\n\r\n", head);   // no body

                var range = await RawAsync(port, $"GET {path} HTTP/1.1\r\nHost: localhost\r\nRange: bytes=100-199\r\n\r\n", 500);
                Assert.StartsWith("HTTP/1.1 206 Partial Content\r\n", range);
                Assert.Equal(new[] { "Content-Type", "Accept-Ranges", "Content-Range", "Server", "Date", "Content-Length" }, HeaderNames(range));
                Assert.Contains("Content-Range: bytes 100-199/300000\r\n", range);
                Assert.Contains("Content-Length: 100\r\n", range);
                var body = range.Substring(range.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
                Assert.Equal(data.Skip(100).Take(100).Select(b => (char)b), body.Select(c => c));

                var suffix = await RawAsync(port, $"GET {path} HTTP/1.1\r\nHost: localhost\r\nRange: bytes=-100\r\n\r\n", 500);
                Assert.Contains("Content-Range: bytes 299900-299999/300000\r\n", suffix);

                var unsatisfiable = await RawAsync(port, $"GET {path} HTTP/1.1\r\nHost: localhost\r\nRange: bytes=500000-\r\n\r\n", 500);
                Assert.StartsWith("HTTP/1.1 416 Requested Range Not Satisfiable\r\n", unsatisfiable);
                Assert.Contains("Content-Range: bytes */300000\r\n", unsatisfiable);

                Assert.StartsWith("HTTP/1.1 405 ", await RawAsync(port, $"POST {path} HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n", 500));
                Assert.StartsWith("HTTP/1.1 404 ", await RawAsync(port, "GET /media/unknown/ HTTP/1.1\r\nHost: localhost\r\n\r\n", 500));
            }
        }

        [Fact]
        public async Task MediaServerAnswersOnIpv6LoopbackToo()
        {
            if (!Socket.OSSupportsIPv6)
                return;
            var port = FreePort();
            TorrentMediaServer.Port = port;
            using (var media = new TorrentMediaServer(new MemoryStream(new byte[1000]), _ => true))
            {
                var response = await RawAsync(port, $"GET {new Uri(media.Url).AbsolutePath} HTTP/1.1\r\nHost: localhost\r\nRange: bytes=0-9\r\n\r\n", 500, IPAddress.IPv6Loopback);
                Assert.StartsWith("HTTP/1.1 206 ", response);
            }
        }

        [Fact]
        public async Task KeepAliveServesSeveralRangesOnOneConnection()
        {
            var port = FreePort();
            TorrentMediaServer.Port = port;
            var data = Enumerable.Range(0, 10_000).Select(i => (byte)(i % 251)).ToArray();
            using (var media = new TorrentMediaServer(new MemoryStream(data), _ => true))
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var path = new Uri(media.Url).AbsolutePath;
                for (var i = 0; i < 3; i++)
                {
                    var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nRange: bytes={i * 10}-{i * 10 + 9}\r\n\r\n");
                    await stream.WriteAsync(request, 0, request.Length);
                    var response = await ReadOneResponseAsync(stream);
                    Assert.StartsWith("HTTP/1.1 206 ", response);
                    Assert.DoesNotContain("Connection: close", response);
                }
            }
        }

        /// A range waiting for a piece that never arrives must end when AVPlay drops the connection, so the read lock
        /// is free for the next range (HttpListener only noticed on the next GET).
        [Fact]
        public async Task ClientClosingEndsATransferWaitingForData()
        {
            var port = FreePort();
            TorrentMediaServer.Port = port;
            var blocking = new BlockingStream(1_000_000);
            using (var media = new TorrentMediaServer(blocking, _ => false))
            {
                var path = new Uri(media.Url).AbsolutePath;
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    var request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nRange: bytes=0-99\r\n\r\n");
                    await client.GetStream().WriteAsync(request, 0, request.Length);
                    await blocking.ReadStarted.Task.WithTimeout(5000);
                }
                // The read is cancelled through the response's Aborted token.
                await blocking.ReadCancelled.Task.WithTimeout(5000);
            }
        }

        // ---- generic server behaviour ----

        private static MiniHttpServer Start(MiniHttpHandler handler, MiniHttpOptions options, out int port, ConcurrentQueue<string> log = null)
        {
            var server = new MiniHttpServer(new[] { (new IPEndPoint(IPAddress.Loopback, 0), true) }, handler, options,
                line => log?.Enqueue(line));
            server.Start();
            port = server.BoundEndPoints[0].Port;
            return server;
        }

        private static Task Ok(MiniHttpRequest request, MiniHttpResponse response)
        {
            var body = Encoding.ASCII.GetBytes("ok");
            response.ContentLength = body.Length;
            return response.WriteAsync(body);
        }

        [Fact]
        public async Task SurvivesGarbageResetsAndRapidConnects()
        {
            var unobserved = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> watch = (s, e) => Interlocked.Increment(ref unobserved);
            TaskScheduler.UnobservedTaskException += watch;
            try
            {
                var thrower = 0;
                using (var server = Start((request, response) =>
                {
                    if (request.Path == "/throw")
                    {
                        Interlocked.Increment(ref thrower);
                        throw new ArgumentNullException("boom");
                    }
                    return Ok(request, response);
                }, new MiniHttpOptions { HeaderTimeout = TimeSpan.FromMilliseconds(500), IdleTimeout = TimeSpan.FromMilliseconds(500) }, out var port))
                {
                    var tasks = Enumerable.Range(0, 60).Select(async i =>
                    {
                        using (var client = new TcpClient())
                        {
                            await client.ConnectAsync(IPAddress.Loopback, port);
                            var stream = client.GetStream();
                            string text;
                            switch (i % 6)
                            {
                                case 0: return;   // connect and leave
                                case 1: text = "GARBAGE\r\n\r\n"; break;
                                case 2: text = "GET / HTTP/1.1\r\nHost: x\r\n"; break;   // reset mid-header
                                case 3: text = "POST / HTTP/1.1\r\nContent-Length: 100\r\n\r\nabc"; break;   // reset mid-body
                                case 4: text = "GET /throw HTTP/1.1\r\n\r\n"; break;
                                default: text = new string('x', 40000); break;   // oversized head
                            }
                            var bytes = Encoding.ASCII.GetBytes(text);
                            try { await stream.WriteAsync(bytes, 0, bytes.Length); } catch (IOException) { }
                            client.Client.LingerState = new LingerOption(true, 0);   // RST on close
                        }
                    });
                    await Task.WhenAll(tasks);
                    var after = await RawAsync(port, "GET / HTTP/1.1\r\n\r\n", 500);
                    Assert.StartsWith("HTTP/1.1 200 OK\r\n", after);
                    Assert.EndsWith("ok", after);
                    Assert.Equal(500, int.Parse(Head(await RawAsync(port, "GET /throw HTTP/1.1\r\n\r\n", 500)).Split(' ')[1]));
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.Equal(0, unobserved);
            }
            finally { TaskScheduler.UnobservedTaskException -= watch; }
        }

        [Fact]
        public async Task StalledHeadersAreClosedByTheWatchdog()
        {
            var log = new ConcurrentQueue<string>();
            using (var server = Start(Ok, new MiniHttpOptions { HeaderTimeout = TimeSpan.FromMilliseconds(400) }, out var port, log))
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var partial = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n");
                await stream.WriteAsync(partial, 0, partial.Length);
                var read = stream.ReadAsync(new byte[16], 0, 16);
                Assert.Same(read, await Task.WhenAny(read, Task.Delay(5000)));
                Assert.True(await read.ContinueWith(t => t.IsFaulted || t.Result == 0));
                Assert.Equal(1, server.Stats.Timeouts);
            }
        }

        [Fact]
        public async Task SlowBodyIsBoundedByTheBodyDeadline()
        {
            using (var server = Start(async (request, response) =>
            {
                byte[] body;
                try { body = await request.ReadBodyAsync(1000); }
                catch (Exception) { response.StatusCode = 400; return; }
                response.ContentLength = 0;
            }, new MiniHttpOptions { BodyTimeout = TimeSpan.FromMilliseconds(400) }, out var port))
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();
                var partial = Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nContent-Length: 100\r\n\r\n0123456789");
                await stream.WriteAsync(partial, 0, partial.Length);
                var started = DateTime.UtcNow;
                await ReadUntilQuietAsync(stream, 3000);
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "the body deadline closed the connection");
            }
        }

        [Fact]
        public async Task OversizedBodyGetsItsResponseInsteadOfAReset()
        {
            using (var server = Start(async (request, response) =>
            {
                var body = await request.ReadBodyAsync(100);
                response.StatusCode = body == null ? 413 : 200;
                response.ContentLength = 0;
            }, new MiniHttpOptions(), out var port))
            {
                var response = await RawAsync(port, "POST / HTTP/1.1\r\nContent-Length: 50000\r\n\r\n" + new string('a', 50000), 1000);
                Assert.StartsWith("HTTP/1.1 413 ", response);
            }
        }

        [Fact]
        public async Task ChunkedAndExpectContinueBodiesAreRead()
        {
            using (var server = Start(async (request, response) =>
            {
                var body = await request.ReadBodyAsync(1000);
                response.ContentLength = body.Length;
                await response.WriteAsync(body);
            }, new MiniHttpOptions(), out var port))
            {
                var chunked = await RawAsync(port, "POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", 500);
                Assert.EndsWith("hello world", chunked);
                using (var http = new HttpClient())
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/") { Content = new StringContent("ping") };
                    message.Headers.ExpectContinue = true;
                    using (var answer = await http.SendAsync(message))
                        Assert.Equal("ping", await answer.Content.ReadAsStringAsync());
                }
            }
        }

        [Fact]
        public async Task ConnectionsBeyondTheCapGet503()
        {
            var release = new TaskCompletionSource<bool>();
            using (var server = Start(async (request, response) =>
            {
                await release.Task;
                await Ok(request, response);
            }, new MiniHttpOptions { MaxConnections = 2 }, out var port))
            {
                var held = Enumerable.Range(0, 2).Select(_ => RawAsync(port, "GET / HTTP/1.1\r\n\r\n", 4000)).ToList();
                await Task.Delay(300);
                var rejected = await RawAsync(port, "GET / HTTP/1.1\r\n\r\n", 1000);
                Assert.StartsWith("HTTP/1.1 503 ", rejected);
                release.SetResult(true);
                foreach (var response in await Task.WhenAll(held))
                    Assert.StartsWith("HTTP/1.1 200 ", response);
            }
        }

        [Fact]
        public async Task DisposeEndsOpenConnectionsAndTheAcceptLoop()
        {
            var entered = new TaskCompletionSource<bool>();
            var aborted = new TaskCompletionSource<bool>();
            var server = Start(async (request, response) =>
            {
                response.Aborted.Register(() => aborted.TrySetResult(true));
                entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, response.Aborted).ContinueWith(_ => { });
            }, new MiniHttpOptions(), out var port);
            var pending = RawAsync(port, "GET / HTTP/1.1\r\n\r\n", 5000);
            await entered.Task.WithTimeout(5000);
            server.Dispose();
            await aborted.Task.WithTimeout(5000);
            await pending.WithTimeout(10000);
            await Assert.ThrowsAnyAsync<SocketException>(async () =>
            {
                using (var client = new TcpClient())
                    await client.ConnectAsync(IPAddress.Loopback, port);
            });
        }

        [Theory]
        [InlineData("http://127.0.0.1:45551/", "127.0.0.1", 45551, 1)]
        [InlineData("http://+:8080/", "0.0.0.0", 8080, 1)]
        [InlineData("http://*:8080/", "0.0.0.0", 8080, 1)]
        [InlineData("http://localhost:9000/", "127.0.0.1", 9000, 2)]
        [InlineData("http://[::1]:9001/", "::1", 9001, 1)]
        public void PrefixesMapToEndpoints(string prefix, string address, int port, int count)
        {
            var endpoints = MiniHttpServer.FromPrefix(prefix).ToList();
            Assert.Equal(count, endpoints.Count);
            Assert.Equal(IPAddress.Parse(address), endpoints[0].Item1.Address);
            Assert.Equal(port, endpoints[0].Item1.Port);
            Assert.True(endpoints[0].Item2);
        }

        /// A stream whose reads wait until cancelled (a piece that never arrives).
        private sealed class BlockingStream : Stream
        {
            private readonly long length;
            public readonly TaskCompletionSource<bool> ReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReadCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public BlockingStream(long length) { this.length = length; }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => length;
            public override long Position { get; set; }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ReadStarted.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { ReadCancelled.TrySetResult(true); throw; }
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => Position = offset;
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    internal static class TaskTimeouts
    {
        public static async Task WithTimeout(this Task task, int ms)
        {
            if (await Task.WhenAny(task, Task.Delay(ms)) != task)
                throw new TimeoutException("timed out after " + ms + " ms");
            await task;
        }
    }
}
