"""Import Netflix account exports into the Mac's private viewing-history store."""

import argparse
import csv
import io
import json
import sqlite3
import unicodedata
from collections import Counter
from contextlib import closing, contextmanager
from datetime import date, datetime, timezone
from pathlib import Path, PurePosixPath
from zipfile import BadZipFile, ZipFile


DEFAULT_DATABASE = Path(__file__).resolve().parents[1] / "local-data" / "netflix.sqlite"
EXPORT_FILES = {
    "viewingactivity.csv", "netflixviewinghistory.csv", "ratings.csv",
}

SCHEMA = """
CREATE TABLE IF NOT EXISTS viewing_event (
    profile TEXT NOT NULL,
    title TEXT NOT NULL,
    started_at TEXT NOT NULL,
    precision TEXT NOT NULL CHECK (precision IN ('session', 'day')),
    duration_ms INTEGER,
    bookmark_ms INTEGER,
    latest_bookmark INTEGER NOT NULL CHECK (latest_bookmark IN (0, 1)),
    PRIMARY KEY (profile, title, started_at, precision)
) STRICT;
CREATE TABLE IF NOT EXISTS title_rating (
    profile TEXT NOT NULL,
    title TEXT NOT NULL,
    format TEXT NOT NULL CHECK (format IN ('stars', 'thumbs')),
    rated_at TEXT NOT NULL,
    value INTEGER NOT NULL,
    PRIMARY KEY (profile, title, format, rated_at)
) STRICT;
"""


@contextmanager
def export_tables(path):
    if path.suffix.lower() == ".zip":
        with ZipFile(path) as archive:
            tables = [
                info for info in archive.infolist()
                if PurePosixPath(info.filename).name.lower() in EXPORT_FILES and not info.is_dir()
            ]
            if not tables:
                raise ValueError("The ZIP does not contain ViewingActivity.csv or Ratings.csv")
            yield ((PurePosixPath(table.filename).name, archive.open(table)) for table in tables)
    else:
        yield ((path.name, path.open("rb")),)


def clean(value):
    return unicodedata.normalize("NFC", (value or "").strip())


def timestamp(value):
    result = datetime.fromisoformat(value)
    if result.tzinfo is None:
        result = result.replace(tzinfo=timezone.utc)
    return result.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def viewing_day(value, date_order):
    try:
        return date.fromisoformat(value).isoformat()
    except ValueError:
        pass

    first_text, second_text, year_text = value.replace(".", "/").replace("-", "/").split("/")
    first, second, year = map(int, (first_text, second_text, year_text))
    if len(year_text) == 2:
        year += 2000
    elif len(year_text) != 4:
        raise ValueError("Invalid viewing year")
    if date_order is None:
        if first != second and max(first, second) <= 12:
            raise ValueError("Ambiguous viewing dates. Use --date-order mdy or dmy")
        date_order = "dmy" if first > 12 else "mdy"
    month, day = (first, second) if date_order == "mdy" else (second, first)
    return date(year, month, day).isoformat()


def playback_time(value):
    if value.upper() in {"", "N/A", "NOT LATEST VIEW"}:
        return None
    hours, minutes, seconds = value.split(":")
    whole, _, fraction = seconds.replace(",", ".").partition(".")
    if not all(part.isascii() and part.isdecimal() for part in (hours, minutes, whole)):
        raise ValueError("Invalid playback time")
    if int(minutes) >= 60 or int(whole) >= 60 or (fraction and not fraction.isdecimal()):
        raise ValueError("Invalid playback time")
    return ((int(hours) * 60 + int(minutes)) * 60 + int(whole)) * 1000 + int((fraction + "000")[:3])


def read_export(path, *, profile=None, date_order=None, database=None):
    counts = Counter()
    with export_tables(path) as tables:
        for name, table in tables:
            with table, io.TextIOWrapper(table, encoding="utf-8-sig", newline="") as text:
                rows = csv.DictReader(text, strict=True)
                fields = {clean(field).lower() for field in rows.fieldnames or ()}
                if {"title", "start time", "profile name"} <= fields:
                    kind = "session"
                elif {"title", "date"} <= fields:
                    kind = "day"
                elif {"title name", "profile name", "rating type"} <= fields:
                    kind = "rating"
                else:
                    raise ValueError(f"{name} does not have recognizable Netflix headers")

                try:
                    for raw in rows:
                        if None in raw or None in raw.values():
                            raise ValueError("Row has an unexpected number of fields")
                        row = {clean(key).lower(): clean(value) for key, value in raw.items()}
                        owner = row.get("profile name", profile if kind == "day" else "") or ""
                        if profile is None:
                            counts[owner or "(single-profile viewing activity)"] += 1
                        elif owner == profile:
                            save_row(database, profile, row, kind, date_order, counts)
                except (ValueError, csv.Error) as error:
                    raise ValueError(f"{name}, line {rows.line_num}. {error}") from error
    return counts


