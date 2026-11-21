# Parity checklist: samsung-stremio-spotify-mcp → RU7100 app

Rules extracted from `../samsung-stremio-spotify-mcp/server/` (file:line refer to that repo). Tags: **TV** must run in the TV app (works with the Mac asleep); **MAC** fine on the Mac; **CONFLICT** relies on Mac-side remux/HLS/normalization/serving and is not ported.

TV re-tuning notes (2019 Samsung RU7100, Tizen 5.0): no AV1, no Dolby Vision, no DTS / DTS-HD audio, no TrueHD. HEVC (incl. 10-bit HDR10) and H.264 are hardware-decoded; EAC3/AC3/AAC are supported. Track checks use the TV player's track list instead of ffprobe. Stop-after (H6) is new: design it.

## A. Account auth and add-on collection
- **A1 Device-link sign-in** (TV: show code/QR on screen). `stremio-core.mjs:53-82`
  - Start: `GET https://link.stremio.com/api/v2/create?type=Create` returns `{result:{code, link}}`. The code is valid about 10 minutes.
  - Complete: `GET .../read?type=Read&code=<code>`. While not yet approved, the error message matches `/invalid|expired|token|unknown/` or `authKey` is missing: keep polling. When approved it returns `{result:{authKey}}`.
  - Unwrapping at :496-501: an `error` field means failure. Take `payload.result ?? payload`; `success:false` means failure; return `outer.result || outer`.
  - Accept `authKey` or `auth_key`. Store `{authKey, linkedAt}` (config.mjs:17-20).
- **A2 Calling the API** (TV). `stremio-core.mjs:464-469`: `POST https://api.strem.io/api/<method>` with a JSON body and a 20 s timeout. Reply is `{result}` or `{error:{message}}`. HTTP not OK or an `error` field means failure.
- **A3 Auth check** (TV). `getUser` with `{authKey}`. Read `_id|id, email, avatar, dateRegistered` (:46, :503-506).
- **A4 Add-on collection** (TV). `stremio-core.mjs:34-40`
  - Request `addonCollectionGet` with `{authKey, update:true}`. Use `result.addons || result`; each item has `{transportUrl, manifest:{id,name,resources}}`.
  - Cache 60 s (:20).
  - Resource match: the entry is `"stream"` or `{name:"stream"}` (:516-519).
  - Filters:
    - Stream add-ons skip the local cast add-on: id `local.samsung-media-tv.cast-to-tv`, or a loopback URL containing `/stremio-cast/` (:661-669).
    - If no stream add-on is found, use Torrentio `https://torrentio.strem.fun/manifest.json` (:7, :131-140).
    - If no subtitle add-on is found, use `https://opensubtitles-v3.strem.io/manifest.json` (:8, :378-387).
  - Add-on base URL = `transportUrl` with `/manifest.json(?…)` removed (:156).
- **A5 Cast-to-TV add-on, cast pages and deep links** (MAC). `stremio-addon.mjs`. Not needed on the TV.
- **A6 Trakt device auth** (TV; base URL `https://api.trakt.tv`). `trakt.mjs:19-65`
  - `POST /oauth/device/code` with `{client_id}` returns `{device_code, user_code, verification_url, expires_in=600, interval=5}`.
  - `POST /oauth/device/token` with `{code, client_id, client_secret}`. Status meanings: 400 pending, 410 expired, 418 denied, 429 slow down, 200 token JSON.
  - Refresh when `(created_at + expires_in)*1000 <= now + 5 min`: `POST /oauth/token` with `{refresh_token, client_id, client_secret, redirect_uri:"urn:ietf:wg:oauth:2.0:oob", grant_type:"refresh_token"}` (:111-131).
  - Headers: `trakt-api-version: 2`, `trakt-api-key: <client_id>`, `Authorization: Bearer <token>` (:135-137).
  - Status check: `GET /users/settings`, read `user.username` and `user.name`.

