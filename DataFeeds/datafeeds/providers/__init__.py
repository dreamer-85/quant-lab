"""DataFeeds provider implementations, grouped by market (crypto / forex).

Importing a sibling module registers the feed classes with the central
registry (``datafeeds.core.registry``). ``import datafeeds.providers`` is
called by the CLI and entry scripts so every feed is discoverable by name.
"""

from __future__ import annotations

from . import crypto, forex  # noqa: F401  (registration side effects)

__all__ = ["crypto", "forex"]