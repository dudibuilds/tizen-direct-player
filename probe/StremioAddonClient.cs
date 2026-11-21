using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace Ru7100Probe
{
    internal sealed class StremioAddonClient : IDisposable
    {
        private const int MaxDocumentBytes = 2 * 1024 * 1024;
        private readonly HttpClient http;

        public StremioAddonClient(HttpMessageHandler handler = null)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(15);
        }

        public async Task<AddonStream[]> GetStreamsAsync(Uri manifest, string type, string videoId, CancellationToken token)
        {
            var payload = await GetJsonAsync<StreamsResponse>(Resource(manifest, "stream", type, videoId), token)
                .ConfigureAwait(false);
            return (payload.Streams ?? Array.Empty<AddonStream>()).Where(source => source?.IsPlayable == true).ToArray();
        }

        public async Task<AddonSubtitle[]> GetSubtitlesAsync(Uri manifest, string type, string videoId,
            AddonStream source, CancellationToken token)
        {
            var extras = new List<string>();
            if (!string.IsNullOrEmpty(source?.Hints?.VideoHash))
                extras.Add("videoHash=" + Uri.EscapeDataString(source.Hints.VideoHash));
            if (source?.Hints?.VideoSize > 0)
                extras.Add("videoSize=" + source.Hints.VideoSize.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(source?.Hints?.Filename))
                extras.Add("filename=" + Uri.EscapeDataString(source.Hints.Filename));
            var payload = await GetJsonAsync<SubtitlesResponse>(Resource(manifest, "subtitles", type, videoId, extras), token)
                .ConfigureAwait(false);
            return (payload.Subtitles ?? Array.Empty<AddonSubtitle>()).Where(track => track != null && IsWebUrl(track.Url)).ToArray();
        }

        public async Task<TextSubtitles> LoadSubtitlesAsync(AddonSubtitle track, CancellationToken token)
        {
            if (!IsWebUrl(track.Url))
                throw new InvalidDataException("The selected subtitles do not use HTTP.");
            using var buffer = await ReadAsync(new Uri(track.Url), token).ConfigureAwait(false);
            using var text = new StreamReader(buffer, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
            return new TextSubtitles(track.Language ?? track.Id ?? "Subtitles", text.ReadToEnd());
        }

        private async Task<T> GetJsonAsync<T>(Uri uri, CancellationToken token)
        {
            using var buffer = await ReadAsync(uri, token).ConfigureAwait(false);
            return (T)new DataContractJsonSerializer(typeof(T),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(buffer);
        }

        private async Task<MemoryStream> ReadAsync(Uri uri, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
                throw new InvalidDataException("The add-on response is too large.");
            using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new MemoryStream();
            try
            {
                var bytes = new byte[16_384];
                int count;
                while ((count = await body.ReadAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > MaxDocumentBytes)
                        throw new InvalidDataException("The add-on response is too large.");
                    buffer.Write(bytes, 0, count);
                }
                buffer.Position = 0;
                return buffer;
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        private static Uri Resource(Uri manifest, string resource, string type, string id, List<string> extras = null)
        {
            const string suffix = "/manifest.json";
            if (!IsWebUrl(manifest.AbsoluteUri) || !manifest.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal))
                throw new ArgumentException("The add-on URL must end in /manifest.json.", nameof(manifest));
            if (type != "movie" && type != "series")
                throw new ArgumentException("A movie or series type is required.", nameof(type));
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("The add-on video ID is empty.", nameof(id));

            var endpoint = manifest.AbsolutePath.Substring(0, manifest.AbsolutePath.Length - suffix.Length) +
                "/" + resource + "/" + type + "/" + Uri.EscapeDataString(id);
            if (extras?.Count > 0)
                endpoint += "/" + string.Join("&", extras);
            return new UriBuilder(manifest) { Path = endpoint + ".json", Fragment = "" }.Uri;
        }

        internal static bool IsWebUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

        public void Dispose() => http.Dispose();

        [DataContract]
        private sealed class StreamsResponse
        {
            [DataMember(Name = "streams")] public AddonStream[] Streams { get; set; }
        }

        [DataContract]
        private sealed class SubtitlesResponse
        {
            [DataMember(Name = "subtitles")] public AddonSubtitle[] Subtitles { get; set; }
        }
    }

    [DataContract]
    internal sealed class AddonStream
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "infoHash")] public string InfoHash { get; set; }
        [DataMember(Name = "fileIdx")] public int? FileIndex { get; set; }
        [DataMember(Name = "sources")] public string[] Sources { get; set; }
        [DataMember(Name = "subtitles")] public AddonSubtitle[] Subtitles { get; set; }
        [DataMember(Name = "behaviorHints")] public AddonStreamHints Hints { get; set; }

        public bool IsTorrent => InfoHash?.Length == 40 && InfoHash.All(Uri.IsHexDigit) && !(FileIndex < 0);
        public bool IsDirect => StremioAddonClient.IsWebUrl(Url) && Hints?.Headers == null;
        public bool IsPlayable => IsTorrent || IsDirect;

        // Same fallback trackers as samsung-stremio-spotify-mcp (stremio-core.mjs) when an add-on lists none.
        internal static readonly string[] DefaultTrackers =
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://exodus.desync.com:6969/announce",
            "https://tracker.tamersunion.org:443/announce",
        };

        public MagnetLink Magnet()
        {
            if (!IsTorrent)
                throw new InvalidOperationException("The selected stream has no BitTorrent v1 info hash.");
            var trackers = (Sources ?? Array.Empty<string>())
                .Where(source => source?.StartsWith("tracker:", StringComparison.Ordinal) == true)
                .Select(source => source.Substring("tracker:".Length))
                .Where(tracker => Uri.TryCreate(tracker, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
                    && (uri.Scheme == "udp" || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
            var list = trackers.Distinct(StringComparer.Ordinal).ToList();
            if (list.Count == 0)
                list.AddRange(DefaultTrackers);
            return MagnetLink.Parse("magnet:?xt=urn:btih:" + InfoHash + string.Concat(
                list.Select(value => "&tr=" + Uri.EscapeDataString(value))));
        }

        public ITorrentManagerFile SelectVideo(IList<ITorrentManagerFile> files)
        {
            if (FileIndex.HasValue)
            {
                if (FileIndex.Value < 0 || FileIndex.Value >= files.Count || !IsVideo(files[FileIndex.Value]))
                    throw new InvalidDataException("The add-on's torrent file index does not identify a supported video.");
                return files[FileIndex.Value];
            }
            return files.Where(IsVideo).OrderByDescending(file => file.Length).FirstOrDefault()
                ?? throw new InvalidDataException("The torrent contains no supported video file.");
        }

        private static bool IsVideo(ITorrentManagerFile file)
        {
            var extension = Path.GetExtension(file.Path).ToLowerInvariant();
            return file.Length > 0 && (extension == ".mp4" || extension == ".mkv" || extension == ".m4v"
                || extension == ".mov" || extension == ".ts" || extension == ".webm");
        }
    }

    [DataContract]
    internal sealed class AddonStreamHints
    {
        [DataMember(Name = "videoHash")] public string VideoHash { get; set; }
        [DataMember(Name = "videoSize")] public long? VideoSize { get; set; }
        [DataMember(Name = "filename")] public string Filename { get; set; }
        [DataMember(Name = "proxyHeaders")] public AddonProxyHeaders Headers { get; set; }
    }

    [DataContract]
    internal sealed class AddonProxyHeaders
    {
        [DataMember(Name = "request")] public Dictionary<string, string> Request { get; set; }
        [DataMember(Name = "response")] public Dictionary<string, string> Response { get; set; }
    }

    [DataContract]
    internal sealed class AddonSubtitle
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "lang")] public string Language { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
    }
}

namespace Ru7100Probe
{
    internal static class TrackerFanout
    {
        // MonoTorrent puts magnet trackers in one tier and fails over serially (~45 s each).
        // Announce to every tracker at once so the first responsive one supplies peers.
        public static void AnnounceToAll(MonoTorrent.Client.TorrentManager manager, CancellationToken token)
        {
            foreach (var tracker in manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).ToArray())
                _ = manager.TrackerManager.AnnounceAsync(tracker, token).AsTask().ContinueWith(task => { var _ = task.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