## B. Catalogs, search, metadata (all TV)
- **B1 Search.** `stremio-core.mjs:84-98`
  - `GET https://v3-cinemeta.strem.io/catalog/{series|movie}/top/search=<q>.json` returns `{metas:[{id,type,name,releaseInfo,poster,posterShape}]}`.
  - `type=auto` queries series and movie.
  - Rank by `titleSimilarity` (:649-658): lowercase, non-alphanumerics become spaces. Equal = 1; either contains the other = 0.9; otherwise shared words / max word count. Tie-break: `releaseInfo` descending.
  - Limit clamped to 1-20, default 8. Cache 60 s.
- **B2 Resolve a title or id.** `stremio-core.mjs:100-112`
  - `tt\d+` ids go straight to `getMeta`, trying series then movie.
  - Otherwise search with limit 5. Return "ambiguous" with 5 candidates when there is more than one result, `top < 0.72`, and `top - second < 0.15`.
- **B3 Metadata.** `stremio-core.mjs:114-121`: `GET /meta/{type}/{id}.json` returns `{meta:{…, videos:[{id,season,episode,released,title|name}]}}`. Cache 5 min.
  - Sort videos by season, then episode, then released (:512-514).
  - "Released" means `!released || released <= now` (:123-125).
- **B4 Episode parsing.** `stremio-core.mjs:444-451`: regex `season N episode|ep M` or `sNN eNNN`. Strip the match from the query.
- **B5 HTTP retries.** `stremio-core.mjs:471-494`: 3 tries, 20 s timeout each. Retry on 429, 5xx, timeout or `TypeError`, waiting 250 ms × attempt between tries.
- **B6 Caching.** `stream-startup.mjs:32-49`: one in-flight load shared per key; failures are not cached; at most 256 entries.

## C. Library and watch state (TV)
- **C1 Reading the library.** `datastoreGet` with `{authKey, collection:"libraryItem", ids:[…], all:bool}`. The result is an array of libraryItem (`stremio-core.mjs:299-303`). Fetch everything with `ids:[], all:true`; fetch one with `ids:[id], all:false`.
- **C2 libraryItem shape** (`stremio-core.mjs:313-325`, :440-442):
  ```
  {_id, name, type, poster, posterShape:"poster", removed, temp, _ctime, _mtime (ISO),
   behaviorHints:{defaultVideoId, featuredVideoId, hasScheduledVideos},
   state:{lastWatched, timeWatched, timeOffset, overallTimeWatched, timesWatched,
          flaggedWatched, duration, video_id, watched, noNotif}}
  ```
  Times are in ms. `watched` is the serialized watched-bitfield string.
- **C3 Watched bitfield**, from the npm package `stremio-watched-bitfield` (`node_modules/stremio-watched-bitfield/lib/*.js`). Port it exactly.
  - Format: `"{lastVideoId}:{bitLength}:{base64(zlibDeflate(bytes))}"`. The id can itself contain `:`, so split from the right.
  - Bit i lives in byte i>>3 at mask `1<<(i%8)` (least significant bit first). pako deflate output is zlib-format, header included.
  - Video ids = the ids of the **sorted** `meta.videos`.
  - Resize when the stored last id has moved. Unknown id or offset < 0: start blank. Offset > 0: shift bits left by `offset = (lastLen-1) - idx(lastId)`.
  - On a parse error, fall back to blank (`stremio-core.mjs:339-352`).
- **C4 Library status** (`stremio.mjs:73-88`).
  - Series: `watching` if `timeOffset > 1000`; else `unwatched` if there is a next video; else `caught_up`.
  - Movies: `watching` if `timeOffset > 1000`, else `library`.
  - Removed items are skipped.
  - `progress = round(timeOffset/duration*1000)/10`. Notifications are on when `!noNotif`.
