using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Addons
{
    /// Installed add-ons: the Stremio account collection first, then local installs (persisted in IKeyValueStore),
    /// deduplicated by id. Per-resource lookups add the A4 defaults (Torrentio, OpenSubtitles v3, Cinemeta)
    /// when nothing installed provides the resource; defaults never appear in GetInstalledAsync.
    public sealed class AddonRegistry : IAddonRegistry
    {
        public const string StoreKey = "addons.installed";
        public const string CastAddonId = "local.samsung-media-tv.cast-to-tv";
        public const string OpenSubtitlesManifestUrl = "https://opensubtitles-v3.strem.io/manifest.json";
        public const string CinemetaManifestUrl = "https://v3-cinemeta.strem.io/manifest.json";
        public const string CinemetaId = "com.linvo.cinemeta";
        private static readonly TimeSpan AccountCacheTtl = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ManifestCacheTtl = TimeSpan.FromMinutes(30);

        private readonly IKeyValueStore store;
        private readonly AddonHttp http;
        private readonly Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> accountCollection;
        private readonly AsyncCache<IReadOnlyList<AddonDescriptor>> accountCache;
        private readonly AsyncCache<IReadOnlyList<CatalogDescriptor>> manifestCatalogs;
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        private List<StoredAddon> local;

        public AddonRegistry(IKeyValueStore store, HttpMessageHandler handler = null,
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> accountCollection = null)
            : this(store, new AddonHttp(handler), accountCollection, null)
        {
        }

        public AddonRegistry(IKeyValueStore store, AddonHttp http,
            Func<CancellationToken, Task<IReadOnlyList<AddonDescriptor>>> accountCollection, Func<DateTimeOffset> clock)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.http = http ?? new AddonHttp();
            this.accountCollection = accountCollection;
            accountCache = new AsyncCache<IReadOnlyList<AddonDescriptor>>(4, clock);
            manifestCatalogs = new AsyncCache<IReadOnlyList<CatalogDescriptor>>(64, clock);
        }

        public static AddonDescriptor DefaultOpenSubtitles() => new AddonDescriptor
        {
            TransportUrl = OpenSubtitlesManifestUrl,
            Id = "org.stremio.opensubtitlesv3",
            Name = "OpenSubtitles v3",
            Resources = new[] { "subtitles" },
            Types = new[] { "movie", "series" },
        };

        public static AddonDescriptor DefaultCinemeta()
        {
            var cinemeta = new AddonDescriptor
            {
                TransportUrl = CinemetaManifestUrl,
                Id = CinemetaId,
                Name = "Cinemeta",
                Resources = new[] { "catalog", "meta" },
                Types = new[] { "movie", "series" },
            };
            var extra = new[] { "search", "genre", "skip" };
            AddonCatalogs.Attach(cinemeta, new[]
            {
                new CatalogDescriptor { AddonId = CinemetaId, Type = "movie", Id = "top", Name = "Popular", ExtraSupported = extra },
                new CatalogDescriptor { AddonId = CinemetaId, Type = "series", Id = "top", Name = "Popular", ExtraSupported = extra },
                new CatalogDescriptor { AddonId = CinemetaId, Type = "movie", Id = "imdbRating", Name = "Featured", ExtraSupported = new[] { "genre", "skip" } },
                new CatalogDescriptor { AddonId = CinemetaId, Type = "series", Id = "imdbRating", Name = "Featured", ExtraSupported = new[] { "genre", "skip" } },
            });
            return cinemeta;
        }

        /// A4: the local cast add-on (by id, or a loopback URL under /stremio-cast/) is never a source.
        public static bool IsLocalCastAddon(AddonDescriptor addon)
        {
            if (addon == null) return false;
            if (addon.Id == CastAddonId) return true;
            if (!Uri.TryCreate(addon.TransportUrl ?? "", UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.Trim('[', ']').ToLowerInvariant();
            return (host == "127.0.0.1" || host == "localhost" || host == "::1") && uri.AbsolutePath.Contains("/stremio-cast/");
        }

        public async Task<IReadOnlyList<AddonDescriptor>> GetInstalledAsync(CancellationToken token)
        {
            var account = await GetAccountAddonsAsync(token).ConfigureAwait(false);
            var locals = await LoadLocalAsync(token).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<AddonDescriptor>();
            foreach (var addon in account.Concat(locals.Select(item => item.Descriptor)))
            {
                if (addon == null || string.IsNullOrEmpty(addon.Id) || string.IsNullOrEmpty(addon.TransportUrl)) continue;
                if (IsLocalCastAddon(addon) || !seen.Add(addon.Id)) continue;
                merged.Add(addon);
            }
            return merged;
        }

        /// Installed add-ons providing `resource` (optionally for `type`), else the A4 default for it.
        public async Task<IReadOnlyList<AddonDescriptor>> GetForResourceAsync(string resource, MediaType? type, CancellationToken token)
        {
            var installed = await GetInstalledAsync(token).ConfigureAwait(false);
            var matching = installed.Where(addon => addon.SupportsResource(resource)
                && (!type.HasValue || addon.SupportsType(type.Value))).ToList();
            if (matching.Count > 0) return matching;
            var fallback = DefaultFor(resource);
            return fallback == null ? (IReadOnlyList<AddonDescriptor>)Array.Empty<AddonDescriptor>() : new[] { fallback };
        }

        public static AddonDescriptor DefaultFor(string resource)
        {
            switch (resource)
            {
                // No built-in stream add-on: sources come only from add-ons the user installs.
                case "subtitles": return DefaultOpenSubtitles();
                case "catalog":
                case "meta": return DefaultCinemeta();
                default: return null;
            }
        }

        /// Installed add-on by id, or one of the defaults by id.
        public async Task<AddonDescriptor> FindAsync(string addonId, CancellationToken token)
        {
            var installed = await GetInstalledAsync(token).ConfigureAwait(false);
            var found = installed.FirstOrDefault(addon => addon.Id == addonId);
            if (found != null) return found;
            foreach (var fallback in new[] { DefaultCinemeta(), DefaultOpenSubtitles() })
                if (fallback.Id == addonId) return fallback;
            return null;
        }

        /// Catalogs from the manifest; fetches (and caches) the manifest when the descriptor carries none.
        public async Task<IReadOnlyList<CatalogDescriptor>> GetCatalogsAsync(AddonDescriptor addon, CancellationToken token)
        {
            var known = addon.KnownCatalogs();
            if (known != null) return known;
            try
            {
                var catalogs = await manifestCatalogs.GetOrLoadAsync(addon.TransportUrl, async () =>
                {
                    var manifest = await http.GetJsonAsync(addon.TransportUrl, CancellationToken.None).ConfigureAwait(false);
                    return AddonManifestParser.Parse(manifest, addon.TransportUrl).Catalogs();
                }, value => ManifestCacheTtl, token).ConfigureAwait(false);
                AddonCatalogs.Attach(addon, catalogs);
                return catalogs;
            }
            catch (AddonRequestException)
            {
                return Array.Empty<CatalogDescriptor>();
            }
        }

        /// Home rows: every browsable catalog of every catalog add-on (Cinemeta when none is installed).
        public async Task<IReadOnlyList<HomeCatalog>> GetHomeCatalogsAsync(CancellationToken token)
        {
            var addons = await GetForResourceAsync("catalog", null, token).ConfigureAwait(false);
            var catalogLists = await Task.WhenAll(addons.Select(addon => GetCatalogsAsync(addon, token))).ConfigureAwait(false);
            var rows = new List<HomeCatalog>();
            for (var i = 0; i < addons.Count; i++)
                foreach (var catalog in catalogLists[i])
                    if (catalog.IsBrowsable)
                        rows.Add(new HomeCatalog { Addon = addons[i], Catalog = catalog });
            return rows;
        }

        public async Task<AddonDescriptor> InstallAsync(string manifestUrl, CancellationToken token)
        {
            var url = AddonUrls.NormalizeManifestUrl(manifestUrl);
            var manifest = await http.GetJsonAsync(url, token).ConfigureAwait(false);
            var descriptor = AddonManifestParser.Parse(manifest, url);
            if (IsLocalCastAddon(descriptor))
                throw new AddonRequestException("The cast add-on cannot be installed on the TV.");

            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var locals = new List<StoredAddon>(await LoadLocalAsync(token).ConfigureAwait(false));
                locals.RemoveAll(item => item.Descriptor.Id == descriptor.Id);
                locals.Add(new StoredAddon { TransportUrl = url, Manifest = manifest, Descriptor = descriptor });
                await SaveLocalAsync(locals, token).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
            return descriptor;
        }

        /// Removes a local install. Add-ons that come only from the Stremio account cannot be removed here.
        public async Task RemoveAsync(string addonId, CancellationToken token)
        {
            await writeLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var locals = new List<StoredAddon>(await LoadLocalAsync(token).ConfigureAwait(false));
                if (locals.RemoveAll(item => item.Descriptor.Id == addonId) > 0)
                {
                    await SaveLocalAsync(locals, token).ConfigureAwait(false);
                    return;
                }
            }
            finally
            {
                writeLock.Release();
            }
            var account = await GetAccountAddonsAsync(token).ConfigureAwait(false);
            if (account.Any(addon => addon?.Id == addonId))
                throw new InvalidOperationException("This add-on comes from the Stremio account. Remove it in Stremio.");
        }

        public void InvalidateAccountCache() => accountCache.Clear();

        private async Task<IReadOnlyList<AddonDescriptor>> GetAccountAddonsAsync(CancellationToken token)
        {
            if (accountCollection == null) return Array.Empty<AddonDescriptor>();
            try
            {
                // A4: cache the account collection for 60 s; a failure falls back to local installs and defaults.
                return await accountCache.GetOrLoadAsync("account",
                    async () => await accountCollection(CancellationToken.None).ConfigureAwait(false) ?? Array.Empty<AddonDescriptor>(),
                    value => AccountCacheTtl, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Array.Empty<AddonDescriptor>();
            }
        }

        private async Task<IReadOnlyList<StoredAddon>> LoadLocalAsync(CancellationToken token)
        {
            var cached = local;
            if (cached != null) return cached;
            var list = new List<StoredAddon>();
            var json = await store.ReadAsync(StoreKey, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                JArray items;
                try
                {
                    using (var reader = new JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None })
                        items = JToken.ReadFrom(reader) as JArray;
                }
                catch (JsonException)
                {
                    items = null;
                }
                foreach (var item in (items ?? new JArray()).OfType<JObject>())
                {
                    var url = (item["transportUrl"] as JValue)?.Value as string;
                    var manifest = item["manifest"] as JObject;
                    try
                    {
                        var descriptor = AddonManifestParser.Parse(manifest, url);
                        list.Add(new StoredAddon { TransportUrl = url, Manifest = manifest, Descriptor = descriptor });
                    }
                    catch (AddonRequestException)
                    {
                        // Skip a corrupt entry rather than losing every install.
                    }
                }
            }
            local = list;
            return list;
        }

        private async Task SaveLocalAsync(List<StoredAddon> items, CancellationToken token)
        {
            var array = new JArray(items.Select(item => new JObject
            {
                ["transportUrl"] = item.TransportUrl,
                ["manifest"] = item.Manifest,
            }));
            await store.WriteAsync(StoreKey, array.ToString(Formatting.None), token).ConfigureAwait(false);
            local = items;
        }

        private sealed class StoredAddon
        {
            public string TransportUrl;
            public JObject Manifest;
            public AddonDescriptor Descriptor;
        }
    }
}
