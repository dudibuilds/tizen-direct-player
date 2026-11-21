"""Command line: build the feed, ingest TV feedback, inspect the taste profile.

  python3 -m mac.feed build --profile NAME [--output PATH] [--config PATH] ...
  python3 -m mac.feed ingest-feedback FILE|- [--log PATH]
  python3 -m mac.feed profile --profile NAME
"""

import argparse
import json
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

from mac.netflix_history import clean

from . import config as feed_config
from . import fallback, feedback, paths
from .builder import BuildOptions, FeedBuilder, resolve_path
from .candidates import parse_datetime
from .http import FixtureHttp, Http
from .storage import write_json


def make_http(config, args, cache_dir):
    if getattr(args, "fixtures", None):
        return FixtureHttp(args.fixtures)
    settings = config["http"]
    return Http(cache_dir, offline=args.offline, user_agent=settings["userAgent"],
                timeout=float(settings["timeoutSeconds"]), host_intervals=settings["hostIntervals"],
                default_interval=float(settings["defaultIntervalSeconds"]))


def add_common(parser):
    parser.add_argument("--profile", required=True, help="Netflix profile name used at import")
    parser.add_argument("--config", type=Path, default=paths.DEFAULT_CONFIG, help="JSON config (default local-data/feed-config.json)")
    parser.add_argument("--database", type=Path, help="Netflix history SQLite (default local-data/netflix.sqlite)")
    parser.add_argument("--imdb", type=Path, help="IMDb datasets folder (default local-data/imdb)")
    parser.add_argument("--feedback", type=Path, action="append", default=[],
                        help="extra TV feedback file (JSON array or JSON lines); repeatable")
    parser.add_argument("--library", type=Path, help="Stremio library JSON (datastoreGet result)")
    parser.add_argument("--cache-dir", type=Path, help="HTTP and playability cache (default local-data/feed-cache)")
    parser.add_argument("--offline", action="store_true", help="use cached responses only, no network")
    parser.add_argument("--fixtures", type=Path, help=argparse.SUPPRESS)  # dev/test: answer HTTP from DIR/routes.json
    parser.add_argument("--now", help="pretend the current time is this ISO timestamp (tests)")


def prepare(args):
    # The default config file is optional; one named on the command line must exist.
    config = feed_config.load(args.config, required=args.config != paths.DEFAULT_CONFIG)
    for path in args.feedback:
        if not path.exists():
            raise ValueError(f"Feedback file not found: {path}")
    if args.library:
        config["stremioLibrary"]["path"] = str(args.library)
    now = parse_datetime(args.now) if args.now else datetime.now(timezone.utc)
    if now is None:
        raise ValueError(f"Invalid --now value: {args.now}")
    cache_dir = args.cache_dir or resolve_path(config.get("cacheDir"), paths.DEFAULT_CACHE)
    return config, now, cache_dir, make_http(config, args, cache_dir)


def log_to_stderr(message):
    print(message, file=sys.stderr)


def command_build(args):
    config, now, cache_dir, http = prepare(args)
    if args.no_playability:
        config["playability"]["enabled"] = False
    output = args.output or resolve_path(config.get("output"), paths.DEFAULT_OUTPUT)
    curation_path = args.curation_request or resolve_path(config.get("curationRequest"), paths.DEFAULT_CURATION_REQUEST)
    log = (lambda message: None) if args.quiet else log_to_stderr
    options = BuildOptions(
        profile=clean(args.profile), config=config, http=http, now=now, output=output, database=args.database,
        imdb_dir=args.imdb, feedback_paths=args.feedback, cache_dir=cache_dir, curation_path=curation_path, log=log,
    )
    snapshot, details, curation = FeedBuilder(options).build()
    write_json(output, snapshot)
    details_path = output.with_name(output.stem + ".report.json")
    write_json(details_path, details)
    fallback.write_or_clear(curation_path, curation)
    command_status = {"ran": False}
    if curation is not None and not args.no_curation_command:
        command_status = fallback.run_command(config["fallback"].get("command"), curation_path,
                                              float(config["fallback"]["commandTimeoutSeconds"]), log)
    print(json.dumps({
        "output": str(output), "report": str(details_path), "rows": snapshot["report"]["rows"],
        "totalItems": snapshot["report"]["totalItems"], "needsCuration": curation is not None,
        **({"curationRequest": str(curation_path), "curationCommand": command_status} if curation else {}),
        "warnings": len(details["warnings"]),
    }, ensure_ascii=False, sort_keys=True))


def command_profile(args):
    config, now, cache_dir, http = prepare(args)
    options = BuildOptions(profile=clean(args.profile), config=config, http=http, now=now,
                           database=args.database, imdb_dir=args.imdb, feedback_paths=args.feedback,
                           cache_dir=cache_dir, log=log_to_stderr)
    builder = FeedBuilder(options)
    events, items, history, imdb = builder.load_inputs()
    profile = builder.build_profile(events, items, history, imdb)
    print(json.dumps(profile.summary(), ensure_ascii=False, indent=2))


def command_ingest(args):
    events, rejected = [], 0
    for name in args.files:
        text = sys.stdin.read() if name == "-" else Path(name).read_text(encoding="utf-8")
        parsed, bad = feedback.parse_text(text)
        events.extend(parsed)
        rejected += bad
    log_path = args.log
    if log_path is None:
        config = feed_config.load(args.config)
        log_path = resolve_path(config.get("feedbackLog"), paths.DEFAULT_FEEDBACK_LOG)
    result = feedback.merge_into_log(log_path, events)
    print(json.dumps({**result, "rejected": rejected, "log": str(log_path)}, sort_keys=True))


def main(argv=None):
    parser = argparse.ArgumentParser(prog="python3 -m mac.feed", description="Rule-based recommendation feed builder.")
    commands = parser.add_subparsers(dest="command", required=True)

    build = commands.add_parser("build", help="Build the recommendation feed snapshot")
    add_common(build)
    build.add_argument("--output", type=Path, help="snapshot path (default local-data/recommendation-feed.json)")
    build.add_argument("--curation-request", type=Path, help="needs_curation request path")
    build.add_argument("--no-playability", action="store_true", help="skip the Torrentio playability check")
    build.add_argument("--no-curation-command", action="store_true", help="never run the configured fallback command")
    build.add_argument("--quiet", action="store_true", help="no warnings on stderr")
    build.set_defaults(handler=command_build)

    profile = commands.add_parser("profile", help="Print the taste profile summary")
    add_common(profile)
    profile.set_defaults(handler=command_profile)

    ingest = commands.add_parser("ingest-feedback", help="Merge TV feedback events into the local log")
    ingest.add_argument("files", nargs="+", help="JSON array / JSON lines files, or - for stdin")
    ingest.add_argument("--log", type=Path, help="log path (default local-data/tv-feedback.jsonl)")
    ingest.add_argument("--config", type=Path, default=paths.DEFAULT_CONFIG)
    ingest.set_defaults(handler=command_ingest)

    args = parser.parse_args(argv)
    try:
        args.handler(args)
    except (OSError, ValueError, sqlite3.Error) as error:
        parser.exit(1, f"{error}\n")
