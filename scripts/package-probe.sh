#!/usr/bin/env bash
# Builds and signs the TV app (probe/) as artifacts/ru7100-probe.tpk.
# The agent API keys live in artifacts/ru7100-api-keys.json (ignored by git, created once, mode 600) and are embedded
# in the package. The MCP servers use the same file: RU7100_API_KEY_FILE=artifacts/ru7100-api-keys.json.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: %s /path/to/sdktools certificate-profile-name\n' "$0" >&2
  exit 2
fi

project="$(cd "$(dirname "$0")/.." && pwd)"
sdktools="$1"
profile="$2"
artifacts="$project/artifacts"
mkdir -p "$artifacts"

api_keys="$artifacts/ru7100-api-keys.json"
if [[ ! -s "$api_keys" ]]; then
  secret() { openssl rand -base64 32; }
  (
    umask 077
    printf '{"keys":[%s,%s,%s,%s]}\n' \
      "{\"id\":\"admin\",\"secret\":\"$(secret)\",\"scopes\":[\"admin\"]}" \
      "{\"id\":\"playback\",\"secret\":\"$(secret)\",\"scopes\":[\"playback\"]}" \
      "{\"id\":\"library\",\"secret\":\"$(secret)\",\"scopes\":[\"library\"]}" \
      "{\"id\":\"recommendations\",\"secret\":\"$(secret)\",\"scopes\":[\"recommendations\"]}" \
      > "$api_keys"
  )
  printf 'Generated agent API keys in %s\n' "$api_keys"
fi

DOTNET_CLI_HOME="$artifacts/dotnet-home" NUGET_PACKAGES="$artifacts/nuget" DOTNET_NOLOGO=1 \
  dotnet build "$project/probe/Ru7100Probe.csproj" --configuration Release --nologo "-p:ApiKeysPath=$api_keys"

build="$project/probe/bin/Release/tizen50"
version="$(xmllint --xpath 'string(/*/@version)' "$project/probe/tizen-manifest.xml")"
"$sdktools/data/tools/tizen-core/tz" pack -t tpk -s "$profile" \
  -p "$sdktools/sdk-data/profile/profiles.xml" -b "$build/org.dudi.ru7100probe-$version.tpk" \
  -o "$artifacts/ru7100-probe.tpk"
