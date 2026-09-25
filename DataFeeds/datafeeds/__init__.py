"""DataFeeds: pull market data (crypto + forex) from multiple exchanges.

Historical and live feeds funnel through one shared, provider-agnostic
framework so that adding a new exchange is a matter of implementing two
classes and registering them (see ``datafeeds/core/registry.py``).
"""

__version__ = "0.1.0"

DEFAULT_FEED_ROOT = "feeds"
DEFAULT_DERIV_APP_ID = 1089