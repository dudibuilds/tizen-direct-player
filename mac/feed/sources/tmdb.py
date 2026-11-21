"""TMDB (optional, only with a user-supplied key): trending, now playing / on the air, discover,
and per-title recommendations/similar for the user's top titles ("Because you watched X").

TMDB list results carry TMDB ids only. Each new id costs one details call
(`append_to_response=external_ids,keywords,credits`) that yields the IMDb id plus keywords,
people, countries and original language. Details are cached for ~90 days and capped per run.
"""

import urllib.parse
from datetime import timedelta

from ..candidates import Candidate, is_imdb_id, parse_date, parse_year
from ..language import country_name
from .base import Source

API = "https://api.themoviedb.org/3"
IMAGE = "https://image.tmdb.org/t/p/w342"
GENRES = {
    "Science Fiction": ("Sci-Fi",), "Sci-Fi & Fantasy": ("Sci-Fi", "Fantasy"),
    "Action & Adventure": ("Action", "Adventure"), "War & Politics": ("War",), "Reality": ("Reality-TV",),
    "Talk": ("Talk-Show",), "Kids": ("Family",), "TV Movie": (),
}
DETAILS_TTL = 90 * 86400


def map_genres(names):
    result = []
    for name in names:
        for genre in GENRES.get(name, (name,)):
            if genre not in result:
                result.append(genre)
    return tuple(result)


class Tmdb:
    def __init__(self, http, settings, ttl):
        self.http = http
        self.api_key = settings.get("apiKey")
        self.token = settings.get("readToken")
        self.ttl = ttl
        self.lookups_left = int(settings.get("maxDetailLookups", 300))

    def get(self, path, params=None, ttl=None):
        query = dict(params or {})
        headers = {}
        if self.token:
            headers["Authorization"] = f"Bearer {self.token}"
        else:
            query["api_key"] = self.api_key
        url = f"{API}/{path}" + ("?" + urllib.parse.urlencode(sorted(query.items())) if query else "")
        return self.http.get_json(url, ttl=self.ttl if ttl is None else ttl, headers=headers)

    def results(self, path, params=None):
        payload = self.get(path, params)
        return [item for item in payload.get("results") or () if isinstance(item, dict)] if isinstance(payload, dict) else []

    def find(self, imdb_id):
        payload = self.get(f"find/{imdb_id}", {"external_source": "imdb_id"}, ttl=DETAILS_TTL)
        for key, kind in (("movie_results", "movie"), ("tv_results", "tv")):
            for item in payload.get(key) or ():
                if isinstance(item, dict) and item.get("id"):
                    return kind, item["id"]
        return None

    def details(self, kind, tmdb_id):
        return self.get(f"{kind}/{tmdb_id}", {"append_to_response": "external_ids,keywords,credits"}, ttl=DETAILS_TTL)

    def candidate(self, kind, item, now):
        """List result -> Candidate via a (cached) details call; None when no IMDb id or budget is spent."""
        tmdb_id = item.get("id")
        if not tmdb_id:
            return None
        if self.lookups_left <= 0:
            return None
        before = self._requests()
        details = self.details(kind, tmdb_id)
        if self._requests() > before:          # cache hits are free
            self.lookups_left -= 1
        return candidate_from_details(kind, details)

    def _requests(self):
        stats = getattr(self.http, "stats", {})
        return stats["api.themoviedb.org"]["requests"] if "api.themoviedb.org" in stats else 0


