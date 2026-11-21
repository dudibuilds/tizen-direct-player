import json
import unittest
from collections import Counter
from datetime import date

from mac.feed.http import FixtureHttp, HttpError
from mac.feed.language import language_class
from mac.feed.playability import (PlayabilityChecker, detect_anime_languages, detect_general_languages, evaluate,
                                  hard_filter, parse_size, parse_stream)
from mac.feed.profile import ProfileBuilder
from mac.feed.ranking import Ranker, Scored, content_link, mmr_select
from mac.feed.tests.support import FIXTURES, NOW, TempDirTest, candidate, empty_profile, event, fixture_config


def streams(name):
    return json.loads((FIXTURES / "http/torrentio" / f"{name}.json").read_text())["streams"]


def ranker(config=None, profile=None):
    config = config or fixture_config()
    return Ranker(profile or empty_profile(config), config, NOW)


class QualityGateTest(unittest.TestCase):
    def test_vote_threshold_scales_with_recency(self):
        rules = ranker()
        fresh = candidate(released=date(2026, 11, 1), year=2026, rating=7.0, votes=400, genres=("Drama",))
        older = candidate(released=date(2025, 3, 1), year=2025, rating=7.0, votes=400, genres=("Drama",))
        self.assertIsNone(rules.filter(fresh))
        self.assertEqual(rules.filter(older), "few_votes")
        self.assertIsNone(rules.filter(candidate(released=date(2025, 3, 1), year=2025, rating=7.0, votes=3500, genres=("Drama",))))

    def test_rating_threshold_scales_with_recency(self):
        rules = ranker()
        self.assertIsNone(rules.filter(candidate(released=date(2026, 11, 10), year=2026, rating=6.1, votes=200)))
        self.assertEqual(rules.filter(candidate(released=date(2025, 1, 10), year=2025, rating=6.1, votes=20000)), "low_rating")

    def test_unrated_titles_need_a_voucher_and_must_be_new(self):
        rules = ranker()
        lonely = candidate(released=date(2026, 11, 12), year=2026, sources={"cinemeta": {"rank": 1}})
        trusted = candidate(released=date(2026, 11, 12), year=2026, trusted_new=True, sources={"addon:x": {"rank": 1}})
        stale = candidate(released=date(2026, 5, 1), year=2026, trusted_new=True)
        self.assertEqual(rules.filter(lonely), "unrated")
        self.assertIsNone(rules.filter(trusted))
        self.assertEqual(rules.filter(stale), "unrated")

    def test_external_votes_use_a_smaller_scale(self):
        rules = ranker()
        tmdb = candidate(released=date(2025, 6, 1), year=2025, ext_rating=7.2, ext_votes=400)
        self.assertIsNone(rules.filter(tmdb))
        self.assertEqual(rules.filter(candidate(released=date(2025, 6, 1), year=2025, ext_rating=7.2, ext_votes=150)), "few_votes")


class FilterTest(unittest.TestCase):
    def profile(self, config):
        builder = ProfileBuilder(config, NOW)
        builder.add_feedback([
            event("tt0000001", "completed", "2026-11-01T00:00:00Z", position_ms=99, duration_ms=100),
            event("tt0000002", "not_interested", "2026-11-01T00:00:00Z"),
            event("tt0000003", "thumbs_down", "2026-11-01T00:00:00Z"),
        ])
        profile = builder.finish()
        profile.compute_affinities({"tt0000003": candidate("tt0000003", name="Bad Saga", genres=("Action",))}.get)
        return profile

    def test_reasons(self):
        config = fixture_config()
        rules = ranker(config, self.profile(config))
        good = dict(released=date(2026, 9, 1), year=2026, rating=7.5, votes=9000, genres=("Drama",))
        self.assertEqual(rules.filter(candidate("tt0000001", **good)), "watched")
        self.assertEqual(rules.filter(candidate("tt0000002", **good)), "not_interested")
        self.assertEqual(rules.filter(candidate("tt0000003", **good)), "disliked")
        self.assertEqual(rules.filter(candidate("tt0000004", name="Bad Saga 2", **good)), "disliked_franchise")
        self.assertEqual(rules.filter(candidate("tt0000005", **{**good, "genres": ("Talk-Show",)})), "blocked_genre")
        self.assertEqual(rules.filter(candidate("tt0000006", **{**good, "released": date(2027, 1, 1), "year": 2027})), "unreleased")
        self.assertEqual(rules.filter(candidate("tt0000007", **{**good, "released": date(2010, 1, 1), "year": 2010})), "release_window")
        linked = candidate("tt0000008", **{**good, "released": date(2016, 1, 1), "year": 2016}, seeds={"tt0000001": 0.9})
        self.assertIsNone(rules.filter(linked))      # related titles may be older
        self.assertEqual(rules.filter(candidate("tt0000009", **{**good, "released": None, "year": None})), "no_release_info")
        self.assertEqual(rules.filter(candidate("tt0000010", name=" ", **good)), "no_name")

    def test_foreign_exclude_mode(self):
        config = fixture_config(filters={"language": {"foreign": "exclude"}})
        foreign = candidate(released=date(2026, 9, 1), year=2026, rating=7.5, votes=9000, countries=("France",))
        self.assertEqual(ranker(config).filter(foreign), "language")


