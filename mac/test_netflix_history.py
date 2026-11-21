import sqlite3
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

from netflix_history import import_history, read_export


class NetflixHistoryTest(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.database = self.directory / "private" / "history.sqlite"

    def query(self, sql):
        with sqlite3.connect(self.database) as database:
            return database.execute(sql).fetchall()

    def test_viewing_activity_with_two_digit_dates_is_idempotent(self):
        export = self.directory / "NetflIxViewingHistory.csv"
        export.write_text('Title,Date\n"Show, The",11/17/26\n', encoding="utf-8-sig")
        self.assertEqual(import_history(export, "Me", "mdy", self.database)["viewing_events_added"], 1)
        self.assertEqual(import_history(export, "Me", "mdy", self.database)["viewing_events_added"], 0)
        self.assertEqual(
            self.query("SELECT title, started_at, bookmark_ms FROM viewing_event"),
            [("Show, The", "2026-11-17", None)],
        )

    def test_detailed_zip_preserves_bookmarks_and_filters_profiles(self):
        export = self.directory / "report.zip"
        with ZipFile(export, "w") as archive:
            archive.writestr("CONTENT_INTERACTION/ViewingActivity.csv", (
                "Profile Name,Title,Start Time,Duration,Bookmark,Latest Bookmark,Supplemental Video Type,Attributes\n"
                "Me,Show,2026-11-17 16:00:00,00:03:00,00:12:31,Not latest view,,\n"
                "Me,Show,2026-11-17 17:00:00,00:01:32,00:13:01,00:14:03.1,,\n"
                "Me,Show,2026-11-17 18:00:00,00:00:09,00:00:09,,TRAILER,\n"
                "Other,Second Show,2026-11-17 17:00:00,00:01:32,00:13:01,,,\n"
            ))
            archive.writestr("CONTENT_INTERACTION/Ratings.csv", (
                "Profile Name,Title Name,Rating Type,Thumbs Value,Event Utc Ts\n"
                "Me,Show,thumb,3,2026-11-17 16:00:00\n"
            ))
            archive.writestr("PAYMENT/Billing.csv", "ignore this file")
        self.assertEqual(dict(read_export(export)), {"Me": 4, "Other": 1})
        counts = import_history(export, "Me", None, self.database)
        self.assertEqual(counts["viewing_event_total"], 2)
        self.assertEqual(counts["skipped_previews"], 1)
        self.assertEqual(self.query("SELECT value FROM title_rating"), [(3,)])
        self.assertEqual(
            self.query("SELECT bookmark_ms, latest_bookmark FROM viewing_event ORDER BY started_at"),
            [(751_000, 0), (843_100, 1)],
        )

    def test_ambiguous_dates_roll_back_import(self):
        export = self.directory / "history.csv"
        export.write_text("Title,Date\nFirst,11/17/26\nSecond,11/10/26\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Ambiguous viewing dates"):
            import_history(export, "Me", None, self.database)
        self.assertEqual(self.query("SELECT COUNT(*) FROM viewing_event"), [(0,)])
