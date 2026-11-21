#!/usr/bin/env bash
# Compare managed C# uTP implementations against libutp's ucat under identical conditions (docs/utp-comparison.md).
# Not part of `dotnet test`. Each candidate is driven by bench/UtpCompare/<Candidate> through the same driver
# (bench/UtpCompare/Shared/Driver.cs): transfers end by byte count, not FIN, and the sender waits for its data to be
# acknowledged before closing.
#
#   scripts/utp-compare.sh /path/to/libutp /path/to/candidate-clones [bytes]
#
# candidate-clones holds monotorrent/, PeerSharp/ and bzTorrent/ checkouts (outside this repository).
# Env: SPECS (two-way cases, see below), CANDIDATES (default "ours mt peersharp bz"), REPS (default 1), CASES (default "two-way recv"),
#      BENCH_MB (default 300: one-way clean receive), DL_LOSS_MB (default 16: one-way receive, 3% loss + 20 ms jitter).
# Ports 19610-19611 (the older uTP scripts use 19100-19400).
set -uo pipefail

LIBUTP=${1:?usage: scripts/utp-compare.sh /path/to/libutp /path/to/candidate-clones [bytes]}
CAND=${2:?usage: scripts/utp-compare.sh /path/to/libutp /path/to/candidate-clones [bytes]}
BYTES=${3:-8000000}
CANDIDATES=${CANDIDATES:-ours mt peersharp bz}
REPS=${REPS:-1}
CASES=${CASES:-two-way recv}
BENCH_MB=${BENCH_MB:-300}
DL_LOSS_MB=${DL_LOSS_MB:-16}
# Two-way cases, one per line: label,we-connect|they-connect,loss,jitterMs
SPECS=${SPECS:-"clean,we-connect,0,0
clean,they-connect,0,0
3% loss + 20 ms jitter,we-connect,0.03,20
3% loss + 20 ms jitter,they-connect,0.03,20
10% loss + 40 ms jitter,we-connect,0.10,40
10% loss + 40 ms jitter,they-connect,0.10,40"}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
WORK=$(mktemp -d "${TMPDIR:-/tmp}/utp-compare.XXXXXX")
trap 'kill $(jobs -p) 2>/dev/null || true; [ -n "${KEEP:-}" ] || rm -rf "$WORK"' EXIT
export NUGET_PACKAGES=${NUGET_PACKAGES:-$ROOT/artifacts/nuget} DOTNET_CLI_HOME=${DOTNET_CLI_HOME:-/tmp/ru7100-dotnet-home}
PT=19611 PR=19610

# ucat, patched as in scripts/utp-interop.sh so an accepting ucat also writes.
[ -f "$LIBUTP/libutp.a" ] || make -C "$LIBUTP" >/dev/null
perl -pe 's/^\tutp_check_timeouts\(ctx\);$/\tif (s && buf_len) write_data(); \/\* interop: retry \*\/\n\tutp_check_timeouts(ctx);/' "$LIBUTP/ucat.c" > "$WORK/ucat.c"
cc -DPOSIX -O2 -w -I"$LIBUTP" -c -o "$WORK/ucat.o" "$WORK/ucat.c"
c++ -o "$WORK/ucat" "$WORK/ucat.o" "$LIBUTP/libutp.a"
UCAT="$WORK/ucat"

spec() {   # name -> "project props dll" (macOS bash 3.2: no associative arrays)
  case $1 in
    ours) echo "Ours/Ours.csproj - UtpCompare.Ours.dll" ;;
    mt) echo "MtMain/MtMain.csproj -p:MonoTorrentSrc=$CAND/monotorrent/src Benchmark.dll" ;;
    peersharp) echo "PeerSharp/PeerSharp.Compare.csproj -p:PeerSharpSrc=$CAND/PeerSharp/src PeerSharp.Benchmarks.dll" ;;
    bz) echo "BzTorrent/BzTorrent.Compare.csproj -p:BzTorrentSrc=$CAND/bzTorrent UtpCompare.BzTorrent.dll" ;;
    *) return 1 ;;
  esac
}
dll() { local p; read -r _ _ p <<< "$(spec "$1")"; echo "$WORK/bin/$1/$p"; }
for c in ours $CANDIDATES; do
  [ -f "$(dll "$c")" ] && continue
  read -r proj props _ <<< "$(spec "$c")" || { echo "unknown candidate $c"; exit 1; }
  [ "$props" = - ] && props=""
  dotnet build "$ROOT/bench/UtpCompare/$proj" -c Release $props -o "$WORK/bin/$c" -nologo -v q >/dev/null || { echo "BUILD FAILED $c"; exit 1; }
done
RELAY="dotnet $(dll ours)"
limit() { perl -e 'alarm shift; exec @ARGV' "$@"; }   # limit <seconds> cmd...

head -c "$BYTES" /dev/urandom > "$WORK/ours.bin"
head -c "$BYTES" /dev/urandom > "$WORK/theirs.bin"
sum() { shasum -a 256 "$1" | cut -c1-16; }
size() { stat -f %z "$1" 2>/dev/null || echo 0; }

