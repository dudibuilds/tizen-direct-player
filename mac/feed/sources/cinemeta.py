"""Cinemeta (and any Stremio catalog add-on): catalog parsing, meta fetching, last-videos.

Cinemeta quirks: catalog URLs 307-redirect (urllib follows), the "year" catalog requires
genre=YYYY, `imdbRating` may be "" and `released` may be null.
"""

import re
import urllib.parse
from datetime import datetime, timezone

from ..candidates import Candidate, is_imdb_id, parse_date, parse_datetime, parse_year, poster_for
from .base import Source

CINEMETA = "https://v3-cinemeta.strem.io"
CINEMETA_GENRES = {
    "movie": {"Action", "Adventure", "Animation", "Biography", "Comedy", "Crime", "Documentary", "Drama",
              "Family", "Fantasy", "History", "Horror", "Mystery", "Romance", "Sci-Fi", "Sport", "Thriller",
              "War", "Western"},
}
CINEMETA_GENRES["series"] = CINEMETA_GENRES["movie"] | {"Reality-TV", "Talk-Show", "Game-Show"}
MANIFEST_TAIL = re.compile(r"/manifest\.json(?:\?.*)?$", re.IGNORECASE)
PAGE_SIZE = 50


def addon_base(url):
    return MANIFEST_TAIL.sub("", url.split("#", 1)[0]).rstrip("/")


def catalog_url(base, type_, catalog_id, extra=None):
    path = f"{base}/catalog/{urllib.parse.quote(type_, safe='')}/{urllib.parse.quote(catalog_id, safe='')}"
    if extra:
        path += "/" + "&".join(f"{key}={urllib.parse.quote(str(value), safe='')}" for key, value in extra)
    return path + ".json"


def _strings(value, limit=None):
    if isinstance(value, str):
        value = [value]
    if not isinstance(value, list):
        return ()
    items = tuple(item.strip() for item in value if isinstance(item, str) and item.strip())
    return items[:limit] if limit else items


def parse_videos(meta, now):
    """Released regular-season videos as (season, episode, released datetime, id), sorted."""
    videos = []
    for video in meta.get("videos") or ():
        if not isinstance(video, dict) or not video.get("id"):
            continue
        season = video.get("season")
        episode = video.get("episode", video.get("number"))
        if not isinstance(season, int) or not isinstance(episode, int) or season <= 0:
            continue
        released = parse_datetime(video.get("released") or video.get("firstAired"))
        videos.append((season, episode, released, str(video["id"])))
    videos.sort(key=lambda item: (item[0], item[1], item[2] or datetime.min.replace(tzinfo=timezone.utc)))
    return videos


def parse_meta(meta, fallback_type=None, now=None):
    """A Stremio meta (catalog entry or full meta) -> Candidate, or None without an IMDb id."""
    if not isinstance(meta, dict):
        return None
    identifier = meta.get("id")
    if not is_imdb_id(identifier) and is_imdb_id(meta.get("imdb_id")):
        identifier = meta["imdb_id"]
    if not is_imdb_id(identifier):
        return None
    type_ = meta.get("type") if meta.get("type") in {"movie", "series"} else fallback_type
    if type_ not in {"movie", "series"}:
        return None
    rating = None
    try:
        rating = float(meta.get("imdbRating")) if meta.get("imdbRating") not in (None, "") else None
    except (TypeError, ValueError):
        rating = None
    if rating is not None and rating <= 0:
        rating = None
    countries = tuple(part.strip() for part in str(meta.get("country") or "").split(",") if part.strip())
    candidate = Candidate(
        id=identifier, type=type_, name=str(meta.get("name") or "").strip(),
        year=parse_year(meta.get("releaseInfo") or meta.get("year")),
        released=parse_date(meta.get("released")),
        genres=_strings(meta.get("genres") or meta.get("genre")),
        countries=countries,
        directors=_strings(meta.get("director")),
        writers=_strings(meta.get("writer"), 6),
        cast=_strings(meta.get("cast"), 8),
        rating=rating,
        poster=meta.get("poster") if isinstance(meta.get("poster"), str) else poster_for(identifier),
    )
    candidate.enriched = bool(countries or candidate.cast or candidate.directors)
    if type_ == "series" and now is not None:
        videos = [video for video in parse_videos(meta, now) if video[2] is None or video[2] <= now]
        dated = [video for video in videos if video[2] is not None]
        if dated:
            candidate.latest_release = max(video[2] for video in dated).date()
            newest_season = max(video[0] for video in dated)
            candidate.latest_season_start = min(video[2] for video in dated if video[0] == newest_season).date()
        first = next((video for video in videos if (video[0], video[1]) == (1, 1)), videos[0] if videos else None)
        if first:
            candidate.first_video_id = first[3]
    return candidate


def parse_catalogs(manifest):
    """Port of AddonManifestParser.ParseCatalogs: [{type, id, name, supported, required}]."""
    catalogs = []
    for item in manifest.get("catalogs") or ():
        if not isinstance(item, dict) or not item.get("type") or not item.get("id"):
            continue
        supported = list(_strings(item.get("extraSupported")))
        required = list(_strings(item.get("extraRequired")))
        for extra in item.get("extra") or ():
            if not isinstance(extra, dict) or not extra.get("name"):
                continue
            if extra["name"] not in supported:
                supported.append(extra["name"])
            if extra.get("isRequired") is True and extra["name"] not in required:
                required.append(extra["name"])
        catalogs.append({"type": item["type"], "id": item["id"], "name": item.get("name") or item["id"],
                         "supported": supported, "required": required})
    return catalogs


