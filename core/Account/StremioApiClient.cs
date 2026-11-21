using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    /// A Stremio API or link-service failure. IsTransient means a retry later may succeed
    /// (network failure, timeout, HTTP 429 or 5xx).
    public class StremioApiException : Exception
    {
        public StremioApiException(string message, bool isTransient, int? httpStatus = null, Exception inner = null)
            : base(message, inner)
        {
            IsTransient = isTransient;
            HttpStatus = httpStatus;
        }

        public bool IsTransient { get; }
        public int? HttpStatus { get; }
    }

    public sealed class StremioNotSignedInException : Exception
    {
        public StremioNotSignedInException() : base("Stremio is not signed in. Link the device first.") { }
    }

    public sealed class StremioLinkExpiredException : Exception
    {
        public StremioLinkExpiredException(string code) : base("The Stremio link code " + code + " expired. Start a new link.") { Code = code; }
        public string Code { get; }
    }

    public sealed class StremioEndpoints
    {
        public string ApiUrl { get; set; } = "https://api.strem.io/api";
        public string LinkUrl { get; set; } = "https://link.stremio.com/api/v2";
    }

    /// Low-level Stremio HTTP calls (parity A1, A2, B5). No state.
    public sealed class StremioApiClient
    {
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

        // Shared parser settings: keep ISO date strings as strings so items round-trip byte-for-byte.
        internal static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
        };

        readonly HttpClient http;
        readonly StremioEndpoints endpoints;
        readonly Func<TimeSpan, CancellationToken, Task> delay;

        public StremioApiClient(HttpMessageHandler handler, StremioEndpoints endpoints = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            http = new HttpClient(handler ?? new HttpClientHandler(), false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            this.endpoints = endpoints ?? new StremioEndpoints();
            this.delay = delay ?? ((d, t) => Task.Delay(d, t));
        }

        public StremioEndpoints Endpoints => endpoints;

        /// A2: POST {api}/{method} with a JSON body. Returns payload.result. No retries.
        public async Task<JToken> PostAsync(string method, JObject body, CancellationToken token)
        {
            var url = endpoints.ApiUrl.TrimEnd('/') + "/" + method;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(RequestTimeout);
                HttpResponseMessage response;
                try
                {
                    var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    response = await http.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new StremioApiException("Stremio " + method + " failed: timeout", true);
                }
                catch (HttpRequestException error)
                {
                    throw new StremioApiException("Stremio " + method + " failed: " + error.Message, true, null, error);
                }
                using (response)
                {
                    string text;
                    try { text = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException || error is HttpRequestException)
                    {
                        throw new StremioApiException("Stremio " + method + " failed: " + error.Message, true, (int)response.StatusCode, error);
                    }
                    var payload = ParseObject(text);
                    var errorToken = payload["error"];
                    int status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode || IsTruthy(errorToken))
                    {
                        string message = ErrorMessage(errorToken) ?? "HTTP " + status;
                        throw new StremioApiException("Stremio " + method + " failed: " + message, IsRetryableStatus(status), status);
                    }
                    return payload["result"];
                }
            }
        }

        /// B5: GET JSON with 3 tries, retrying 429/5xx/network/timeout after 250 ms × attempt.
        public async Task<JObject> GetJsonAsync(string url, CancellationToken token)
        {
            StremioApiException last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    return await GetOnceAsync(url, token).ConfigureAwait(false);
                }
                catch (StremioApiException error)
                {
                    last = error;
                    if (attempt == 2 || !error.IsTransient) throw;
                }
                await delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), token).ConfigureAwait(false);
            }
            throw last;
        }

        async Task<JObject> GetOnceAsync(string url, CancellationToken token)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cts.CancelAfter(RequestTimeout);
                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new StremioApiException("GET " + url + " failed: timeout", true);
                }
                catch (HttpRequestException error)
                {
                    throw new StremioApiException("GET " + url + " failed: " + error.Message, true, null, error);
                }
                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                        throw new StremioApiException("GET " + url + " failed: HTTP " + status, IsRetryableStatus(status), status);
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ParseObject(text);
                }
            }
        }

        /// A1 create: GET {link}/create?type=Create.
        public async Task<(string Code, string Link)> CreateLinkAsync(CancellationToken token)
        {
            var payload = await GetJsonAsync(endpoints.LinkUrl.TrimEnd('/') + "/create?type=Create", token).ConfigureAwait(false);
            var result = UnwrapLinkResult(payload);
            var code = (string)result?["code"];
            var link = (string)result?["link"];
            if (string.IsNullOrEmpty(code)) throw new StremioApiException("Stremio link failed: no code returned", false);
            return (code, link);
        }

        /// A1 read: GET {link}/read?type=Read&code=... Returns the unwrapped result (may lack authKey).
        public async Task<JToken> ReadLinkAsync(string code, CancellationToken token)
        {
            var payload = await GetJsonAsync(endpoints.LinkUrl.TrimEnd('/') + "/read?type=Read&code=" + Uri.EscapeDataString(code), token).ConfigureAwait(false);
            return UnwrapLinkResult(payload);
        }

        /// stremio-core.mjs unwrapLinkResult.
        internal static JToken UnwrapLinkResult(JObject payload)
        {
            if (IsTruthy(payload["error"])) throw new StremioApiException(ErrorMessage(payload["error"]) ?? "Stremio link failed", false);
            JToken outer = payload["result"];
            if (outer == null || outer.Type == JTokenType.Null) outer = payload;
            if (outer is JObject outerObj)
            {
                var success = outerObj["success"];
                if (success != null && success.Type == JTokenType.Boolean && !(bool)success)
                    throw new StremioApiException(ErrorMessage(outerObj["error"]) ?? "Stremio link failed", false);
                var inner = outerObj["result"];
                if (IsTruthy(inner)) return inner;
            }
            return outer;
        }

        internal static JObject ParseObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            try
            {
                var token = JsonConvert.DeserializeObject<JToken>(text, JsonSettings);
                return token as JObject ?? new JObject();
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        static string ErrorMessage(JToken error)
        {
            if (error == null || error.Type == JTokenType.Null) return null;
            if (error.Type == JTokenType.String) return (string)error;
            var message = error["message"];
            return message != null && message.Type != JTokenType.Null ? message.ToString() : null;
        }

        internal static bool IsTruthy(JToken token)
        {
            if (token == null) return false;
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined: return false;
                case JTokenType.Boolean: return (bool)token;
                case JTokenType.String: return ((string)token).Length > 0;
                case JTokenType.Integer: return (long)token != 0;
                case JTokenType.Float: var d = (double)token; return d != 0 && !double.IsNaN(d);
                default: return true;
            }
        }

        static bool IsRetryableStatus(int status) => status == 429 || status >= 500;
    }
}
