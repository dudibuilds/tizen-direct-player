# Remote UI dev mode

The TV app's code (AppHost, TvShell, view-models, layout) runs on the Mac. The TV runs a thin client that paints the Mac's frames, sends remote keys back, and executes player and source-opener calls on its own hardware player and torrent engine. Media still goes from the source straight to the TV; the Mac never handles media bytes.

Use it to iterate on `core/` code against the real TV screen and remote without reinstalling. Changes to `probe/Tizen/Ui/ElmFramePainter.cs`, `probe/Tizen/Player`, `probe/Sources` or the client itself still need a reinstall.

## Run

```sh
scripts/tv-dev.sh --remote-ui                 # once: installs the client (about 7 s)
dotnet watch --project devhost run            # the app on the Mac; restarts on every code change
```

The TV shows "Waiting for Mac dev server…" until the dev host is up and reconnects within about a second of each restart. After a restart the app starts at Home. `scripts/tv-dev.sh` without `--remote-ui` puts the normal app back.

The first `--remote-ui` run writes `artifacts/ru7100-remote-ui.json` with the Mac's LAN address (`en0`, or `REMOTE_UI_HOST`), port 45560 and a random token. That file is git-ignored and gets embedded in the client. The dev host listens on that address only and drops connections that don't present the token.

The dev host's terminal also accepts keys: type `u`, `d`, `l`, `r`, `ok`, `back`, `play`, `menu` or `info`, or any Tizen key name, then Enter. `text` prints the visible text of the current screen, and `probe-player <url>` plays a direct URL on the TV's player for a few seconds. The agent API for this app instance is on `http://127.0.0.1:45553` (`RU7100_API_URL`).

## How it works

- `devhost/Protocol/RemoteUiProtocol.cs` is shared by both sides. It uses one TCP connection opened by the TV, with length-prefixed JSON messages.
- The Mac sends `UiLayout` target frames as diffs by node key: only changed properties, and the node order as runs of the previous order. The TV runs the same `UiAnimator`, lowering and `ElmFramePainter`, so animation needs no traffic and stays at the TV's own frame rate. Posters are downloaded by the TV's poster cache.
- `devhost/RemoteTv.cs` implements `IPlayerBackend`, `ISubtitleCapablePlayer`, `IVideoAreaPlayer` and `ISourceOpener` as calls to the TV. The TV streams player state, position, track lists, subtitle cues and source stats back.
- When the connection drops, the TV stops its player and closes its sources, and the Mac ends its session.
- The client is compiled only with `-p:RemoteUi=true`, which defines `REMOTE_UI`. Normal and release packages contain none of it.

## Measured on the RU7100 (2026-11-18, Wi-Fi LAN)

- Mac work per key (handle, layout, diff, send): 0.5–2 ms.
- Frame sent to painted on the TV: p50 about 50 ms, p95 about 80 ms. Of that, the TV's diff apply and paint take about 30 ms. Keys were injected on the Mac. With the physical remote, key to paint adds the TV-to-Mac hop (a few ms on the LAN). The client measures it and the dev host prints it every 5 s, but it has not been measured with a person at the remote yet.
- A focus move sends 0.5–2.5 KB. Screen changes send 6–12 KB. The first full frame of Home is about 6–13 KB.
- The TV paints up to about 30 frames a second while animating (the timer cap). Average paint time is 4–20 ms, and the first paint with poster decodes takes up to about 500 ms.
- Mac restart (build and start) to the TV showing Home again: about 7 s.

## Limits

- The Mac has its own store (`local-data/devhost`): Stremio sign-in, library cache and settings are separate from the TV's.
- Animator and painter code runs on the TV, so changes to `UiAnimator`, `UiFrameLowering`, `UiAssets` or the painter need a reinstall to show.
- The Mac downloads subtitle files and add-on data; only media playback stays on the TV.
- One TV at a time. A new connection replaces the old one.
