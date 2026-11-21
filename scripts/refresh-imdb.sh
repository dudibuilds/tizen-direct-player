#!/usr/bin/env bash
set -euo pipefail
umask 077

project="$(cd "$(dirname "$0")/.." && pwd)"
data="${RU7100_CATALOG_DIR:-$project/local-data/imdb}"
mkdir -p "$data"

for table in basics ratings akas episode; do
  target="$data/title.$table.tsv.gz"
  temporary="$(mktemp "$target.XXXXXXXX")"
  trap 'rm -f "$temporary"' EXIT
  conditional=()
  if [[ -f "$target" ]]; then
    conditional=(--time-cond "$target")
  fi
  curl --fail --location --silent --show-error --remote-time --max-time 210 \
    "${conditional[@]}" "https://datasets.imdbws.com/title.$table.tsv.gz" --output "$temporary"
  if [[ -s "$temporary" ]]; then
    gzip -t "$temporary"
    mv -f "$temporary" "$target"
  else
    rm "$temporary"
  fi
  trap - EXIT
done
