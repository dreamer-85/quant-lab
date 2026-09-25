"""One-script research: declare exchange + features + strategy; an orchestrator does the rest.

Two authoring styles, both end in the same flow (pull when missing -> compute
features -> run the engine -> report):

- **File-based**: a single ``.py`` with a ``Research`` class (what/when/where to
  measure) and the standard ``Strategy`` class (the expectation) run via
  ``quantlab research <script.py>``.
- **Base-class API**: subclass ``ResearchStrategy``, override the hooks, and call
  ``MyStrategy.run()`` from your own script (see the runnable copy at
  ``docs/examples/python/research_strategy_api.py``). The generated job points the
  engine at your file and sets ``experimentConfig["class"]`` to the subclass name,
  so subclasses run under their own name. ``run()`` resolves the script file from
  the class's module, so it keeps working when you ``import`` your strategy or
  upload it to the cloud VM.

Everything here sticks to the stdlib so the orchestrator runs on the same
Python the strategy jobs do.
"""

from __future__ import annotations

import csv
import difflib
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
from dataclasses import asdict, dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

_EVENT_CANONICAL = {
    "bar": "Bar",
    "candle": "Bar",
    "candles": "Bar",
    "ohlcv": "Bar",
    "trade": "Trade",
    "trades": "Trade",
    "quote": "Quote",
    "quotes": "Quote",
    "tick": "Quote",
    "orderbook": "OrderBookSnapshot",
    "book": "OrderBookSnapshot",
    "orderbooksnapshot": "OrderBookSnapshot",
    "bookupdate": "OrderBookUpdate",
    "bookupdates": "OrderBookUpdate",
    "orderbookupdate": "OrderBookUpdate",
    "orderbookupdates": "OrderBookUpdate",
    "custom": "Custom",
}

# Event types no provider stages; the CSV must be placed by hand.
_MANUAL_TYPES = ("OrderBookSnapshot", "OrderBookUpdate", "Custom")


def _get(cls, name, default=None):
    return getattr(cls, name, default)


def _csv_rows(path: Path) -> list[dict]:
    """Reads a staged/result CSV into a list of rows (empty when missing)."""
    try:
        with path.open("r", newline="", encoding="utf-8") as handle:
            return list(csv.DictReader(handle))
    except (OSError, csv.Error):
        return []


@dataclass
class RunResult:
    """Everything a finished run produced, returned by ``MyStrategy.run()``.

    ``report``/``signals`` are the row dicts behind the flat CSVs; the ``*_df()``
    helpers return pandas DataFrames when pandas is installed, so the result is
    immediately usable in a notebook or REPL.
    """

    job_id: str
    exit_code: int
    succeeded: bool
    meta: dict
    manifest: dict
    report: list[dict] = field(default_factory=list)
    signals: list[dict] = field(default_factory=list)
    summary: str = ""
    results_dir: str = ""

    @property
    def metrics(self) -> dict:
        return (self.manifest or {}).get("metrics", {})

    def report_df(self):
        """Observation rows as a pandas DataFrame (requires pandas)."""
        import pandas as pd  # noqa: PLC0415

        return pd.DataFrame(self.report)

    def signals_df(self):
        """Strategy rows as a pandas DataFrame (requires pandas)."""
        import pandas as pd  # noqa: PLC0415

        return pd.DataFrame(self.signals)

    def __post_init__(self):
        if self.summary and not self.summary.endswith("\n"):
            self.summary += "\n"


_KNOWN_HOOKS = ("initialize", "on_observation", "on_outcome", "finalize")