- **C5 Next video in library** (`stremio.mjs:1192-1199`). If the current `video_id` is unwatched, return it. Otherwise step forward with `nextReleasedVideoAfter` past watched ones. If that finds nothing, use `nextUnwatchedVideo`.
- **C6 `nextUnwatchedVideo`** (`stremio-core.mjs:354-360`): first unwatched released video, preferring season > 0 (season 0 only when there are no regular episodes).
- **C7 `nextReleasedVideoAfter`** (`stremio-core.mjs:422-438`). If current season > 0, use only season > 0 episodes. Take index + 1. If the current id isn't in the list, take the first episode later by season/episode.
- **C8 New episodes** (`stremio.mjs:439-464`).
  - Candidates: series with `!removed && !temp && !noNotif`, optional fuzzy name filter, 4 checked in parallel.
  - Baseline = `lastWatched || _ctime`. Skip videos that are watched or released on or before the baseline.
  - With `sinceLastCheck`, skip ids already in `alerts.announced`.
  - A video is reported only if streams exist and at least one survives ranking (episodic, anime check from F11).
  - Limit 20; the tool layer does not pass `limit` through (`tool-registry.mjs:325`). Sort by release date, newest first.
  - `acknowledge` writes `{announced:{[videoId]: ISO}}` to `stremio-alerts.json`. The TV app needs local storage for this.
- **C9 "New episode" label** (`stremio.mjs:1215`): `released > (lastWatched || _ctime)`.

## D. Library changes (TV)
- **D1 Write.** `datastorePut` with `{authKey, collection:"libraryItem", changes:[item]}` (`stremio-core.mjs:305-308`).
- **D2 Upsert** (`stremio-core.mjs:310-337`).
  - Fetch the existing item or create the default from C2.
  - Force `removed=false, temp=false`, then apply the change.
  - Overwrite `name/type/poster` from metadata. Keep `_ctime`; set `_mtime=now`. Merge `state` over the defaults.
- **D3 Add / remove / notifications** (`stremio.mjs:90-100`). Remove sets `removed=true`. The notifications switch sets `noNotif=!notifications`.
- **D4 Mark watched/unwatched, series** (`stremio.mjs:960-981`).
  - Flip the bit. Adjust `timesWatched` by ±1 only when the value actually changes.
  - When watched: `lastWatched = max(existing, video.released || now)`.
  - If a next episode exists: `overallTimeWatched += timeWatched; timeWatched=0; timeOffset=1; video_id=next.id`. Otherwise `timeOffset=0`.
- **D5 Mark watched/unwatched, movie.** `flaggedWatched=1/0`. `timesWatched` +1, or 0 when unmarked. `lastWatched` = now or null. `timeOffset=0`.

## E. Progress and watched sync to Stremio cloud (TV; must work with the Mac asleep)
- **E1 Playback start** (`stremio.mjs:677-689`). Upsert with:
  - `noNotif=false`, `video_id`, `timeOffset=max(0,resumeMs)`.
  - `duration` and `timeWatched` kept if it's the same video, otherwise reset to 0.
  - Then Trakt `start`.
- **E2 Periodic save** (`stremio.mjs:779`, :943-958). Every ≥60 s of playhead progress set:
  - `video_id`, `duration=durationMs`, `timeOffset=pos`, `timeWatched=max(timeWatched,pos)`.
- **E3 Completion** (`stremio.mjs:776-778`). After at least 2 credible samples (I1) and `pos/dur ≥ 0.9`:
  - `timeOffset=0` plus `applyWatched` (D4/D5).
  - Do not touch the bitfield if the episode is missing from Cinemeta (:951-954).
  - Happens once per session (`markedWatched`).
- **E4 On stop/finalize** (`stremio.mjs:983-1011`). If samples ≥ 2, duration > 0 and not yet marked: one final progress save. Then Trakt `stop` with progress 100 if marked, else the actual percentage.
- **E5 Ordering.** All history writes run one after another through `enqueueHistory` (:1013-1017), and errors are kept in `historyError`. Keep this ordering so `datastorePut` calls can't race each other.

## F. Finding and ranking sources (TV)
- **F1 Discovery** (`stremio-core.mjs:142-188`).
  - Query every stream add-on in parallel: `GET {base}/stream/{type}/{videoId}.json` returns `{streams:[{name,title,description,infoHash,fileIdx,url,sources,externalUrl,behaviorHints:{filename}}]}`.
  - 30 s overall deadline, 30 s per fetch.
  - Results stream in. The first result returns early once `viable` is true.
  - Cache 2 min, or 5 s if empty; at most 256 entries.