class LanguageTest(unittest.TestCase):
    def test_classes(self):
        self.assertEqual(language_class(candidate(countries=("United Kingdom", "France"))), "english")
        self.assertEqual(language_class(candidate(countries=("Germany", "United States"))), "foreign")
        self.assertEqual(language_class(candidate(countries=("Spain",))), "preferred")
        self.assertEqual(language_class(candidate(countries=("Japan",), genres=("Animation", "Action"))), "anime")
        self.assertEqual(language_class(candidate(genres=("Anime",))), "anime")
        self.assertEqual(language_class(candidate(countries=("France",), original_language="en")), "english")
        self.assertEqual(language_class(candidate()), "unknown")


def scored(identifier, score, genres, name=None, directors=()):
    item = candidate(identifier, name=name or f"Film {identifier}", genres=genres, directors=directors)
    from mac.feed.profile import franchise_key
    return Scored(item, score, dominant_genre=genres[0] if genres else "none", franchise=franchise_key(item.name))


class DiversityTest(unittest.TestCase):
    def test_genre_and_franchise_caps(self):
        items = [scored(f"tt00000{10 + n}", 0.9 - n * 0.01, ("Crime",)) for n in range(6)]
        items += [scored("tt0000020", 0.5, ("Comedy",)), scored("tt0000021", 0.49, ("Horror",))]
        items += [scored("tt0000030", 0.95, ("Drama",), "Saga"), scored("tt0000031", 0.94, ("Western",), "Saga 2")]
        rejected = Counter()
        picked = mmr_select(items, 5, max_genre_share=0.4, rejected=rejected)
        genres = Counter(item.dominant_genre for item in picked)
        self.assertLessEqual(genres["Crime"], 2)
        self.assertEqual(sum(1 for item in picked if item.franchise == "saga"), 1)
        self.assertEqual(len(picked), 5)
        self.assertGreater(rejected["diversity_genre_cap"], 0)
        self.assertEqual(rejected["diversity_franchise_cap"], 1)

    def test_accept_hook_and_determinism(self):
        items = [scored(f"tt00000{40 + n}", 0.5, (genre,)) for n, genre in enumerate(("A", "B", "C", "D"))]
        picked = mmr_select(items, 2, accept=lambda item: item.id != "tt0000040")
        self.assertEqual([item.id for item in picked], ["tt0000041", "tt0000042"])
        self.assertEqual([item.id for item in mmr_select(list(reversed(items)), 2)], ["tt0000040", "tt0000041"])

    def test_mmr_prefers_variety_over_near_duplicates(self):
        items = [scored("tt0000050", 0.80, ("Crime", "Drama"), directors=("X",)),
                 scored("tt0000051", 0.79, ("Crime", "Drama"), directors=("X",)),
                 scored("tt0000052", 0.70, ("Comedy",))]
        picked = mmr_select(items, 2, lambda_=0.6, max_genre_share=1.0)
        self.assertEqual([item.id for item in picked], ["tt0000050", "tt0000052"])

    def test_content_link(self):
        seed = candidate(genres=("Sci-Fi", "Thriller"), directors=("Mara Quinn",), countries=("United States",))
        self.assertGreater(content_link(seed, candidate(genres=("Sci-Fi",), directors=("Mara Quinn",))), 0.4)
        self.assertEqual(content_link(seed, candidate(genres=("Sci-Fi", "Adventure"))), 0.0)
        self.assertGreater(content_link(seed, candidate(genres=("Sci-Fi", "Thriller", "Drama"), countries=("United States",))), 0.4)