def _check_declaration(cls, meta: dict) -> None:
    """Fail early with friendly, actionable errors instead of a silent empty run."""
    known_events = set(_EVENT_CANONICAL.values())
    for event in meta["event_types"]:
        if event not in known_events:
            close = difflib.get_close_matches(event.lower(), known_events, n=1)
            hint = f" (did you mean '{close[0]}'?)" if close else ""
            raise ValueError(
                f"{cls.__name__}.event_types contains unknown type '{event}'{hint}; "
                f"known: {', '.join(sorted(known_events))}"
            )

    if meta["interval_seconds"] <= 0:
        raise ValueError(f"{cls.__name__}.interval_seconds must be positive; got {meta['interval_seconds']}")

    if meta["live_duration_seconds"] <= 0:
        raise ValueError(f"{cls.__name__}.live_duration_seconds must be positive; got {meta['live_duration_seconds']}")

    if meta["stream_live"] and (meta["start"] or meta["end"]):
        raise ValueError(
            f"{cls.__name__}.stream_live replays events in real time; leave .start/.end empty "
            "(the window is derived live from the session)"
        )

    if meta["start"] and meta["end"]:
        try:
            if datetime.fromisoformat(meta["start"]) > datetime.fromisoformat(meta["end"]):
                raise ValueError(
                    f"{cls.__name__}.start ({meta['start']}) is after .end ({meta['end']})"
                )
        except ValueError as exc:
            if str(exc).startswith(f"{cls.__name__}.start"):
                raise
            raise ValueError(f"{cls.__name__}.start/end are not ISO datetimes: {exc}") from exc

    if isinstance(cls, type) and issubclass(cls, ResearchStrategy) and cls is not ResearchStrategy:
        for name in vars(cls):
            if not name.startswith("on_") or name in _KNOWN_HOOKS:
                continue
            close = difflib.get_close_matches(name, _KNOWN_HOOKS, n=1)
            hint = f"did you mean '{close[0]}'?" if close else (
                "expected hooks: initialize / on_observation / on_outcome / finalize"
            )
            raise ValueError(
                f"{cls.__name__}.{name} is not a research hook — {hint}"
            )


class ResearchStrategy:
    """Base class for one-script research declared entirely in Python.

    Subclass it, declare the data/features as class attributes, override the
    expectation hooks, and call ``MyStrategy.run()`` — the orchestrator pulls the
    exchange data when missing, computes the features, runs the engine, and
    returns a :class:`RunResult` (rows, manifest, plain-text summary; the rows
    are also reachable as pandas DataFrames when pandas is installed).

    Declaration keys match the file-based ``Research`` class: ``exchange``,
    ``market``, ``symbols`` (or ``symbol``), ``event_types``,
    ``interval_seconds``, ``start``, ``end``, ``resolution``, ``features``,
    ``raw_fields``, ``horizons``, ``experiment_config``, ``job_id``, and the
    live pair ``live``/``live_duration_seconds``. Hooks mirror the python_strategy
    contract (docs/python-strategies.md): ``initialize`` (context),
    ``on_observation(observation, features)`` returning a row dict or None,
    ``on_outcome(outcome)``, and ``finalize()`` returning optional
    rows/metrics/metadata.

    By default data is pulled historically when missing from the feed root. Set
    ``live = True`` (session length in ``live_duration_seconds``) to instead open
    a bounded websocket session and evaluate the freshly captured window; leave
    ``start``/``end`` empty so the orchestrator derives the replay window from
    the staged data's span.

    Set ``stream_live = True`` for direct event-driven streaming: the engine
    subscribes the exchange WebSocket in-process and calls ``on_observation``
    once per incoming event (no observation grid), so nothing arriving is
    aggregated or dropped. ``event_types`` are the subscriptions (e.g.
    ``["Trade", "Quote", "Bar"]``). ``start``/``end`` must stay empty.

    The generated job points ``strategyScript`` at the file that defines the class
    and sets ``experimentConfig["class"]`` to the subclass name, so the engine
    instantiates it under its own name.
    """

    exchange = None
    market = "crypto"
    symbols = None
    symbol = None
    event_types = ["Bar", "Trade", "Quote"]
    interval_seconds = 60
    start = ""
    end = ""
    resolution = ""
    features = []
    raw_fields = []
    horizons = []
    experiment_config = {}
    job_id = ""
    live = False
    stream_live = False
    live_duration_seconds = 30

    def initialize(self, context):
        pass

    def on_observation(self, observation, features):
        return None

    def on_outcome(self, outcome):
        pass

    def finalize(self):
        return None

    @classmethod
    def meta(cls) -> dict:
        """Normalized declaration plus the class-name override for the engine."""
        meta = _meta_from_class(cls)
        meta["experiment_config"] = {**meta["experiment_config"], "class": cls.__name__}
        return meta

    @classmethod
    def run(cls, data_dir="", output_dir="", build=False, no_pull=False,
            sample_seconds=0, env=None) -> RunResult:
        """Runs this strategy end to end: pull (if needed) -> job -> engine -> report.

        Returns a :class:`RunResult` with the generated rows (``report``/``signals``),
        the manifest, and a plain-text ``summary``. Pass ``sample_seconds`` to
        replay only the most recent that-many seconds of staged/pulled data.
        ``exit_code`` is 0 on success.
        """
        module = sys.modules.get(cls.__module__)
        path = getattr(module, "__file__", None)
        if not path:
            raise ValueError(
                f"{cls.__name__}.run() must be called from a script file, "
                "not a REPL, notebook, or -c."
            )

        return run_strategy(
            str(Path(path).resolve()),
            cls.meta(),
            data_dir=data_dir,
            output_dir=output_dir,
            build=build,
            no_pull=no_pull,
            sample_seconds=sample_seconds,
            env=env,
        )


