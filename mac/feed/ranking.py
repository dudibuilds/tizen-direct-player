"""Rule-based filtering, scoring and diversity (MMR) for feed candidates."""

import math
from collections import Counter
from dataclasses import dataclass, field
from datetime import date

from .language import FOREIGN, language_class
from .profile import _jaccard, franchise_key, same_franchise


def age_days(candidate, now):
    """Days since the most relevant release: movie release, series premiere or newest season start."""
    dates = [day for day in (candidate.released, candidate.latest_season_start) if day is not None]
    if dates:
        return (now.date() - max(dates)).days
    if candidate.year:
        start = date(candidate.year, 1, 1)
        end = min(now.date(), date(candidate.year, 12, 31))
        if end < start:
            return -1
        return (now.date() - (start + (end - start) / 2)).days
    return None


def release_year(candidate):
    if candidate.year:
        return candidate.year
    return candidate.released.year if candidate.released else None


@dataclass
class Scored:
    candidate: object
    score: float
    parts: dict = field(default_factory=dict)
    notes: list = field(default_factory=list)       # [(family/role, feature, value)]
    language: str = "unknown"
    dominant_genre: str = "none"
    franchise: str = ""

    @property
    def id(self):
        return self.candidate.id


class Ranker:
    def __init__(self, profile, config, now):
        self.profile = profile
        self.config = config
        self.filters = config["filters"]
        self.ranking = config["ranking"]
        self.now = now

    # ---- filters ----

    def filter(self, candidate):
        """Why the candidate cannot appear in discovery rows, or None."""
        f = self.filters
        if not (candidate.name or "").strip():
            return "no_name"
        excluded = self.profile.exclusion(candidate.id)
        if excluded:
            return excluded
        if candidate.name and self.profile.franchise_disliked(candidate.name):
            return "disliked_franchise"
        blocked = set(f.get("blockedGenres") or ())
        if blocked & set(candidate.genres):
            return "blocked_genre"
        if candidate.released and candidate.released > self.now.date():
            return "unreleased"
        year = release_year(candidate)
        if year is None:
            return "no_release_info"
        if year > self.now.year:
            return "unreleased"
        window = max(f["releaseWindowYears"], f["topPicksWindowYears"])
        if candidate.seeds:
            window = max(window, f["becauseWindowYears"])
        if year < self.now.year - window:
            return "release_window"
        reason = self.quality_gate(candidate)
        if reason:
            return reason
        if language_class(candidate) == FOREIGN and f["language"]["foreign"] == "exclude":
            return "language"
        return None

    def quality_gate(self, candidate):
        """Rating/vote thresholds that scale with recency (new titles need fewer votes)."""
        f = self.filters
        rating, votes, kind = candidate.best_rating()
        age = age_days(candidate, self.now)
        age = max(0, age if age is not None else 10**4)
        ramp = min(1.0, age / f["recencyRampDays"])
        if rating is None:
            vouched = candidate.trusted_new or bool(candidate.seeds) or len(candidate.sources) >= 2
            return None if age <= f["unratedGraceDays"] and vouched else "unrated"
        min_rating = f["minRating"]["new"] + (f["minRating"]["base"] - f["minRating"]["new"]) * ramp
        if rating < min_rating:
            return "low_rating"
        if votes is not None:
            factor = f["externalVotesFactor"] if kind == "external" else 1.0
            base = f["minVotes"].get(candidate.type, f["minVotes"]["movie"]) * factor
            needed = max(f["minVotesFloor"] * factor, base * ramp)
            if votes < needed:
                return "few_votes"
        return None

    # ---- scoring ----

    def score(self, candidate):
        weights = self.ranking["weights"]
        affinity, notes = self.profile.affinity(candidate)
        rating, votes, kind = candidate.best_rating()
        if rating is None:
            quality = 0.35
        else:
            confidence = min(1.0, math.log10(votes + 1) / 4.5) if votes else (0.6 if kind == "imdb" else 0.4)
            quality = max(0.0, min(1.0, (rating - 5.5) / 4.0)) * confidence
        age = age_days(candidate, self.now)
        freshness = 0.5 ** (max(0, age) / self.ranking["freshnessHalfLifeDays"]) if age is not None else 0.0
        families = {source.split(":")[0] for source in candidate.sources}
        sources = min(1.0, 0.3 * len(families) + (0.25 if candidate.trusted_new else 0.0)
                      + (0.2 if candidate.seeds else 0.0))
        best_rank = min((info.get("rank", 50) for info in candidate.sources.values()), default=50)
        popularity = max(0.0, 1.0 - best_rank / 50)
        parts = {"affinity": affinity, "quality": quality, "freshness": freshness, "sources": sources,
                 "popularity": popularity}
        score = sum(weights[name] * value for name, value in parts.items())
        language = language_class(candidate)
        if language == FOREIGN:
            country = candidate.countries[0] if candidate.countries else None
            if self.profile.normalized["country"].get(country, 0.0) <= 0:
                score -= self.filters["language"]["foreignPenalty"]
                parts["foreignPenalty"] = self.filters["language"]["foreignPenalty"]
        genre_affinity = self.profile.normalized["genre"]
        dominant = sorted(candidate.genres, key=lambda genre: (-genre_affinity.get(genre, 0.0), genre))
        return Scored(candidate, round(score, 6), parts, notes, language, dominant[0] if dominant else "none",
                      franchise_key(candidate.name))


