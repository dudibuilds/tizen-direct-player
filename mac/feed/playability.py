"""Playability check against the stream add-on set in playability.streamAddonUrl using the TV's rules.

Ported from core/Addons/StreamRanking.cs (parity F2-F10) for the Samsung RU7100: no AV1, no Dolby
Vision without an HDR10 base layer, no DTS, no TrueHD, no cam/screener, at most 6.5 GB (unknown
size passes, as on the TV), plus the general (F6) and anime (F7) language tiers. Regexes use
re.ASCII so \\b, \\w and \\d stay ASCII as in the C# ECMAScript-mode originals.

A title is playable when at least one stream passes every rule. Verdicts are cached on disk
(pass and fail TTLs differ); network failures are "unknown" and never cached.
"""

import re
import time
import urllib.parse
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from .language import ANIME, FOREIGN, LANGUAGE_NAMES, has_genre, has_preferred, preferred_code, subtitle_codes
from .storage import read_json, write_json

MAX_STREAM_SIZE_GB = 6.5
MIN_HEALTHY_SEEDERS = 50
FLAGS = re.ASCII | re.IGNORECASE


def R(pattern):
    return re.compile(pattern, FLAGS)


CAM = R(r"\b(cam|hdcam|telesync|telecine|dvdscr)\b|web[ ._-]*screener|\bscreener\b")
SEEDERS_EMOJI = re.compile("\U0001F464\\s*(\\d+)", re.ASCII)
SEEDERS_WORD = R(r"(?:seeders?|seeds?)\D{0,4}(\d+)")
SIZE = re.compile("(?:\U0001F4BE\\s*)?([0-9]+(?:\\.[0-9]+)?)\\s*(GB|MB)(?![A-Za-z0-9_])", FLAGS)
Q4K = R(r"2160p|\b4k\b")
Q1080 = R(r"1080p")
Q720 = R(r"720p")
H264 = R(r"\bh[ .]?264|x264|\bavc\b")
HEVC = R(r"\bh[ .]?265|x265|\bhevc\b")
AV1 = R(r"\bav1\b")
DOLBY_VISION = R(r"\b(?:dolby[ ._-]?vision|dovi|dv)\b")
HDR10 = R(r"\bhdr(?:10)?\b")
DTS = R(r"\bdts")
TRUEHD = R(r"\btrue[ ._-]?hd|\btruhd")

FLAG = re.compile("[\U0001F1E6-\U0001F1FF]{2}")
FLAG_GB = "\U0001F1EC\U0001F1E7"
FLAG_US = "\U0001F1FA\U0001F1F8"
MULTI_AUDIO = R(r"\b(?:dual|multi)[ ._-]*(?:audio|dub)\b|\bdual[ ._-]*lang(?:uage)?\b")
JAPANESE_AUDIO = R(r"\b(?:jpn|japanese|jap)[ ._-]*(?:audio|dub)?\b|\bja[ ._-]*audio\b")
JAPANESE_SUBS = R(r"\b(?:jpn|japanese|jap)[ ._-]*(?:subs?|subtitles?)\b")
ENGLISH_SUBS_ANIME = R(r"\b(?:eng|english)[ ._-]*(?:subs?|subtitles?)\b")
MULTI_SUBS = R(r"\bmulti[ ._-]*(?:subs?|subtitles?)\b")
ENGLISH_DUB = R(r"\b(?:eng|english)[ ._-]*(?:audio|dub(?:bed)?)\b")
ENGLISH_LABEL = R(r"\b(?:eng|english)[ ._-]*(?:audio|dub(?:bed)?|voice|ddp(?:[257]\.1)?|e-?ac-?3(?:[257]\.1)?"
                  r"|ac-?3(?:[257]\.1)?|aac(?:[257]\.1)?|truehd(?:[257]\.1)?|dts(?:[ ._-]*hd)?|[257]\.1|2\.0)\b")
