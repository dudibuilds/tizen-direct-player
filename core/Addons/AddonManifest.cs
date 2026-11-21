using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Addons
{
    /// Manifest URL handling (A4: base = transportUrl without /manifest.json and its query).
    public static class AddonUrls
    {
        private const string ManifestSuffix = "/manifest.json";
        private static readonly Regex ManifestTail = new Regex(@"/manifest\.json(?:\?.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// Accepts http(s) (and stremio://, which Stremio install links use for https). Ensures the path ends in
        /// /manifest.json. Torrentio-style configuration path segments and query strings are kept verbatim.
        public static string NormalizeManifestUrl(string input)
        {
            var text = (input ?? "").Trim();
            if (text.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
                text = "https://" + text.Substring("stremio://".Length);
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host))
                throw new ArgumentException("The add-on URL must be an http(s) URL.", nameof(input));

            var hash = text.IndexOf('#');
            if (hash >= 0) text = text.Substring(0, hash);
            var queryStart = text.IndexOf('?');
            var path = queryStart >= 0 ? text.Substring(0, queryStart) : text;
            var query = queryStart >= 0 ? text.Substring(queryStart) : "";
            if (!path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase))
                path = path.TrimEnd('/') + ManifestSuffix;
            return path + query;
        }

        public static string BaseUrl(string transportUrl)
        {
            if (string.IsNullOrEmpty(transportUrl)) return null;
            return ManifestTail.Replace(transportUrl, "").TrimEnd('/');
        }

        /// {base}/{resource}/{type}/{id}[/{extra}].json with the id escaped like encodeURIComponent.
        public static string Resource(string transportUrl, string resource, string type, string id, string extra = null)
        {
            var url = BaseUrl(transportUrl) + "/" + resource + "/" + Uri.EscapeDataString(type) + "/" + Uri.EscapeDataString(id);
            if (!string.IsNullOrEmpty(extra)) url += "/" + extra;
            return url + ".json";
        }

        public static bool IsWebUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// Parses and validates a Stremio add-on manifest into an AddonDescriptor with its catalogs attached.
    public static class AddonManifestParser
    {
        /// Throws AddonRequestException when id, name, resources, types or catalogs are missing or malformed.
        public static AddonDescriptor Parse(JObject manifest, string transportUrl)
        {
            if (manifest == null) throw new AddonRequestException("The add-on manifest is empty.");
            var id = (manifest["id"] as JValue)?.Value as string;
            var name = (manifest["name"] as JValue)?.Value as string;
            if (string.IsNullOrWhiteSpace(id)) throw new AddonRequestException("The add-on manifest has no id.");
            if (string.IsNullOrWhiteSpace(name)) throw new AddonRequestException("The add-on manifest has no name.");
            if (!(manifest["resources"] is JArray resourcesJson) || resourcesJson.Count == 0)
                throw new AddonRequestException("The add-on manifest lists no resources.");
            if (!(manifest["types"] is JArray typesJson))
                throw new AddonRequestException("The add-on manifest lists no types.");
            if (manifest["catalogs"] != null && manifest["catalogs"].Type != JTokenType.Array)
                throw new AddonRequestException("The add-on manifest catalogs are not a list.");
            if (manifest["catalogs"] == null)
                throw new AddonRequestException("The add-on manifest has no catalogs list.");

            var resources = new List<string>();
            var types = new List<string>(Strings(typesJson));
            foreach (var entry in resourcesJson)
            {
                // Resources are "stream" or {name:"stream", types:[...], idPrefixes:[...]}.
                string resource = null;
                if (entry.Type == JTokenType.String) resource = (string)entry;
                else if (entry is JObject obj)
                {
                    resource = (obj["name"] as JValue)?.Value as string;
                    if (obj["types"] is JArray resourceTypes) types.AddRange(Strings(resourceTypes));
                }
                if (!string.IsNullOrWhiteSpace(resource) && !resources.Contains(resource)) resources.Add(resource);
            }
            if (resources.Count == 0) throw new AddonRequestException("The add-on manifest lists no resources.");

            var descriptor = new AddonDescriptor
            {
                TransportUrl = transportUrl,
                Id = id,
                Name = name,
                Resources = resources,
                Types = types.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            };
            AddonCatalogs.Attach(descriptor, ParseCatalogs(id, manifest["catalogs"] as JArray));
            return descriptor;
        }

        public static IReadOnlyList<CatalogDescriptor> ParseCatalogs(string addonId, JArray catalogs)
        {
            var list = new List<CatalogDescriptor>();
            if (catalogs == null) return list;
            foreach (var item in catalogs.OfType<JObject>())
            {
                var type = (item["type"] as JValue)?.Value as string;
                var id = (item["id"] as JValue)?.Value as string;
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id)) continue;
                var supported = new List<string>(Strings(item["extraSupported"] as JArray));
                var required = new List<string>(Strings(item["extraRequired"] as JArray));
                if (item["extra"] is JArray extra)
                {
                    foreach (var e in extra.OfType<JObject>())
                    {
                        var extraName = (e["name"] as JValue)?.Value as string;
                        if (string.IsNullOrEmpty(extraName)) continue;
                        if (!supported.Contains(extraName)) supported.Add(extraName);
                        if (e["isRequired"]?.Type == JTokenType.Boolean && (bool)e["isRequired"] && !required.Contains(extraName))
                            required.Add(extraName);
                    }
                }
                list.Add(new CatalogDescriptor
                {
                    AddonId = addonId,
                    Type = type,
                    Id = id,
                    Name = ((item["name"] as JValue)?.Value as string) ?? id,
                    ExtraSupported = supported,
                    ExtraRequired = required,
                    Genres = Strings(item["genres"] as JArray).ToList(),
                });
            }
            return list;
        }

        private static IEnumerable<string> Strings(JArray array)
        {
            if (array == null) yield break;
            foreach (var token in array)
                if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token))
                    yield return (string)token;
        }
    }

    /// Helpers on the shared AddonDescriptor. Catalogs ride along with the descriptor instance
    /// because the contract type has no catalog field.
    public static class AddonCatalogs
    {
        private static readonly ConditionalWeakTable<AddonDescriptor, IReadOnlyList<CatalogDescriptor>> Table =
            new ConditionalWeakTable<AddonDescriptor, IReadOnlyList<CatalogDescriptor>>();

        public static void Attach(AddonDescriptor addon, IReadOnlyList<CatalogDescriptor> catalogs)
        {
            if (addon == null) return;
            lock (Table)
            {
                Table.Remove(addon);
                Table.Add(addon, catalogs ?? Array.Empty<CatalogDescriptor>());
            }
        }

        /// Catalogs from the manifest this descriptor was parsed from; null when unknown
        /// (e.g. an account add-on built elsewhere). AddonRegistry.GetCatalogsAsync fetches them in that case.
        public static IReadOnlyList<CatalogDescriptor> KnownCatalogs(this AddonDescriptor addon)
        {
            if (addon == null) return null;
            lock (Table)
                return Table.TryGetValue(addon, out var catalogs) ? catalogs : null;
        }

        /// Catalogs listed in the manifest, or empty when unknown.
        public static IReadOnlyList<CatalogDescriptor> Catalogs(this AddonDescriptor addon) =>
            KnownCatalogs(addon) ?? Array.Empty<CatalogDescriptor>();

        public static bool SupportsResource(this AddonDescriptor addon, string resource)
        {
            if (addon?.Resources == null) return false;
            foreach (var item in addon.Resources)
                if (string.Equals(item, resource, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// True when the add-on declares no types or declares this one.
        public static bool SupportsType(this AddonDescriptor addon, MediaType type)
        {
            if (addon?.Types == null || addon.Types.Count == 0) return true;
            var name = AddonTypes.ToStremio(type);
            foreach (var item in addon.Types)
                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static string BaseUrl(this AddonDescriptor addon) => AddonUrls.BaseUrl(addon?.TransportUrl);
    }
}
