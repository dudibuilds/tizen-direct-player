"""Pluggable candidate sources. Each source is independent: a failure is recorded, never fatal."""

import time
from collections import Counter
from dataclasses import dataclass, field


@dataclass
class SourceContext:
    http: object
    config: dict
    now: object                       # aware UTC datetime
    profile: object                   # TasteProfile
    imdb: object = None               # ImdbIndex or None
    log: object = print
    clock: object = time.monotonic
    started: dict = field(default_factory=dict)

    def settings(self, name):
        return self.config["sources"].get(name, {})

    def ttl(self, name, default_hours=12):
        return float(self.settings(name).get("ttlHours", default_hours)) * 3600

    def expired(self, name):
        budget = self.settings(name).get("budgetSeconds")
        if not budget:
            return False
        return self.clock() - self.started.get(name, self.clock()) > budget


@dataclass
class SourceReport:
    errors: list = field(default_factory=list)
    counts: Counter = field(default_factory=Counter)

    def error(self, message):
        self.errors.append(str(message))


class Source:
    name = "source"

    def enabled(self, ctx):
        """(enabled, reason-if-disabled)."""
        return bool(ctx.settings(self.name).get("enabled", True)), "disabled in config"

    def collect(self, ctx, report):
        raise NotImplementedError


def run_sources(sources, ctx, pool):
    summary = {}
    for source in sources:
        enabled, why = source.enabled(ctx)
        if not enabled:
            summary[source.name] = {"enabled": False, "reason": why}
            continue
        report = SourceReport()
        ctx.started[source.name] = ctx.clock()
        try:
            items = list(source.collect(ctx, report) or [])
        except Exception as error:  # noqa: BLE001 - one broken source must not sink the feed
            report.error(f"{type(error).__name__}: {error}")
            items = []
        added = 0
        for item in items:
            if pool.add(item) is not None:
                added += 1
        if ctx.expired(source.name):
            report.counts["budget_exhausted"] += 1
        summary[source.name] = {
            "enabled": True,
            "candidates": added,
            "seconds": round(ctx.clock() - ctx.started[source.name], 2),
            "errorCount": len(report.errors),
            "errors": report.errors[:5],
            **({"counts": dict(sorted(report.counts.items()))} if report.counts else {}),
        }
    return summary
