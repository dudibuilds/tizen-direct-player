using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Addons;

namespace Ru7100.Core.Ui
{
    /// What a Services-row card shows about its service (docs/streaming-services.md, mock 10): the backdrop of its most
    /// popular title, and the banner lines while the card has focus.
    public sealed class ServiceCard
    {
        public StreamingService Service { get; set; }
        /// IMDb id of the service's most popular title, whose art the card and billboard show.
        public string TopId { get; set; }
        /// Large art for the billboard (the most popular title's background).
        public string Backdrop => UiFormat.BackgroundFor(TopId);
        /// Card-sized art for the row.
        public string CardArt => UiFormat.CardArtFor(TopId);
        /// "228 series · 132 movies · 5 new this week"
        public string Facts { get; set; }
        /// "Popular now: Nocturne, Slow Horses, Dark Matter"
        public string Description { get; set; }
        /// Line inside the card: "5 new this week" (or the most popular title when nothing is new).
        public string Caption { get; set; }
    }

    /// One service's headline numbers, cached for CacheHours: each card costs about ten small catalogue requests, so
    /// Home asks at most every few hours (the add-on fallback refreshes on the same cycle).
    public sealed class ServiceSummaryCache
    {
        public const int CacheHours = 6;
        public const int NewDays = 7;
        private readonly IServiceCatalog catalog;
        private readonly Dictionary<string, (DateTimeOffset At, ServiceCard Card)> cards = new Dictionary<string, (DateTimeOffset, ServiceCard)>();

        /// Services whose numbers are fetched at once. A cold Home used to fire all nine cards' ~90 requests together,
        /// competing with the rows people look at first (the row still lists its cards in the fixed order).
        public const int Concurrency = 2;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(Concurrency, Concurrency);
        private readonly Dictionary<string, Task<ServiceCard>> inFlight = new Dictionary<string, Task<ServiceCard>>();

        public ServiceSummaryCache(IServiceCatalog catalog) => this.catalog = catalog;

        /// One fetch per service at a time: a second Home (or a Back to Home) asking while the first is still loading
        /// shares it. A caller's cancel only stops its own wait.
        public Task<ServiceCard> GetAsync(StreamingService service, DateTimeOffset now, CancellationToken token)
        {
            lock (cards)
            {
                if (cards.TryGetValue(service.Id, out var hit) && now - hit.At < TimeSpan.FromHours(CacheHours))
                    return Task.FromResult(hit.Card);
                if (!inFlight.TryGetValue(service.Id, out var task) || task.IsCompleted)
                {
                    task = LoadAsync(service, now);
                    inFlight[service.Id] = task;
                }
                return TaskUtil.WithCancellation(task, token);
            }
        }

        private async Task<ServiceCard> LoadAsync(StreamingService service, DateTimeOffset now)
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try { return await FetchAsync(service, now, CancellationToken.None).ConfigureAwait(false); }
            finally { slots.Release(); }
        }

        private async Task<ServiceCard> FetchAsync(StreamingService service, DateTimeOffset now, CancellationToken token)
        {
            var popular = catalog.GetTitlesAsync(service, null, null, 0, 3, token);
            var series = Total(catalog.GetTitlesAsync(service, MediaType.Series, null, 0, 1, token));
            var movies = Total(catalog.GetTitlesAsync(service, MediaType.Movie, null, 0, 1, token));
            var fresh = Count(catalog.GetNewAsync(service, now.UtcDateTime, NewDays, 200, token));
            await Task.WhenAll(popular, series, movies, fresh).ConfigureAwait(false);
            var top = popular.Result.Titles;
            if (top.Count == 0)
                return null;   // nothing to show: the card stays out of the row
            var card = new ServiceCard
            {
                Service = service,
                TopId = top[0].Meta.Id,
                Facts = UiFormat.Facts(series.Result > 0 ? Count(series.Result, "series", "series") : null,
                    movies.Result > 0 ? Count(movies.Result, "movie", "movies") : null,
                    fresh.Result > 0 ? fresh.Result + " new this week" : null),
                Description = "Popular now: " + string.Join(", ", top.Select(t => t.Meta.Name)),
                Caption = fresh.Result > 0 ? fresh.Result + " new this week" : "Popular: " + top[0].Meta.Name
            };
            lock (cards)
                cards[service.Id] = (now, card);
            return card;
        }

        private static string Count(int n, string one, string many) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

        private static async Task<int> Total(Task<ServicePage> page)
        {
            try { return (await page.ConfigureAwait(false)).Total; }
            catch (Exception) { return 0; }   // counts are decoration: the card still shows without them
        }

        private static async Task<int> Count(Task<IReadOnlyList<ServiceTitle>> titles)
        {
            try { return (await titles.ConfigureAwait(false)).Count; }
            catch (Exception) { return 0; }
        }
    }

    /// The rows of a service page, in order (mock 11). Genre rows that come back empty drop out.
    public static class ServiceRows
    {
        public const int PopularCount = 10;
        public const int RowCount = 20;
        public const int NewDays = 30;
        public static readonly IReadOnlyList<string> Genres = new[] { "Drama", "Comedy", "Thriller", "Sci-Fi", "Action", "Animation", "Documentary" };
    }
}
