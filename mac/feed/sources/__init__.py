"""Candidate sources. Add a source by subclassing Source and listing it in ALL_SOURCES."""

from .base import Source, SourceContext, SourceReport, run_sources
from .cinemeta import CatalogAddonSource, CinemetaSource
from .imdb_recent import ImdbRecentSource
from .new_episodes import NewEpisodesSource
from .tmdb import TmdbSource
from .trakt import TraktSource

ALL_SOURCES = (NewEpisodesSource, CinemetaSource, CatalogAddonSource, ImdbRecentSource, TmdbSource, TraktSource)


def default_sources():
    return [source() for source in ALL_SOURCES]


__all__ = ["ALL_SOURCES", "Source", "SourceContext", "SourceReport", "default_sources", "run_sources"]
