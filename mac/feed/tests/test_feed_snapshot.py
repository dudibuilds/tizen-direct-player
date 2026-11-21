import json
import subprocess
import sys
import unittest

from mac.feed import fallback, schema
from mac.feed.paths import PROJECT
from mac.feed.tests.support import FIXTURES, PROFILE, TempDirTest, fixture_config


def items_by_row(snapshot):
    return {row["id"]: [item["id"] for item in row["items"]] for row in snapshot["rows"]}


class SnapshotTest(TempDirTest):
    def test_schema_rows_and_reasons(self):
        snapshot, details, curation = self.build()
        self.assertEqual(schema.validate(snapshot), [])
        self.assertEqual(snapshot["version"], 1)
        self.assertEqual(snapshot["generatedAt"], "2026-11-18T04:00:00Z")
        rows = items_by_row(snapshot)
        self.assertEqual(list(rows)[:2], ["new-episodes", "new-for-you"])
        self.assertEqual(list(rows)[-1], "top-picks")
        self.assertTrue(any(row.startswith("because-") for row in rows))

        by_id = {item["id"]: item for row in snapshot["rows"] for item in row["items"]}
        self.assertEqual(by_id["tt9100001"]["reason"], "New season · you finished S2")
        self.assertEqual(by_id["tt9100001"]["videoId"], "tt9100001:3:1")
        self.assertEqual(by_id["tt9100002"]["reason"], "New episode · S2E5")
        self.assertEqual(by_id["tt9300018"]["reason"], "New on Netflix · Crime & Mystery")   # unrated, trusted catalog
        self.assertEqual(by_id["tt9300001"]["reason"], "New · from Mara Quinn")
        because = [row for row in snapshot["rows"] if row["kind"] == "because"]
        self.assertTrue(all(item["reason"] == row["title"] for row in because for item in row["items"]))
        self.assertIn("Because you watched Paper Kings", [row["title"] for row in because])

        excluded = {"tt9100003": "abandoned show", "tt9200003": "thumbs down", "tt9300003": "disliked franchise",
                    "tt9300004": "AV1/DV/DTS/TrueHD only", "tt9300005": "foreign without dub or preferred-language subs",
                    "tt9300006": "low rating", "tt9300007": "too old", "tt9300016": "not interested franchise",
                    "tt9300017": "unrated", "tt9300019": "unreleased", "tt9300020": "talk show"}
        for identifier, why in excluded.items():
            self.assertNotIn(identifier, by_id, why)

        report = snapshot["report"]
        self.assertEqual(report["rows"]["new-episodes"], 2)
        self.assertEqual(report["totalItems"], sum(len(row["items"]) for row in snapshot["rows"]))
        self.assertEqual(report["filtered"]["disliked_franchise"], 2)
        self.assertEqual(report["filtered"]["unplayable"], 2)
        self.assertIn("AV1 video", " ".join(report["unplayable"]))
        self.assertEqual(report["languages"]["anime"], 1)
        self.assertEqual(report["sources"]["tmdb"], {"enabled": False, "reason": "no TMDB key configured"})
        self.assertFalse(report["needsCuration"])
        self.assertIsNone(curation)
        self.assertEqual(details["profile"]["coverage"]["netflix"]["matchedTitles"], 5)
        self.assertNotIn("Viewer", json.dumps(details["profile"]["recent"]))

    def test_build_is_deterministic(self):
        first, _, _ = self.build()
        second, _, _ = self.build()             # same inputs, now with a warm playability cache
        for snapshot in (first, second):
            snapshot["report"].pop("playability")
        self.assertEqual(json.dumps(first, sort_keys=True), json.dumps(second, sort_keys=True))

    def test_playability_cache_makes_the_second_run_polite(self):
        self.build()
        torrentio_first = [url for url in self.http.requests if "torrentio" in url]
        self.build()
        torrentio_second = [url for url in self.http.requests if "torrentio" in url]
        self.assertGreater(len(torrentio_first), 5)
        self.assertEqual(torrentio_second, [])

    def test_release_day_titles_and_nameless_candidates(self):
        from mac.feed.candidates import Candidate
        from mac.feed.sources.base import Source

        class Extra(Source):
            name = "extra"

            def collect(self, ctx, report):
                today = ctx.now.date()
                return [Candidate("tt8000001", "movie", "Out Today", year=today.year, released=today, rating=7.5,
                                  votes=300, genres=("Crime", "Mystery"), countries=("United States",), enriched=True),
                        Candidate("tt8000002", "movie", "", year=today.year, released=today, rating=7.5, votes=300,
                                  genres=("Crime",), enriched=True)]

        from mac.feed.sources import default_sources
        config = fixture_config(rows={"rowSize": 20})
        config["sources"]["extra"] = {"enabled": True}
        snapshot, details, _ = self.build(config, sources=[*default_sources(), Extra()])
        rows = items_by_row(snapshot)
        self.assertIn("tt8000001", rows["new-for-you"])
        self.assertEqual(snapshot["report"]["filtered"]["no_name"], 1)

    def test_without_netflix_or_imdb_the_feed_still_ships(self):
        snapshot, details, curation = self.build(database=False, imdb_dir=self.tmp / "no-imdb")
        self.assertEqual(schema.validate(snapshot), [])
        self.assertTrue(any("IMDb datasets not found" in warning for warning in details["warnings"]))
        self.assertGreater(snapshot["report"]["totalItems"], 0)


