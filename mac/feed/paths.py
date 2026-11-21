"""Default locations. Everything private lives in the ignored local-data/ folder."""

import os
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[2]
DATA = Path(os.environ.get("RU7100_DATA_DIR") or PROJECT / "local-data").resolve()

DEFAULT_CONFIG = DATA / "feed-config.json"
DEFAULT_OUTPUT = DATA / "recommendation-feed.json"
DEFAULT_CACHE = DATA / "feed-cache"
DEFAULT_FEEDBACK_LOG = DATA / "tv-feedback.jsonl"
DEFAULT_CURATION_REQUEST = DATA / "feed-needs-curation.json"
DEFAULT_IMDB = DATA / "imdb"
DEFAULT_DATABASE = DATA / "netflix.sqlite"
