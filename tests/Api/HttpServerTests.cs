using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Api;
using Xunit;

namespace Ru7100.Tests.Api
{
    /// One listener per class: HttpListener.Prefixes.Add can take seconds on macOS (host name lookup).
    public sealed class HttpServerFixture : IDisposable
    {
        public static readonly byte[] AdminSecret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        public static readonly byte[] PlaybackSecret = Enumerable.Range(50, 32).Select(i => (byte)i).ToArray();
        public readonly ApiHttpServer Server;
        public readonly HttpClient Client = new HttpClient(new HttpClientHandler { UseProxy = false });
        public readonly string BaseUrl;

        public HttpServerFixture()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            BaseUrl = "http://127.0.0.1:" + port;
            var auth = new ApiAuthenticator(new[]
            {
                new ApiKey("admin", AdminSecret, new[] { ApiScopes.Admin }),
                new ApiKey("player", PlaybackSecret, new[] { ApiScopes.Playback }),
            });
            Server = new ApiHttpServer(new ApiFixture().Router, auth, new[] { BaseUrl + "/" }, maxBodyBytes: 4096);
            Server.Start();
        }

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
        }
    }

    public sealed class HttpServerTests : IClassFixture<HttpServerFixture>
    {
        private static readonly byte[] AdminSecret = HttpServerFixture.AdminSecret;
        private static readonly byte[] PlaybackSecret = HttpServerFixture.PlaybackSecret;
        private readonly HttpClient client;
        private readonly string baseUrl;

        public HttpServerTests(HttpServerFixture fixture)
        {
            client = fixture.Client;
            baseUrl = fixture.BaseUrl;
        }

        private HttpRequestMessage Signed(HttpMethod method, string path, string body, string keyId = "admin", byte[] secret = null, string nonce = null)
        {
            var bytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            nonce = nonce ?? Guid.NewGuid().ToString("N");
            var message = new HttpRequestMessage(method, baseUrl + path);
            if (body != null) message.Content = new ByteArrayContent(bytes);
            message.Headers.Add(ApiHeaders.Key, keyId);
            message.Headers.Add(ApiHeaders.Time, time);
            message.Headers.Add(ApiHeaders.Nonce, nonce);
            message.Headers.Add(ApiHeaders.Signature, ApiAuthenticator.Sign(secret ?? AdminSecret, method.Method, path, time, nonce, bytes));
            return message;
        }

        private async Task<(int Status, JObject Body)> Send(HttpRequestMessage message)
        {
            using (var response = await client.SendAsync(message))
                return ((int)response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));
        }

        [Fact]
        public async Task SignedCommandSucceeds()
        {
            var (status, body) = await Send(Signed(HttpMethod.Post, "/api", "{\"command\":\"library.search\",\"args\":{\"query\":\"Sintel\"}}"));
            Assert.Equal(200, status);
            Assert.Equal("Sintel", (string)body["result"][0]["name"]);
        }

        [Fact]
        public async Task SignedStatusGetSucceeds()
        {
            var (status, body) = await Send(Signed(HttpMethod.Get, "/api/status", null));
            Assert.Equal(200, status);
            Assert.False((bool)body["result"]["playback"]["active"]);
        }

        [Fact]
        public async Task UnsignedRequestIs401()
        {
            var message = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api") { Content = new StringContent("{\"command\":\"status\"}") };
            var (status, body) = await Send(message);
            Assert.Equal(401, status);
            Assert.Equal("unauthorized", (string)body["error"]["code"]);
        }

        [Fact]
        public async Task TamperedBodyIs401()
        {
            var message = Signed(HttpMethod.Post, "/api", "{\"command\":\"status\"}");
            message.Content = new StringContent("{\"command\":\"playback.stop\"}");
            Assert.Equal(401, (await Send(message)).Status);
        }

        [Fact]
        public async Task ReplayedRequestIs401()
        {
            const string nonce = "fixednonce0123456789";
            Assert.Equal(200, (await Send(Signed(HttpMethod.Post, "/api", "{\"command\":\"status\"}", nonce: nonce))).Status);
            Assert.Equal(401, (await Send(Signed(HttpMethod.Post, "/api", "{\"command\":\"status\"}", nonce: nonce))).Status);
        }

        [Fact]
        public async Task ScopeDenialIs403()
        {
            var (status, body) = await Send(Signed(HttpMethod.Post, "/api", "{\"command\":\"addons.install\",\"args\":{\"manifestUrl\":\"https://a.example/manifest.json\"}}", "player", PlaybackSecret));
            Assert.Equal(403, status);
            Assert.Equal("forbidden", (string)body["error"]["code"]);
        }

        [Fact]
        public async Task ValidationErrorIs400()
        {
            var (status, body) = await Send(Signed(HttpMethod.Post, "/api", "{\"command\":\"playback.play\",\"args\":{}}", "player", PlaybackSecret));
            Assert.Equal(400, status);
            Assert.Equal("missing_argument", (string)body["error"]["code"]);
        }

        [Fact]
        public async Task UnknownRouteIs404()
        {
            Assert.Equal(404, (await Send(Signed(HttpMethod.Get, "/status", null))).Status);
            Assert.Equal(404, (await Send(Signed(HttpMethod.Get, "/api", null))).Status);
        }

        [Fact]
        public async Task OversizedBodyIs413()
        {
            var body = "{\"command\":\"status\",\"args\":{\"scope\":\"" + new string('a', 5000) + "\"}}";
            Assert.Equal(413, (await Send(Signed(HttpMethod.Post, "/api", body))).Status);
        }
    }
}
