# Recommendation feed

The Mac builds the TV's recommendation rows with plain rules. Nothing is trained. A scheduled CLI reads the taste signals, gathers candidates from free sources, filters and scores them, checks that each shown title has a stream the RU7100 can play, and writes a versioned snapshot. The publish step sends the snapshot to the TV, which keeps showing the last one while the Mac sleeps.

The Netflix history only bootstraps the profile. As in-app feedback grows, Netflix signals lose weight, and once the user cancels Netflix the profile keeps learning from what happens on the TV. An agent is a fallback for thin feeds. The deterministic feed ships either way.

Code lives in `mac/feed/`. Tests and fixtures live in `mac/feed/tests/`.

## Run it

```sh
bash scripts/refresh-imdb.sh                     # IMDb datasets, about weekly
python3 -m mac.feed build --profile 'Profile name'
python3 -m mac.feed profile --profile 'Profile name'          # print the taste profile summary
python3 -m mac.feed ingest-feedback events.json               # merge TV events into the local log
```

`build` writes `local-data/recommendation-feed.json` (override with `--output`) and a sidecar `recommendation-feed.report.json` with the profile summary, per-source errors, every filtered title and its reason. Other useful flags are `--config`, `--feedback FILE` (repeatable), `--library FILE`, `--offline` (cache only), `--no-playability`, `--no-curation-command` and `--now ISO` for reproducible runs.

`python3 mac/recommendations.py --profile X` still runs the older single-list IMDb ranker (`mac/feed/legacy.py`). It writes `local-data/recommendations.json`, which `mcp/recommendation-server.mjs` reads. The two outputs are separate files.

## Pipeline

1. Inputs: the Netflix SQLite from `mac/netflix_history.py` (read-only), the TV feedback log `local-data/tv-feedback.jsonl`, and optionally the Stremio library (a `datastoreGet` JSON file, or the account when `stremioLibrary.authKey` is set).
2. Taste profile (`profile.py`): per-title weights, then feature affinities.
3. Candidate sources (`sources/`): each source runs on its own with a time budget. A failing source is recorded in the report and the others continue.
4. Filters and scoring (`ranking.py`).
5. Rows with diversity and lazy playability checks (`rows.py`, `playability.py`).
6. Snapshot, schema check (`schema.py`), and the fallback request when rows are thin (`fallback.py`).

## Taste profile

Every signal has a fixed weight from `profile.weights` in the config and decays with a half-life. The weights sum into one number per title.

| Signal | Weight | Half-life |
|---|---|---|
| TV `completed` (movie) | +2 | 180 d |
| TV `completed` (series) | +0.6 per episode, at most +4 | 180 d |
| Rewatch (same video completed 7+ days apart) | +2 | 180 d |
| TV `play_started` / best `progress` (only without a completion) | +0.3 / up to +0.5 | 180 d |
| TV `abandoned` before 25% (or 15 min) | -2 (movie, or a show dropped on its first episode); -0.75 (later episode) | 180 d |
| TV `abandoned` later | -0.5 | 180 d |
| `thumbs_up` / `thumbs_down` (latest wins) | +4 / -5 | 720 d |
| `not_interested` (cleared by a later thumbs up, library add, play or completion) | -2 | 720 d |
| `added_to_library` / `removed` (latest wins) | +1.5 / -0.5 | 180 d |
| Stremio library item, watched movie, watched series | +1, +2, +1 to +2 | 365 d |
| Netflix movie: 60+ min, partial, under 15 min, date-only | +2, +0.5, -1.5, +1 | 540 d |
| Netflix series: 1 + 0.75 x log2(days watched), at most 4; sampled once | up to +4; -0.5 | 540 d |
| Netflix rewatch (days 30+ apart) | +1.5 | 540 d |
| Netflix thumbs 1/2/3, stars 1-5 | -4/+3/+5, -4 to +5 | 720 d |

Netflix signals are multiplied by a bootstrap factor, `max(0.2, 1 - in-app titles / 60)`. In-app titles are titles with TV feedback or Stremio watch activity.

Affinities are weighted sums over each title's metadata. Genres split the title weight evenly. People count directors (1.0), writers (0.6) and the first four cast members (0.5). Country uses the first production country. Era is the decade. Type is movie or series. Keywords exist only when TMDB is configured. Genre affinity is divided by `sqrt(5 + how common the genre is in the candidate pool)`, so Drama does not win by being everywhere. Each family is then scaled to [-1, 1]. People and countries come from Cinemeta metadata for the 150 strongest titles (cached two weeks), because the IMDb datasets we download have no principals or language.

