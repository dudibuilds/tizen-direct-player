"""Statistical taste profile: explicit per-signal weights, time decay and feature affinities.

No model is trained. Each signal (Netflix view, TV completion, thumbs, ...) has a fixed weight
from config, decays with a half-life, and sums into a per-title weight. Feature affinities
(genre, people, country, era, type, keyword) are weighted sums over the titles' metadata.
"""

import math
import re
from collections import Counter, defaultdict
from dataclasses import dataclass, field

from .candidates import parse_datetime
from .feedback import VIDEO_ID
from .imdb import title_key

FAMILIES = ("genre", "person", "country", "era", "type", "keyword")
SEQUEL_SUFFIX = re.compile(r"\s+(?:\d{1,2}|[ivx]{1,4}|part \w+|chapter \w+|vol(?:ume)? \w+)$")
LEADING_ARTICLE = re.compile(r"^(?:the|a|an)\s+")


def franchise_key(name):
    """Normalized name without subtitle, sequel number or leading article ("The Saw X: Y" -> "saw")."""
    base = re.split(r":| - ", name or "", maxsplit=1)[0]
    key = LEADING_ARTICLE.sub("", title_key(base))
    previous = None
    while previous != key:
        previous = key
        key = SEQUEL_SUFFIX.sub("", key).strip()
    return key or title_key(name or "")


def same_franchise(a, b):
    """Exact key match, or one key is a word-prefix of the other with at least two words."""
    if not a or not b:
        return False
    if a == b:
        return True
    short, long = sorted((a, b), key=len)
    return len(short.split()) >= 2 and (long + " ").startswith(short + " ")


def decay(at, now, half_life_days):
    if at is None or half_life_days <= 0:
        return 1.0
    age = max(0.0, (now - at).total_seconds() / 86400)
    return 0.5 ** (age / half_life_days)


@dataclass
class Signal:
    imdb_id: str
    kind: str
    weight: float
    at: object
    family: str       # netflix | explicit | inApp | library
    source: str       # netflix | tv | library


@dataclass
class TitleState:
    id: str
    type: str = None
    name: str = ""
    signals: list = field(default_factory=list)
    weight: float = 0.0
    watched: bool = False            # seen (completed, partially or abandoned): never re-recommend
    disliked: bool = False           # latest explicit opinion is negative
    not_interested: bool = False
    abandoned: bool = False          # series dropped early: no "new episodes"
    in_library: bool = False
    notifications: bool = True
    last_at: object = None           # last viewing activity (new-episode baseline)
    last_episode: tuple = None       # (season, episode) furthest watched
    finished_episode: tuple = None   # (season, episode) furthest completed
    netflix_seasons: dict = field(default_factory=dict)   # season -> distinct episode labels
    sources: set = field(default_factory=set)

    @property
    def last_season(self):
        seasons = []
        if self.last_episode:
            seasons.append(self.last_episode[0])
        if self.netflix_seasons:
            seasons.append(max(self.netflix_seasons))
        return max(seasons) if seasons else None

    def touch(self, at):
        if at is not None and (self.last_at is None or at > self.last_at):
            self.last_at = at

    def reach(self, episode, completed=False):
        if episode is None or episode[0] <= 0:
            return
        if self.last_episode is None or episode > self.last_episode:
            self.last_episode = episode
        if completed and (self.finished_episode is None or episode > self.finished_episode):
            self.finished_episode = episode


