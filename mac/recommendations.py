"""Rank new movies and series using local Netflix history and public IMDb metadata.

Thin wrapper kept for mcp/recommendation-server.mjs; the code lives in mac/feed/legacy.py.
The rule-based multi-row feed is `python3 -m mac.feed build --profile NAME`.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from mac.feed.legacy import main  # noqa: E402

if __name__ == "__main__":
    main()
