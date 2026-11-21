// Transport-independent command router: {command, args} -> handler, with per-command scopes and strict arguments.
// Success: {"ok":true,"result":...}. Failure: {"ok":false,"error":{"code","message"}} with an HTTP-style status.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Api
{
    public sealed class ApiServices
    {
        public IPlaybackCommands Playback { get; set; }
        public ICatalog Catalog { get; set; }
        public ILibrary Library { get; set; }
        public ISourceResolver Sources { get; set; }
        public IAddonRegistry Addons { get; set; }
        public IStremioAccount Account { get; set; }
        public IKeyValueStore Store { get; set; }          // new-episode alerts
        public IRecommendationsStore Recommendations { get; set; }
        public Func<DateTimeOffset> Clock { get; set; }
    }

    public sealed class ApiResponse
    {
        public ApiResponse(int statusCode, JObject body)
        {
            StatusCode = statusCode;
            Body = body;
        }

        public int StatusCode { get; }
        public JObject Body { get; }
        public bool Ok => StatusCode >= 200 && StatusCode < 300;

        public static ApiResponse Success(object result) =>
            new ApiResponse(200, new JObject { ["ok"] = true, ["result"] = ApiJson.ToToken(result) });

        public static ApiResponse Failure(int statusCode, string code, string message) =>
            new ApiResponse(statusCode, new JObject { ["ok"] = false, ["error"] = new JObject { ["code"] = code, ["message"] = message } });
    }

    public delegate Task<object> ApiHandler(ApiArgs args, CancellationToken token);

    public sealed class ApiCommand
    {
        public ApiCommand(string name, IReadOnlyCollection<string> scopes, IReadOnlyCollection<string> args, ApiHandler handler)
        {
            Name = name;
            Scopes = scopes;
            Args = args;
            Handler = handler;
        }

        public string Name { get; }
        public IReadOnlyCollection<string> Scopes { get; }   // any one of these (or admin) grants access
        public IReadOnlyCollection<string> Args { get; }     // allowed argument names
        public ApiHandler Handler { get; }
    }

    public sealed class ApiRouter
    {
        public const string Version = "1";
        private static readonly string[] Qualities = { "auto", "4k", "1080p" };
        private static readonly string[] Modes = { "single", "binge" };
        private static readonly string[] SearchTypes = { "auto", "series", "movie" };
        private static readonly string[] LibraryStatuses = { "all", "watching", "unwatched", "caught_up", "library" };
        private static readonly string[] None = new string[0];
        private static readonly string[] PlaybackOnly = { ApiScopes.Playback };
        private static readonly string[] LibraryOnly = { ApiScopes.Library };
        private static readonly string[] AdminOnly = { ApiScopes.Admin };
        private static readonly string[] RecommendationsOnly = { ApiScopes.Recommendations };
        private static readonly string[] PlaybackOrLibrary = { ApiScopes.Playback, ApiScopes.Library };
        private static readonly string[] AnyScope = { ApiScopes.Playback, ApiScopes.Library, ApiScopes.Recommendations };

        private readonly Dictionary<string, ApiCommand> commands = new Dictionary<string, ApiCommand>(StringComparer.Ordinal);
        private readonly ApiServices services;
        private readonly LibraryCommands library;
        private readonly object linkLock = new object();
        private string pendingLinkCode;

        public ApiRouter(ApiServices services)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            if (services.Catalog != null && services.Library != null)
                library = new LibraryCommands(services.Catalog, services.Library, services.Sources, services.Store, services.Clock);
            RegisterBuiltIns();
        }

        public IEnumerable<ApiCommand> Commands => commands.Values;

        public ApiCommand Find(string name) => name != null && commands.TryGetValue(name, out var command) ? command : null;

        /// Lets the host add commands (for example recommendation updates under the recommendations scope).
        public void Register(ApiCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command.Scopes == null || command.Scopes.Count == 0 || command.Scopes.Any(s => !ApiScopes.IsKnown(s)))
                throw new ArgumentException("Command scopes must be known scope names", nameof(command));
            commands[command.Name] = command;
        }

        /// Parses a {"command": "...", "args": {...}} body and runs it.
        public Task<ApiResponse> ExecuteJsonAsync(string body, IReadOnlyCollection<string> scopes, CancellationToken token)
        {
            JObject request;
            try { request = ApiJson.Parse(body ?? "") as JObject; }
            catch (Exception) { request = null; }
            if (request == null) return Task.FromResult(ApiResponse.Failure(400, "invalid_request", "Body must be a JSON object {command, args}"));
            var unexpected = request.Properties().Select(p => p.Name).Where(n => n != "command" && n != "args").ToList();
            if (unexpected.Count > 0)
                return Task.FromResult(ApiResponse.Failure(400, "invalid_request", "Unexpected field: " + string.Join(", ", unexpected)));
            var command = request["command"];
            if (command == null || command.Type != JTokenType.String)
                return Task.FromResult(ApiResponse.Failure(400, "invalid_request", "command must be a string"));
            var args = request["args"];
            if (args != null && args.Type != JTokenType.Null && args.Type != JTokenType.Object)
                return Task.FromResult(ApiResponse.Failure(400, "invalid_request", "args must be an object"));
            return ExecuteAsync((string)command, args as JObject, scopes, token);
        }

        public async Task<ApiResponse> ExecuteAsync(string name, JObject args, IReadOnlyCollection<string> scopes, CancellationToken token)
        {
            var command = Find(name);
            if (command == null) return ApiResponse.Failure(404, "unknown_command", "Unknown command: " + name);
            if (!ApiScopes.Allows(scopes, command.Scopes))
                return ApiResponse.Failure(403, "forbidden", "This key lacks the " + string.Join(" or ", command.Scopes) + " scope for " + name);
            try
            {
                var reader = new ApiArgs(args);
                reader.AssertAllowed(command.Args);
                var result = await command.Handler(reader, token).ConfigureAwait(false);
                return ApiResponse.Success(result);
            }
            catch (ApiException error)
            {
                return ApiResponse.Failure(error.StatusCode, error.Code, error.Message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return ApiResponse.Failure(503, "cancelled", "The request was cancelled");
            }
            catch (NotSupportedException error)
            {
                return ApiResponse.Failure(501, "not_supported", error.Message);
            }
            catch (InvalidOperationException error)
            {
                return ApiResponse.Failure(409, "conflict", error.Message);
            }
            catch (Exception error)
            {
                return ApiResponse.Failure(500, "internal_error", error.Message);
            }
        }

        private void Add(string name, string[] scopes, string[] args, ApiHandler handler) =>
            commands[name] = new ApiCommand(name, scopes, args, handler);

        private IPlaybackCommands Playback => services.Playback ?? throw new ApiException("unavailable", "Playback is not available", 503);
        private LibraryCommands LibraryService => library ?? throw new ApiException("unavailable", "The library is not available", 503);
        private IAddonRegistry Addons => services.Addons ?? throw new ApiException("unavailable", "Add-ons are not available", 503);
        private IRecommendationsStore RecommendationStore => services.Recommendations ?? throw new ApiException("unavailable", "Recommendations are not available", 503);
        private IStremioAccount Account => services.Account ?? throw new ApiException("unavailable", "The Stremio account is not available", 503);

        private void RegisterBuiltIns()
        {
            Add("status", AnyScope, new[] { "scope" }, async (a, t) =>
            {
                var scope = a.Choice("scope", new[] { "all", "stremio_playback" }, "all");
                var playback = services.Playback == null ? null : await services.Playback.GetStatusAsync(t).ConfigureAwait(false);
                if (scope == "stremio_playback") return playback ?? throw new ApiException("unavailable", "Playback is not available", 503);
                return new
                {
                    api = new { version = Version, commands = commands.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() },
                    playback,
                    account = services.Account == null ? null : new { authenticated = services.Account.IsSignedIn },
                };
            });

            // ---- playback (same commands as the remote) ----
            Add("playback.play", PlaybackOnly, new[] { "query", "season", "episode", "confirmed", "mode", "quality", "maxSizeGb", "positionSeconds" }, async (a, t) =>
            {
                var request = new PlayRequest
                {
                    Query = a.RequireString("query"),
                    Season = a.OptionalInt("season", 0, 10000),
                    Episode = a.OptionalInt("episode", 0, 100000),
                    Confirmed = a.OptionalBool("confirmed") ?? false,
                    Mode = a.Choice("mode", Modes, "single"),
                    Quality = a.Choice("quality", Qualities, "auto"),
                    MaxSizeGb = a.OptionalNumber("maxSizeGb", 0, double.PositiveInfinity, exclusiveMin: true),
                    PositionSeconds = a.OptionalNumber("positionSeconds", 0),
                };
                if ((request.Season == null) != (request.Episode == null))
                    throw ApiException.Missing(request.Season == null ? "season" : "episode", "Both season and episode are required");
                return await Playback.PlayAsync(request, t).ConfigureAwait(false);
            });
            Add("playback.pause", PlaybackOnly, None, async (a, t) => await Playback.PauseAsync(t).ConfigureAwait(false));
            Add("playback.resume", PlaybackOnly, None, async (a, t) => await Playback.ResumeAsync(t).ConfigureAwait(false));
            Add("playback.stop", PlaybackOnly, None, async (a, t) => await Playback.StopAsync(t).ConfigureAwait(false));
            Add("playback.seek", PlaybackOnly, new[] { "positionSeconds" }, async (a, t) =>
            {
                var position = a.OptionalNumber("positionSeconds", 0) ?? throw ApiException.Missing("positionSeconds");
                return await Playback.SeekAsync(position, t).ConfigureAwait(false);
            });
            Add("playback.enable_binge", PlaybackOnly, None, async (a, t) => await Playback.SetBingeAsync(true, t).ConfigureAwait(false));
            Add("playback.disable_binge", PlaybackOnly, None, async (a, t) => await Playback.SetBingeAsync(false, t).ConfigureAwait(false));
            Add("playback.stop_after_current", PlaybackOnly, new[] { "enabled" }, async (a, t) =>
                await Playback.SetStopAfterCurrentAsync(a.OptionalBool("enabled") ?? true, t).ConfigureAwait(false));
            Add("playback.select_audio", PlaybackOnly, new[] { "index" }, async (a, t) =>
                await Playback.SelectAudioAsync(a.RequireInt("index", 0, 255), t).ConfigureAwait(false));
            Add("playback.select_subtitle", PlaybackOnly, new[] { "index" }, async (a, t) =>
                await Playback.SelectSubtitleAsync(a.RequireInt("index", -1, 255), t).ConfigureAwait(false));
            Add("playback.subtitle_size", PlaybackOnly, new[] { "size" }, async (a, t) =>
                await Playback.SetSubtitleSizeAsync(a.RequireInt("size", PlaybackLimits.SubtitleSizeMin, PlaybackLimits.SubtitleSizeMax), t).ConfigureAwait(false));
            Add("playback.subtitle_offset", PlaybackOnly, new[] { "offsetMs" }, async (a, t) =>
                await Playback.SetSubtitleOffsetAsync(a.RequireInt("offsetMs", -PlaybackLimits.SubtitleOffsetMaxMs, PlaybackLimits.SubtitleOffsetMaxMs), t).ConfigureAwait(false));

            // ---- queue ----
            var queueArgs = new[] { "items", "quality", "maxSizeGb" };
            Add("queue.set", PlaybackOnly, queueArgs, async (a, t) =>
            {
                var request = ReadQueueRequest(a) ?? throw ApiException.Missing("items", "items are required for set");
                return Named("set", await Playback.SetQueueAsync(request, t).ConfigureAwait(false));
            });
            Add("queue.start", PlaybackOnly, queueArgs, async (a, t) =>
            {
                var request = ReadQueueRequest(a);
                if (request == null && (a.Has("quality") || a.Has("maxSizeGb")))
                    throw ApiException.Irrelevant("quality and maxSizeGb need items");
                if (request != null)
                {
                    var set = await Playback.SetQueueAsync(request, t).ConfigureAwait(false);
                    if (set != null && set.Status == "ambiguous") return Named("start", set);
                }
                return Named("start", await Playback.StartQueueAsync(t).ConfigureAwait(false));
            });
            Add("queue.status", PlaybackOnly, None, async (a, t) =>
            {
                var status = await Playback.GetStatusAsync(t).ConfigureAwait(false);
                return new QueueResult { Action = "status", Queue = status?.Queue };
            });
            Add("queue.clear", PlaybackOnly, None, async (a, t) => Named("clear", await Playback.ClearQueueAsync(t).ConfigureAwait(false)));
            Add("queue.skip", PlaybackOnly, None, async (a, t) => Named("skip", await Playback.SkipQueueAsync(t).ConfigureAwait(false)));

            // ---- discovery and library ----
            Add("library.search", PlaybackOrLibrary, new[] { "query", "type", "limit" }, async (a, t) =>
            {
                var query = a.RequireString("query");
                var type = a.Choice("type", SearchTypes, "auto");
                var limit = a.OptionalInt("limit", 1, 20) ?? 8;
                return await LibraryService.Titles.SearchAsync(query, ParseType(type), limit, t).ConfigureAwait(false);
            });
            Add("library.list", PlaybackOrLibrary, new[] { "query", "status" }, async (a, t) =>
                await LibraryService.ListAsync(a.OptionalString("query"), a.Choice("status", LibraryStatuses, "all"), t).ConfigureAwait(false));
            Add("library.new_episodes", PlaybackOrLibrary, new[] { "query", "sinceLastCheck", "acknowledge" }, async (a, t) =>
                await LibraryService.NewEpisodesAsync(a.OptionalString("query"), a.OptionalBool("sinceLastCheck") ?? false,
                    a.OptionalBool("acknowledge") ?? false, t).ConfigureAwait(false));
            Add("library.add", LibraryOnly, new[] { "queryOrId" }, async (a, t) =>
                await LibraryService.UpdateAsync(a.RequireString("queryOrId"), "add", true, t).ConfigureAwait(false));
            Add("library.remove", LibraryOnly, new[] { "queryOrId" }, async (a, t) =>
                await LibraryService.UpdateAsync(a.RequireString("queryOrId"), "remove", true, t).ConfigureAwait(false));
            Add("library.set_notifications", LibraryOnly, new[] { "queryOrId", "notifications" }, async (a, t) =>
            {
                var id = a.RequireString("queryOrId");
                return await LibraryService.UpdateAsync(id, "notifications", a.RequireBool("notifications"), t).ConfigureAwait(false);
            });
            Add("library.mark_watched", LibraryOnly, new[] { "queryOrId", "season", "episode", "watched" }, async (a, t) =>
            {
                var id = a.RequireString("queryOrId");
                return await LibraryService.MarkWatchedAsync(id, a.OptionalInt("season", 0, 10000), a.OptionalInt("episode", 0, 100000),
                    a.OptionalBool("watched") ?? true, t).ConfigureAwait(false);
            });

            // ---- add-ons ----
            Add("addons.list", PlaybackOrLibrary, None, async (a, t) =>
                new { addons = await Addons.GetInstalledAsync(t).ConfigureAwait(false) ?? Array.Empty<AddonDescriptor>() });
            Add("addons.install", AdminOnly, new[] { "manifestUrl" }, async (a, t) =>
            {
                var url = a.RequireString("manifestUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || (parsed.Scheme != "https" && parsed.Scheme != "http")
                    || !parsed.AbsolutePath.EndsWith("/manifest.json", StringComparison.Ordinal))
                    throw ApiException.Invalid("manifestUrl must be an http(s) URL ending in /manifest.json");
                var addon = await Addons.InstallAsync(url, t).ConfigureAwait(false);
                return new { ok = true, action = "install", addon };
            });
            Add("addons.remove", AdminOnly, new[] { "addonId" }, async (a, t) =>
            {
                var id = a.RequireString("addonId");
                await Addons.RemoveAsync(id, t).ConfigureAwait(false);
                return new { ok = true, action = "remove", addonId = id };
            });

            // ---- Stremio account ----
            Add("account.status", AnyScope, None, (a, t) =>
                Task.FromResult<object>(new { authenticated = Account.IsSignedIn, pendingCode = PendingCode() }));
            Add("account.link_begin", AdminOnly, None, async (a, t) =>
            {
                var link = await Account.BeginLinkAsync(t).ConfigureAwait(false);
                lock (linkLock) pendingLinkCode = link.Code;
                return new
                {
                    code = link.Code,
                    link = link.Link,
                    expiresInMinutes = 10,
                    next = "Open the link, approve it, then call account with action=link_complete.",
                };
            });
            Add("account.link_complete", AdminOnly, new[] { "code" }, async (a, t) =>
            {
                var code = a.OptionalString("code") ?? PendingCode()
                    ?? throw ApiException.Missing("code", "No pending Stremio link code. Call account link_begin first.");
                if (await Account.CompleteLinkAsync(code, t).ConfigureAwait(false))
                {
                    lock (linkLock) if (pendingLinkCode == code) pendingLinkCode = null;
                    return new { authenticated = true };
                }
                return new { authenticated = false, pending = true, code, message = "Approve the device link, then try again. Start a new link if it expired." };
            });
            Add("account.sign_out", AdminOnly, None, async (a, t) =>
            {
                await Account.SignOutAsync(t).ConfigureAwait(false);
                return new { ok = true, authenticated = false };
            });

            // ---- recommendations (pushed by the Mac's scheduled publisher; the TV keeps the last good snapshot) ----
            Add("recommendations.set", RecommendationsOnly, new[] { "rows", "generatedAt", "source", "expectedVersion" }, async (a, t) =>
            {
                var rows = RecommendationValidator.ParseRows(a.OptionalArray("rows"));
                var generatedAt = a.OptionalString("generatedAt");
                var source = a.OptionalString("source");
                if (generatedAt != null && generatedAt.Length > 40) throw ApiException.Invalid("generatedAt may be at most 40 characters");
                if (source != null && source.Length > 120) throw ApiException.Invalid("source may be at most 120 characters");
                var expected = ReadSequence(a, "expectedVersion");
                var stored = await RecommendationStore.ReplaceAsync(new RecommendationSnapshot { GeneratedAt = generatedAt, Source = source, Rows = rows }, expected, t).ConfigureAwait(false);
                return new
                {
                    ok = true,
                    version = stored.Version,
                    publishedAt = stored.PublishedAt,
                    rows = stored.Rows.Count,
                    items = stored.Rows.Sum(r => r.Items.Count),
                };
            });
            Add("recommendations.get", RecommendationsOnly, None, async (a, t) =>
                await RecommendationStore.GetAsync(t).ConfigureAwait(false));
            Add("recommendations.feedback.list", RecommendationsOnly, new[] { "afterSequence", "limit" }, async (a, t) =>
                await RecommendationStore.ListFeedbackAsync(ReadSequence(a, "afterSequence"), a.OptionalInt("limit", 1, 1000) ?? 200, t).ConfigureAwait(false));
            Add("recommendations.feedback.ack", RecommendationsOnly, new[] { "sequence" }, async (a, t) =>
            {
                var sequence = ReadSequence(a, "sequence") ?? throw ApiException.Missing("sequence");
                return new { ok = true, ackedSequence = await RecommendationStore.AckFeedbackAsync(sequence, t).ConfigureAwait(false) };
            });
        }

        private static long? ReadSequence(ApiArgs a, string name)
        {
            var value = a.OptionalNumber(name, 0, 9007199254740991d);
            if (value == null) return null;
            if (Math.Floor(value.Value) != value.Value) throw ApiException.Invalid(name + " must be an integer");
            return (long)value.Value;
        }

        private string PendingCode()
        {
            lock (linkLock) return pendingLinkCode;
        }

        private static MediaType? ParseType(string type) =>
            type == "series" ? MediaType.Series : type == "movie" ? (MediaType?)MediaType.Movie : null;

        private static QueueResult Named(string action, QueueResult result)
        {
            result = result ?? new QueueResult();
            if (result.Action == null) result.Action = action;
            return result;
        }

        private static QueueRequest ReadQueueRequest(ApiArgs a)
        {
            var items = a.OptionalArray("items");
            if (items == null) return null;
            if (items.Count == 0) throw ApiException.Invalid("items must be a non-empty array");
            if (items.Count > 100) throw ApiException.Invalid("items may hold at most 100 entries");
            var parsed = new List<QueueItemRequest>();
            for (var index = 0; index < items.Count; index++)
            {
                if (!(items[index] is JObject entry)) throw ApiException.Invalid("items[" + index + "] must be an object");
                var item = new ApiArgs(entry);
                try
                {
                    item.AssertAllowed(new[] { "query", "season", "episode" });
                    var request = new QueueItemRequest
                    {
                        Query = item.RequireString("query"),
                        Season = item.OptionalInt("season", 0, 10000),
                        Episode = item.OptionalInt("episode", 0, 100000),
                    };
                    if ((request.Season == null) != (request.Episode == null))
                        throw ApiException.Missing("episode", "Both season and episode are required");
                    parsed.Add(request);
                }
                catch (ApiException error)
                {
                    throw new ApiException(error.Code, "items[" + index + "]: " + error.Message, error.StatusCode);
                }
            }
            return new QueueRequest
            {
                Items = parsed,
                Quality = a.Choice("quality", Qualities, "auto"),
                MaxSizeGb = a.OptionalNumber("maxSizeGb", 0, double.PositiveInfinity, exclusiveMin: true),
            };
        }
    }
}