ENGLISH_TAG = R(r"\beng\b(?![ ._-]*(?:subs?|subtitles?)\b)")
FOREIGN_TAG = R(r"(?:^|[ ._\[(-])(?:rus|ukr|ita|ger|fre|spa|pol|tur|hin|kor|chi)(?=$|[ ._\])-])(?![ ._-]*(?:subs?|subtitles?)\b)")
ENGLISH_SUBS = R(r"\b(?:eng|english)[ ._-]*(?:subs?|subtitles?)\b|\bengsub\b")
FOREIGN_AUDIO = R(r"\b(?:jpn|japanese|kor|korean|spa|spanish|latino|fre|french|ita|italian|ger|german|rus|russian"
                  r"|hin|hindi|ara|arabic|tur|turkish|por|portuguese|chi|chinese|mandarin|cantonese)"
                  r"[ ._-]*(?:audio|dub(?:bed)?|voice)\b")
_PREFERRED_SUBS = {}


def preferred_subs_pattern():
    """Release-name marker for subtitles in the preferred language ("spa subs", "spanish subtitles", "spasub")."""
    code = preferred_code()
    if code not in _PREFERRED_SUBS:
        long, name = LANGUAGE_NAMES.get(code, (code, code))
        _PREFERRED_SUBS[code] = R(rf"\b(?:{long}|{name})[ ._-]*(?:subs?|subtitles?)\b|\b{long}sub\b")
    return _PREFERRED_SUBS[code]


INFO_HASH = re.compile(r"^[0-9a-fA-F]{40}$")
AMBIGUOUS_ANIMATION_COUNTRIES = {"South Korea", "China", "Taiwan"}


@dataclass
class Stream:
    name: str = None
    title: str = None
    description: str = None
    info_hash: str = None
    file_idx: int = None
    url: str = None
    external_url: str = None
    filename: str = None
    video_size: int = None
    proxy_headers: bool = False

    @property
    def identity(self):
        if self.info_hash:
            return f"{self.info_hash.lower()}:{self.file_idx if self.file_idx is not None else 'auto'}"
        return self.url or None

    @property
    def text(self):
        return " ".join(part or "" for part in (self.name, self.title, self.description, self.filename))


def _str(value):
    return value if isinstance(value, str) else None


def parse_stream(item):
    hints = item.get("behaviorHints") if isinstance(item.get("behaviorHints"), dict) else {}
    file_idx = item.get("fileIdx")
    if isinstance(file_idx, float) and file_idx.is_integer():
        file_idx = int(file_idx)
    size = hints.get("videoSize")
    return Stream(
        name=_str(item.get("name")), title=_str(item.get("title")), description=_str(item.get("description")),
        info_hash=_str(item.get("infoHash")),
        file_idx=file_idx if isinstance(file_idx, int) and not isinstance(file_idx, bool) else None,
        url=_str(item.get("url")), external_url=_str(item.get("externalUrl")), filename=_str(hints.get("filename")),
        video_size=int(size) if isinstance(size, (int, float)) and not isinstance(size, bool) and size > 0 else None,
        proxy_headers=isinstance(hints.get("proxyHeaders"), dict),
    )


def deduplicate(streams):
    seen, result = set(), []
    for stream in streams:
        identity = stream.identity
        if identity is None or identity in seen:
            continue
        seen.add(identity)
        result.append(stream)
    return result


def is_web_url(value):
    if not value:
        return False
    parts = urllib.parse.urlsplit(value)
    return parts.scheme in {"http", "https"} and bool(parts.hostname) and not parts.username


def hard_filter(stream, text):
    """Why the stream can never play on the RU7100, or None (StreamRanking.HardFilter)."""
    has_hash = bool(stream.info_hash and INFO_HASH.match(stream.info_hash))
    if stream.external_url:
        return "external link"
    if not has_hash and not is_web_url(stream.url):
        return "no playable url or info hash"
    if not has_hash and stream.proxy_headers:
        return "needs proxy headers"
    if CAM.search(text):
        return "cam/screener"
    if AV1.search(text):
        return "AV1 video"
    if DOLBY_VISION.search(text) and not HDR10.search(text):
        return "Dolby Vision without HDR10"
    if DTS.search(text):
        return "DTS audio"
    if TRUEHD.search(text):
        return "TrueHD audio"
    return None


def parse_size(text):
    match = SIZE.search(text or "")
    if not match:
        return None
    bytes_ = round(float(match.group(1)) * (1e9 if match.group(2).upper() == "GB" else 1e6))
    return bytes_ if bytes_ > 0 else None


