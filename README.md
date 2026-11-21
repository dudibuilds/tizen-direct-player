# RU7100 Direct Player

**A Stremio-compatible media player for Samsung Tizen smart TVs that plays torrents and HTTP streams directly on the TV.** It needs no casting, no phone, and no PC or media server in the playback path. It loads your Stremio add-ons, finds and ranks sources, and streams them with its own BitTorrent engine into Samsung's hardware player. You control it with the normal TV remote, or from AI agents over an authenticated API and MCP server.

> **Status: early.** Built for and tested on one TV, a 2019 Samsung **UE55RU7100** (Tizen 5.0). Other Tizen 5+ Samsung TVs may work but are untested. Expect rough edges.

## Why

Watching Stremio add-ons on an older Samsung TV usually means casting from a phone, adding a streaming stick, or running a PC server that transcodes. This app runs entirely on the TV:

- **Torrents on the TV itself.** A managed-C# BitTorrent engine (MonoTorrent) with a RAM-only rolling cache, so any size of movie plays without filling the TV's tiny storage. Seeking and resume are built in; seeks far into uncached parts of a file are still being hardened.
- **uTP (BEP 29) in pure managed C#.** Samsung's TVs refuse native code from user apps, so libtorrent and libutp can't be used. This app includes its own uTP implementation, wire-compatible with libutp and libtorrent, plugged into MonoTorrent. See [docs/utp.md](docs/utp.md).
- **Your Stremio add-ons.** It installs add-ons by manifest URL or loads them from your Stremio account (device-link sign-in, no password typed on the TV). It covers catalogs, search, library and watch state, continue watching, next episode, binge mode and an ordered queue. Progress syncs back to Stremio; Trakt is optional.
- **A 10-foot UI.** Rows, pop-out cards with trailer previews (official trailers from IMDb), a title page and episode lists, a source picker with quality, size and health badges, and search as you type across all catalog add-ons. It is sized for viewing from across the room.
- **A full player on the remote.** Play/pause, seek, audio and subtitle tracks and languages, subtitle size, style and timing (±30 s). Audio and subtitle choices are remembered per show.
- **Agent control.** An authenticated HTTP API plus an MCP server (`mcp/`) expose playback, library and recommendation commands, so Claude or another agent can drive the TV ("play the next episode of …"). Agents and the remote drive the same player.
- **Recommendations without a model.** A deterministic feed builder can be bootstrapped from a Netflix viewing-history export, then keeps learning from what you watch in the app. See [docs/recommendations.md](docs/recommendations.md).

**No content is included.** The app ships with Cinemeta (metadata) and OpenSubtitles (subtitles) only. Streams come only from add-ons *you* install. Use it with content you have the right to watch.

## Requirements

- A Samsung Tizen TV with **Developer Mode** enabled (tested: RU7100, Tizen 5.0).
- A Mac or PC with the .NET SDK and the **Tizen SDK / Tizen Studio** command-line tools (`sdb`, `tz`).
- A **Samsung certificate profile** for your own TV (created in Tizen Studio's Certificate Manager, tied to your TV's DUID). A package signed by someone else won't install on your TV, so you build and sign it yourself.

## Quick start

```sh
# 1. Build, sign and package (creates artifacts/ru7100-probe.tpk and the API keys)
./scripts/package-probe.sh "$TIZEN_SDKTOOLS" "Your certificate profile name"

# 2. Install and launch on the TV
"$TIZEN_SDKTOOLS/data/tools/sdb" connect "$TV_IP:26101"
"$TIZEN_SDKTOOLS/data/tools/sdb" -s "$TV_IP:26101" install artifacts/ru7100-probe.tpk
"$TIZEN_SDKTOOLS/data/tools/sdb" -s "$TV_IP:26101" shell 0 execute org.dudi.ru7100probe
```

For day-to-day work, `scripts/tv-dev.sh` builds, signs, installs, relaunches and waits for the API in one step (set `TIZEN_SDKTOOLS`, `TV_TARGET` and optionally `TIZEN_PROFILE`).

Then add a stream add-on: sign in to your Stremio account from the app, or install one by manifest URL through the API (`addons.install`).

## Develop on the Mac, not the TV

The UI renders the same view-models in a browser preview, with the keyboard standing in for the remote. You iterate there and use the TV only for hardware checks:

```sh
dotnet run --project preview
```

See [preview/README.md](preview/README.md), [docs/development.md](docs/development.md) (TV install, MCP, Netflix import, recommendations) and [docs/remote-ui.md](docs/remote-ui.md) (drive the real TV from code running on the Mac).

Run the tests with `dotnet test tests`.

## Layout

| Path | What |
|---|---|
| `core/` | Platform-independent app: add-ons, Stremio account and library, playback session, source ranking, UI view-models and layout, agent API |
| `probe/` | The Tizen TV app: EFL painter, AVPlay player, torrent engine and uTP (`probe/Sources/Utp`) |
| `preview/` | Browser preview of the TV UI on the Mac |
| `host/`, `devhost/` | Run the app's API or UI on the Mac against fakes or the real TV |
| `mcp/` | MCP servers for playback and recommendations |
| `mac/` | Netflix history import and the recommendation feed builder (Python) |
| `desktop/` | Mac command-line checks: torrent start time, uTP interop, cache behaviour |
| `docs/` | Design notes: uTP, recommendations, UX research, Stremio feature parity |

## Working with AI agents

The repo was built mostly by AI coding agents, and `AGENTS.md` holds the project's standing decisions: the architecture rules, the design language and the performance bar. Read it before changing anything substantial.

## Acknowledgements

[MonoTorrent](https://github.com/alanmcgovern/monotorrent) (BitTorrent engine), [libutp](https://github.com/bittorrent/libutp) (the uTP reference implementation used for interop tests), [Stremio](https://www.stremio.com/) and its add-on protocol, Cinemeta and OpenSubtitles. See [NOTICE.md](NOTICE.md).

This project is not affiliated with Samsung, Stremio, Netflix or IMDb.

## License

[MIT](LICENSE)
