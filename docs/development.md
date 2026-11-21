# Development guide

Details for building, running and extending the app. Start with the [README](../README.md).

## Run on the TV

The TV app (`probe/`, package `org.dudi.ru7100probe`) composes the remote UI, the playback session engine and the agent API in `core/App/AppHost.cs`. Remote keys and MCP commands drive the same session. Use a Tizen SDK with a Samsung certificate profile for the target TV. The SDK path below is the folder containing `data/tools/sdb`, `data/tools/tizen-core/tz`, and `sdk-data/profile/profiles.xml`.

```sh
./scripts/package-probe.sh "$SDKTOOLS" "$TV_CERT_PROFILE"
"$SDKTOOLS/data/tools/sdb" connect "$TV_IP:26101"
"$SDKTOOLS/data/tools/sdb" -s "$TV_IP:26101" install artifacts/ru7100-probe.tpk
"$SDKTOOLS/data/tools/sdb" -s "$TV_IP:26101" shell 0 execute org.dudi.ru7100probe
```

The script creates `artifacts/ru7100-api-keys.json` once (admin, playback, library and recommendations keys; ignored by git) and embeds it in the package. A build without it runs with the agent API off. The app keeps its state under the app data folder: `store/` (settings, Stremio sign-in, library cache and outbox, playback session, queue, recommendations and feedback), `media/` (torrent data, swept on start, at most 512 MB of idle data kept) and `posters/`.

Back on Home exits the app. Leaving the app (Home or Source button) pauses playback and saves the position; on the next launch the app offers to continue where it stopped.

Run the shared transport on macOS with `dotnet run --project desktop/TransportCheck.csproj`. It checks byte ranges at the head, tail and middle of the torrent from a fresh cache. Add `-- --serve` to keep the URL open for local playback debugging.

## Import Netflix viewing history on the Mac

Netflix's [Viewing activity download](https://help.netflix.com/en/node/101917) contains dates and titles for one profile. The [Netflix account data export](https://help.netflix.com/en/node/100624) can also include per-session durations, bookmarks and ratings. The importer accepts either CSV or ZIP and reads only viewing activity and rating tables.

```sh
python3 mac/netflix_history.py profiles /path/to/Netflix-export.zip
python3 mac/netflix_history.py import /path/to/Netflix-export.zip --profile 'Profile name'
python3 mac/netflix_history.py import /path/to/NetflixViewingHistory.csv --profile 'Profile name' --date-order mdy
```

Set `--date-order dmy` for a day-first export. The date-only CSV has no profile column, so pass the name of the profile that downloaded it. The importer writes to a private SQLite file in ignored `local-data/`, skips trailers, hidden views and unattended autoplay in the detailed export, and deduplicates repeated imports. A date-only record means the title was played that day.

## Refresh recommendations on the Mac

The local matcher uses [IMDb's personal-use datasets](https://developer.imdb.com/non-commercial-datasets/) so matching titles in the watch history requires no per-title network requests. It estimates genre affinity from recency, repeated viewing and imported ratings. A versioned snapshot includes ranked recommendations, recently watched titles and counts for labels that could be matched.

```sh
bash scripts/refresh-imdb.sh
python3 mac/recommendations.py --profile 'Profile name'
RU7100_NETFLIX_PROFILE='Profile name' node mcp/recommendation-server.mjs
```

The recommendations MCP can fetch the snapshot, refresh it from local data and curate the order with agent notes. It has no playback commands. The playback MCP has no access to the Netflix export.

## MCP during development

Against the TV, forward the loopback API port and point the MCP server at the package's key file:

```sh
npm --prefix mcp ci
"$SDKTOOLS/data/tools/sdb" -s "$TV_IP:26101" forward tcp:45551 tcp:45551
RU7100_API_URL=http://127.0.0.1:45551 RU7100_API_KEY_FILE=artifacts/ru7100-api-keys.json RU7100_API_KEY_ID=admin node mcp/server.mjs
```

Without the TV, run the same API on the Mac. `--services fakes` (the default) serves fixtures; `--services real` runs the TV composition with the real add-ons, Cinemeta search, source ranking, Stremio account and library, and the real session engine over a simulated player and source opener, so nothing is downloaded:

```sh
dotnet build host -c Release
dotnet host/bin/Release/net10.0/ru7100-host.dll --generate-keys /tmp/ru7100-keys.json
dotnet host/bin/Release/net10.0/ru7100-host.dll --keys /tmp/ru7100-keys.json --services real --data /tmp/ru7100-host-data --verbose
RU7100_API_URL=http://127.0.0.1:45561 RU7100_API_KEY_FILE=/tmp/ru7100-keys.json RU7100_API_KEY_ID=admin node mcp/server.mjs
```

The server uses stdio. Every request is signed, time-limited and carries a unique nonce (see `mcp/README.md`). Keep the package and key file private. The loopback port is a developer transport; the product's paired LAN transport is a separate task. The admin-only command `dev.diagnostics` returns the playback status, the torrent engine state and the app's recent log.
