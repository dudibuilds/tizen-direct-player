using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account.Trakt
{
    public sealed class TraktApiException : Exception
    {
        public TraktApiException(string message, bool isTransient, int? httpStatus = null, Exception inner = null) : base(message, inner)
        {
            IsTransient = isTransient;
            HttpStatus = httpStatus;
        }

        public bool IsTransient { get; }
        public int? HttpStatus { get; }
    }

    public sealed class TraktDeviceCode
    {
        public string UserCode { get; set; }
        public string VerificationUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int IntervalSeconds { get; set; }
    }

    public enum TraktAuthStatus { Authenticated, Pending, SlowDown, Expired, Denied }

    public sealed class TraktUser
    {
        public string Username { get; set; }
        public string Name { get; set; }
    }

    /// What a scrobble refers to (the session fields used by trakt.mjs buildScrobblePayload).
    public sealed class TraktScrobbleTarget
    {
        public MediaType Type { get; set; }
        public string ImdbId { get; set; }
        public string Title { get; set; }
        public int Season { get; set; }
        public int Episode { get; set; }
    }

    /// Trakt device auth (A6) and scrobbling (K1-K3). Client id/secret come from the
    /// IKeyValueStore key "trakt-credentials" ({client_id, client_secret}); nothing is in code.
    public sealed class TraktClient
    {
        public const string AppVersion = "0.2.1";
        public const string RedirectUri = "urn:ietf:wg:oauth:2.0:oob";
        static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        readonly HttpClient http;
        readonly IKeyValueStore store;
        readonly Func<DateTimeOffset> now;
        readonly string baseUrl;
        readonly ConcurrentDictionary<string, JObject> episodeCache = new ConcurrentDictionary<string, JObject>(StringComparer.Ordinal);
        readonly SemaphoreSlim tokenGate = new SemaphoreSlim(1, 1);

        public TraktClient(HttpMessageHandler handler, IKeyValueStore store, Func<DateTimeOffset> now = null, string baseUrl = "https://api.trakt.tv")
        {
            http = new HttpClient(handler ?? new HttpClientHandler(), false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
            this.baseUrl = baseUrl.TrimEnd('/');
        }

        public async Task<bool> IsConfiguredAsync(CancellationToken token) => await ReadCredentialsAsync(false, token).ConfigureAwait(false) != null;

        public async Task<bool> IsAuthenticatedAsync(CancellationToken token)
        {
            var saved = await StoreJson.ReadAsync(store, AccountKeys.TraktToken, token).ConfigureAwait(false);
            return !string.IsNullOrEmpty((string)saved?["access_token"]);
        }

        /// A6 step 1: POST /oauth/device/code.
        public async Task<TraktDeviceCode> BeginDeviceAuthAsync(CancellationToken token)
        {
            var credentials = await ReadCredentialsAsync(true, token).ConfigureAwait(false);
            var response = await SendAsync(HttpMethod.Post, "/oauth/device/code", new JObject { ["client_id"] = credentials.Item1 }, null, null, token).ConfigureAwait(false);
            var expiresIn = NumberOr(response["expires_in"], 600);
            var pending = new TraktDeviceCode
            {
                UserCode = (string)response["user_code"],
                VerificationUrl = (string)response["verification_url"],
                ExpiresAt = now().AddSeconds(expiresIn),
                IntervalSeconds = (int)NumberOr(response["interval"], 5),
            };
            await StoreJson.WriteAsync(store, AccountKeys.TraktPendingAuth, new JObject
            {
                ["deviceCode"] = (string)response["device_code"],
                ["userCode"] = pending.UserCode,
                ["verificationUrl"] = pending.VerificationUrl,
                ["expiresAt"] = pending.ExpiresAt.ToUnixTimeMilliseconds(),
                ["intervalSeconds"] = pending.IntervalSeconds,
            }, token).ConfigureAwait(false);
            return pending;
        }

        /// A6 step 2: POST /oauth/device/token. 400 pending, 410 expired, 418 denied, 429 slow down, 200 token.
        public async Task<TraktAuthStatus> CompleteDeviceAuthAsync(CancellationToken token)
        {
            var credentials = await ReadCredentialsAsync(true, token).ConfigureAwait(false);
            var pending = await StoreJson.ReadAsync(store, AccountKeys.TraktPendingAuth, token).ConfigureAwait(false);
            var deviceCode = (string)pending?["deviceCode"];
            if (string.IsNullOrEmpty(deviceCode)) throw new InvalidOperationException("No pending Trakt device code. Call BeginDeviceAuthAsync first.");
            if (now().ToUnixTimeMilliseconds() >= (long)NumberOr(pending["expiresAt"], 0)) return TraktAuthStatus.Expired;

            var body = new JObject { ["code"] = deviceCode, ["client_id"] = credentials.Item1, ["client_secret"] = credentials.Item2 };
            using (var response = await RawSendAsync(HttpMethod.Post, "/oauth/device/token", body, null, null, token).ConfigureAwait(false))
            {
                int status = (int)response.StatusCode;
                if (status == 400) return TraktAuthStatus.Pending;
                if (status == 410) return TraktAuthStatus.Expired;
                if (status == 418) return TraktAuthStatus.Denied;
                if (status == 429) return TraktAuthStatus.SlowDown;
                var payload = await ReadPayloadAsync(response).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new TraktApiException("Trakt device authentication failed: " + ((string)payload["error"] ?? "HTTP " + status), status >= 500, status);
                await StoreJson.WriteAsync(store, AccountKeys.TraktToken, payload, token).ConfigureAwait(false);
                await StoreJson.WriteAsync(store, AccountKeys.TraktPendingAuth, null, token).ConfigureAwait(false);
                return TraktAuthStatus.Authenticated;
            }
        }

        public Task SignOutAsync(CancellationToken token) => StoreJson.WriteAsync(store, AccountKeys.TraktToken, null, token);

        /// Status check: GET /users/settings.
        public async Task<TraktUser> GetUserAsync(CancellationToken token)
        {
            var response = await AuthorizedAsync(HttpMethod.Get, "/users/settings", null, token).ConfigureAwait(false);
            return new TraktUser { Username = (string)response["user"]?["username"], Name = (string)response["user"]?["name"] };
        }

        /// K1: POST /scrobble/{start|pause|stop}. Returns false (skipped) when not authenticated.
        public async Task<bool> ScrobbleAsync(string action, TraktScrobbleTarget target, double progress, CancellationToken token)
        {
            if (action != "start" && action != "pause" && action != "stop") throw new ArgumentException("Unsupported Trakt scrobble action: " + action, nameof(action));
            if (!await IsAuthenticatedAsync(token).ConfigureAwait(false)) return false;
            var episode = await ResolveEpisodeAsync(target, token).ConfigureAwait(false);
            var payload = BuildScrobblePayload(target, progress, episode, now());
            await AuthorizedAsync(HttpMethod.Post, "/scrobble/" + action, payload, token).ConfigureAwait(false);
            return true;
        }

        /// K2: episode lookup, cached in memory for the app lifetime. Failures yield null.
        async Task<JObject> ResolveEpisodeAsync(TraktScrobbleTarget target, CancellationToken token)
        {
            if (target.Type != MediaType.Series) return null;
            var key = target.ImdbId + ":" + target.Season + ":" + target.Episode;
            if (episodeCache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var path = "/shows/" + Uri.EscapeDataString(target.ImdbId) + "/seasons/" + target.Season + "/episodes/" + target.Episode + "?extended=full";
                var episode = await AuthorizedAsync(HttpMethod.Get, path, null, token).ConfigureAwait(false);
                episodeCache[key] = episode;
                return episode;
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                return null;
            }
        }

        /// K3 (trakt.mjs buildScrobblePayload).
        public static JObject BuildScrobblePayload(TraktScrobbleTarget target, double progress, JObject episode, DateTimeOffset now)
        {
            double normalized = double.IsNaN(progress) ? 0 : Math.Max(0, Math.Min(100, progress));
            var payload = new JObject
            {
                ["progress"] = Math.Round(normalized * 100, MidpointRounding.AwayFromZero) / 100,
                ["app_version"] = AppVersion,
                ["app_date"] = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            if (target.Type == MediaType.Series)
            {
                payload["show"] = new JObject { ["title"] = target.Title, ["ids"] = new JObject { ["imdb"] = target.ImdbId } };
                if (episode != null)
                {
                    var ep = new JObject { ["season"] = episode["season"], ["number"] = episode["number"] };
                    if (StremioApiClient.IsTruthy(episode["title"])) ep["title"] = episode["title"];
                    if (StremioApiClient.IsTruthy(episode["ids"])) ep["ids"] = episode["ids"];
                    payload["episode"] = ep;
                }
                else
                {
                    payload["episode"] = new JObject { ["season"] = target.Season, ["number"] = target.Episode };
                }
            }
            else
            {
                payload["movie"] = new JObject { ["title"] = target.Title, ["ids"] = new JObject { ["imdb"] = target.ImdbId } };
            }
            return payload;
        }

        async Task<JObject> AuthorizedAsync(HttpMethod method, string path, JObject body, CancellationToken token)
        {
            var credentials = await ReadCredentialsAsync(true, token).ConfigureAwait(false);
            var accessToken = await AccessTokenAsync(credentials, token).ConfigureAwait(false);
            return await SendAsync(method, path, body, accessToken, credentials.Item1, token).ConfigureAwait(false);
        }

        /// Refresh when (created_at + expires_in) * 1000 <= now + 5 min.
        async Task<string> AccessTokenAsync(Tuple<string, string> credentials, CancellationToken token)
        {
            await tokenGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var saved = await StoreJson.ReadAsync(store, AccountKeys.TraktToken, token).ConfigureAwait(false);
                var access = (string)saved?["access_token"];
                if (string.IsNullOrEmpty(access)) throw new TraktApiException("Trakt is not authenticated.", false);
                var refresh = (string)saved["refresh_token"];
                double createdAt = NumberOr(saved["created_at"], 0), expiresIn = NumberOr(saved["expires_in"], 0);
                if (!string.IsNullOrEmpty(refresh) && createdAt > 0 && expiresIn > 0
                    && (createdAt + expiresIn) * 1000 <= now().ToUnixTimeMilliseconds() + 300000)
                {
                    var renewed = await SendAsync(HttpMethod.Post, "/oauth/token", new JObject
                    {
                        ["refresh_token"] = refresh,
                        ["client_id"] = credentials.Item1,
                        ["client_secret"] = credentials.Item2,
                        ["redirect_uri"] = RedirectUri,
                        ["grant_type"] = "refresh_token",
                    }, null, null, token).ConfigureAwait(false);
                    await StoreJson.WriteAsync(store, AccountKeys.TraktToken, renewed, token).ConfigureAwait(false);
                    access = (string)renewed["access_token"];
                }
                return access;
            }
            finally
            {
                tokenGate.Release();
            }
        }

        async Task<JObject> SendAsync(HttpMethod method, string path, JObject body, string accessToken, string clientId, CancellationToken token)
        {
            using (var response = await RawSendAsync(method, path, body, accessToken, clientId, token).ConfigureAwait(false))
            {
                var payload = await ReadPayloadAsync(response).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    string message = (string)payload["error"] ?? (string)payload["message"] ?? "HTTP " + status;
                    throw new TraktApiException("Trakt " + method.Method + " " + path + " failed: " + message, status == 429 || status >= 500, status);
                }
                return payload;
            }
        }

        async Task<HttpResponseMessage> RawSendAsync(HttpMethod method, string path, JObject body, string accessToken, string clientId, CancellationToken token)
        {
            if (clientId == null)
            {
                var credentials = await ReadCredentialsAsync(false, token).ConfigureAwait(false);
                clientId = credentials?.Item1;
            }
            var request = new HttpRequestMessage(method, baseUrl + path);
            request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            if (!string.IsNullOrEmpty(clientId)) request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);
            if (!string.IsNullOrEmpty(accessToken)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(RequestTimeout);
                try
                {
                    return await http.SendAsync(request, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TraktApiException("Trakt " + method.Method + " " + path + " failed: timeout", true);
                }
                catch (HttpRequestException error)
                {
                    throw new TraktApiException("Trakt " + method.Method + " " + path + " failed: " + error.Message, true, null, error);
                }
            }
        }

        static async Task<JObject> ReadPayloadAsync(HttpResponseMessage response)
        {
            try
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return StremioApiClient.ParseObject(text);
            }
            catch (Exception error) when (error is IOException || error is HttpRequestException)
            {
                return new JObject();
            }
        }

        /// trakt.mjs readCredentials: both values present and not "PASTE_" placeholders.
        async Task<Tuple<string, string>> ReadCredentialsAsync(bool required, CancellationToken token)
        {
            var saved = await StoreJson.ReadAsync(store, AccountKeys.TraktCredentials, token).ConfigureAwait(false);
            var id = (string)saved?["client_id"];
            var secret = (string)saved?["client_secret"];
            bool valid = !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret)
                && !id.StartsWith("PASTE_", StringComparison.Ordinal) && !secret.StartsWith("PASTE_", StringComparison.Ordinal);
            if (!valid && required) throw new TraktApiException("Trakt API credentials are missing (store key " + AccountKeys.TraktCredentials + ").", false);
            return valid ? Tuple.Create(id, secret) : null;
        }

        static double NumberOr(JToken token, double fallback)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float && token.Type != JTokenType.String)) return fallback;
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value != 0 ? value : fallback;
        }
    }
}