def similarity(a, b):
    """0..1 overlap used by MMR: same franchise 1, else genres (0.7) and shared people (0.3)."""
    if a.franchise and same_franchise(a.franchise, b.franchise):
        return 1.0
    x, y = a.candidate, b.candidate
    genres = _jaccard(set(x.genres), set(y.genres))
    if set(x.directors + x.writers) & set(y.directors + y.writers):
        people = 1.0
    elif set(x.cast[:4]) & set(y.cast[:4]):
        people = 0.5
    else:
        people = 0.0
    return 0.7 * genres + 0.3 * people


def mmr_select(items, size, *, lambda_=0.75, max_genre_share=0.4, max_per_franchise=1, accept=None,
               key=lambda item: item.score, rejected=None, limit=250):
    """Greedy maximal-marginal-relevance pick with hard caps on dominant genre and franchise.

    `accept(item)` runs only for an item about to be picked (lazy playability checks); returning
    False skips it. `rejected` (a Counter) collects why items were passed over.
    """
    rejected = rejected if rejected is not None else Counter()
    remaining = sorted(items, key=lambda item: (-key(item), item.id))[:limit]
    genre_cap = max(2, math.ceil(max_genre_share * size))
    genres, franchises = Counter(), Counter()
    selected = []
    while remaining and len(selected) < size:
        best, best_value = None, None
        for item in remaining:
            redundancy = max((similarity(item, chosen) for chosen in selected), default=0.0)
            value = lambda_ * key(item) - (1 - lambda_) * redundancy
            if best_value is None or value > best_value + 1e-12:
                best, best_value = item, value
        remaining.remove(best)
        if genres[best.dominant_genre] >= genre_cap:
            rejected["diversity_genre_cap"] += 1
            continue
        if best.franchise and franchises[best.franchise] >= max_per_franchise:
            rejected["diversity_franchise_cap"] += 1
            continue
        if accept is not None and not accept(best):
            continue
        selected.append(best)
        genres[best.dominant_genre] += 1
        if best.franchise:
            franchises[best.franchise] += 1
    return selected


def content_link(seed, candidate):
    """Keyless "Because you watched" link from shared creators/cast, genres, country and type."""
    creators = set(seed.directors + seed.writers) & set(candidate.directors + candidate.writers)
    cast = set(seed.cast[:5]) & set(candidate.cast[:5])
    people = min(1.0, 0.6 * len(creators) + 0.3 * len(cast))
    genres = _jaccard(set(seed.genres), set(candidate.genres))
    if people == 0 and genres < 0.66:
        return 0.0
    keywords = _jaccard(set(seed.keywords), set(candidate.keywords)) if seed.keywords and candidate.keywords else 0.0
    country = 0.1 if seed.countries and candidate.countries and seed.countries[0] == candidate.countries[0] else 0.0
    same_type = 0.05 if seed.type == candidate.type else 0.0
    return min(1.0, 0.5 * people + 0.4 * genres + 0.2 * keywords + country + same_type)