def _find_strategy_subclass(module):
    """The script's ResearchStrategy subclass (any name), excluding the base itself."""
    for name, value in vars(module).items():
        if isinstance(value, type) and issubclass(value, ResearchStrategy) and value is not ResearchStrategy:
            return value
    return None


def load_script(path: str):
    """Imports a research script module and validates it describes a runnable strategy."""
    script = Path(path).resolve()
    if not script.is_file():
        raise FileNotFoundError(f"research script not found: {script}")

    digest = hashlib.sha1(str(script).encode("utf-8")).hexdigest()[:12]
    spec = importlib.util.spec_from_file_location(f"_quantlab_research_{digest}", script)
    if spec is None or spec.loader is None:
        raise ValueError(f"cannot load research script: {script}")

    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)

    if not hasattr(module, "Research"):
        if _find_strategy_subclass(module) is None:
            raise ValueError(
                f"{script.name} must define a Research class (data declaration) or a subclass of "
                "quantlab.research.ResearchStrategy (see docs/python-strategies.md)"
            )
    elif not hasattr(module, "Strategy"):
        raise ValueError(
            f"{script.name} defines Research but no Strategy class (the strategy expectation) "
            "— contract in docs/python-strategies.md"
        )
    return module


def normalize_event_type(name) -> str:
    """Maps any accepted alias to a canonical engine MarketEventType name."""
    key = str(name).lower().replace("_", "").replace("-", "").replace(" ", "")
    canon = _EVENT_CANONICAL.get(key)
    if canon is None:
        raise ValueError(
            f"unknown event type {name!r} (valid: " + ", ".join(sorted(set(_EVENT_CANONICAL.values()))) + ")"
        )
    return canon


def resolution_for(interval_seconds: int) -> str:
    """Lean resolution for a bar/observation interval in seconds."""
    interval_seconds = int(interval_seconds)
    if interval_seconds % 86400 == 0:
        return "Daily"
    if interval_seconds % 3600 == 0:
        return "Hour"
    if interval_seconds % 60 == 0:
        return "Minute"
    return "Second"


def interval_label(interval_seconds: int, market: str) -> str:
    """DataFeeds interval label; forex takes plain seconds, crypto minute-aligned bars."""
    interval_seconds = int(interval_seconds)
    if market.lower() == "forex":
        return f"{interval_seconds}"
    if interval_seconds % 86400 == 0:
        return f"{interval_seconds // 86400}d"
    if interval_seconds % 3600 == 0:
        return f"{interval_seconds // 3600}h"
    if interval_seconds % 60 == 0:
        return f"{interval_seconds // 60}m"
    raise ValueError(
        f"interval {interval_seconds}s is not minute-aligned for crypto bars; use a multiple of 60"
    )


def _timespan(seconds: int) -> str:
    total = int(seconds)
    hours, rem = divmod(total, 3600)
    minutes, secs = divmod(rem, 60)
    return f"{hours:02}:{minutes:02}:{secs:02}"


def _meta_from_class(cls) -> dict:
    """Reads and normalizes a Research/ResearchStrategy declaration into a plain dict."""
    exchange = _get(cls, "exchange")
    if not exchange:
        raise ValueError("exchange is required (e.g. 'binance')")

    market = _get(cls, "market", "crypto") or "crypto"
    symbols = list(_get(cls, "symbols", None) or [])
    single = _get(cls, "symbol", None)
    if single and not symbols:
        symbols = [single]
    if not symbols:
        raise ValueError("symbols is required (provider-native tickers, e.g. ['BTCUSDT'])")

    raw_types = list(_get(cls, "event_types", ["Bar", "Trade", "Quote"]) or ["Bar", "Trade", "Quote"])
    event_types = []
    for raw in raw_types:
        canon = normalize_event_type(raw)
        if canon not in event_types:
            event_types.append(canon)

    interval_seconds = int(_get(cls, "interval_seconds", 60) or 60)

    symbols = [str(s) for s in symbols]
    meta = {
        "exchange": str(exchange),
        "market": str(market),
        "symbols": symbols,
        "event_types": event_types,
        "interval_seconds": interval_seconds,
        "start": str(_get(cls, "start", "") or ""),
        "end": str(_get(cls, "end", "") or ""),
        "resolution": str(_get(cls, "resolution", "") or resolution_for(interval_seconds)),
        "features": [str(f) for f in list(_get(cls, "features", []) or [])],
        "raw_fields": [str(f) for f in list(_get(cls, "raw_fields", []) or [])],
        "horizons": [str(h) for h in list(_get(cls, "horizons", []) or [])],
        "experiment_config": dict(_get(cls, "experiment_config", {}) or {}),
        "job_id": str(_get(cls, "job_id", "") or f"{exchange}-{symbols[0].lower()}"),
        "live": bool(_get(cls, "live", False)),
        "stream_live": bool(_get(cls, "stream_live", False)),
        "live_duration_seconds": float(_get(cls, "live_duration_seconds", 30) or 30),
    }
    _check_declaration(cls, meta)
    return meta


