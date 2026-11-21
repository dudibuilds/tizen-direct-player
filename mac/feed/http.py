"""Polite JSON HTTP client: retries, per-host spacing, an on-disk TTL cache and offline/fixture modes.

Retry rules follow the TV add-on client (parity B5): 3 tries, retry on 429, 5xx, timeouts and
network errors, waiting 250 ms x attempt (or Retry-After, capped) between tries.
"""

import hashlib
import json
import re
import socket
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter, defaultdict
from pathlib import Path

DEFAULT_USER_AGENT = "ru7100-feed/1.0"
MAX_DOCUMENT_BYTES = 8 * 1024 * 1024
SECRET_PARAMETERS = {"api_key", "apikey", "token", "access_token", "client_secret"}


class HttpError(Exception):
    def __init__(self, message, status=None):
        super().__init__(message)
        self.status = status


def redact(url):
    """The URL with secret query parameters replaced, safe for logs and cache metadata."""
    parts = urllib.parse.urlsplit(url)
    if not parts.query:
        return url
    query = [
        (key, "REDACTED" if key.lower() in SECRET_PARAMETERS else value)
        for key, value in urllib.parse.parse_qsl(parts.query, keep_blank_values=True)
    ]
    return urllib.parse.urlunsplit(parts._replace(query=urllib.parse.urlencode(query)))


class RateLimiter:
    """Keeps a minimum interval between requests to the same host."""

    def __init__(self, sleep=time.sleep, clock=time.monotonic):
        self.sleep = sleep
        self.clock = clock
        self.last = {}

    def wait(self, host, interval):
        if interval <= 0:
            return
        last = self.last.get(host)
        if last is not None:
            remaining = interval - (self.clock() - last)
            if remaining > 0:
                self.sleep(remaining)
        self.last[host] = self.clock()


