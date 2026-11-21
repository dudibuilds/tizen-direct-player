"""Feed pipeline: inputs -> taste profile -> candidate sources -> filters -> scoring -> rows -> snapshot."""

import time
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from . import SNAPSHOT_VERSION, fallback, feedback, library, netflix, paths, schema
from .candidates import Candidate, CandidatePool, poster_for
from .imdb import has_dataset, load_index
from .playability import PlayabilityChecker
from .profile import ProfileBuilder
from .ranking import Ranker
from .rows import RowBuilder, language_counts
from .sources import SourceContext, default_sources, run_sources
from .sources.cinemeta import CINEMETA, fetch_meta


# Filter reasons worth showing a curating agent (exclusions such as "watched" are not).
SOFT_REJECTIONS = {"few_votes", "low_rating", "unrated", "release_window", "language"}


@dataclass
class BuildOptions:
    profile: str
    config: dict
    http: object
    now: object
    output: Path = paths.DEFAULT_OUTPUT
    database: Path = None
    imdb_dir: Path = None
    feedback_paths: list = field(default_factory=list)
    cache_dir: Path = None
    curation_path: Path = None
    sources: list = None
    log: object = print
    run_fallback_command: bool = True


def resolve_path(value, default):
    return Path(value).expanduser() if value else default


def title_meta(title):
    return Candidate(id=title.identifier, type=title.kind, name=title.name, year=title.year, genres=title.genres,
                     rating=title.rating, votes=title.votes, poster=poster_for(title.identifier))


