#!/usr/bin/env bash
# uTP interop check against libutp (github.com/bittorrent/libutp, its ucat example), both directions, on 127.0.0.1,
# clean and through a lossy / reordering UDP relay. Not part of `dotnet test`.
#
#   scripts/utp-interop.sh /path/to/libutp [bytes]
#   BENCH_MB=300 scripts/utp-interop.sh /path/to/libutp      # also: receive-path CPU per MB, uTP vs managed TCP
#
# The libutp checkout is built with `make` if ucat is missing (keep it outside this repository). Each case sends
# a random file each way at once and compares SHA-256; the harness prints retransmit / fast-resend counters.
set -euo pipefail

LIBUTP=${1:?usage: scripts/utp-interop.sh /path/to/libutp [bytes]}
BYTES=${2:-8000000}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d "${TMPDIR:-/tmp}/utp-interop.XXXXXX")
trap 'kill $(jobs -p) 2>/dev/null || true; [ -n "${KEEP:-}" ] || rm -rf "$WORK"' EXIT

[ -f "$LIBUTP/libutp.a" ] || make -C "$LIBUTP" >/dev/null
# Stock ucat never writes on an accepted socket until libutp reports it writable, which libutp does not do for a
# responder that has sent nothing yet (ucat listening to ucat has the same one-way result). This copy retries the
# write while it has buffered input, so both directions run at once. (Stock ucat also trips its own
# "utp_close on a destroyed socket" assertion at exit after a two-way transfer; that abort is after the data and
# does not affect the result.)
perl -pe 's/^\tutp_check_timeouts\(ctx\);$/\tif (s && buf_len) write_data(); \/\* interop: retry \*\/\n\tutp_check_timeouts(ctx);/' "$LIBUTP/ucat.c" > "$WORK/ucat.c"
grep -q 'interop: retry' "$WORK/ucat.c"
cc -DPOSIX -O2 -w -I"$LIBUTP" -c -o "$WORK/ucat.o" "$WORK/ucat.c"
c++ -o "$WORK/ucat" "$WORK/ucat.o" "$LIBUTP/libutp.a"
UCAT="$WORK/ucat"
export NUGET_PACKAGES=${NUGET_PACKAGES:-$ROOT/artifacts/nuget} DOTNET_CLI_HOME=${DOTNET_CLI_HOME:-/tmp/ru7100-dotnet-home}
dotnet build "$ROOT/desktop/TransportCheck.csproj" -nologo -v q >/dev/null
HARNESS="dotnet $ROOT/desktop/bin/Debug/net10.0/TransportCheck.dll"

head -c "$BYTES" /dev/urandom > "$WORK/ours.bin"
head -c "$BYTES" /dev/urandom > "$WORK/theirs.bin"
sum() { shasum -a 256 "$1" | cut -c1-16; }
failures=0

check() {   # name
  local name=$1 ok=1
  [ "$(sum "$WORK/ours.bin")" = "$(sum "$WORK/ucat-got.bin")" ] || ok=0
  [ "$(sum "$WORK/theirs.bin")" = "$(sum "$WORK/we-got.bin")" ] || ok=0
  if [ $ok = 1 ]; then echo "PASS $name ($BYTES bytes each way)"; else
    echo "FAIL $name: ucat got $(wc -c < "$WORK/ucat-got.bin") B, we got $(wc -c < "$WORK/we-got.bin") B"; failures=$((failures + 1)); fi
  rm -f "$WORK/ucat-got.bin" "$WORK/we-got.bin"
}

# relay <loss> <jitter ms>: ucat/our side talk to port 19100, which forwards to 19101
run_case() {   # name direction loss jitter
  local name=$1 direction=$2 loss=$3 jitter=$4 target=19101 relay=""
  if [ "$loss" != "0" ] || [ "$jitter" != "0" ]; then
    $HARNESS --udp-relay 19100 19101 "$loss" "$jitter" 300 2>"$WORK/relay.log" & relay=$!
    sleep 0.5
    target=19100
  fi
  if [ "$direction" = "we-connect" ]; then
    "$UCAT" -d -l -p 19101 < "$WORK/theirs.bin" > "$WORK/ucat-got.bin" 2>"$WORK/ucat.log" & local u=$!
    sleep 0.5
    $HARNESS --utp-cat connect "127.0.0.1:$target" "$WORK/ours.bin" "$WORK/we-got.bin" 2>&1 | sed 's/^/    /' || true
    wait $u || true
  else
    $HARNESS --utp-cat listen 19101 "$WORK/ours.bin" "$WORK/we-got.bin" 2>&1 | sed 's/^/    /' & local h=$!   # (background: failures show in check)
    sleep 1.5
    "$UCAT" -d 127.0.0.1 "$target" < "$WORK/theirs.bin" > "$WORK/ucat-got.bin" 2>"$WORK/ucat.log" || true
    wait $h || true
  fi
  [ -n "$relay" ] && { kill $relay 2>/dev/null || true; wait $relay 2>/dev/null || true; sed 's/^/    /' "$WORK/relay.log" | tail -1; }
  grep -E "^Error|Assertion|Warning" "$WORK/ucat.log" | sed 's/^/    ucat: /' || true
  check "$name"
}

run_case "we connect, libutp listens"            we-connect    0    0
run_case "libutp connects, we listen"            they-connect  0    0
run_case "we connect, 3% loss + 20 ms jitter"    we-connect    0.03 20
run_case "libutp connects, 3% loss + 20 ms jitter" they-connect 0.03 20
run_case "we connect, 10% loss + 40 ms jitter"   we-connect    0.10 40
run_case "libutp connects, 10% loss + 40 ms jitter" they-connect 0.10 40

# CPU per MB on the download path (what the TV does): libutp sends BENCH_MB to our listener, which only counts.
# Baseline: the same amount over loopback TCP into a managed NetworkStream reader. Release build.
if [ -n "${BENCH_MB:-}" ]; then
  BENCH="dotnet $WORK/bench/TransportCheck.dll"
  dotnet build "$ROOT/desktop/TransportCheck.csproj" -c Release -o "$WORK/bench" -nologo -v q >/dev/null
  head -c $((BENCH_MB * 1024 * 1024)) /dev/urandom > "$WORK/big.bin"
  : > "$WORK/empty.bin"
  for i in 1 2 3; do
    $BENCH --utp-cat listen 19201 "$WORK/empty.bin" /dev/null 2>&1 | grep "cpu" | sed 's/^/    /' & h=$!
    sleep 1.5
    "$UCAT" 127.0.0.1 19201 < "$WORK/big.bin" > /dev/null 2>&1 || true
    wait $h || true
    $BENCH --tcp-sink 19202 2>&1 | sed 's/^/    /' & h=$!
    sleep 1.5
    cat "$WORK/big.bin" > /dev/tcp/127.0.0.1/19202 || true
    wait $h || true
  done
fi
exit $failures
