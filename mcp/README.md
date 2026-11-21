# RU7100 agent API and MCP servers

The TV app exposes one authenticated command API (`core/Api`). The MCP servers here and the Mac scheduler
talk to it. The remote and the API drive the same player through `IPlaybackCommands`.

## Pieces

| Path | What it is |
|---|---|
| `core/Api/ApiRouter.cs` | Transport-independent router: `{command, args}` → handler, strict argument checks, per-command scopes |
| `core/Api/ApiHttpServer.cs` | `HttpListener` host: `POST /api`, `GET /api/status`. Runs in the TV app and in `host/` |
| `core/Api/ApiAuth.cs` | Keys, HMAC signatures, replay cache |
| `core/Api/PlaybackCommands.cs` | `IPlaybackCommands`, the player command interface the session engine implements |
| `core/Api/Recommendations.cs` | Recommendation snapshot, feedback events, `IRecommendationsStore` over `IKeyValueStore` |
| `host/` | Mac console host running the same API over in-memory fakes |
| `mcp/server.mjs` | TV MCP server (`stremio_*`, `app_status`, `player_controls`, `addons`, `account`, aliases) |
| `mcp/recommendation-server.mjs` | Mac recommendation MCP server, plus `publish_to_tv` and `get_tv_feedback` |
| `mcp/recommendations-feed.mjs` | Publish and feedback functions shared by the MCP tools and the CLIs |
| `mcp/publish-recommendations.mjs`, `mcp/tv-feedback.mjs` | CLIs for the scheduled recommendation job |

## Wire protocol

`POST /api` with body `{"command": "<name>", "args": {...}}`. `GET /api/status` is the same as command `status`.

- Success: HTTP 200, `{"ok": true, "result": ...}`.
- Failure: `{"ok": false, "error": {"code", "message"}}` with these statuses:

  | Status | Codes |
  |---|---|
  | 400 | `missing_argument`, `invalid_argument`, `irrelevant_argument`, `invalid_request` |
  | 401 | `unauthorized` |
  | 403 | `forbidden` (scope) |
  | 404 | `unknown_command`, `not_found` |
  | 409 | `no_active_playback`, `no_queue`, `no_active_queue`, `version_conflict`, `conflict` |
  | 413 | `too_large` (body over 256 KB) |
  | 502 | `playback_failed` (no source could be started; the message lists what was tried) |
  | 503 | `unavailable` |
  | 500 | `internal_error` |

Results are camelCase JSON with nulls kept. Player states are upper case like the old MCP (`state: "PLAYING"`); other enums are lowercase (`type: "series"`).

### Signing

Every request carries four headers:

| Header | Value |
|---|---|
| `X-Ru7100-Key` | key id |
| `X-Ru7100-Time` | unix seconds; accepted within ±60 s |
| `X-Ru7100-Nonce` | 16-64 chars of `[A-Za-z0-9_-]`; each key+nonce is accepted once |
| `X-Ru7100-Signature` | `base64(HMAC-SHA256(secret, METHOD + "\n" + PATH + "\n" + TIME + "\n" + NONCE + "\n" + hex(sha256(raw body))))` |

`PATH` is the URL path without the query, e.g. `/api`. The body hash covers the exact bytes sent; for a GET it is the hash of the empty string. The signature is compared in constant time. A nonce is recorded only after its signature verifies, in a bounded cache that drops entries older than the window.

### Keys and scopes

Keys come from a JSON file: `{"keys":[{"id":"agent","secret":"<base64, at least 32 bytes>","scopes":["playback","library"]}]}`.

| Scope | Commands |
|---|---|
| `playback` | `playback.*`, `queue.*`, plus reads: `library.search`, `library.list`, `library.new_episodes`, `addons.list` |
| `library` | `library.*` (reads and edits), `addons.list` |
| `recommendations` | `recommendations.*` only |
| `admin` | everything, including `addons.install/remove` and `account.link_begin/link_complete/sign_out` |

`status` and `account.status` accept any key. A playback-only key cannot touch recommendations, add-ons, the account or library edits, and a recommendations key cannot play anything.

### Binding

The default is `http://127.0.0.1:45551/` (loopback; the Mac reaches it through `sdb forward tcp:45551 tcp:45551`, see the root README). LAN access needs `http://+:45551/` or the TV's address plus a pairing flow. That flow is not built yet. The plan:

1. The TV shows a pairing code.
2. The Mac exchanges the code for a per-device key with chosen scopes.
3. The TV stores the key list in its app data.
4. The TV can revoke keys from its settings.

Until that exists, keep loopback binding and install keys with the app build.

## Commands

