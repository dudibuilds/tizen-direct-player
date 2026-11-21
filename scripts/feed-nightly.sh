#!/usr/bin/env bash
# Nightly recommendation feed: refresh IMDb datasets weekly, pull TV feedback, build the
# rule-based feed, then publish it to the TV. Safe to run by hand; launchd runs it via
# scripts/launchd/com.ru7100.feed-nightly.plist.template (see docs/recommendations.md).
#
# Environment:
#   RU7100_FEED_PROFILE       Netflix profile name (falls back to RU7100_NETFLIX_PROFILE). Required.
#   RU7100_DATA_DIR           private data folder (default <project>/local-data)
#   RU7100_CATALOG_DIR        IMDb datasets folder (default $RU7100_DATA_DIR/imdb)
#   RU7100_FEED_OUTPUT        snapshot path (default $RU7100_DATA_DIR/recommendation-feed.json)
#   RU7100_IMDB_MAX_AGE_DAYS  refresh IMDb datasets when older than this (default 7)
#   RU7100_SKIP_IMDB_REFRESH  set to 1 to never download IMDb datasets
#   RU7100_FEEDBACK_CLI       TV feedback puller (default mcp/tv-feedback.mjs; prints events as JSON)
#   RU7100_PUBLISH_CLI        snapshot publisher (default mcp/publish-recommendations.mjs <snapshot>)
#   RU7100_FEED_ARGS          extra arguments for `python3 -m mac.feed build`
#   RU7100_PYTHON, RU7100_NODE  interpreters (default python3, node)
set -euo pipefail
umask 077

project="$(cd "$(dirname "$0")/.." && pwd)"
data="${RU7100_DATA_DIR:-$project/local-data}"
profile="${RU7100_FEED_PROFILE:-${RU7100_NETFLIX_PROFILE:-}}"
python="${RU7100_PYTHON:-python3}"
node="${RU7100_NODE:-node}"
snapshot="${RU7100_FEED_OUTPUT:-$data/recommendation-feed.json}"
imdb_dir="${RU7100_CATALOG_DIR:-$data/imdb}"
imdb_max_age_days="${RU7100_IMDB_MAX_AGE_DAYS:-7}"
feedback_cli="${RU7100_FEEDBACK_CLI:-$project/mcp/tv-feedback.mjs}"
publish_cli="${RU7100_PUBLISH_CLI:-$project/mcp/publish-recommendations.mjs}"

log() { printf '%s feed-nightly: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

if [[ -z "$profile" ]]; then
  log "Set RU7100_FEED_PROFILE (or RU7100_NETFLIX_PROFILE) to the imported Netflix profile name."
  exit 2
fi
mkdir -p "$data"
export RU7100_DATA_DIR="$data"

# One run at a time; a lock older than six hours is left over from a crash.
lock="$data/.feed-nightly.lock"
if [[ -d "$lock" ]] && [[ -n "$(find "$lock" -maxdepth 0 -mmin +360 2>/dev/null)" ]]; then
  rmdir "$lock" 2>/dev/null || true
fi
if ! mkdir "$lock" 2>/dev/null; then
  log "Another feed run is in progress ($lock)."
  exit 0
fi
pulled=""
cleanup() {
  [[ -n "$pulled" ]] && rm -f "$pulled"
  rmdir "$lock" 2>/dev/null || true
}
trap cleanup EXIT

# 1. IMDb datasets, at most weekly (the download is several hundred MB).
stamp="$imdb_dir/.last-refresh"
if [[ "${RU7100_SKIP_IMDB_REFRESH:-0}" == "1" ]]; then
  log "IMDb refresh skipped (RU7100_SKIP_IMDB_REFRESH=1)"
elif [[ ! -f "$stamp" ]] || [[ -n "$(find "$stamp" -mtime +"$imdb_max_age_days" 2>/dev/null)" ]]; then
  log "Refreshing IMDb datasets in $imdb_dir"
  mkdir -p "$imdb_dir"
  if RU7100_CATALOG_DIR="$imdb_dir" bash "$project/scripts/refresh-imdb.sh"; then
    touch "$stamp"
  else
    log "IMDb refresh failed; continuing with the datasets already on disk"
  fi
fi

# 2. In-app feedback from the TV (merged into the local log, deduplicated).
if [[ -f "$feedback_cli" ]]; then
  pulled="$(mktemp "$data/.tv-feedback.XXXXXXXX")"
  if (cd "$project" && "$node" "$feedback_cli" > "$pulled"); then
    if (cd "$project" && "$python" -m mac.feed ingest-feedback "$pulled"); then
      # Acknowledge on the TV only after the events are safely in the local log.
      last="$("$python" -c 'import json,sys; print(json.load(open(sys.argv[1])).get("lastSequence") or "")' "$pulled" 2>/dev/null || true)"
      if [[ -n "$last" ]]; then
        (cd "$project" && "$node" "$feedback_cli" --ack-through "$last" > /dev/null) || log "Acknowledging TV feedback failed; events will be pulled again (ingest deduplicates)"
      fi
    fi
  else
    log "Pulling TV feedback failed; building from the feedback already logged"
  fi
else
  log "Skipping TV feedback pull: $feedback_cli not found"
fi

# 3. Build. The deterministic feed is written even when it asks for curation.
# shellcheck disable=SC2086 # RU7100_FEED_ARGS is intentionally word-split
(cd "$project" && "$python" -m mac.feed build --profile "$profile" --output "$snapshot" ${RU7100_FEED_ARGS:-})

# 4. Publish to the TV.
if [[ -f "$publish_cli" ]]; then
  if (cd "$project" && "$node" "$publish_cli" "$snapshot"); then
    log "Published $snapshot"
  else
    log "Publishing failed; the TV keeps its previous feed"
    exit 1
  fi
else
  log "Skipping publish: $publish_cli not found"
fi
