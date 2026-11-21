// Full-length trailers from IMDb's own video CDN. IMDb serves official trailers as plain
// MP4 files (H.264 High + AAC, up to 1080p) that allow ranges anywhere, so the TV's player plays them directly.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Trailers
{
    public sealed class ImdbTrailer
    {
        public string TitleId { get; set; }
        public string VideoId { get; set; }      // e.g. vi2535636505
        public string Name { get; set; }
        public string Url { get; set; }          // direct MP4 (preferred) or HLS master
        public bool IsHls { get; set; }
        public int Height { get; set; }          // 1080, 720, 480 (0 for adaptive HLS)
        public int Width => Height <= 0 ? 0 : Height * 16 / 9;
        public int RuntimeSeconds { get; set; }
    }

    public sealed class ImdbTrailerResolver
    {
        private const string Endpoint = "https://graphql.imdb.com/";
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(20);   // playback URLs carry an expiry
        private readonly HttpClient http;
        private readonly Func<DateTime> utcNow;
        private readonly Dictionary<string, (DateTime at, ImdbTrailer trailer)> cache = new Dictionary<string, (DateTime, ImdbTrailer)>();

        public ImdbTrailerResolver(HttpMessageHandler handler = null, Func<DateTime> utcNow = null)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(10);
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// The best trailer for an IMDb title id, or null when IMDb has none.
        public async Task<ImdbTrailer> ResolveAsync(string titleId, CancellationToken token)
        {
            if (string.IsNullOrEmpty(titleId) || !titleId.StartsWith("tt", StringComparison.Ordinal))
                return null;
            lock (cache)
                if (cache.TryGetValue(titleId, out var hit) && utcNow() - hit.at < CacheFor)
                    return hit.trailer;
            var query = "query { title(id: \"" + titleId + "\") { primaryVideos(first: 6) { edges { node { id name { value } " +
                "contentType { id } runtime { value } playbackURLs { url videoMimeType videoDefinition } } } } } }";
            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Content = new StringContent(JsonConvert.SerializeObject(new { query }), Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("x-imdb-client-name", "imdb-web-next");
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
                using (var response = await http.SendAsync(request, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var trailer = Pick(titleId, JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
                    lock (cache)
                        cache[titleId] = (utcNow(), trailer);
                    return trailer;
                }
            }
        }

        /// Prefers videos typed as trailers (then any), 45-200 s long, with the highest MP4 definition; HLS when no MP4.
        internal static ImdbTrailer Pick(string titleId, JObject payload)
        {
            var edges = payload.SelectToken("data.title.primaryVideos.edges") as JArray;
            if (edges == null)
                return null;
            var options = new List<(ImdbTrailer trailer, int score)>();
            foreach (var edge in edges)
            {
                var node = edge["node"];
                if (node == null)
                    continue;
                var urls = (node["playbackURLs"] as JArray)?.ToList() ?? new List<JToken>();
                var mp4 = urls.Where(u => (string)u["videoMimeType"] == "MP4")
                    .Select(u => new { url = (string)u["url"], height = HeightOf((string)u["videoDefinition"]) })
                    .Where(u => !string.IsNullOrEmpty(u.url) && u.height >= 480 && u.height <= 1080)
                    .OrderByDescending(u => u.height).FirstOrDefault();
                var hls = urls.FirstOrDefault(u => (string)u["videoMimeType"] == "M3U8");
                if (mp4 == null && hls == null)
                    continue;
                var runtime = (int?)node.SelectToken("runtime.value") ?? 0;
                var isTrailer = string.Equals((string)node.SelectToken("contentType.id"), "amzn1.imdb.video.contenttype.trailer", StringComparison.Ordinal);
                var trailer = new ImdbTrailer
                {
                    TitleId = titleId, VideoId = (string)node["id"], Name = (string)node.SelectToken("name.value"),
                    Url = mp4?.url ?? (string)hls["url"], IsHls = mp4 == null, Height = mp4?.height ?? 0, RuntimeSeconds = runtime,
                };
                var score = (isTrailer ? 1000 : 0) + (runtime >= 45 && runtime <= 200 ? 500 : 0) + trailer.Height / 10;
                options.Add((trailer, score));
            }
            // Stable: IMDb's order (newest/most relevant first) breaks ties.
            return options.Select((o, i) => (o.trailer, o.score, i)).OrderByDescending(o => o.score).ThenBy(o => o.i)
                .Select(o => o.trailer).FirstOrDefault();
        }

        private static int HeightOf(string definition)
        {
            switch (definition)
            {
                case "DEF_1080p": return 1080;
                case "DEF_720p": return 720;
                case "DEF_480p": return 480;
                case "DEF_SD": return 360;
                case "DEF_240p": return 240;
                default: return 0;
            }
        }
    }
}
