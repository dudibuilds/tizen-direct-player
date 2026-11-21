using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    /// IKeyValueStore keys owned by the account area. Values are JSON text; an empty value means absent.
    public static class AccountKeys
    {
        public const string StremioAuth = "stremio-auth";               // {authKey, linkedAt}
        public const string StremioPendingLink = "stremio-pending-link"; // {code, link, createdAt}
        public const string StremioOutbox = "stremio-outbox";           // [op] ordered library/progress writes
        public const string StremioLibraryCache = "stremio-library-cache"; // [libraryItem] last server copy
        public const string StremioAlerts = "stremio-alerts";           // {announced:{videoId: ISO}}
        public const string TraktCredentials = "trakt-credentials";     // {client_id, client_secret} (config, never in code)
        public const string TraktToken = "trakt-token";                 // Trakt OAuth token JSON
        public const string TraktPendingAuth = "trakt-pending-auth";    // {deviceCode, userCode, verificationUrl, expiresAt, intervalSeconds}
        public const string TraktOutbox = "trakt-outbox";               // [op] ordered scrobbles
    }

    internal static class StoreJson
    {
        public static async Task<JToken> ReadAsync(IKeyValueStore store, string key, CancellationToken token)
        {
            string text;
            try { text = await store.ReadAsync(key, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }   // missing file etc.
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonConvert.DeserializeObject<JToken>(text, StremioApiClient.JsonSettings); }
            catch (JsonException) { return null; }
        }

        public static Task WriteAsync(IKeyValueStore store, string key, JToken value, CancellationToken token)
        {
            return store.WriteAsync(key, value == null || value.Type == JTokenType.Null ? "" : value.ToString(Formatting.None), token);
        }
    }

    /// JS-compatible time helpers. Stremio stores times as ISO strings like 2026-11-18T10:00:00.000Z.
    public static class IsoTime
    {
        public static string Format(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        /// new Date(value): null/empty/invalid yield null.
        public static DateTimeOffset? Parse(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            if (token.Type == JTokenType.Date) return new DateTimeOffset(DateTime.SpecifyKind((DateTime)token, DateTimeKind.Utc));
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(double)token);
            return Parse((string)token);
        }

        public static DateTimeOffset? Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
            return null;
        }

        /// new Date(value || 0) as epoch milliseconds (invalid dates compare as NaN → treated as epoch here).
        public static long EpochMs(JToken token) => Parse(token)?.ToUnixTimeMilliseconds() ?? 0;
    }
}
