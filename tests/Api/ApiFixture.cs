using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Api;
using Ru7100.Fakes;

namespace Ru7100.Tests.Api
{
    /// A router wired to the fakes with a controllable clock and a seeded library:
    /// Breaking Bad watching (S2E1, 5 min in), Space Force caught up, Fresh Show unwatched with 4 new episodes, Sintel unstarted.
    public sealed class ApiFixture
    {
        public DateTimeOffset Now = FakeData.Now;

        public ApiFixture()
        {
            Func<DateTimeOffset> clock = () => Now;
            Catalog = FakeData.Catalog();
            Library = new FakeLibrary(clock);
            Sources = new FakeSourceResolver();
            Addons = new FakeAddonRegistry();
            Account = new FakeAccount();
            Store = new InMemoryStore();
            Playback = new FakePlayback(Catalog, Library, clock);
            Recommendations = new KeyValueRecommendationsStore(Store, clock);

            var bb = Meta("tt0903747");
            Library.Put(bb, e =>
            {
                e.State.VideoId = "tt0903747:2:1";
                e.State.TimeOffsetMs = 300_000;
                e.State.DurationMs = 2_700_000;
                e.State.LastWatched = Now.AddYears(-1);
            }, "tt0903747:1:1", "tt0903747:1:2", "tt0903747:1:3");
            Library.Put(Meta("tt7529770"), e => e.State.LastWatched = Now.AddYears(-1),
                "tt7529770:1:1", "tt7529770:1:2", "tt7529770:1:3");
            Library.Put(Meta("tt9999001"), e => e.State.LastWatched = Now.AddDays(-30));
            Library.Put(Meta("tt1727587", MediaType.Movie));

            Router = new ApiRouter(new ApiServices
            {
                Playback = Playback, Catalog = Catalog, Library = Library, Sources = Sources,
                Addons = Addons, Account = Account, Store = Store, Clock = clock, Recommendations = Recommendations,
            });
        }

        public FakeCatalog Catalog { get; }
        public FakeLibrary Library { get; }
        public FakeSourceResolver Sources { get; }
        public FakeAddonRegistry Addons { get; }
        public FakeAccount Account { get; }
        public InMemoryStore Store { get; }
        public FakePlayback Playback { get; }
        public KeyValueRecommendationsStore Recommendations { get; }
        public ApiRouter Router { get; }

        public MetaDetail Meta(string id, MediaType type = MediaType.Series) =>
            Catalog.GetMetaAsync(type, id, CancellationToken.None).Result;

        public static readonly IReadOnlyCollection<string> AdminKey = new[] { ApiScopes.Admin };

        public Task<ApiResponse> Run(string command, string args = null, IReadOnlyCollection<string> scopes = null) =>
            Router.ExecuteAsync(command, args == null ? null : (JObject)ApiJson.Parse(args), scopes ?? AdminKey, CancellationToken.None);

        public async Task<JToken> Ok(string command, string args = null, IReadOnlyCollection<string> scopes = null)
        {
            var response = await Run(command, args, scopes);
            if (response.StatusCode != 200) throw new Xunit.Sdk.XunitException(command + " failed: " + response.Body);
            return response.Body["result"];
        }
    }
}
