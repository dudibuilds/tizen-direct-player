// Request signing for the agent API.
// signature = base64(HMAC-SHA256(secret, METHOD \n PATH \n TIME \n NONCE \n hex(sha256(body))))
// Headers: X-Ru7100-Key (key id), X-Ru7100-Time (unix seconds), X-Ru7100-Nonce, X-Ru7100-Signature.
// Window ±60 s; each key+nonce is accepted once (recorded only after the signature verifies).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Api
{
    public static class ApiHeaders
    {
        public const string Key = "X-Ru7100-Key";
        public const string Time = "X-Ru7100-Time";
        public const string Nonce = "X-Ru7100-Nonce";
        public const string Signature = "X-Ru7100-Signature";
    }

    public sealed class ApiKey
    {
        public ApiKey(string id, byte[] secret, IEnumerable<string> scopes)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Key id is required", nameof(id));
            if (secret == null || secret.Length < 32) throw new ArgumentException("Key secret must be at least 32 bytes", nameof(secret));
            var list = (scopes ?? Enumerable.Empty<string>()).Distinct().ToList();
            if (list.Count == 0 || list.Any(s => !ApiScopes.IsKnown(s))) throw new ArgumentException("Key scopes must be known scope names", nameof(scopes));
            Id = id;
            Secret = (byte[])secret.Clone();
            Scopes = list;
        }

        public string Id { get; }
        public byte[] Secret { get; }
        public IReadOnlyCollection<string> Scopes { get; }

        /// {"keys":[{"id":"agent","secret":"<base64, >=32 bytes>","scopes":["playback","library"]}]}
        public static IReadOnlyList<ApiKey> ParseKeyFile(string json)
        {
            var root = ApiJson.Parse(json) as JObject;
            var keys = root?["keys"] as JArray ?? throw new FormatException("Key file needs a keys array");
            var result = new List<ApiKey>();
            foreach (var entry in keys.OfType<JObject>())
            {
                var scopes = (entry["scopes"] as JArray)?.Select(s => (string)s) ?? Enumerable.Empty<string>();
                result.Add(new ApiKey((string)entry["id"], Convert.FromBase64String((string)entry["secret"] ?? ""), scopes));
            }
            if (result.Select(k => k.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
                throw new FormatException("Key ids must be unique");
            return result;
        }
    }

    public sealed class ApiAuthResult
    {
        private ApiAuthResult(ApiKey key, string failure)
        {
            Key = key;
            Failure = failure;
        }

        public ApiKey Key { get; }
        /// null on success; otherwise missing_headers | unknown_key | stale | bad_signature | replay | busy.
        public string Failure { get; }
        public bool Succeeded => Failure == null;

        internal static ApiAuthResult Ok(ApiKey key) => new ApiAuthResult(key, null);
        internal static ApiAuthResult Fail(string reason) => new ApiAuthResult(null, reason);
    }

    public sealed class ApiAuthenticator
    {
        public const int WindowSeconds = 60;
        private readonly Dictionary<string, ApiKey> keys;
        private readonly Func<DateTimeOffset> clock;
        private readonly int maxNonces;
        private readonly Dictionary<string, long> nonces = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly object gate = new object();

        public ApiAuthenticator(IEnumerable<ApiKey> keys, Func<DateTimeOffset> clock = null, int maxNonces = 10000)
        {
            this.keys = (keys ?? throw new ArgumentNullException(nameof(keys))).ToDictionary(k => k.Id, StringComparer.Ordinal);
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            this.maxNonces = maxNonces;
        }

        public ApiAuthResult Authenticate(string method, string path, string keyId, string time, string nonce, string signature, byte[] body)
        {
            if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(keyId)
                || !IsValidNonce(nonce) || string.IsNullOrEmpty(signature)
                || !long.TryParse(time, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var issuedAt))
                return ApiAuthResult.Fail("missing_headers");

            ApiKey key;
            if (!keys.TryGetValue(keyId, out key))
                return ApiAuthResult.Fail("unknown_key");

            var now = clock().ToUnixTimeSeconds();
            if (issuedAt < now - WindowSeconds || issuedAt > now + WindowSeconds)
                return ApiAuthResult.Fail("stale");

            byte[] received;
            try { received = Convert.FromBase64String(signature); }
            catch (FormatException) { return ApiAuthResult.Fail("bad_signature"); }
            var expected = ComputeSignature(key.Secret, method, path, time, nonce, body);
            if (!FixedTimeEquals(expected, received))
                return ApiAuthResult.Fail("bad_signature");

            lock (gate)
            {
                var cacheKey = keyId + "\n" + nonce;
                if (nonces.ContainsKey(cacheKey)) return ApiAuthResult.Fail("replay");
                if (nonces.Count >= maxNonces)
                {
                    foreach (var expired in nonces.Where(entry => entry.Value < now - WindowSeconds).Select(entry => entry.Key).ToList())
                        nonces.Remove(expired);
                    if (nonces.Count >= maxNonces) return ApiAuthResult.Fail("busy");
                }
                nonces.Add(cacheKey, issuedAt);
            }
            return ApiAuthResult.Ok(key);
        }

        public static string Sign(byte[] secret, string method, string path, string time, string nonce, byte[] body) =>
            Convert.ToBase64String(ComputeSignature(secret, method, path, time, nonce, body));

        public static string BodyHash(byte[] body)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(body ?? new byte[0]);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private static byte[] ComputeSignature(byte[] secret, string method, string path, string time, string nonce, byte[] body)
        {
            var message = method + "\n" + path + "\n" + time + "\n" + nonce + "\n" + BodyHash(body);
            using (var hmac = new HMACSHA256(secret))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        }

        private static bool IsValidNonce(string nonce)
        {
            if (nonce == null || nonce.Length < 16 || nonce.Length > 64) return false;
            foreach (var c in nonce)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_'))
                    return false;
            return true;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            var difference = 0;
            for (var i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
