"""Candidate titles gathered from every source, merged by IMDb id."""

import re
from dataclasses import dataclass, field
from datetime import date, datetime, timezone

IMDB_ID = re.compile(r"^tt\d{5,10}$")
YEAR = re.compile(r"(\d{4})")
METAHUB_POSTER = "https://images.metahub.space/poster/small/{id}/img"


def is_imdb_id(value):
    return isinstance(value, str) and bool(IMDB_ID.match(value))


def poster_for(identifier):
    return METAHUB_POSTER.format(id=identifier)


def parse_date(value):
    """ISO date or datetime string (or epoch ms) -> date, else None."""
    if value is None or value == "":
        return None
    if isinstance(value, (int, float)):
        return datetime.fromtimestamp(value / 1000, tz=timezone.utc).date()
    text = str(value).strip()
    try:
        return date.fromisoformat(text[:10])
    except ValueError:
        return None


def parse_datetime(value):
    """ISO datetime/date string or epoch ms -> aware UTC datetime, else None."""
    if value is None or value == "":
        return None
    if isinstance(value, (int, float)):
        return datetime.fromtimestamp(value / 1000, tz=timezone.utc)
    text = str(value).strip().replace("Z", "+00:00")
    try:
        result = datetime.fromisoformat(text)
    except ValueError:
        day = parse_date(text)
        if day is None:
            return None
        result = datetime(day.year, day.month, day.day)
    if result.tzinfo is None:
        result = result.replace(tzinfo=timezone.utc)
    return result.astimezone(timezone.utc)


def parse_year(value):
    match = YEAR.search(str(value or ""))
    return int(match.group(1)) if match else None


@dataclass
class Candidate:
    id: str
    type: str                                   # movie | series
    name: str = ""
    year: int = None
    released: date = None                       # movie release / series premiere
    latest_season_start: date = None            # series: first episode date of the newest released season
    latest_release: date = None                 # series: newest released episode date
    genres: tuple = ()
    countries: tuple = ()
    directors: tuple = ()
    writers: tuple = ()
    cast: tuple = ()
    keywords: tuple = ()
    original_language: str = None               # ISO 639-1 from TMDB/Trakt when known
    rating: float = None                        # IMDb rating (Cinemeta or IMDb datasets)
    votes: int = None                           # IMDb votes (IMDb datasets)
    ext_rating: float = None                    # TMDB/Trakt rating when no IMDb rating is known
    ext_votes: int = None
    poster: str = None
    first_video_id: str = None                  # series: video to check/play first (S1E1)
    sources: dict = field(default_factory=dict)   # source -> {"rank": int, "label": str, ...}
    seeds: dict = field(default_factory=dict)     # seed imdb id -> link strength from related-title APIs
    trusted_new: bool = False                   # listed by a curated "new releases" catalog
    new_episode: dict = None                    # {"videoId", "season", "episode", "released", "count", "reason"}
    enriched: bool = False                      # has Cinemeta/TMDB detail (people, country)

    def merge(self, other):
        """Fold another record of the same title into this one, keeping known values."""
        for name in ("name", "year", "released", "latest_season_start", "latest_release", "original_language",
                     "rating", "votes", "ext_rating", "ext_votes", "poster", "first_video_id", "new_episode"):
            if getattr(self, name) in (None, "") and getattr(other, name) not in (None, ""):
                setattr(self, name, getattr(other, name))
        for name in ("genres", "countries", "directors", "writers", "cast", "keywords"):
            if not getattr(self, name) and getattr(other, name):
                setattr(self, name, getattr(other, name))
        if other.latest_release and (self.latest_release is None or other.latest_release > self.latest_release):
            self.latest_release = other.latest_release
        if other.latest_season_start and (self.latest_season_start is None
                                          or other.latest_season_start > self.latest_season_start):
            self.latest_season_start = other.latest_season_start
        for source, info in other.sources.items():
            current = self.sources.get(source)
            if current is None or info.get("rank", 10**6) < current.get("rank", 10**6):
                self.sources[source] = info
        for seed, strength in other.seeds.items():
            self.seeds[seed] = max(strength, self.seeds.get(seed, 0.0))
        self.trusted_new = self.trusted_new or other.trusted_new
        self.enriched = self.enriched or other.enriched
        return self

    def best_rating(self):
        if self.rating is not None:
            return self.rating, self.votes, "imdb"
        if self.ext_rating is not None:
            return self.ext_rating, self.ext_votes, "external"
        return None, None, None

    def video_for_check(self):
        if self.new_episode:
            return self.new_episode["videoId"]
        if self.type == "series":
            return self.first_video_id or f"{self.id}:1:1"
        return self.id


class CandidatePool:
    def __init__(self):
        self.items = {}

    def add(self, candidate):
        if candidate is None or not is_imdb_id(candidate.id):
            return None
        current = self.items.get(candidate.id)
        if current is None:
            self.items[candidate.id] = candidate
            return candidate
        return current.merge(candidate)

    def extend(self, candidates):
        for candidate in candidates:
            self.add(candidate)

    def __iter__(self):
        return iter(self.items[key] for key in sorted(self.items))

    def __len__(self):
        return len(self.items)

    def get(self, identifier):
        return self.items.get(identifier)