class FeedBuilder:
    def __init__(self, options):
        self.options = options
        self.config = options.config
        self.now = options.now
        self.http = options.http
        self.log = options.log
        self.warnings = []
        self.started = time.monotonic()

    def warn(self, message):
        self.warnings.append(message)
        self.log(message)

    # ---- inputs ----

    def load_inputs(self):
        config, options = self.config, self.options
        log_path = resolve_path(config.get("feedbackLog"), paths.DEFAULT_FEEDBACK_LOG)
        events, rejected = feedback.load([log_path, *options.feedback_paths])
        if rejected:
            self.warn(f"{rejected} malformed feedback events ignored")
        items = library.load(config, self.http, self.warn)

        database = options.database or resolve_path(config.get("netflixDatabase"), paths.DEFAULT_DATABASE)
        views, ratings = [], []
        if database.exists():
            views, ratings = netflix.read_rows(database, options.profile)
            if not views and not ratings:
                self.warn(f"No Netflix history for profile {options.profile!r} in {database}")
        else:
            self.warn(f"Netflix history database not found ({database}); building from in-app signals only")

        imdb_dir = options.imdb_dir or resolve_path(config.get("imdbDir"), paths.DEFAULT_IMDB)
        imdb = None
        if has_dataset(imdb_dir, "basics") and has_dataset(imdb_dir, "ratings"):
            settings = config["sources"]["imdb"]
            wanted_ids = {event.imdb_id for event in events} | {item["_id"] for item in items}
            imdb = load_index(
                imdb_dir, wanted_keys=netflix.wanted_keys(views, ratings), wanted_ids=wanted_ids,
                min_year=self.now.year - int(settings.get("years", 2)) + 1,
                recent_min_votes=int(settings.get("minVotes", 1500)), recent_min_rating=float(settings.get("minRating", 6.3)),
            )
        else:
            self.warn(f"IMDb datasets not found in {imdb_dir}; run scripts/refresh-imdb.sh")
        history = None
        if views or ratings:
            if imdb is None:
                self.warn("Netflix history cannot be matched to IMDb ids without the IMDb datasets")
            else:
                history = netflix.aggregate(views, ratings, imdb.lookup)
        return events, items, history, imdb

    # ---- profile ----

    def build_profile(self, events, items, history, imdb):
        builder = ProfileBuilder(self.config, self.now)
        if history is not None:
            builder.add_netflix(history)
        builder.add_feedback(events)
        builder.add_library(items)
        profile = builder.finish()
        meta = self.history_metadata(profile, imdb)
        profile.compute_affinities(meta.get)
        return profile

    def history_metadata(self, profile, imdb):
        """Cinemeta detail (people, country) for the strongest titles; IMDb basics for the rest."""
        meta = {}
        base = self.config["sources"]["cinemeta"].get("baseUrl", CINEMETA).rstrip("/")
        settings = self.config["enrich"]
        ttl = float(settings["ttlHours"]) * 3600
        budget = int(settings["maxHistoryTitles"])
        failures = 0
        for state in profile.ranked_titles():
            title = imdb.get(state.id) if imdb else None
            if title is not None:
                meta[state.id] = title_meta(title)
            if budget <= 0 or not self.config["sources"]["cinemeta"].get("enabled", True):
                continue
            # Feedback without episode ids is most likely a movie.
            type_ = state.type or (title.kind if title else "movie")
            budget -= 1
            try:
                detail = fetch_meta(self.http, base, type_, state.id, ttl, self.now)
            except Exception:  # noqa: BLE001 - metadata is best effort
                failures += 1
                continue
            if detail is not None:
                meta[state.id] = detail.merge(meta[state.id]) if state.id in meta else detail
        if failures:
            self.warn(f"Cinemeta metadata unavailable for {failures} profile titles")
        return meta

    # ---- candidates ----

    def gather(self, profile, imdb):
        pool = CandidatePool()
        ctx = SourceContext(http=self.http, config=self.config, now=self.now, profile=profile, imdb=imdb, log=self.log)
        sources = self.options.sources if self.options.sources is not None else default_sources()
        summary = run_sources(sources, ctx, pool)
        if imdb is not None:
            for candidate in pool:
                title = imdb.get(candidate.id)
                if title is None:
                    continue
                if candidate.votes is None and title.votes:
                    candidate.votes = title.votes
                    if candidate.rating is None:
                        candidate.rating = title.rating
                if not candidate.genres:
                    candidate.genres = title.genres
                if not candidate.year:
                    candidate.year = title.year
        return pool, summary

    def enrich(self, candidates, ranker):
        """Fetch Cinemeta detail for the best not-yet-enriched candidates (country, people, dates)."""
        settings = self.config["enrich"]
        base = self.config["sources"]["cinemeta"].get("baseUrl", CINEMETA).rstrip("/")
        ttl = float(settings["ttlHours"]) * 3600
        pending = sorted((c for c in candidates if not c.enriched), key=lambda c: (-ranker.score(c).score, c.id))
        fetched = failures = 0
        for candidate in pending[:int(settings["maxCandidates"])]:
            try:
                detail = fetch_meta(self.http, base, candidate.type, candidate.id, ttl, self.now)
            except Exception:  # noqa: BLE001
                failures += 1
                continue
            if detail is not None:
                candidate.merge(detail)
                candidate.enriched = True
                fetched += 1
        return {"fetched": fetched, "failures": failures, "pending": max(0, len(pending) - fetched)}

    # ---- the whole run ----

    def build(self):
        options = self.options
        events, items, history, imdb = self.load_inputs()
        profile = self.build_profile(events, items, history, imdb)
        pool, source_summary = self.gather(profile, imdb)
        profile.calibrate_genres(Counter(genre for candidate in pool for genre in candidate.genres))
        ranker = Ranker(profile, self.config, self.now)

        new_episode_candidates = [c for c in pool if c.new_episode]
        survivors = [c for c in pool if not c.new_episode and ranker.filter(c) is None]
        enrichment = self.enrich(survivors, ranker)

        filtered = Counter()
        filtered_titles = []
        soft_rejected = []          # near misses an agent could reconsider
        eligible = []
        for candidate in pool:
            if candidate.new_episode:
                continue
            reason = ranker.filter(candidate)
            if reason:
                filtered[reason] += 1
                filtered_titles.append({"id": candidate.id, "name": candidate.name, "reason": reason})
                if reason in SOFT_REJECTIONS and candidate.name:
                    soft_rejected.append((ranker.score(candidate), reason))
            else:
                eligible.append(ranker.score(candidate))
        eligible.sort(key=lambda scored: (-scored.score, scored.id))

        cache_dir = options.cache_dir or resolve_path(self.config.get("cacheDir"), paths.DEFAULT_CACHE)
        checker = PlayabilityChecker(self.http, self.config, cache_dir)
        rows_builder = RowBuilder(profile, ranker, checker, self.config, self.now)
        rows, thin_because = rows_builder.build(eligible, new_episode_candidates)
        checker.save()
        filtered.update(rows_builder.filtered)

        generated_at = self.now.isoformat().replace("+00:00", "Z")
        row_counts = {row.id: len(row.items) for row in rows}
        gaps = fallback.assess(row_counts, thin_because, self.config)
        snapshot = {
            "version": SNAPSHOT_VERSION,
            "generatedAt": generated_at,
            "profile": options.profile,
            "rows": [
                {"id": row.id, "title": row.title, "kind": row.kind, **({"seed": row.seed} if row.seed else {}),
                 "items": row.items}
                for row in rows if row.items
            ],
            "report": {
                "rows": row_counts,
                "totalItems": sum(row_counts.values()),
                "candidates": len(pool),
                "eligible": len(eligible),
                "newEpisodeCandidates": len(new_episode_candidates),
                "filtered": dict(sorted(filtered.items(), key=lambda pair: (-pair[1], pair[0]))),
                "unplayable": dict(sorted(rows_builder.unplayable.items(), key=lambda pair: (-pair[1], pair[0]))),
                "languages": language_counts(eligible),
                "sources": {name: {key: value for key, value in info.items() if key != "errors"}
                            for name, info in source_summary.items()},
                "playability": checker.report(),
                "thinBecauseSeeds": len(thin_because),
                "needsCuration": bool(gaps),
                "warnings": len(self.warnings),
            },
        }
        problems = schema.validate(snapshot)
        if problems:
            raise ValueError("Snapshot failed the schema check: " + "; ".join(problems[:5]))
        details = {
            "generatedAt": generated_at,
            "profile": profile.summary(),
            "sources": source_summary,
            "enrichment": enrichment,
            "http": self.http.report(),
            "thinBecauseSeeds": thin_because,
            "gaps": gaps,
            "filteredTitles": filtered_titles[:300],
            "rejectedInRows": [{"id": scored.id, "name": scored.candidate.name, "reason": reason}
                               for scored, reason in rows_builder.rejected[:100]],
            "warnings": self.warnings,
            "seconds": round(time.monotonic() - self.started, 2),
        }
        curation = None
        if gaps:
            curation = fallback.request(
                gaps, profile_name=options.profile, generated_at=generated_at, snapshot_path=options.output,
                profile_summary=profile.summary(), rejected=rows_builder.rejected + soft_rejected,
                limit=int(self.config["fallback"]["rejectedLimit"]),
            )
        return snapshot, details, curation
