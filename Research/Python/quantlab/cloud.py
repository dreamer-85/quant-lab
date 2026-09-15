"""Local-inferred-cloud orchestration for QuantLab.

Integrates the local workstation with the GCE VM + GCS pipeline defined by
``deploy/cloud``:

- load the SAME ``deploy/cloud/environment`` file that ``source``-ed bash
  scripts use (single source of truth);
- build a dataset bundle (Lean data root -> tar.gz) locally;
- upload/download bundles and results to Google Cloud Storage via gsutil;
- submit a job to the VM via ``gcloud compute scp/ssh``;
- compare local vs cloud outputs (manifest fields + file hashes + optional
  pandas equality).

Everything is stdlib-only except the optional pandas/pyarrow used by
``compare`` for deep parquet equality.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Sequence

# ---------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------

DEFAULT_ENV_FILE = Path(__file__).resolve().parents[3] / "deploy" / "cloud" / "environment"


def load_environment(
    env_file: Optional[str] = None, write_into_env: bool = False
) -> Dict[str, str]:
    """Parses a ``deploy/cloud`` bash-style environment file into a dict.

    Understands ``export KEY="value"``, ``KEY=value``, ``#`` comments and
    blank lines — the same file ``source``-d by the bash deploy scripts. When
    ``write_into_env`` is True the parsed values are merged into
    ``os.environ`` (never overwriting values already present).
    """
    path = Path(env_file) if env_file else DEFAULT_ENV_FILE
    parsed: Dict[str, str] = {}
    if path.exists():
        for raw in path.read_text(encoding="utf-8").splitlines():
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith("export "):
                line = line[7:].strip()
            if "=" not in line:
                continue
            key, _, value = line.partition("=")
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            parsed[key] = value
    if write_into_env:
        for key, value in parsed.items():
            os.environ.setdefault(key, value)
    return parsed


def _pick(*candidates: Optional[str]) -> Optional[str]:
    """First non-empty value."""
    for c in candidates:
        if c:
            return c
    return None


def environment(
    env_file: Optional[str] = None, v: str = "", vm: str = "", zone: str = "", project: str = ""
) -> Dict[str, str]:
    """Merged effective environment: explicit args > process env > env file."""
    file_env = load_environment(env_file)
    merged = dict(file_env)
    merged.update(
        {
            key: value
            for key, value in {
                "QUANTLAB_GCS_BUCKET": _pick(
                    os.environ.get("QUANTLAB_GCS_BUCKET"),
                    file_env.get("QUANTLAB_GCS_BUCKET"),
                ),
                "QUANTLAB_GCS_PREFIX": _pick(
                    os.environ.get("QUANTLAB_GCS_PREFIX"),
                    file_env.get("QUANTLAB_GCS_PREFIX"),
                    "quantlab",
                ),
                "QUANTLAB_VM": _pick(v, vm, os.environ.get("QUANTLAB_VM"), file_env.get("QUANTLAB_VM")),
                "QUANTLAB_ZONE": _pick(zone, os.environ.get("QUANTLAB_ZONE"), file_env.get("QUANTLAB_ZONE")),
                "QUANTLAB_PROJECT": _pick(
                    project, os.environ.get("QUANTLAB_PROJECT"), file_env.get("QUANTLAB_PROJECT")
                ),
                "QUANTLAB_REPO_DIR": _pick(
                    os.environ.get("QUANTLAB_REPO_DIR"), file_env.get("QUANTLAB_REPO_DIR"), "myEngine"
                ),
                "QUANTLAB_DATA_ROOT": _pick(
                    os.environ.get("QUANTLAB_DATA_ROOT"), file_env.get("QUANTLAB_DATA_ROOT")
                ),
                "QUANTLAB_OUTPUT_ROOT": _pick(
                    os.environ.get("QUANTLAB_OUTPUT_ROOT"), file_env.get("QUANTLAB_OUTPUT_ROOT")
                ),
            }.items()
            if value
        }
    )
    return merged


def _bucket_root(env: Dict[str, str]) -> str:
    """Normalizes QUANTLAB_GCS_BUCKET (accepts 'my-bucket' or 'gs://my-bucket/') to 'gs://my-bucket'."""
    bucket = env["QUANTLAB_GCS_BUCKET"].rstrip("/")
    return bucket if bucket.startswith("gs://") else f"gs://{bucket}"


def _bucket_path(object_name: str, env: Dict[str, str]) -> str:
    """Full gs:// path for an object under bucket/prefix."""
    prefix = env.get("QUANTLAB_GCS_PREFIX", "quantlab").strip("/")
    return f"{_bucket_root(env)}/{prefix}/{object_name.lstrip('/')}"


_REQUIRED_DB_FILES = (
    "market-hours/market-hours-database.json",
    "symbol-properties/symbol-properties-database.csv",
    "symbol-properties/security-database.csv",
)


