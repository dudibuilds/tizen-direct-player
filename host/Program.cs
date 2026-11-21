// ru7100-host: the TV app's agent API (core/Api) running on the Mac, for MCP development and tests.
//
//   dotnet run --project host -- --keys <file> [--port 45561] [--bind 127.0.0.1] [--data <dir>] [--services fakes|real] [--verbose]
//   dotnet run --project host -- --generate-keys <file>
//
// --services fakes (default): in-memory catalog, library and a simulated engine (tests/Api/Fakes); no network.
// --services real: the TV composition (core/App AppHost) with the real add-ons, Cinemeta catalog, source ranking,
//   Stremio account and library, and the real PlaybackSession, over a simulated player and source opener: nothing
//   is downloaded or decoded on the Mac. Search, ranking and progress sync talk to the real services.
//
// Prints "ru7100-host listening on <url>" once ready. Ctrl+C or SIGTERM stops it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Api;
using Ru7100.Core.App;
using Ru7100.Fakes;

namespace Ru7100.Host
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var options = ParseOptions(args);
            if (options == null) return Usage();

            if (options.TryGetValue("generate-keys", out var generatePath))
            {
                GenerateKeys(generatePath);
                Console.WriteLine("Wrote admin, playback, library and recommendations keys to " + generatePath);
                return 0;
            }

            if (!options.TryGetValue("keys", out var keysPath)) return Usage("--keys is required");
            var keys = ApiKey.ParseKeyFile(await File.ReadAllTextAsync(keysPath));
            var port = options.TryGetValue("port", out var portText) ? int.Parse(portText) : 45561;
            var bind = options.TryGetValue("bind", out var bindText) ? bindText : "127.0.0.1";
            var services = options.TryGetValue("services", out var servicesText) ? servicesText : "fakes";
            if (services != "fakes" && services != "real") return Usage("--services must be fakes or real");
            var verbose = options.ContainsKey("verbose");

            IKeyValueStore store = options.TryGetValue("data", out var dataDir) ? new FileKeyValueStore(dataDir) : new InMemoryStore();
            ApiRouter router;
            IRecommendationsStore recommendations;
            AppHost app = null;
            if (services == "real")
            {
                app = new AppHost(new AppPlatform
                {
                    Store = store,
                    Player = new SimulatedPlayer(),
                    Opener = new SimulatedOpener(),
                    Log = verbose ? line => Console.Error.WriteLine(line) : (Action<string>)null,
                });
                await app.InitializeAsync(CancellationToken.None);
                router = app.Router;
                recommendations = app.Recommendations;
            }
            else
            {
                var catalog = FakeData.Catalog();
                var library = SeedLibrary(catalog);
                var fakeRecommendations = new KeyValueRecommendationsStore(store);
                recommendations = fakeRecommendations;
                router = new ApiRouter(new ApiServices
                {
                    Playback = new FakePlayback(catalog, library),
                    Catalog = catalog,
                    Library = library,
                    Sources = new FakeSourceResolver(),
                    Addons = new FakeAddonRegistry(),
                    Account = new FakeAccount(),
                    Store = store,
                    Recommendations = fakeRecommendations,
                });
            }

            // Host-only helper: stands in for the TV UI/player recording a feedback event.
            router.Register(new ApiCommand("dev.record_feedback", new[] { ApiScopes.Admin },
                new[] { "imdbId", "videoId", "kind", "positionMs", "durationMs" }, async (a, t) =>
                {
                    var position = a.OptionalNumber("positionMs", 0);
                    var duration = a.OptionalNumber("durationMs", 0);
                    return await recommendations.RecordFeedbackAsync(new FeedbackEvent
                    {
                        ImdbId = a.RequireString("imdbId"),
                        VideoId = a.OptionalString("videoId"),
                        Kind = a.RequireString("kind"),
                        PositionMs = position == null ? (long?)null : (long)position.Value,
                        DurationMs = duration == null ? (long?)null : (long)duration.Value,
                    }, t);
                }));

            var prefix = "http://" + bind + ":" + port + "/";
            using (var server = new ApiHttpServer(router, new ApiAuthenticator(keys), new[] { prefix }))
            {
                if (verbose) server.Log += line => Console.Error.WriteLine(line);
                server.Start();
                Console.WriteLine("ru7100-host listening on " + prefix + " (" + keys.Count + " keys, services=" + services + ")");
                var stop = new TaskCompletionSource<bool>();
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; stop.TrySetResult(true); };
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => stop.TrySetResult(true);
                await stop.Task;
            }
            if (app != null)
            {
                await app.ShutdownAsync(CancellationToken.None);
                app.Dispose();
            }
            return 0;
        }

        private static FakeLibrary SeedLibrary(FakeCatalog catalog)
        {
            var library = new FakeLibrary(() => DateTimeOffset.UtcNow);
            MetaDetail Meta(string id, MediaType type) => catalog.GetMetaAsync(type, id, CancellationToken.None).Result;
            library.Put(Meta("tt0903747", MediaType.Series), e =>
            {
                e.State.VideoId = "tt0903747:2:1";
                e.State.TimeOffsetMs = 300_000;
                e.State.DurationMs = 2_700_000;
                e.State.LastWatched = DateTimeOffset.UtcNow.AddYears(-1);
            }, "tt0903747:1:1", "tt0903747:1:2", "tt0903747:1:3");
            library.Put(Meta("tt9999001", MediaType.Series), e => e.State.LastWatched = FakeData.Now.AddDays(-30));
            library.Put(Meta("tt1727587", MediaType.Movie));
            return library;
        }

        private static void GenerateKeys(string path)
        {
            string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var keys = new JArray(
                Key("admin", Secret(), ApiScopes.Admin),
                Key("playback", Secret(), ApiScopes.Playback),
                Key("library", Secret(), ApiScopes.Library),
                Key("recommendations", Secret(), ApiScopes.Recommendations));
            File.WriteAllText(path, new JObject { ["keys"] = keys }.ToString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static JObject Key(string id, string secret, params string[] scopes) =>
            new JObject { ["id"] = id, ["secret"] = secret, ["scopes"] = new JArray(scopes) };

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) return null;
                var name = args[i].Substring(2);
                if (name == "verbose") { options[name] = "1"; continue; }
                if (i + 1 >= args.Length) return null;
                options[name] = args[++i];
            }
            return options;
        }

        private static int Usage(string problem = null)
        {
            if (problem != null) Console.Error.WriteLine(problem);
            Console.Error.WriteLine("usage: ru7100-host --keys <file> [--port 45561] [--bind 127.0.0.1] [--data <dir>] [--services fakes|real] [--verbose]");
            Console.Error.WriteLine("       ru7100-host --generate-keys <file>");
            return 64;
        }
    }
}
