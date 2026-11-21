"""Snapshot schema check (stdlib only). Used by the builder before writing and by tests.

{version, generatedAt, profile, rows: [{id, title, kind, seed?, items: [{id, type, name, poster?, reason, score,
 year?, genres?, videoId?, released?, playability}]}], report: {...}}
"""

from . import SNAPSHOT_VERSION
from .candidates import is_imdb_id, parse_datetime

PLAYABILITY = {"playable", "unknown", "unchecked"}


def validate(snapshot):
    """List of problems; empty when the snapshot matches the schema."""
    errors = []
    if not isinstance(snapshot, dict):
        return ["snapshot is not an object"]
    if snapshot.get("version") != SNAPSHOT_VERSION:
        errors.append(f"version must be {SNAPSHOT_VERSION}")
    if parse_datetime(snapshot.get("generatedAt")) is None:
        errors.append("generatedAt must be an ISO timestamp")
    rows = snapshot.get("rows")
    if not isinstance(rows, list):
        return errors + ["rows must be a list"]
    seen_rows, seen_items = set(), set()
    for index, row in enumerate(rows):
        where = f"rows[{index}]"
        if not isinstance(row, dict):
            errors.append(f"{where} is not an object")
            continue
        for key in ("id", "title", "kind"):
            if not isinstance(row.get(key), str) or not row[key]:
                errors.append(f"{where}.{key} must be a non-empty string")
        if row.get("id") in seen_rows:
            errors.append(f"{where}.id is duplicated")
        seen_rows.add(row.get("id"))
        items = row.get("items")
        if not isinstance(items, list) or not items:
            errors.append(f"{where}.items must be a non-empty list")
            continue
        for position, item in enumerate(items):
            spot = f"{where}.items[{position}]"
            if not isinstance(item, dict):
                errors.append(f"{spot} is not an object")
                continue
            if not is_imdb_id(item.get("id")):
                errors.append(f"{spot}.id must be an IMDb id")
            if item.get("type") not in {"movie", "series"}:
                errors.append(f"{spot}.type must be movie or series")
            for key in ("name", "reason"):
                if not isinstance(item.get(key), str) or not item[key]:
                    errors.append(f"{spot}.{key} must be a non-empty string")
            if not isinstance(item.get("score"), (int, float)) or isinstance(item.get("score"), bool):
                errors.append(f"{spot}.score must be a number")
            if "poster" in item and not isinstance(item["poster"], str):
                errors.append(f"{spot}.poster must be a string")
            if item.get("playability") not in PLAYABILITY:
                errors.append(f"{spot}.playability must be one of {sorted(PLAYABILITY)}")
            if "videoId" in item and not str(item["videoId"]).startswith(str(item.get("id"))):
                errors.append(f"{spot}.videoId must belong to the title")
            if item.get("id") in seen_items:
                errors.append(f"{spot}.id appears in more than one row")
            seen_items.add(item.get("id"))
    if not isinstance(snapshot.get("report"), dict):
        errors.append("report must be an object")
    return errors