# ---------------------------------------------------------------------------
# Tool discovery
# ---------------------------------------------------------------------------


def _find_command(names: Sequence[str]) -> Optional[str]:
    for name in names:
        path = shutil.which(name)
        if path:
            return path
    return None


def find_gcloud() -> Optional[str]:
    return _find_command(["gcloud", "gcloud.cmd"])


def find_gsutil() -> Optional[str]:
    return _find_command(["gsutil", "gsutil.cmd"])


def _cmd(binary: str, args: Sequence[str]) -> List[str]:
    """Builds a runnable command; Windows needs cmd.exe for .cmd shims."""
    if sys.platform == "win32" and binary.lower().endswith(".cmd"):
        return ["cmd", "/c", binary, *list(args)]
    return [binary, *list(args)]


def run(binary: str, args: Sequence[str], check: bool = True, **kwargs) -> subprocess.CompletedProcess:
    """Runs a tool, printing the command, returning the CompletedProcess."""
    cmd = _cmd(binary, args)
    print(f"+ {' '.join(map(str, cmd))}")
    return subprocess.run(cmd, check=check, **kwargs)


# ---------------------------------------------------------------------------
# Bundle building / downloading
# ---------------------------------------------------------------------------


def make_bundle(
    data_root: str,
    out_path: str,
    extra_dirs: Optional[Sequence[str]] = None,
    follow_symlinks: bool = False,
    verify: bool = True,
) -> Path:
    """Packs a Lean data root into a ``tar.gz`` bundle for GCS staging.

    Includes the static DB folders (``market-hours``, ``symbol-properties``),
    the dataset folders (``crypto``) and any ``extra_dirs``. Mirrors the
    layout expected by ``deploy/cloud/stage-data.sh``.
    """
    root = Path(data_root)
    if not root.is_dir():
        raise FileNotFoundError(f"data root not found: {root}")

    dirs = ["market-hours", "symbol-properties", "crypto"]
    if extra_dirs:
        dirs += list(extra_dirs)

    missing = [d for d in dirs if not (root / d).is_dir()]
    if missing:
        raise FileNotFoundError(f"data root missing expected folders: {missing}")

    if verify:
        missing_db = [rel for rel in _REQUIRED_DB_FILES if not (root / rel).is_file()]
        if missing_db:
            raise FileNotFoundError(f"data root missing required DB files: {missing_db}")

    dest = Path(out_path)
    dest.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(dest, "w:gz") as tar:
        for directory in dirs:
            tar.add(root / directory, arcname=directory, recursive=True,
                    filter=None if follow_symlinks else _no_symlink_filter)
    return dest


def _no_symlink_filter(tarinfo: tarfile.TarInfo) -> Optional[tarfile.TarInfo]:
    if tarinfo.issym() or tarinfo.islnk():
        return None
    return tarinfo


def upload_bundle(bundle_path: str, env: Optional[Dict[str, str]] = None, check: bool = True) -> str:
    """Uploads a bundle to GCS bundle/prefix, returning the gs:// object path."""
    env = env or environment()
    binary = find_gsutil()
    if not binary:
        raise RuntimeError("gsutil not found — install the Google Cloud SDK (deploy/cloud/install-gcloud-windows.ps1)")
    dest = _bucket_path(Path(bundle_path).name, env)
    run(binary, ["-m", "cp", str(bundle_path), dest], check=check)
    return dest


def download_object(object_name: str, dest_dir: str, env: Optional[Dict[str, str]] = None, check: bool = True) -> Path:
    """Downloads ``object_name`` (under bundle/prefix) into dest_dir."""
    env = env or environment()
    binary = find_gsutil()
    if not binary:
        raise RuntimeError("gsutil not found — install the Google Cloud SDK")
    src = _bucket_path(object_name, env)
    dest = Path(dest_dir)
    dest.mkdir(parents=True, exist_ok=True)
    run(binary, ["-m", "cp", src, str(dest)], check=check)
    return dest / object_name


def list_remote(env: Optional[Dict[str, str]] = None, check: bool = True) -> List[str]:
    """Lists objects under bundle/prefix."""
    env = env or environment()
    binary = find_gsutil()
    if not binary:
        raise RuntimeError("gsutil not found")
    prefix = _bucket_root(env) + "/" + env.get("QUANTLAB_GCS_PREFIX", "quantlab")
    proc = run(binary, ["ls", f"{prefix}/"], check=check, capture_output=True, text=True)
    return proc.stdout.strip().splitlines()


# ---------------------------------------------------------------------------
# VM submission
# ---------------------------------------------------------------------------


