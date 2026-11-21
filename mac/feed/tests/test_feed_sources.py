import io
import json
import unittest
import urllib.error
from datetime import date

from mac.feed import feedback
from mac.feed.candidates import CandidatePool
from mac.feed.http import FixtureHttp, Http, HttpError, redact
from mac.feed.imdb import load_index
from mac.feed.sources import SourceContext, SourceReport, run_sources
from mac.feed.sources.cinemeta import (CatalogAddonSource, CinemetaSource, addon_base, catalog_url, parse_catalogs,
                                       parse_meta)
from mac.feed.sources.imdb_recent import ImdbRecentSource
from mac.feed.sources.new_episodes import NewEpisodesSource
from mac.feed.sources.tmdb import TmdbSource, candidate_from_details
from mac.feed.sources.trakt import TraktSource, parse_item
from mac.feed.tests.support import FIXTURES, NOW, TempDirTest, empty_profile, event, fixture_config, fixture_http
from mac.feed.profile import ProfileBuilder


def context(config=None, http=None, profile=None, imdb=None):
    config = config or fixture_config()
    return SourceContext(http=http or fixture_http(), config=config, now=NOW,
                         profile=profile or empty_profile(config), imdb=imdb, log=lambda message: None)


def collect(source, ctx):
    report = SourceReport()
    return source.collect(ctx, report), report


class CinemetaParsingTest(unittest.TestCase):
    def test_catalog_entry_quirks(self):
        payload = json.loads((FIXTURES / "http/cinemeta/movie-top.json").read_text())
        metas = {meta["id"]: meta for meta in payload["metas"]}
        unrated = parse_meta(metas["tt9300017"], "movie", NOW)
        self.assertIsNone(unrated.rating)                     # imdbRating ""
        signal = parse_meta(metas["tt9300001"], "movie", NOW)
        self.assertEqual((signal.rating, signal.year, signal.released), (7.4, 2026, date(2026, 10, 20)))
        self.assertEqual(signal.directors, ("Mara Quinn",))
        self.assertTrue(signal.enriched)
        self.assertIsNone(parse_meta({"id": "tmdb:1", "type": "movie", "name": "x"}))
        self.assertEqual(parse_meta({"id": "x", "imdb_id": "tt1234567", "type": "movie"}).id, "tt1234567")
        self.assertIsNone(parse_meta({"id": "tt1234567", "type": "movie", "released": None, "imdbRating": None}).released)

    def test_series_videos(self):
        payload = json.loads((FIXTURES / "http/cinemeta/series-year-2025.json").read_text())
        city = parse_meta(payload["metas"][0], "series", NOW)
        self.assertEqual(city.first_video_id, "tt9300022:1:1")
        self.assertEqual(city.latest_season_start, date(2026, 9, 10))
        self.assertEqual(city.latest_release, date(2026, 9, 10))

    def test_urls(self):
        self.assertEqual(catalog_url("https://x", "movie", "year", [("genre", 2026), ("skip", 50)]),
                         "https://x/catalog/movie/year/genre=2026&skip=50.json")
        self.assertEqual(catalog_url("https://x", "movie", "top", [("genre", "Sci-Fi")]), "https://x/catalog/movie/top/genre=Sci-Fi.json")
        self.assertEqual(addon_base("https://host/conf=a|b/manifest.json?x=1"), "https://host/conf=a|b")


class CinemetaSourceTest(unittest.TestCase):
    def test_specs_include_year_and_profile_genres(self):
        profile = ProfileBuilder(fixture_config(), NOW).finish()
        profile.raw["genre"] = {"Crime": 3.0, "Talk-Show": 2.0, "Comedy": -1.0}
        specs = CinemetaSource().specs(context(profile=profile))
        labels = {(type_, label) for type_, _, _, label in specs}
        self.assertIn(("movie", "new 2026"), labels)
        self.assertIn(("series", "new 2025"), labels)
        self.assertIn(("movie", "top Crime"), labels)
        self.assertIn(("series", "top Talk-Show"), labels)
        self.assertNotIn(("movie", "top Talk-Show"), labels)   # not a Cinemeta movie genre
        self.assertNotIn(("movie", "top Comedy"), labels)      # disliked genre

    def test_collect_records_ranks_and_errors(self):
        routes = [[r"/catalog/movie/top\.json", "http/cinemeta/movie-top.json"], [r"/catalog/", None]]
        config = fixture_config(sources={"cinemeta": {"yearCatalogYears": 0, "genreCatalogs": 0, "catalogs": ["top"]}})
        items, report = collect(CinemetaSource(), context(config, fixture_http(routes)))
        self.assertEqual(items[0].sources["cinemeta"], {"rank": 0, "catalog": "top"})
        self.assertEqual(len(items), 12)
        self.assertEqual(len(report.errors), 1)                # series/top answered 404


