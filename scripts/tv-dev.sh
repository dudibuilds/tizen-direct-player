#!/usr/bin/env bash
# Fast TV iteration loop for the RU7100 app (probe/):
#   incremental build -> sign -> install -> relaunch -> forward the API port -> wait until the API answers.
# Skips signing and installing when the build output matches what this checkout last installed.
#
#   scripts/tv-dev.sh                build and install if anything changed, then (re)launch and wait for the API
#   scripts/tv-dev.sh --force        sign and install even if nothing changed
#   scripts/tv-dev.sh launch         relaunch the installed app and wait for the API
#   scripts/tv-dev.sh status         print the API status (admin key)
#   scripts/tv-dev.sh --remote-ui    install the remote UI dev client instead: the app then runs on the Mac
#                                    (dotnet run --project devhost; docs/remote-ui.md). Combines with --force.
#
# There is no on-TV hot reload: Samsung's launcher only runs DLLs inside the signed, installed package (AGENTS.md).
#
# Environment overrides:
#   TIZEN_SDKTOOLS   SDK tools dir (sdb at data/tools/sdb, tz at data/tools/tizen-core/tz)   (required)
#   TIZEN_PROFILE    signing profile name                      (default: RU7100 Personal)
#   TV_TARGET        sdb target host:port, e.g. 192.168.1.20:26101            (required)
#   RU7100_ARTIFACTS shared artifacts dir: API keys, remote UI config, NuGet cache (default: the main checkout's
#                    artifacts/, so worktrees share them)
#   CONFIGURATION    build configuration                       (default: Release, as scripts/package-probe.sh)
#   REMOTE_UI_HOST   Mac LAN address the remote UI client dials (default: this Mac's en0 address); used only when
#                    artifacts/ru7100-remote-ui.json is first created
set -euo pipefail

command="dev"
force=""
remote_ui=""
for arg in "$@"; do
  case "$arg" in
    --force) force=1 ;;
    --remote-ui) remote_ui=1 ;;
    launch|status) command="$arg" ;;
    *) printf 'usage: %s [--force] [--remote-ui] [launch|status]\n' "$0" >&2; exit 2 ;;
  esac
done

clock_ms() { perl -MTime::HiRes=time -e 'printf "%d", time*1000'; }
started=$(clock_ms)
project="$(cd "$(dirname "$0")/.." && pwd)"
sdktools="${TIZEN_SDKTOOLS:?set TIZEN_SDKTOOLS to the Tizen SDK tools folder (see README)}"
profile="${TIZEN_PROFILE:-RU7100 Personal}"
target="${TV_TARGET:?set TV_TARGET to your TV, e.g. 192.168.1.20:26101}"
artifacts="${RU7100_ARTIFACTS:-$(dirname "$(git -C "$project" rev-parse --path-format=absolute --git-common-dir)")/artifacts}"
configuration="${CONFIGURATION:-Release}"
app_id="org.dudi.ru7100probe"
api_port=45551
sdb="$sdktools/data/tools/sdb"
tz="$sdktools/data/tools/tizen-core/tz"
api_keys="$artifacts/ru7100-api-keys.json"
remote_config="$artifacts/ru7100-remote-ui.json"
build="$project/probe/bin/$configuration/tizen50"
out="$project/artifacts/tv-dev"            # per checkout, so worktrees don't collide; ignored by git
installed_stamp="$out/installed.sha"
mkdir -p "$out"

export DOTNET_CLI_HOME="$artifacts/dotnet-home" NUGET_PACKAGES="$artifacts/nuget" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export RU7100_API_KEY_FILE="$api_keys" RU7100_API_KEY_ID=admin

say() { printf '[tv-dev %6s s] %s\n' "$(perl -e 'printf "%.1f", ($ARGV[0] - $ARGV[1]) / 1000' "$(clock_ms)" "$started")" "$*"; }
die() { printf 'tv-dev: %s\n' "$*" >&2; exit 1; }

[[ -s "$api_keys" ]] || die "no API keys at $api_keys (run scripts/package-probe.sh once to create them)"

