"""QuantLab research client: local + cloud orchestration.

Defines research jobs and runs them through the QuantConnect.Research.Runner
engine — either locally (Runner DLL) or on a GCE VM (via gcloud/gsutil) —
and compares the results.

Basic usage:

    from quantlab import ResearchJob, run

    job = ResearchJob(
        dataset="btcusdt_trade",
        symbols=["BTCUSDT"],
        asset_class="crypto",
        venue="bybit",
        resolution="minute",
        start_time="2022-12-13T00:00:00Z",
        end_time="2022-12-13T23:59:00Z",
        event_types=["bar"],
        observation_interval_seconds=300,
        features=["mid_price", "trade_volume", "trade_intensity"],
        experiment_name="dataset_audit",
    )

    result = run(job, data_dir="C:/Users/KONZA/Desktop/QuantLab/Lean/Data",
                 runner_dir=None)  # None = auto-detect built runner
    print(result.manifest)
    print(result.load_parquet())  # pandas DataFrame

Unified CLI (cloud orchestration included):

    python -m quantlab env
    python -m quantlab run local  jobs/my.json --data-dir <lean-data>
    python -m quantlab bundle build --data-root <lean-data> --out bundle.tgz
    python -m quantlab bundle upload bundle.tgz
    python -m quantlab run cloud  jobs/my.json --vm quantlab-vm
    python -m quantlab results download bybit-btcusdt-20221213 --dest out/
    python -m quantlab compare out-local/ out-cloud/
"""

from .runner import ResearchJob, ResearchResult, build_runner, find_runner, run
from .cloud import (
    CompareResult,
    compare_local_cloud,
    download_object,
    download_results,
    environment,
    find_gcloud,
    find_gsutil,
    load_environment,
    make_bundle,
    run_local,
    run_local_dict,
    submit_job,
    upload_bundle,
)
from .discover import DatasetProfile, build_job, discover_profiles, job_to_dict

__all__ = [
    "ResearchJob",
    "ResearchResult",
    "run",
    "run_local",
    "run_local_dict",
    "build_runner",
    "find_runner",
    "submit_job",
    "make_bundle",
    "upload_bundle",
    "download_results",
    "download_object",
    "compare_local_cloud",
    "CompareResult",
    "environment",
    "load_environment",
    "find_gcloud",
    "find_gsutil",
    "DatasetProfile",
    "discover_profiles",
    "build_job",
    "job_to_dict",
]