class Http:
    """JSON GET/POST with retries, host spacing and a TTL cache keyed by the (unredacted) URL hash."""

    def __init__(self, cache_dir=None, *, offline=False, user_agent=DEFAULT_USER_AGENT, timeout=20.0,
                 host_intervals=None, default_interval=0.1, max_attempts=3, sleep=time.sleep,
                 clock=time.time, opener=None, breaker_failures=3):
        self.cache_dir = Path(cache_dir) / "http" if cache_dir else None
        self.offline = offline
        self.user_agent = user_agent
        self.timeout = timeout
        self.host_intervals = dict(host_intervals or {})
        self.default_interval = default_interval
        self.max_attempts = max_attempts
        self.sleep = sleep
        self.clock = clock
        self.limiter = RateLimiter(sleep=sleep)
        self.opener = opener or urllib.request.build_opener()
        self.stats = defaultdict(Counter)
        self.breaker_failures = breaker_failures
        self.failures = Counter()          # consecutive failed requests per host

    # ---- public API ----

    def get_json(self, url, *, ttl=0, headers=None, timeout=None, stale_ok=True):
        return self.request_json("GET", url, ttl=ttl, headers=headers, timeout=timeout, stale_ok=stale_ok)

    def post_json(self, url, body, *, headers=None, timeout=None):
        return self.request_json("POST", url, body=body, headers=headers, timeout=timeout)

    def request_json(self, method, url, *, body=None, ttl=0, headers=None, timeout=None, stale_ok=True):
        host = urllib.parse.urlsplit(url).hostname or ""
        cache_file = self._cache_file(method, url, headers) if ttl > 0 and method == "GET" else None
        cached = self._read_cache(cache_file)
        if cached is not None and self.clock() - cached["fetchedAt"] <= ttl:
            self.stats[host]["cache_hits"] += 1
            return cached["body"]
        broken = self.breaker_failures and self.failures[host] >= self.breaker_failures
        if self.offline or broken:
            if cached is not None:
                self.stats[host]["stale"] += 1
                return cached["body"]
            if broken:
                self.stats[host]["skipped_unavailable"] += 1
                raise HttpError(f"{host} is not answering; skipped for the rest of this run")
            self.stats[host]["offline_misses"] += 1
            raise HttpError(f"offline and not cached: {redact(url)}")
        try:
            value = self._fetch(method, url, host, body, headers, timeout or self.timeout)
            self.failures[host] = 0
        except HttpError as error:
            # Client errors (404 etc.) mean the host is up; timeouts, 429 and 5xx count toward the breaker.
            if error.status is None or error.status == 429 or error.status >= 500:
                self.failures[host] += 1
            else:
                self.failures[host] = 0
            if cached is not None and stale_ok:
                self.stats[host]["stale"] += 1
                return cached["body"]
            raise
        if cache_file is not None:
            self._write_cache(cache_file, url, value)
        return value

    def report(self):
        return {host: dict(sorted(counter.items())) for host, counter in sorted(self.stats.items())}

    # ---- internals ----

    def _fetch(self, method, url, host, body, headers, timeout):
        request_headers = {"User-Agent": self.user_agent, "Accept": "application/json"}
        request_headers.update(headers or {})
        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            request_headers["Content-Type"] = "application/json"
        last_error = None
        for attempt in range(self.max_attempts):
            final = attempt == self.max_attempts - 1
            self.limiter.wait(host, self.host_intervals.get(host, self.default_interval))
            self.stats[host]["requests"] += 1
            wait = 0.25 * (attempt + 1)
            try:
                request = urllib.request.Request(url, data=data, headers=request_headers, method=method)
                with self.opener.open(request, timeout=timeout) as response:
                    length = response.headers.get("Content-Length")
                    if length and length.isdigit() and int(length) > MAX_DOCUMENT_BYTES:
                        raise HttpError(f"{method} {redact(url)}: response too large")
                    raw = response.read(MAX_DOCUMENT_BYTES + 1)
                if len(raw) > MAX_DOCUMENT_BYTES:
                    raise HttpError(f"{method} {redact(url)}: response too large")
                try:
                    return json.loads(raw.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError) as error:
                    raise HttpError(f"{method} {redact(url)}: invalid JSON ({error})") from error
            except urllib.error.HTTPError as error:
                status = error.code
                last_error = HttpError(f"{method} {redact(url)} failed: HTTP {status}", status)
                retryable = status == 429 or status >= 500
                if not retryable or final:
                    self.stats[host]["errors"] += 1
                    raise last_error from None
                retry_after = error.headers.get("Retry-After") if error.headers else None
                if retry_after and retry_after.strip().isdigit():
                    wait = min(10.0, float(retry_after))
            except (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError, OSError) as error:
                if isinstance(error, HttpError):
                    raise
                last_error = HttpError(f"{method} {redact(url)} failed: {getattr(error, 'reason', error)}")
                if final:
                    self.stats[host]["errors"] += 1
                    raise last_error from None
            self.sleep(wait)
        raise last_error or HttpError(f"{method} {redact(url)} failed")

    def _cache_file(self, method, url, headers):
        if self.cache_dir is None:
            return None
        auth = (headers or {}).get("Authorization", "")
        digest = hashlib.sha256(f"{method} {url} {auth}".encode("utf-8")).hexdigest()
        return self.cache_dir / digest[:2] / f"{digest}.json"

    def _read_cache(self, path):
        if path is None:
            return None
        try:
            with open(path, encoding="utf-8") as source:
                entry = json.load(source)
            if isinstance(entry, dict) and "fetchedAt" in entry and "body" in entry:
                return entry
        except (OSError, ValueError):
            pass
        return None

    def _write_cache(self, path, url, value):
        from .storage import write_json
        try:
            write_json(path, {"fetchedAt": self.clock(), "url": redact(url), "body": value}, indent=None)
        except OSError:
            pass


class FixtureHttp:
    """Test and dry-run stand-in for Http: URLs are answered from fixture files by regex routes.

    routes: list of [pattern, file-or-null]. The first `re.search` match wins; null answers 404.
    Unmatched URLs raise HttpError("no fixture"), which sources record as a source error.
    """

    def __init__(self, directory, routes=None):
        self.directory = Path(directory)
        if routes is None:
            with open(self.directory / "routes.json", encoding="utf-8") as source:
                routes = json.load(source)
        self.routes = [(re.compile(pattern), target) for pattern, target in routes]
        self.requests = []
        self.stats = defaultdict(Counter)

    def get_json(self, url, **_):
        return self.request_json("GET", url)

    def post_json(self, url, body, **_):
        return self.request_json("POST", url)

    def request_json(self, method, url, **_):
        host = urllib.parse.urlsplit(url).hostname or ""
        self.requests.append(url)
        self.stats[host]["requests"] += 1
        for pattern, target in self.routes:
            if pattern.search(url):
                if target is None:
                    self.stats[host]["errors"] += 1
                    raise HttpError(f"{method} {url} failed: HTTP 404", 404)
                if isinstance(target, (dict, list)):
                    return json.loads(json.dumps(target))
                with open(self.directory / target, encoding="utf-8") as source:
                    return json.load(source)
        self.stats[host]["errors"] += 1
        raise HttpError(f"no fixture for {method} {url}")

    def report(self):
        return {host: dict(sorted(counter.items())) for host, counter in sorted(self.stats.items())}