Hard exclusions from discovery rows are watched titles (including abandoned movies), `not_interested`, the latest opinion being negative, and disliked franchises. A franchise is disliked when one of its titles has a negative opinion and the franchise's summed weight is at most -1.5. Franchise keys drop subtitles, sequel numbers and a leading article, so "Iron Tide" blocks "Iron Tide 2".

## Candidate sources

All sources work without keys except TMDB and Trakt, which stay disabled until a key is set.

| Source | What it adds | Cache |
|---|---|---|
| `cinemeta` | Popular (`top`), Featured (`imdbRating`), New (`year` for this year and last), and `top` for the user's three strongest genres, for movies and series | 12 h |
| `catalogAddons` | Every browsable catalog (movie/series, no required extras) of the add-ons listed in `sources.catalogAddons.manifests` | 12 h |
| `imdb` | Releases from the last two years with at least 1,500 votes and rating 6.3 in the local datasets | none (local) |
| `newEpisodes` | Cinemeta `last-videos` for followed shows; episodes released in the last 45 days after the user's last activity and furthest episode | 6 h |
| `tmdb` | Trending, now playing, on the air, discover (recent, original language en/he), and recommendations/similar for the user's top titles | 24 h lists, 90 d details |
| `trakt` | Trending, anticipated, and related titles for the user's top titles | 24 h |

To add a source, subclass `sources.base.Source`, implement `collect(ctx, report)` returning `Candidate` objects, and add it to `ALL_SOURCES`.

## Filters

`Ranker.filter` returns the first reason that applies. The report counts each reason.

- `no_name`: a source returned a title without a name.
- Exclusions: `watched`, `not_interested`, `disliked`, `disliked_franchise`.
- `blocked_genre`: Talk-Show, News, Game-Show, Adult.
- `unreleased`, `no_release_info`, and `release_window`: older than the widest row window (5 years, or 12 for titles linked to a seed).
- Quality, scaled by age. A title released today needs rating 6.0 and 100 votes. Both rise linearly over a year to 6.4 and 3,000 votes for movies (1,500 for series). TMDB/Trakt vote counts use a tenth of that. An unrated title passes only within 45 days of release and only when a trusted catalog, a seed link or two sources vouch for it (`unrated` otherwise).
- Language. The original language comes from TMDB/Trakt when known, else the first production country (English-speaking countries count as English), else unknown. Anime is genre Anime, or Japan plus Animation, as in parity F11. Foreign originals follow `filters.language.foreign`: `dub` (default), `preferred_subs`, `dub_or_preferred_subs` or `exclude` (the preferred language is `filters.language.preferred`, default `en`). Foreign titles also lose 0.08 from their score unless the user has a positive affinity for that country.

## Scoring and rows

```
score = 0.45 affinity + 0.25 quality + 0.12 freshness + 0.10 sources + 0.08 popularity
```

Quality is `(rating - 5.5) / 4` times a vote-confidence factor. Freshness halves every 120 days from the release date, series premiere or newest season start. The sources term rewards independent sources, trusted catalogs and seed links. Popularity is the best catalog rank.

Rows fill in this order, and a title appears in one row only:

1. New episodes of your shows: newest release first. The reason reads "New season · you finished S2", "New episode · S2E5" or "3 new episodes · up to S4E3". The item's `videoId` is the next unwatched episode, which can be older than the window when the user is behind.
2. New for you: released (or a new season started) in the last 180 days.
3. Because you watched X: up to four rows. Seeds are the strongest positive titles; a seed whose genres overlap an earlier seed by two thirds moves to the back of the list. TMDB, Trakt and the row builder use the same list. A candidate links to a seed through TMDB/Trakt related lists, or without keys through shared directors/writers/cast, genre overlap of at least two thirds, same country and same type. A row needs at least four titles; the builder moves on to the next seed otherwise.
4. Top picks: the rest within five years, with positive affinity.

Each row uses a maximal-marginal-relevance pick (lambda 0.75) with hard caps: a dominant genre may fill at most 40% of a row and each franchise one slot.

## Playability

A title is shown only when a stream add-on (Torrentio by default) lists at least one stream that passes the TV rules, ported from `core/Addons/StreamRanking.cs`:

- no external links, cam/screener releases, AV1, Dolby Vision without HDR10, DTS or TrueHD;
- at most 6.5 GB (unknown size passes, as on the TV) and at least 3 seeders when the count is known;
- language: general titles need a stream that is not foreign-audio-only (parity F6 tier 0 or above); anime needs Japanese audio, dual audio or English subtitles and no English-dub-only (F7); foreign originals need English or multi audio (tier 1+) or subtitles in the preferred language on OpenSubtitles v3, per the configured mode.