- **F2 Dedupe key** (`stream-startup.mjs:18-30`): `infoHash.lower + ":" + (fileIdx ?? "auto")`, otherwise `url`. Streams with no identity are dropped.
  - Note: `stream-health.mjs:85` has a different key (`fileIdx ?? 0`). Recovery uses the startup key.
- **F3 Hard filters** (`stremio-core.mjs:521-524`): drop if `externalUrl` is set, if there's neither `url` nor `infoHash`, or if the text matches `cam|hdcam|telesync|telecine|dvdscr|web screener|screener`.
  - Text searched = name + title + description + filename.
- **F4 Parsing.**
  - Seeders: `👤 N`, or `seeders?/seeds? N` (:525).
  - Size: `(💾)? N.N GB|MB`, decimal units (1e9/1e6) (:636-640).
  - Quality: `2160p|4k` → 4k; `1080p`; `720p`; else unknown (:528).
  - Codec labels (:642-647):
    - Video: `h264|x264|avc`, then `hevc|x265`, then `av1`.
    - Audio: `aac`, then `eac3|ddp`, then `flac`, then `opus`.
- **F5 Score** (`stremio-core.mjs:529-565`).
  - Base `log2(seeders+1)*12`.
  - Quality: 1080p +30. 4k +27 if seeders ≥ 20 and size ≤ 15 GB (or unknown), else +8. 720p +12. Unknown −18.
  - Source type: WEB-DL +13, else BluRay/BDRip +10.
  - Video codec: H.264 +15, else HEVC +5.
  - Audio and other:

    | Match | Points |
    |---|---|
    | AAC | +10 |
    | AV1, Dolby Vision or DV | −35 |
    | FLAC, TrueHD, DTS or Opus | −12 |
    | upscale | −16 |
    | torrent with no integer `fileIdx` | −3 |

  - Episodic only: size 0.3-4 GB +8; size > 8 GB −20.
  - Anime only:
    - Japanese audio +28, else multi-audio +14.
    - English subs +24, else multi-subs +12.
    - Japanese audio + English subs: another +30.
    - English dub only: −30.
  - **Re-tune for the TV.** These penalties reflect what the Mac→DLNA path could handle. The TV's own player may cope differently with HEVC, DV, EAC3 and DTS.
- **F6 General language tiers** (`stremio-core.mjs:607-634`).

  | Tier | Condition |
  |---|---|
  | 7 | English audio + subs in the preferred language |
  | 6 | English audio + English subs |
  | 5 | English audio + multi-subs |
  | 4 | English audio |
  | 3 | multi-audio + subs in the preferred language |
  | 2 | multi-audio + English subs |
  | 1 | multi-audio |
  | 0 | nothing detected |
  | −1 | foreign audio only |

  - English audio means English present and no foreign marker.
  - Flag emoji (:601-605): 🇬🇧/🇺🇸 count as English; any other flag counts as foreign.
  - Foreign tags: `rus|ukr|ita|ger|fre|spa|pol|tur|hin|kor|chi`, delimited.
  - The preferred subtitle language is `Languages.Subtitles` (default English, so tiers 7 and 3 are unused by default).
- **F7 Anime tiers** (`stremio-core.mjs:581-597`).

  | Tier | Condition |
  |---|---|
  | 5 | Japanese audio + English subs |
  | 4 | Japanese audio + multi-subs, or multi-audio + English subs |
  | 3 | multi-audio + multi-subs |
  | 2 | Japanese audio or English subs |
  | 1 | multi-audio or multi-subs |
  | −1 | English dub only |

- **F8 Health bands** (`stremio-core.mjs:202-223`).
  - Healthy floor = `max(50, 0.1 × best known seeders)`, where best = max over torrents with a known count.
  - Band 2 = healthy: direct URL, or seeders ≥ floor.
  - Band 1 = unknown: no seeder count.
  - Band 0 = weak.
  - Anime without original audio is always band 0.
- **F9 Sort order** (`stremio-core.mjs:226-235`, :204-214).
  - Group key, compared descending: `[tier<0 ? 0 : 1, healthBand, tier]`. Then score descending.
  - Quality filter: exact match unless `auto`. The tool schema allows `auto|4k|1080p`; the cast add-on also allows 720p.
