"""Recent releases from the local IMDb datasets (no network)."""

import math

from ..candidates import Candidate, poster_for
from .base import Source


class ImdbRecentSource(Source):
    name = "imdb"

    def enabled(self, ctx):
        if not ctx.settings(self.name).get("enabled", True):
            return False, "disabled in config"
        if ctx.imdb is None:
            return False, "IMDb datasets not found (run scripts/refresh-imdb.sh)"
        return True, None

    def collect(self, ctx, report):
        settings = ctx.settings(self.name)
        first_year = ctx.now.year - int(settings.get("years", 2)) + 1
        min_votes = int(settings.get("minVotes", 1500))
        min_rating = float(settings.get("minRating", 6.3))
        eligible = [
            title for title in ctx.imdb.recent
            if first_year <= title.year <= ctx.now.year and title.votes >= min_votes and title.rating >= min_rating
        ]
        eligible.sort(key=lambda title: (-(title.rating * math.log10(max(10, title.votes))), title.identifier))
        items = []
        for rank, title in enumerate(eligible[:int(settings.get("limit", 400))]):
            items.append(Candidate(
                id=title.identifier, type=title.kind, name=title.name, year=title.year, genres=title.genres,
                rating=title.rating, votes=title.votes, poster=poster_for(title.identifier),
                sources={"imdb": {"rank": rank}},
            ))
        report.counts["eligible"] = len(eligible)
        return items