def research_meta(module) -> dict:
    """Normalized declaration from a script module (Research class or ResearchStrategy subclass)."""
    if hasattr(module, "Research"):
        return _meta_from_class(module.Research)

    subclass = _find_strategy_subclass(module)
    if subclass is not None:
        return subclass.meta()

    raise ValueError(
        f"{getattr(module, '__name__', 'script')} must define a Research class, or a subclass of "
        "quantlab.research.ResearchStrategy (see docs/python-strategies.md)."
    )


def symbol_dir(feed_root, market: str, provider: str, symbol: str) -> Path:
    """DataFeeds staging layout: <root>/<market>/<provider>/<symbol>/."""
    safe = str(symbol).replace("/", "").replace(" ", "_")
    return Path(feed_root) / market / provider / safe


def missing_symbols(feed_root, meta: dict) -> list[str]:
    """Symbols that have no staged CSV files under the DataFeeds layout."""
    missing = []
    for symbol in meta["symbols"]:
        folder = symbol_dir(feed_root, meta["market"], meta["exchange"], symbol)
        if not folder.is_dir() or not any(folder.glob("*.csv")):
            missing.append(symbol)
    return missing


def _csv_timestamp_bounds(path: Path):
    """(first, last) event timestamps of a sorted staged CSV, in ISO-8601 UTC."""
    first = last = None
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("timestamp_ms"):
                continue
            try:
                ms = float(line.split(",", 1)[0])
            except ValueError:
                continue
            seconds = ms / 1000.0
            if first is None:
                first = seconds
            last = seconds
    if first is None:
        return None
    # Offset-less UTC: the engine compares these strings against UTC event
    # timestamps by literal ticks — a "+00:00"/zone suffix is re-parsed as
    # local time and shifts the whole window on non-UTC machines.
    fmt = "%Y-%m-%dT%H:%M:%S.%f"
    return (
        datetime.fromtimestamp(first, tz=timezone.utc).strftime(fmt),
        datetime.fromtimestamp(last, tz=timezone.utc).strftime(fmt),
    )


def staged_window(feed_root, meta: dict):
    """Earliest/latest event timestamp across staged files (they are sorted ascending).

    Lets a run with empty ``start``/``end`` replay the full captured span without
    the engine needing unbounded-window support.
    """
    spans = []
    for symbol in meta["symbols"]:
        folder = symbol_dir(feed_root, meta["market"], meta["exchange"], symbol)
        if not folder.is_dir():
            continue
        for csv_file in sorted(folder.glob("*.csv")):
            bounds = _csv_timestamp_bounds(csv_file)
            if bounds:
                spans.append(bounds)
    if not spans:
        return None
    return min(s[0] for s in spans), max(s[1] for s in spans)


def _kind_flags(meta: dict) -> list[str]:
    """--no-bars/--no-trades/--no-quotes for the pull surface."""
    flags = []
    if "Bar" not in meta["event_types"]:
        flags.append("--no-bars")
    if "Trade" not in meta["event_types"]:
        flags.append("--no-trades")
    if "Quote" not in meta["event_types"]:
        flags.append("--no-quotes")
    return flags


def pull_commands(meta: dict, feed_root) -> list[list[str]]:
    """One `datafeeds pull` argv per symbol, mirrors the DataFeeds CLI surface."""
    flags = _kind_flags(meta)
    commands = []
    for symbol in meta["symbols"]:
        cmd = [
            sys.executable,
            "-m",
            "datafeeds",
            "pull",
            "--market",
            meta["market"],
            "--provider",
            meta["exchange"],
            "--symbol",
            symbol,
            "--mode",
            "historical",
        ]
        if meta["start"]:
            cmd += ["--start", meta["start"]]
        if meta["end"]:
            cmd += ["--end", meta["end"]]
        cmd += ["--interval", interval_label(meta["interval_seconds"], meta["market"])]
        cmd += flags + ["--out", str(Path(feed_root).resolve())]
        commands.append(cmd)
    return commands


