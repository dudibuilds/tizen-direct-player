"""Atomic private file writes and small JSON helpers."""

import json
import os
import tempfile
from pathlib import Path


def write_text(path, text):
    """Write text atomically (temp file + rename) with owner-only permissions."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent, delete=False) as output:
        pending = Path(output.name)
        try:
            output.write(text)
            output.flush()
            os.chmod(pending, 0o600)
            os.replace(pending, path)
        finally:
            pending.unlink(missing_ok=True)


def write_json(path, value, *, indent=2):
    write_text(path, json.dumps(value, ensure_ascii=False, indent=indent) + "\n")


def read_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as source:
            return json.load(source)
    except FileNotFoundError:
        return default
