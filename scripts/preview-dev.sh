#!/usr/bin/env bash
# Browser preview with hot reload: dotnet watch rebuilds and restarts the server whenever preview/ or core/ code
# changes, and the open page reloads itself when it sees the new server (boot id), so Chrome never shows a stale build.
#   scripts/preview-dev.sh            http://127.0.0.1:5237
#   PORT=5238 scripts/preview-dev.sh
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1 DOTNET_WATCH_SUPPRESS_EMOJIS=1 DOTNET_USE_POLLING_FILE_WATCHER=0
exec dotnet watch --non-interactive --no-hot-reload --project preview/Preview.csproj run -- --port "${PORT:-5237}"
