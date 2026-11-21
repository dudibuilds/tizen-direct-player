import math
import unittest
from datetime import timedelta

from mac.feed.candidates import parse_datetime
from mac.feed.imdb import Title, load_index
from mac.feed.netflix import aggregate, read_rows, season_of, wanted_keys
from mac.feed.profile import ProfileBuilder, decay, franchise_key, same_franchise
from mac.feed.tests.support import FIXTURES, NOW, PROFILE, TempDirTest, candidate, event, fixture_config


def build(events=(), library=(), history=None, config=None):
    builder = ProfileBuilder(config or fixture_config(), NOW)
    if history is not None:
        builder.add_netflix(history)
    builder.add_feedback(sorted(events, key=lambda e: e.at))
    builder.add_library(list(library))
    return builder.finish()


def days_ago(days):
    return (NOW - timedelta(days=days)).isoformat()


class DecayTest(unittest.TestCase):
    def test_half_life(self):
        self.assertAlmostEqual(decay(NOW - timedelta(days=180), NOW, 180), 0.5)
        self.assertAlmostEqual(decay(NOW - timedelta(days=360), NOW, 180), 0.25)
        self.assertEqual(decay(NOW + timedelta(days=3), NOW, 180), 1.0)
        self.assertEqual(decay(None, NOW, 180), 1.0)


class FeedbackWeightingTest(unittest.TestCase):
    def test_completed_rewatch_and_thumbs_up_are_positive(self):
        profile = build([
            event("tt0000001", "completed", days_ago(30), position_ms=95, duration_ms=100),
            event("tt0000001", "completed", days_ago(0), position_ms=99, duration_ms=100),
            event("tt0000001", "thumbs_up", days_ago(0)),
        ])
        state = profile.get("tt0000001")
        kinds = sorted(signal.kind for signal in state.signals)
        self.assertEqual(kinds, ["completed", "rewatch", "thumbs_up"])
        # completed 2 + rewatch 2 + thumbs 4, barely decayed
        self.assertAlmostEqual(state.weight, 8.0, delta=0.1)
        self.assertTrue(state.watched)
        self.assertEqual(profile.exclusion("tt0000001"), "watched")

    def test_negative_feedback(self):
        profile = build([
            event("tt0000002", "thumbs_down", days_ago(1)),
            event("tt0000003", "not_interested", days_ago(1)),
            event("tt0000004", "play_started", days_ago(2)),
            event("tt0000004", "abandoned", days_ago(2), position_ms=10, duration_ms=100),
        ])
        self.assertLess(profile.get("tt0000002").weight, -4.9)
        self.assertEqual(profile.exclusion("tt0000002"), "disliked")
        self.assertLess(profile.get("tt0000003").weight, -1.9)
        self.assertEqual(profile.exclusion("tt0000003"), "not_interested")
        abandoned = profile.get("tt0000004")
        self.assertLess(abandoned.weight, -1.5)  # -2 early abandon +0.3 play started
        self.assertEqual(profile.exclusion("tt0000004"), "watched")

    def test_later_opinion_wins(self):
        profile = build([
            event("tt0000005", "thumbs_down", days_ago(10)),
            event("tt0000005", "thumbs_up", days_ago(1)),
            event("tt0000006", "not_interested", days_ago(10)),
            event("tt0000006", "added_to_library", days_ago(2)),
        ])
        self.assertFalse(profile.get("tt0000005").disliked)
        self.assertGreater(profile.get("tt0000005").weight, 3.9)
        self.assertFalse(profile.get("tt0000006").not_interested)
        self.assertIsNone(profile.exclusion("tt0000006"))

    def test_series_abandoned_on_first_episode_stops_new_episodes(self):
        profile = build([
            event("tt0000007", "play_started", days_ago(5), "tt0000007:1:1"),
            event("tt0000007", "abandoned", days_ago(5), "tt0000007:1:1", position_ms=60000, duration_ms=3000000),
            event("tt0000008", "completed", days_ago(9), "tt0000008:1:1"),
            event("tt0000008", "completed", days_ago(8), "tt0000008:1:2"),
            event("tt0000008", "abandoned", days_ago(7), "tt0000008:1:3", position_ms=60000, duration_ms=3000000),
        ])
        dropped, followed = profile.get("tt0000007"), profile.get("tt0000008")
        self.assertTrue(dropped.abandoned)
        self.assertFalse(followed.abandoned)
        self.assertEqual(followed.last_episode, (1, 3))
        self.assertEqual(followed.finished_episode, (1, 2))
        shows = [state.id for state in profile.shows_for_new_episodes(10)]
        self.assertEqual(shows, ["tt0000008"])

    def test_abandon_then_completion_is_forgiven(self):
        profile = build([
            event("tt0000009", "abandoned", days_ago(20), position_ms=5, duration_ms=100),
            event("tt0000009", "completed", days_ago(10), position_ms=98, duration_ms=100),
        ])
        self.assertNotIn("abandoned_early", [signal.kind for signal in profile.get("tt0000009").signals])

    def test_time_decay_ranks_recent_activity_higher(self):
        profile = build([
            event("tt0000010", "completed", days_ago(1), position_ms=98, duration_ms=100),
            event("tt0000011", "completed", days_ago(360), position_ms=98, duration_ms=100),
        ])
        recent, old = profile.get("tt0000010").weight, profile.get("tt0000011").weight
        self.assertAlmostEqual(old / recent, 0.25, delta=0.01)  # in-app half-life 180 days

    def test_explicit_opinions_decay_slower_than_plays(self):
        profile = build([
            event("tt0000012", "thumbs_up", days_ago(360)),
            event("tt0000013", "completed", days_ago(360), position_ms=98, duration_ms=100),
        ])
        thumbs = profile.get("tt0000012").weight / 4.0
        play = profile.get("tt0000013").weight / 2.0
        self.assertGreater(thumbs, play)
        self.assertAlmostEqual(thumbs, 0.5 ** (360 / 720), places=3)