class CatalogAddonTest(unittest.TestCase):
    def test_browsable_catalogs_only(self):
        manifest = json.loads((FIXTURES / "http/addons/manifest.json").read_text())
        catalogs = {catalog["id"]: catalog for catalog in parse_catalogs(manifest)}
        self.assertEqual(catalogs["dnp"]["required"], ["genre"])
        self.assertEqual(catalogs["nfx"]["supported"], ["skip"])
        items, report = collect(CatalogAddonSource(), context())
        self.assertEqual(sorted(item.id for item in items), ["tt9300002", "tt9300018"])
        self.assertTrue(all(item.trusted_new for item in items))
        self.assertEqual(items[0].sources["addon:example.streaming-catalogs"]["label"], "Netflix")
        self.assertEqual(report.counts["skipped_non_imdb"], 1)

    def test_disabled_without_manifests(self):
        config = fixture_config(sources={"catalogAddons": {"manifests": []}})
        self.assertEqual(CatalogAddonSource().enabled(context(config)), (False, "no catalog add-on manifests configured"))


class ImdbSourceTest(unittest.TestCase):
    def test_recent_releases_with_thresholds(self):
        index = load_index(FIXTURES / "imdb", min_year=2025, recent_min_votes=1500, recent_min_rating=6.3)
        items, report = collect(ImdbRecentSource(), context(imdb=index))
        ids = [item.id for item in items]
        self.assertIn("tt9300009", ids)          # 2025, 40k votes, 7.8
        self.assertNotIn("tt9300007", ids)       # 1999
        self.assertNotIn("tt9300004", ids)       # 900 votes
        self.assertNotIn("tt9300006", ids)       # rating 4.9
        self.assertTrue(all(item.poster.startswith("https://images.metahub.space/") for item in items))
        self.assertEqual(ImdbRecentSource().enabled(context(imdb=None))[0], False)


class NewEpisodesTest(unittest.TestCase):
    def profile(self):
        builder = ProfileBuilder(fixture_config(), NOW)
        # Night Harbor: two full seasons on Netflix (via season counts); Glass Valley: up to S2E4 on the TV.
        harbor = builder.state("tt9100001", "series", "Night Harbor")
        harbor.netflix_seasons = {1: 6, 2: 6}
        harbor.last_at = event("tt9100001", "completed", "2025-02-06T20:00:00Z").at
        builder.add(harbor, "netflix_series_watched", 3.0, harbor.last_at, "netflix", "netflix")
        builder.add_feedback([event("tt9100002", "completed", f"2026-11-{6 + n:02d}T21:00:00Z", f"tt9100002:2:{n}")
                              for n in range(1, 5)])
        builder.add_feedback([])
        dropped = builder.state("tt9100003", "series", "Ember Road")
        dropped.abandoned = True
        builder.add(dropped, "abandoned_early", -2.0, NOW, "inApp", "tv")
        return builder.finish()

    def test_reasons_and_baselines(self):
        http = fixture_http()
        items, report = collect(NewEpisodesSource(), context(http=http, profile=self.profile()))
        found = {item.id: item.new_episode for item in items}
        self.assertEqual(found["tt9100001"]["reason"], "New season · you finished S2")
        self.assertEqual(found["tt9100001"]["videoId"], "tt9100001:3:1")
        self.assertEqual(found["tt9100001"]["count"], 2)      # S3E3 airs after "now"
        self.assertEqual(found["tt9100002"]["reason"], "New episode · S2E5")
        self.assertEqual(found["tt9100002"]["released"], "2026-11-14")
        self.assertNotIn("tt9100003", http.requests[0])       # abandoned shows are not even requested
        self.assertIn("lastVideosIds=tt9100001,tt9100002.json", http.requests[0])

    def test_a_user_who_is_behind_resumes_at_the_next_episode(self):
        builder = ProfileBuilder(fixture_config(), NOW)
        builder.add_feedback([event("tt9100002", "completed", "2026-10-02T21:00:00Z", "tt9100002:2:1")])
        config = fixture_config(sources={"newEpisodes": {"days": 20}})
        items, _ = collect(NewEpisodesSource(), context(config, profile=builder.finish()))
        episode = {item.id: item.new_episode for item in items}["tt9100002"]
        self.assertEqual(episode["reason"], "New episode · S2E5")   # only S2E5 is inside the 20-day window
        self.assertEqual(episode["videoId"], "tt9100002:2:2")      # but playback starts at the next unwatched one


