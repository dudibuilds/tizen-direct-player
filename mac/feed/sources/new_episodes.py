"""New seasons/episodes of shows in the user's history or Stremio library (Cinemeta last-videos).

Baseline per parity C8/C9: a video is new when it was released after the user's last activity
on the show (and after the furthest episode they reached), within the configured window.
"""

from datetime import timedelta

from .base import Source
from .cinemeta import CINEMETA, fetch_last_videos, parse_meta, parse_videos


def describe(first, latest, count, state, season_sizes):
    """Short reason text for the new-episodes row."""
    season, episode = first[0], first[1]
    last_season = state.last_season
    if episode == 1 and (last_season is None or season > last_season):
        if last_season:
            finished = False
            if state.finished_episode and state.finished_episode[0] == last_season:
                finished = state.finished_episode[1] >= season_sizes.get(last_season, 10**6)
            elif state.netflix_seasons.get(last_season):
                finished = state.netflix_seasons[last_season] >= season_sizes.get(last_season, 10**6)
            return f"New season · you {'finished' if finished else 'watched'} S{last_season}"
        return f"New season {season}"
    if count > 1:
        return f"{count} new episodes · up to S{latest[0]}E{latest[1]}"
    return f"New episode · S{season}E{episode}"


class NewEpisodesSource(Source):
    name = "newEpisodes"

    def enabled(self, ctx):
        if not ctx.settings(self.name).get("enabled", True):
            return False, "disabled in config"
        if not ctx.profile or not ctx.profile.shows_for_new_episodes(1):
            return False, "no followed shows in the profile"
        return True, None

    def collect(self, ctx, report):
        settings = ctx.settings(self.name)
        base = ctx.settings("cinemeta").get("baseUrl", CINEMETA).rstrip("/")
        shows = {state.id: state for state in ctx.profile.shows_for_new_episodes(int(settings.get("maxShows", 200)))}
        window_start = ctx.now - timedelta(days=int(settings.get("days", 45)))
        try:
            metas = fetch_last_videos(ctx.http, base, shows, ctx.ttl(self.name, 6))
        except Exception as error:  # noqa: BLE001
            report.error(error)
            return []
        items = []
        for meta in metas:
            candidate = parse_meta(meta, "series", ctx.now)
            state = shows.get(candidate.id) if candidate else None
            if state is None:
                continue
            videos = [video for video in parse_videos(meta, ctx.now) if video[2] is not None and video[2] <= ctx.now]
            season_sizes = {}
            for video in videos:
                season_sizes[video[0]] = max(season_sizes.get(video[0], 0), video[1])
            # Unwatched = after the furthest episode reached on the TV/Stremio; for Netflix-only shows
            # (no episode numbers) = released after the last viewing and not in an older season.
            baseline = state.last_at
            reached = state.last_episode
            netflix_season = max(state.netflix_seasons) if state.netflix_seasons else 0
            if reached:
                unwatched = [video for video in videos if (video[0], video[1]) > reached]
            else:
                unwatched = [video for video in videos
                             if (baseline is None or video[2] > baseline) and video[0] >= netflix_season]
            fresh = [video for video in unwatched if video[2] >= window_start]
            if not fresh:
                report.counts["no_new_videos"] += 1
                continue
            first, latest = fresh[0], fresh[-1]
            next_up = unwatched[0]          # play from where the user is, even if that is older than the window
            candidate.new_episode = {
                "videoId": next_up[3], "season": next_up[0], "episode": next_up[1],
                "released": max(video[2] for video in fresh).date().isoformat(), "count": len(fresh),
                "reason": describe(first, latest, len(fresh), state, season_sizes),
            }
            candidate.sources[self.name] = {"rank": 0}
            items.append(candidate)
        report.counts["shows_checked"] = len(shows)
        return items