class LibraryTest(unittest.TestCase):
    def test_library_items(self):
        profile = build(library=[
            {"_id": "tt0000020", "type": "movie", "name": "Seen", "state": {"flaggedWatched": 1, "lastWatched": days_ago(10)}},
            {"_id": "tt0000021", "type": "series", "name": "Show", "_ctime": days_ago(100),
             "state": {"video_id": "tt0000021:3:4", "lastWatched": days_ago(3), "noNotif": True}},
            {"_id": "tt0000022", "type": "movie", "name": "Gone", "removed": True, "state": {"flaggedWatched": 1}},
        ])
        self.assertTrue(profile.get("tt0000020").watched)
        show = profile.get("tt0000021")
        self.assertEqual(show.last_episode, (3, 4))
        self.assertFalse(show.notifications)
        self.assertIsNone(profile.get("tt0000022"))
        self.assertEqual(profile.shows_for_new_episodes(10), [])  # notifications off


class NetflixBootstrapTest(TempDirTest):
    def history(self):
        database = self.netflix_database()
        views, ratings = read_rows(database, PROFILE)
        index = load_index(FIXTURES / "imdb", wanted_keys=wanted_keys(views, ratings))
        return aggregate(views, ratings, index.lookup)

    def test_label_matching_and_seasons(self):
        history = self.history()
        self.assertEqual(history.coverage()["viewingLabels"], 17)
        self.assertEqual(history.coverage()["matchedLabels"], 16)  # "Some Unmatched Show" stays unmatched
        harbor = history.titles["tt9100001"]
        self.assertEqual({season: len(labels) for season, labels in harbor.seasons.items()}, {1: 6, 2: 6})
        self.assertEqual(len(harbor.days), 12)
        self.assertEqual(history.titles["tt9200001"].ratings[0][:2], ("thumbs", 2))
        self.assertEqual(season_of("Show: Limited Series: Part One", Title("tt1", "series", "Show", 2020, (), 0, 0))[0], 1)

    def test_netflix_signals(self):
        history = self.history()
        profile = build(history=history)
        kinds = {state.id: sorted(signal.kind for signal in state.signals) for state in profile.titles.values()}
        self.assertEqual(kinds["tt9100001"], ["netflix_series_watched"])
        self.assertEqual(kinds["tt9100003"], ["netflix_series_sampled"])
        self.assertTrue(profile.get("tt9100003").abandoned)
        self.assertEqual(kinds["tt9200001"], ["netflix_movie_completed", "netflix_thumbs_2"])
        self.assertEqual(kinds["tt9200003"], ["netflix_movie_partial"])
        series_weight = 1.0 + math.log2(12) * 0.75
        last = parse_datetime("2025-02-06T20:00:00Z")
        expected = series_weight * decay(last, NOW, 540)
        self.assertAlmostEqual(profile.get("tt9100001").weight, expected, places=4)

    def test_bootstrap_fades_as_in_app_feedback_grows(self):
        history = self.history()
        alone = build(history=history).get("tt9200001").weight
        config = fixture_config(profile={"bootstrap": {"fadeAfterTitles": 4, "minFactor": 0.2}})
        events = [event(f"tt00000{30 + n}", "completed", days_ago(1), position_ms=99, duration_ms=100) for n in range(3)]
        faded = build(events, history=history, config=config)
        self.assertAlmostEqual(faded.netflix_factor, 0.25)
        self.assertAlmostEqual(faded.get("tt9200001").weight, alone * 0.25, places=6)
        many = [event(f"tt00000{40 + n}", "completed", days_ago(1), position_ms=99, duration_ms=100) for n in range(9)]
        self.assertEqual(build(many, history=history, config=config).netflix_factor, 0.2)