TMDB_MOVIE = {
    "id": 501, "imdb_id": "tt7000001", "title": "Tmdb Movie", "release_date": "2026-09-01",
    "genres": [{"id": 878, "name": "Science Fiction"}, {"id": 53, "name": "Thriller"}],
    "original_language": "en", "production_countries": [{"iso_3166_1": "US", "name": "United States of America"}],
    "vote_average": 7.3, "vote_count": 820, "poster_path": "/p.jpg",
    "keywords": {"keywords": [{"id": 1, "name": "time loop"}, {"id": 2, "name": "space"}]},
    "credits": {"cast": [{"name": "B Actor", "order": 1}, {"name": "A Actor", "order": 0}],
                "crew": [{"name": "D Rector", "job": "Director"}, {"name": "W Riter", "job": "Writer"}]},
    "external_ids": {"imdb_id": "tt7000001"},
}
TMDB_TV = {
    "id": 601, "name": "Tmdb Show", "first_air_date": "2026-03-01", "original_language": "ko", "origin_country": ["KR"],
    "genres": [{"id": 10765, "name": "Sci-Fi & Fantasy"}], "created_by": [{"name": "Show Runner"}],
    "keywords": {"results": [{"name": "dystopia"}]}, "external_ids": {"imdb_id": "tt7000002"},
    "vote_average": 8.0, "vote_count": 300,
}


class TmdbTest(unittest.TestCase):
    def test_details_parsing(self):
        movie = candidate_from_details("movie", TMDB_MOVIE)
        self.assertEqual((movie.id, movie.type, movie.genres), ("tt7000001", "movie", ("Sci-Fi", "Thriller")))
        self.assertEqual(movie.cast[:2], ("A Actor", "B Actor"))
        self.assertEqual(movie.directors, ("D Rector",))
        self.assertEqual(movie.keywords, ("space", "time loop"))
        self.assertEqual(movie.countries, ("United States",))
        self.assertEqual((movie.ext_rating, movie.ext_votes, movie.rating), (7.3, 820, None))
        show = candidate_from_details("tv", TMDB_TV)
        self.assertEqual((show.type, show.genres, show.countries, show.directors), ("series", ("Sci-Fi", "Fantasy"),
                                                                                    ("South Korea",), ("Show Runner",)))
        self.assertIsNone(candidate_from_details("movie", {"id": 1, "external_ids": {"imdb_id": None}}))

    def test_source_lists_and_seed_links(self):
        routes = [
            [r"/trending/movie/week", {"results": [{"id": 501}]}],
            [r"/find/tt0000070", {"movie_results": [{"id": 900}], "tv_results": []}],
            [r"/movie/900/recommendations", {"results": [{"id": 501}, {"id": 502}]}],
            [r"/movie/900/similar", {"results": []}],
            [r"/movie/501\?", TMDB_MOVIE],
            [r"/movie/502\?", {"id": 502, "external_ids": {}}],
            [r"api\.themoviedb\.org", {"results": []}],
        ]
        http = FixtureHttp(FIXTURES, routes)
        config = fixture_config(sources={"tmdb": {"apiKey": "k-123"}})
        builder = ProfileBuilder(config, NOW)
        builder.add_feedback([event("tt0000070", "thumbs_up", "2026-11-01T00:00:00Z")])
        profile = builder.finish()
        profile.compute_affinities({"tt0000070": parse_meta({"id": "tt0000070", "type": "movie", "name": "Seed",
                                                             "genres": ["Sci-Fi"]})}.get)
        ctx = context(config, http, profile)
        self.assertTrue(TmdbSource().enabled(ctx)[0])
        items, report = collect(TmdbSource(), ctx)
        merged = CandidatePool()
        merged.extend(items)
        found = merged.get("tt7000001")
        self.assertIn("tmdb:trending", found.sources)
        self.assertIn("tmdb:recommendations", found.sources)
        self.assertAlmostEqual(found.seeds["tt0000070"], 1.0)
        self.assertEqual(report.counts["unmapped"], 1)
        self.assertTrue(all("api_key=k-123" in url for url in http.requests))
        self.assertFalse(TmdbSource().enabled(context())[0])