def _gcloud(args: Sequence[str], env: Optional[Dict[str, str]] = None, check: bool = True) -> None:
    binary = find_gcloud()
    if not binary:
        raise RuntimeError("gcloud not found — install the Google Cloud SDK (deploy/cloud/install-gcloud-windows.ps1)")
    run(binary, list(args), check=check)


def submit_job(
    job_file: str,
    vm: str = "",
    zone: str = "",
    project: str = "",
    data_dir: str = "",
    output_dir: str = "",
    repo_dir: str = "",
    skip_scp: bool = False,
    env_file: Optional[str] = None,
) -> None:
    """Uploads a job file to the VM and runs it there synchronously.

    Requires ``gcloud`` authenticated. Uses the VM repo (default
    ``~/myEngine``) and the VM's ``deploy/cloud/environment`` for data/output
    roots; explicit ``data_dir``/``output_dir`` override them.
    """
    env = environment(env_file, vm=vm, zone=zone, project=project)
    target_vm = env.get("QUANTLAB_VM") or None
    if not target_vm:
        raise ValueError("no VM configured: set QUANTLAB_VM (or pass --vm)")
    target_zone = env.get("QUANTLAB_ZONE") or None
    target_project = env.get("QUANTLAB_PROJECT") or None

    scp_args = ["compute", "scp"]
    ssh_args = ["compute", "ssh"]
    if target_project:
        scp_args += ["--project", target_project]
        ssh_args += ["--project", target_project]
    if target_zone:
        scp_args += ["--zone", target_zone]
        ssh_args += ["--zone", target_zone]

    remote_job = f"~/job-{Path(job_file).name}"
    if not skip_scp:
        _gcloud([*scp_args, str(job_file), f"{target_vm}:{remote_job}"])

    repo = repo_dir or env.get("QUANTLAB_REPO_DIR", "myEngine")
    data_part = f' --data-dir "{data_dir}"' if data_dir else ""
    output_part = f' --output-dir "{output_dir}"' if output_dir else ""
    command = (
        f"cd {repo} && "
        f"source deploy/cloud/environment && "
        f"deploy/cloud/run-job.sh {remote_job.replace('~', '$HOME')}{data_part}{output_part}"
    )
    _gcloud([*ssh_args, target_vm, "--command", command])


def download_results(
    job_id: str,
    dest_dir: str,
    env: Optional[Dict[str, str]] = None,
    check: bool = True,
) -> Path:
    """Downloads a job's output folder from GCS into dest_dir/job_id."""
    env = env or environment()
    binary = find_gsutil()
    if not binary:
        raise RuntimeError("gsutil not found")
    src = _bucket_path(f"output/{job_id}", env)
    dest = Path(dest_dir)
    dest.mkdir(parents=True, exist_ok=True)
    run(binary, ["-m", "cp", "-r", src, str(dest)], check=check)
    return dest / job_id


# ---------------------------------------------------------------------------
# Local run (thin wrapper over runner.run so the CLI has one surface)
# ---------------------------------------------------------------------------

from .runner import ResearchJob, ResearchResult, run as _run_local  # noqa: E402


def run_local(
    job_file: str,
    data_dir: str = "",
    output_dir: str = "",
    build: bool = False,
) -> ResearchResult:
    """Runs a job JSON file through the local Runner DLL."""
    job_json = Path(job_file)
    if not job_json.is_file():
        raise FileNotFoundError(f"job file not found: {job_json}")
    payload = json.loads(job_json.read_text(encoding="utf-8"))
    return run_local_dict(payload, data_dir=data_dir, output_dir=output_dir, build=build)


def run_local_dict(
    payload: Dict[str, object],
    data_dir: str = "",
    output_dir: str = "",
    build: bool = False,
) -> ResearchResult:
    """Runs an in-memory job dict through the local Runner DLL (no job file needed)."""
    job = ResearchJob(
        dataset=str(payload.get("dataset", "")),
        symbols=[str(s) for s in payload.get("symbols", [])],
        asset_class=str(payload.get("assetClass", "crypto")),
        venue=str(payload.get("venue", "")),
        resolution=str(payload.get("resolution", "minute")),
        start_time=str(payload.get("startTime", "")),
        end_time=str(payload.get("endTime", "")),
        event_types=[str(e) for e in payload.get("eventTypes", [])],
        observation_interval_seconds=_parse_timespan(str(payload.get("observationInterval", "00:00:00.100"))),
        features=[str(f) for f in payload.get("features", [])],
        experiment_name=str(payload.get("experimentName", "")),
        experiment_config={str(k): str(v) for k, v in payload.get("experimentConfig", {}).items()},
        horizons=[str(h) for h in payload.get("horizons", [])],
        output_location=str(payload.get("outputLocation", "")),
        output_format=str(payload.get("outputFormat", "parquet")),
        engine_version=str(payload.get("engineVersion", "1.0.0")),
        max_events=int(payload.get("maxEvents", 0)),
        enable_checkpointing=bool(payload.get("enableCheckpointing", True)),
        checkpoint_directory=str(payload.get("checkpointDirectory", "")),
        reorder=str(payload.get("reorder", "fullsort")),
        job_id=str(payload.get("jobId", "")),
    )
    if output_dir:
        job.output_location = output_dir

    api_job = job
    return _run_local(api_job, data_dir=data_dir or None, build=build)


