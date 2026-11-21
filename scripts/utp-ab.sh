#!/usr/bin/env bash
# uTP A/B with the desktop start-check (real Torrentio swarms): for each source, run each configuration in turn
# (cold, fresh data directory each; the order rotates every round), pinned to one info hash, and print the RESULT /
# TRANSPORT / SUSTAIN lines.
#
#   scripts/utp-ab.sh [rounds] [sustain seconds] -- "movie <imdb id> [infohash]" "series <imdb id>:<season>:<episode>" ...
# Without an info hash the first run picks rank 0 and later runs pin its hash.
# CONFIGS (default "on off"): on = uTP with the default TCP grace, off = TCP only, graceN = uTP with an N ms TCP grace
# (grace0 = the first transport to connect wins).
# Env: RU7100_PORT (default 28111; the TV holds 27492 on the home router), plus anything StartCheck reads.
# Each run holds /tmp/ru7100-net-measure.lock (mkdir lock shared with other network measurements on this Mac), so
# two measurements never share the link.
set -uo pipefail
LOCK=/tmp/ru7100-net-measure.lock
release() { rmdir "$LOCK" 2>/dev/null || true; }
trap 'release; exit 130' INT TERM

ROUNDS=${1:-3}
SUSTAIN=${2:-15}
shift 2 || true
[ "${1:-}" = "--" ] && shift
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export NUGET_PACKAGES=${NUGET_PACKAGES:-$ROOT/artifacts/nuget} DOTNET_CLI_HOME=${DOTNET_CLI_HOME:-/tmp/ru7100-dotnet-home}
export RU7100_PORT=${RU7100_PORT:-28111} RU7100_SUSTAIN_S=$SUSTAIN
read -r -a configs <<< "${CONFIGS:-on off}"
dotnet build "$ROOT/desktop/TransportCheck.csproj" -nologo -v q >/dev/null

for source in "$@"; do
  read -r type id hash <<< "$source"
  hash=${hash:-}
  for round in $(seq 1 "$ROUNDS"); do
    count=${#configs[@]}
    for step in $(seq 0 $((count - 1))); do
      config=${configs[$(((step + round - 1) % count))]}
      case $config in
        off) utp=0 grace="" ;;
        on) utp=1 grace="" ;;
        grace*) utp=1 grace=${config#grace} ;;
      esac
      while ! mkdir "$LOCK" 2>/dev/null; do sleep 10; done
      started=$(date +%T)
      # Overlap check: any other start-check (not from this checkout) seen during the run.
      overlap=$(mktemp)
      ( while sleep 3; do ps -axo command | grep "[T]ransportCheck.dll --start" | grep -vc "$ROOT" >> "$overlap"; done ) & sampler=$!
      out=$(RU7100_UTP=$utp RU7100_UTP_GRACE_MS=$grace RU7100_HASH=$hash dotnet "$ROOT/desktop/bin/Debug/net10.0/TransportCheck.dll" --start-check "$type" "$id" 2>&1)
      release
      kill $sampler 2>/dev/null; wait $sampler 2>/dev/null
      others=$(sort -n "$overlap" | tail -1); rm -f "$overlap"
      [ -z "$hash" ] && hash=$(sed -n 's/.*hash=\([0-9a-fA-F]\{40\}\).*/\1/p' <<< "$out" | head -1)
      echo "== $type $id round $round utp=$utp grace=${grace:-default} hash=$hash started=$started ended=$(date +%T) overlap=${others:-0}"
      grep -E "^(SUSTAIN|TRANSPORT|RESULT)|Exception" <<< "$out" | cut -c1-400
      grep -E "^conn at end|^network:" <<< "$out" | cut -c1-500
    done
  done
done