class TraktTest(unittest.TestCase):
    def test_parse_and_related(self):
        movie = {"title": "Trakt Film", "year": 2026, "ids": {"imdb": "tt7100001", "tmdb": 1}, "released": "2026-05-02",
                 "genres": ["science-fiction", "drama"], "rating": 7.1, "votes": 950, "language": "en", "country": "us"}
        parsed = parse_item(movie, "movie")
        self.assertEqual((parsed.genres, parsed.countries, parsed.original_language), (("Sci-Fi", "Drama"), ("United States",), "en"))
        self.assertIsNone(parse_item({"ids": {"imdb": None}}, "movie"))
        routes = [
            [r"/movies/trending", [{"watchers": 10, "movie": movie}]],
            [r"/movies/tt0000080/related", [dict(movie, ids={"imdb": "tt7100002"}, title="Related Film")]],
            [r"api\.trakt\.tv", []],
        ]
        http = FixtureHttp(FIXTURES, routes)
        config = fixture_config(sources={"trakt": {"clientId": "cid"}})
        builder = ProfileBuilder(config, NOW)
        builder.add_feedback([event("tt0000080", "thumbs_up", "2026-11-01T00:00:00Z")])
        profile = builder.finish()
        profile.compute_affinities({"tt0000080": parse_meta({"id": "tt0000080", "type": "movie", "name": "Seed",
                                                             "genres": ["Drama"]})}.get)
        items, _ = collect(TraktSource(), context(config, http, profile))
        self.assertEqual(sorted(item.id for item in items), ["tt7100001", "tt7100002"])
        related = [item for item in items if item.id == "tt7100002"][0]
        self.assertAlmostEqual(related.seeds["tt0000080"], 0.9)


class RunSourcesTest(unittest.TestCase):
    def test_a_failing_source_is_isolated(self):
        class Broken(CinemetaSource):
            name = "broken"

            def collect(self, ctx, report):
                raise RuntimeError("boom")

        config = fixture_config()
        config["sources"]["broken"] = {"enabled": True}
        pool = CandidatePool()
        summary = run_sources([Broken(), CatalogAddonSource(), ImdbRecentSource()], context(config), pool)
        self.assertEqual(summary["broken"]["errors"], ["RuntimeError: boom"])
        self.assertEqual(summary["catalogAddons"]["candidates"], 2)
        self.assertEqual(summary["imdb"], {"enabled": False, "reason": "IMDb datasets not found (run scripts/refresh-imdb.sh)"})


