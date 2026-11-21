"""In-app TV feedback events: parsing, validation and the append-only local log.

Event shape (JSON array or JSON lines):
  {"imdbId": "tt…", "videoId": "tt…:S:E"?, "kind": KIND, "positionMs"?, "durationMs"?, "at": ISO-8601 or epoch ms}
"""

import json
import re
from dataclasses import dataclass
from pathlib import Path

from .candidates import is_imdb_id, parse_datetime

KINDS = {
    "play_started", "progress", "completed", "abandoned", "thumbs_up", "thumbs_down",
    "not_interested", "added_to_library", "removed",
}
VIDEO_ID = re.compile(r"^(tt\d+)(?::(\d+):(\d+))?$")


@dataclass(frozen=True)
class Event:
    imdb_id: str
    kind: str
    at: object                 # aware datetime
    video_id: str = None
    position_ms: int = None
    duration_ms: int = None

    def key(self):
        return (self.imdb_id, self.video_id or "", self.kind, self.at.isoformat(), self.position_ms or 0)

    def episode(self):
        """(season, episode) from a series video id, else None."""
        match = VIDEO_ID.match(self.video_id or "")
        if match and match.group(2) is not None:
            return int(match.group(2)), int(match.group(3))
        return None

    def to_json(self):
        value = {"imdbId": self.imdb_id, "kind": self.kind, "at": self.at.isoformat().replace("+00:00", "Z")}
        if self.video_id:
            value["videoId"] = self.video_id
        if self.position_ms is not None:
            value["positionMs"] = self.position_ms
        if self.duration_ms is not None:
            value["durationMs"] = self.duration_ms
        return value


def _int(value):
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)) and value >= 0:
        return int(value)
    return None


def parse_event(raw):
    """One raw dict -> Event, or None when it is malformed."""
    if not isinstance(raw, dict):
        return None
    imdb_id = raw.get("imdbId")
    kind = raw.get("kind")
    at = parse_datetime(raw.get("at"))
    if not is_imdb_id(imdb_id) or kind not in KINDS or at is None:
        return None
    video_id = raw.get("videoId")
    if video_id is not None:
        match = VIDEO_ID.match(str(video_id))
        if not match or match.group(1) != imdb_id:
            return None
        video_id = str(video_id)
    return Event(imdb_id, kind, at, video_id, _int(raw.get("positionMs")), _int(raw.get("durationMs")))


def parse_text(text):
    """JSON array, {"events": [...]}, or JSON lines -> (events, rejected count)."""
    text = text.strip()
    if not text:
        return [], 0
    raws = None
    if text[0] in "[{":
        try:
            value = json.loads(text)
            if isinstance(value, list):
                raws = value
            elif isinstance(value, dict) and isinstance(value.get("events"), list):
                raws = value["events"]
            elif isinstance(value, dict):
                raws = [value]
        except json.JSONDecodeError:
            raws = None
    if raws is None:
        raws = []
        for line in text.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                raws.append(json.loads(line))
            except json.JSONDecodeError:
                raws.append(None)
    events, rejected = [], 0
    for raw in raws:
        event = parse_event(raw)
        if event is None:
            rejected += 1
        else:
            events.append(event)
    return events, rejected


def load(paths):
    """Events from every existing path, deduplicated and sorted by time."""
    events, rejected = {}, 0
    for path in paths:
        path = Path(path)
        if not path.exists():
            continue
        parsed, bad = parse_text(path.read_text(encoding="utf-8"))
        rejected += bad
        for event in parsed:
            events.setdefault(event.key(), event)
    ordered = sorted(events.values(), key=lambda event: (event.at, event.imdb_id, event.kind, event.video_id or ""))
    return ordered, rejected


def merge_into_log(log_path, incoming):
    """Append new events to the JSON-lines log, keeping it deduplicated and time-ordered."""
    from .storage import write_text
    existing, _ = load([log_path])
    known = {event.key() for event in existing}
    added = [event for event in incoming if event.key() not in known]
    if added:
        merged = sorted(existing + added, key=lambda event: (event.at, event.imdb_id, event.kind, event.video_id or ""))
        write_text(log_path, "".join(json.dumps(event.to_json(), ensure_ascii=False) + "\n" for event in merged))
    return {"added": len(added), "total": len(existing) + len(added)}
