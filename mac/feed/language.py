"""Original-language class of a title, from the best evidence available without keys.

Evidence chain: TMDB/Trakt `original_language` when a key supplied it, else the first production
country from Cinemeta (Cinemeta has no language field; IMDb datasets have no original language),
else "unknown". The Torrentio language tiers (playability) and OpenSubtitles subtitles in the viewer's
preferred language settle what the country proxy cannot.

Anime follows parity F11: genre "Anime", or Japan + "Animation".
"""

ENGLISH_COUNTRIES = {"United States", "United Kingdom", "Canada", "Australia", "Ireland", "New Zealand"}
COUNTRY_CODES = {
    "us": "United States", "gb": "United Kingdom", "uk": "United Kingdom", "ca": "Canada",
    "au": "Australia", "ie": "Ireland", "nz": "New Zealand", "jp": "Japan",
    "kr": "South Korea", "cn": "China", "tw": "Taiwan", "fr": "France", "de": "Germany",
    "es": "Spain", "it": "Italy", "in": "India", "mx": "Mexico", "br": "Brazil", "dk": "Denmark",
    "se": "Sweden", "no": "Norway", "pl": "Poland", "tr": "Turkey", "be": "Belgium", "nl": "Netherlands",
}

ENGLISH, PREFERRED, ANIME, FOREIGN, UNKNOWN = "english", "preferred", "anime", "foreign", "unknown"

# ISO 639-1 code -> (ISO 639-2 code, English name), for the viewer's preferred language.
LANGUAGE_NAMES = {
    "en": ("eng", "english"), "es": ("spa", "spanish"), "fr": ("fre", "french"), "de": ("ger", "german"),
    "it": ("ita", "italian"), "pt": ("por", "portuguese"), "ru": ("rus", "russian"), "pl": ("pol", "polish"),
    "tr": ("tur", "turkish"), "nl": ("dut", "dutch"), "ar": ("ara", "arabic"), "ko": ("kor", "korean"),
    "zh": ("chi", "chinese"), "hi": ("hin", "hindi"), "he": ("heb", "hebrew"),
}
ALIASES = {"iw": "he", "fra": "fr", "deu": "de", "zho": "zh", "nld": "nl"}

# The viewer's preferred language (config filters.language.preferred): originals in it count like
# English, and subtitles in it make a foreign original watchable. "en" means English only.
PREFERRED_LANGUAGE = {"code": "en", "countries": frozenset()}


def normalize(code):
    code = (code or "").strip().lower().split("-")[0].split("_")[0]
    for short, (long, name) in LANGUAGE_NAMES.items():
        if code in (long, name):
            return short
    return ALIASES.get(code, code)


def set_preferred(code, countries=()):
    PREFERRED_LANGUAGE["code"] = normalize(code) or "en"
    PREFERRED_LANGUAGE["countries"] = frozenset(countries or ())


def preferred_code():
    return PREFERRED_LANGUAGE["code"]


def has_preferred():
    return preferred_code() != "en"


def subtitle_codes():
    """Codes an add-on may use for subtitles in the preferred language."""
    code = preferred_code()
    long, name = LANGUAGE_NAMES.get(code, (code, code))
    return {code, long, name} | {alias for alias, target in ALIASES.items() if target == code}


def has_genre(candidate, genre):
    return any((item or "").strip().casefold() == genre for item in candidate.genres)


def is_anime(candidate):
    if has_genre(candidate, "anime"):
        return True
    countries = set(candidate.countries)
    if "Japan" in countries and has_genre(candidate, "animation"):
        return True
    return candidate.original_language == "ja" and has_genre(candidate, "animation")


def language_class(candidate):
    if is_anime(candidate):
        return ANIME
    language = (candidate.original_language or "").lower()
    if language:
        if language == "en":
            return ENGLISH
        if has_preferred() and normalize(language) == preferred_code():
            return PREFERRED
        return FOREIGN
    if candidate.countries:
        first = candidate.countries[0]
        if first in ENGLISH_COUNTRIES:
            return ENGLISH
        if first in PREFERRED_LANGUAGE["countries"]:
            return PREFERRED
        return FOREIGN
    return UNKNOWN


def country_name(code_or_name):
    if not code_or_name:
        return None
    text = str(code_or_name).strip()
    return COUNTRY_CODES.get(text.lower(), text) if len(text) <= 3 else text