def parse_seeders(text):
    match = SEEDERS_EMOJI.search(text) or SEEDERS_WORD.search(text)
    return (int(match.group(1)), True) if match else (0, False)


@dataclass
class GeneralLanguages:
    english_audio: bool = False
    multi_audio: bool = False
    preferred_subtitles: bool = False
    english_subtitles: bool = False
    multi_subtitles: bool = False
    foreign_audio_only: bool = False
    tier: int = 0


def detect_general_languages(text):
    """F6 tiers: 7 English audio + preferred-language subs ... 0 nothing detected, -1 foreign audio only."""
    text = text or ""
    flag_english = flag_foreign = False
    for flag in FLAG.findall(text):
        if flag in (FLAG_GB, FLAG_US):
            flag_english = True
        else:
            flag_foreign = True
    english = bool(ENGLISH_LABEL.search(text) or ENGLISH_TAG.search(text)) or flag_english
    foreign = flag_foreign or bool(FOREIGN_TAG.search(text))
    multi = bool(MULTI_AUDIO.search(text)) or (english and foreign)
    result = GeneralLanguages(
        multi_audio=multi, english_audio=english and not foreign,
        preferred_subtitles=has_preferred() and bool(preferred_subs_pattern().search(text)), english_subtitles=bool(ENGLISH_SUBS.search(text)),
        multi_subtitles=bool(MULTI_SUBS.search(text)),
    )
    result.foreign_audio_only = (bool(FOREIGN_AUDIO.search(text)) or foreign) and not english and not multi
    if result.english_audio and result.preferred_subtitles:
        result.tier = 7
    elif result.english_audio and result.english_subtitles:
        result.tier = 6
    elif result.english_audio and result.multi_subtitles:
        result.tier = 5
    elif result.english_audio:
        result.tier = 4
    elif multi and result.preferred_subtitles:
        result.tier = 3
    elif multi and result.english_subtitles:
        result.tier = 2
    elif multi:
        result.tier = 1
    elif result.foreign_audio_only:
        result.tier = -1
    return result


@dataclass
class AnimeLanguages:
    japanese_audio: bool = False
    multi_audio: bool = False
    english_subtitles: bool = False
    multi_subtitles: bool = False
    english_dub_only: bool = False
    tier: int = 0

    @property
    def original_audio(self):
        return self.japanese_audio or self.multi_audio or self.english_subtitles


def detect_anime_languages(text):
    """F7 tiers: 5 Japanese audio + English subs ... -1 English dub only."""
    text = text or ""
    result = AnimeLanguages(
        multi_audio=bool(MULTI_AUDIO.search(text)),
        japanese_audio=bool(JAPANESE_AUDIO.search(text)) and not JAPANESE_SUBS.search(text),
        english_subtitles=bool(ENGLISH_SUBS_ANIME.search(text)),
        multi_subtitles=bool(MULTI_SUBS.search(text)),
    )
    result.english_dub_only = bool(ENGLISH_DUB.search(text)) and not result.japanese_audio and not result.multi_audio
    if result.japanese_audio and result.english_subtitles:
        result.tier = 5
    elif (result.japanese_audio and result.multi_subtitles) or (result.multi_audio and result.english_subtitles):
        result.tier = 4
    elif result.multi_audio and result.multi_subtitles:
        result.tier = 3
    elif result.japanese_audio or result.english_subtitles:
        result.tier = 2
    elif result.multi_audio or result.multi_subtitles:
        result.tier = 1
    if result.english_dub_only:
        result.tier = -1
    return result


@dataclass
class Verdict:
    status: str                       # playable | unplayable | unknown | unchecked
    reason: str = ""
    video_id: str = None
    best: dict = None
    rejects: dict = field(default_factory=dict)
    cached: bool = False

    @property
    def playable(self):
        return self.status == "playable"


