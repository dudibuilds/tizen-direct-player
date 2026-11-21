"""Legacy single-list ranker (schemaVersion 1), kept for mcp/recommendation-server.mjs.

`python3 mac/recommendations.py --profile X` runs this module unchanged in behaviour: same
arguments, same `{schemaVersion, items, ...}` snapshot at local-data/recommendations.json and the
same one-line JSON summary on stdout. The rule-based multi-row feed is `python3 -m mac.feed build`.
"""

import argparse
import json
import math
import sqlite3
from collections import Counter
from contextlib import closing
from datetime import date, datetime, timezone
from pathlib import Path

from mac.netflix_history import DEFAULT_DATABASE, clean

from .imdb import Title, choose_title, load_catalog, series_keys, title_key  # noqa: F401 (re-exported)
from .storage import write_json

DATA = DEFAULT_DATABASE.parent


def read_profile(database_path, profile):
    with closing(sqlite3.connect(database_path.as_uri() + "?mode=ro", uri=True)) as database:
        history = database.execute("""
            SELECT title, min(started_at), max(started_at), count(DISTINCT substr(started_at, 1, 10))
            FROM viewing_event WHERE profile = ? GROUP BY title
        """, (profile,)).fetchall()
        ratings = database.execute("""
            SELECT title, format, value, rated_at
            FROM title_rating WHERE profile = ? ORDER BY rated_at, format
        """, (profile,)).fetchall()
    return history, ratings


def rating_weight(format, value):
    if format == "thumbs":
        return {1: -3, 2: 3, 3: 5}.get(value, 0)
    return {-1: -3, 1: -3, 2: -1, 3: 1, 4: 3, 5: 5}.get(value, 0)


def build_snapshot(history, ratings, lookup, candidates, now, limit):
    matched = {}
    item_views = Counter()
    recent = {}
    excluded = set()
    for label, first, last, views in history:
        item = choose_title(label, int(first[:4]), lookup)
        if item is None:
            continue
        matched[label] = item
        excluded.add(item.identifier)
        item_views[item] += views
        recent[item] = max(last, recent.get(item, ""))

    explicit = {}
    for label, format, value, at in ratings:
        item = matched.get(label) or choose_title(label, int(at[:4]), lookup)
        if item is None:
            continue
        weight = rating_weight(format, value)
        if weight < 0:
            excluded.add(item.identifier)
        explicit[item] = weight

    baseline = Counter(genre for item in candidates for genre in item.genres)
    preferences = Counter()
    for item in item_views.keys() | explicit.keys():
        weight = 0.0
        if item in recent:
            age = (now.date() - date.fromisoformat(recent[item][:10])).days
            weight = math.log1p(item_views[item]) / (1 + max(0, age) / 540)
        weight += explicit.get(item, 0)
        for genre in item.genres:
            preferences[genre] += weight / len(item.genres)
    affinity = {
        genre: preference / math.sqrt(5 + baseline[genre])
        for genre, preference in preferences.items()
    }
    strongest = max((abs(score) for score in affinity.values()), default=0) or 1

    ranking = []
    for item in candidates:
        if item.identifier in excluded:
            continue
        matches = sorted(item.genres, key=lambda genre: affinity.get(genre, 0), reverse=True)
        genre_score = sum(affinity.get(genre, 0) for genre in matches) / (strongest * len(matches))
        quality = (item.rating - 5.5) / 4.5
        confidence = min(1, math.log10(item.votes) / 5)
        freshness = max(0, 1 - (now.year - item.year) / 5)
        score = 0.55 * genre_score + 0.25 * quality + 0.12 * confidence + 0.08 * freshness
        reason = [genre for genre in matches[:2] if affinity.get(genre, 0) > 0]
        ranking.append((score, item.identifier, item, reason))

    ranked = sorted(ranking, reverse=True)
    return {
        "schemaVersion": 1,
        "generatedAt": now.isoformat(),
        "historyCoverage": {
            "viewingLabels": len(history), "matchedLabels": len(matched),
            "matchedTitles": len(item_views), "ratingsUsed": len(explicit),
        },
        "items": [
            {**item.card(), "score": round(score, 4), "matchedGenres": reason}
            for score, _, item, reason in ranked[:limit]
        ],
        "recentlyWatched": [
            {**item.card(), "lastWatchedAt": recent[item]}
            for item in sorted(recent, key=recent.get, reverse=True)[:20]
        ],
    }


def write_snapshot(path, snapshot):
    write_json(Path(path), snapshot)


def main(argv=None):
    parser = argparse.ArgumentParser(description="Rank new movies and series using local Netflix history and public IMDb metadata.")
    parser.add_argument("--profile", required=True)
    parser.add_argument("--database", type=Path, default=DEFAULT_DATABASE)
    parser.add_argument("--imdb", type=Path, default=DATA / "imdb")
    parser.add_argument("--output", type=Path, default=DATA / "recommendations.json")
    parser.add_argument("--limit", type=int, default=60)
    args = parser.parse_args(argv)

    try:
        history, ratings = read_profile(args.database.resolve(), clean(args.profile))
        if not history and not ratings:
            raise ValueError("Import Netflix viewing activity or ratings for the selected profile first.")
        labels = {row[0] for row in history} | {row[0] for row in ratings}
        wanted = {key for label in labels for key in [title_key(label), *series_keys(label)]}
        now = datetime.now(timezone.utc)
        lookup, candidates = load_catalog(args.imdb, wanted, now.year)
        snapshot = build_snapshot(history, ratings, lookup, candidates, now, max(1, args.limit))
        write_snapshot(args.output, snapshot)
    except (OSError, ValueError, sqlite3.Error) as error:
        parser.exit(1, f"{error}\n")
    print(json.dumps(snapshot["historyCoverage"], sort_keys=True))
