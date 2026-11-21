using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Ui
{
    /// Extension: a card owned by an optional extension (HomeViewModel partial hooks), which decides what OK opens.
    /// Service: a Services-row card (opens that service's page). SeeAll: the last card of a service row (opens Browse
    /// with Tile.BrowsePreset). ServiceChip: a chip of a service page's header (Home, Series, Movies).
    public enum TileAction { Details, Resume, PlayFirst, Search, Browse, Queue, Addons, SignIn, Account, Motion, Home, Service, SeeAll, ServiceChip }

    /// Buttons of a popped card, left to right. Down from the card focuses the row; OK activates the focused one.
    public enum PopButton { Play, MyList, Rate, Info }

    /// Card shapes: Portrait posters, Landscape art (episode still or background with the title logo over it),
    /// Ranked (portrait with a big rank numeral, for Top 10 rows).
    public enum TileStyle { Portrait, Landscape, Ranked }

    public sealed class Tile
    {
        public string Id { get; set; }
        public MediaType Type { get; set; }
        public string Title { get; set; }
        /// Line under/over the card: "S2:E3 · 26m left", a feed reason, a release date.
        public string Subtitle { get; set; }
        public string Poster { get; set; }
        /// Landscape art: episode still or background image.
        public string Image { get; set; }
        /// Title logo drawn over landscape art.
        public string Logo { get; set; }
        public double Progress { get; set; }    // 0..1
        /// Corner label earned from data: "New Episode", "New Season", "Next episode · Fri", "Recently added".
        public string Badge { get; set; }
        public bool Watched { get; set; }
        public int Rank { get; set; }
        public TileAction Action { get; set; } = TileAction.Details;
        /// Billboard overrides for cards that are not catalog titles (no IMDb art, no extras): the backdrop, the facts
        /// line and the description. Null: the title's own (BackgroundFor(Id), type and extras).
        public string Background { get; set; }
        public string Facts { get; set; }
        public string Description { get; set; }
        /// The card art carries its own title treatment (a channel tile): no logo is drawn over it on the card.
        public bool ArtHasTitle { get; set; }
        /// Episode-level cards: the episode the card is about.
        public string VideoId { get; set; }
        public long ResumeMs { get; set; }
        /// Services-row card: the service and what its card and the billboard show.
        public ServiceCard ServiceCard { get; set; }
        /// See all card: the Browse filters it opens with.
        public IDictionary<BrowseFilter, string> BrowsePreset { get; set; }
    }

    public sealed class HomeRow
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public bool IsMenu { get; set; }
        public TileStyle Style { get; set; }
        public IReadOnlyList<Tile> Items { get; set; } = Array.Empty<Tile>();
    }

    /// Menu bar, then (hiding empty rows): Continue watching, New episodes of your shows, For you, Start something
    /// new, Top 10 movies/series, Short episodes, Your library, add-on catalog rows. Focus is remembered per row.
    /// OK means the same on every screen: it opens the title page. The one exception is Continue watching, whose
    /// cards resume at once. Play/Pause on a title plays it directly (while a trailer sounds it switches its sound).
    public sealed class HomeViewModel : Screen
    {
        public const int NewEpisodeSeriesLimit = 12;
        public const int ShortRuntimeMinutes = 30;
        public const int ShortCandidates = 16;
        /// The billboard follows focus once it rests this long (fast scrolling doesn't load every background).
        public const int HeroDelayMs = 350;
        /// Idle time on a focused title before its preview starts (with sound unless Preview sound is off). The
        /// trailer always plays in the trailer view (full screen, rows slid down to a strip).
        public const int TrailerDelayMs = 2500;

        private static readonly string[] FixedOrder = { "menu", "continue", "new", "foryou", "services", "firsteps", "top:movie", "top:series", "short", "library" };

        private long focusChangedMs;
        private string lastFocusKey;
        private readonly Dictionary<string, HomeRow> rows = new Dictionary<string, HomeRow>();
        private readonly List<string> order = new List<string>(FixedOrder);
        private readonly Dictionary<string, int> columns = new Dictionary<string, int> { ["menu"] = 1 };   // Up, OK is still Search
        private string focusedKey = "continue";
        private IReadOnlyList<LibraryEntry> libraryEntries = Array.Empty<LibraryEntry>();
        private IReadOnlyList<RecommendationItem> feed = Array.Empty<RecommendationItem>();

        public override ScreenKind Kind => ScreenKind.Home;

        /// The launch intro playing over Home on a cold start (LaunchIntro), or null.
        public LaunchIntro Intro { get; private set; }
        private Action afterIntro;

        /// Plays the launch intro over this Home when it is pushed; `afterIntro` runs once it has handed over (for
        /// example the "Continue watching?" offer, which must not appear over the intro).
        public HomeViewModel WithLaunchIntro(Action afterIntro = null, System.Threading.Tasks.Task loadAfter = null)
        {
            Intro = new LaunchIntro();
            this.afterIntro = afterIntro;
            this.loadAfter = loadAfter;
            return this;
        }

        /// The app's start-up reads (settings, sign-in, library): Home's first loads wait for them, while the intro
        /// plays straight away (lean start).
        private System.Threading.Tasks.Task loadAfter;

        /// Home has something to show behind the intro: its first content row and a billboard title.
        public bool ReadyForReveal => Rows.Any(r => !r.IsMenu);

        /// Starts the intro (and its sound, together) once the painter can draw the logo; until then it holds the ink page.
        private void StartIntroIfReady(long nowMs)
        {
            if (Intro == null || Intro.Started || Intro.Done)
                return;
            if (!Intro.TryStart(nowMs, Services.LaunchIntroReady?.Invoke() ?? true))
                return;
            Services.LaunchTrace?.Invoke("intro: start");
            if (Services.LaunchSoundEnabled)
                Services.LaunchSound?.Invoke();
        }

        /// Clock time for the layout (the intro's timeline).
        public long LayoutNowMs => NowMs;
        public override string Title => Service?.Name ?? "Home";

        /// Home (null), or one streaming service's page (docs/streaming-services.md): the same rows, billboard and
        /// pop-out cards, scoped to that service, with a chip bar instead of the menu and the service's tint.
        public Ru7100.Core.Addons.StreamingService Service { get; }

        /// Service page: which chip is current ("all", "series" or "movie"); its rows show that type.
        public string ServiceMode { get; private set; } = "all";

        public HomeViewModel(Ru7100.Core.Addons.StreamingService service = null)
        {
            Service = service;
            if (service == null)
                return;
            order.Clear();
            order.Add("menu");
            order.Add("svc:popular");
            order.Add("svc:new");
            order.AddRange(ServiceRows.Genres.Select(g => "svc:genre:" + g));
            focusedKey = "svc:popular";
            columns["menu"] = 0;   // Up from the rows lands on the Home chip
        }

        public IReadOnlyList<HomeRow> Rows
        {
            get
            {
                var list = order.Where(k => rows.ContainsKey(k) && (rows[k].IsMenu || rows[k].Items.Count > 0)).Select(k => rows[k]).ToList();
                // The Services row is never the first row, so Home always opens on a title (AGENTS.md, 2026-11-20).
                var first = list.FindIndex(r => !r.IsMenu);
                if (first >= 0 && list[first].Key == "services" && first + 1 < list.Count)
                {
                    var services = list[first];
                    list.RemoveAt(first);
                    list.Insert(first + 1, services);
                }
                return list;
            }
        }

        public int FocusedRowIndex
        {
            get
            {
                var visible = Rows;
                var index = visible.ToList().FindIndex(r => r.Key == focusedKey);
                return index >= 0 ? index : Math.Min(1, visible.Count - 1);
            }
        }

        public HomeRow FocusedRow => Rows.Count == 0 ? null : Rows[FocusedRowIndex];
        public int FocusedColumn(HomeRow row) => row != null && columns.TryGetValue(row.Key, out var c) ? Math.Min(c, Math.Max(0, row.Items.Count - 1)) : 0;
        /// Title shown in the billboard (background art, title, facts, description).
        public Tile HeroTile { get; private set; }
        public MetaExtras HeroExtras { get; private set; }
        /// YouTube id of the trailer playing in the billboard, or null.
        public string TrailerId { get; private set; }
        public bool TrailerMuted { get; private set; } = true;
        private int trailerEndSeen;
        private int trailerStartSeen;
        private long trailerStartedMs = -1;
        private bool compactShown;

        /// Netflix trailer view: TrailerCompactDelayMs after the trailer's first frames, the facts fade out and the
        /// logo shrinks toward the bottom-left so the video shows; any end or navigation restores the full billboard.
        public bool TrailerCompact => TrailerId != null && trailerStartedMs >= 0 && NowMs - trailerStartedMs >= UiLayout.TrailerCompactDelayMs;

        private void TickTrailerCompact(long nowMs)
        {
            if (TrailerId != null && trailerStartedMs < 0 && Services.TrailerStartCount != trailerStartSeen && Services.StartedTrailerId == TrailerId)
                trailerStartedMs = nowMs;
            if (TrailerCompact != compactShown)
            {
                compactShown = TrailerCompact;
                Changed();
            }
        }
        /// The focused title's preview played to its end: it waits until focus moves away and comes back.
        private bool trailerPlayed;

        // ---- Pop-out trailer card (Netflix web hover card, UiServices.TrailerCards) ----

        /// The row title whose card is popped out, or null. Any key shrinks it; focus resting CardPopDelayMs pops it.
        public Tile PoppedTile { get; private set; }
        public string PoppedRowKey { get; private set; }
        /// Card trailer: IMDb title id (always) and YouTube fallback id (may be null), frozen when it starts.
        public string CardTrailerTitleId { get; private set; }
        public string CardTrailerVideoId { get; private set; }
        public bool CardTrailerPlaying => CardTrailerTitleId != null;
        /// Facts for the popped card (the billboard's extras when they belong to the same title).
        public MetaExtras CardExtras => PoppedTile != null && HeroTile != null && HeroTile.Id == PoppedTile.Id ? HeroExtras : null;
        public bool InLibrary(string id) => id != null && libraryEntries.Any(e => e.Id == id);

        private long lastKeyMs;
        private long poppedAtMs;
        private bool cardDismissed;      // Back closed it: it waits until focus moves
        private bool popAfterNavigation; // set on return to Home: the next focus check keeps the card dismissed
        private bool cardTrailerPlayed;  // played to its end for this title: the card keeps the still art
        private int cardEndSeen;

        public Tile FocusedTile => FocusedRow == null || FocusedRow.Items.Count == 0 ? null : FocusedRow.Items[FocusedColumn(FocusedRow)];

        protected internal override void OnPushed()
        {
            StartIntroIfReady(NowMs);
            if (Service != null)
            {
                SetRow("menu", "", ChipTiles(), menu: true);
                LoadServiceRows();
                return;
            }
            SetRow("menu", "", MenuTiles(), menu: true);
            var wait = loadAfter;
            loadAfter = null;
            if (wait != null && !wait.IsCompleted)
            {
                pendingFirstLoad = wait;   // Tick runs the first load on the UI thread once the reads are done
                return;
            }
            FirstLoad();
        }

        private System.Threading.Tasks.Task pendingFirstLoad;

        /// Called from Tick (UI thread): runs Home's first load once the start-up reads have finished.
        private void RunPendingFirstLoad()
        {
            if (pendingFirstLoad == null || !pendingFirstLoad.IsCompleted)
                return;
            // Never in the middle of the intro's animated beats (0.35 s until the logo settles): the load's start costs
            // ~0.1 s on the TV, which would drop frames of the moving mark. Before the beats or once settled is fine.
            var now = NowMs;
            if (Intro != null && Intro.Started && !Intro.Done && Intro.Elapsed(now) >= 350 && !Intro.ShowHome(now))
                return;
            pendingFirstLoad = null;
            UiWork.Current = "Home.FirstLoad";
            SetRow("menu", "", MenuTiles(), menu: true);   // sign-in state may have changed the menu
            FirstLoad();
            Changed();
        }

        /// Home's first loads: library rows and catalog rows, in one place so the deferred lean-start path and the
        /// direct path load the same rows.
        private void FirstLoad()
        {
            Reload();
            LoadCatalogRows();
        }

        protected internal override void OnResumed()
        {
            TrailerId = null;   // back from another screen: wait for focus to rest again
            ShrinkCard();
            popAfterNavigation = true;   // back from the title page or playback: no card until the viewer navigates
            lastFocusKey = null;
            if (Service != null)
            {
                SetRow("menu", "", ChipTiles(), menu: true);
                return;
            }
            SetRow("menu", "", MenuTiles(), menu: true);
            Reload();
        }

        /// Home (the current section), Search, Browse, Queue; then one Settings item that holds the rarely used
        /// Add-ons, account and preview switches. A signed-out account keeps its own "Sign in" item.
        private IReadOnlyList<Tile> MenuTiles()
        {
            var tiles = new List<Tile>
            {
                new Tile { Id = "home", Title = "Home", Action = TileAction.Home },
                new Tile { Id = "search", Title = "Search", Action = TileAction.Search }
            };
            if (Services.Browse != null)
                tiles.Add(new Tile { Id = "browse", Title = "Browse", Action = TileAction.Browse });
            if (Services.Queue != null)
                tiles.Add(new Tile { Id = "queue", Title = "Queue", Action = TileAction.Queue });
            if (Services.Account != null && !Services.Account.IsSignedIn)
                tiles.Add(new Tile { Id = "signin", Title = "Sign in to Stremio", Action = TileAction.SignIn });
            if (Services.Player?.Settings != null || Services.Addons != null || Services.Account != null)
                tiles.Add(new Tile { Id = "settings", Title = "Settings", Action = TileAction.Motion });
            return tiles;
        }

        /// Service page header: Home, Series and Movies switch the rows' type; Genres opens the service in Browse.
        private IReadOnlyList<Tile> ChipTiles() => new[]
        {
            new Tile { Id = "all", Title = "Home", Action = TileAction.ServiceChip },
            new Tile { Id = "series", Title = "Series", Action = TileAction.ServiceChip },
            new Tile { Id = "movie", Title = "Movies", Action = TileAction.ServiceChip },
            new Tile { Id = "genres", Title = "Genres", Action = TileAction.SeeAll, BrowsePreset = Preset(null) }
        };

        private MediaType? ModeType => ServiceMode == "series" ? MediaType.Series : ServiceMode == "movie" ? MediaType.Movie : (MediaType?)null;

        /// Browse filters for a See all card: this service, the page's type (series when it shows both) and the genre.
        private IDictionary<BrowseFilter, string> Preset(string genre)
        {
            var preset = new Dictionary<BrowseFilter, string>
            {
                [BrowseFilter.Service] = Service.Name,
                [BrowseFilter.Type] = ServiceMode == "movie" ? "Movies" : "Series"
            };
            if (genre != null)
                preset[BrowseFilter.Genre] = genre;
            return preset;
        }

        private static Tile SeeAll(IDictionary<BrowseFilter, string> preset) =>
            new Tile { Id = "seeall", Title = "See all", Action = TileAction.SeeAll, BrowsePreset = preset };

        private static Tile TitleTile(Ru7100.Core.Addons.ServiceTitle t) => new Tile
        {
            Id = t.Meta.Id, Type = t.Meta.Type, Title = t.Meta.Name, Subtitle = t.Meta.ReleaseInfo, Poster = t.Meta.Poster,
            Image = UiFormat.CardArtFor(t.Meta.Id), Logo = UiFormat.LogoFor(t.Meta.Id)
        };

        /// Service page rows (mock 11): Popular in the US (ranked), New on the service, then genre rows; each ends in See all.
        private void LoadServiceRows()
        {
            var catalog = Services.ServiceCatalog;
            if (catalog == null)
                return;
            var service = Service;
            var type = ModeType;
            var now = Services.Clock.UtcNow.UtcDateTime;
            // Each chip change starts a new batch: the previous batch's requests are cancelled, and its answers are
            // dropped even when the viewer has come back to the same chip (Series → Movies → Series used to accept the
            // first Series batch's late rows).
            serviceRowsBatch?.Cancel();
            var batch = serviceRowsBatch = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
            var cancel = batch.Token;
            Func<bool> current = () => batch == serviceRowsBatch && !cancel.IsCancellationRequested;
            var genresStarted = false;
            Action startGenres = () =>
            {
                if (genresStarted || !current()) return;
                genresStarted = true;
                foreach (var genre in ServiceRows.Genres)
                {
                    var g = genre;
                    Load(token => catalog.GetTitlesAsync(service, type, g, 0, ServiceRows.RowCount, token), page =>
                    {
                        if (!current()) return;
                        var tiles = page.Titles.Select(TitleTile).ToList();
                        if (tiles.Count > 0) tiles.Add(SeeAll(Preset(g)));
                        SetRow("svc:genre:" + g, g, tiles, TileStyle.Landscape);
                    }, _ => { }, cancel);
                }
            };
            // Popular (the first row) goes first; the genre rows follow once it has answered, so they don't compete
            // with it for the connection.
            Load(token => catalog.GetTitlesAsync(service, type, null, 0, ServiceRows.PopularCount, token), page =>
            {
                if (!current()) return;
                var tiles = page.Titles.Select((t, i) => { var tile = TitleTile(t); tile.Rank = i + 1; return tile; }).ToList();
                if (tiles.Count > 0) tiles.Add(SeeAll(Preset(null)));
                SetRow("svc:popular", "Popular in the " + Ru7100.Core.Addons.StreamingServices.Region, tiles, TileStyle.Ranked);
                startGenres();
            }, _ => startGenres(), cancel);
            Load(token => catalog.GetNewAsync(service, now, ServiceRows.NewDays, ServiceRows.RowCount * 2, token), titles =>
            {
                if (!current()) return;
                var tiles = titles.Where(t => type == null || t.Meta.Type == type).Take(ServiceRows.RowCount).Select(TitleTile).ToList();
                if (tiles.Count > 0) tiles.Add(SeeAll(Preset(null)));
                SetRow("svc:new", "New on " + service.Name, tiles, TileStyle.Landscape);
            }, _ => { }, cancel);
        }

        private CancellationTokenSource serviceRowsBatch;

        private readonly Dictionary<string, ServiceCard> serviceCards = new Dictionary<string, ServiceCard>();

        /// Home's Services row (mock 10): one card per service, in the fixed order, as each service's numbers arrive.
        private void LoadServicesRow()
        {
            var cache = Services.ServiceSummaries;
            if (cache == null)
                return;
            var now = Services.Clock.UtcNow;
            foreach (var s in Ru7100.Core.Addons.StreamingServices.All)
            {
                var service = s;
                Load(token => cache.GetAsync(service, now, token), card =>
                {
                    if (card == null) return;
                    serviceCards[service.Id] = card;
                    SetRow("services", "Services", Ru7100.Core.Addons.StreamingServices.All.Where(x => serviceCards.ContainsKey(x.Id)).Select(x =>
                    {
                        var c = serviceCards[x.Id];
                        return new Tile { Id = "service:" + x.Id, Title = x.Name, Action = TileAction.Service, ServiceCard = c, Image = c.CardArt };
                    }).ToList(), TileStyle.Landscape);
                }, _ => { });
            }
        }

        /// Reloads library-driven rows and recommendations (after playback, sign-in, library edits).
        public void Reload()
        {
            if (Service != null)
                return;
            LoadServicesRow();
            var now = Services.Clock.UtcNow;
            if (Services.Library != null && (Services.Account == null || Services.Account.IsSignedIn))
            {
                Load(token => Services.Library.GetAllAsync(token), entries =>
                {
                    libraryEntries = (entries ?? Array.Empty<LibraryEntry>()).Where(e => !e.Removed && !e.Temp).ToList();
                    SetContinueRow();
                    SetRow("library", "Your library", libraryEntries.OrderByDescending(e => e.Modified).Select(e => new Tile
                    {
                        Id = e.Id, Type = e.Type, Title = e.Name, Poster = e.Poster,
                        Image = UiFormat.CardArtFor(e.Id), Logo = UiFormat.LogoFor(e.Id),
                        Watched = e.Type == MediaType.Movie && (e.State?.FlaggedWatched > 0 || e.State?.TimesWatched > 0),
                        Progress = e.State != null && e.State.DurationMs > 0 && e.State.TimeOffsetMs > 1000 ? Math.Min(1, e.State.TimeOffsetMs / (double)e.State.DurationMs) : 0
                    }).ToList(), TileStyle.Landscape);
                    LoadNewEpisodes(libraryEntries, now);
                    LoadShortEpisodes();
                    BuildFirstEpisodes();
                }, _ => { });
            }
            if (Services.Recommendations != null)
            {
                Load(token => Services.Recommendations.GetAsync(token), items =>
                {
                    feed = (items ?? Array.Empty<RecommendationItem>()).Where(i => i?.Meta != null).ToList();
                    SetRow("foryou", "For you", feed.Select(i => new Tile
                    {
                        Id = i.Meta.Id, Type = i.Meta.Type, Title = i.Meta.Name, Subtitle = i.Reason, Poster = i.Meta.Poster,
                        Image = UiFormat.CardArtFor(i.Meta.Id), Logo = UiFormat.LogoFor(i.Meta.Id)
                    }).ToList(), TileStyle.Landscape);
                    BuildFirstEpisodes();
                    LoadShortEpisodes();
                }, _ => { });
            }
        }

        /// Shows with episodes that came out since the viewer last watched them (filled by the new-episodes check;
        /// kept between reloads so Continue watching keeps its labels at once).
        private IReadOnlyDictionary<string, FreshEpisodes> freshShows = new Dictionary<string, FreshEpisodes>();

        /// What came out for one show since it was last watched.
        public sealed class FreshEpisodes
        {
            public VideoInfo First, Latest;
            public int Count;
            public bool NewSeason => First != null && First.Episode == 1 && First.Season > 1;
            public string Badge => NewSeason ? "New Season" : "New Episode";
        }

        /// Continue watching: titles stopped part-way, and series whose next episode is up (after an episode is
        /// finished the library points at the next released one with a 1 ms offset, Stremio's "up next"). A show with
        /// a new episode carries a "New Episode" / "New Season" label and moves up as if watched when it came out.
        public static IReadOnlyList<Tile> ContinueTiles(IEnumerable<LibraryEntry> entries, IReadOnlyDictionary<string, FreshEpisodes> fresh)
        {
            fresh = fresh ?? new Dictionary<string, FreshEpisodes>();
            DateTimeOffset SortKey(LibraryEntry e)
            {
                var watched = e.State.LastWatched ?? e.Modified;
                return fresh.TryGetValue(e.Id, out var f) && f.Latest?.Released > watched ? f.Latest.Released.Value : watched;
            }
            return entries.Where(IsContinue).OrderByDescending(SortKey).Select(e =>
            {
                var tile = ContinueTile(e);
                if (fresh.TryGetValue(e.Id, out var f))
                    tile.Badge = f.Badge;
                return tile;
            }).ToList();
        }

        /// Stopped part-way (over a second in), or a series whose next episode is up.
        public static bool IsContinue(LibraryEntry e) =>
            e?.State != null && (e.State.TimeOffsetMs > 1000 || IsUpNext(e));

        private static bool IsUpNext(LibraryEntry e) =>
            e.Type == MediaType.Series && e.State.TimeOffsetMs > 0 && e.State.TimeOffsetMs <= 1000 && !string.IsNullOrEmpty(e.State.VideoId);

        private void SetContinueRow() =>
            SetRow("continue", "Continue watching", ContinueTiles(libraryEntries, freshShows), TileStyle.Landscape);

        private static Tile ContinueTile(LibraryEntry e)
        {
            var parts = (e.State.VideoId ?? "").Split(':');
            int season = 0, episode = 0;
            var isEpisode = e.Type == MediaType.Series && parts.Length >= 3
                && int.TryParse(parts[parts.Length - 2], out season) && int.TryParse(parts[parts.Length - 1], out episode);
            var upNext = IsUpNext(e);
            var left = upNext ? "Up next" : e.State.DurationMs > 0 ? UiFormat.Remaining(e.State.DurationMs - e.State.TimeOffsetMs) : null;
            return new Tile
            {
                Id = e.Id, Type = e.Type, Title = e.Name, Poster = e.Poster,
                Image = isEpisode ? UiFormat.EpisodeStill(e.Id, season, episode) ?? UiFormat.CardArtFor(e.Id) : UiFormat.CardArtFor(e.Id),
                Logo = UiFormat.LogoFor(e.Id),
                Subtitle = UiFormat.Facts(isEpisode ? UiFormat.EpisodeColon(season, episode) : null, left),
                Progress = !upNext && e.State.DurationMs > 0 ? Math.Min(1, e.State.TimeOffsetMs / (double)e.State.DurationMs) : 0,
                Action = TileAction.Resume, VideoId = e.State.VideoId, ResumeMs = upNext ? 0 : e.State.TimeOffsetMs
            };
        }

        /// "Start something new": feed series nobody started yet, as S1:E1 cards that play the pilot.
        private void BuildFirstEpisodes()
        {
            var started = new HashSet<string>(libraryEntries.Where(e => e.State != null && (e.State.TimeOffsetMs > 0 || e.State.TimesWatched > 0 || !string.IsNullOrEmpty(e.State.Watched))).Select(e => e.Id));
            SetRow("firsteps", "Start something new", feed.Where(i => i.Meta.Type == MediaType.Series && !started.Contains(i.Meta.Id)).Take(12).Select(i => new Tile
            {
                Id = i.Meta.Id, Type = MediaType.Series, Title = i.Meta.Name, Poster = i.Meta.Poster,
                Image = UiFormat.EpisodeStill(i.Meta.Id, 1, 1) ?? UiFormat.CardArtFor(i.Meta.Id), Logo = UiFormat.LogoFor(i.Meta.Id),
                Subtitle = "S1:E1 · " + (i.Reason ?? "Pilot"), Action = TileAction.PlayFirst, VideoId = i.Meta.Id + ":1:1"
            }).ToList(), TileStyle.Landscape);
        }

        /// "Short episodes": library and feed series whose runtime is under 30 minutes (runtime from IMetaExtras).
        private void LoadShortEpisodes()
        {
            if (Services.Extras == null)
                return;
            var candidates = libraryEntries.Where(e => e.Type == MediaType.Series).Select(e => (e.Id, e.Name, e.Poster))
                .Concat(feed.Where(i => i.Meta.Type == MediaType.Series).Select(i => (i.Meta.Id, i.Meta.Name, i.Meta.Poster)))
                .GroupBy(c => c.Item1).Select(g => g.First()).Take(ShortCandidates).ToList();
            if (candidates.Count == 0)
                return;
            var extras = Services.Extras;
            Load(async token =>
            {
                var found = await Task.WhenAll(candidates.Select(async c =>
                {
                    try { return (c, await extras.GetAsync(MediaType.Series, c.Item1, token).ConfigureAwait(false)); }
                    catch (Exception) when (!token.IsCancellationRequested) { return (c, (MetaExtras)null); }
                })).ConfigureAwait(false);
                return found.Where(f => UiFormat.RuntimeMinutes(f.Item2?.Runtime) is int m && m > 0 && m < ShortRuntimeMinutes)
                    .Select(f => new Tile
                    {
                        Id = f.c.Item1, Type = MediaType.Series, Title = f.c.Item2, Poster = f.c.Item3,
                        Image = UiFormat.CardArtFor(f.c.Item1), Logo = UiFormat.LogoFor(f.c.Item1), Subtitle = f.Item2.Runtime + " episodes"
                    }).ToList();
            }, tiles => SetRow("short", "Short episodes", tiles, TileStyle.Landscape), _ => { });
        }

        private void LoadNewEpisodes(IReadOnlyList<LibraryEntry> live, DateTimeOffset now)
        {
            if (Services.Catalog == null)
                return;
            var library = Services.Library;
            var catalog = Services.Catalog;
            Load(token => CheckShowsAsync(catalog, library, live, now, token), found =>
            {
                freshShows = found.Where(r => r.Fresh != null).ToDictionary(r => r.Entry.Id, r => r.Fresh);
                SetContinueRow();
                // A show already in Continue watching carries its label there; this row keeps the others and the
                // episodes coming this week.
                var inContinue = new HashSet<string>(libraryEntries.Where(IsContinue).Select(e => e.Id));
                SetRow("new", "New episodes of your shows", NewEpisodeTiles(found.Where(r => r.Fresh == null || !inContinue.Contains(r.Entry.Id))), TileStyle.Landscape);
            }, _ => { });
        }

        /// One show's result from the new-episodes check: what came out since it was last watched, or the next
        /// episode due within a week.
        public sealed class ShowCheck
        {
            public LibraryEntry Entry;
            public FreshEpisodes Fresh;
            public VideoInfo Upcoming;
        }

        /// Parity C8/C9 without the stream-viability check: series with notifications on whose released,
        /// unwatched episodes came out after lastWatched (or the library add date), newest first; then shows with
        /// an episode coming within a week ("Next episode · Fri").
        public static async Task<IReadOnlyList<Tile>> FindNewEpisodesAsync(ICatalog catalog, ILibrary library, IReadOnlyList<LibraryEntry> entries, DateTimeOffset now, CancellationToken token) =>
            NewEpisodeTiles(await CheckShowsAsync(catalog, library, entries, now, token).ConfigureAwait(false));

        /// Checks the most recently watched series (notifications on) for released, unwatched episodes that came out
        /// after lastWatched (or the library add date), and for an episode due within a week.
        public static async Task<IReadOnlyList<ShowCheck>> CheckShowsAsync(ICatalog catalog, ILibrary library, IReadOnlyList<LibraryEntry> entries, DateTimeOffset now, CancellationToken token)
        {
            var candidates = entries.Where(e => e.Type == MediaType.Series && !e.Removed && !e.Temp && !(e.State?.NoNotif ?? false))
                .OrderByDescending(e => e.State?.LastWatched ?? e.Modified).Take(NewEpisodeSeriesLimit).ToList();
            var tasks = candidates.Select(async entry =>
            {
                try
                {
                    var meta = await catalog.GetMetaAsync(MediaType.Series, entry.Id, token).ConfigureAwait(false);
                    var baseline = entry.State?.LastWatched ?? entry.Created;
                    var fresh = meta.Videos.Where(v => v.Season > 0 && v.Released != null && v.Released > baseline && v.Released <= now && !library.IsWatched(entry, meta, v.Id)).ToList();
                    if (fresh.Count > 0)
                        return new ShowCheck
                        {
                            Entry = entry,
                            Fresh = new FreshEpisodes
                            {
                                First = fresh.OrderBy(v => v.Season).ThenBy(v => v.Episode).First(),
                                Latest = fresh.OrderByDescending(v => v.Released).First(),
                                Count = fresh.Count
                            }
                        };
                    var next = meta.Videos.Where(v => v.Season > 0 && v.Released != null && v.Released > now && v.Released <= now.AddDays(7)).OrderBy(v => v.Released).FirstOrDefault();
                    return next != null ? new ShowCheck { Entry = entry, Upcoming = next } : null;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    return null;
                }
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.Where(r => r != null).ToList();
        }

        /// New episodes first (newest release first), then shows with an episode due this week (soonest first).
        public static IReadOnlyList<Tile> NewEpisodeTiles(IEnumerable<ShowCheck> checks)
        {
            var list = checks.Where(c => c != null).ToList();
            var fresh = list.Where(c => c.Fresh != null).OrderByDescending(c => c.Fresh.Latest.Released).Select(c => new Tile
            {
                Id = c.Entry.Id, Type = MediaType.Series, Title = c.Entry.Name, Poster = c.Entry.Poster,
                Image = UiFormat.EpisodeStill(c.Entry.Id, c.Fresh.First.Season, c.Fresh.First.Episode) ?? UiFormat.CardArtFor(c.Entry.Id),
                Logo = UiFormat.LogoFor(c.Entry.Id),
                Subtitle = UiFormat.EpisodeColon(c.Fresh.Latest) + (c.Fresh.Count > 1 ? " · " + c.Fresh.Count + " new episodes" : ""),
                Badge = c.Fresh.Badge, VideoId = c.Fresh.First.Id
            });
            var upcoming = list.Where(c => c.Fresh == null && c.Upcoming != null).OrderBy(c => c.Upcoming.Released).Select(c => new Tile
            {
                Id = c.Entry.Id, Type = MediaType.Series, Title = c.Entry.Name, Poster = c.Entry.Poster,
                Image = UiFormat.CardArtFor(c.Entry.Id), Logo = UiFormat.LogoFor(c.Entry.Id),
                Subtitle = UiFormat.EpisodeColon(c.Upcoming),
                Badge = "Next episode · " + c.Upcoming.Released.Value.ToString("ddd", CultureInfo.InvariantCulture), VideoId = c.Upcoming.Id
            });
            return fresh.Concat(upcoming).ToList();
        }

        private void LoadCatalogRows()
        {
            if (Services.CatalogRows == null || Services.Catalog == null)
                return;
            Load(token => Services.CatalogRows.GetRowsAsync(token), specs =>
            {
                var sawTop = new HashSet<MediaType>();
                foreach (var spec in specs ?? Array.Empty<CatalogRowSpec>())
                {
                    // Cinemeta's "top" catalog becomes the ranked Top 10 row for its type.
                    var top = spec.CatalogId == "top" && spec.AddonId == "com.linvo.cinemeta" && sawTop.Add(spec.Type);
                    var key = top ? "top:" + (spec.Type == MediaType.Movie ? "movie" : "series") : "catalog:" + spec.AddonId + "/" + spec.Type + "/" + spec.CatalogId;
                    if (!order.Contains(key))
                        order.Add(key);
                    var captured = spec;
                    Load(token => Services.Catalog.GetCatalogAsync(captured.AddonId, captured.CatalogId, captured.Type, 0, token), items =>
                    {
                        var list = (items ?? Array.Empty<MetaSummary>()).ToList();
                        if (top)
                            SetRow(key, "Top 10 " + (captured.Type == MediaType.Movie ? "movies" : "series") + " today",
                                list.Take(10).Select((m, i) => new Tile { Id = m.Id, Type = m.Type, Title = m.Name, Subtitle = m.ReleaseInfo, Poster = m.Poster, Rank = i + 1 }).ToList(), TileStyle.Ranked);
                        else
                            SetRow(key, captured.Title, list.Select(m => new Tile { Id = m.Id, Type = m.Type, Title = m.Name, Subtitle = m.ReleaseInfo, Poster = m.Poster }).ToList(), TileStyle.Portrait);
                    }, _ => { });
                }
            }, _ => { });
        }

        private void SetRow(string key, string title, IReadOnlyList<Tile> items, TileStyle style = TileStyle.Portrait, bool menu = false)
        {
            // Keep focus on the same tile when a row reloads.
            string focusedId = null;
            if (rows.TryGetValue(key, out var old) && columns.TryGetValue(key, out var col) && col < old.Items.Count)
                focusedId = old.Items[col].Id;
            rows[key] = new HomeRow { Key = key, Title = title, Items = items, IsMenu = menu, Style = style };
            if (focusedId != null)
            {
                var index = items.ToList().FindIndex(t => t.Id == focusedId);
                columns[key] = index >= 0 ? index : Math.Min(columns[key], Math.Max(0, items.Count - 1));
            }
            Changed();
        }

        /// Trailer view: while a trailer plays, the rows slide down to a thin strip, the menu hides and the
        /// billboard keeps only the logo and one line. Any key ends it (that key only restores the UI) and the
        /// trailer starts again once focus has rested for TrailerDelayMs.
        public bool Immersive => TrailerId != null;

        /// Trailer view buttons, as on Netflix: 0 = Play, 1 = More info (the title page).
        public int HeroButton { get; private set; }
        public static readonly string[] HeroButtons = { "Play", "More info" };

        public override bool HandleKey(KeyPress key)
        {
            if (Intro != null && Intro.HandleKey(NowMs))
            {
                Changed();
                return true;
            }
            if (key.Key == RemoteKey.PlayPause || key.Key == RemoteKey.Play)
            {
                // The direct play key always plays the focused title (user, 2026-11-19), also while a preview is
                // sounding or the trailer view is up. Preview sound is the "Preview sound" setting, not a key.
                var focused = Immersive ? HeroTile ?? FocusedTile : FocusedTile;
                if (focused != null && FocusedRow != null && !FocusedRow.IsMenu && IsTitle(focused))
                {
                    ShrinkCard();
                    cardDismissed = true;
                    PlayNow(focused);
                }
                return true;
            }
            if (Immersive)
                return ImmersiveKey(key);
            // Every other key restarts the pop-out dwell, even one that doesn't move focus (Left on the first tile).
            lastKeyMs = key.AtMs;
            if (PoppedTile != null)
                return PoppedKey(key);
            var handled = Navigate(key);
            NoteFocus(key.AtMs);
            return handled;
        }

        /// Trailer view: Left/Right move between Play and More info, OK activates; any other key (or Left/Right past
        /// either button) stops the trailer and brings the page back without moving focus.
        private bool ImmersiveKey(KeyPress key)
        {
            var tile = HeroTile;
            switch (key.Key)
            {
                case RemoteKey.Left when HeroButton > 0:
                    HeroButton--;
                    return true;
                case RemoteKey.Right when HeroButton < HeroButtons.Length - 1:
                    HeroButton++;
                    return true;
                case RemoteKey.Select:
                case RemoteKey.Info:
                    var play = key.Key == RemoteKey.Select && HeroButton == 0;
                    DismissTrailer(key.AtMs);
                    if (tile == null) return true;
                    if (!play) OpenDetails(tile);
                    else if (tile.Action == TileAction.Resume) Open(tile);
                    else PlayTile(tile, 0);
                    return true;
                default:   // Back, Up, Down, or past either button: the page comes back; focus stays put
                    DismissTrailer(key.AtMs);
                    return true;
            }
        }

        /// Button of the popped card that has focus, or null while focus is on the card itself (the tile level).
        public PopButton? CardButton { get; private set; }

        /// Play, My List (with a library), Rate (with a feedback sink), More info.
        public IReadOnlyList<PopButton> CardButtons
        {
            get
            {
                var list = new List<PopButton> { PopButton.Play };
                if (Services.Library != null && Services.Catalog != null) list.Add(PopButton.MyList);
                if (Services.Feedback != null) list.Add(PopButton.Rate);
                list.Add(PopButton.Info);
                return list;
            }
        }

        /// What OK does on a title card, popped or not: Continue watching resumes, every other title opens its page.
        public static PopButton OkButton(Tile tile) => tile != null && tile.Action == TileAction.Resume ? PopButton.Play : PopButton.Info;

        /// Popped card. On the card itself every key does what it does on the tile (OK, arrows that move on at once,
        /// the colour shortcuts), except Down, which steps into the card's button row. In the row Left/Right move
        /// between Play, My List, Rate and More info, OK activates, Up returns to the card, Down goes on to the next
        /// row and Back shrinks the card. Focus is never trapped and every button is reachable with the arrows.
        private bool PoppedKey(KeyPress key)
        {
            var tile = PoppedTile;
            var buttons = CardButtons;
            if (CardButton != null)
            {
                var at = Math.Max(0, buttons.ToList().IndexOf(CardButton.Value));
                switch (key.Key)
                {
                    case RemoteKey.Left: CardButton = buttons[Math.Max(0, at - 1)]; return true;
                    case RemoteKey.Right: CardButton = buttons[Math.Min(buttons.Count - 1, at + 1)]; return true;
                    case RemoteKey.Up: CardButton = null; return true;
                    case RemoteKey.Select:
                        ActivateCardButton(tile, CardButton.Value);
                        return true;
                }
            }
            switch (key.Key)
            {
                case RemoteKey.Down when CardButton == null:
                    CardButton = buttons[0];
                    return true;
                case RemoteKey.Select:
                    ActivateCardButton(tile, OkButton(tile));
                    return true;
                case RemoteKey.Info:
                case RemoteKey.Blue:
                    ActivateCardButton(tile, PopButton.Info);
                    return true;
                case RemoteKey.Green:
                    ToggleMyList(tile);
                    return true;
                case RemoteKey.Back:
                    ShrinkCard();
                    cardDismissed = true;
                    return true;
                default:
                    ShrinkCard();
                    var handled = Navigate(key);
                    NoteFocus(key.AtMs);
                    return handled;
            }
        }

        private void ActivateCardButton(Tile tile, PopButton button)
        {
            switch (button)
            {
                case PopButton.MyList:
                    ToggleMyList(tile);
                    return;
                case PopButton.Rate:
                    RateDialog.Show(Shell, Services.Feedback, new MetaSummary { Id = tile.Id, Type = tile.Type, Name = tile.Title, Poster = tile.Poster });
                    return;
            }
            ShrinkCard();
            cardDismissed = true;
            if (button == PopButton.Play) PlayNow(tile);
            else OpenDetails(tile);
        }

        /// Plays the card's episode: a Continue watching card from where it stopped, anything else from the start.
        private void PlayNow(Tile tile) => PlayTile(tile, tile.Action == TileAction.Resume ? tile.ResumeMs : 0);

        /// Green on a popped card: "My List" is the Stremio library (add needs the full meta, so it loads first).
        private void ToggleMyList(Tile tile)
        {
            if (Services.Library == null || Services.Catalog == null)
                return;
            if (InLibrary(tile.Id))
                Run(token => Services.Library.RemoveAsync(tile.Id, token), () => { Shell.ShowToast("Removed from my library"); Reload(); });
            else
                Run(async token =>
                {
                    var meta = await Services.Catalog.GetMetaAsync(tile.Type, tile.Id, token).ConfigureAwait(false);
                    await Services.Library.AddAsync(meta, token).ConfigureAwait(false);
                }, () => { Shell.ShowToast("Added to my library"); Reload(); });
        }

        private void ShrinkCard()
        {
            if (PoppedTile == null && CardTrailerTitleId == null)
                return;
            PoppedTile = null;
            PoppedRowKey = null;
            CardButton = null;
            CardTrailerTitleId = null;
            CardTrailerVideoId = null;
            Changed();
        }

        /// Pops the focused row title once focus rests (and a key was pressed recently: never on launch or while the
        /// TV idles), then starts its trailer under the preview rules. Only one trailer plays: the billboard's trailer
        /// view never starts while cards are on.
        private void TickCard(long nowMs)
        {
            var tile = FocusedTile;
            var row = FocusedRow;
            if (Shell?.Dialog != null)
                lastKeyMs = nowMs;   // a dialog or card menu: the dwell starts over once it closes
            if (PoppedTile != null && (tile?.Id != PoppedTile.Id || row?.Key != PoppedRowKey || Shell?.Dialog != null || Shell?.Current != this))
                ShrinkCard();
            else if (PoppedTile != null && tile != PoppedTile)
                PoppedTile = tile;   // the row reloaded (My List, progress): same title, fresh card facts; the trailer plays on
            if (tile == null || row == null || row.IsMenu || !IsTitle(tile))
                return;
            if (PoppedTile == null)
            {
                var rested = nowMs - Math.Max(focusChangedMs, lastKeyMs) >= Services.CardPopDelayMs;
                if (!rested || cardDismissed || Shell?.Dialog != null || !Services.RecentlyInteracted(nowMs))
                    return;
                PoppedTile = tile;
                PoppedRowKey = row.Key;
                poppedAtMs = nowMs;
                Changed();
            }
            if (CardTrailerTitleId != null && Services.TrailerEndCount != cardEndSeen && Services.EndedTrailerId == CardTrailerVideoId)
            {
                CardTrailerTitleId = null;   // played once: the card keeps the still art until focus moves
                CardTrailerVideoId = null;
                cardTrailerPlayed = true;
                Changed();
            }
            // The trailer starts once the title's extras are in (they carry the YouTube fallback id), or after a
            // second without them (IMDb needs only the title id).
            var extrasReady = CardExtras != null || nowMs - poppedAtMs >= 1000;
            if (CardTrailerTitleId == null && !cardTrailerPlayed && extrasReady && Services.TrailerPlayback && Services.AutoplayPreviews && Services.RecentlyInteracted(nowMs))
            {
                CardTrailerTitleId = PoppedTile.Id;
                CardTrailerVideoId = CardExtras?.TrailerId;
                TrailerMuted = !Services.PreviewSound;
                cardEndSeen = Services.TrailerEndCount;
                Changed();
            }
        }

        private void DismissTrailer(long nowMs)
        {
            TrailerId = null;   // dismissed: it waits until focus moves away and comes back
            trailerPlayed = true;
            focusChangedMs = nowMs;
            HeroButton = 0;
        }

        private void NoteFocus(long nowMs)
        {
            var focusKey = FocusedRow?.Key + "/" + FocusedTile?.Id;
            if (focusKey == lastFocusKey)
                return;
            lastFocusKey = focusKey;
            focusChangedMs = nowMs;
            HeroButton = 0;
            TrailerId = null;
            trailerPlayed = false;
            ShrinkCard();
            cardDismissed = popAfterNavigation;
            popAfterNavigation = false;
            cardTrailerPlayed = false;
        }

        protected internal override void Tick(long nowMs)
        {
            RunPendingFirstLoad();
            if (Intro != null)
            {
                StartIntroIfReady(nowMs);
                var wasHandingOff = Intro.HandingOff;
                Intro.Tick(nowMs, ReadyForReveal && (Services.LaunchHandoffReady?.Invoke() ?? true));
                if (!wasHandingOff && Intro.HandingOff)
                    Services.LaunchTrace?.Invoke("intro: hand-off (" + Rows.Count(r => !r.IsMenu) + " rows)");
                if (Intro.Done)
                {
                    Services.LaunchTrace?.Invoke("intro: done");
                    Intro = null;
                    var then = afterIntro;
                    afterIntro = null;
                    then?.Invoke();
                }
                Changed();   // the intro is a timeline: lay out again on every tick while it plays
            }
            TickTrailerCompact(nowMs);
            NoteFocus(nowMs);
            if (TrailerId != null && Services.TrailerEndCount != trailerEndSeen && Services.EndedTrailerId == TrailerId)
            {
                TrailerId = null;   // played once: back to the still art until focus comes back
                trailerPlayed = true;
                Changed();
            }
            if (Shell?.Dialog != null)
            {
                // Never start a trailer (or the trailer view) behind a dialog or card menu.
                focusChangedMs = Math.Max(focusChangedMs, nowMs - TrailerDelayMs + 1000);
            }
            var tile = FocusedTile;
            if (tile != null && tile.Action == TileAction.Service)
            {
                // A Services-row card: the billboard describes the service once focus rests (no extras, no trailer).
                if (HeroTile != tile && (HeroTile == null || nowMs - focusChangedMs >= HeroDelayMs))
                {
                    HeroTile = tile;
                    HeroExtras = null;
                    TrailerId = null;
                    Changed();
                }
                return;
            }
            if (tile == null || !IsTitle(tile))
                return;
            if ((HeroTile == null || HeroTile.Id != tile.Id) && (HeroTile == null || nowMs - focusChangedMs >= HeroDelayMs))
            {
                HeroTile = tile;
                HeroExtras = null;
                TrailerId = null;
                if (Services.Extras != null && IsTitle(tile))
                {
                    var wanted = tile;
                    Load(token => Services.Extras.GetAsync(wanted.Type, wanted.Id, token), extras =>
                    {
                        if (HeroTile == wanted)
                            HeroExtras = extras;
                    }, _ => { });
                }
                Changed();
            }
            else if (HeroTile != null && HeroTile.Id == tile.Id && HeroTile != tile)
                HeroTile = tile;   // same title from another row: keep the art, refresh the card facts
            if (Services.TrailerCards)
            {
                TickCard(nowMs);
                return;
            }
            if (TrailerId == null && !trailerPlayed && Services.TrailerPlayback && Services.AutoplayPreviews && Services.RecentlyInteracted(nowMs) && HeroTile != null && HeroTile.Id == tile.Id && HeroExtras?.TrailerId != null && nowMs - focusChangedMs >= TrailerDelayMs)
            {
                TrailerId = HeroExtras.TrailerId;
                TrailerMuted = !Services.PreviewSound;
                trailerEndSeen = Services.TrailerEndCount;
                trailerStartSeen = Services.TrailerStartCount;
                trailerStartedMs = -1;
                Changed();
            }
        }

        private static bool IsTitle(Tile tile) => tile.Action == TileAction.Details || tile.Action == TileAction.Resume || tile.Action == TileAction.PlayFirst;

        private bool Navigate(KeyPress key)
        {
            var visible = Rows;
            if (visible.Count == 0)
                return key.Key != RemoteKey.Back;
            var rowIndex = FocusedRowIndex;
            var row = visible[rowIndex];
            var column = FocusedColumn(row);
            switch (key.Key)
            {
                case RemoteKey.Up:
                    focusedKey = visible[Math.Max(0, rowIndex - 1)].Key;
                    return true;
                case RemoteKey.Down:
                    focusedKey = visible[Math.Min(visible.Count - 1, rowIndex + 1)].Key;
                    return true;
                case RemoteKey.Left:
                    columns[row.Key] = Math.Max(0, column - 1);
                    focusedKey = row.Key;
                    return true;
                case RemoteKey.Right:
                    columns[row.Key] = Math.Min(row.Items.Count - 1, column + 1);
                    focusedKey = row.Key;
                    return true;
                case RemoteKey.Select:
                    focusedKey = row.Key;
                    if (row.Items.Count > 0)
                        Open(row.Items[column]);
                    return true;
                case RemoteKey.Info:
                case RemoteKey.Blue:
                    // Title page for any title card (center may resume or play instead).
                    if (row.Items.Count > 0 && IsTitle(row.Items[column]))
                        OpenDetails(row.Items[column]);
                    return true;
                case RemoteKey.Menu:
                case RemoteKey.Yellow:
                    // Card actions: title page and rate. (The RU7100 remote may lack Menu/Info keys; its
                    // colour-key pad gives Yellow/Blue as fallbacks. To verify on the TV.)
                    if (row.Items.Count > 0 && IsTitle(row.Items[column]))
                    {
                        var tile = row.Items[column];
                        if (Services.Feedback == null)
                            OpenDetails(tile);
                        else
                            RateDialog.ShowCardActions(Shell, Services.Feedback, new MetaSummary { Id = tile.Id, Type = tile.Type, Name = tile.Title, Poster = tile.Poster }, () => OpenDetails(tile));
                    }
                    return true;
                case RemoteKey.Back:
                    // First Back returns to the first tile of the top content row; at the top it is unhandled (host may exit).
                    if (rowIndex > 1 || column > 0 && !row.IsMenu)
                    {
                        focusedKey = visible.Count > 1 ? visible[1].Key : visible[0].Key;
                        columns[focusedKey] = 0;
                        return true;
                    }
                    return false;
            }
            return true;
        }

        private void OpenDetails(Tile tile) =>
            Shell.Push(new DetailsViewModel(tile.Type, tile.Id, new MetaSummary { Id = tile.Id, Type = tile.Type, Name = tile.Title, Poster = tile.Poster }));

        /// One press to continue: fetches the title's metadata and starts playback of the card's episode.
        private bool playPending;

        /// One press to continue: fetches the title's metadata and starts the card's episode. A second press while
        /// the metadata loads is ignored, and a result that arrives after the user moved to another screen is dropped.
        /// At 90 % or more of an episode (parity H2) it plays the next released episode instead.
        private void PlayTile(Tile tile, long resumeMs)
        {
            if (playPending)
                return;
            playPending = true;
            var shell = Shell;
            Load(token => Services.Catalog.GetMetaAsync(tile.Type, tile.Id, token), meta =>
            {
                playPending = false;
                if (shell.Current != this || shell.Dialog != null)
                    return;
                var video = tile.Type == MediaType.Series ? meta.Videos.FirstOrDefault(v => v.Id == tile.VideoId) ?? meta.Videos.FirstOrDefault(v => v.Season > 0) : null;
                if (tile.Type == MediaType.Series && video == null)
                {
                    OpenDetails(tile);
                    return;
                }
                if (resumeMs > 0 && tile.Progress >= 0.9)
                {
                    resumeMs = 0;
                    if (video != null)
                    {
                        var list = meta.Videos.Where(v => video.Season <= 0 || v.Season > 0).ToList();
                        var index = list.FindIndex(v => v.Id == video.Id);
                        var next = index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
                        if (next == null || !UiFormat.IsReleased(next, Services.Clock.UtcNow))
                        {
                            OpenDetails(tile);   // caught up: the title page says what's next
                            return;
                        }
                        video = next;
                    }
                }
                shell.StartPlayback(new PlayRequest { Meta = meta, Video = video, ResumeMs = resumeMs });
            }, _ =>
            {
                playPending = false;
                if (shell.Current == this)
                    OpenDetails(tile);
            });
        }

        /// Settings list: the rarely used destinations (Add-ons, account) and the switches. Choosing a switch flips it
        /// and keeps the list open with the new value.
        private void ShowSettings()
        {
            var settings = Services.Player?.Settings;
            var buttons = new List<DialogButton>();
            if (Services.Addons != null)
                buttons.Add(new DialogButton("Add-ons", () => Shell.Push(new AddonsViewModel())));
            if (Services.Account != null)
                buttons.Add(Services.Account.IsSignedIn
                    ? new DialogButton("Stremio account", () => Open(new Tile { Action = TileAction.Account }))
                    : new DialogButton("Sign in to Stremio", () => Shell.Push(new SignInViewModel())));
            if (settings != null)
            {
                buttons.Add(new DialogButton("Reduced motion: " + (settings.ReducedMotion ? "On" : "Off"), () =>
                {
                    settings.SetReducedMotion(!settings.ReducedMotion);
                    ShowSettings(2);
                }));
                buttons.Add(new DialogButton("Autoplay previews: " + (settings.AutoplayPreviews ? "On" : "Off"), () =>
                {
                    settings.SetAutoplayPreviews(!settings.AutoplayPreviews);
                    ShowSettings(3);
                }));
                buttons.Add(new DialogButton("Preview sound: " + (settings.PreviewSound ? "On" : "Off"), () =>
                {
                    settings.SetPreviewSound(!settings.PreviewSound);
                    ShowSettings(4);
                }));
                buttons.Add(new DialogButton("Launch sound: " + (settings.LaunchSound ? "On" : "Off"), () =>
                {
                    settings.SetLaunchSound(!settings.LaunchSound);
                    ShowSettings(5);
                }));
            }
            buttons.Add(new DialogButton("Done", null));
            Shell.ShowDialog(new ConfirmDialog("Settings", "Previews are trailers that play on a title you rest on. Reduced motion turns off animations.", buttons.ToArray()) { Focus = Math.Min(settingsFocus, buttons.Count - 1) });
            settingsFocus = 0;
        }

        private int settingsFocus;

        /// Reopens the list on the switch that was just flipped (counted from the end, so missing rows don't shift it).
        private void ShowSettings(int switchRow)
        {
            var leading = (Services.Addons != null ? 1 : 0) + (Services.Account != null ? 1 : 0);
            settingsFocus = leading + switchRow - 2;
            ShowSettings();
        }

        private void Open(Tile tile)
        {
            switch (tile.Action)
            {
                case TileAction.Search: Shell.Push(new SearchViewModel()); break;
                case TileAction.Browse: Shell.Push(new BrowseViewModel()); break;
                case TileAction.Queue: Shell.Push(new QueueViewModel()); break;
                case TileAction.Addons: Shell.Push(new AddonsViewModel()); break;
                case TileAction.SignIn: Shell.Push(new SignInViewModel()); break;
                case TileAction.Resume: PlayTile(tile, tile.ResumeMs); break;
                case TileAction.Service:
                    if (tile.ServiceCard?.Service != null)
                        Shell.Push(new HomeViewModel(tile.ServiceCard.Service));
                    break;
                case TileAction.SeeAll: Shell.Push(new BrowseViewModel(tile.BrowsePreset)); break;
                case TileAction.ServiceChip:
                    if (tile.Id != ServiceMode)
                    {
                        ServiceMode = tile.Id;
                        foreach (var key in order.Where(k => k.StartsWith("svc:", StringComparison.Ordinal)).ToList())
                            rows.Remove(key);
                        HeroTile = null;
                        LoadServiceRows();
                        Changed();
                    }
                    break;
                case TileAction.Home:
                    // Already here: OK returns to the first row.
                    var shelves = Rows;
                    if (shelves.Count > 1)
                    {
                        focusedKey = shelves[1].Key;
                        columns[focusedKey] = 0;
                    }
                    break;
                case TileAction.Motion:
                    ShowSettings();
                    break;
                case TileAction.Account:
                    Shell.ShowDialog(new ConfirmDialog("Stremio account", "Signed in. Library and progress sync with Stremio.",
                        new DialogButton("Done", null),
                        new DialogButton("Sign out", () => Run(token => Services.Account.SignOutAsync(token), () =>
                        {
                            rows.Remove("continue");
                            rows.Remove("library");
                            rows.Remove("new");
                            OnResumed();
                        }))));
                    break;
                default:
                    OpenDetails(tile);
                    break;
            }
        }
    }
}