def language_ok(text, anime, language_class, foreign_mode, preferred_subs):
    if anime:
        languages = detect_anime_languages(text)
        if not languages.original_audio or languages.tier < 1:
            return False, "anime without Japanese audio or English subtitles", languages.tier
        return True, None, languages.tier
    languages = detect_general_languages(text)
    if language_class == FOREIGN:
        dub = languages.tier >= 1
        if foreign_mode == "dub" and not dub:
            return False, "foreign original without English audio", languages.tier
        if foreign_mode == "preferred_subs" and not preferred_subs:
            return False, "foreign original without subtitles in the preferred language", languages.tier
        if foreign_mode == "dub_or_preferred_subs" and not (dub or preferred_subs):
            return False, "foreign original without English audio or preferred-language subtitles", languages.tier
        if foreign_mode == "exclude":
            return False, "foreign original", languages.tier
        return True, None, languages.tier
    if languages.tier < 0:
        return False, "foreign audio only", languages.tier
    return True, None, languages.tier


def evaluate(items, *, anime=False, language_class="english", foreign_mode="dub", preferred_subs=False,
             max_size_gb=MAX_STREAM_SIZE_GB, min_seeders=0):
    """Apply the TV rules to an add-on `streams` list. Playable when one stream passes all of them."""
    streams = deduplicate(parse_stream(item) for item in items or () if isinstance(item, dict))
    if not streams:
        return Verdict("unplayable", "no streams")
    max_bytes = min(max_size_gb, MAX_STREAM_SIZE_GB) * 1e9
    rejects = Counter()
    passing = []
    for stream in streams:
        text = stream.text
        reason = hard_filter(stream, text)
        if reason:
            rejects[reason] += 1
            continue
        size = parse_size(text) or stream.video_size
        if size is not None and size > max_bytes:
            rejects["over the size cap"] += 1
            continue
        seeders, known = parse_seeders(text)
        if stream.info_hash and known and seeders < min_seeders:
            rejects["too few seeders"] += 1
            continue
        ok, why, tier = language_ok(text, anime, language_class, foreign_mode, preferred_subs)
        if not ok:
            rejects[why] += 1
            continue
        quality = "4k" if Q4K.search(text) else "1080p" if Q1080.search(text) else "720p" if Q720.search(text) else "unknown"
        passing.append((tier, seeders, -(size or 0), stream, quality, size))
    if passing:
        tier, seeders, _, stream, quality, size = max(passing, key=lambda entry: entry[:3])
        best = {"quality": quality, "seeders": seeders, "languageTier": tier,
                "sizeGb": round(size / 1e9, 2) if size else None}
        return Verdict("playable", "ok", best=best, rejects=dict(rejects))
    ordered = [name for name, _ in sorted(rejects.items(), key=lambda pair: (-pair[1], pair[0]))]
    if not ordered:
        reason = "no playable stream"
    elif len(ordered) == 1:
        reason = ordered[0]
    else:
        reason = "no stream passes the TV rules (" + ", ".join(ordered[:3]) + ")"
    return Verdict("unplayable", reason, rejects=dict(rejects))


def streams_with_japanese_audio(items):
    return any(detect_anime_languages(parse_stream(item).text).japanese_audio for item in items or () if isinstance(item, dict))


def is_ambiguous_animation(candidate):
    if not has_genre(candidate, "animation"):
        return False
    return not candidate.countries or candidate.countries[0] in AMBIGUOUS_ANIMATION_COUNTRIES