| Command | Args |
|---|---|
| `status` | `scope?: all \| stremio_playback` |
| `playback.play` | `query`, `season?`, `episode?` (both or neither), `confirmed?`, `mode?: single \| binge`, `quality?: auto \| 4k \| 1080p`, `maxSizeGb? > 0`, `positionSeconds? >= 0` |
| `playback.pause`, `playback.resume`, `playback.stop`, `playback.enable_binge`, `playback.disable_binge` | none |
| `playback.seek` | `positionSeconds` |
| `playback.stop_after_current` | `enabled?` (default true) |
| `playback.select_audio` | `index` 0-255 |
| `playback.select_subtitle` | `index` -1-255 (-1 = off) |
| `playback.subtitle_size` | `size` 24-72 |
| `playback.subtitle_offset` | `offsetMs` ±30000 |
| `queue.set` | `items: [{query, season?, episode?}]`, `quality?`, `maxSizeGb?` |
| `queue.start` | same, with items optional (none starts the saved queue) |
| `queue.status`, `queue.clear`, `queue.skip` | none |
| `library.search` | `query`, `type?: auto \| series \| movie`, `limit?` 1-20 |
| `library.list` | `query?`, `status?: all \| watching \| unwatched \| caught_up \| library` |
| `library.new_episodes` | `query?`, `sinceLastCheck?`, `acknowledge?` |
| `library.add`, `library.remove` | `queryOrId` |
| `library.set_notifications` | `queryOrId`, `notifications` |
| `library.mark_watched` | `queryOrId`, `season?`, `episode?`, `watched?` |
| `addons.list` | none |
| `addons.install` | `manifestUrl` (http(s), ends in `/manifest.json`) |
| `addons.remove` | `addonId` |
| `account.status`, `account.link_begin`, `account.sign_out` | none |
| `account.link_complete` | `code?` (defaults to the pending code) |
| `recommendations.set` | `rows`, `generatedAt?`, `source?`, `expectedVersion?` |
| `recommendations.get` | none |
| `recommendations.feedback.list` | `afterSequence?`, `limit?` 1-1000 |
| `recommendations.feedback.ack` | `sequence` |

Result shapes follow the old `samsung-stremio-spotify-mcp` tools:
- search hits: `{id,type,name,releaseInfo,poster,posterShape,matchScore}`;
- library items: `{id,name,type,state,currentVideoId,progress,nextEpisode,notifications}`;
- `new_episodes`: `{count, episodes:[{series,seriesId,video,bestStream}]}`;
- play: `status` is `ambiguous`, `confirmation_required`, `caught_up`, `starting` or `playing`; a started play also carries `series, video, resumeMs, mode, selected, fallbacksTried, warning?, state` (the old `tv.state` is now `state`);
- control commands: `{ok,action,state,positionMs,durationMs,mode,session}`;
- queue: `{ok,action,status?,itemIndex?,queue,session}`; start and skip merge the started play fields in, as the old tool did, and a skip past the end returns `status: "queue_complete"`.

To confirm playback, check that `status` (scope `stremio_playback`) returns `active: true` and that `session.positionMs` rises between two reads.

### Recommendations

`recommendations.set` replaces the whole snapshot. The payload looks like this:

```
rows: [{id: "for-you", title: "For You", items: [{id: "tt…", type: "movie"|"series", name, poster?, reason?, score?}]}]
```

Limits:
- at most 20 rows, 50 items per row and 300 items in total;
- titles up to 80 characters, names up to 200, reasons up to 140;
- row ids are lowercase slugs;
- item ids are IMDb ids, unique within a row;
- unknown fields are rejected.

The TV assigns `version` (+1 per accepted push). Pass `expectedVersion` to fail with `version_conflict` instead of overwriting a newer feed. A rejected push leaves the stored snapshot untouched. The TV also keeps the previous good snapshot and serves it if the current file cannot be read.

The TV records events with `IRecommendationsStore.RecordFeedbackAsync`. Each event has these fields:

| Field | Value |
|---|---|
| `sequence` | assigned by the TV |
| `imdbId` | IMDb id |
| `videoId?` | video id, when relevant |
| `kind` | `play_started`, `progress`, `completed`, `abandoned`, `thumbs_up`, `thumbs_down`, `not_interested`, `added_to_library` or `removed` |
| `positionMs?` | playback position |
| `durationMs?` | duration |
| `at` | time of the event |

The TV keeps up to 2000 unacknowledged events, dropping the oldest first. `feedback.list` reads after the stored ack cursor (or `afterSequence`) and returns `{events,lastSequence,ackedSequence,latestSequence,hasMore,gap}`. `gap: true` means events were dropped before they were read. `feedback.ack` moves the cursor forward, never back, and deletes acknowledged events.

## Running locally (no TV)

