"""Trakt (optional, only with a user-supplied client id): trending, anticipated and related titles.

Trakt results include IMDb ids directly, so no id mapping is needed.
"""

import urllib.parse

from ..candidates import Candidate, is_imdb_id, parse_date, poster_for
from ..language import country_name
from .base import Source

API = "https://api.trakt.tv"
GENRES = {"science-fiction": "Sci-Fi", "reality": "Reality-TV", "game-show": "Game-Show", "talk-show": "Talk-Show",
          "children": "Family", "suspense": "Thriller", "anime": "Anime", "sporting-events": "Sport"}


def map_genre(slug):
    return GENRES.get(slug, slug.replace("-", " ").title().replace(" ", "-"))


def parse_item(item, kind):
    """Trakt movie/show object (extended=full) -> Candidate."""
    if not isinstance(item, dict):
        return None
    imdb_id = (item.get("ids") or {}).get("imdb")
    if not is_imdb_id(imdb_id):
        return None
    released = parse_date(item.get("released") if kind == "movie" else item.get("first_aired"))
    country = country_name(item.get("country"))
    return Candidate(
        id=imdb_id, type="movie" if kind == "movie" else "series", name=item.get("title") or "",
        year=item.get("year") if isinstance(item.get("year"), int) else (released.year if released else None),
        released=released,
        genres=tuple(map_genre(slug) for slug in item.get("genres") or () if isinstance(slug, str)),
        countries=(country,) if country else (),
        original_language=item.get("language"),
        ext_rating=item.get("rating") or None, ext_votes=item.get("votes") or None,
        poster=poster_for(imdb_id),
    )


class TraktSource(Source):
    name = "trakt"

    def enabled(self, ctx):
        settings = ctx.settings(self.name)
        if not settings.get("enabled", True):
            return False, "disabled in config"
        if not settings.get("clientId"):
            return False, "no Trakt client id configured"
        return True, None

    def get(self, ctx, path, params):
        headers = {"trakt-api-version": "2", "trakt-api-key": ctx.settings(self.name)["clientId"],
                   "Content-Type": "application/json"}
        url = f"{API}/{path}?" + urllib.parse.urlencode(sorted({"extended": "full", **params}.items()))
        payload = ctx.http.get_json(url, ttl=ctx.ttl(self.name, 24), headers=headers)
        return payload if isinstance(payload, list) else []

    def collect(self, ctx, report):
        limit = int(ctx.settings(self.name).get("limit", 40))
        items = []
        for label, kind, path in (("trending", "movie", "movies/trending"), ("trending", "show", "shows/trending"),
                                  ("anticipated", "movie", "movies/anticipated"),
                                  ("anticipated", "show", "shows/anticipated")):
            if ctx.expired(self.name):
                return items
            try:
                rows = self.get(ctx, path, {"limit": limit})
            except Exception as error:  # noqa: BLE001
                report.error(error)
                continue
            for rank, row in enumerate(rows):
                candidate = parse_item(row.get(kind) if isinstance(row, dict) else None, kind)
                if candidate:
                    candidate.sources[f"trakt:{label}"] = {"rank": rank}
                    items.append(candidate)
        for seed in ctx.profile.seeds(int(ctx.settings(self.name).get("seeds", 6)), ctx.config["rows"]["seedMinWeight"]):
            if ctx.expired(self.name):
                break
            kind = "show" if seed.type == "series" else "movie"
            try:
                rows = self.get(ctx, f"{kind}s/{seed.id}/related", {"limit": 20})
            except Exception as error:  # noqa: BLE001
                report.error(error)
                continue
            for rank, row in enumerate(rows):
                candidate = parse_item(row, kind)
                if candidate:
                    candidate.sources["trakt:related"] = {"rank": rank}
                    candidate.seeds[seed.id] = 0.9 * (1 - rank / 40)
                    items.append(candidate)
        return items
