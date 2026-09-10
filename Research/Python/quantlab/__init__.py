"""QuantLab research client.

Defines research jobs and runs them through the QuantConnect.Research.Runner engine.

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

    result = run(job, data_dir="C:/Users/KONZA/Desktop/Lean/Data",
                 runner_dir=None)  # None = auto-detect built runner
    print(result.manifest)
    print(result.load_parquet())  # pandas DataFrame
"""

from .runner import ResearchJob, ResearchResult, build_runner, find_runner, run

__all__ = ["ResearchJob", "ResearchResult", "run", "build_runner", "find_runner"]