connect() {
  if "$sdb" devices | grep -q "^$target[[:space:]]*device"; then return; fi
  if ! nc -z -G 3 "${target%:*}" "${target##*:}" 2>/dev/null; then
    die "the TV does not answer on $target. It may be off: turn it on, or wake it through the media server MCP
  (http://127.0.0.1:4512/mcp, tool tv_control, action power_on), then retry."
  fi
  "$sdb" connect "$target" >/dev/null
  "$sdb" devices | grep -q "^$target[[:space:]]*device" || die "sdb could not connect to $target"
}

# Remote UI dev mode: where the TV dials, and the shared token (git-ignored, mode 600). devhost reads the same file.
ensure_remote_config() {
  [[ -s "$remote_config" ]] && return
  local host="${REMOTE_UI_HOST:-$(ipconfig getifaddr en0 || true)}"
  [[ -n "$host" ]] || die "set REMOTE_UI_HOST to this Mac's LAN address"
  (umask 077; printf '{"host":"%s","port":45560,"token":"%s"}\n' "$host" "$(openssl rand -hex 24)" >"$remote_config")
  say "created $remote_config (host $host)"
}

build_app() {
  local extra=(-p:RemoteUi=false)
  if [[ -n "$remote_ui" ]]; then
    ensure_remote_config
    extra=(-p:RemoteUi=true "-p:RemoteUiConfigPath=$remote_config")
  fi
  if ! dotnet build "$project/probe/Ru7100Probe.csproj" --configuration "$configuration" --nologo \
    "-p:ApiKeysPath=$api_keys" "${extra[@]}" >"$out/build.log" 2>&1; then
    grep -E ': error ' "$out/build.log" | sort -u >&2
    die "build failed (full log: $out/build.log)"
  fi
}

# What the package is made of (the .tpk itself embeds timestamps, so hash its inputs instead).
output_hash() {
  (cd "$build" && find . \( -path ./publish -o -path ./tpkroot \) -prune -o -type f ! -name '*.tpk' -print \
    | LC_ALL=C sort | xargs shasum; shasum "$project/probe/tizen-manifest.xml") | shasum | cut -d' ' -f1
}

install_app() {
  local version
  version="$(xmllint --xpath 'string(/*/@version)' "$project/probe/tizen-manifest.xml")"
  "$tz" pack -t tpk -s "$profile" -p "$sdktools/sdk-data/profile/profiles.xml" \
    -b "$build/$app_id-$version.tpk" -o "$out/ru7100-probe.tpk" >"$out/sign.log" 2>&1 \
    || { tail -5 "$out/sign.log" >&2; die "signing failed (log: $out/sign.log)"; }
  say "signed"
  "$sdb" -s "$target" install "$out/ru7100-probe.tpk" >"$out/install.log" 2>&1 || true
  grep -q "install completed" "$out/install.log" || { tail -5 "$out/install.log" >&2; die "install failed (log: $out/install.log)"; }
  say "installed"
}

forward() { "$sdb" -s "$target" forward "tcp:$api_port" "tcp:$api_port" >/dev/null; }

launch_and_wait() {
  # Installing stops the running app; `execute` on a running single-instance app only brings it forward.
  "$sdb" -s "$target" shell 0 execute "$app_id" >/dev/null
  if [[ -n "$remote_ui" ]]; then
    say "launched the remote UI client; it dials the Mac dev host (dotnet run --project devhost)"
    return
  fi
  forward
  say "launched, waiting for the API"
  if ! node "$project/scripts/tv-dev-api.mjs" wait 25; then
    # Seen once right after an install: the launch was lost. Launch again before giving up.
    say "no answer yet: launching again"
    "$sdb" -s "$target" shell 0 execute "$app_id" >/dev/null
    node "$project/scripts/tv-dev-api.mjs" wait 35 \
      || die "the app did not answer; it shows startup errors on screen (trace: shared/trusted/startup.log)"
  fi
}

api_up() { forward && node "$project/scripts/tv-dev-api.mjs" wait 0 >/dev/null 2>&1; }

dev() {
  # sdb connect (up to 3 s when the TV is off) runs while the build does.
  connect &
  local connecting=$!
  build_app
  say "built ($configuration${remote_ui:+, remote UI client})"
  wait "$connecting" || exit 1
  local hash
  hash="$(output_hash)"
  if [[ -z "$force" && -s "$installed_stamp" && "$(cat "$installed_stamp")" == "$hash" ]]; then
    if [[ -z "$remote_ui" ]] && api_up; then
      say "unchanged and running: nothing to do"
      return
    fi
    say "unchanged: relaunching without reinstalling"
  else
    install_app
    printf '%s\n' "$hash" >"$installed_stamp"
  fi
  launch_and_wait
  say "ready"
}

case "$command" in
  dev) dev ;;
  launch) connect; launch_and_wait; say "ready" ;;
  status) connect; forward; node "$project/scripts/tv-dev-api.mjs" command status ;;
esac
