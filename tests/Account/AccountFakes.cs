using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Account;

namespace Ru7100.Core.Tests.Account
{
    sealed class MemoryStore : IKeyValueStore
    {
        public readonly ConcurrentDictionary<string, string> Values = new ConcurrentDictionary<string, string>();

        public Task<string> ReadAsync(string key, CancellationToken token)
        {
            Values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public JToken Json(string key) =>
            Values.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)
                ? JsonConvert.DeserializeObject<JToken>(v, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })
                : null;
    }

    sealed class RecordedRequest
    {
        public HttpMethod Method;
        public string Url;
        public string Body;
        public Dictionary<string, string> Headers;
        public JObject Json => string.IsNullOrEmpty(Body) ? null : JObject.Parse(Body);
    }

    /// Fixture HttpMessageHandler: routes are matched in order by method and URL prefix. No network.
    sealed class FixtureHandler : HttpMessageHandler
    {
        readonly List<(HttpMethod Method, string Prefix, Func<RecordedRequest, HttpResponseMessage> Respond)> routes =
            new List<(HttpMethod, string, Func<RecordedRequest, HttpResponseMessage>)>();
        public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();
        public bool Offline;

        public FixtureHandler On(HttpMethod method, string prefix, Func<RecordedRequest, HttpResponseMessage> respond)
        {
            routes.Add((method, prefix, respond));
            return this;
        }

        public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var text = body is string s ? s : JsonConvert.SerializeObject(body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Url = request.RequestUri.ToString(),
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(),
                Headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value)),
            };
            lock (Requests) Requests.Add(recorded);
            if (Offline) throw new HttpRequestException("network unreachable");
            foreach (var route in routes)
            {
                if (route.Method == request.Method && recorded.Url.StartsWith(route.Prefix, StringComparison.Ordinal))
                    return route.Respond(recorded);
            }
            return Json(new { error = "no fixture for " + recorded.Url }, HttpStatusCode.NotFound);
        }
    }

    /// In-memory api.strem.io datastore behind the fixture handler.
    sealed class FakeStremioServer
    {
        public const string Api = "https://api.test/api";
        public const string Link = "https://link.test/api/v2";
        public readonly FixtureHandler Handler = new FixtureHandler();
        public readonly Dictionary<string, JObject> Items = new Dictionary<string, JObject>();
        public readonly List<JObject> Puts = new List<JObject>();
        public string AuthKey = "key-1";
        public Func<RecordedRequest, HttpResponseMessage> Override;

        public FakeStremioServer()
        {
            Handler.On(HttpMethod.Post, Api + "/", Respond);
        }

        HttpResponseMessage Respond(RecordedRequest request)
        {
            if (Override != null)
            {
                var overridden = Override(request);
                if (overridden != null) return overridden;
            }
            var method = request.Url.Substring((Api + "/").Length);
            var body = JsonConvert.DeserializeObject<JObject>(request.Body, new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            if ((string)body["authKey"] != AuthKey) return FixtureHandler.Json(new { error = new { message = "Session does not exist", code = 1 } });
            switch (method)
            {
                case "datastoreGet":
                {
                    var ids = ((JArray)body["ids"]).Select(i => (string)i).ToList();
                    var all = (bool)body["all"];
                    var result = new JArray(Items.Values.Where(i => all || ids.Contains((string)i["_id"])).Select(i => i.DeepClone()));
                    return FixtureHandler.Json(new JObject { ["result"] = result }.ToString(Formatting.None));
                }
                case "datastorePut":
                    foreach (var change in ((JArray)body["changes"]).OfType<JObject>())
                    {
                        Items[(string)change["_id"]] = (JObject)change.DeepClone();
                        Puts.Add((JObject)change.DeepClone());
                    }
                    return FixtureHandler.Json(new JObject { ["result"] = new JObject { ["success"] = true } }.ToString(Formatting.None));
                case "getUser":
                    return FixtureHandler.Json(new JObject { ["result"] = new JObject { ["_id"] = "u1", ["email"] = "a@b.c", ["dateRegistered"] = "2020-01-01T00:00:00.000Z" } }.ToString(Formatting.None));
                default:
                    return FixtureHandler.Json(new { error = new { message = "unknown method" } });
            }
        }

        public StremioApiClient Client() =>
            new StremioApiClient(Handler, new StremioEndpoints { ApiUrl = Api, LinkUrl = Link }, (d, t) => Task.CompletedTask);
    }

    sealed class FixedAuth : IStremioAuthKeyProvider
    {
        public string Key = "key-1";
        public Task<string> GetAuthKeyAsync(CancellationToken token) =>
            Key == null ? throw new StremioNotSignedInException() : Task.FromResult(Key);
    }

    sealed class FakeCatalog : ICatalog
    {
        public readonly Dictionary<string, MetaDetail> Metas = new Dictionary<string, MetaDetail>();

        public Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MetaSummary>>(Array.Empty<MetaSummary>());

        public Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token) =>
            Metas.TryGetValue(id, out var meta) ? Task.FromResult(meta) : throw new InvalidOperationException("no meta " + id);

        public Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MetaSummary>>(Array.Empty<MetaSummary>());
    }

    static class Metas
    {
        public static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal);

        /// Series with seasons → episode counts; releases one week apart from 2020-01-01; season 0 specials allowed.
        public static MetaDetail Series(string id, string name, params (int Season, int Count)[] seasons)
        {
            var videos = new List<VideoInfo>();
            var release = At("2020-01-01T00:00:00Z");
            foreach (var (season, count) in seasons)
            {
                for (int e = 1; e <= count; e++)
                {
                    videos.Add(new VideoInfo { Id = id + ":" + season + ":" + e, Season = season, Episode = e, Released = release, Title = "S" + season + "E" + e });
                    release = release.AddDays(7);
                }
            }
            return new MetaDetail
            {
                Summary = new MetaSummary { Id = id, Type = MediaType.Series, Name = name, Poster = "https://img/" + id + ".jpg" },
                Videos = LibraryRules.SortVideos(videos),
            };
        }

        public static MetaDetail Movie(string id, string name) => new MetaDetail
        {
            Summary = new MetaSummary { Id = id, Type = MediaType.Movie, Name = name, Poster = "https://img/" + id + ".jpg" },
        };
    }
}