class FallbackTest(TempDirTest):
    def test_thin_feed_writes_a_curation_request_and_runs_the_command(self):
        marker = self.tmp / "marker.txt"
        command = [sys.executable, "-c",
                   "import os, pathlib; pathlib.Path(os.environ['MARKER']).write_text(os.environ['FEED_CURATION_REQUEST'])"]
        config = fixture_config(fallback={"minTotalItems": 500, "minRowItems": {"new-for-you": 50}, "minBecauseRows": 9,
                                          "command": command})
        snapshot, details, curation = self.build(config)
        self.assertTrue(snapshot["report"]["needsCuration"])
        self.assertGreater(len(snapshot["rows"]), 0)                  # the deterministic feed still ships
        self.assertEqual(curation["kind"], "needs_curation")
        self.assertEqual({gap["row"] for gap in curation["gaps"]}, {"*", "new-for-you", "because"})
        self.assertIn("topGenres", curation["profileSummary"])
        rejected = {entry["id"]: entry["reason"] for entry in curation["rejected"]}
        self.assertTrue(rejected["tt9300004"].startswith("unplayable"))
        self.assertEqual(rejected["tt9300010"], "few_votes")             # filter near-miss
        self.assertNotIn("tt9200003", rejected)                           # exclusions are never offered

        path = self.tmp / "curation.json"
        self.assertTrue(fallback.write_or_clear(path, curation))
        import os
        os.environ["MARKER"] = str(marker)
        try:
            status = fallback.run_command(config["fallback"]["command"], path, 30, lambda message: None)
        finally:
            del os.environ["MARKER"]
        self.assertEqual(status, {"ran": True, "exitCode": 0})
        self.assertEqual(marker.read_text(), str(path))
        self.assertFalse(fallback.write_or_clear(path, None))
        self.assertFalse(path.exists())

    def test_assess_thresholds(self):
        config = fixture_config(fallback={"minTotalItems": 5, "minRowItems": {"top-picks": 3}, "minBecauseRows": 1})
        self.assertEqual(fallback.assess({"top-picks": 3, "because-tt1": 2, "new-for-you": 3}, [], config), [])
        gaps = fallback.assess({"top-picks": 1}, [{"seed": "tt1"}], config)
        self.assertEqual([gap["row"] for gap in gaps], ["*", "new-for-you", "top-picks", "because"])
        self.assertEqual(fallback.run_command(None, "x", 1, print), {"ran": False})


class CommandLineTest(TempDirTest):
    def run_cli(self, *args):
        return subprocess.run([sys.executable, *args], cwd=PROJECT, capture_output=True, text=True, timeout=120,
                              env={"PATH": "/usr/bin:/bin", "RU7100_DATA_DIR": str(self.tmp / "data")})

    def test_build_and_ingest_commands(self):
        database = self.netflix_database()
        log = self.tmp / "data" / "tv-feedback.jsonl"
        ingest = self.run_cli("-m", "mac.feed", "ingest-feedback", str(FIXTURES / "feedback.jsonl"), "--log", str(log))
        self.assertEqual(ingest.returncode, 0, ingest.stderr)
        self.assertEqual(json.loads(ingest.stdout)["added"], 20)
        output = self.tmp / "out" / "feed.json"
        result = self.run_cli(
            "-m", "mac.feed", "build", "--profile", PROFILE, "--config", str(FIXTURES / "config.json"),
            "--database", str(database), "--imdb", str(FIXTURES / "imdb"), "--library", str(FIXTURES / "library.json"),
            "--feedback", str(log), "--fixtures", str(FIXTURES), "--now", "2026-11-18T04:00:00Z",
            "--cache-dir", str(self.tmp / "cache"), "--output", str(output), "--quiet",
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        summary = json.loads(result.stdout)
        self.assertEqual(summary["rows"]["new-episodes"], 2)
        snapshot = json.loads(output.read_text())
        self.assertEqual(schema.validate(snapshot), [])
        self.assertTrue((self.tmp / "out" / "feed.report.json").exists())
        self.assertEqual(oct(output.stat().st_mode & 0o777), "0o600")

    def test_legacy_wrapper_still_works(self):
        database = self.netflix_database()
        output = self.tmp / "recommendations.json"
        result = self.run_cli(str(PROJECT / "mac" / "recommendations.py"), "--profile", PROFILE, "--database", str(database),
                              "--imdb", str(FIXTURES / "imdb"), "--output", str(output))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout)["matchedTitles"], 5)
        snapshot = json.loads(output.read_text())
        self.assertEqual(snapshot["schemaVersion"], 1)
        self.assertIsInstance(snapshot["items"], list)
        self.assertNotIn("tt9200001", [item["id"] for item in snapshot["items"]])   # watched


class ConfigTest(TempDirTest):
    def test_example_config_uses_known_keys(self):
        from mac.feed import config as feed_config

        example = json.loads((PROJECT / "mac" / "feed" / "config.example.json").read_text())

        def unknown(value, defaults, path=""):
            problems = []
            for key, item in value.items():
                if key.startswith("_"):
                    continue
                if key not in defaults:
                    problems.append(path + key)
                elif isinstance(item, dict) and isinstance(defaults[key], dict) and defaults[key]:
                    problems.extend(unknown(item, defaults[key], f"{path}{key}."))
            return problems

        self.assertEqual(unknown(example, feed_config.DEFAULTS), [])
        loaded = feed_config.load(PROJECT / "mac" / "feed" / "config.example.json", environ={"RU7100_TMDB_API_KEY": "k"})
        self.assertEqual(loaded["sources"]["tmdb"]["apiKey"], "k")
        self.assertEqual(loaded["sources"]["cinemeta"]["catalogs"], ["top", "imdbRating"])   # defaults kept
        with self.assertRaises(ValueError):
            feed_config.load(self.tmp / "missing.json", required=True)


if __name__ == "__main__":
    unittest.main()
