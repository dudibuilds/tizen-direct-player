#!/bin/sh
# Starts the browser preview, drives it with remote keys over HTTP and checks that
# center during playback opens the overlay, Back closes it, and Back twice leaves playback.
# Usage: tests/Ui/preview-smoke.sh [port]
set -eu
port="${1:-5199}"
base="http://127.0.0.1:$port"
root="$(cd "$(dirname "$0")/../.." && pwd)"

dotnet build "$root/preview/Preview.csproj" -nologo -v quiet >/dev/null
dotnet run --project "$root/preview" --no-build -- --port "$port" --fake >/dev/null 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT

i=0
until curl -sf "$base/text" >/dev/null; do
  i=$((i + 1))
  [ "$i" -gt 60 ] && { echo "preview did not start"; exit 1; }
  sleep 0.5
done

key() { curl -sf -X POST "$base/key" -d "$1"; }
expect() {
  case "$2" in
    *"$1"*) echo "ok: $1" ;;
    *) echo "FAILED: expected '$1' in:"; echo "$2"; exit 1 ;;
  esac
}

curl -sf -X POST "$base/reset" -d "" >/dev/null
key Info >/dev/null              # Info on Continue watching -> title page
sleep 0.5
expect "[Resume S2:E3" "$(curl -sf "$base/text")"
key Back >/dev/null
key Select >/dev/null            # one press to continue: resume -> player
sleep 2                          # preparing -> playing
expect "overlay: hidden" "$(curl -sf "$base/text")"
expect "overlay: open" "$(key Select)"
expect ">[Audio & subtitles]" "$(key Up)"
expect "overlay: hidden" "$(key Back)"
expect "Press Back again to exit" "$(key Back)"   # first Back: overlay with the exit hint
expect "== Home" "$(key Back)"                      # second Back within 3 s leaves; progress is saved
expect "\"nodes\"" "$(curl -sf "$base/state")"
expect "data-key" "$(curl -sf "$base/snapshot.html")"
echo "preview smoke test passed"