```sh
dotnet build host -c Release
dotnet host/bin/Release/net10.0/ru7100-host.dll --generate-keys /tmp/ru7100-keys.json
dotnet host/bin/Release/net10.0/ru7100-host.dll --keys /tmp/ru7100-keys.json --port 45561 --data /tmp/ru7100-tv-data --verbose
```

`--generate-keys` writes `admin`, `playback`, `library` and `recommendations` keys (mode 0600). Other flags:
- `--data`: persists the recommendation feed and new-episode alerts as files. Without it, they stay in memory.
- `--services real`: the TV composition (`core/App/AppHost`) with real add-ons, catalog, source ranking, Stremio account and library, over a simulated player and opener (no downloads). The automated test uses `fakes`.
- `dev.record_feedback` (admin key, host only): stands in for the TV recording an event.

The fakes have these titles:
- Breaking Bad, being watched;
- Fresh Show, with new episodes;
- Space Force and Dog Days, which together make an ambiguous "space dog" search;
- Sintel, a movie.

The fake player's position advances in real time.

Point the TV MCP server at it:

```sh
RU7100_API_URL=http://127.0.0.1:45561 RU7100_API_KEY_FILE=/tmp/ru7100-keys.json RU7100_API_KEY_ID=playback node mcp/server.mjs
```

`RU7100_API_KEY_FILE` may hold a single `{id, secret}` or a host key file (then `RU7100_API_KEY_ID` picks the entry). Alternatively set `RU7100_API_KEY_ID` and `RU7100_API_KEY_SECRET`.

### Tests

- `npm --prefix mcp ci` once, then `npm --prefix mcp test`. The test builds `host/`, starts it on a free port with temp keys and data, and runs about 25 round trips:
  - the TV MCP server: discover, play/confirm, status with rising position, controls, queue, library, add-ons, account, scope denials, aliases;
  - the recommendation server: publish, automatic publish on curate, invalid push keeps the last good feed, feedback with ack;
  - both CLIs;
  - a host restart with persisted data;
  - replay and tamper checks.

  Set `RU7100_SKIP_BUILD=1` to reuse the last build.
- `dotnet test tests/Core.Tests.csproj` runs the C# auth, routing, validation, result-shape, recommendation and HTTP tests (`tests/Api`).

## MCP tool mapping

| MCP tool | API command(s) |
|---|---|
| `stremio_discover` `search` / `library` / `new_episodes` | `library.search` / `library.list` / `library.new_episodes` |
| `stremio_library` `add` / `remove` / `set_notifications` / `mark_watched` | `library.*` |
| `stremio_playback` `play` / `pause` / `resume` / `stop` / `seek` / `enable_binge` / `disable_binge` | `playback.*` |
| `stremio_queue` `set` / `start` / `status` / `clear` / `skip` | `queue.*` |
| `app_status`, `media_status` (alias) | `status` |
| `player_controls` | `playback.select_audio`, `select_subtitle`, `subtitle_size`, `subtitle_offset`, `stop_after_current` |
| `addons` | `addons.*` |
| `account` | `account.*` |
| `get_playback_status`, `pause_playback`, `resume_playback`, `stop_playback`, `seek_playback`, `select_audio_track`, `select_subtitles`, `set_subtitle_size`, `set_subtitle_timing` | aliases of the old probe tools |

Differences from the old MCP:
- `target` is gone, because playback always runs in the TV app.
- `media_status` scopes for the TV, integrations and media targets are gone.
- `seek_to_probe_point` is gone.
- Errors keep the old payload `{tool, action, code, message}` with `isError: true`.

## Recommendation delivery (scheduled job)

The scheduled job is deterministic. It runs `mac/recommendations.py`, then:

```sh
RU7100_API_URL=… RU7100_RECOMMENDATIONS_KEY_FILE=~/.ru7100/recommendations-key.json node mcp/publish-recommendations.mjs [snapshot.json] [--expected-version N] [--dry-run]
RU7100_API_URL=… RU7100_RECOMMENDATIONS_KEY_FILE=… node mcp/tv-feedback.mjs > events.json && node mcp/tv-feedback.mjs --ack-through "$(jq .lastSequence events.json)"
```

Without a file, `publish-recommendations.mjs` reads `$RU7100_DATA_DIR/recommendations.json` and the curation file. It builds these rows:
- "For You": up to 50, curated items first, reasons from the agent note or matched genres;
- "New for you": released this year or last;
- up to two "Because you watched X" rows built from `recentlyWatched`, by shared genres.

A scheduler can import `publishSnapshot`, `buildRows` and `fetchFeedback` from `mcp/recommendations-feed.mjs` instead. The MCP tools `publish_to_tv` and `get_tv_feedback` wrap the same functions. `refresh_recommendations` and `curate_recommendations` publish automatically when a key is configured; the result's `tvPublish` field reports the outcome. The recommendation server uses only `recommendations.*` (enforced by the client and by the key's scope).
