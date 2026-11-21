# Streaming services: plan

Status: approved 2026-11-20 (mocks `docs/design/10-services-home.html` to `12-services-all.html`, tints and JustWatch as primary source). Building in the order of section 5. Decisions so far: AGENTS.md, 2026-11-20 "Streaming-service feeds".

The goal: open "Apple TV" or "Disney+" and feel like you are inside that service's catalogue, with its popular titles, what's new on it, and its genres. Every title still plays through our app (Stremio add-on sources and torrents, played directly on the TV). A service is only a way to browse. We never open or connect to the service itself.

**What you decided (2026-11-20):**
- A service is a place you enter, not a filter. Home and the rest of the app stay unfiltered, and there is no service picker or "which services" setting.
- Catalogues come from the least restricted region. That is the US: its lists are larger and it adds Hulu, Paramount+ and Peacock (numbers below).
- Each service page gets its own colour tint (section 2, mocked).
- JustWatch-backed code may go to the public repo.

## 1. Where the catalogue data comes from

Tested from the Mac on 2026-11-20. The TV would make the same requests itself and nothing is bundled in the app, but **nothing has been requested from the RU7100 yet** (YouTube behaved differently from the TV than from the Mac), so the first build step is one JustWatch request from the TV.

| Source | Key | Regions | Paging | Genres | Fresh | Verdict |
|---|---|---|---|---|---|---|
| **JustWatch GraphQL** (`apis.justwatch.com/graphql`, `popularTitles`, `newTitles`) | none | Per country (US and a smaller region tested) | `first` + `offset`, so our `skip` maps directly; tested `offset=40` = cursor page 2 | Yes (`filter.genres`) | Live | **Primary**, but unofficial |
| **Streaming Catalogs** Stremio add-on (`pw.ers.netflix-catalog`, 7a82163c306e-stremio-netflix-catalog-addon.baby-beamup.club) | none | One list, whatever region is configured | None: fixed ~99 items, `skip` ignored | No | Rebuilt from JustWatch every 6 h | **Fallback**: one "Popular" row per service |
| **TMDB discover** (`with_watch_providers`, `watch_region=US`) | user's TMDB key | Per TMDB's docs (its provider data comes from JustWatch) | `page`, 20 per page (docs) | Yes (docs) | n/a | **Official alternative** once a key is supplied (AGENTS.md: TMDB only with the user's key). From TMDB's docs, not tested: no key |
| Hosted TMDB add-on (`tmdb-addon`, streaming catalogs) | none | Not proven | `skip`, 20 per page | Yes | n/a | **Not usable**: every custom config returned 504 |

### JustWatch in detail

- **US vs a smaller region** (subscription titles, series / movies):

  | Service | US | Smaller region |
  |---|---|---|
  | Netflix | 3,580 / 4,709 | 2,739 / 4,339 |
  | HBO Max | 1,782 / 1,911 | 606 / 535 |
  | Crunchyroll | 1,759 / 258 | 873 / 23 |
  | Disney+ | 688 / 1,422 | not tracked |
  | Apple TV | 228 / 132 | 224 / 121 |
  | Hulu | 1,948 / 1,293 | — |
  | Paramount+ | 1,004 / 1,074 | — |
  | Peacock | 618 / 976 | — |
  | Prime Video | id `amp`, 5,508 series: includes add-on channels, needs a closer look | 772 / 1,134 (id `prv`) |

  Provider ids differ by country (Prime Video is `amp` in the US, `prv` in some other regions), so the service list is read from JustWatch's `packages` query for the region, not hard-coded.
- **New this week (US, 7 days to 2026-11-20):** HBO Max 80, Netflix 76, Disney+ 16, Apple TV 5. Some new US entries aren't shows or films (a podcast, a wrestling event on Disney+), so they go through the source check like everything else (section 3).
- **IMDb ids:** only 2 of the 224 Apple series in the smaller region lacked one; titles without an IMDb id are dropped since we can't find sources for them.
- **"New on X"** uses `newTitles` (pageType `NEW`) for one day at a time; the row walks back day by day, up to 30 days, until it has 20 titles. New seasons come back with their show's title and IMDb id.
- **Popular** is JustWatch's popularity in that country, not the service's own viewing numbers. The row is called "Popular in the US" for that reason, never "Top 10 on Apple TV".
- **Names:** shown as plain text from a small table keyed by provider (Apple TV, Disney+, HBO Max, Netflix, Prime Video, Hulu, Paramount+, Peacock, Crunchyroll). JustWatch's own names are used for anything not in the table.
- **Risk:** the GraphQL API is undocumented and meant for JustWatch's own sites. It can change or block without notice, and its terms don't grant third-party use. This is the same position as the IMDb trailer decision (personal, non-commercial). Mitigation: the code sits behind one `IServiceCatalog` interface, so TMDB (with a key) or the Streaming Catalogs add-on can replace it without touching the UI, and a failure falls back to the add-on's popular list.

### How it fits the code

- New `IServiceCatalog` in `core/Addons` (or `core/Services`): `GetServicesAsync(region)`, `GetTitlesAsync(service, type, sort, genre, skip)`, `GetNewAsync(service, type, days)`. Results are `MetaSummary` with IMDb ids, so title pages, sources and watch state work unchanged.
- Artwork and facts come from Cinemeta/metahub by IMDb id with `?format=jpg` (RU7100 can't decode WebP), as today. JustWatch's own images are not used.
- **Browse:** add `BrowseFilter.Service` ("Any service", then every service). `ServiceBrowseSource : IBrowseSource` maps `skip` to `offset` and `genre` to JustWatch genre codes. The other filters (year, rating, "Watchable with", quality) keep working unchanged. A service page's "See all" opens Browse with the service preset (the `BrowseViewModel(preset)` constructor already exists). "Any service" is the default, so Browse stays unfiltered.
- **Entry point:** a Services row on Home only, no menu item. In `HomeViewModel.FixedOrder` it goes after `continue`, `new` and `foryou` and before `firsteps`, with one guard: Services is never the first row, even when the rows above are empty, so Home always opens on a title. Back from a service page returns to the same card. If you end up using services daily, a "Services" menu item can be added later.
- **Offline:** the last loaded first page of each service row is cached on the TV like the feed, so the row isn't empty while the Mac is asleep or the API is slow.
- **Request cost.** A row on a service page is one request (`first=20`). The service page loads its first row first and the rest as you scroll, the same way Home does. Each card in the Home row costs about 9 small requests: two `popularTitles` calls (series and movies, for the art, names and counts) and up to seven daily `newTitles` calls for "N new this week". They run in the background after Home's first rows are up, and the results are cached on the TV for 6 hours. If that's too much, the card's meta line can drop the weekly count.

## 2. UI

1. **Services row on Home** (`10-services-home.html`). It sits below the personal rows, so reaching it scrolls Home; the mock shows that scrolled layout (380 px banner, row above peeking in dimmed, next row below). One landscape card for every service, in a fixed order (biggest catalogue first). Each card shows the backdrop of what's most popular on that service right now under a gradient in the service's tint, the service name as plain white Title text, and a meta line ("5 new this week"). Focus ring and motion are the same as on every other row. While a service card has focus, the banner shows the service name in Display type, its series and movie counts, and what's popular on it now. Home itself stays ink; only the cards carry tints. All numbers in the mock are measured (US, 2026-11-20).
2. **Service page** (`11-services-page.html`). OK on a service card opens a full page for that service:
   - Top-left: the service name in Title type where Home has its menu, with "Services" in meta grey above it. Back returns to Home with focus on the same card.
   - A chip bar that acts as the service's own menu: Home, Series, Movies, Genres. The chip for where you are has the amber selected outline (viewer state); white means focus, as everywhere.
   - Billboard: Home's compact hero for the focused title (logo, facts, synopsis) with its rank. Like Home, it has no buttons: OK on a card opens the title page and Play/Pause plays it. Trailer preview and pop-out cards follow the existing rules.
   - Rows, all scoped to the service: Popular in the US (with rank badges), New on Apple TV, then genre rows (Drama, Comedy, Sci-Fi, …) chosen by what the service has most of. If you've started anything on the service, a Continue watching row comes first (not shown in the mock). Every row ends with a "See all" tile that opens mock 12.
3. **See all / Browse** (`12-services-all.html`). The existing Browse grid with the Service chip set to the service, so all Browse filters still apply. It keeps the service's tint because you are still inside the service.

Dropped since round one: Settings → Services (mock 13). With no picker and a fixed region, there is nothing to set.

### Service tints (design-system change, needs your approval)

Each service gets one tint colour. It is used in three places only: the page background (the ink page shifted toward the tint), the scrims and gradients over artwork, and a soft glow behind the service name. It is never used for focus (white), viewer state (amber) or health (green), and never on text, chips, badges or progress. Off the service's own pages, the tint appears only in its Home card's gradient. On the TV it's the same pre-rendered gradient images with a different colour, so it costs nothing per frame.

| Service | Page ink | Tint (glow and gradients) |
|---|---|---|
| Apple TV | #0E1622 | #3C5A7A steel blue |
| Netflix | #160B0E | #7A2430 deep crimson |
| Disney+ | #0A1022 | #2A4C9A royal blue |
| HBO Max | #120C1F | #5A3A9A violet |
| Prime Video | #091620 | #1F6680 teal blue |
| Hulu | #0A1712 | #2F7A55 forest |
| Paramount+ | #0A1020 | #3354A0 cobalt |
| Peacock | #0C1416 | #3F6E70 slate teal |
| Crunchyroll | #170F0A | #7A4A2A rust (kept dark so it never reads as amber) |

These hues *suggest* each service's usual colour without matching it: they are dark, desaturated washes, not brand colours, and they sit next to no logos. Open `11-services-page.html?tint=off` to compare with the plain ink page.

### "Feels like their app" without copying it

What makes a service feel like a place is structure: you go *into* it, everything on the page belongs to it, it has its own front page, its own new arrivals, its own popularity list and its own genres, and Back takes you out again. The tint adds a sense of place on top. The mocks deliver this with our own components.

We do **not** use the services' logos, exact brand colours, fonts, icons, artwork style, splash animations or screen layouts. The service name appears only as plain text in our type, which identifies the catalogue the same way JustWatch or a TV guide does. Title artwork is the title's own (Cinemeta/metahub), as everywhere else in the app.

## 3. Playability

A service lists titles that may have no good source. Before a row is shown, its first titles go through the existing source-signature check (the one behind Browse's "Watchable with"), and titles with no source are left out. Nothing is reordered once a row is on screen, so navigation stays smooth. Rows below the first are checked while you are still above them. In See all, Browse's "Watchable with" filter narrows the grid to titles with a matching source, as it does today.

## 4. Approvals

All open questions are settled: tints and colours as in section 2, JustWatch as the primary source (both approved 2026-11-20).

## 5. Built so far (2026-11-20)

- Catalogue client (`core/Addons/StreamingServices.cs`): JustWatch with the add-on fallback, the nine services with names and tints.
- Browse: `BrowseFilter.Service` (after Type); a service's catalogue fills the grid under its name, in its tint. Browse from the menu never reopens inside a service.
- Home: the Services row below For you (never the first row), tinted cards, the banner for a focused service; numbers cached for 6 hours in memory.
- Service page: `HomeViewModel(service)` reuses Home's rows, billboard, pop-out cards and trailers, with a chip header (Home, Series, Movies, Genres), the tinted page ink and glow, and See all cards that open Browse.
- Checked in the Mac preview with live data. Not yet done: the request check from the TV, frame timings on the TV, caching the Services row on disk for when the network is slow, and the source check before a service row is shown (section 3; Browse's own availability check works inside a service).

## 6. Order

1. One JustWatch request and one Streaming Catalogs request from the RU7100, to confirm they work from the TV.
2. `IServiceCatalog` over JustWatch (US), with the Streaming Catalogs add-on as fallback; tests with recorded responses.
3. `BrowseFilter.Service` and `ServiceBrowseSource`.
4. Services row on Home and the service page with tints (preview first, then the TV).
5. TMDB provider (only when a key is supplied).
6. Port to the public repo with your go-ahead, as for any change.
