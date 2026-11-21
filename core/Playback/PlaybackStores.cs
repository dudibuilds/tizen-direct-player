using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Ru7100.Core.Playback
{
    /// JSON documents in IKeyValueStore used by the playback engine.
    public static class PlaybackKeys
    {
        public const string Session = "playback/session";
        public const string Queue = "playback/queue";
        public const string TrackPreferences = "playback/track-preferences";
    }

    internal static class JsonStore
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter() },
        };

        public static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, Settings);

        public static T Deserialize<T>(string text) where T : class
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            try { return JsonConvert.DeserializeObject<T>(text, Settings); }
            catch (JsonException) { return null; }
        }

        public static async Task<T> ReadAsync<T>(IKeyValueStore store, string key, CancellationToken token) where T : class
        {
            if (store == null)
                return null;
            try { return Deserialize<T>(await store.ReadAsync(key, token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        public static Task WriteAsync<T>(IKeyValueStore store, string key, T value, CancellationToken token) =>
            store == null ? Task.CompletedTask : store.WriteAsync(key, value == null ? "null" : Serialize(value), token);
    }

    /// Last-used audio/subtitle language per show (metaId).
    public sealed class TrackPreferenceStore
    {
        private readonly IKeyValueStore store;
        private readonly IClock clock;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private Dictionary<string, TrackPreference> cache;

        public TrackPreferenceStore(IKeyValueStore store, IClock clock)
        {
            this.store = store;
            this.clock = clock;
        }

        public async Task<TrackPreference> GetAsync(string metaId, CancellationToken token)
        {
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            lock (cache)
                return metaId != null && cache.TryGetValue(metaId, out var value) ? value : null;
        }

        public async Task UpdateAsync(string metaId, string audioLanguage, string subtitleLanguage, CancellationToken token)
        {
            if (string.IsNullOrEmpty(metaId))
                return;
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            string text;
            lock (cache)
            {
                if (!cache.TryGetValue(metaId, out var value))
                    cache[metaId] = value = new TrackPreference();
                if (audioLanguage != null)
                    value.AudioLanguage = audioLanguage;
                if (subtitleLanguage != null)
                    value.SubtitleLanguage = subtitleLanguage;
                value.UpdatedAt = clock.UtcNow;
                text = JsonStore.Serialize(cache);
            }
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { if (store != null) await store.WriteAsync(PlaybackKeys.TrackPreferences, text, token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        /// Remembers a subtitle speed fix for a show and the release of the file it was learned on.
        public async Task UpdateSubtitleTimingAsync(string metaId, string releaseKey, RememberedTiming timing, CancellationToken token, bool forget = false)
        {
            if (string.IsNullOrEmpty(metaId) || string.IsNullOrEmpty(releaseKey) || timing == null && !forget)
                return;
            await EnsureLoadedAsync(token).ConfigureAwait(false);
            string text;
            lock (cache)
            {
                if (!cache.TryGetValue(metaId, out var value))
                    cache[metaId] = value = new TrackPreference();
                if (value.SubtitleTimings == null)
                    value.SubtitleTimings = new Dictionary<string, RememberedTiming>(StringComparer.Ordinal);
                if (forget)
                    value.SubtitleTimings.Remove(releaseKey);
                else
                    value.SubtitleTimings[releaseKey] = timing;
                value.UpdatedAt = clock.UtcNow;
                text = JsonStore.Serialize(cache);
            }
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { if (store != null) await store.WriteAsync(PlaybackKeys.TrackPreferences, text, token).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        private async Task EnsureLoadedAsync(CancellationToken token)
        {
            if (cache != null)
                return;
            var loaded = await JsonStore.ReadAsync<Dictionary<string, TrackPreference>>(store, PlaybackKeys.TrackPreferences, token)
                .ConfigureAwait(false);
            if (cache == null)
                cache = loaded ?? new Dictionary<string, TrackPreference>(StringComparer.Ordinal);
        }
    }
}