Series are checked on the first new episode or on S1E1. Checks run lazily, only for a title about to enter a row, at most 200 per run, 1.5 s apart. Verdicts are cached in `local-data/feed-cache/playability.json` (72 h for a pass, 24 h for a fail). A network error counts as `unknown`: the title is shown with `"playability": "unknown"` and nothing is cached. A title skipped because the budget ran out is left out. After three failed requests in a row (timeouts, 429 or 5xx), the HTTP client stops calling that host for the rest of the run and answers from the cache when it can, so a dead Torrentio or Cinemeta costs minutes, not hours.

## Snapshot

```json
{
  "version": 1,
  "generatedAt": "2026-11-18T04:00:00Z",
  "profile": "Profile name",
  "rows": [
    {"id": "new-episodes", "title": "New episodes of your shows", "kind": "new-episodes",
     "items": [{"id": "tt9100001", "type": "series", "name": "Night Harbor", "poster": "https://...",
                "reason": "New season · you finished S2", "score": 0.62, "year": 2023, "genres": ["Crime"],
                "videoId": "tt9100001:3:1", "released": "2026-11-17", "playability": "playable"}]},
    {"id": "because-tt9200002", "title": "Because you watched Paper Kings", "kind": "because", "seed": "tt9200002", "items": []}
  ],
  "report": {"rows": {}, "totalItems": 0, "candidates": 0, "eligible": 0, "filtered": {}, "unplayable": {},
             "languages": {}, "sources": {}, "playability": {}, "needsCuration": false}
}
```

Row ids are `new-episodes`, `new-for-you`, `because-<seed id>` and `top-picks`. Empty rows are left out of `rows` and listed as 0 in `report.rows`. `schema.validate` checks the shape before the file is written.

## Fallback curation

When the feed falls below `fallback.minTotalItems` (30), a row in `fallback.minRowItems` is short, or there are fewer than `fallback.minBecauseRows` (2) because-rows, the build writes `local-data/feed-needs-curation.json` with the gaps, a profile summary, the best rejected candidates with reasons (unplayable titles and near misses such as `few_votes` or `unrated`, never the user's exclusions), and the rules a suggestion must meet. A later healthy run deletes the file. If `fallback.command` is set (an argv list, off by default) it runs once with `FEED_CURATION_REQUEST` pointing at the request.

## Configuration and keys

Copy `mac/feed/config.example.json` to `local-data/feed-config.json` and keep only what you change. A `--config` path given on the command line must exist; the default file is optional. Defaults and every option are in `mac/feed/config.py`. Keys can also come from the environment, which keeps them off disk:

- TMDB: `RU7100_TMDB_API_KEY` (v3 key) or `RU7100_TMDB_READ_TOKEN` (v4 read token). Free from themoviedb.org account settings.
- Trakt: `RU7100_TRAKT_CLIENT_ID`, the client id of an app created at trakt.tv/oauth/applications.
- Stremio library: `RU7100_STREMIO_AUTH_KEY`, or `stremioLibrary.path` for an exported file.

The HTTP cache stores responses under a hash of the full URL and redacts `api_key` from the stored metadata. Never commit `local-data/`.

To add a catalog add-on, put its manifest URL in `sources.catalogAddons.manifests`. An entry can be a string or an object with `label` (shown as "New on <label>"), `trustedNew` (lets unrated new releases through) and `catalogs` (catalog ids to keep). Only catalogs with IMDb (`tt`) ids are used.

## Scheduling

`scripts/feed-nightly.sh` refreshes the IMDb datasets when they are more than a week old, pulls TV feedback with `node mcp/get-tv-feedback.mjs` (expected to print events as a JSON array or JSON lines) and merges it into the log, builds the feed, and publishes it with `node mcp/publish-recommendations.mjs <snapshot>`. Either Node CLI is skipped with a message while it does not exist. Environment overrides are listed at the top of the script. A lock directory prevents overlapping runs.

The LaunchAgent template is not installed by the repository. To install it by hand:

```sh
mkdir -p local-data/logs
sed -e "s|__PROJECT__|$PWD|g" -e "s|__PROFILE__|Profile name|g" \
  scripts/launchd/com.ru7100.feed-nightly.plist.template > ~/Library/LaunchAgents/com.ru7100.feed-nightly.plist
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.ru7100.feed-nightly.plist
```

It runs at 04:30. If the Mac is asleep then, launchd runs it after wake.

## Tests

```sh
python3 -m unittest discover mac/feed/tests
```

The tests use a synthetic title set with a fixed clock (2026-11-18) and answer every HTTP request from `mac/feed/tests/fixtures/routes.json`, so they need no network. Two Torrentio payloads are trimmed real responses for rule checks.
