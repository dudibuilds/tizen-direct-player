using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Addons;

namespace Ru7100.Core.Tests.Addons
{
    /// Routes requests by unescaped absolute URL. No real network.
    internal sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> routes =
            new ConcurrentDictionary<string, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>(StringComparer.Ordinal);

        public ConcurrentQueue<string> Requests { get; } = new ConcurrentQueue<string>();

        public int CountOf(string url)
        {
            var count = 0;
            foreach (var item in Requests) if (item == url) count++;
            return count;
        }

        public FixtureHandler Json(string url, string body) =>
            Route(url, (request, token) => Task.FromResult(Response(HttpStatusCode.OK, body)));

        public FixtureHandler Fixture(string url, string fixtureName) => Json(url, Fixtures.Read(fixtureName));

        public FixtureHandler Status(string url, HttpStatusCode status) =>
            Route(url, (request, token) => Task.FromResult(Response(status, "{}")));

        /// Responds with each status/body in turn, repeating the last.
        public FixtureHandler Sequence(string url, params (HttpStatusCode Status, string Body)[] responses)
        {
            var index = -1;
            return Route(url, (request, token) =>
            {
                var i = Math.Min(Interlocked.Increment(ref index), responses.Length - 1);
                return Task.FromResult(Response(responses[i].Status, responses[i].Body));
            });
        }

        /// Never answers until the request is cancelled.
        public FixtureHandler Hang(string url) =>
            Route(url, async (request, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("unreachable");
            });

        public FixtureHandler Route(string url, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            routes[url] = handler;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = Uri.UnescapeDataString(request.RequestUri.AbsoluteUri);
            Requests.Enqueue(url);
            if (routes.TryGetValue(url, out var handler)) return handler(request, cancellationToken);
            return Task.FromResult(Response(HttpStatusCode.NotFound, "{\"error\":\"no fixture for " + url + "\"}"));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    internal sealed class MemoryStore : IKeyValueStore
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();

        public Task<string> ReadAsync(string key, CancellationToken token)
        {
            lock (Values) return Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        }

        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            lock (Values) Values[key] = value;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeClock
    {
        public DateTimeOffset Now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        public Func<DateTimeOffset> Func => () => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    internal static class Fixtures
    {
        public static string Read(string name, [CallerFilePath] string caller = "")
        {
            var directory = Path.Combine(Path.GetDirectoryName(caller), "Fixtures");
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                // Callers outside tests/Addons still resolve the folder next to this file.
                path = Path.Combine(Path.GetDirectoryName(Here()), "Fixtures", name);
            }
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static string Here([CallerFilePath] string path = "") => path;

        public static IReadOnlyList<AddonStreamItem> Streams(string name, AddonDescriptor addon = null) =>
            StreamRanking.ParseStreams(JObject.Parse(Read(name)), addon ?? TorrentioDescriptor());

        public static AddonHttp Http(FixtureHandler handler, List<TimeSpan> delays = null) =>
            new AddonHttp(handler, (wait, token) =>
            {
                delays?.Add(wait);
                return Task.CompletedTask;
            });

        public static AddonStreamItem Torrent(string title, string name = "Torrentio\n1080p", string hash = null, int? fileIdx = 0,
            params string[] sources) => new AddonStreamItem
            {
                AddonId = "com.stremio.torrentio.addon",
                Name = name,
                Title = title,
                InfoHash = hash ?? HashOf(title),
                FileIdx = fileIdx,
                Sources = sources,
            };

        public static string HashOf(string text)
        {
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var builder = new StringBuilder();
                foreach (var b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        public static string StreamsJson(params JObject[] streams) => new JObject { ["streams"] = new JArray(streams) }.ToString();

        public static JObject StreamJson(string title, string name = "Torrentio\n1080p", string hash = null, int fileIdx = 0) => new JObject
        {
            ["name"] = name,
            ["title"] = title,
            ["infoHash"] = hash ?? HashOf(title),
            ["fileIdx"] = fileIdx,
        };

        /// Descriptor matching the recorded Torrentio fixtures (the app itself ships no stream add-on).
        internal static AddonDescriptor TorrentioDescriptor() => new AddonDescriptor
        {
            TransportUrl = "https://torrentio.strem.fun/manifest.json",
            Id = "com.stremio.torrentio.addon",
            Name = "Torrentio",
            Resources = new[] { "stream" },
            Types = new[] { "movie", "series", "anime", "other" },
        };
    }
}