def live_commands(meta: dict, feed_root) -> list[list[str]]:
    """One `datafeeds pull --mode live` argv per symbol (bounded websocket session)."""
    commands = []
    for symbol in meta["symbols"]:
        cmd = [
            sys.executable,
            "-m",
            "datafeeds",
            "pull",
            "--market",
            meta["market"],
            "--provider",
            meta["exchange"],
            "--symbol",
            symbol,
            "--mode",
            "live",
            "--duration",
            str(float(meta["live_duration_seconds"])),
        ]
        if "Bar" in meta["event_types"]:
            cmd += ["--interval", interval_label(meta["interval_seconds"], meta["market"])]
        cmd += _kind_flags(meta) + ["--out", str(Path(feed_root).resolve())]
        commands.append(cmd)
    return commands


def _run_pull(cmd: list[str], cwd: Path) -> None:
    proc = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True)
    if proc.stderr.strip():
        print(f"[datafeeds] {proc.stderr.strip()}", file=sys.stderr)
    if proc.stdout.strip():
        print(proc.stdout.strip())
    if proc.returncode != 0:
        args = dict(zip(cmd[4::2], cmd[5::2]))
        tail = (proc.stdout or proc.stderr).strip()[-1500:]
        raise ValueError(
            f"data pull failed (provider={args.get('--provider')} symbol={args.get('--symbol')} "
            f"market={args.get('--market')}):\n{tail}"
        )


def build_job_dict(script_path: str, meta: dict) -> dict:
    """The Runner job for the python_strategy experiment from a Research declaration."""
    job = {
        "jobId": meta["job_id"],
        "dataset": "datafeeds",
        "symbols": meta["symbols"],
        "assetClass": meta["market"],
        "venue": meta["exchange"],
        "resolution": meta["resolution"],
        "eventTypes": meta["event_types"],
        "features": meta["features"],
        "experimentName": "python_strategy",
        "strategyScript": str(Path(script_path).resolve()),
        "outputFormat": "csv",
        "reorder": "InOrderStreaming",
        "enableCheckpointing": False,
    }
    if meta["stream_live"]:
        # Direct event-driven live: the engine subscribes the WebSocket in-process and the
        # replay engine emits one observation per event (no observation grid), so nothing
        # that arrives is aggregated or dropped between interval boundaries.
        source = {"mode": "live", "provider": meta["exchange"]}
        if meta["market"]:
            source["category"] = meta["market"]
        if meta["live_duration_seconds"] > 0:
            source["liveDurationSeconds"] = int(meta["live_duration_seconds"])
        job["source"] = source
        job["observationInterval"] = None
    else:
        job["source"] = {"mode": "feed", "provider": meta["exchange"]}
        job["observationInterval"] = _timespan(meta["interval_seconds"])
    if meta["start"]:
        job["startTime"] = meta["start"]
    if meta["end"]:
        job["endTime"] = meta["end"]
    if meta["raw_fields"]:
        job["rawFields"] = meta["raw_fields"]
    if meta["horizons"]:
        job["horizons"] = meta["horizons"]
    if meta["experiment_config"]:
        job["experimentConfig"] = meta["experiment_config"]
    return job


def _flatten_results(output_root: Path, job_id: str, manifest: dict) -> None:
    """Coalesces the engine's per-symbol/experiment CSVs into flat run-level files.

    ``results/<job>/<job>.report.csv``  — observation rows
    ``results/<job>/<job>.signals.csv``  — strategy rows (on_observation/finalize)
    The nested ``<symbol>/`` and ``experiment/`` dirs are removed once emptied.
    """
    job_dir = output_root / job_id
    if not job_dir.is_dir():
        return
    for path in manifest.get("outputFiles") or []:
        p = Path(path)
        try:
            rel = p.relative_to(job_dir)
        except ValueError:
            continue
        if p.suffix.lower() == ".csv":
            kind = "signals" if "experiment" in rel.parts else "report"
            dest = job_dir / f"{job_id}.{kind}.csv"
        elif "experiment" in rel.parts:
            dest = job_dir / f"{job_id}.metrics{''.join(p.suffixes)}"
        else:
            continue
        if dest.exists() and dest.resolve() != p.resolve():
            dest.unlink()
        if p.resolve() != dest.resolve():
            p.rename(dest)
    for leftover in sorted(job_dir.rglob("*"), key=lambda c: len(c.parts), reverse=True):
        if leftover.is_file() and "_metrics" in leftover.name and leftover.suffix == ".json" \
                and "experiment" in leftover.parts:
            dest = job_dir / f"{job_id}.metrics.json"
            if dest.exists():
                dest.unlink()
            leftover.rename(dest)
    for child in sorted(job_dir.iterdir(), key=lambda c: len(c.parts), reverse=True):
        if child.is_dir() and not any(child.iterdir()):
            child.rmdir()


