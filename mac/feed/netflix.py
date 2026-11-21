"""Read the imported Netflix history (read-only) and match labels to IMDb titles.

The Netflix import only bootstraps the taste profile; its weight fades as in-app feedback grows.
"""

import re
import sqlite3
from collections import defaultdict
from contextlib import closing
from dataclasses import dataclass, field
from pathlib import Path

from .candidates import parse_datetime
from .imdb import choose_title, series_keys, title_key

SEASON = re.compile(r"\b(?:Season|Series|Part|Volume|Chapter|Book)\s+(\d{1,3})\b", re.IGNORECASE)
LIMITED = re.compile(r"\b(?:Limited Series|Miniseries)\b", re.IGNORECASE)


@dataclass
class NetflixTitle:
    title: object                                  # imdb.Title
    labels: set = field(default_factory=set)
    days: set = field(default_factory=set)         # distinct ISO dates watched
    sessions: int = 0
    total_ms: int = 0
    max_bookmark_ms: int = 0
    has_durations: bool = False
    first: object = None                           # datetime
    last: object = None
    seasons: dict = field(default_factory=dict)    # season -> set of episode labels
    ratings: list = field(default_factory=list)    # [(format, value, datetime)] oldest first

    @property
    def max_season(self):
        return max(self.seasons) if self.seasons else None


@dataclass
class NetflixHistory:
    titles: dict = field(default_factory=dict)     # imdb id -> NetflixTitle
    labels: int = 0
    matched_labels: int = 0
    ratings: int = 0
    matched_ratings: int = 0

    def coverage(self):
        return {
            "viewingLabels": self.labels, "matchedLabels": self.matched_labels,
            "matchedTitles": len(self.titles), "ratings": self.ratings, "matchedRatings": self.matched_ratings,
        }


def read_rows(database_path, profile):
    """(viewing rows, rating rows) for one profile, opened read-only."""
    path = Path(database_path).resolve()
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as database:
        views = database.execute("""
            SELECT title, started_at, precision, duration_ms, bookmark_ms
            FROM viewing_event WHERE profile = ? ORDER BY title, started_at
        """, (profile,)).fetchall()
        ratings = database.execute("""
            SELECT title, format, value, rated_at
            FROM title_rating WHERE profile = ? ORDER BY rated_at, format, title
        """, (profile,)).fetchall()
    return views, ratings


def wanted_keys(views, ratings):
    labels = {row[0] for row in views} | {row[0] for row in ratings}
    return {key for label in labels for key in [title_key(label), *series_keys(label)]}


def season_of(label, title):
    """Season number and episode label parsed from 'Show: Season 2: Episode' style labels."""
    match = SEASON.search(label)
    if match:
        return int(match.group(1)), label[match.end():].strip(" :") or label
    if LIMITED.search(label):
        return 1, LIMITED.split(label, 1)[-1].strip(" :") or label
    if title.kind == "series" and ": " in label:
        return None, label.split(": ", 1)[1]
    return None, None


def aggregate(views, ratings, lookup):
    history = NetflixHistory()
    by_label = defaultdict(list)
    for label, started_at, precision, duration_ms, bookmark_ms in views:
        by_label[label].append((started_at, precision, duration_ms, bookmark_ms))
    history.labels = len(by_label)
    matched_by_label = {}
    for label in sorted(by_label):
        rows = by_label[label]
        first_year = int(rows[0][0][:4])
        item = choose_title(label, first_year, lookup)
        if item is None:
            continue
        history.matched_labels += 1
        matched_by_label[label] = item
        entry = history.titles.setdefault(item.identifier, NetflixTitle(item))
        entry.labels.add(label)
        season, episode_label = season_of(label, item)
        if season is not None:
            entry.seasons.setdefault(season, set()).add(episode_label)
        for started_at, precision, duration_ms, bookmark_ms in rows:
            at = parse_datetime(started_at)
            entry.days.add(started_at[:10])
            entry.sessions += 1
            if precision == "session" and duration_ms is not None:
                entry.has_durations = True
                entry.total_ms += duration_ms or 0
                entry.max_bookmark_ms = max(entry.max_bookmark_ms, bookmark_ms or 0)
            if at is not None:
                entry.first = at if entry.first is None or at < entry.first else entry.first
                entry.last = at if entry.last is None or at > entry.last else entry.last
    history.ratings = len(ratings)
    for label, format, value, rated_at in ratings:
        at = parse_datetime(rated_at)
        item = matched_by_label.get(label) or choose_title(label, int(rated_at[:4]), lookup)
        if item is None or at is None:
            continue
        history.matched_ratings += 1
        entry = history.titles.setdefault(item.identifier, NetflixTitle(item))
        entry.ratings.append((format, value, at))
        if entry.last is None or at > entry.last:
            entry.last = at
    return history


def load_history(database_path, profile, lookup):
    views, ratings = read_rows(database_path, profile)
    return aggregate(views, ratings, lookup)