- **F10 6.5 GB cap and `maxSizeGb`** (`stremio-core.mjs:196-200`, :212-213).
  - Effective limit = `min(requested, 6.5)` if requested > 0, else 6.5.
  - Streams with **unknown size pass** the filter. The size is checked again after probing (`stremio.mjs:546, 587`).
  - The tool text calls `maxSizeGb` "an optional lower size limit": it only lowers the cap, never raises it (`tool-registry.mjs:154`).
- **F11 Anime detection** (`stremio.mjs:1201-1214`).
  - Anime if genre is `Anime`, or country is Japan + genre `Animation`.
  - Animation with no country or South Korea/China/Taiwan counts as anime only if any stream has Japanese audio.
  - When the early-return check (F13) runs, "ambiguous animation that isn't anime" is never viable, so it waits for all add-ons (:489-491).
- **F12 Track rules, ffprobe-based** (`media-normalizer.mjs:18-75`, :866-908).
  - The audio track must be `eng` (anime: `jpn`), or the only track when the stream label hints it. Otherwise **the source is rejected** (:39-41).
  - Subtitle preference: the preferred language, then English (anime: English only).
  - Track choice: full dialogue beats SDH, which beats forced/signs/songs; the default flag breaks ties (:872-880).
  - Language comes from the `language` tag, or the track title (`english|日本語` and other language names).
  - **Keep the rules (TV)**, but use the TV player's own track list. **CONFLICT**: ffprobe on the Mac (:188-204), plus the `needsNormalization` → MKV header patch / HLS remux path (`stremio.mjs:629-646`).
- **F13 Early-return viability** (`stremio.mjs:487-494`). Return early once an untried candidate is band 2 and top tier (general tier 7 / anime tier 5); otherwise wait for every add-on.
- **F14 External subtitles** (`stremio-core.mjs:389-420`, `stremio.mjs:1121-1135`).
  - Fetch `GET {base}/subtitles/{type}/{id}.json` (`{subtitles:[{url,lang}]}`), 15 s timeout, cache 2 min.
  - Pick the preferred language, then English (anime: English).
  - If the embedded subtitle is in the first-choice language, skip external ones.
  - If embedded is English, wait at most 1.5 s for an external one in the preferred language.
  - Clamp the subtitle offset to ±30 s (`media-normalizer.mjs:910-914`).
- **F15 Codec filter for the target** (`stremio.mjs:1099-1106`). Drop entries whose video codec isn't in `supportedVideoCodecs`; give H.264 +15 within a group. Also reject after probing if any video track's codec is unsupported (:550-551).

## G. Startup race, budgets and stall blacklist (TV)
- **G1 Attempt loop** (`stremio.mjs:508-704`).
  - At most 8 attempts per play.
  - Pool = untried candidates that aren't blacklisted, or all untried if every one is blacklisted.
  - Race group = entries in the same rank group as `pool[0]`.
  - Reserve = same validity and health, lower tier. It is a backup only, never a rival.
- **G2 Race** (`stream-startup.mjs:106-176`; call at `stremio.mjs:600`): `maxConcurrent 3, initial 2`. A new candidate is hedged in every 750 ms. The reserve joins after **10 s** with 2 extra slots. First success wins; losers are cleaned up.
- **G3 Time budgets** (`stream-startup.mjs:54-80`).
  - Prepare: 30 s, retried once after removing the engine, so a dead swarm can cost 60 s.
  - Media probe: 60 s.
  - Overall deadline: 120 s, extended +60 s on progress, hard maximum 300 s.
- **G4 Stall blacklist** (`stremio.mjs:58-60`, :582-584, :715-730). A failure after ≥10 s of preparation blacklists the infoHash for **6 h**; season packs share one hash, so later episodes are covered too. A successful probe clears it.
- **G5 Torrent URL and engine** (`stremio-core.mjs:9-15`, :237-297).
  - Path: `/{infoHash}/{fileIdx||0}?tr=...`.
  - Sources = the stream's `sources`, plus 5 default trackers if none are `tracker:`, plus `dht:<hash>`.
  - Default trackers: opentrackr:1337, stealth.si:80, torrent.eu.org:451, desync:6969, tamersunion https:443.
  - The Mac uses a local stremio-server at :11470 (`?external=1` returns 307; `/{hash}/remove`). The **TV needs its own torrent engine**. Per-hash leases stop a shared engine being reset (:62-63, :571-580).
