#!/usr/bin/env bash
# MonoTorrent main's engine (uTP only) against uTP-only libtorrent peers on 127.0.0.1, the same two steps as
# scripts/utp-engine-check.sh runs for our engine: download from a libtorrent seed (SHA-256 checked), then a libtorrent
# leecher downloads the file back over incoming uTP. Not part of `dotnet test`. Needs libtorrent's Python bindings.
#
#   scripts/utp-compare-engine.sh /path/to/monotorrent/src [megabytes]
# Ports 19620 (seed) and 19621 (engine).
set -uo pipefail

MTSRC=${1:?usage: scripts/utp-compare-engine.sh /path/to/monotorrent/src [megabytes]}
MB=${2:-24}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d "${TMPDIR:-/tmp}/utp-compare-engine.XXXXXX")
trap 'kill $(jobs -p) 2>/dev/null || true; rm -rf "$WORK"' EXIT
PY=${PYTHON:-$(ls /opt/homebrew/bin/python3.1[0-9] 2>/dev/null | sort -V | tail -1)}
SEED_PORT=19620
LISTEN_PORT=19621
export NUGET_PACKAGES=${NUGET_PACKAGES:-$ROOT/artifacts/nuget} DOTNET_CLI_HOME=${DOTNET_CLI_HOME:-/tmp/ru7100-dotnet-home}
dotnet build "$ROOT/bench/UtpCompare/MtEngine/MtEngine.csproj" -c Release -p:MonoTorrentSrc="$MTSRC" -o "$WORK/bin" -nologo -v q >/dev/null || exit 1

HASH=$("$PY" "$ROOT/scripts/utp-libtorrent.py" make "$WORK/seed" "$MB")
SHA=$(shasum -a 256 "$WORK/seed/sample.mkv" | cut -d' ' -f1)
echo "torrent $HASH, $MB MB"
"$PY" "$ROOT/scripts/utp-libtorrent.py" seed "$WORK/seed" $SEED_PORT > "$WORK/seed.log" 2>&1 &
sleep 1

status=0
dotnet "$WORK/bin/UtpCompare.MtEngine.dll" "$WORK/seed/sample.torrent" $SEED_PORT $LISTEN_PORT "$SHA" "$WORK/engine" 150 \
  > "$WORK/engine.out" 2> "$WORK/engine.err" &
engine=$!
for _ in $(seq 1 300); do grep -qE "ENGINE-(READY|FAIL)" "$WORK/engine.out" 2>/dev/null && break; kill -0 $engine 2>/dev/null || break; sleep 0.5; done
if grep -q ENGINE-READY "$WORK/engine.out"; then
  echo "PASS download over uTP from libtorrent ($(grep ENGINE-READY "$WORK/engine.out"))"
  if "$PY" "$ROOT/scripts/utp-libtorrent.py" leech "$WORK/leech" "$WORK/seed/sample.torrent" 127.0.0.1:$LISTEN_PORT > "$WORK/leech.log" 2>&1; then
    echo "PASS upload over incoming uTP to libtorrent ($(grep 'complete in' "$WORK/leech.log" | sed 's/leech: //'))"
  else
    echo "FAIL upload: $(tail -1 "$WORK/leech.log")"; status=1
  fi
else
  echo "FAIL download: $(cat "$WORK/engine.out") $(tail -3 "$WORK/engine.err")"; status=1
fi
kill $engine 2>/dev/null
echo "seed side: $(grep 'seed: progress' "$WORK/seed.log" | tail -1 | sed 's/seed: //')"
exit $status