def candidate_from_details(kind, details):
    if not isinstance(details, dict):
        return None
    imdb_id = details.get("imdb_id") or (details.get("external_ids") or {}).get("imdb_id")
    if not is_imdb_id(imdb_id):
        return None
    type_ = "movie" if kind == "movie" else "series"
    released = parse_date(details.get("release_date") if kind == "movie" else details.get("first_air_date"))
    keywords_block = details.get("keywords") or {}
    keywords = keywords_block.get("keywords") or keywords_block.get("results") or ()
    credits = details.get("credits") or {}
    directors = tuple(person.get("name") for person in credits.get("crew") or ()
                      if isinstance(person, dict) and person.get("job") == "Director" and person.get("name"))
    if kind == "tv":
        directors = tuple(person.get("name") for person in details.get("created_by") or ()
                          if isinstance(person, dict) and person.get("name")) or directors
    cast = tuple(person.get("name") for person in sorted(
        (person for person in credits.get("cast") or () if isinstance(person, dict) and person.get("name")),
        key=lambda person: person.get("order", 999))[:8])
    countries = tuple(filter(None, (country_name(item.get("iso_3166_1")) for item in details.get("production_countries") or ()
                                    if isinstance(item, dict))))
    if kind == "tv" and details.get("origin_country"):
        countries = tuple(filter(None, (country_name(code) for code in details["origin_country"]))) or countries
    poster = details.get("poster_path")
    return Candidate(
        id=imdb_id, type=type_, name=details.get("title") or details.get("name") or "",
        year=released.year if released else parse_year(details.get("first_air_date") or details.get("release_date")),
        released=released,
        genres=map_genres(genre.get("name") for genre in details.get("genres") or () if isinstance(genre, dict)),
        countries=countries, directors=directors[:3], cast=cast,
        keywords=tuple(sorted({item.get("name") for item in keywords if isinstance(item, dict) and item.get("name")}))[:20],
        original_language=details.get("original_language"),
        ext_rating=details.get("vote_average") or None, ext_votes=details.get("vote_count") or None,
        poster=f"{IMAGE}{poster}" if poster else None,
        enriched=True,
    )


class TmdbSource(Source):
    name = "tmdb"

    def enabled(self, ctx):
        settings = ctx.settings(self.name)
        if not settings.get("enabled", True):
            return False, "disabled in config"
        if not (settings.get("apiKey") or settings.get("readToken")):
            return False, "no TMDB key configured"
        return True, None

    def collect(self, ctx, report):
        settings = ctx.settings(self.name)
        tmdb = Tmdb(ctx.http, settings, ctx.ttl(self.name, 24))
        since = (ctx.now - timedelta(days=int(settings.get("discoverDays", 180)))).date().isoformat()
        languages = "|".join(settings.get("originalLanguages") or ())
        region = settings.get("region")
        lists = [
            ("trending", "movie", "trending/movie/week", {}),
            ("trending", "tv", "trending/tv/week", {}),
            ("nowPlaying", "movie", "movie/now_playing", {"region": region} if region else {}),
            ("onTheAir", "tv", "tv/on_the_air", {}),
            ("discover", "movie", "discover/movie", {"sort_by": "popularity.desc", "vote_count.gte": 100,
                                                     "primary_release_date.gte": since,
                                                     **({"with_original_language": languages} if languages else {})}),
            ("discover", "tv", "discover/tv", {"sort_by": "popularity.desc", "vote_count.gte": 50,
                                               "first_air_date.gte": since,
                                               **({"with_original_language": languages} if languages else {})}),
        ]
        items = []
        for label, kind, path, params in lists:
            if ctx.expired(self.name):
                return items
            try:
                results = tmdb.results(path, params)
            except Exception as error:  # noqa: BLE001
                report.error(error)
                continue
            for rank, result in enumerate(results):
                candidate = self._safe_candidate(tmdb, kind, result, ctx, report)
                if candidate:
                    candidate.sources[f"tmdb:{label}"] = {"rank": rank}
                    items.append(candidate)
        for seed in ctx.profile.seeds(int(settings.get("seeds", 6)), ctx.config["rows"]["seedMinWeight"]):
            if ctx.expired(self.name):
                break
            try:
                found = tmdb.find(seed.id)
            except Exception as error:  # noqa: BLE001
                report.error(error)
                continue
            if not found:
                report.counts["seed_not_found"] += 1
                continue
            kind, tmdb_id = found
            for relation, strength in (("recommendations", 1.0), ("similar", 0.8)):
                try:
                    results = tmdb.results(f"{kind}/{tmdb_id}/{relation}")
                except Exception as error:  # noqa: BLE001
                    report.error(error)
                    continue
                for rank, result in enumerate(results[:20]):
                    candidate = self._safe_candidate(tmdb, kind, result, ctx, report)
                    if candidate:
                        candidate.sources[f"tmdb:{relation}"] = {"rank": rank}
                        candidate.seeds[seed.id] = strength * (1 - rank / 40)
                        items.append(candidate)
        report.counts["detail_lookups_left"] = tmdb.lookups_left
        return items

    @staticmethod
    def _safe_candidate(tmdb, kind, result, ctx, report):
        try:
            candidate = tmdb.candidate(kind, result, ctx.now)
        except Exception as error:  # noqa: BLE001
            report.error(error)
            return None
        if candidate is None:
            report.counts["unmapped"] += 1
        return candidate