class FeedbackFileTest(TempDirTest):
    def test_formats_and_log_merge(self):
        events, rejected = feedback.load([FIXTURES / "feedback.jsonl"])
        self.assertEqual((len(events), rejected), (20, 2))
        array, bad = feedback.parse_text(json.dumps([{"imdbId": "tt1234567", "kind": "thumbs_up", "at": 1763424000000},
                                                     {"imdbId": "tt1234567", "kind": "liked", "at": "2026-01-01"},
                                                     {"imdbId": "tt1234567", "videoId": "tt7654321:1:1", "kind": "completed",
                                                      "at": "2026-01-01"}]))
        self.assertEqual((len(array), bad), (1, 2))
        log = self.tmp / "log.jsonl"
        self.assertEqual(feedback.merge_into_log(log, events), {"added": 20, "total": 20})
        self.assertEqual(feedback.merge_into_log(log, events[:5] + array), {"added": 1, "total": 21})
        again, rejected = feedback.load([log])
        self.assertEqual((len(again), rejected), (21, 0))


class FakeResponse(io.BytesIO):
    def __init__(self, body):
        super().__init__(json.dumps(body).encode())
        self.headers = {}

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False


class FakeOpener:
    def __init__(self, outcomes):
        self.outcomes = list(outcomes)
        self.calls = []

    def open(self, request, timeout):
        self.calls.append((request.full_url, request.get_header("User-agent")))
        outcome = self.outcomes.pop(0)
        if isinstance(outcome, Exception):
            raise outcome
        return FakeResponse(outcome)


class HttpTest(TempDirTest):
    def make(self, outcomes, **options):
        self.clock_value = 1000.0
        opener = FakeOpener(outcomes)
        http = Http(self.tmp, opener=opener, sleep=lambda seconds: None, clock=lambda: self.clock_value, **options)
        return http, opener

    def test_retry_cache_and_offline(self):
        error = urllib.error.HTTPError("https://api.example/x", 503, "busy", {}, None)
        http, opener = self.make([error, {"ok": 1}])
        self.assertEqual(http.get_json("https://api.example/x?api_key=SECRET", ttl=60), {"ok": 1})
        self.assertEqual(len(opener.calls), 2)
        self.assertEqual(opener.calls[0][1], "ru7100-feed/1.0")
        self.assertEqual(http.get_json("https://api.example/x?api_key=SECRET", ttl=60), {"ok": 1})
        self.assertEqual(len(opener.calls), 2)                  # cache hit
        cached = list((self.tmp / "http").rglob("*.json"))[0].read_text()
        self.assertNotIn("SECRET", cached)
        offline = Http(self.tmp, offline=True, clock=lambda: 10**9)
        self.assertEqual(offline.get_json("https://api.example/x?api_key=SECRET", ttl=60), {"ok": 1})  # stale ok offline
        with self.assertRaises(HttpError):
            offline.get_json("https://api.example/other", ttl=60)

    def test_client_errors_are_not_retried(self):
        error = urllib.error.HTTPError("https://api.example/y", 404, "missing", {}, None)
        http, opener = self.make([error])
        with self.assertRaises(HttpError) as raised:
            http.get_json("https://api.example/y")
        self.assertEqual(raised.exception.status, 404)
        self.assertEqual(len(opener.calls), 1)

    def test_stale_cache_survives_an_outage(self):
        down = urllib.error.URLError("down")
        http, opener = self.make([{"v": 1}, down, down, down])
        http.get_json("https://api.example/z", ttl=10)
        self.clock_value += 100
        self.assertEqual(http.get_json("https://api.example/z", ttl=10), {"v": 1})
        self.assertEqual(http.report()["api.example"]["stale"], 1)

    def test_unresponsive_host_is_skipped_after_three_failures(self):
        down = urllib.error.URLError("timed out")
        http, opener = self.make([down] * 9 + [{"ok": 1}])
        for path in ("a", "b", "c"):
            with self.assertRaises(HttpError):
                http.get_json(f"https://slow.example/{path}")
        self.assertEqual(len(opener.calls), 9)
        with self.assertRaises(HttpError) as raised:
            http.get_json("https://slow.example/d")
        self.assertIn("skipped for the rest of this run", str(raised.exception))
        self.assertEqual(len(opener.calls), 9)                  # no further network calls
        self.assertEqual(http.get_json("https://fast.example/x"), {"ok": 1})   # other hosts unaffected

    def test_redact(self):
        self.assertEqual(redact("https://h/p?api_key=abc&page=2"), "https://h/p?api_key=REDACTED&page=2")


if __name__ == "__main__":
    unittest.main()