def _parse_timespan(value: str) -> float:
    """Parses 'HH:MM:SS[.ffffff]' (the runner's TimeSpan format) to seconds."""
    try:
        parts = value.split(":")
        if len(parts) >= 3:
            hours = int(parts[0])
            minutes = int(parts[1])
            seconds = float(parts[2])
            return hours * 3600 + minutes * 60 + seconds
    except (ValueError, TypeError):
        pass
    return 0.1


# ---------------------------------------------------------------------------
# Comparison
# ---------------------------------------------------------------------------


@dataclass
class CompareResult:
    identical: bool
    manifest_match: bool = False
    manifest_fields: Dict[str, object] = field(default_factory=dict)
    mismatches: List[str] = field(default_factory=list)
    files_matched: int = 0
    files_total: int = 0


def _manifest(path: Optional[str]) -> Optional[Dict[str, object]]:
    if not path:
        return None
    p = Path(path)
    if p.is_dir():
        p = p / "manifest.json"
    if not p.is_file():
        return None
    return json.loads(p.read_text(encoding="utf-8"))


COMPARE_FIELDS = ("succeeded", "eventsProcessed", "observationsWritten", "symbolsProcessed", "symbolsReused")


def compare_local_cloud(
    local: str,
    cloud: str,
    compare_files: bool = True,
    deep_parquet: bool = False,
) -> CompareResult:
    """Compares a local result and a cloud result.

    ``local``/``cloud`` may be a ``manifest.json`` path or a job output folder.
    ``manifest.json`` fields are compared field-by-field; the output files are
    byte-compared via their hash (``deep_parquet`` additionally loads both with
    pandas and compares frame equality, requiring pandas + pyarrow).
    """
    lm = _manifest(local)
    cm = _manifest(cloud)
    if lm is None or cm is None:
        raise ValueError("both local and cloud must point to a manifest.json or a folder containing one")

    mismatches: List[str] = []
    for field_name in COMPARE_FIELDS:
        lv = lm.get(field_name)
        cv = cm.get(field_name)
        if lv != cv:
            mismatches.append(f"{field_name}: local={lv!r} cloud={cv!r}")

    l_dir = Path(local) if Path(local).is_dir() else Path(local).parent
    c_dir = Path(cloud) if Path(cloud).is_dir() else Path(cloud).parent
    l_files = sorted(l_dir.rglob("*")) if compare_files else []
    c_files = sorted(c_dir.rglob("*")) if compare_files else []
    l_files = [f for f in l_files if f.is_file() and f.name != "manifest.json"]
    c_files = [f for f in c_files if f.is_file() and f.name != "manifest.json"]
    if l_files or c_files:
        if len(l_files) != len(c_files):
            mismatches.append(f"file count: local={len(l_files)} cloud={len(c_files)}")
        rel_files = [f.relative_to(l_dir).as_posix() for f in l_files]
        for rel in rel_files:
            lf = l_dir / rel
            cf = c_dir / rel
            if not cf.is_file():
                mismatches.append(f"missing cloud file: {rel}")
                continue
            if deep_parquet and rel.endswith(".parquet"):
                same = _parquet_equal(lf, cf)
            else:
                same = _file_hash(lf) == _file_hash(cf)
            if not same:
                mismatches.append(f"content differs: {rel}")

    manifest_match = not any(
        m.split(":")[0].strip() in COMPARE_FIELDS for m in mismatches if ": " in m
    )
    files_matched = len(rel_files) if l_files or c_files else 0
    return CompareResult(
        identical=not mismatches,
        manifest_match=manifest_match,
        manifest_fields={"local": lm, "cloud": cm},
        mismatches=mismatches,
        files_matched=files_matched,
        files_total=max(len(l_files), len(c_files)),
    )


def _file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _parquet_equal(a: Path, b: Path) -> bool:
    import pandas as pd  # noqa: PLC0415

    left = pd.read_parquet(a)
    right = pd.read_parquet(b)
    return left.equals(right)


# ---------------------------------------------------------------------------
# Convenience helpers used by the CLI
# ---------------------------------------------------------------------------


def describe(result: CompareResult) -> str:
    if result.identical:
        return "IDENTICAL: local and cloud outputs match (manifest fields + output files)"
    lines = ["DIFFER:"]
    lines.extend(f"  - {m}" for m in result.mismatches)
    return "\n".join(lines)