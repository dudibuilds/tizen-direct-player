"""IMDb non-commercial datasets: title matching for Netflix labels and a by-id index.

The datasets are downloaded by scripts/refresh-imdb.sh. Files may be gzipped (`title.X.tsv.gz`)
or plain (`title.X.tsv`, used by test fixtures).
"""

import csv
import gzip
import unicodedata
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

TYPES = {"movie": "movie", "tvMovie": "movie", "tvSpecial": "movie",
         "tvSeries": "series", "tvMiniSeries": "series"}


@dataclass(frozen=True)
class Title:
    identifier: str
    kind: str
    name: str
    year: int
    genres: tuple
    rating: float
    votes: int

    def card(self):
        return {
            "id": self.identifier, "type": self.kind, "name": self.name,
            "year": self.year, "genres": self.genres,
        }


def title_key(value):
    letters = unicodedata.normalize("NFKC", value).casefold()
    return " ".join("".join(ch if ch.isalnum() else " " for ch in letters).split())


def series_keys(value):
    parts = value.split(": ")
    return [title_key(": ".join(parts[:i])) for i in range(len(parts) - 1, 0, -1)]


def dataset_path(directory, name):
    directory = Path(directory)
    packed = directory / f"title.{name}.tsv.gz"
    return packed if packed.exists() else directory / f"title.{name}.tsv"


def has_dataset(directory, name):
    return dataset_path(directory, name).exists()


def tsv(directory, name):
    path = dataset_path(directory, name)
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "rt", encoding="utf-8", newline="") as source:
        rows = csv.reader(source, delimiter="\t", quoting=csv.QUOTE_NONE)
        next(rows)
        yield from rows


def load_catalog(directory, wanted, year):
    """Legacy loader used by mac/recommendations.py: (label lookup, eligible recent titles)."""
    votes_by_id = {
        identifier: (float(rating), int(votes))
        for identifier, rating, votes in tsv(directory, "ratings")
    }
    aliases_by_id = defaultdict(set)
    if has_dataset(directory, "akas"):
        for identifier, _, name, region, language, *_ in tsv(directory, "akas"):
            if region in {"US", "GB"} or language == "en":
                key = title_key(name)
                if key in wanted:
                    aliases_by_id[identifier].add(key)

    lookup = defaultdict(list)
    candidates = []
    for row in tsv(directory, "basics"):
        identifier, kind, name, original, adult, start, _, _, genre_list = row
        if kind not in TYPES or adult == "1" or start == "\\N" or genre_list == "\\N":
            continue
        release = int(start)
        keys = {title_key(name), title_key(original)} & wanted
        keys.update(aliases_by_id.get(identifier, ()))
        rating, votes = votes_by_id.get(identifier, (0.0, 0))
        eligible = year - 5 <= release <= year and votes >= 500 and rating >= 5.5
        if not keys and not eligible:
            continue
        item = Title(identifier, TYPES[kind], name, release, tuple(genre_list.split(",")), rating, votes)
        for key in keys:
            lookup[key].append(item)
        if eligible:
            candidates.append(item)
    return lookup, candidates


def choose_title(label, watched_year, lookup):
    options = lookup.get(title_key(label), ())
    options = [item for item in options if item.year <= watched_year]
    if options:
        return choose_unambiguous(options)
    for key in series_keys(label):
        options = [item for item in lookup.get(key, ()) if item.kind == "series" and item.year <= watched_year]
        if options:
            return choose_unambiguous(options)
    return None


def choose_unambiguous(options):
    if len(options) == 1:
        return options[0]
    ranked = sorted(options, key=lambda item: (-item.votes, item.identifier))
    first, second = ranked[:2]
    return first if first.votes >= max(100, second.votes * 4) else None


@dataclass
class ImdbIndex:
    """What the feed needs from the datasets, loaded in one pass over title.basics."""
    lookup: dict = field(default_factory=dict)     # label key -> [Title]
    by_id: dict = field(default_factory=dict)      # tt id -> Title (movie/series with votes >= index floor, or wanted)
    recent: list = field(default_factory=list)     # Titles released since min_year passing the source floor

    def get(self, identifier):
        return self.by_id.get(identifier)


def load_index(directory, *, wanted_keys=(), wanted_ids=(), min_year=None, recent_min_votes=1000,
               recent_min_rating=6.0, index_min_votes=100):
    """Load the label lookup, a by-id index and recent releases.

    by_id keeps movie/series titles with at least `index_min_votes` votes (to join votes and genres
    onto candidates from other sources) plus every id in `wanted_ids`.
    """
    wanted_keys = set(wanted_keys)
    wanted_ids = set(wanted_ids)
    ratings = {}
    for identifier, rating, votes in tsv(directory, "ratings"):
        try:
            ratings[identifier] = (float(rating), int(votes))
        except ValueError:
            continue
    aliases_by_id = defaultdict(set)
    if wanted_keys and has_dataset(directory, "akas"):
        for identifier, _, name, region, language, *_ in tsv(directory, "akas"):
            if region in {"US", "GB"} or language == "en":
                key = title_key(name)
                if key in wanted_keys:
                    aliases_by_id[identifier].add(key)

    index = ImdbIndex(lookup=defaultdict(list))
    for row in tsv(directory, "basics"):
        if len(row) < 9:
            continue
        identifier, kind, name, original, adult, start, _, _, genre_list = row[:9]
        if kind not in TYPES or adult == "1" or start == "\\N":
            continue
        try:
            release = int(start)
        except ValueError:
            continue
        rating, votes = ratings.get(identifier, (0.0, 0))
        keys = {title_key(name), title_key(original)} & wanted_keys if wanted_keys else set()
        keys.update(aliases_by_id.get(identifier, ()))
        keep = bool(keys) or identifier in wanted_ids or votes >= index_min_votes
        if not keep:
            continue
        genres = () if genre_list == "\\N" else tuple(genre_list.split(","))
        item = Title(identifier, TYPES[kind], name, release, genres, rating, votes)
        for key in keys:
            index.lookup[key].append(item)
        if identifier in wanted_ids or votes >= index_min_votes:
            index.by_id[identifier] = item
        if (min_year is not None and release >= min_year and votes >= recent_min_votes
                and rating >= recent_min_rating and genres):
            index.recent.append(item)
    index.lookup = dict(index.lookup)
    index.recent.sort(key=lambda item: item.identifier)
    return index