- **G6 Caches.**
  - Probe-metadata cache: 5 min, 128 entries (:589-597). On the Mac it holds ffprobe output (CONFLICT).
  - Warm-winner cache: 5 min, 64 entries, keyed `videoId:target:quality:maxSize` (:67-68, :690-691). TV-applicable.

## H. Playback session (TV)
- **H1 Explicit season/episode** (`stremio.mjs:118-121`): plays **without confirmation**. Resume only if `state.video_id` matches and `1000 < timeOffset < 0.9 × duration`.
- **H2 Bare series title** (`stremio.mjs:122-139`).
  - Current video at `1000 < offset < 0.9 × dur`: resume it.
  - Offset ≥ 0.9 × dur: next released episode.
  - Otherwise: next library video.
  - Nothing left: `caught_up` plus the next scheduled episode (first with `released > now`).
  - Without `confirmed`, return `confirmation_required` with a proposal: `"Resume SxEy at m:ss?"` or `"Play SxEy (new episode)?"`.
- **H3 Movies** (`stremio.mjs:141-145`): always need confirmation (`"Play {name}?"`). Resume if `1000 < offset < 0.9 × dur`.
- **H4 Modes** (`stremio.mjs:146`, :250-270): `single | binge | queue`.
  - `enable_binge`: attach to the queue if the current item matches it; otherwise set binge.
  - `disable_binge` in queue mode: pause the queue.
- **H5 Binge advance** (`stremio.mjs:868-882`): next released episode, keeping quality, maxSize and target. No next episode ends the session as `binge_complete`.
- **H6 Stop-after:** not implemented anywhere (searched the whole repo).
- **H7 Superseding requests** (`stream-startup.mjs:82-104`). A user action increments a revision and cancels in-flight preparations. Commits run one at a time and recheck `current()`.
- **H8 Session record** (`stremio.mjs:661-672`): `{metaId, videoId, season, episode, mode, quality, maxSizeGb, sourceIdentity, sourceSizeBytes, resumeMs, lastPositionMs, durationMs, lastSavedMs, credibleSamples, markedWatched, traktLastSentMs, recovery:{attempted, switches}}`.

## I. Monitoring, completion, failover (TV; the TV app reads its own player instead of DLNA polling)
- **I1 Monitor every 5 s** (`stremio.mjs:739-809`).
  - A sample is credible when duration > 0, position ≥ 0, duration within 5 s of the known duration, and position not more than 3 s behind the last.
  - Trakt `start` is re-sent every 60 s of progress (:780-783).
- **I2 Advancing in binge/queue** (`stremio.mjs:785`): once marked watched and (ratio ≥ 0.98, or ≤ 30 s left, or player STOPPED).
- **I3 Player STOPPED** (`stremio.mjs:787-792`, :1220).
  - Not watched, not user-paused, and a starved stop: recover.
  - Queue: advance if watched, else finalize.
  - Binge: ignore. Single: finalize.
- **I4 TV off or unreachable** (`stremio.mjs:754-761`): MAC-only (the TV app is the device). Off ends the session; unreachable for ≥30 s ends it.
- **I5 Health model** (`stream-health.mjs:1-42`).
  - Window 120 s.
  - Reset on pause/stop, a seek (−3 s or more than elapsed + 15 s), or a gap over 30 s.
  - Moving = advanced ≥ `min(2 s, 0.5 × gap)`.
  - Stalled = no progress for 30 s.
  - Intermittent = elapsed ≥ 90 s and playback ratio < 0.65.
  - Grace = startup under 120 s, or bad for under 90 s.
  - "Improving" = throughput above `max(16 KB/s, 1.25 × previous)`; waits up to 60 s, capped at 300 s bad.
  - Otherwise: **replace** the source.
  - Ignored within 30 s of the end (`stremio.mjs:815-816`).
