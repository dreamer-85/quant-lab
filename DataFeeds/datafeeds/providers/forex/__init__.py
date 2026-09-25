"""Forex providers. Importing this package registers every feed."""

from __future__ import annotations

from . import deriv  # noqa: F401

__all__ = ["deriv"]