def save_row(database, profile, row, kind, date_order, counts):
    if kind == "rating":
        thumbs = row["rating type"].lower() in {"thumb", "thumbs"}
        value = row.get("thumbs value" if thumbs else "star value")
        if not value:
            counts["skipped_ratings"] += 1
            return
        event_time = row.get("event utc ts") or row.get("event date")
        if not event_time or not row["title name"]:
            counts["skipped_ratings"] += 1
            return
        result = database.execute("""
            INSERT OR IGNORE INTO title_rating VALUES (?, ?, ?, ?, ?)
        """, (profile, row["title name"], "thumbs" if thumbs else "stars",
              timestamp(event_time), int(value)))
        counts["ratings_added"] += result.rowcount
        return

    title = row["title"]
    if not title:
        counts["skipped_empty_titles"] += 1
        return
    if kind == "day":
        event = (profile, title, viewing_day(row["date"], date_order), kind, None, None, 0)
    else:
        if row.get("supplemental video type", "").upper() not in {"", "N/A"}:
            counts["skipped_previews"] += 1
            return
        attributes = row.get("attributes", "").lower()
        if "view was hidden" in attributes or "autoplayed: user action: none" in attributes:
            counts["skipped_hidden_or_autoplay"] += 1
            return
        latest = row.get("latest bookmark", "")
        latest_position = playback_time(latest) if ":" in latest else None
        bookmark = playback_time(row.get("bookmark", ""))
        event = (profile, title, timestamp(row["start time"]), kind,
                 playback_time(row.get("duration", "")),
                 latest_position if latest_position is not None else bookmark,
                 int(latest.upper() != "NOT LATEST VIEW"))

    result = database.execute("INSERT OR IGNORE INTO viewing_event VALUES (?, ?, ?, ?, ?, ?, ?)", event)
    counts["viewing_events_added"] += result.rowcount


def open_database(path):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if not path.exists():
        try:
            path.touch(mode=0o600, exist_ok=False)
        except FileExistsError:
            pass
    database = sqlite3.connect(path)
    try:
        database.executescript(SCHEMA)
    except BaseException:
        database.close()
        raise
    return database


def import_history(path, profile, date_order, database_path):
    with closing(open_database(database_path)) as database, database:
        counts = read_export(path, profile=profile, date_order=date_order, database=database)
        if not counts:
            raise ValueError("No rows matched that profile. Use the profiles command to check the name")
        for table in ("viewing_event", "title_rating"):
            counts[table + "_total"] = database.execute(
                f"SELECT count(*) FROM {table} WHERE profile = ?", (profile,)
            ).fetchone()[0]
        return counts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subcommands = parser.add_subparsers(dest="command", required=True)
    profiles = subcommands.add_parser("profiles", help="List profile names and counts without importing")
    ingest = subcommands.add_parser("import", help="Import the selected profile from a Netflix CSV or ZIP")
    for command in (profiles, ingest):
        command.add_argument("export", type=Path)
    ingest.add_argument("--profile", required=True)
    ingest.add_argument("--date-order", choices=("mdy", "dmy"))
    ingest.add_argument("--database", type=Path, default=DEFAULT_DATABASE)
    args = parser.parse_args()

    try:
        if args.command == "profiles":
            report = read_export(args.export)
        else:
            profile = clean(args.profile)
            if not profile:
                raise ValueError("Profile must not be empty")
            report = import_history(args.export, profile, args.date_order, args.database)
    except (ValueError, OSError, BadZipFile, sqlite3.Error) as error:
        parser.exit(1, f"{error}\n")
    print(json.dumps(report, ensure_ascii=False, sort_keys=True))


if __name__ == "__main__":
    main()
