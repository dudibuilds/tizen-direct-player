#!/usr/bin/env bash
# M3 proof on the Mac: the TV's torrent engine (TorrentSourceOpener: MonoTorrent + managed uTP) against libtorrent
# peers that speak uTP only (TCP disabled), on 127.0.0.1:
#   1. download: a libtorrent seed serves a random file; we fetch metadata and every byte over uTP (SHA-256 checked)
#   2. upload: a libtorrent leecher connects IN to our listen port over uTP and downloads the file from us
# Not part of `dotnet test`. Needs libtorrent's Python bindings: brew install libtorrent-rasterbar
#
#   scripts/utp-engine-check.sh [megabytes]
#   RU7100_UTP=0 scripts/utp-engine-check.sh     # control: uTP disabled, both steps must fail
set -euo pipefail

MB=${1:-24}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d "${TMPDIR:-/tmp}/utp-engine.XXXXXX")
trap 'kill $(jobs -p) 2>/dev/null || true; rm -rf "$WORK"' EXIT
PY=${PYTHON:-$(ls /opt/homebrew/bin/python3.1[0-9] 2>/dev/null | sort -V | tail -1)}
SEED_PORT=19300
LISTEN_PORT=19400

export NUGET_PACKAGES=${NUGET_PACKAGES:-$ROOT/artifacts/nuget} DOTNET_CLI_HOME=${DOTNET_CLI_HOME:-/tmp/ru7100-dotnet-home}
dotnet build "$ROOT/desktop/TransportCheck.csproj" -nologo -v q >/dev/null

HASH=$("$PY" "$ROOT/scripts/utp-libtorrent.py" make "$WORK/seed" "$MB")
SHA=$(shasum -a 256 "$WORK/seed/sample.mkv" | cut -d' ' -f1)
echo "torrent $HASH, $MB MB"
"$PY" "$ROOT/scripts/utp-libtorrent.py" seed "$WORK/seed" $SEED_PORT > "$WORK/seed.log" 2>&1 &
sleep 1

status=0
dotnet "$ROOT/desktop/bin/Debug/net10.0/TransportCheck.dll" --utp-engine-check "$HASH" $SEED_PORT $LISTEN_PORT "$SHA" 40 \
  > "$WORK/engine.out" 2> >(sed 's/^/  /' >&2) &
engine=$!
for _ in $(seq 1 240); do grep -q ENGINE-READY "$WORK/engine.out" 2>/dev/null && break; kill -0 $engine 2>/dev/null || break; sleep 0.5; done
if grep -q ENGINE-READY "$WORK/engine.out"; then
  echo "PASS download over uTP from libtorrent"
  if "$PY" "$ROOT/scripts/utp-libtorrent.py" leech "$WORK/leech" "$WORK/seed/sample.torrent" 127.0.0.1:$LISTEN_PORT > "$WORK/leech.log" 2>&1; then
    echo "PASS upload over incoming uTP to libtorrent ($(grep 'complete in' "$WORK/leech.log" | sed 's/leech: //'))"
  else
    echo "FAIL upload: $(tail -1 "$WORK/leech.log")"; status=1
  fi
else
  echo "FAIL download"; status=1
fi
wait $engine || status=1
echo "seed side: $(grep 'seed: progress' "$WORK/seed.log" | tail -1 | sed 's/seed: //')"
exit $status
