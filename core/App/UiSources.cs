using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Addons;
using Ru7100.Core.Api;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using ApiRecommendationItem = Ru7100.Core.Api.RecommendationItem;
using UiRecommendationItem = Ru7100.Core.Ui.RecommendationItem;

namespace Ru7100.Core.App
{
    /// "For You" row for the remote UI from the snapshot the Mac pushes through recommendations.set (persisted on the
    /// TV, so it works with the Mac asleep). Rows are flattened in order, duplicates dropped, titles the user hid
    /// (thumbs down / not interested) left out. The row's "Because you watched X" title becomes the reason when an
    /// item has none.
    public sealed class RecommendationsFeed : IRecommendationsFeed
    {
        public const int MaxItems = 40;
        private readonly IRecommendationsStore store;
        private readonly IFeedbackSink feedback;

        public RecommendationsFeed(IRecommendationsStore store, IFeedbackSink feedback = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.feedback = feedback;
        }

        public async Task<IReadOnlyList<UiRecommendationItem>> GetAsync(CancellationToken token)
        {
            var snapshot = await store.GetAsync(token).ConfigureAwait(false);
            var hidden = feedback == null ? (IReadOnlyCollection<string>)Array.Empty<string>() : await feedback.GetHiddenAsync(token).ConfigureAwait(false);
            var hiddenSet = new HashSet<string>(hidden, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var items = new List<UiRecommendationItem>();
            foreach (var row in snapshot?.Rows ?? Array.Empty<RecommendationRow>())
            {
                foreach (var item in row.Items ?? Array.Empty<ApiRecommendationItem>())
                {
                    if (item?.Id == null || hiddenSet.Contains(item.Id) || !seen.Add(item.Id)) continue;
                    items.Add(new UiRecommendationItem
                    {
                        Meta = new MetaSummary
                        {
                            Id = item.Id, Name = item.Name, Poster = item.Poster,
                            Type = item.Type == "movie" ? MediaType.Movie : MediaType.Series,
                        },
                        Reason = string.IsNullOrEmpty(item.Reason) && row.Id != "for-you" ? row.Title : item.Reason,
                        Score = item.Score ?? 0,
                    });
                    if (items.Count >= MaxItems) return items;
                }
            }
            return items;
        }
    }

    /// Home rows from the installed add-ons' catalogs (account collection + local installs + Cinemeta), in add-on
    /// order. Catalogs that require extra arguments (search-only, genre-only) are skipped by the registry. Falls back to
    /// the Cinemeta defaults when nothing is listed or the registry fails, so Home is never empty offline.
    public sealed class AddonCatalogRows : ICatalogRowSource
    {
        public const int MaxRows = 12;
        private readonly AddonRegistry registry;
        private readonly Action<string> log;

        public AddonCatalogRows(AddonRegistry registry, Action<string> log = null)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            this.log = log;
        }

        public async Task<IReadOnlyList<CatalogRowSpec>> GetRowsAsync(CancellationToken token)
        {
            try
            {
                var catalogs = await registry.GetHomeCatalogsAsync(token).ConfigureAwait(false);
                var rows = new List<CatalogRowSpec>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var home in catalogs ?? Array.Empty<HomeCatalog>())
                {
                    var type = home?.Catalog?.MediaType;
                    if (type == null || home.Addon?.Id == null) continue;
                    if (!seen.Add(home.Addon.Id + "/" + home.Catalog.Type + "/" + home.Catalog.Id)) continue;
                    rows.Add(new CatalogRowSpec
                    {
                        AddonId = home.Addon.Id, CatalogId = home.Catalog.Id, Type = type.Value,
                        Title = home.Addon.Id == AddonRegistry.CinemetaId ? CinemetaTitle(home) : home.Title,
                    });
                    if (rows.Count >= MaxRows) break;
                }
                if (rows.Count > 0) return rows;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                log?.Invoke("catalog rows: " + error.Message);
            }
            return await StaticCatalogRows.CinemetaDefaults().GetRowsAsync(token).ConfigureAwait(false);
        }

        private static string CinemetaTitle(HomeCatalog home)
        {
            var noun = home.Catalog.MediaType == MediaType.Series ? "series" : "movies";
            return home.Catalog.Id == "top" ? "Popular " + noun : home.Catalog.Id == "imdbRating" ? "Top rated " + noun : home.Title;
        }
    }

    /// Parity C8 availability check for StremioLibrary.GetNewEpisodesAsync: an episode counts as new only when the
    /// installed stream add-ons return at least one source that survives ranking (episodic, anime rules).
    public static class NewEpisodeAvailability
    {
        public static Func<MetaDetail, VideoInfo, CancellationToken, Task<bool>> For(ISourceResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            return async (meta, video, token) =>
            {
                var ranked = await resolver.GetRankedAsync(MediaType.Series, video.Id, new SourceOptions
                {
                    Episodic = true,
                    IsAnime = PlayResolver.IsJapaneseAnime(meta),
                }, token).ConfigureAwait(false);
                return ranked != null && ranked.Count > 0;
            };
        }
    }

    /// IStremioAccount decorator: signing in or out refreshes the add-on registry's cached account collection, so the
    /// account's add-ons (and their catalogs) appear or disappear without restarting the app.
    public sealed class RegistryAwareAccount : IStremioAccount
    {
        private readonly IStremioAccount inner;
        private readonly AddonRegistry registry;

        public RegistryAwareAccount(IStremioAccount inner, AddonRegistry registry)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public bool IsSignedIn => inner.IsSignedIn;
        public Task<(string Code, string Link)> BeginLinkAsync(CancellationToken token) => inner.BeginLinkAsync(token);

        public async Task<bool> CompleteLinkAsync(string code, CancellationToken token)
        {
            var done = await inner.CompleteLinkAsync(code, token).ConfigureAwait(false);
            if (done) registry.InvalidateAccountCache();
            return done;
        }

        public async Task SignOutAsync(CancellationToken token)
        {
            await inner.SignOutAsync(token).ConfigureAwait(false);
            registry.InvalidateAccountCache();
        }

        public Task<IReadOnlyList<AddonDescriptor>> GetAddonCollectionAsync(CancellationToken token) => inner.GetAddonCollectionAsync(token);
    }
}