- **I6 Throughput sample** (`stream-health.mjs:54-83`): every 30 s while bad, a 128 KB HTTP range read at the playhead's byte offset, 5 s timeout. On the TV, read local engine stats instead.
- **I7 Starved stop** (`stream-health.mjs:46-50`): playhead frozen ≥10 s and more than 30 s from the end.
- **I8 Replacing a source** (`stremio.mjs:839-866`, `stream-health.mjs:89-95`).
  - At most **3 switches**, with a 5 min cooldown.
  - Excludes previously tried sources, resumes at `lastPositionMs`, keeps mode, quality, size and target.
  - If replacement fails after a starved stop, the session ends as `stream_failed`.
- **I9 Pause, resume, seek on normalized streams.** CONFLICT: the HLS restart/pause and patched-MKV resume code (`stremio.mjs:273-428`, :1067-1095). Native seeking on the TV makes all of it unnecessary.
- **I10 Restore after restart** (`stremio.mjs:1222-1265`). TV equivalent: save the session locally and resume on app launch.

## J. Queue (TV; stored locally in `stremio-queue.json`)
- **J1 Shape** (`stremio.mjs:228-236`, :1054):
  ```
  {id:"queue:<ts>", status, currentIndex, quality, maxSizeGb(effective), createdAt, completedAt,
   lastError, lastStopReason, items:[{metaId, mediaType, name, videoId, season, episode, title}]}
  ```
- **J2 Building the queue** (`stremio.mjs:205-226`). Series only; ambiguous titles return `{status:"ambiguous", itemIndex}`. If no season/episode is given, use the next library video, or error.
- **J3 Status flow.**
  - `set` → ready. `start` → playing.
  - A failed start → error with `lastError` (:904-906).
  - After the last item → completed (:921-926).
  - `skip` ends the current item as `queue_skip` and advances (:194-202). `clear` → null, and the session falls back to single mode.
  - Finalize with a queue session sets the queue to `stopped` (user_stop) or `interrupted` (other reasons). Exceptions that leave it untouched: `queue_skip, stream_recovery, episode_advance, queue_completed` (:1002-1009).
  - Starting a queue item uses no target, so the default target applies (:900).

## K. Trakt scrobbling (TV)
- **K1 Endpoint.** `POST /scrobble/{start|pause|stop}` (`trakt.mjs:67-74`). Skipped when there is no token.
- **K2 Episode lookup.** `GET /shows/{imdbId}/seasons/{s}/episodes/{e}?extended=full`, cached forever in memory (:76-87).
- **K3 Payload** (`trakt.mjs:89-105`):
  ```
  {progress: round2(clamp0..100), app_version:"0.2.1", app_date:"YYYY-MM-DD",
   show:{title, ids:{imdb}}, episode:{season, number, title?, ids?}}
  ```
  Movies send `movie:{title, ids:{imdb}}` instead.
  - If the episode lookup failed, send `{season, number}` from the session.
- **K4 Triggers** (`stremio.mjs:298-300`, :687, :781, :991, :1039-1041).
  - `start` at play and resume, then every 60 s.
  - `pause` on pause.
  - `stop` at finalize, with 100 if marked watched.
  - `progress = pos/dur × 100`.
- **K5 Dispatch.** `playback-events.mjs` passes events to registered handlers (Trakt only, `index.mjs:23`). Errors go to `session.traktError`.

## L. Mac-only or conflicting pieces (don't port)
- **CONFLICT:**
  - All of `media-normalizer.mjs`: ffprobe/ffmpeg remux, HLS (4 s segments, 8 s startup buffer), MKV header patching, subtitle sanitizing and serving (gateway port 4513).
  - DLNA casting with LAN rewriting of localhost URLs (`stremio-cast.mjs`).
  - The local stremio-server engine at :11470 (`stremio-core.mjs:283-297`).
- **MAC:** the cast add-on and deep links (`stremio-addon.mjs`), sleep prevention, TV power and wake (`stremio.mjs:732-737`), the MCP tool layer (`tool-registry.mjs`), `media_status`.