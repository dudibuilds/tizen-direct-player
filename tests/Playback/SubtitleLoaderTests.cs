using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Net;
using Ru7100.Core.Playback;
using Xunit;

namespace Ru7100.Tests.Playback
{
    /// HttpSubtitleLoader: the whole download is bounded (HttpClient.Timeout alone stops at the headers) and the size
    /// cap applies while reading, not after buffering everything.
    public class SubtitleLoaderTests
    {
        private static MiniHttpServer Serve(MiniHttpHandler handler, out string url)
        {
            var server = new MiniHttpServer(new[] { (new IPEndPoint(IPAddress.Loopback, 0), true) }, handler,
                new MiniHttpOptions { WriteTimeout = null });
            server.Start();
            url = "http://127.0.0.1:" + server.BoundEndPoints[0].Port + "/subs.srt";
            return server;
        }

        [Fact]
        public async Task HeadersThenSilenceTimesOutOnTheWholeDownload()
        {
            using (var server = Serve(async (request, response) =>
            {
                response.ContentLength = 1000;
                await response.WriteAsync(Encoding.ASCII.GetBytes("1\n00:00:01"));
                await Task.Delay(Timeout.Infinite, response.Aborted).ContinueWith(_ => { });
            }, out var url))
            using (var loader = new HttpSubtitleLoader(total: TimeSpan.FromSeconds(1)))
            {
                var started = DateTime.UtcNow;
                await Assert.ThrowsAsync<TimeoutException>(() => loader.LoadTextAsync(url, CancellationToken.None));
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task AnOversizedBodyWithoutALengthIsCutOffWhileReading()
        {
            using (var server = Serve(async (request, response) =>
            {
                var chunk = new byte[64 * 1024];
                for (var i = 0; i < 200 && !response.Aborted.IsCancellationRequested; i++)
                    await response.WriteAsync(chunk);   // no Content-Length: the body ends at close
            }, out var url))
            using (var loader = new HttpSubtitleLoader(total: TimeSpan.FromSeconds(10)))
                await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadTextAsync(url, CancellationToken.None));
        }

        [Fact]
        public async Task AnOversizedLengthIsRejectedBeforeReading()
        {
            using (var server = Serve((request, response) =>
            {
                response.ContentLength = HttpSubtitleLoader.MaxBytes + 1;
                return Task.CompletedTask;   // short body: the server closes the connection
            }, out var url))
            using (var loader = new HttpSubtitleLoader(total: TimeSpan.FromSeconds(10)))
                await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadTextAsync(url, CancellationToken.None));
        }

        [Fact]
        public async Task ANormalFileLoads()
        {
            using (var server = Serve(async (request, response) =>
            {
                var body = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nHola\n");
                response.ContentLength = body.Length;
                await response.WriteAsync(body);
            }, out var url))
            using (var loader = new HttpSubtitleLoader())
                Assert.Contains("Hola", await loader.LoadTextAsync(url, CancellationToken.None));
        }
    }
}
