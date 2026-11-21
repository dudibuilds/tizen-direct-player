"""Feed configuration: built-in defaults deep-merged with an optional JSON file and env keys.

The file (default local-data/feed-config.json, ignored by git) may hold API keys. Keys can also
come from the environment so they never need to be written to disk:
RU7100_TMDB_API_KEY, RU7100_TMDB_READ_TOKEN, RU7100_TRAKT_CLIENT_ID, RU7100_STREMIO_AUTH_KEY.
"""

import copy
import json
import os
from pathlib import Path
from .language import set_preferred

DEFAULTS = {
    "netflixDatabase": None,          # default local-data/netflix.sqlite
    "imdbDir": None,                  # default local-data/imdb
    "feedbackLog": None,              # default local-data/tv-feedback.jsonl
    "curationRequest": None,          # default local-data/feed-needs-curation.json
    "cacheDir": None,                 # default local-data/feed-cache
    "stremioLibrary": {"path": None, "authKey": None},
    "http": {
        "userAgent": "ru7100-feed/1.0",
        "timeoutSeconds": 20,
        "defaultIntervalSeconds": 0.1,
        "hostIntervals": {
            "torrentio.strem.fun": 1.5,
            "opensubtitles-v3.strem.io": 1.0,
            "api.trakt.tv": 0.4,
            "api.themoviedb.org": 0.05,
        },
    },
    "sources": {
        "cinemeta": {
            "enabled": True,
            "baseUrl": "https://v3-cinemeta.strem.io",
            "catalogs": ["top", "imdbRating"],
            "yearCatalogYears": 2,       # Cinemeta "New" (year) catalog for this year and the previous one
            "genreCatalogs": 3,          # also browse the user's N strongest genres
            "pages": 1,
            "ttlHours": 12,
            "budgetSeconds": 120,
        },
        "catalogAddons": {
            # Each entry: "https://.../manifest.json" or
            # {"url": "...", "catalogs": ["id", ...], "label": "Netflix", "trustedNew": true}
            "enabled": True,
            "manifests": [],
            "pages": 1,
            "ttlHours": 12,
            "budgetSeconds": 120,
        },
        "imdb": {"enabled": True, "years": 2, "minVotes": 1500, "minRating": 6.3, "limit": 400},
        "newEpisodes": {
            "enabled": True,
            "days": 45,                  # how far back a release still counts as new
            "maxShows": 200,
            "ttlHours": 6,
            "budgetSeconds": 120,
        },
        "tmdb": {
            "enabled": True,
            "apiKey": None,
            "readToken": None,
            "region": "US",
            "seeds": 6,
            "discoverDays": 180,
            "originalLanguages": ["en"],
            "maxDetailLookups": 300,
            "ttlHours": 24,
            "budgetSeconds": 180,
        },
        "trakt": {
            "enabled": True,
            "clientId": None,
            "seeds": 6,
            "limit": 40,
            "ttlHours": 24,
            "budgetSeconds": 120,
        },
    },
    "profile": {
        "halfLifeDays": {"netflix": 540, "inApp": 180, "explicit": 720, "library": 365},
        "bootstrap": {"fadeAfterTitles": 60, "minFactor": 0.2},
        "weights": {
            "netflixMovieCompleted": 2.0,
            "netflixMoviePartial": 0.5,
            "netflixMovieWatched": 1.0,
            "netflixMovieAbandoned": -1.5,
            "netflixRewatch": 1.5,
            "netflixSeriesBase": 1.0,
            "netflixSeriesPerLog2Day": 0.75,
            "netflixSeriesMax": 4.0,
            "netflixSeriesSampled": -0.5,
            "netflixThumbs": {"1": -4.0, "2": 3.0, "3": 5.0},
            "netflixStars": {"1": -4.0, "2": -1.5, "3": 1.0, "4": 3.0, "5": 5.0},
            "playStarted": 0.3,
            "progressMax": 0.5,
            "completedMovie": 2.0,
            "completedEpisode": 0.6,
            "completedSeriesMax": 4.0,
            "rewatch": 2.0,
            "abandonedEarly": -2.0,
            "abandonedEarlyEpisode": -0.75,
            "abandonedLate": -0.5,
            "thumbsUp": 4.0,
            "thumbsDown": -5.0,
            "notInterested": -2.0,
            "addedToLibrary": 1.5,
            "removedFromLibrary": -0.5,
            "libraryItem": 1.0,
            "libraryWatchedMovie": 2.0,
            "libraryWatchedSeries": 1.0,
            "libraryInProgress": 0.5,
        },
        "abandonEarlyRatio": 0.25,
        "abandonEarlyMinutes": 15,
        "rewatchMinDays": 7,
        "featureWeights": {"genre": 0.5, "person": 0.2, "country": 0.1, "era": 0.05, "type": 0.1, "keyword": 0.05},
        "personRoleWeights": {"director": 1.0, "writer": 0.6, "cast": 0.5},
        "castLimit": 4,
        "dislikedFranchiseThreshold": -1.5,
    },
    "enrich": {"maxHistoryTitles": 150, "maxCandidates": 150, "ttlHours": 336},
    "filters": {
        "releaseWindowYears": 3,
        "becauseWindowYears": 12,
        "topPicksWindowYears": 5,
        "minVotes": {"movie": 3000, "series": 1500},
        "minVotesFloor": 100,
        "minRating": {"base": 6.4, "new": 6.0},
        "recencyRampDays": 365,
        "unratedGraceDays": 45,
        "externalVotesFactor": 0.1,
        "blockedGenres": ["Talk-Show", "News", "Game-Show", "Adult"],
        "language": {
            # the viewer's preferred language (ISO 639-1) and the countries whose originals are in it;
            # "en" means English only.
            "preferred": "en",
            "preferredCountries": [],
            # foreign originals: "dub" (English/multi audio stream), "preferred_subs" (subtitles in the
            # preferred language exist), "dub_or_preferred_subs" or "exclude". English, preferred-language
            # and anime originals use the parity tiers.
            "foreign": "dub",
            "foreignPenalty": 0.08,
        },
    },
    "ranking": {
        "weights": {"affinity": 0.45, "quality": 0.25, "freshness": 0.12, "sources": 0.10, "popularity": 0.08},
        "freshnessHalfLifeDays": 120,
        "mmrLambda": 0.75,
        "maxDominantGenreShare": 0.4,
        "maxPerFranchise": 1,
    },
    "rows": {
        "rowSize": 20,
        "newDays": 180,
        "becauseRows": 4,
        "becauseRowSize": 12,
        "becauseMinItems": 4,
        "becauseMinLink": 0.4,
        "seedMinWeight": 1.5,
        "topPicksMinAffinity": 0.0,     # top picks must match the profile (ignored while the profile is empty)
    },
    "playability": {
        "enabled": True,
        "streamAddonUrl": None,
        "subtitleAddonUrl": "https://opensubtitles-v3.strem.io/manifest.json",
        "maxSizeGb": 6.5,
        "minSeeders": 3,
        "maxChecksPerRun": 200,
        "passTtlHours": 72,
        "failTtlHours": 24,
        "includeUnknown": True,     # network error: show the title, flagged "unknown"
        "includeUnchecked": False,  # check budget exhausted: leave the title out
        "timeoutSeconds": 25,
    },
    "fallback": {
        "minTotalItems": 30,
        "minRowItems": {"new-for-you": 6, "top-picks": 8},
        "minBecauseRows": 2,
        "rejectedLimit": 40,
        "command": None,            # e.g. ["claude", "-p", "..."]; FEED_CURATION_REQUEST points at the request
        "commandTimeoutSeconds": 600,
    },
}

