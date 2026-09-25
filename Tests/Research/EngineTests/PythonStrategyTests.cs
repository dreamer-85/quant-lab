using QuantConnect.Research.Engine;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Experiments.Python;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Ingest.LocalFeed;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class PythonStrategyTests
    {
        private sealed class FakeHost : IPythonStrategyHost
        {
            public bool Disposed;
            public string InitializedScript;
            public Dictionary<string, object> InitContext;
            public readonly List<Dictionary<string, object>> Observations = new();
            public readonly List<Dictionary<string, object>> Features = new();
            public readonly List<Dictionary<string, object>> Outcomes = new();
            public readonly StrategyFinalizeResult Final = new();
            public bool Finalized;

            public Func<Dictionary<string, object>, Dictionary<string, object>, Dictionary<string, object>> RowFor =
                (observation, features) => null;

            public void Initialize(string scriptPath, Dictionary<string, object> context)
            {
                InitializedScript = scriptPath;
                InitContext = context;
            }

            public Dictionary<string, object> OnObservation(Dictionary<string, object> observation, Dictionary<string, object> features)
            {
                Observations.Add(observation);
                Features.Add(features);
                return RowFor(observation, features);
            }

            public void OnOutcome(Dictionary<string, object> outcome)
            {
                Outcomes.Add(outcome);
            }

            public StrategyFinalizeResult Finalize()
            {
                Finalized = true;
                return Final;
            }

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private static ResearchJob BuildJob()
        {
            return new ResearchJob
            {
                JobId = "py-test",
                Dataset = "datafeeds",
                Symbols = new List<string> { "BTCUSDT" },
                AssetClass = "crypto",
                Venue = "binance",
                Resolution = Resolution.Minute,
                StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2024, 1, 1, 0, 10, 0, DateTimeKind.Utc),
                EventTypes = new List<MarketEventType> { MarketEventType.Bar, MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = TimeSpan.FromMinutes(1),
                Features = new List<string> { "mid_price", "spread", "trade_volume" },
                ExperimentName = "python_strategy",
                StrategyScript = @"C:\strategies\sma_cross.py",
                OutputFormat = "csv",
                EnableCheckpointing = false
            };
        }

        private static string TempRoot()
        {
            return Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
        }

        private static ResearchJob BuildFeedJob(string root, string outputRoot)
        {
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };
            job.OutputLocation = outputRoot;
            return job;
        }

        private static void WriteFeedFiles(string root, string symbol = "BTCUSDT")
        {
            var dir = Path.Combine(root, "crypto", "binance", symbol);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "bars_60.csv"),
                "timestamp_ms,open,high,low,close,volume\n" +
                "1704067200000,42000,42050,41900,42010,10.5\n" +
                "1704067260000,42010,42100,41990,42090,12.0\n");
            File.WriteAllText(Path.Combine(dir, "trades.csv"),
                "timestamp_ms,price,size,side,trade_id\n" +
                "1704067201000,42005,0.10,buy,t-1\n" +
                "1704067202000,42020,0.25,sell,t-2\n");
        }

        [Test]
        public void Factory_CreatesPythonStrategyExperiment()
        {
            var experiment = ExperimentFactory.Create(BuildJob());
            Assert.That(experiment, Is.InstanceOf<PythonStrategyExperiment>());
        }

        [Test]
        public void Validate_RequiresStrategyScriptForPythonStrategy()
        {
            var job = BuildJob();
            job.StrategyScript = string.Empty;
            Assert.That(job.Validate(out var errors), Is.False);
            Assert.That(errors.Any(e => e.Contains("StrategyScript")), Is.True);

            job.StrategyScript = @"C:\strategies\sma_cross.py";
            Assert.That(job.Validate(out _), Is.True);
        }

        [Test]
        public void Experiment_InitializesHostWithScriptAndResolvedContext()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            var context = job.CreateExperimentContext();

            experiment.Initialize(context);

            Assert.That(host.InitializedScript, Is.EqualTo(job.StrategyScript));
            Assert.That(host.InitContext["dataset"], Is.EqualTo("datafeeds"));
            Assert.That(host.InitContext["asset_class"], Is.EqualTo("crypto"));
            Assert.That(host.InitContext["venue"], Is.EqualTo("binance"));
            Assert.That((string)host.InitContext["resolution"], Is.EqualTo("Minute"));
            Assert.That((double)host.InitContext["observation_interval_seconds"], Is.EqualTo(60d));
            Assert.That((List<string>)host.InitContext["features"], Contains.Item("mid_price"));
            Assert.That((Dictionary<string, object>)host.InitContext["config"], Is.Not.Null);
        }

        [Test]
        public void Experiment_ExperimentConfigScriptOverridesJobStrategyScript()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            var context = job.CreateExperimentContext();
            context.Configuration["script"] = @"C:\strategies\override.py";

            experiment.Initialize(context);

            Assert.That(host.InitializedScript, Is.EqualTo(@"C:\strategies\override.py"));
        }

        [Test]
        public void Experiment_ObservationFeedsRawDataAndFeaturesToHost()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var observation = ObservationsTestHelpers.CreateObservationWithTrades();
            var features = new FeatureResult
            {
                Timestamp = observation.Timestamp,
                Symbol = observation.State.Symbol,
                Values = new Dictionary<string, decimal> { ["mid_price"] = 100.5m }
            };

            experiment.OnObservation(observation, features);

            Assert.That(host.Observations, Has.Count.EqualTo(1));
            var obs = host.Observations[0];
            Assert.That(obs["symbol"], Is.EqualTo("BTCUSDT"));
            Assert.That(obs["trade_count"], Is.EqualTo(4));
            var trades = (List<object>)obs["trades"];
            Assert.That(trades, Has.Count.EqualTo(4));
            Assert.That(((Dictionary<string, object>)trades[0])["side"], Is.EqualTo("buy"));
            Assert.That((double)obs["mid"], Is.EqualTo(100.5d));

            Assert.That(host.Features[0]["mid_price"], Is.EqualTo(100.5d));
        }

        [Test]
        public void Experiment_BarOnlyObservationExposesPeriodPrices()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithBars(), new FeatureResult());

            Assert.That(host.Observations, Has.Count.EqualTo(1));
            var obs = host.Observations[0];
            Assert.That((double)obs["open"], Is.EqualTo(99d));
            Assert.That((double)obs["close"], Is.EqualTo(107d));
            Assert.That((double)obs["high"], Is.EqualTo(108d));
            Assert.That((double)obs["low"], Is.EqualTo(98d));
            Assert.That((double)obs["last_price"], Is.EqualTo(107d));
            Assert.That((List<object>)obs["bars"], Has.Count.EqualTo(2));
        }

        [Test]
        public void Experiment_ObservationRowAutoFillsTimestampAndSymbol()
        {
            var host = new FakeHost();
            host.RowFor = (observation, features) => new Dictionary<string, object> { ["signal"] = 1.0d };
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var observation = ObservationsTestHelpers.CreateObservationWithTrades();
            experiment.OnObservation(observation, new FeatureResult());

            var result = experiment.Finalize();
            var row = result.Rows.Single();
            Assert.That(row["signal"], Is.EqualTo(1.0d));
            Assert.That(row["timestamp"], Is.EqualTo(observation.Timestamp.ToString("O")));
            Assert.That(row["symbol"], Is.EqualTo("BTCUSDT"));
        }

        [Test]
        public void Experiment_OutcomeForwardsHorizonAndReturn()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var referenceTs = new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);
            experiment.OnOutcome(new OutcomeData
            {
                ReferenceTimestamp = referenceTs,
                FuturePrice = 101m,
                ReferencePrice = 100m,
                Horizon = TimeSpan.FromMinutes(1)
            });

            Assert.That(host.Outcomes, Has.Count.EqualTo(1));
            var outcome = host.Outcomes[0];
            Assert.That(outcome["horizon_seconds"], Is.EqualTo(60d));
            Assert.That(outcome["future_price"], Is.EqualTo(101d));
            Assert.That(outcome["outcome_return"], Is.EqualTo(0.01d));
        }

        [Test]
        public void Experiment_FinalizeCollectsRowsMetricsAndMetadata()
        {
            var host = new FakeHost();
            host.Final.Rows.Add(new Dictionary<string, object> { ["note"] = "from_finalize" });
            host.Final.Metrics["sharpe"] = 1.25d;
            host.Final.Metadata["author"] = "tests";

            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());
            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            var result = experiment.Finalize();

            Assert.That(host.Finalized, Is.True);
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Metrics["sharpe"], Is.EqualTo(1.25d));
            Assert.That(result.Metrics["observation_count"], Is.EqualTo(1L));
            Assert.That(result.Metrics["row_count"], Is.EqualTo(1L));
            Assert.That(result.Metadata["author"], Is.EqualTo("tests"));
            Assert.That(result.Metadata["strategy_script"], Is.EqualTo(job.StrategyScript));
            Assert.That(result.Rows.Single()["note"], Is.EqualTo("from_finalize"));
            Assert.That(host.Disposed, Is.True);
        }

        [Test]
        public void Executor_RunsPythonStrategyEndToEnd()
        {
            var root = TempRoot();
            var outputRoot = TempRoot();
            WriteFeedFiles(root);

            var job = BuildFeedJob(root, outputRoot);
            job.StrategyScript = Path.Combine(root, "sma_cross.py");
            job.Horizons = new List<string> { "60s" };

            var host = new FakeHost();
            host.RowFor = (observation, features) =>
                new Dictionary<string, object>
                {
                    ["signal"] = features.TryGetValue("mid_price", out var mid) ? mid : 0m
                };
            host.Final.Rows.Add(new Dictionary<string, object> { ["note"] = "final_e2e" });
            host.Final.Metrics["rows_from_strategy"] = 1L;
            host.Final.Metadata["author"] = "e2e";

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var executor = new LocalResearchExecutor(
                source,
                environment: new ResearchEnvironment(outputRoot: outputRoot),
                experimentFactory: j => new PythonStrategyExperiment(j, () => host));

            var result = executor.Execute(job);

            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.ExperimentResult, Is.Not.Null);
            Assert.That(result.ExperimentResult.IsSuccess, Is.True);
            Assert.That(host.Outcomes.Count, Is.GreaterThan(0), "delayed labels should be resolved and forwarded");
            Assert.That(result.ExperimentResult.Metrics["rows_from_strategy"], Is.EqualTo(1L));
            Assert.That(result.ExperimentResult.Metrics["observation_count"], Is.GreaterThan(0L));
            Assert.That(result.ExperimentResult.Rows.Any(r => r.ContainsKey("signal")), Is.True);
            Assert.That(result.ExperimentResult.Rows.First(r => r.ContainsKey("signal"))["timestamp"], Is.Not.Empty);
            Assert.That(result.ExperimentResult.Metadata["author"], Is.EqualTo("e2e"));
            Assert.That(host.Disposed, Is.True);

            var expCsv = Path.Combine(outputRoot, job.JobId, "experiment", "python_strategy.csv");
            Assert.That(File.Exists(expCsv), Is.True);
            Assert.That(File.ReadAllText(expCsv), Does.Contain("signal"));

            var metricsJson = Path.Combine(outputRoot, job.JobId, "experiment", "python_strategy_metrics.json");
            Assert.That(File.Exists(metricsJson), Is.True);
        }
    }
}