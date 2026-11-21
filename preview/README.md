# TV UI browser preview

Runs the TV app's screens (`core/Ui`) on the Mac and shows them in a browser. The page draws the same `UiLayout` frames the ElmSharp painter draws on the TV, on a 1920x1080 stage scaled to the window, so layout, focus and theme match.

```sh
dotnet run --project preview            # http://127.0.0.1:5173, live add-on data when Cinemeta answers
dotnet run --project preview -- --port 5191
dotnet run --project preview -- --fake  # offline fixtures only
dotnet run --project preview -- --live  # insist on live data
```

Frames come from the same `UiAnimator` the TV uses (focus growth, gliding rows, overlay slides, screen transitions, billboard crossfades) at 30 fps; the page only moves and fades elements it already has, like the ElmSharp painter. "Settings" in the Home menu switches reduced motion on.

## Keys

The keyboard acts as the Samsung remote. The page also has a clickable remote.

| Keyboard | Remote key | Tizen key name |
|---|---|---|
| Arrow keys | Up / Down / Left / Right | `Up`, `Down`, `Left`, `Right` |
| Enter | Select (center) | `Return` |
| Backspace or Escape | Back | `XF86Back` |
| Space | PlayPause | `XF86PlayBack` |
| P | Play | `XF86AudioPlay` |
| K | Pause | `XF86AudioPause` |
| S | Stop | `XF86AudioStop` |
| [ | Rewind | `XF86AudioRewind` |
| ] | FastForward | `XF86AudioForward` |
| N | Next | `XF86AudioNext` |
| M | Menu (card actions, rate) | `XF86SimpleMenu` |
| I | Info (title page) | `XF86Info` |
| Y / B | Yellow / Blue colour keys | `XF86Yellow`, `XF86Blue` |

Holding a key repeats it, and the app treats that as a held remote key: seeking speeds up from 10 s to 30, 60 and 120 s steps.

## HTTP endpoints

- `GET /` serves the page.
- `GET /state?since=<version>` long-polls until the UI changes. It returns `{version, screen, html, text, video}`.
- `GET /snapshot.html` renders the settled layout as static HTML (handy for headless screenshots).
- `GET /text` returns the plain-text snapshot (`UiText.RenderText`).
- `POST /key` takes a remote key (`Select`) or a Tizen key name (`Return`) as the body, and returns the new text snapshot.
- `POST /reset` starts over on Home with fresh fake data.

`tests/Ui/preview-smoke.sh` drives these endpoints. It checks that pressing center during playback opens the overlay, that Back closes it, and that Back twice (hint, then exit) leaves playback.

## Playback

The fake player keeps its own clock. The browser `<video>` follows it with play, pause and seek, looping the public Sintel trailer MP4 (download.blender.org) under the simulated timeline. If the clip can't play, a placeholder with a running clock takes its place, and the overlay, seeking and subtitles still work.

The app subtitle layer draws all subtitles, never the browser's captions. That includes the English test cues from `probe/probe.en.srt` and generated demo lines (English and Spanish) every 6 s. Colour, outline, background box, position, size and timing can all be previewed. The subtitle style is saved in `$TMPDIR/ru7100-ui-preview/settings`.

## Trailers

After about 1.2 s on a Home title its card pops out and plays the trailer in the card; on a title page the preview plays in the header behind the page after about 2.5 s (the actions stay; any key stops it and still acts), and the Trailer button opens the full-screen trailer view. Trailers come from the Cinemeta `trailerStreams` YouTube ids, as a YouTube embed, with sound unless preview sound is turned off in Settings. Play/Pause switches the sound on Home. On Details the Trailer button unmutes or stops it. The still art stays visible until YouTube reports that the video is playing, so a blocked embed never leaves a black hole. The TV plays the same trailers on its hardware player (`probe/Tizen/Trailer`).

## Real data

`preview/Composition.cs` picks the data. In live mode it builds the TV's own composition root, `core/App` `AppHost`, with the Mac host's `SimulatedPlayer` and `SimulatedOpener` from `host/Simulation.cs`. The preview and the TV therefore share the same wiring: the add-on registry, Cinemeta catalog, search across catalog add-ons, source ranking, the real `PlaybackSession`, player settings and the feedback recorder for the Rate actions. The library and account are fakes, because the preview has no Stremio sign-in. The For You row uses fixture recommendations, since a fresh Mac has an empty store.

Fake mode (`--fake`, or when Cinemeta can't be reached) uses `preview/fixtures/cinemeta.json`, a snapshot of real Cinemeta metadata. Its streams are Torrentio-style and ranked by the real `core/Addons` rules, and a fake player shows demo subtitle cues.

## Remote shortcuts worth knowing

- Center on a Continue watching card resumes at once. Info (`I`, or the Blue colour key) opens the title page.
- While a title is starting, Back cancels at once and center opens the source picker. A start that fails stays on the start card with Try again / Choose another source / Back.
- During playback, Back shows the overlay with "Press Back again to exit"; a second Back within 3 s leaves (the position is saved).
- Menu (`M`, or the Yellow colour key) on a card opens its actions: title page, I like this, Not for me, Not interested.
- Home menu bar: Home, Search, Browse (filters), Queue, and Settings at the right (Add-ons, account, reduced motion, previews).
- Center on any other title opens its page; Play/Pause plays it directly. On a popped card, Down steps into its buttons (Play, My List, Rate, More info).
- Several previews can run side by side: each serves torrent streams on its own loopback port (40000 + the preview port; override with RU7100_MEDIA_PORT).
