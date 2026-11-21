// HTTP host for the agent API. Platform-neutral: the TV app and the Mac dev host both run it.
//   POST /api          body {"command": "...", "args": {...}}
//   GET  /api/status   same as command "status"
// Every request is signed (see ApiAuth.cs). Each connection runs on its own task, so a long play does not block pause
// or status. Bind to loopback by default (http://127.0.0.1:45551/). LAN binding (http://+:port/) is for a later
// pairing flow. Runs on MiniHttpServer (core/Net), not HttpListener, whose accept path crashed the TV app.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Net;

namespace Ru7100.Core.Api
{
    public sealed class ApiHttpServer : IDisposable
    {
        public const string DefaultPrefix = "http://127.0.0.1:45551/";
        public const int DefaultMaxBodyBytes = 256 * 1024;
        /// Open API connections at once; more get 503 (a burst of diagnostics polls must not crowd out playback).
        public const int DefaultMaxConnections = 16;
        private readonly ApiRouter router;
        private readonly ApiAuthenticator authenticator;
        private readonly IReadOnlyList<string> prefixes;
        private readonly int maxBodyBytes;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private MiniHttpServer server;

        public ApiHttpServer(ApiRouter router, ApiAuthenticator authenticator, IReadOnlyList<string> prefixes = null, int maxBodyBytes = DefaultMaxBodyBytes)
        {
            this.router = router ?? throw new ArgumentNullException(nameof(router));
            this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            this.prefixes = prefixes ?? new[] { DefaultPrefix };
            this.maxBodyBytes = maxBodyBytes;
        }

        /// Called with one line per request (method, path, key, status) and for listener errors. Never includes secrets.
        public event Action<string> Log;

        public MiniHttpStats Stats => server?.Stats;

        public void Start()
        {
            if (server != null) throw new InvalidOperationException("Already started");
            var endpoints = prefixes.SelectMany(MiniHttpServer.FromPrefix).ToList();
            server = new MiniHttpServer(endpoints, HandleAsync, new MiniHttpOptions
            {
                Name = "api",
                MaxConnections = DefaultMaxConnections,
                BodyTimeout = TimeSpan.FromSeconds(10),
                WriteTimeout = TimeSpan.FromSeconds(30),
            }, line => Log?.Invoke(line));
            server.Start();
        }

        public void Dispose()
        {
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            try { server?.Dispose(); } catch (Exception) { }
        }

        private async Task HandleAsync(MiniHttpRequest request, MiniHttpResponse output)
        {
            var path = request.Path;
            var keyId = request.Header(ApiHeaders.Key);
            ApiResponse response;
            try
            {
                response = await ProcessAsync(request, path, keyId).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                response = ApiResponse.Failure(500, "internal_error", error.Message);
            }

            var payload = Encoding.UTF8.GetBytes(response.Body.ToString(Newtonsoft.Json.Formatting.None));
            output.StatusCode = response.StatusCode;
            output.KeepAlive = false;
            output.ContentType = "application/json; charset=utf-8";
            output.ContentLength = payload.Length;
            try { await output.WriteAsync(payload).ConfigureAwait(false); }
            catch (Exception) { output.Abort("client gone"); }
            Log?.Invoke(request.Method + " " + path + " key=" + (keyId ?? "-") + " -> " + response.StatusCode);
        }

        private async Task<ApiResponse> ProcessAsync(MiniHttpRequest request, string path, string keyId)
        {
            var isCommand = request.Method == "POST" && path == "/api";
            var isStatus = request.Method == "GET" && path == "/api/status";
            if (!isCommand && !isStatus) return ApiResponse.Failure(404, "not_found", "Use POST /api or GET /api/status");

            if (request.ContentLength > maxBodyBytes) return ApiResponse.Failure(413, "too_large", "Body exceeds " + maxBodyBytes + " bytes");
            byte[] body;
            try { body = await request.ReadBodyAsync(maxBodyBytes).ConfigureAwait(false); }
            catch (Exception) { return ApiResponse.Failure(400, "invalid_request", "The request body did not arrive in time"); }
            if (body == null) return ApiResponse.Failure(413, "too_large", "Body exceeds " + maxBodyBytes + " bytes");

            var auth = authenticator.Authenticate(request.Method, path, keyId, request.Header(ApiHeaders.Time),
                request.Header(ApiHeaders.Nonce), request.Header(ApiHeaders.Signature), body);
            if (!auth.Succeeded)
            {
                Log?.Invoke("auth rejected: " + auth.Failure + " key=" + (keyId ?? "-"));
                return ApiResponse.Failure(401, "unauthorized", "Missing, stale, replayed or invalid request signature");
            }

            if (isStatus)
            {
                if (body.Length != 0) return ApiResponse.Failure(400, "invalid_request", "GET /api/status takes no body");
                return await router.ExecuteAsync("status", null, auth.Key.Scopes, lifetime.Token).ConfigureAwait(false);
            }

            string text;
            try { text = new UTF8Encoding(false, true).GetString(body); }
            catch (ArgumentException) { return ApiResponse.Failure(400, "invalid_request", "Body must be UTF-8 JSON"); }
            return await router.ExecuteJsonAsync(text, auth.Key.Scopes, lifetime.Token).ConfigureAwait(false);
        }
    }
}