class AffinityTest(unittest.TestCase):
    def test_genre_people_and_negative_affinities(self):
        profile = build([
            event("tt0000050", "thumbs_up", days_ago(1)),
            event("tt0000051", "thumbs_down", days_ago(1)),
        ])
        metas = {
            "tt0000050": candidate("tt0000050", genres=("Crime", "Drama"), directors=("Ann Lee",), countries=("United States",), year=2024),
            "tt0000051": candidate("tt0000051", name="Laugh Riot", genres=("Comedy",), cast=("Bo Fun",), year=2010),
        }
        profile.compute_affinities(metas.get)
        self.assertGreater(profile.normalized["genre"]["Crime"], 0)
        self.assertLess(profile.normalized["genre"]["Comedy"], 0)
        self.assertEqual(profile.top_features("person", 1), ["Ann Lee"])
        liked, _ = profile.affinity(candidate("tt1", genres=("Crime",), directors=("Ann Lee",), countries=("United States",), year=2025))
        disliked, _ = profile.affinity(candidate("tt2", genres=("Comedy",), cast=("Bo Fun",), year=2011))
        self.assertGreater(liked, 0.3)
        self.assertLess(disliked, -0.3)
        self.assertEqual(profile.disliked_franchises, ["laugh riot"])
        self.assertTrue(profile.franchise_disliked("Laugh Riot 2: The Return"))

    def test_franchise_keys(self):
        self.assertEqual(franchise_key("The Saw X"), "saw")
        self.assertEqual(franchise_key("Iron Tide 2"), "iron tide")
        self.assertEqual(franchise_key("Dune: Part Two"), "dune")
        self.assertTrue(same_franchise(franchise_key("Sunny Days Again"), franchise_key("Sunny Days 3")))
        self.assertFalse(same_franchise(franchise_key("Dark"), franchise_key("Dark Waters")))

    def test_library_items_without_watch_activity_do_not_fade_netflix(self):
        library = [{"_id": f"tt00001{n:02d}", "type": "movie", "name": f"Saved {n}", "state": {}} for n in range(30)]
        self.assertEqual(build(library=library).netflix_factor, 1.0)
        watched = [dict(item, state={"flaggedWatched": 1}) for item in library[:6]]
        self.assertAlmostEqual(build(library=watched).netflix_factor, 0.9)

    def test_seeds_prefer_distinct_genres(self):
        events = [event(f"tt00000{60 + n}", "thumbs_up", days_ago(n + 1)) for n in range(3)]
        profile = build(events)
        metas = {
            "tt0000060": candidate("tt0000060", genres=("Crime", "Drama")),
            "tt0000061": candidate("tt0000061", genres=("Crime", "Drama")),
            "tt0000062": candidate("tt0000062", genres=("Sci-Fi",)),
        }
        profile.compute_affinities(metas.get)
        self.assertEqual([state.id for state in profile.seeds(2, 1.0)], ["tt0000060", "tt0000062"])
        self.assertEqual([state.id for state in profile.seeds(3, 1.0)], ["tt0000060", "tt0000062", "tt0000061"])


if __name__ == "__main__":
    unittest.main()
