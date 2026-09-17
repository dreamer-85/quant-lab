"""Python client for the QuantConnect.Research.Runner engine."""

from __future__ import annotations

import hashlib
import json
import os
import subprocess
import tempfile
from dataclasses import asdict, dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

ENGINE_DLL_NAME = "QuantConnect.Research.Runner.dll"


def _repo_root() -> Path:
    """Root of the Lean repository (quantlab package lives at <lean>/Research/Python/quantlab)."""
    return Path(__file__).resolve().parents[3]


def _format_timespan(seconds: float) -> str:
    total = max(0.0, float(seconds))
    hours = int(total // 3600)
    minutes = int((total % 3600) // 60)
    secs = total % 60
    return f"{hours:02d}:{minutes:02d}:{secs:06.3f}"


_REORDER_MAP = {
    "fullsort": "FullSort",
    "full_sort": "FullSort",
    "inorderstreaming": "InOrderStreaming",
    "in_order_streaming": "InOrderStreaming",
    "streaming": "InOrderStreaming",
}


def _format_reorder(reorder: str) -> str:
    return _REORDER_MAP.get(reorder.strip().lower(), reorder)


@dataclass
class ResearchJob:
    """A research job specification, mirroring Research.Jobs.ResearchJob."""

    dataset: str
    symbols: List[str]
    asset_class: str = "crypto"
    venue: str = ""
    resolution: str = "minute"
    start_time: str = ""
    end_time: str = ""
    event_types: List[str] = field(default_factory=list)
    observation_interval_seconds: float = 0.1
    features: List[str] = field(default_factory=list)
    experiment_name: str = ""
    experiment_config: Dict[str, str] = field(default_factory=dict)
    horizons: List[str] = field(default_factory=list)
    output_location: str = ""
    output_format: str = "parquet"
    engine_version: str = "1.0.0"
    max_events: int = 0
    enable_checkpointing: bool = True
    checkpoint_directory: str = ""
    reorder: str = "fullsort"
    job_id: str = ""
    source: Dict[str, Any] = field(default_factory=dict)

    def config_hash(self) -> str:
        """Deterministic job id: SHA256 of the configuration."""
        canonical = asdict(self)
        canonical.pop("job_id", None)
        if isinstance(canonical.get("start_time"), datetime):
            canonical["start_time"] = canonical["start_time"].isoformat()
        if isinstance(canonical.get("end_time"), datetime):
            canonical["end_time"] = canonical["end_time"].isoformat()
        blob = json.dumps(canonical, sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(blob.encode("utf-8")).hexdigest()[:8]

    def to_job_dict(self) -> Dict[str, Any]:
        """Converts to the runner's job JSON schema."""
        start = self.start_time
        end = self.end_time
        if isinstance(start, datetime):
            start = start.isoformat()
        if isinstance(end, datetime):
            end = end.isoformat()

        return {
            "jobId": self.job_id or self.config_hash(),
            "dataset": self.dataset,
            "symbols": list(self.symbols),
            "assetClass": self.asset_class,
            "venue": self.venue,
            "resolution": self.resolution,
            "startTime": start,
            "endTime": end,
            "eventTypes": list(self.event_types),
            "observationInterval": _format_timespan(self.observation_interval_seconds),
            "features": list(self.features),
            "experimentName": self.experiment_name,
            "experimentConfig": dict(self.experiment_config),
            "horizons": list(self.horizons),
            "outputLocation": self.output_location,
            "outputFormat": self.output_format,
            "engineVersion": self.engine_version,
            "maxEvents": self.max_events,
            "enableCheckpointing": self.enable_checkpointing,
            "checkpointDirectory": self.checkpoint_directory,
            "reorder": _format_reorder(self.reorder),
        }

        if self.source:
            job["source"] = dict(self.source)
        return job


def find_runner(build: bool = False) -> Path:
    """Locates the built runner DLL, optionally building it if missing."""
    root = _repo_root()
    relative = root / "Research" / "Runner"
    for configuration in ("Release", "Debug"):
        dll = relative / "bin" / configuration / "net10.0" / ENGINE_DLL_NAME
        if dll.exists():
            return dll
    if build:
        build_runner()
        return find_runner(build=False)
    raise FileNotFoundError(
        "Runner DLL not found. Build it with `quantlab.build_runner()` "
        "or run: dotnet build <lean>/Research/Runner"
    )


def build_runner(configuration: str = "Release") -> Path:
    """Builds the runner project and returns the DLL path."""
    root = _repo_root()
    project = root / "Research" / "Runner" / "QuantConnect.Research.Runner.csproj"
    subprocess.run(["dotnet", "build", str(project), "-c", configuration], check=True)
    return root / "Research" / "Runner" / "bin" / configuration / "net10.0" / ENGINE_DLL_NAME


@dataclass
class ResearchResult:
    """Outcome of a runner invocation."""

    exit_code: int
    stdout: str
    stderr: str
    manifest: Dict[str, Any] = field(default_factory=dict)

    @property
    def succeeded(self) -> bool:
        return bool(self.manifest.get("succeeded"))

    @property
    def output_files(self) -> List[str]:
        return list(self.manifest.get("outputFiles", []))

    @property
    def metrics(self) -> Dict[str, Any]:
        return dict(self.manifest.get("metrics", {}))

    def load_parquet(self, index: int = 0):
        """Loads the first parquet output as a pandas DataFrame (requires pandas + pyarrow)."""
        if not self.output_files:
            raise ValueError("No output files were produced")
        import pandas as pd

        return pd.read_parquet(self.output_files[index])


def run(
    job: ResearchJob,
    data_dir: Optional[str] = None,
    runner_dll: Optional[str] = None,
    build: bool = False,
) -> ResearchResult:
    """Runs a research job through the engine.

    Args:
        job: The job to run.
        data_dir: Root Lean data folder (e.g. <lean>/Data). Defaults to <repo>/Data.
        runner_dll: Explicit path to QuantConnect.Research.Runner.dll.
        build: Build the runner first if no DLL is found.
    """
    dll = Path(runner_dll) if runner_dll else find_runner(build=build)
    if not dll.exists():
        raise FileNotFoundError(f"Runner DLL not found: {dll}")

    if data_dir is None:
        data_dir = str(_repo_root() / "Data")

    if not job.output_location:
        job.output_location = os.path.join(tempfile.gettempdir(), "quantlab")
    os.makedirs(job.output_location, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="quantlab_") as tmp:
        job_file = Path(tmp) / "job.json"
        job_file.write_text(json.dumps(job.to_job_dict(), indent=2, default=str), encoding="utf-8")

        cmd = ["dotnet", str(dll), "--job-file", str(job_file), "--data-dir", data_dir]
        proc = subprocess.run(cmd, capture_output=True, text=True)

    manifest = {}
    manifest_path = Path(job.output_location) / job.to_job_dict()["jobId"] / "manifest.json"
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    return ResearchResult(
        exit_code=proc.returncode,
        stdout=proc.stdout,
        stderr=proc.stderr,
        manifest=manifest,
    )