class ProfileBuilder:
    """Collects signals from every input, then `finish()` returns the TasteProfile."""

    def __init__(self, config, now):
        self.config = config
        self.settings = config["profile"]
        self.w = self.settings["weights"]
        self.now = now
        self.titles = {}
        self.coverage = {}

    def state(self, imdb_id, type_=None, name=""):
        state = self.titles.get(imdb_id)
        if state is None:
            state = self.titles[imdb_id] = TitleState(imdb_id)
        if type_ and not state.type:
            state.type = type_
        if name and not state.name:
            state.name = name
        return state

    def add(self, state, kind, weight, at, family, source):
        if weight:
            state.signals.append(Signal(state.id, kind, float(weight), at, family, source))
        state.sources.add(source)

    # ---- Netflix (bootstrap) ----

    def add_netflix(self, history):
        w = self.w
        self.coverage["netflix"] = history.coverage()
        for imdb_id in sorted(history.titles):
            entry = history.titles[imdb_id]
            title = entry.title
            state = self.state(imdb_id, title.kind, title.name)
            if entry.days:
                state.touch(entry.last)
                state.watched = True
            if title.kind == "series" and entry.days:
                days = len(entry.days)
                state.netflix_seasons = {season: len(labels) for season, labels in entry.seasons.items()}
                if days <= 1 and entry.sessions <= 2:
                    state.abandoned = True
                    self.add(state, "netflix_series_sampled", w["netflixSeriesSampled"], entry.last, "netflix", "netflix")
                else:
                    weight = min(w["netflixSeriesMax"], w["netflixSeriesBase"] + math.log2(days) * w["netflixSeriesPerLog2Day"])
                    self.add(state, "netflix_series_watched", weight, entry.last, "netflix", "netflix")
            elif entry.days:
                minutes = max(entry.total_ms, entry.max_bookmark_ms) / 60000
                if not entry.has_durations:
                    self.add(state, "netflix_movie_watched", w["netflixMovieWatched"], entry.last, "netflix", "netflix")
                elif minutes >= 60:
                    self.add(state, "netflix_movie_completed", w["netflixMovieCompleted"], entry.last, "netflix", "netflix")
                elif minutes < self.settings["abandonEarlyMinutes"]:
                    self.add(state, "netflix_movie_abandoned", w["netflixMovieAbandoned"], entry.last, "netflix", "netflix")
                else:
                    self.add(state, "netflix_movie_partial", w["netflixMoviePartial"], entry.last, "netflix", "netflix")
                days = sorted(entry.days)
                if len(days) >= 2 and (parse_datetime(days[-1]) - parse_datetime(days[0])).days >= 30:
                    self.add(state, "netflix_rewatch", w["netflixRewatch"], entry.last, "netflix", "netflix")
            if entry.ratings:
                format, value, at = entry.ratings[-1]
                table = w["netflixThumbs"] if format == "thumbs" else w["netflixStars"]
                weight = float(table.get(str(value), 0))
                self.add(state, f"netflix_{format}_{value}", weight, at, "netflix_explicit", "netflix")
                state.disliked = weight <= -3

    # ---- TV feedback (keeps the profile current) ----

    def add_feedback(self, events):
        w = self.w
        by_title = defaultdict(list)
        for event in events:
            by_title[event.imdb_id].append(event)
        self.coverage["feedbackEvents"] = len(events)
        self.coverage["feedbackTitles"] = len(by_title)
        for imdb_id in sorted(by_title):
            evs = sorted(by_title[imdb_id], key=lambda e: (e.at, e.kind))
            series = any(e.episode() for e in evs)
            state = self.state(imdb_id, "series" if series else None)
            plays = [e for e in evs if e.kind in {"play_started", "progress", "completed", "abandoned"}]
            for e in plays:
                state.touch(e.at)
                state.reach(e.episode(), completed=e.kind == "completed")

            explicit = [e for e in evs if e.kind in {"thumbs_up", "thumbs_down"}]
            if explicit:
                last = explicit[-1]
                up = last.kind == "thumbs_up"
                self.add(state, last.kind, w["thumbsUp"] if up else w["thumbsDown"], last.at, "explicit", "tv")
                state.disliked = not up

            not_interested = [e for e in evs if e.kind == "not_interested"]
            if not_interested:
                last = not_interested[-1]
                cleared = any(e.at > last.at and e.kind in {"thumbs_up", "added_to_library", "play_started", "completed"}
                              for e in evs)
                if not cleared:
                    state.not_interested = True
                    self.add(state, "not_interested", w["notInterested"], last.at, "explicit", "tv")

            membership = [e for e in evs if e.kind in {"added_to_library", "removed"}]
            if membership:
                last = membership[-1]
                if last.kind == "added_to_library":
                    state.in_library = True
                    self.add(state, "added_to_library", w["addedToLibrary"], last.at, "inApp", "tv")
                else:
                    self.add(state, "removed", w["removedFromLibrary"], last.at, "inApp", "tv")

            completed = [e for e in evs if e.kind == "completed"]
            if completed:
                state.watched = True
                videos = {e.video_id or imdb_id for e in completed}
                if series:
                    weight = min(w["completedSeriesMax"], w["completedEpisode"] * len(videos))
                else:
                    weight = w["completedMovie"]
                self.add(state, "completed", weight, completed[-1].at, "inApp", "tv")
                by_video = defaultdict(list)
                for e in completed:
                    by_video[e.video_id or imdb_id].append(e.at)
                if any((max(times) - min(times)).days >= self.settings["rewatchMinDays"] for times in by_video.values()):
                    self.add(state, "rewatch", w["rewatch"], completed[-1].at, "inApp", "tv")
            else:
                starts = [e for e in evs if e.kind == "play_started"]
                if starts:
                    self.add(state, "play_started", w["playStarted"], starts[-1].at, "inApp", "tv")
                ratios = [(e.position_ms / e.duration_ms, e.at) for e in evs
                          if e.kind == "progress" and e.position_ms is not None and e.duration_ms]
                if ratios:
                    ratio, at = max(ratios)
                    self.add(state, "progress", w["progressMax"] * min(1.0, ratio), at, "inApp", "tv")

            abandons = [e for e in evs if e.kind == "abandoned"]
            if abandons:
                last = abandons[-1]
                if not any(e.kind == "completed" and e.at > last.at for e in evs):
                    self._add_abandon(state, last, series, plays)

    def _add_abandon(self, state, event, series, plays):
        w = self.w
        ratio = None
        if event.position_ms is not None and event.duration_ms:
            ratio = event.position_ms / event.duration_ms
        if ratio is not None:
            early = ratio < self.settings["abandonEarlyRatio"]
        else:
            early = event.position_ms is not None and event.position_ms < self.settings["abandonEarlyMinutes"] * 60000
        if series:
            videos = {e.video_id for e in plays if e.video_id}
            first_episode = event.episode() == (1, 1) or len(videos) <= 1
            if early and first_episode:
                state.abandoned = True
                self.add(state, "abandoned_early", w["abandonedEarly"], event.at, "inApp", "tv")
            elif early:
                self.add(state, "abandoned_early_episode", w["abandonedEarlyEpisode"], event.at, "inApp", "tv")
            else:
                self.add(state, "abandoned_late", w["abandonedLate"], event.at, "inApp", "tv")
        else:
            state.watched = True
            kind = "abandoned_early" if early else "abandoned_late"
            self.add(state, kind, w["abandonedEarly"] if early else w["abandonedLate"], event.at, "inApp", "tv")

    # ---- Stremio library ----

    def add_library(self, items):
        w = self.w
        self.coverage["libraryItems"] = len(items)
        for item in sorted(items, key=lambda value: value.get("_id", "")):
            if item.get("removed") or item.get("temp"):
                continue
            type_ = item.get("type") if item.get("type") in {"movie", "series"} else None
            state = self.state(item["_id"], type_, item.get("name") or "")
            info = item.get("state") or {}
            at = parse_datetime(info.get("lastWatched")) or parse_datetime(item.get("_mtime")) or parse_datetime(item.get("_ctime"))
            state.in_library = True
            self.add(state, "library_item", w["libraryItem"], at, "library", "library")
            times = info.get("timesWatched") or 0
            offset = info.get("timeOffset") or 0
            if type_ == "movie":
                if info.get("flaggedWatched") or times > 0:
                    state.watched = True
                    state.touch(at)
                    self.add(state, "library_watched", w["libraryWatchedMovie"], at, "library", "library")
                elif offset > 0:
                    state.touch(at)
                    self.add(state, "library_in_progress", w["libraryInProgress"], at, "library", "library")
            elif type_ == "series":
                state.notifications = not info.get("noNotif")
                match = VIDEO_ID.match(info.get("video_id") or "")
                if times > 0 or (info.get("overallTimeWatched") or 0) > 0 or (match and match.group(2)):
                    state.watched = True
                    state.touch(parse_datetime(info.get("lastWatched")) or parse_datetime(item.get("_ctime")))
                    weight = w["libraryWatchedSeries"] + min(2.0, math.log1p(times)) * 0.5
                    self.add(state, "library_watched", weight, at, "library", "library")
                    if match and match.group(2):
                        state.reach((int(match.group(2)), int(match.group(3))))

    # ---- combine ----

    def finish(self):
        settings = self.settings
        halves = settings["halfLifeDays"]
        activity = {"library_watched", "library_in_progress"}
        in_app = {state.id for state in self.titles.values()
                  if "tv" in state.sources or any(signal.kind in activity for signal in state.signals)}
        bootstrap = settings["bootstrap"]
        factor = 1.0
        if bootstrap["fadeAfterTitles"] > 0:
            factor = max(bootstrap["minFactor"], 1.0 - len(in_app) / bootstrap["fadeAfterTitles"])
        half_life = {"netflix": halves["netflix"], "netflix_explicit": halves["explicit"], "explicit": halves["explicit"],
                     "inApp": halves["inApp"], "library": halves["library"]}
        for state in self.titles.values():
            total = 0.0
            for signal in state.signals:
                weight = signal.weight * decay(signal.at, self.now, half_life[signal.family])
                if signal.family.startswith("netflix"):
                    weight *= factor
                total += weight
            state.weight = total
        return TasteProfile(self.titles, self.config, self.now, netflix_factor=factor,
                            in_app_titles=len(in_app), coverage=self.coverage)