def fetch_catalog(http, base, type_, catalog_id, extra, ttl):
    payload = http.get_json(catalog_url(base, type_, catalog_id, extra), ttl=ttl)
    metas = payload.get("metas") if isinstance(payload, dict) else None
    return metas if isinstance(metas, list) else []


def fetch_meta(http, base, type_, identifier, ttl, now):
    payload = http.get_json(f"{base}/meta/{type_}/{urllib.parse.quote(identifier, safe='')}.json", ttl=ttl)
    meta = payload.get("meta") if isinstance(payload, dict) else None
    return parse_meta(meta, type_, now) if meta else None


def fetch_last_videos(http, base, ids, ttl, chunk=50):
    """Cinemeta `last-videos` (series, up to 100 ids per call): full metas with their newest videos."""
    metas = []
    ordered = sorted(set(ids))
    for start in range(0, len(ordered), chunk):
        part = ordered[start:start + chunk]
        url = catalog_url(base, "series", "last-videos", [("lastVideosIds", ",".join(part))])
        url = url.replace("%2C", ",")
        payload = http.get_json(url, ttl=ttl)
        found = payload.get("metasDetailed") or payload.get("metas") if isinstance(payload, dict) else None
        metas.extend(meta for meta in found or () if isinstance(meta, dict))
    return metas


class CinemetaSource(Source):
    name = "cinemeta"

    def specs(self, ctx):
        settings = ctx.settings(self.name)
        specs = []
        genres = ctx.profile.top_features("genre", int(settings.get("genreCatalogs", 0))) if ctx.profile else []
        for type_ in ("movie", "series"):
            for catalog in settings.get("catalogs", ()):
                specs.append((type_, catalog, [], catalog))
            for offset in range(int(settings.get("yearCatalogYears", 0))):
                year = ctx.now.year - offset
                specs.append((type_, "year", [("genre", year)], f"new {year}"))
            for genre in genres:
                if genre in CINEMETA_GENRES[type_]:
                    specs.append((type_, "top", [("genre", genre)], f"top {genre}"))
        return specs

    def collect(self, ctx, report):
        settings = ctx.settings(self.name)
        base = settings.get("baseUrl", CINEMETA).rstrip("/")
        ttl = ctx.ttl(self.name)
        items = []
        for type_, catalog, extra, label in self.specs(ctx):
            for page in range(max(1, int(settings.get("pages", 1)))):
                if ctx.expired(self.name):
                    return items
                page_extra = list(extra) + ([("skip", page * PAGE_SIZE)] if page else [])
                try:
                    metas = fetch_catalog(ctx.http, base, type_, catalog, page_extra, ttl)
                except Exception as error:  # noqa: BLE001
                    report.error(error)
                    break
                for rank, meta in enumerate(metas):
                    candidate = parse_meta(meta, type_, ctx.now)
                    if candidate is None:
                        report.counts["skipped_non_imdb"] += 1
                        continue
                    candidate.sources["cinemeta"] = {"rank": page * PAGE_SIZE + rank, "catalog": label}
                    items.append(candidate)
                if len(metas) < PAGE_SIZE:
                    break
        return items


class CatalogAddonSource(Source):
    """Catalog add-ons the user installs by manifest URL (e.g. streaming-service "new on" catalogs)."""
    name = "catalogAddons"

    def enabled(self, ctx):
        settings = ctx.settings(self.name)
        if not settings.get("enabled", True):
            return False, "disabled in config"
        if not settings.get("manifests"):
            return False, "no catalog add-on manifests configured"
        return True, None

    def collect(self, ctx, report):
        settings = ctx.settings(self.name)
        ttl = ctx.ttl(self.name)
        items = []
        for entry in settings.get("manifests", ()):
            entry = {"url": entry} if isinstance(entry, str) else dict(entry)
            url = entry.get("url")
            if not url or not url.startswith(("http://", "https://")):
                report.error(f"invalid manifest entry: {entry!r}")
                continue
            try:
                manifest = ctx.http.get_json(url, ttl=max(ttl, 86400))
            except Exception as error:  # noqa: BLE001
                report.error(error)
                continue
            addon_id = str(manifest.get("id") or addon_base(url))
            wanted = set(entry.get("catalogs") or ())
            base = addon_base(url)
            for catalog in parse_catalogs(manifest):
                if catalog["type"] not in {"movie", "series"} or catalog["required"]:
                    continue
                if wanted and catalog["id"] not in wanted:
                    continue
                label = entry.get("label") or catalog["name"]
                pages = max(1, int(entry.get("pages", settings.get("pages", 1))))
                for page in range(pages if "skip" in catalog["supported"] else 1):
                    if ctx.expired(self.name):
                        return items
                    extra = [("skip", page * PAGE_SIZE)] if page else []
                    try:
                        metas = fetch_catalog(ctx.http, base, catalog["type"], catalog["id"], extra, ttl)
                    except Exception as error:  # noqa: BLE001
                        report.error(error)
                        break
                    for rank, meta in enumerate(metas):
                        candidate = parse_meta(meta, catalog["type"], ctx.now)
                        if candidate is None:
                            report.counts["skipped_non_imdb"] += 1
                            continue
                        candidate.sources[f"addon:{addon_id}"] = {"rank": page * PAGE_SIZE + rank, "label": label,
                                                                  "catalog": catalog["id"]}
                        candidate.trusted_new = bool(entry.get("trustedNew", False))
                        items.append(candidate)
                    if len(metas) < PAGE_SIZE:
                        break
        return items