# ucat_start <ucat args...>: ucat reads a FIFO that stays open after the file, so it never half-closes.
ucat_start() {   # feedFile args...
  local feed=$1; shift
  rm -f "$WORK/fifo"; mkfifo "$WORK/fifo"
  "$UCAT" "$@" < "$WORK/fifo" > "$WORK/ucat-got.bin" 2>"$WORK/ucat.log" & UCAT_PID=$!
  exec 3>"$WORK/fifo"
  cat "$feed" >&3 & FEED_PID=$!
}
ucat_stop() {   # expectedBytes
  local i
  for i in $(seq 1 300); do [ "$(size "$WORK/ucat-got.bin")" -ge "$1" ] && break; kill -0 $UCAT_PID 2>/dev/null || break; sleep 0.1; done
  kill $FEED_PID 2>/dev/null; exec 3>&-
  kill $UCAT_PID 2>/dev/null; wait $UCAT_PID 2>/dev/null; wait $FEED_PID 2>/dev/null
}
relay_start() {   # loss jitter
  if [ "$1" != 0 ] || [ "$2" != 0 ]; then
    $RELAY relay $PR $PT "$1" "$2" 600 "$WORK/relay.txt" 2>/dev/null & RELAY_PID=$!; sleep 0.7; TARGET=$PR
  else RELAY_PID=""; TARGET=$PT; : > "$WORK/relay.txt"; fi
}
relay_stop() { [ -n "$RELAY_PID" ] && { sleep 0.6; kill $RELAY_PID 2>/dev/null; wait $RELAY_PID 2>/dev/null; }; cat "$WORK/relay.txt" 2>/dev/null | tr -d '\n'; }

two_way() {   # cand case direction loss jitter
  local cand=$1 name=$2 direction=$3 drv="dotnet $(dll $1)" out pass=1 rs
  relay_start "$4" "$5"
  rm -f "$WORK/we-got.bin" "$WORK/ucat-got.bin"
  if [ "$direction" = we-connect ]; then
    ucat_start "$WORK/theirs.bin" -l -p $PT; sleep 0.4
    out=$(limit 240 $drv cat connect 127.0.0.1:$TARGET "$WORK/ours.bin" "$BYTES" "$WORK/we-got.bin" 2>&1 | grep RESULT)
  else
    limit 240 $drv cat listen $PT "$WORK/ours.bin" "$BYTES" "$WORK/we-got.bin" > "$WORK/drv.out" 2>&1 & local d=$!
    sleep 1.5
    ucat_start "$WORK/theirs.bin" 127.0.0.1 $TARGET
    wait $d; out=$(grep RESULT "$WORK/drv.out")
  fi
  ucat_stop "$BYTES"
  rs=$(relay_stop)
  [ "$(sum "$WORK/ours.bin")" = "$(sum "$WORK/ucat-got.bin")" ] || pass=0
  [ -f "$WORK/we-got.bin" ] && [ "$(sum "$WORK/theirs.bin")" = "$(sum "$WORK/we-got.bin")" ] || pass=0
  echo "CASE cand=$cand case=\"$name\" pass=$pass ucat_got=$(size "$WORK/ucat-got.bin") | ${out:-RESULT none: $(tail -2 "$WORK/drv.out" 2>/dev/null | tr '\n' ' ')} | $rs"
}

one_way_recv() {   # cand case megabytes loss jitter: ucat sends, the candidate only receives (the TV's download path)
  local cand=$1 name=$2 bytes=$(($3 * 1000 * 1000)) drv="dotnet $(dll $1)" out rs
  [ -f "$WORK/big-$3.bin" ] || head -c $bytes /dev/urandom > "$WORK/big-$3.bin"
  relay_start "$4" "$5"
  limit 300 $drv cat listen $PT - "$bytes" /dev/null > "$WORK/drv.out" 2>&1 & local d=$!
  sleep 1.5
  rm -f "$WORK/ucat-got.bin"
  ucat_start "$WORK/big-$3.bin" 127.0.0.1 $TARGET
  wait $d; out=$(grep RESULT "$WORK/drv.out")
  ucat_stop 0
  rs=$(relay_stop)
  local want=$(sum "$WORK/big-$3.bin")
  echo "CASE cand=$cand case=\"$name\" expect_sha=$want | ${out:-RESULT none: $(tail -2 "$WORK/drv.out" | tr '\n' ' ')} | $rs"
}

for rep in $(seq 1 "$REPS"); do
  for kind in $CASES; do
    if [ "$kind" = two-way ]; then
      while IFS=, read -r -u 4 label direction loss jitter; do
        [ -n "$label" ] || continue
        for cand in $CANDIDATES; do two_way "$cand" "$direction, $label" "$direction" "$loss" "$jitter"; done
      done 4<<< "$SPECS"
    elif [ "$kind" = recv ]; then
      for cand in $CANDIDATES; do one_way_recv "$cand" "recv ${BENCH_MB} MB clean" "$BENCH_MB" 0 0; done
      for cand in $CANDIDATES; do one_way_recv "$cand" "recv ${DL_LOSS_MB} MB 3% loss + 20 ms jitter" "$DL_LOSS_MB" 0.03 20; done
    fi
  done
done