class TasteProfile:
    def __init__(self, titles, config, now, *, netflix_factor=1.0, in_app_titles=0, coverage=None):
        self.titles = titles
        self.config = config
        self.settings = config["profile"]
        self.now = now
        self.netflix_factor = netflix_factor
        self.in_app_titles = in_app_titles
        self.coverage = coverage or {}
        self.raw = {family: {} for family in FAMILIES}
        self.normalized = {family: {} for family in FAMILIES}
        self.disliked_franchises = []
        self.meta = {}

    # ---- queries ----

    def get(self, imdb_id):
        return self.titles.get(imdb_id)

    def exclusion(self, imdb_id):
        """Why this title must not be recommended in discovery rows, or None."""
        state = self.titles.get(imdb_id)
        if state is None:
            return None
        if state.not_interested:
            return "not_interested"
        if state.disliked:
            return "disliked"
        if state.watched:
            return "watched"
        return None

    def franchise_disliked(self, name):
        key = franchise_key(name)
        return any(same_franchise(key, disliked) for disliked in self.disliked_franchises)

    def ranked_titles(self):
        return sorted(self.titles.values(), key=lambda state: (-abs(state.weight), state.id))

    def shows_for_new_episodes(self, limit):
        shows = [
            state for state in self.titles.values()
            if state.type == "series" and state.weight > 0 and state.notifications
            and not (state.disliked or state.not_interested or state.abandoned)
        ]
        shows.sort(key=lambda state: (state.last_at is None, -(state.last_at.timestamp() if state.last_at else 0), state.id))
        return shows[:limit]

    def top_features(self, family, count, positive=True):
        values = self.raw.get(family, {})
        items = [(value, key) for key, value in values.items() if (value > 0) == positive and value != 0]
        items.sort(key=lambda pair: (-abs(pair[0]), pair[1]))
        return [key for _, key in items[:count]]

    def seeds(self, count, min_weight):
        """Strongest positive titles with metadata, skipping near-duplicates in genre."""
        # Near-duplicates go to the back, so seeds(n) is always a prefix of seeds(m) for n < m and
        # the sources and the row builder agree on the seeds.
        distinct, similar = [], []
        candidates = [state for state in self.ranked_titles() if state.weight >= min_weight
                      and not state.disliked and not state.not_interested and self.meta.get(state.id)]
        for state in candidates:
            genres = set(self.meta[state.id].genres)
            if any(_jaccard(genres, set(self.meta[other.id].genres)) >= 0.67 for other in distinct):
                similar.append(state)
            else:
                distinct.append(state)
        return (distinct + similar)[:count]

    # ---- affinities ----

    def compute_affinities(self, meta_for):
        """meta_for(imdb_id) -> object with genres/countries/directors/writers/cast/keywords/year/type, or None."""
        roles = self.settings["personRoleWeights"]
        cast_limit = self.settings["castLimit"]
        sums = {family: defaultdict(float) for family in FAMILIES}
        for state in sorted(self.titles.values(), key=lambda value: value.id):
            meta = meta_for(state.id)
            if meta is None:
                continue
            self.meta[state.id] = meta
            if not state.name:
                state.name = meta.name
            if not state.type:
                state.type = meta.type
            weight = state.weight
            if weight == 0:
                continue
            if meta.genres:
                for genre in meta.genres:
                    sums["genre"][genre] += weight / len(meta.genres)
            people = {}
            for role, names in (("director", meta.directors), ("writer", meta.writers), ("cast", meta.cast[:cast_limit])):
                for person in names:
                    people[person] = max(people.get(person, 0.0), roles[role])
            for person, role_weight in people.items():
                sums["person"][person] += weight * role_weight
            if meta.countries:
                sums["country"][meta.countries[0]] += weight
            if meta.year:
                sums["era"][f"{meta.year // 10 * 10}s"] += weight
            if meta.type:
                sums["type"][meta.type] += weight
            for keyword in meta.keywords:
                sums["keyword"][keyword] += weight / math.sqrt(len(meta.keywords))
        self.raw = {family: dict(values) for family, values in sums.items()}
        for family in FAMILIES:
            self.normalized[family] = _normalize(self.raw[family])
        self._find_disliked_franchises()

    def calibrate_genres(self, pool_genre_counts):
        """Genre affinity relative to how common the genre is in the candidate pool (as the legacy ranker)."""
        adjusted = {genre: value / math.sqrt(5 + pool_genre_counts.get(genre, 0)) for genre, value in self.raw["genre"].items()}
        self.normalized["genre"] = _normalize(adjusted)

    def _find_disliked_franchises(self):
        threshold = self.settings["dislikedFranchiseThreshold"]
        groups = defaultdict(list)
        for state in self.titles.values():
            name = state.name or (self.meta.get(state.id).name if self.meta.get(state.id) else "")
            if name:
                groups[franchise_key(name)].append(state)
        disliked = []
        for key in sorted(groups):
            states = groups[key]
            negative = any(state.disliked or state.not_interested for state in states)
            if negative and sum(state.weight for state in states) <= threshold:
                disliked.append(key)
        self.disliked_franchises = disliked

    def affinity(self, candidate):
        """(score in [-1, 1], [(family, feature, value)] strongest positive contributions)."""
        weights = self.settings["featureWeights"]
        parts = {}
        notes = []
        genre_values = [(self.normalized["genre"].get(genre, 0.0), genre) for genre in candidate.genres]
        if genre_values:
            parts["genre"] = sum(value for value, _ in genre_values) / len(genre_values)
            notes.extend(("genre", genre, value) for value, genre in genre_values if value > 0)
        people = [(self.normalized["person"].get(person, 0.0), person, role)
                  for role, names in (("director", candidate.directors), ("writer", candidate.writers),
                                      ("cast", candidate.cast[:self.settings["castLimit"]]))
                  for person in names]
        if people:
            best = max((value for value, _, _ in people), default=0.0)
            worst = min((value for value, _, _ in people), default=0.0)
            parts["person"] = max(0.0, best) + min(0.0, worst)
            notes.extend((role, person, value) for value, person, role in people if value > 0)
        if candidate.countries:
            parts["country"] = self.normalized["country"].get(candidate.countries[0], 0.0)
        if candidate.year:
            parts["era"] = self.normalized["era"].get(f"{candidate.year // 10 * 10}s", 0.0)
        parts["type"] = self.normalized["type"].get(candidate.type, 0.0)
        if candidate.keywords:
            values = [self.normalized["keyword"].get(keyword, 0.0) for keyword in candidate.keywords]
            parts["keyword"] = max(values) if values else 0.0
        total_weight = sum(weights[family] for family in parts) or 1.0
        score = sum(weights[family] * value for family, value in parts.items()) / total_weight
        notes.sort(key=lambda note: (-note[2], note[0], note[1]))
        return max(-1.0, min(1.0, score)), notes

    def summary(self, limit=8):
        reasons = Counter(filter(None, (self.exclusion(state.id) for state in self.titles.values())))
        recent = sorted((state for state in self.titles.values() if state.last_at), key=lambda s: (s.last_at, s.id), reverse=True)
        return {
            "titles": len(self.titles),
            "positive": sum(1 for state in self.titles.values() if state.weight > 0),
            "negative": sum(1 for state in self.titles.values() if state.weight < 0),
            "excluded": dict(sorted(reasons.items())),
            "inAppTitles": self.in_app_titles,
            "netflixFactor": round(self.netflix_factor, 3),
            "coverage": self.coverage,
            "topGenres": self.top_features("genre", limit),
            "dislikedGenres": self.top_features("genre", 3, positive=False),
            "topPeople": self.top_features("person", limit),
            "topCountries": self.top_features("country", 4),
            "topEras": self.top_features("era", 3),
            "dislikedFranchises": self.disliked_franchises[:limit],
            "recent": [{"id": state.id, "name": state.name, "weight": round(state.weight, 2)} for state in recent[:limit]],
        }


def _normalize(values):
    strongest = max((abs(value) for value in values.values()), default=0.0) or 1.0
    return {key: value / strongest for key, value in values.items()}


def _jaccard(a, b):
    if not a and not b:
        return 0.0
    return len(a & b) / len(a | b)