class StreamRulesTest(unittest.TestCase):
    def test_hard_filters_on_real_torrentio_titles(self):
        reasons = [hard_filter(parse_stream(item), parse_stream(item).text) for item in streams("real-movie-sample")]
        self.assertEqual(reasons[0], "AV1 video")                  # 2160p AV1 HDR10
        self.assertEqual(reasons[1], "Dolby Vision without HDR10")  # DV remux; DV is checked before DTS as in C#
        self.assertEqual(reasons[2], "DTS audio")                  # HDR10 DV with DTS-HD
        self.assertIsNone(reasons[3])                              # HEVC HDR10 AAC
        self.assertIsNone(reasons[6])                              # YIFY x264
        dv_only = {"name": "Torrentio\n4k DV", "title": "Film 2160p WEB-DL DV H.265\n👤 9 💾 5 GB", "infoHash": "a" * 40}
        dv_hdr = dict(dv_only, title="Film 2160p WEB-DL DV HDR10 H.265\n👤 9 💾 5 GB")
        self.assertEqual(hard_filter(parse_stream(dv_only), parse_stream(dv_only).text), "Dolby Vision without HDR10")
        self.assertIsNone(hard_filter(parse_stream(dv_hdr), parse_stream(dv_hdr).text))
        self.assertEqual(hard_filter(parse_stream({"externalUrl": "https://x"}), ""), "external link")
        self.assertEqual(hard_filter(parse_stream({"title": "x"}), "x"), "no playable url or info hash")

    def test_size_and_language_parsing(self):
        self.assertEqual(parse_size("👤 297 💾 8.91 GB ⚙️ 1337x"), 8_910_000_000)
        self.assertEqual(parse_size("715.1 MB"), 715_100_000)
        self.assertIsNone(parse_size("no size"))
        self.assertEqual(detect_general_languages("Film.2026.1080p.WEB\n🇬🇧 / 🇮🇹").tier, 1)
        self.assertEqual(detect_general_languages("Film.2026.1080p.WEB").tier, 0)
        self.assertEqual(detect_general_languages("Film.GERMAN.1080p\n🇩🇪").tier, -1)
        self.assertEqual(detect_general_languages("Film ENG audio Spa subs").tier, 7)
        self.assertEqual(detect_general_languages("Film DUAL-AUDIO GER-ENG").tier, 1)
        self.assertEqual(detect_anime_languages("[Grp] Show 01 Japanese Audio English Subs").tier, 5)
        self.assertEqual(detect_anime_languages("[Grp] Show 01 English Dub").tier, -1)
        self.assertEqual(detect_anime_languages("[Grp] Show 01 Dual Audio Multi Subs").tier, 3)

    def test_evaluate_fixture_payloads(self):
        good = evaluate(streams("good"))
        self.assertEqual((good.status, good.best["quality"], good.best["seeders"]), ("playable", "1080p", 210))
        self.assertEqual(good.rejects, {"over the size cap": 1})
        bad = evaluate(streams("bad"))
        self.assertEqual(bad.status, "unplayable")
        self.assertEqual(bad.rejects, {"AV1 video": 1, "Dolby Vision without HDR10": 1, "DTS audio": 1, "TrueHD audio": 1,
                                       "over the size cap": 1, "cam/screener": 1})
        self.assertTrue(bad.reason.startswith("no stream passes the TV rules ("))
        self.assertEqual(evaluate([]).reason, "no streams")
        self.assertEqual(evaluate(streams("foreign"), language_class="foreign").status, "unplayable")
        self.assertEqual(evaluate(streams("foreign"), language_class="foreign", foreign_mode="dub_or_preferred_subs", preferred_subs=True).status, "playable")
        self.assertEqual(evaluate(streams("foreign"), language_class="foreign", foreign_mode="dub", preferred_subs=True).status,
                         "unplayable")
        self.assertEqual(evaluate(streams("foreign"), language_class="english").reason, "foreign audio only")
        self.assertEqual(evaluate(streams("multi"), language_class="foreign").status, "playable")
        anime = evaluate(streams("anime"), anime=True)
        self.assertEqual((anime.status, anime.best["languageTier"]), ("playable", 5))
        dub_only = [streams("anime")[0]]
        self.assertEqual(evaluate(dub_only, anime=True).reason, "anime without Japanese audio or English subtitles")
        self.assertEqual(evaluate(streams("good"), min_seeders=500).reason, "no stream passes the TV rules (over the size cap, too few seeders)")

    def test_real_series_sample(self):
        verdict = evaluate(streams("real-series-sample"), language_class="foreign", foreign_mode="dub")
        self.assertEqual(verdict.status, "playable")
        self.assertEqual(verdict.best["languageTier"], 2)           # 🇬🇧/🇸🇪 multi-audio with EngSub


