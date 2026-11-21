"""Shared fixtures for the feed tests. Everything is offline: HTTP is answered from fixtures/."""

import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from mac.feed.language import set_preferred

from mac.feed import config as feed_config
from mac.feed.builder import BuildOptions, FeedBuilder
from mac.feed.candidates import Candidate
from mac.feed.feedback import parse_event
from mac.feed.http import FixtureHttp
from mac.feed.profile import ProfileBuilder
from mac.netflix_history import import_history

FIXTURES = Path(__file__).resolve().parent / "fixtures"
NOW = datetime(2026, 11, 18, 4, 0, tzinfo=timezone.utc)
PROFILE = "Viewer"


def fixture_config(**overrides):
    """Defaults + fixtures/config.json + overrides (deep-merged), with no keys from the environment."""
    with open(FIXTURES / "config.json", encoding="utf-8") as source:
        data = json.load(source)
    config = feed_config.merge(feed_config.merge(feed_config.load(None, environ={}), data), overrides)
    language = config["filters"]["language"]
    set_preferred(language.get("preferred", "en"), language.get("preferredCountries", ()))
    return config


def fixture_http(routes=None):
    if routes is None:
        return FixtureHttp(FIXTURES)
    return FixtureHttp(FIXTURES, routes)


def event(imdb_id, kind, at, video_id=None, position_ms=None, duration_ms=None):
    raw = {"imdbId": imdb_id, "kind": kind, "at": at}
    if video_id:
        raw["videoId"] = video_id
    if position_ms is not None:
        raw["positionMs"] = position_ms
    if duration_ms is not None:
        raw["durationMs"] = duration_ms
    parsed = parse_event(raw)
    assert parsed is not None, raw
    return parsed


def candidate(identifier="tt0000001", type_="movie", **fields):
    fields.setdefault("name", f"Title {identifier}")
    return Candidate(id=identifier, type=type_, **fields)


def empty_profile(config=None, now=NOW):
    return ProfileBuilder(config or fixture_config(), now).finish()


class TempDirTest(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tmp = Path(temporary.name)

    def netflix_database(self):
        database = self.tmp / "netflix.sqlite"
        import_history(FIXTURES / "netflix" / "ViewingActivity.csv", PROFILE, None, database)
        import_history(FIXTURES / "netflix" / "Ratings.csv", PROFILE, None, database)
        return database

    def build(self, config=None, http=None, *, feedback=True, library=True, database=True, **options):
        config = config or fixture_config()
        config["feedbackLog"] = str(self.tmp / "no-log.jsonl")
        if library:
            config["stremioLibrary"]["path"] = str(FIXTURES / "library.json")
        self.http = http or fixture_http()
        imdb_dir = options.pop("imdb_dir", FIXTURES / "imdb")
        build_options = BuildOptions(
            profile=PROFILE, config=config, http=self.http, now=NOW, output=self.tmp / "feed.json",
            database=self.netflix_database() if database else self.tmp / "missing.sqlite",
            imdb_dir=imdb_dir, feedback_paths=[FIXTURES / "feedback.jsonl"] if feedback else [],
            cache_dir=self.tmp / "cache", curation_path=self.tmp / "curation.json", log=lambda message: None,
            **options,
        )
        return FeedBuilder(build_options).build()
