"""Rule-based recommendation feed builder for the RU7100 app.

No model training: a scheduled CLI gathers free candidate sources, scores them against a
statistical taste profile with explicit rules, checks that they are playable on the TV and
writes a versioned snapshot. See docs/recommendations.md.
"""

SNAPSHOT_VERSION = 1
CURATION_REQUEST_VERSION = 1
