"""Discover research jobs from the Lean data folder layout.

The data directory is self-describing: a file like

    <root>/crypto/bybit/minute/btcusdt/20221213_trade.zip

encodes assetClass (crypto), venue (bybit), resolution (minute), symbol
(btcusdt), date (2022-12-13) and event type (Trade). This module scans a data
root, groups files into profiles, and builds a :class:`ResearchJob` from a
profile plus user overrides — no hand-written job.json required.
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

from .runner import ResearchJob

_RESOLUTION_ORDER = ("second", "minute", "hour", "daily")

# (segment, subfolder) -> resolution name
_RESOLUTIONS = {
    "second": "Second",
    "minute": "Minute",
    "hour": "Hour",
    "hourly": "Hour",
    "daily": "Daily",
}

_TICK_TYPE_MAP = {
    "trade": "Trade",
    "quote": "Quote",
    "openinterest": "Quote",
    "bar": "Trade",
}


@dataclass(frozen=True)
class DatasetProfile:
    """A unique asset/venue/resolution/symbol group found in a data root.

    ``profiles`` overrides equality so two groups with identical tick types and
    names collapse to one entry.
    """

    asset_class: str
    venue: str
    resolution: str
    symbol: str
    dates: List[str] = field(default_factory=list)
    tick_types: List[str] = field(default_factory=list)

    @property
    def start_time(self) -> str:
        return f"{_resolve_date(min(self.dates))}T00:00:00Z" if self.dates else ""

    @property
    def end_time(self) -> str:
        return f"{_resolve_date(max(self.dates))}T23:59:59Z" if self.dates else ""

    @property
    def label(self) -> str:
        """Short reproducible label: e.g. bybit-btcusdt-20221213."""
        date_part = f"-{min(self.dates)}" if self.dates else ""
        return f"{self.venue}-{self.symbol.lower()}{date_part}"


def discover_profiles(data_root: str) -> List[DatasetProfile]:
    """Scans a Lean data root and returns one profile per dataset/venue/asset/resolution/symbol.

    Expected layout (as produced by the Lean crypto data tree):

        <root>/<asset>/<venue>/<resolution>/<symbol>/<YYYYMMDD>[_<ticktype>].zip

    Any zip file that does not match the expected shape is skipped.
    """
    root = Path(data_root)
    if not root.is_dir():
        raise FileNotFoundError(f"data root not found: {root}")

    groups: Dict[tuple, DatasetProfile] = {}
    for zip_path in root.rglob("*.zip"):
        rel = zip_path.relative_to(root).parts
        if len(rel) < 4:
            continue

        asset_class, venue, resolution_segment, symbol_segment, *rest = rel
        resolution = _RESOLUTIONS.get(resolution_segment.lower())
        if resolution is None:
            resolution = _infer_resolution(zip_path.name)
        if resolution is None:
            continue

        m = re.match(r"^(\d{8})(?:_([a-z]+))?\.zip$", zip_path.name, re.IGNORECASE)
        if not m:
            continue

        date, suffix = m.group(1), m.group(2)
        tick_type = _TICK_TYPE_MAP.get((suffix or "trade").lower(), "Trade")

        key = (asset_class.lower(), venue.lower(), resolution, symbol_segment.lower())
        profile = groups.get(key)
        if profile is None:
            profile = DatasetProfile(
                asset_class=asset_class.lower(),
                venue=venue.lower(),
                resolution=resolution,
                symbol=symbol_segment.upper(),
            )
            groups[key] = profile
        profile.dates.append(date)
        if tick_type not in profile.tick_types:
            profile.tick_types.append(tick_type)

    return sorted(
        list(groups.values()),
        key=lambda p: (p.asset_class, p.venue, p.symbol),
    )


def _resolve_date(compact: str) -> str:
    """'20221213' -> '2022-12-13'"""
    return f"{compact[0:4]}-{compact[4:6]}-{compact[6:8]}"


def _infer_resolution(filename: str) -> Optional[str]:
    for name in _RESOLUTION_ORDER:
        if filename.startswith(f"{name}_") or f"_{name}_" in filename:
            return _RESOLUTIONS[name]
    return None


def build_job(
    profile: DatasetProfile,
    *,
    dataset: Optional[str] = None,
    job_id: Optional[str] = None,
    features: Optional[List[str]] = None,
    observation_interval_seconds: Optional[float] = None,
    event_types: Optional[List[str]] = None,
    experiment_name: Optional[str] = None,
    horizons: Optional[List[str]] = None,
    output_format: str = "parquet",
    output_location: str = "",
    engine_version: str = "1.0.0",
    reorder: str = "FullSort",
) -> ResearchJob:
    """Builds a :class:`ResearchJob` from a discovered profile and overrides.

    Only fields that cannot be inferred from the data are required (or defaulted):
    features default to a sensible baseline set, the observation interval defaults
    per resolution, and the experiment name defaults to the profile label.
    """
    resolution_seconds = {
        "Second": 1,
        "Minute": 60,
        "Hour": 3600,
        "Daily": 86400,
    }

    return ResearchJob(
        dataset=dataset or profile.venue,
        symbols=[profile.symbol],
        asset_class=profile.asset_class,
        venue=profile.venue,
        resolution=profile.resolution,
        start_time=profile.start_time,
        end_time=profile.end_time,
        event_types=event_types or (profile.tick_types or ["Trade"]),
        observation_interval_seconds=observation_interval_seconds
        or resolution_seconds.get(profile.resolution, 60),
        features=features
        or ["mid_price", "spread", "trade_volume", "trade_intensity"],
        experiment_name=experiment_name or f"{profile.label}-baseline",
        horizons=horizons or [],
        output_location=output_location,
        output_format=output_format,
        engine_version=engine_version,
        reorder=reorder,
        job_id=job_id or profile.label,
    )


def job_to_dict(job: ResearchJob) -> Dict[str, object]:
    """Dict form of the job for serialization to job.json."""
    return job.to_job_dict()