def _feed_provenance(feed_root: Path, meta: dict) -> str:
    """One line per symbol summarizing the pull manifest staged next to its CSVs."""
    lines = []
    for symbol in meta["symbols"]:
        pull = Path(feed_root) / meta["market"] / meta["exchange"] / symbol / "pull.json"
        if not pull.is_file():
            continue
        try:
            doc = json.loads(pull.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        bits = [f"{k}={v['rows']} rows" for k, v in doc.get("files", {}).items()]
        lines.append(f"  {symbol}: {doc.get('source')} window "
                     f"start={doc['requested_window_ms']['start_ms']} "
                     f"end={doc['requested_window_ms']['end_ms']} ({', '.join(bits) or 'no files'})")
        for note in doc.get("notes") or []:
            lines.append(f"    note: {note.replace(chr(8212), '-')}")
    return "\n".join(lines)


def _decompose_error(error) -> tuple[str, str, list[str]]:
    """Splits a manifest error string into (what, where, python traceback lines).

    ``what``   - the hook/message summary (e.g. "Strategy.on_observation failed ...")
    ``where``  - the last strategy-side frame ("<file>:<line> (<func>)")
    ``frames`` - the contiguous Python traceback (the .NET frames are dropped)
    """
    text = (error or "").replace("\r\n", "\n")
    lines = [ln for ln in text.split("\n")]

    what = lines[0].strip() if lines and lines[0].strip() else "run failed"
    marker = "StrategyScriptException: "
    if marker in what:
        what = what.split(marker, 1)[1]

    frames: list[str] = []
    for i, ln in enumerate(lines):
        if ln.strip().startswith('File "'):
            block = []
            j = i
            while j < len(lines) and lines[j].strip() and not lines[j].lstrip().startswith("at "):
                block.append(lines[j].strip())
                j += 1
            frames = block
            break

    where = ""
    for frame in reversed(frames):
        m = re.match(r'File "(.+?)", line (.+), in (.+)', frame)
        if m:
            where = f"{Path(m.group(1)).name}:{m.group(2)} ({m.group(3)})"
            break

    return what, where, frames


def _write_summary(feed_root: Path, output_root: Path, meta: dict, job: dict, result) -> str:
    """Writes ``summary.txt`` next to the run's CSVs and returns the text."""
    job_id = meta["job_id"]
    job_dir = output_root / job_id
    job_dir.mkdir(parents=True, exist_ok=True)
    manifest = result.manifest or {}
    metrics = manifest.get("metrics") or {}
    signals = metrics.get("signals", 0)
    buys = metrics.get("buys", 0)
    sig_file = job_dir / (job_id + ".signals.csv")
    if sig_file.is_file():
        with sig_file.open(encoding="utf-8") as fh:
            rows = list(csv.reader(fh))
        if rows:
            header = rows[0]
            action_idx = header.index("action") if "action" in header else -1
            signals = len(rows) - 1
            buys = sum(
                1 for r in rows[1:]
                if action_idx >= 0 and len(r) > action_idx and r[action_idx].strip().lower() == "buy"
            )
    status = "succeeded" if manifest.get("succeeded") else "FAILED"
    lines = [
        f"quantlab run - {job_id}",
        "=" * len(f"quantlab run - {job_id}"),
        f"status   : {status}{' (' + str(round(manifest.get('elapsedSeconds', 0), 2)) + 's)' if status == 'succeeded' else ''}",
        f"window   : {job.get('startTime')} -> {job.get('endTime')}",
        f"symbols  : {', '.join(meta['symbols'])} ({meta['market']}/{meta['exchange']})",
        f"replay   : {manifest.get('eventsProcessed', 0)} events, "
        f"{manifest.get('observationsWritten', 0)} observations",
        f"strategy : {meta.get('strategy', 'python_strategy')}",
        f"activity : signals={signals} buys={buys}",
        "",
        "files",
        f"  report.csv    {job_dir / (job_id + '.report.csv')}",
        f"  signals.csv   {job_dir / (job_id + '.signals.csv')}",
        f"  manifest.json {job_dir / 'manifest.json'}",
        f"  strategy.log  {job_dir / 'strategy.log'}",
    ]
    if not manifest.get("succeeded"):
        what, where, frames = _decompose_error(manifest.get("error"))
        lines.append(f"error     : {what}")
        if where:
            lines.append(f"where     : {where}")
        if frames:
            lines += ["", "traceback (strategy side)"] + [f"  {ln}" for ln in frames]
    provenance = _feed_provenance(feed_root, meta)
    if provenance:
        lines += ["", "data authenticity (per symbol)", provenance]
    path = job_dir / "summary.txt"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return "\n".join(lines)


def run_strategy(
    script_path: str,
    meta: dict,
    data_dir: str = "",
    output_dir: str = "",
    build: bool = False,
    no_pull: bool = False,
    job_only: bool = False,
    sample_seconds: float = 0,
    env: dict | None = None,
) -> RunResult:
    """Shared orchestration: pull (if needed) -> generate job -> run -> report.

    Returns a :class:`RunResult` (``exit_code`` is 0 on success). When
    ``sample_seconds > 0`` only the most recent that-many seconds of the
    staged/pulled window is replayed (quick iteration on a real window).
    """
    env = env or {}
    repo_root = Path(__file__).resolve().parents[3]

    # Strategies that subclass ResearchStrategy import quantlab; make the package's
    # parent directory reachable from the engine's embedded interpreter.
    os.environ.setdefault("QUANTLAB_PYTHON_PATH", str(Path(__file__).resolve().parents[1]))

    # Data and results live next to the strategy file by default (so the fetched
    # CSVs are easy to inspect), unless the caller points at another root.
    script_dir = Path(script_path).resolve().parent

    feed_root = Path(
        data_dir
        or env.get("QUANTLAB_DATA_ROOT")
        or os.environ.get("QUANTLAB_DATA_ROOT")
        or script_dir / "feeds"
    ).resolve()
    output_root = Path(
        output_dir
        or env.get("QUANTLAB_OUTPUT_ROOT")
        or os.environ.get("QUANTLAB_OUTPUT_ROOT")
        or script_dir / "results"
    ).resolve()
    output_root.mkdir(parents=True, exist_ok=True)

    # The engine's embedded interpreter writes the strategy's print()s/tracebacks
    # here, so hook output is never lost again.
    strategy_log_path = output_root / meta["job_id"] / "strategy.log"
    meta["experiment_config"] = {
        **meta.get("experiment_config", {}),
        "strategy_log": str(strategy_log_path),
    }

    job = build_job_dict(script_path, meta)
    job_id = meta["job_id"]
    if job_only:
        print(json.dumps(job, indent=2))
        return RunResult(job_id=job_id, exit_code=0, succeeded=True,
                         meta=meta, manifest={}, results_dir=str(output_root))

    manual = [t for t in meta["event_types"] if t in _MANUAL_TYPES]
    if manual:
        print(
            f"[research] note: {', '.join(manual)} are not auto-pulled by providers — "
            "stage the CSV by hand under <data-dir>/<market>/<exchange>/<symbol>/"
        )

    if meta["stream_live"]:
        if no_pull:
            raise ValueError("no_pull is not supported with stream_live=True — the live WebSocket session is the source")
        now = datetime.now(timezone.utc).replace(microsecond=0)
        job["startTime"] = (now - timedelta(seconds=300)).strftime("%Y-%m-%dT%H:%M:%S")
        job["endTime"] = (now + timedelta(seconds=int(meta["live_duration_seconds"]) + 300)).strftime("%Y-%m-%dT%H:%M:%S")
        print(
            f"[research] streaming {meta['live_duration_seconds']:g}s of live {meta['exchange']} "
            f"({', '.join(meta['event_types'])}) -> one observation per event, features per request"
        )
    elif meta["live"]:
        if no_pull:
            raise ValueError("no_pull is not supported with live=True — the live session is the pull")
        feed_root.mkdir(parents=True, exist_ok=True)
        print(f"[research] opening {meta['live_duration_seconds']:g}s live session(s) on {meta['exchange']} ...")
        datafeeds_dir = repo_root / "DataFeeds"
        for cmd in live_commands(meta, feed_root):
            _run_pull(cmd, datafeeds_dir)
    else:
        missing = missing_symbols(feed_root, meta)
        if missing:
            if no_pull:
                raise ValueError(
                    f"no staged data for {', '.join(missing)} under {feed_root} "
                    f"(expected <root>/{meta['market']}/{meta['exchange']}/<symbol>/); "
                    "run without --no-pull to auto-pull, or stage the CSVs first"
                )
            feed_root.mkdir(parents=True, exist_ok=True)
            print(f"[research] pulling {len(missing)} symbol(s) from {meta['exchange']} into {feed_root} ...")
            datafeeds_dir = repo_root / "DataFeeds"
            for cmd in pull_commands(meta, feed_root):
                _run_pull(cmd, datafeeds_dir)

    if not meta["start"] and not meta["end"] and not meta["stream_live"]:
        window = staged_window(feed_root, meta)
        if window is None:
            raise ValueError(
                f"no data staged under {feed_root} to derive a replay window "
                "(set start/end, or let the orchestrator pull/live-capture first)"
            )
        job["startTime"], job["endTime"] = window
        print(f"[research] derived replay window from staged data: {window[0]} -> {window[1]}")

    if sample_seconds and sample_seconds > 0 and not meta["stream_live"]:
        bounds = staged_window(feed_root, meta)
        if bounds is None:
            print("[research] sample skipped: no staged data to bound the replay window")
        else:
            end_dt = datetime.fromisoformat(bounds[1])
            start_dt = end_dt - timedelta(seconds=int(sample_seconds))
            job["startTime"] = start_dt.strftime("%Y-%m-%dT%H:%M:%S")
            job["endTime"] = end_dt.strftime("%Y-%m-%dT%H:%M:%S")
            print(f"[research] sample replay: last {sample_seconds:g}s -> "
                  f"{job['startTime']} -> {job['endTime']}")

    job_file = output_root / f"{job_id}.job.json"
    job_file.write_text(json.dumps(job, indent=2) + "\n", encoding="utf-8")
    print(f"[research] job: {job_file}")

    from .cloud import run_local

    result = run_local(str(job_file), data_dir=str(feed_root), output_dir=str(output_root), build=build)
    print(f"exit={result.exit_code}")
    print(result.stdout.strip())
    if result.stderr.strip():
        print(f"[stderr]\n{result.stderr.strip()}", file=sys.stderr)

    results_dir = output_root / job_id
    report = signals = []
    summary_text = ""
    manifest_out = result.manifest or {}
    if manifest_out:
        _flatten_results(output_root, job_id, manifest_out)
        summary_text = _write_summary(feed_root, output_root, meta, job, result)
        print(summary_text)
        if manifest_out.get("succeeded"):
            print(f"manifest : {results_dir / 'manifest.json'}")
        else:
            what, where, frames = _decompose_error(manifest_out.get("error"))
            print()
            print(f"run FAILED ({job_id})")
            print(f"  what    : {what}")
            if where:
                print(f"  where   : {where}")
            if frames:
                print("  python traceback:")
                for ln in frames:
                    print("    " + ln)
            print(f"  trace log: {strategy_log_path}")
        report = _csv_rows(results_dir / f"{job_id}.report.csv")
        signals = _csv_rows(results_dir / f"{job_id}.signals.csv")
    else:
        print("no manifest")

    log_msg = f"[research] strategy log: {strategy_log_path}"
    if strategy_log_path.is_file():
        body = strategy_log_path.read_text(encoding="utf-8", errors="replace")
        printed = [
            ln for ln in body.splitlines()
            if ln.strip() and not ln.startswith("# quantlab strategy log") and not ln.startswith("[ql]")
        ]
        if not printed:
            log_msg += " (empty — strategy printed nothing)"
    print(log_msg)

    return RunResult(
        job_id=job_id,
        exit_code=result.exit_code,
        succeeded=bool(manifest_out.get("succeeded")),
        meta=meta,
        manifest=manifest_out,
        report=report,
        signals=signals,
        summary=summary_text,
        results_dir=str(results_dir),
    )


def run(
    script_path: str,
    data_dir: str = "",
    output_dir: str = "",
    build: bool = False,
    no_pull: bool = False,
    job_only: bool = False,
    sample_seconds: float = 0,
    env: dict | None = None,
) -> int:
    """File-based one-script flow: pull (if needed) -> generate job -> run -> report.

    Returns the exit code; the full :class:`RunResult` is produced internally by
    ``run_strategy`` (the base-class API returns it directly from ``.run()``).
    """
    script = load_script(script_path)
    meta = research_meta(script)
    return run_strategy(
        script_path,
        meta,
        data_dir=data_dir,
        output_dir=output_dir,
        build=build,
        no_pull=no_pull,
        job_only=job_only,
        sample_seconds=sample_seconds,
        env=env,
    ).exit_code