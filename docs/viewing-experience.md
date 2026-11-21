# Viewing experience: plan

Status: planned, mocks in review (`docs/design/06-*.html` to `09-*.html`). Decisions: AGENTS.md, 2026-11-20.

The app stays standalone. Everything below runs in the TV app with data the app fetches itself. Nothing is analysed or downloaded on the Mac.

## 1. Launch intro

Today the app shows nothing while it starts, then pops the home screen in once the catalogs and artwork arrive.

- **Tizen splash image.** The launcher shows a still frame (ink background, centred mark) the moment the app is opened, before our code runs. Set it in `tizen-manifest.xml`.
- **Animated intro.** As soon as the painter is up: the second chevron slides out from behind the first (beat one, with an amber glow), "direct" rises in letter by letter (beat two, a bright tap), then the whole logo flies into Home's menu and becomes its wordmark while Home fades in. Home's menu draws no wordmark of its own until the logo lands, so there is never a second copy on screen. Short original sound, not Netflix's. Home loads behind it.
- **Timing.** The intro plays for at least 1.8 s and hands over to Home as soon as Home's first row and billboard art are ready. If loading takes longer, the mark holds with a slow amber progress line under the wordmark; no spinner.
- **Rules.** Any key skips the intro. The sound follows a "Launch sound" setting (default on) and never plays when the app resumes from the background, only on a cold start.
- **Done when:** a cold start on the RU7100 shows the splash within the launcher's first frame, never shows a blank or half-built screen, and Home appears with its art already in place.

## 2. Next episode at the credits

When the credits start, the picture shrinks into the top-left corner and an Up next panel with one button, "Next episode", counts down and then plays it on its own (with or without binge mode). OK plays now; Back hides the panel and keeps the credits.

Where the credits start, best source first:

| Source | Lookup | Covers |
|---|---|---|
| TheIntroDB / IntroDB (community APIs) | TMDB or IMDb id + season/episode | Many popular series: intro, recap, credits, preview |
| AniSkip (community API) | MyAnimeList id + episode | Most popular anime: opening and ending |
| Chapter markers in the file | MKV `Chapters` in the head bytes we already fetch | Many anime releases ("ED"), some movies ("Credits") |
| Last subtitle line | Subtitle file we already load | Almost everything; skip "[music]" and credit lines |
| What you did last time | Per show: the moment you pressed Next before | Shows you watch, gets better each episode |
| Fixed fallback | Last 45 s of the episode | Everything |

Movies don't get the card; the end of a movie leads to the title page with "More like this" instead (later).

"Are you still watching?": after 3 episodes in a row with no key press, playback pauses at the start of the next episode and asks. Continue watching / Back to browse.

## 3. Next episode ready in advance

The RAM cache (160 MB) can't hold a whole episode, and it doesn't need to: an instant start needs the sources picked, the torrent's metadata and peers, and the first and last few MB of the file (about 20 to 40 MB).

- Start when the current episode is past 50% and its download is idle (buffer ahead full, or the end of the file reached), so it never slows what you're watching.
- Prefer the same torrent when it is a season pack, then the same release group, so quality, audio and subtitle timing stay consistent.
- The card at the credits shows a green "Ready" dot once the next episode can start instantly.
- Code: `TorrentSourceOpener.PrefetchAsync` exists; wire it into `PlaybackSession`. Per the standing rule it reaches the TV after the Mac start-time check shows faster next-episode starts.
- **Done when:** the next episode plays within 2 s of pressing Next on the RU7100, in a few runs on a few shows.

## 4. Skip intro and skip recap

Same sources as the credits (TheIntroDB/IntroDB intro and recap, AniSkip opening, chapters). A "Skip intro" button appears bottom right for the length of the intro; OK skips. No fallback guess: the button only shows when a source knows the times.

## 5. Smaller touches

- **Resume a few seconds back:** resuming starts 5 s before where you stopped.
- **Continue watching starts instantly:** prefetch the first Continue watching item while you're on Home (same mechanism as 3).

## Order

1. Launch intro (splash image, animated mark, sound).
2. Next-episode card with countdown and autoplay, using the API, chapter, subtitle, learned and fallback sources; "Still watching".
3. Next-episode prefetch, proven on the Mac first.
4. Skip intro and recap.
5. Resume 5 s back; Continue watching prefetch.
