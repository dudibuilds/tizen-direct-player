using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Ru7100Probe;
using Xunit;

namespace Ru7100.Tests.Playback
{
    [Collection("media-port")]
    public class MediaServerTests
    {
        // TorrentMediaServer listens on a fixed port with a unique path. Hedged startup opens several at once,
        // so two instances must serve ranges side by side.
        [Fact]
        public async Task TwoServersOnTheSharedPortServeRangesConcurrently()
        {
            var first = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
            var second = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 13)).ToArray();
            // A free port: 45550 is often held by a running harness or the preview on the dev Mac.
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            TorrentMediaServer.Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using (var a = new TorrentMediaServer(new MemoryStream(first), _ => true))
            using (var b = new TorrentMediaServer(new MemoryStream(second), _ => true))
            using (var http = new HttpClient())
            {
                async Task<byte[]> Range(string url, long from, long to)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(from, to);
                    using (var response = await http.SendAsync(request))
                    {
                        Assert.Equal(206, (int)response.StatusCode);
                        return await response.Content.ReadAsByteArrayAsync();
                    }
                }

                var results = await Task.WhenAll(Range(a.Url, 1000, 1999), Range(b.Url, 5000, 5999));
                Assert.Equal(first.Skip(1000).Take(1000), results[0]);
                Assert.Equal(second.Skip(5000).Take(1000), results[1]);
            }
        }
    }
}
