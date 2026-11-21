"""Agent fallback hook: when the deterministic feed is too thin, ask for curation.

The snapshot is always written first; this only adds a structured "needs_curation" request file
and, when configured, runs a command (for example a headless agent CLI). Off by default.
"""

import os
import subprocess
from pathlib import Path

from . import CURATION_REQUEST_VERSION
from .storage import write_json


def assess(row_counts, thin_because, config):
    """Gaps between the built rows and the configured minimums."""
    settings = config["fallback"]
    gaps = []
    total = sum(row_counts.values())
    if total < settings["minTotalItems"]:
        gaps.append({"row": "*", "have": total, "want": settings["minTotalItems"],
                     "hint": "The whole feed is thin: suggest playable new releases matching the profile."})
    for row, want in sorted(settings.get("minRowItems", {}).items()):
        have = row_counts.get(row, 0)
        if have < want:
            gaps.append({"row": row, "have": have, "want": want, "hint": f"Suggest titles for the {row} row."})
    because_rows = sum(1 for row in row_counts if row.startswith("because-"))
    if because_rows < settings["minBecauseRows"]:
        gaps.append({"row": "because", "have": because_rows, "want": settings["minBecauseRows"],
                     "hint": "Suggest titles related to these seeds.", "thinSeeds": thin_because})
    return gaps


def request(gaps, *, profile_name, generated_at, snapshot_path, profile_summary, rejected, limit):
    return {
        "kind": "needs_curation",
        "version": CURATION_REQUEST_VERSION,
        "generatedAt": generated_at,
        "profile": profile_name,
        "snapshot": str(snapshot_path),
        "gaps": gaps,
        "profileSummary": profile_summary,
        "rejected": [
            {"id": scored.id, "type": scored.candidate.type, "name": scored.candidate.name,
             "reason": reason, "score": round(scored.score, 4)}
            for scored, reason in sorted(rejected, key=lambda pair: (-pair[0].score, pair[0].id))[:limit]
        ],
        "rules": ("Suggest IMDb ids (tt...) for the rows in `gaps`. Titles must be released, not watched or "
                  "rejected by the user, English originals or foreign titles with English audio or preferred-language "
                  "subtitles (anime: Japanese audio with English subtitles), and playable on the RU7100 "
                  "(no AV1, Dolby Vision without HDR10, DTS or TrueHD; at most 6.5 GB)."),
    }


def write_or_clear(path, payload):
    """Write the request when there are gaps; remove a stale one when the feed recovered."""
    path = Path(path)
    if payload is None:
        path.unlink(missing_ok=True)
        return False
    write_json(path, payload)
    return True


def run_command(command, request_path, timeout, log):
    """Run the configured curation command once. Never raises; returns a short status dict."""
    if not command:
        return {"ran": False}
    argv = [command] if isinstance(command, str) else list(command)
    env = {**os.environ, "FEED_CURATION_REQUEST": str(request_path)}
    try:
        completed = subprocess.run(argv, env=env, timeout=timeout, capture_output=True, text=True, check=False)
    except (OSError, subprocess.SubprocessError) as error:
        log(f"Curation command failed to run: {type(error).__name__}: {error}")
        return {"ran": False, "error": type(error).__name__}
    if completed.returncode != 0:
        log(f"Curation command exited with {completed.returncode}")
    return {"ran": True, "exitCode": completed.returncode}
