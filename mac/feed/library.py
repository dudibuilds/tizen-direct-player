"""Stremio library items (watch state), from a JSON file or the Stremio API (datastoreGet).

Shape per parity C2: {_id, name, type, removed, temp, _ctime, _mtime,
state: {lastWatched, timeWatched, timeOffset, timesWatched, flaggedWatched, duration, video_id, noNotif}}.
"""

import json
from pathlib import Path

from .candidates import is_imdb_id

STREMIO_API = "https://api.strem.io/api/datastoreGet"


def load_file(path):
    with open(path, encoding="utf-8") as source:
        value = json.load(source)
    if isinstance(value, dict):
        value = value.get("result", value.get("items", []))
    return [item for item in value if isinstance(item, dict)] if isinstance(value, list) else []


def fetch(http, auth_key):
    payload = http.post_json(STREMIO_API, {"authKey": auth_key, "collection": "libraryItem", "ids": [], "all": True})
    if isinstance(payload, dict) and payload.get("error"):
        raise ValueError(f"Stremio datastoreGet failed: {payload['error']}")
    result = payload.get("result", payload) if isinstance(payload, dict) else payload
    return [item for item in result if isinstance(item, dict)] if isinstance(result, list) else []


def load(config, http, log):
    """Library items from the configured file and/or account. Never raises; logs and returns []."""
    settings = config.get("stremioLibrary") or {}
    items = []
    if settings.get("path"):
        path = Path(settings["path"]).expanduser()
        try:
            items.extend(load_file(path))
        except (OSError, ValueError) as error:
            log(f"Stremio library file skipped: {error}")
    if settings.get("authKey"):
        try:
            items.extend(fetch(http, settings["authKey"]))
        except Exception as error:  # noqa: BLE001 - the library is optional
            log(f"Stremio library fetch skipped: {type(error).__name__}")
    seen, unique = set(), []
    for item in items:
        identifier = item.get("_id")
        if is_imdb_id(identifier) and identifier not in seen:
            seen.add(identifier)
            unique.append(item)
    return unique
