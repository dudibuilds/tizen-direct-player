using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    public sealed class StremioUser
    {
        public string Id { get; set; }
        public string Email { get; set; }
        public string Avatar { get; set; }
        public string DateRegistered { get; set; }
    }

    /// Supplies the signed-in auth key to the library. Implemented by StremioAccount.
    public interface IStremioAuthKeyProvider
    {
        /// Throws StremioNotSignedInException when there is no key.
        Task<string> GetAuthKeyAsync(CancellationToken token);
    }

    /// Device-link sign-in (A1), auth check (A3) and add-on collection (A4).
    /// Call InitializeAsync once at startup so IsSignedIn reflects the stored key.
    public sealed class StremioAccount : IStremioAccount, IStremioAuthKeyProvider
    {
        public static readonly TimeSpan LinkCodeLifetime = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan AddonCacheTtl = TimeSpan.FromSeconds(60);
        static readonly Regex PendingPattern = new Regex("invalid|expired|token|unknown", RegexOptions.IgnoreCase);

        readonly StremioApiClient api;
        readonly IKeyValueStore store;
        readonly Func<DateTimeOffset> now;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        string authKey;
        bool loaded;
        IReadOnlyList<AddonDescriptor> addonCache;
        DateTimeOffset addonCacheExpires;

        public StremioAccount(StremioApiClient api, IKeyValueStore store, Func<DateTimeOffset> now = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public bool IsSignedIn => !string.IsNullOrEmpty(authKey);

        public async Task InitializeAsync(CancellationToken token)
        {
            var saved = await StoreJson.ReadAsync(store, AccountKeys.StremioAuth, token).ConfigureAwait(false);
            authKey = (string)saved?["authKey"];
            loaded = true;
        }

        public async Task<string> GetAuthKeyAsync(CancellationToken token)
        {
            if (!loaded) await InitializeAsync(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(authKey)) throw new StremioNotSignedInException();
            return authKey;
        }

        /// A1 start. The code is valid about 10 minutes; show Link (or a QR of it) on the TV.
        public async Task<(string Code, string Link)> BeginLinkAsync(CancellationToken token)
        {
            var created = await api.CreateLinkAsync(token).ConfigureAwait(false);
            await StoreJson.WriteAsync(store, AccountKeys.StremioPendingLink, new JObject
            {
                ["code"] = created.Code,
                ["link"] = created.Link,
                ["createdAt"] = IsoTime.Format(now()),
            }, token).ConfigureAwait(false);
            return created;
        }

        /// A1 complete. Returns false while the link is not yet approved. Throws StremioLinkExpiredException
        /// once a pending code is older than 10 minutes (start a new link). code may be null to use the pending one.
        public async Task<bool> CompleteLinkAsync(string code, CancellationToken token)
        {
            var pending = await StoreJson.ReadAsync(store, AccountKeys.StremioPendingLink, token).ConfigureAwait(false);
            string authCode = (code ?? (string)pending?["code"] ?? "").Trim();
            if (authCode.Length == 0) throw new InvalidOperationException("No pending Stremio link code. Call BeginLinkAsync first.");
            DateTimeOffset? createdAt = string.Equals((string)pending?["code"], authCode, StringComparison.Ordinal)
                ? IsoTime.Parse(pending?["createdAt"]) : null;

            JToken result;
            try
            {
                result = await api.ReadLinkAsync(authCode, token).ConfigureAwait(false);
            }
            catch (StremioApiException error) when (PendingPattern.IsMatch(error.Message))
            {
                return PendingOrExpired(authCode, createdAt);
            }
            var key = (string)result?["authKey"] ?? (string)result?["auth_key"];
            if (string.IsNullOrEmpty(key)) return PendingOrExpired(authCode, createdAt);

            await StoreJson.WriteAsync(store, AccountKeys.StremioAuth, new JObject
            {
                ["authKey"] = key,
                ["linkedAt"] = IsoTime.Format(now()),
            }, token).ConfigureAwait(false);
            await StoreJson.WriteAsync(store, AccountKeys.StremioPendingLink, null, token).ConfigureAwait(false);
            authKey = key;
            loaded = true;
            InvalidateAddonCache();
            return true;
        }

        bool PendingOrExpired(string code, DateTimeOffset? createdAt)
        {
            if (createdAt.HasValue && now() - createdAt.Value > LinkCodeLifetime) throw new StremioLinkExpiredException(code);
            return false;
        }

        /// Raised after sign-out so a live StremioLibrary drops its in-memory cache and outbox.
        public event EventHandler SignedOut;

        /// Clears the local auth key, the library cache and any writes not yet synced, so nothing
        /// from this account leaks into the next one. The key is not revoked server-side.
        public async Task SignOutAsync(CancellationToken token)
        {
            await StoreJson.WriteAsync(store, AccountKeys.StremioAuth, null, token).ConfigureAwait(false);
            await StoreJson.WriteAsync(store, AccountKeys.StremioOutbox, null, token).ConfigureAwait(false);
            await StoreJson.WriteAsync(store, AccountKeys.StremioLibraryCache, null, token).ConfigureAwait(false);
            authKey = null;
            loaded = true;
            InvalidateAddonCache();
            SignedOut?.Invoke(this, EventArgs.Empty);
        }

        /// A3: getUser. Throws StremioApiException when the key is rejected.
        public async Task<StremioUser> GetUserAsync(CancellationToken token)
        {
            var key = await GetAuthKeyAsync(token).ConfigureAwait(false);
            var user = await api.PostAsync("getUser", new JObject { ["authKey"] = key }, token).ConfigureAwait(false);
            if (user == null || user.Type != JTokenType.Object) return null;
            return new StremioUser
            {
                Id = (string)user["_id"] ?? (string)user["id"],
                Email = (string)user["email"],
                Avatar = (string)user["avatar"],
                DateRegistered = user["dateRegistered"]?.Type == JTokenType.Null ? null : user["dateRegistered"]?.ToString(),
            };
        }

        /// A4: addonCollectionGet {authKey, update:true}, cached 60 s (failures are not cached).
        /// Returns every installed add-on in account order. Filtering (local cast add-on, Torrentio /
        /// OpenSubtitles fallbacks) belongs to the add-on registry.
        public async Task<IReadOnlyList<AddonDescriptor>> GetAddonCollectionAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (addonCache != null && now() < addonCacheExpires) return addonCache;
                var key = await GetAuthKeyAsync(token).ConfigureAwait(false);
                var result = await api.PostAsync("addonCollectionGet", new JObject { ["authKey"] = key, ["update"] = true }, token).ConfigureAwait(false);
                var list = MapAddons(result);
                addonCache = list;
                addonCacheExpires = now() + AddonCacheTtl;
                return list;
            }
            finally
            {
                gate.Release();
            }
        }

        void InvalidateAddonCache()
        {
            addonCache = null;
        }

        internal static IReadOnlyList<AddonDescriptor> MapAddons(JToken result)
        {
            JToken addons = result is JObject obj && StremioApiClient.IsTruthy(obj["addons"]) ? obj["addons"] : result;
            var list = new List<AddonDescriptor>();
            if (!(addons is JArray array)) return list;
            foreach (var entry in array.OfType<JObject>())
            {
                var manifest = entry["manifest"] as JObject ?? new JObject();
                var resources = (manifest["resources"] as JArray ?? entry["resources"] as JArray ?? new JArray())
                    .Select(r => r.Type == JTokenType.String ? (string)r : (string)r["name"])
                    .Where(r => !string.IsNullOrEmpty(r))
                    .ToList();
                var types = (manifest["types"] as JArray ?? new JArray())
                    .Where(t => t.Type == JTokenType.String).Select(t => (string)t).ToList();
                list.Add(new AddonDescriptor
                {
                    TransportUrl = (string)entry["transportUrl"] ?? (string)entry["transport_url"],
                    Id = (string)manifest["id"] ?? (string)entry["id"],
                    Name = (string)manifest["name"],
                    Resources = resources,
                    Types = types,
                });
            }
            return list;
        }
    }
}