class CheckerTest(TempDirTest):
    def setUp(self):
        super().setUp()
        self.clock = 1_000_000.0

    def checker(self, routes, **playability):
        config = fixture_config(playability=playability)
        http = FixtureHttp(FIXTURES, routes)
        return PlayabilityChecker(http, config, self.tmp, clock=lambda: self.clock), http

    def test_cache_ttl_budget_and_unknown(self):
        routes = [[r"/stream/movie/tt0000001\.json", "http/torrentio/good.json"],
                  [r"/stream/movie/tt0000002\.json", "http/torrentio/bad.json"],
                  [r"/stream/series/tt0000003:1:1\.json", "http/torrentio/good.json"]]
        checker, http = self.checker(routes, maxChecksPerRun=3)
        self.assertEqual(checker.check(candidate("tt0000001"), "english").status, "playable")
        self.assertEqual(checker.check(candidate("tt0000002"), "english").status, "unplayable")
        self.assertEqual(checker.check(candidate("tt0000001"), "english").cached, True)
        self.assertEqual(checker.check(candidate("tt0000003", "series"), "english").video_id, "tt0000003:1:1")
        self.assertEqual(checker.check(candidate("tt0000004"), "english").status, "unchecked")   # budget spent
        checker.save()
        self.assertEqual(len(http.requests), 3)

        self.clock += 30 * 3600                     # fail TTL (24 h) expired, pass TTL (72 h) not
        again, http = self.checker(routes + [[r"/stream/movie/tt0000005", None]], maxChecksPerRun=5)
        self.assertTrue(again.check(candidate("tt0000001"), "english").cached)
        self.assertFalse(again.check(candidate("tt0000002"), "english").cached)
        unknown = again.check(candidate("tt0000005"), "english")
        self.assertEqual(unknown.status, "unknown")
        again.save()
        self.assertNotIn("tt0000005", json.dumps(json.loads((self.tmp / "playability.json").read_text())))

    def test_preferred_subtitles_rescue_foreign_titles(self):
        routes = [[r"/stream/movie/", "http/torrentio/foreign.json"],
                  [r"/subtitles/movie/tt0000006\.json", "http/opensubtitles/spa.json"],
                  [r"/subtitles/movie/", "http/opensubtitles/no-spa.json"]]
        checker, http = self.checker(routes)
        self.assertEqual(checker.check(candidate("tt0000006"), "foreign").status, "playable")
        self.assertEqual(checker.check(candidate("tt0000007"), "foreign").status, "unplayable")

    def test_new_episode_and_disabled(self):
        checker, http = self.checker([[r"/stream/series/tt0000008:3:1\.json", "http/torrentio/good.json"]])
        show = candidate("tt0000008", "series", new_episode={"videoId": "tt0000008:3:1"})
        self.assertEqual(checker.check(show, "english").status, "playable")
        off, _ = self.checker([], enabled=False)
        self.assertEqual(off.check(show, "english").status, "unchecked")

    def test_network_error_is_unknown(self):
        class Down:
            def get_json(self, url, **_):
                raise HttpError("down")

        checker = PlayabilityChecker(Down(), fixture_config(), self.tmp)
        self.assertEqual(checker.check(candidate("tt0000009"), "english").status, "unknown")
        self.assertEqual(checker.cache, {})


if __name__ == "__main__":
    unittest.main()