ENV_KEYS = {
    "RU7100_TMDB_API_KEY": ("sources", "tmdb", "apiKey"),
    "RU7100_TMDB_READ_TOKEN": ("sources", "tmdb", "readToken"),
    "RU7100_TRAKT_CLIENT_ID": ("sources", "trakt", "clientId"),
    "RU7100_STREMIO_AUTH_KEY": ("stremioLibrary", "authKey"),
}


def merge(base, override):
    result = copy.deepcopy(base)
    for key, value in (override or {}).items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = merge(result[key], value)
        else:
            result[key] = copy.deepcopy(value)
    return result


def load(path=None, *, environ=None, required=False):
    """Defaults <- file (if present) <- environment keys."""
    data = {}
    if path is not None:
        path = Path(path)
        if path.exists():
            with open(path, encoding="utf-8") as source:
                data = json.load(source)
            if not isinstance(data, dict):
                raise ValueError(f"{path} must contain a JSON object")
        elif required:
            raise ValueError(f"Config file not found: {path}")
    config = merge(DEFAULTS, data)
    environ = os.environ if environ is None else environ
    for name, keys in ENV_KEYS.items():
        if environ.get(name):
            target = config
            for key in keys[:-1]:
                target = target.setdefault(key, {})
            target[keys[-1]] = environ[name]
    language = config["filters"]["language"]
    set_preferred(language.get("preferred", "en"), language.get("preferredCountries", ()))
    return config
