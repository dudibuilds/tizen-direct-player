"""Row assembly: New episodes, New for you, Because you watched X, Top picks.

Rows are filled in allocation order (new episodes, new for you, because-you-watched, top picks)
and a title appears in only one row. Playability is checked lazily, only for titles about to be
shown, so a nightly run stays within its Torrentio budget.
"""

from collections import Counter
from dataclasses import dataclass, field

from .language import language_class
from .ranking import Scored, age_days, content_link, mmr_select, release_year


@dataclass
class RowResult:
    id: str
    title: str
    kind: str
    items: list = field(default_factory=list)
    seed: str = None


def person_note(notes, minimum=0.3):
    for role, name, value in notes:
        if role in {"director", "writer", "cast"} and value >= minimum:
            return role, name
    return None


def genre_notes(notes, count=2):
    return [name for role, name, value in notes if role == "genre" and value > 0][:count]


def catalog_label(candidate):
    for source in sorted(candidate.sources):
        info = candidate.sources[source]
        if source.startswith("addon:") and info.get("label"):
            return info["label"]
    return None


def new_reason(scored, now, new_days):
    candidate = scored.candidate
    prefix = "New"
    if candidate.type == "series" and candidate.latest_season_start and candidate.released \
            and (now.date() - candidate.released).days > new_days:
        prefix = "New season"
    label = catalog_label(candidate) if candidate.trusted_new else None
    if label and prefix == "New":
        prefix = f"New on {label}"
    person = person_note(scored.notes)
    if person:
        role, name = person
        return f"{prefix} · {'with' if role == 'cast' else 'from'} {name}"
    genres = genre_notes(scored.notes)
    return f"{prefix} · {' & '.join(genres)}" if genres else prefix


def pick_reason(scored):
    person = person_note(scored.notes, 0.4)
    if person:
        role, name = person
        return {"director": f"From director {name}", "writer": f"From writer {name}"}.get(role, f"With {name}")
    genres = genre_notes(scored.notes)
    if genres:
        return f"Matches your taste for {' & '.join(genres)}"
    rating = scored.candidate.rating
    if rating is not None and rating >= 7.5:
        return f"Highly rated · {scored.candidate.genres[0]}" if scored.candidate.genres else "Highly rated"
    return "Popular now"


def item_json(scored, reason, verdict, score=None):
    candidate = scored.candidate
    item = {"id": candidate.id, "type": candidate.type, "name": candidate.name}
    if candidate.poster:
        item["poster"] = candidate.poster
    item["reason"] = reason
    item["score"] = round(score if score is not None else scored.score, 4)
    if candidate.year:
        item["year"] = candidate.year
    if candidate.genres:
        item["genres"] = list(candidate.genres)
    if candidate.new_episode:
        item["videoId"] = candidate.new_episode["videoId"]
        item["released"] = candidate.new_episode["released"]
    item["playability"] = verdict.status if verdict is not None else "unchecked"
    return item