class PlayabilityChecker:
    """Checks candidates lazily (in rank order) with a per-run budget, spacing and a verdict cache."""

    def __init__(self, http, config, cache_dir, *, clock=time.time, foreign_mode=None):
        self.http = http
        self.settings = config["playability"]
        self.foreign_mode = foreign_mode or config["filters"]["language"]["foreign"]
        self.clock = clock
        self.path = Path(cache_dir) / "playability.json" if cache_dir else None
        self.cache = (read_json(self.path, {}) if self.path else {}) or {}
        self.checks_left = int(self.settings.get("maxChecksPerRun", 200))
        self.stats = Counter()
        self.rejects = Counter()
        self.dirty = False
        from .sources.cinemeta import addon_base
        stream_url = self.settings.get("streamAddonUrl")
        self.stream_base = addon_base(stream_url) if stream_url else None
        self.subtitle_base = addon_base(self.settings.get("subtitleAddonUrl", "https://opensubtitles-v3.strem.io/manifest.json"))

    def key(self, candidate, video_id, language_class):
        return "|".join(str(part) for part in (
            candidate.type, video_id, language_class, self.foreign_mode,
            self.settings.get("maxSizeGb", MAX_STREAM_SIZE_GB), self.settings.get("minSeeders", 0)))

    def check(self, candidate, language_class):
        video_id = candidate.video_for_check()
        if not self.settings.get("enabled", True):
            self.stats["unchecked"] += 1
            return Verdict("unchecked", "playability check disabled", video_id)
        if not self.stream_base:
            self.stats["unchecked"] += 1
            return Verdict("unchecked", "no stream add-on configured (playability.streamAddonUrl)", video_id)
        key = self.key(candidate, video_id, language_class)
        entry = self.cache.get(key)
        if entry:
            ttl = float(self.settings["passTtlHours" if entry["status"] == "playable" else "failTtlHours"]) * 3600
            if self.clock() - entry.get("checkedAt", 0) <= ttl:
                self.stats["cache_hits"] += 1
                self.stats[entry["status"]] += 1
                return Verdict(entry["status"], entry.get("reason", ""), video_id, entry.get("best"), cached=True)
        if self.checks_left <= 0:
            self.stats["unchecked"] += 1
            return Verdict("unchecked", "check budget exhausted", video_id)
        self.checks_left -= 1
        self.stats["network_checks"] += 1
        try:
            verdict = self._check_network(candidate, video_id, language_class)
        except Exception as error:  # noqa: BLE001 - network trouble is "unknown", never "unplayable"
            self.stats["unknown"] += 1
            return Verdict("unknown", f"{type(error).__name__}", video_id)
        verdict.video_id = video_id
        self.stats[verdict.status] += 1
        for reason, count in verdict.rejects.items():
            self.rejects[reason] += count
        self.cache[key] = {"status": verdict.status, "reason": verdict.reason, "best": verdict.best,
                           "checkedAt": self.clock()}
        self.dirty = True
        return verdict

    def _check_network(self, candidate, video_id, language_class):
        type_ = candidate.type
        url = f"{self.stream_base}/stream/{type_}/{urllib.parse.quote(video_id, safe=':')}.json"
        payload = self.http.get_json(url, ttl=0, timeout=float(self.settings.get("timeoutSeconds", 25)))
        items = payload.get("streams") if isinstance(payload, dict) else None
        items = items if isinstance(items, list) else []
        anime = language_class == ANIME or (is_ambiguous_animation(candidate) and streams_with_japanese_audio(items))
        options = dict(anime=anime, language_class=language_class, foreign_mode=self.foreign_mode,
                       max_size_gb=float(self.settings.get("maxSizeGb", MAX_STREAM_SIZE_GB)),
                       min_seeders=int(self.settings.get("minSeeders", 0)))
        verdict = evaluate(items, preferred_subs=False, **options)
        if (not verdict.playable and not anime and language_class == FOREIGN
                and self.foreign_mode in {"preferred_subs", "dub_or_preferred_subs"} and has_preferred() and items):
            if self.has_preferred_subtitles(type_, video_id):
                verdict = evaluate(items, preferred_subs=True, **options)
        return verdict

    def has_preferred_subtitles(self, type_, video_id):
        url = f"{self.subtitle_base}/subtitles/{type_}/{urllib.parse.quote(video_id, safe=':')}.json"
        payload = self.http.get_json(url, ttl=3 * 86400)
        subtitles = payload.get("subtitles") if isinstance(payload, dict) else None
        return any(isinstance(item, dict) and str(item.get("lang", "")).lower() in subtitle_codes()
                   for item in subtitles or ())

    def save(self):
        if self.path and self.dirty:
            horizon = self.clock() - 14 * 86400
            self.cache = {key: value for key, value in sorted(self.cache.items()) if value.get("checkedAt", 0) >= horizon}
            write_json(self.path, self.cache, indent=None)
            self.dirty = False

    def report(self):
        return {**dict(sorted(self.stats.items())), "checksLeft": self.checks_left,
                "rejectedStreams": dict(sorted(self.rejects.items(), key=lambda pair: (-pair[1], pair[0]))[:10])}
