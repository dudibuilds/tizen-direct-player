# UX research: Netflix patterns, re-imagined for the RU7100 app

Observed on 2026-11-18 on netflix.com (desktop web, the user's own profile), read-only apart from resuming one Continue Watching episode to inspect the player. Netflix's TV app differs in detail (remote focus instead of hover), so the TV adaptation below is ours. Items marked *(not observed here)* come from general knowledge of the Netflix TV app and should be checked before relying on them.

We copy interaction patterns, not branding: no Netflix name, logo, red "N", fonts or artwork.

## 1. What Netflix does

### Home
- **Billboard (hero)**: full-width key art with the title as a logo image, a metadata line `Show · Drama · 2023 · 3 Seasons · 16+`, a two-line synopsis, **Play** and **More Info**, and tag chips bottom-right (e.g. "Recently Added", "Emmy Nominee", a critic quote). A muted trailer plays in it.
- **About 25 rows**, reshuffled on every visit. Row kinds seen:
  | Kind | Examples | Card style |
  |---|---|---|
  | Personal next steps | Your Next Watch, Continue Watching for &lt;profile&gt;, Catch Up on Unwatched Episodes | Landscape; Continue Watching has a thin progress bar under the card |
  | Charts | Top 10 TV Shows in Your Country Today, Top 10 Movies | Portrait poster beside a huge outlined rank numeral |
  | Episode-level | Hooked from the First Episode, Short Episodes, Big Laughs | Landscape episode still with runtime badge; below it the episode title and `Genre · rating` |
  | Because-you | Because you liked Dark, Because you watched 3 Body Problem | Landscape |
  | Genre / mood / format | Laugh Out Loud Shows, Japanese Anime, Korean TV Shows, Critically Acclaimed Bingeworthy US TV Dramas, Rousing TV Shows, Every Second Counts, TV Shows based on Web Novels, Most Rewatched Shows, Boredom Busters | Landscape |
  | Freshness | New on Netflix, Today's Top Picks for You | Landscape |
- **Card labels** (bottom-centre pill, red or red+white split): `New Season`, `New Episodes`, `Recently Added`, `New Episode | Watch Now`, `Next Episode | Friday` (schedule). A small `TOP 10` corner badge. Title art is baked into the landscape image (logo over a still).
- **Hover preview**: after a short dwell the card grows in place, plays a muted clip, and shows Play, rate (thumb), expand, plus `Show · 13+ · 22m · HD`.

### Title page (modal)
- Trailer at the top with mute toggle; title logo; **Play** or **Resume** with a progress bar and `4 of 22m`; **My List** (+/✓); **Rate** (thumb).
- Metadata: year, `5 Seasons`, badges `HD`, `Spatial Audio`, `AD`, subtitles icon; maturity box `16+` with content descriptors ("violence, language, substances"); for a resumed show, `S6:E8 "Koi Pond"`.
- Side column: Cast (3 + "more"), Genres, **"This show is: Violent, Gritty, Dark"** (mood tags).
- **Episodes**: season dropdown with per-season maturity; numbered rows: still, title, runtime, two-line synopsis; current one highlighted.
- **Trailers & more** (making-of, clips) as cards.
- **More Like This**: 3-column grid; each card shows `N Seasons`, maturity, `HD`, year, a **+** button and a synopsis; expandable.
- **About**: creators, full cast, genres, mood tags, maturity with "Recommended for ages 16 and up".

### Search
- Results appear **as you type** (3 letters were enough); the URL updates to `/search?q=`.
- A **"More to explore"** chip row mixes title suggestions and collections ("Heist Movies", "Heist Action Movies", "Blockbuster Heist Movies").
- Results are a dense landscape grid that includes thematic matches, not only title-prefix matches. The box placeholder is "Titles, people, genres".

### Browse
- **Shows / Movies** pages: own billboard, a **Genres** dropdown (Action, Anime, Asian, British, Comedies, Crime, Documentaries, Dramas, European, Holidays, Horror, Kids, Reality & Talk, …), then genre rows.
- **New & Popular**: New on Netflix, Top 10 TV, Top 10 Movies, **Coming This Week**, **Coming Next Week**.
- **Browse by Languages**: filter `[Original Language | Dubbing | Subtitles] × [language]`, then a poster grid.
- **Notifications** bell with a count.

### Player (paused, controls visible)
- Top: back arrow (left), report-a-problem flag (right). Centre: large pause glyph.
- Bottom: full-width scrubber (red progress, buffered grey) with **remaining time** at the right; then play/pause, **−10 / +10**, volume, title + `E8 Koi Pond` centred, then **Next Episode**, **Episodes**, **Audio & Subtitles**, **Playback Speed**, fullscreen.
- **Next Episode** preview card: still, number, title, synopsis.
- **Episodes** panel: season list with per-episode progress bars; the current episode expands with still, "Now Playing" and synopsis.
- **Audio & Subtitles** panel: two columns. Audio lists `English [Original]` first, then other dubs; Subtitles starts with `Off`, then the user's likely languages (e.g. Spanish, English (CC)), a divider, then all others.
- **Playback speed**: 5-stop slider 0.5×–1.5×.
- *(not observed here)* Skip Intro / Skip Recap buttons, next-episode countdown during credits, "Are you still watching?".

### Subtitle appearance (account settings, per profile)
- Text size (S/M/L), font (7 choices), shadow (None, Raised, Depressed, Uniform, Drop Shadow), background colour/opacity, window colour/opacity, live preview, Save / Reset to Default.

## 2. Re-imagined for our app

Our data: Cinemeta (meta, `videos` with titles/overview/thumbnails/release dates, `logo`, `background`, `trailers`, genres, cast, imdbRating), installed catalog add-ons, Stremio library and watched state, our rule-based feed, and per-source facts from Torrentio (quality, size, HDR, audio/subtitle languages, health). Our input is a 5-way remote, not a mouse.

### Principles
1. **One press to continue.** Launch lands on Home with the hero set to the most recent Continue Watching item; center resumes it.
2. **Focus = hover.** Everything Netflix reveals on hover we reveal on focus dwell (~1.5 s expand, ~2.5 s muted trailer). Moving on cancels instantly.
3. **Show what we know that Netflix can't.** Sources differ per episode, so the title page and cards say whether a title is actually playable well: `4K HDR · 5.1 · EN audio · HE subs`. That is our signature line.
4. **Labels are earned from data**, never decorative.
5. **No dead ends.** Every empty state offers the next action (search, add an add-on, browse a genre).

### Home rows (in order; hide empty rows)
| Row | Source | Card | Label rules |
|---|---|---|---|
| Continue Watching | Stremio library `timeOffset`/`video_id` | Landscape still of the episode, progress bar, `S2:E3 · 21m left` | `New Episode · Watch now` when the next unwatched episode is new since last watch |
| New episodes of your shows | library + Cinemeta `videos.released` | Landscape | `New Episode`, `New Season`, `Next episode · Fri` for scheduled ones |
| For You / Because you watched X | rule-based feed rows | Landscape | Feed `reason` as a subtle subtitle on focus |
| Start something new: first episodes | feed titles not started → S1E1 | Episode still + runtime badge, `Pilot · Drama` | — |
| Top today (movies / series) | Cinemeta `top` catalogs | Portrait + big rank numeral | `Top 10` corner |
| Short episodes (< 30 min) | feed/library filtered by runtime | Episode cards | — |
| Genre / mood rows | feed + Cinemeta genre catalogs | Landscape | — |
| Your library | Stremio library | Landscape | `Watched` check badge, progress |
| Add-on catalogs | each installed catalog add-on | As the add-on's posterShape | — |

Labels available to us: `New Episode`, `New Season`, `Recently added` (release ≤ 30 days), `Next episode · <weekday>`, `Top 10`, `Watched`, `In progress`, `4K`/`HDR` (best playable source, only after a source check has run), `ES subs` (a subtitle source in the preferred language exists).

### Title page
- Hero: background art + logo (Cinemeta `logo`), muted trailer after a short dwell (preview: YouTube embed; TV: still image until trailer playback is proven).
- Primary action adapts: **Resume S2:E3 · 21m left** / **Play S1:E1** / **Play next: S2:E4 (new)**; secondary: **Episodes**, **Trailer**, **My library ✓**, **Rate 👍/👎/❤** (feeds recommendations), **Choose source**.
- Signature line under the metadata: best playable source `1080p · 2.3 GB · EN audio · HE subs` with a health dot, computed in the background when the page opens.
- Metadata line `Series · Crime, Drama · 2008–2013 · 5 seasons · IMDb 9.5`; cast (3 + more); genres; mood tags later (TMDB keywords if keys are added).
- Episodes: season tabs; rows with still, number, title, runtime, synopsis, watched check or progress bar; release date for upcoming ones; the focused row expands.
- More Like This: feed "Because you watched" for this title if present, otherwise same-genre items from catalogs.

### Search
- Keyboard on the left; results update while typing (debounced) across all catalog add-ons with search.
- Chip row: title suggestions plus collections we can serve (genres, "with subtitles in your language", "4K", year ranges), and later people (needs TMDB).
- Results: larger landscape cards in a grid; the focused card expands with metadata and the signature line once known.

### Browse & filters (new screen: "Browse")
Filters we can support with our data: **Type** (Movies/Series), **Genre**, **Year/decade**, **Rating** (IMDb ≥ 7/8), **Runtime** (short episodes / under 2 h), **Language availability** (original language; *has subtitles in your languages*; *English audio*), **Quality** (4K / HDR available), **Status** (new, not started, in progress, finished). Netflix's "Original / Dubbing / Subtitles × language" becomes our **"Watchable with"** filter backed by source checks.

### Player (remote)
Overlay layout mirrors the observed web player but is focus-driven:
- Top-left: title, `S6:E8 · Koi Pond`; top-right: source chip `1080p · 412 peers` (focusable → switch source).
- Scrubber with remaining time; Left/Right seek (accelerating); thumbnail-free (no trickplay images available).
- Bottom row: play/pause, −10/+10, **Next episode** (focus shows preview card), **Episodes** (side panel with season list, per-episode progress, "Now playing"), **Audio & subtitles** (two columns: audio with `[Original]` first; subtitles `Off`, then preferred languages, divider, rest; external add-on subtitles marked), **Subtitle style**, **Speed** (0.5–1.5×, if AVPlay rate change is reliable), **Binge** / **Stop after this episode**.
- Credits: last 30 s (or ≥ 98 %) shows a **Next episode** card with countdown when binge is on; Back cancels.
- *Skip intro*: no data source for intro markers; skip it for v1.
- Media keys act immediately; center opens the overlay (standing decision).

### Subtitle style
Keep our style menu, add Netflix's useful dimensions: shadow type (none / drop / uniform outline), background box colour + opacity, window (none / translucent), size S/M/L plus fine steps; live preview line; Reset to default.

## 3. Hand-off to the UI agent (priority order)
1. Home rows and labels per the table, landscape cards with logo-over-still art, Top 10 rank style, episode-level rows.
2. Title page structure with the adaptive primary action and the signature source line.
3. Player overlay: Next-episode preview, Episodes panel, two-column Audio & subtitles, credits next-episode card.
4. Search chips and expanded focus card.
5. Browse screen with filters (can trail; needs source-availability data from `ISourceResolver`).
6. Rate actions wired to recommendation feedback.