class RowBuilder:
    def __init__(self, profile, ranker, checker, config, now):
        self.profile = profile
        self.ranker = ranker
        self.checker = checker
        self.config = config
        self.rows_config = config["rows"]
        self.ranking = config["ranking"]
        self.playability = config["playability"]
        self.now = now
        self.used = set()
        self.filtered = Counter()
        self.unplayable = Counter()
        self.verdicts = {}
        self.counted = set()
        self.rejected = []          # (scored, reason) for the curation request

    def accept(self, scored):
        verdict = self.verdicts.get(scored.id)
        if verdict is None:
            verdict = self.checker.check(scored.candidate, scored.language)
            self.verdicts[scored.id] = verdict
        if verdict.status == "playable":
            return True
        if verdict.status == "unknown" and self.playability.get("includeUnknown", True):
            return True
        if verdict.status == "unchecked" and self.playability.get("includeUnchecked", False):
            return True
        if scored.id not in self.counted:
            self.counted.add(scored.id)
            reason = f"unplayable: {verdict.reason}" if verdict.status == "unplayable" else verdict.status
            self.filtered["unplayable" if verdict.status == "unplayable" else f"playability_{verdict.status}"] += 1
            if verdict.status == "unplayable":
                self.unplayable[verdict.reason] += 1
            self.rejected.append((scored, reason))
        return False

    def select(self, items, size, key=None):
        return mmr_select(
            [item for item in items if item.id not in self.used], size,
            lambda_=self.ranking["mmrLambda"], max_genre_share=self.ranking["maxDominantGenreShare"],
            max_per_franchise=self.ranking["maxPerFranchise"], accept=self.accept,
            key=key or (lambda item: item.score), rejected=self.filtered,
        )

    def within_years(self, scored, years):
        year = release_year(scored.candidate)
        return year is not None and year >= self.now.year - years

    # ---- rows ----

    def new_episodes(self, candidates):
        row = RowResult("new-episodes", "New episodes of your shows", "new-episodes")
        size = self.rows_config["rowSize"]
        ordered = sorted(candidates, key=lambda c: (c.new_episode["released"], c.id), reverse=True)
        for candidate in ordered:
            if len(row.items) >= size:
                break
            if not (candidate.name or "").strip():
                self.filtered["no_name"] += 1
                continue
            scored = self.ranker.score(candidate)
            if not self.accept(scored):
                continue
            verdict = self.verdicts.get(candidate.id)
            row.items.append(item_json(scored, candidate.new_episode["reason"], verdict))
            self.used.add(candidate.id)
        return row

    def new_for_you(self, eligible):
        row = RowResult("new-for-you", "New for you", "new-for-you")
        new_days = self.rows_config["newDays"]
        pool = [scored for scored in eligible
                if _is_new(age_days(scored.candidate, self.now), new_days)
                and self.within_years(scored, self.config["filters"]["releaseWindowYears"])]
        for scored in self.select(pool, self.rows_config["rowSize"]):
            row.items.append(item_json(scored, new_reason(scored, self.now, new_days), self.verdicts.get(scored.id)))
            self.used.add(scored.id)
        return row

    def because(self, eligible):
        rows = []
        thin = []
        settings = self.rows_config
        window = self.config["filters"]["becauseWindowYears"]
        # Look at twice as many seeds as rows so a seed with no fresh related titles does not cost a row.
        for seed in self.profile.seeds(settings["becauseRows"] * 2, settings["seedMinWeight"]):
            if len(rows) >= settings["becauseRows"]:
                break
            meta = self.profile.meta[seed.id]
            name = seed.name or meta.name
            linked = []
            for scored in eligible:
                if scored.id == seed.id or not self.within_years(scored, window):
                    continue
                link = max(scored.candidate.seeds.get(seed.id, 0.0), content_link(meta, scored.candidate))
                if link >= settings["becauseMinLink"]:
                    linked.append((0.5 * link + 0.5 * scored.score, scored))
            row_score = {scored.id: value for value, scored in linked}
            picked = self.select([scored for _, scored in linked], settings["becauseRowSize"],
                                 key=lambda item: row_score[item.id])
            if len(picked) < settings["becauseMinItems"]:
                thin.append({"seed": seed.id, "name": name, "have": len(picked), "want": settings["becauseMinItems"]})
                continue
            row = RowResult(f"because-{seed.id}", f"Because you watched {name}", "because", seed=seed.id)
            for scored in picked:
                row.items.append(item_json(scored, f"Because you watched {name}", self.verdicts.get(scored.id),
                                           row_score[scored.id]))
                self.used.add(scored.id)
            rows.append(row)
        return rows, thin

    def top_picks(self, eligible):
        row = RowResult("top-picks", "Top picks", "top-picks")
        floor = self.rows_config.get("topPicksMinAffinity")
        has_taste = any(state.weight > 0 for state in self.profile.titles.values())
        pool = [scored for scored in eligible
                if self.within_years(scored, self.config["filters"]["topPicksWindowYears"])
                and (floor is None or not has_taste or scored.parts.get("affinity", 0.0) > floor)]
        for scored in self.select(pool, self.rows_config["rowSize"]):
            row.items.append(item_json(scored, pick_reason(scored), self.verdicts.get(scored.id)))
            self.used.add(scored.id)
        return row

    def build(self, eligible, new_episode_candidates):
        rows = [self.new_episodes(new_episode_candidates), self.new_for_you(eligible)]
        because_rows, thin = self.because(eligible)
        top = self.top_picks(eligible)
        return [rows[0], rows[1], *because_rows, top], thin


def _is_new(age, new_days):
    return age is not None and age <= new_days


def language_counts(scored_items):
    return dict(sorted(Counter(item.language for item in scored_items).items()))


__all__ = ["RowBuilder", "RowResult", "Scored", "language_counts", "language_class"]
