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
        public void Experiment_ExposesOrderBookUpdatesAndLevels()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var observation = ObservationsTestHelpers.CreateObservationWithOrderBookUpdates();
            experiment.OnObservation(observation, new FeatureResult());

            var obs = host.Observations[0];

            var updates = (List<object>)obs["orderbook"];
            Assert.That(updates, Has.Count.EqualTo(2));

            var first = (Dictionary<string, object>)updates[0];
            Assert.That(first["side"], Is.EqualTo("bid"));
            Assert.That(first["action"], Is.EqualTo("add"));
            Assert.That((double)first["price"], Is.EqualTo(99m));
            Assert.That((double)first["quantity"], Is.EqualTo(3m));

            var bids = (List<object>)obs["bid_levels"];
            var asks = (List<object>)obs["ask_levels"];
            Assert.That(bids, Is.Not.Empty);
            Assert.That(asks, Is.Not.Empty);
            Assert.That(((Dictionary<string, object>)bids[0]).Keys,
                Is.EquivalentTo(new[] { "price", "quantity" }));
        }

        [Test]
        public void Experiment_NoOrderBookEvents_OmitsOrderbookButKeepsLevels()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            // This observation has no book *events* in the period, but its state was seeded from a
            // snapshot, so the reconstructed levels are still meaningful and must be exposed.
            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            var obs = host.Observations[0];
            Assert.That(obs.ContainsKey("orderbook"), Is.False, "no book updates occurred in the period");
            Assert.That(obs.ContainsKey("bid_levels"), Is.True);
            Assert.That(((List<object>)obs["bid_levels"]), Is.Not.Empty);
        }

        [TestCase("{\"bad\": NaN, \"close\": 1.0}", "NaN")]
        [TestCase("{\"a\": Infinity}", "Infinity")]
        [TestCase("{\"a\": -Infinity}", "-Infinity")]
        public void NonFiniteDetection_FlagsBareTokens(string json, string expected)
        {
            // json.dumps emits bare NaN/Infinity for non-finite floats: valid Python, invalid JSON.
            Assert.That(PythonNetStrategyHost.FindNonFiniteTokens(json), Does.Contain(expected));
        }

        [TestCase("{\"close\": 1.0, \"x\": 2}")]
        [TestCase("{\"label\": \"NaN\", \"close\": 1.0}")]
        [TestCase("{\"s\": \"infinity\", \"n\": null}")]
        public void NonFiniteDetection_IgnoresCleanAndQuotedText(string json)
        {
            // A quoted "NaN" is data, not a non-finite float, and must not be rejected.
            Assert.That(PythonNetStrategyHost.FindNonFiniteTokens(json), Is.Null);
        }

        [Test]
        public void NonFiniteDetection_IsBounded()
        {
            var json = "{\"a\": " + string.Join(", ", Enumerable.Repeat("NaN", 50_000)) + "}";
            var report = PythonNetStrategyHost.FindNonFiniteTokens(json);

            Assert.That(report, Is.Not.Null);
            Assert.That(report.Split(", ").Length, Is.LessThanOrEqualTo(3),
                "a pathological row is reported a bounded number of times, not once per occurrence");
        }

        [Test]
        public void Experiment_NoBookStateAtAll_OmitsLevelKeys()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var observation = new Observation
            {
                Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Events = new List<MarketEvent>()
            };

            experiment.OnObservation(observation, new FeatureResult());

            var obs = host.Observations[0];
            Assert.That(obs.ContainsKey("orderbook"), Is.False);
            Assert.That(obs.ContainsKey("bid_levels"), Is.False);
            Assert.That(obs.ContainsKey("ask_levels"), Is.False);
        }

        [Test]
        public void Experiment_ExposesOrderBookSnapshotEvents()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithSnapshot(), new FeatureResult());

            var obs = host.Observations[0];
            var snapshots = (List<object>)obs["orderbook_snapshots"];
            Assert.That(snapshots, Has.Count.EqualTo(1));

            var first = (Dictionary<string, object>)snapshots[0];
            Assert.That(first["type"], Is.EqualTo("orderbook_snapshot"));
            Assert.That((double)first["best_bid"], Is.EqualTo(99m));
            Assert.That((double)first["best_ask"], Is.EqualTo(101m));
            Assert.That((double)first["mid"], Is.EqualTo(100m));
            Assert.That(((List<object>)first["bids"]).Count, Is.EqualTo(1));
            Assert.That(((List<object>)first["asks"]).Count, Is.EqualTo(1));
        }

        [Test]
        public void Experiment_ExposesCustomEvents_SoLiquidationIsDetectable()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithCustomEvent(), new FeatureResult());

            var obs = host.Observations[0];
            var customs = (List<object>)obs["custom_events"];
            Assert.That(customs, Has.Count.EqualTo(1));

            var first = (Dictionary<string, object>)customs[0];
            Assert.That(first["custom_type"], Is.EqualTo("Liquidation"));
            Assert.That((double)first["value"], Is.EqualTo(25000m));
            Assert.That(first["type"], Is.EqualTo("custom"));
            var data = (Dictionary<string, object>)first["data"];
            Assert.That(data["side"], Is.EqualTo("sell"));
        }

        [Test]
        public void Experiment_ReportsEventCount_ForEveryEventType()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            // Four trades: the count must reflect the period, not the per-type lists.
            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            var obs = host.Observations[0];
            Assert.That(obs["event_count"], Is.EqualTo(4));
            Assert.That(((List<object>)obs["trades"]).Count, Is.EqualTo(4));
        }

        [Test]
        public void Experiment_EventCountIsZeroForAnEmptyPeriod()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(
                new Observation { Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                new FeatureResult());

            Assert.That(host.Observations[0]["event_count"], Is.EqualTo(0));
        }

        [Test]
        public void Experiment_ExposesUnifiedEventsList_WhenEnabled()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptExposeEvents = true;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            var obs = host.Observations[0];
            var events = (List<object>)obs["events"];
            Assert.That(events, Has.Count.EqualTo(4));
            Assert.That(events.Cast<Dictionary<string, object>>().Select(e => e["type"]),
                Is.All.EqualTo("trade"));

            // The unified list shares serialization with the typed list, so the shapes must agree.
            var firstUnified = (Dictionary<string, object>)events[0];
            var firstTyped = (Dictionary<string, object>)((List<object>)obs["trades"])[0];
            Assert.That(firstUnified["price"], Is.EqualTo(firstTyped["price"]));
        }

        [Test]
        public void Experiment_OmitsUnifiedEventsList_WhenDisabled()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptExposeEvents = false;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            Assert.That(host.Observations[0].ContainsKey("events"), Is.False);
        }

        [Test]
        public void Experiment_ExposesUnifiedEvents_InStreamOrder_AcrossTypes()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptExposeEvents = true;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var mixed = ObservationsTestHelpers.CreateObservationWithTrades();
            mixed.Events.Add(MarketEventNormalizer.CreateOrderBookUpdate(
                mixed.State.Symbol, mixed.Timestamp, OrderBookSide.Bid, 100m, 1m, OrderBookUpdateAction.Modify));

            experiment.OnObservation(mixed, new FeatureResult());

            var types = ((List<object>)host.Observations[0]["events"])
                .Cast<Dictionary<string, object>>().Select(e => (string)e["type"]).ToList();
            Assert.That(types, Is.EqualTo(new[] { "trade", "trade", "trade", "trade", "orderbook_update" }));
        }

        [Test]
        public void Experiment_ExposesRequestedRawFields()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.RawFields = new List<string> { "bid_price", "ask_price", "depth", "trade_flow" };
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            var raw = (Dictionary<string, object>)host.Observations[0]["raw"];
            Assert.That(raw["bid_price"], Is.EqualTo(100d));
            Assert.That(raw["ask_price"], Is.EqualTo(101d));
            Assert.That((double)raw["depth"], Is.EqualTo(200m));
            // +1103 aggressive buy notional, -209 aggressive sell notional.
            Assert.That((double)raw["trade_flow"], Is.EqualTo(894m));
        }

        [Test]
        public void Experiment_OmitsRawDict_WhenNoRawFieldsRequested()
        {
            var host = new FakeHost();
            var job = BuildJob();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), new FeatureResult());

            Assert.That(host.Observations[0].ContainsKey("raw"), Is.False);
        }

        [Test]
        public void Experiment_RawDictSkipsNamesOwnedByAFeature()
        {
            var host = new FakeHost();
            var job = BuildJob();
            // "mid_price" is both a raw field and a feature; the feature owns the column, and the
            // payload must agree with the observation CSV rather than shadowing it.
            job.RawFields = new List<string> { "mid_price", "bid_depth" };
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var features = new FeatureResult();
            features.Values["mid_price"] = 100.5m;
            experiment.OnObservation(ObservationsTestHelpers.CreateObservationWithTrades(), features);

            var raw = (Dictionary<string, object>)host.Observations[0]["raw"];
            Assert.That(raw.ContainsKey("mid_price"), Is.False);
            Assert.That(raw.ContainsKey("bid_depth"), Is.True);
        }

        [Test]
        public void Experiment_ExposesBoundedHistory_ExcludingCurrentPeriod()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptHistoryPeriods = 2;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < 4; i++)
            {
                experiment.OnObservation(
                    ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", start.AddMinutes(i)),
                    new FeatureResult());
            }

            // Period 3 sees periods 1 and 2 only: the window is capped at 2 and never includes now.
            var history = (List<object>)host.Observations[3]["history"];
            Assert.That(history, Has.Count.EqualTo(2));

            var first = (Dictionary<string, object>)history[0];
            var second = (Dictionary<string, object>)history[1];
            Assert.That(first["timestamp"], Is.EqualTo(start.AddMinutes(1).ToString("O")));
            Assert.That(second["timestamp"], Is.EqualTo(start.AddMinutes(2).ToString("O")));
            Assert.That((double)first["close"], Is.EqualTo(100.5m));
        }

        [Test]
        public void Experiment_HistoryIncludesFeatureValues()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptHistoryPeriods = 3;
            job.Features = new List<string> { "mid_price" };
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            // Features are recorded into the history entry for the period they belong to, so the
            // first period must carry them for the second period to see them.
            var features = new FeatureResult();
            features.Values["mid_price"] = 100.5m;
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts), features);

            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts.AddMinutes(1)), new FeatureResult());

            var history = (List<object>)host.Observations[1]["history"];
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That((double)((Dictionary<string, object>)history[0])["mid_price"], Is.EqualTo(100.5m));
        }

        [Test]
        public void Experiment_HistoryIsKeyedBySymbol_SoItNeverLeaksAcrossSymbols()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.Symbols = new List<string> { "BTCUSDT", "ETHUSDT" };
            job.ScriptHistoryPeriods = 5;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts), new FeatureResult());
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts.AddMinutes(1)), new FeatureResult());
            // A different symbol must start with an empty window, not inherit BTCUSDT's periods.
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("ETHUSDT", ts), new FeatureResult());

            var ethHistory = (List<object>)host.Observations[2]["history"];
            Assert.That(ethHistory, Has.Count.EqualTo(0));
        }

        [Test]
        public void Experiment_OmitsHistory_WhenHistoryPeriodsIsZero()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptHistoryPeriods = 0;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts), new FeatureResult());

            Assert.That(host.Observations[0].ContainsKey("history"), Is.False);
        }

        [Test]
        public void Experiment_Reset_ClearsHistoryBetweenRuns()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptHistoryPeriods = 5;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts), new FeatureResult());
            experiment.Reset();

            // A re-run re-initializes the host, exactly as the executor does per job.
            experiment.Initialize(job.CreateExperimentContext());
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts), new FeatureResult());

            var history = (List<object>)host.Observations[1]["history"];
            Assert.That(history, Has.Count.EqualTo(0), "a re-run must not inherit the previous run's periods");
        }

        [Test]
        public void Experiment_ContextAdvertisesHistoryAndEventSettings()
        {
            var host = new FakeHost();
            var job = BuildJob();
            job.ScriptHistoryPeriods = 7;
            job.ScriptExposeEvents = true;
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            Assert.That(host.InitContext["history_periods"], Is.EqualTo(7));
            Assert.That(host.InitContext["expose_events"], Is.EqualTo(true));
        }

        [Test]
        public void Job_ConfigurationHash_ChangesWithScriptContractSettings()
        {
            var a = BuildJob();
            var b = BuildJob();
            b.ScriptHistoryPeriods = 5;
            b.ScriptExposeEvents = true;

            Assert.That(a.GetConfigurationHash(), Is.Not.EqualTo(b.GetConfigurationHash()));
        }

        [Test]
        public void Experiment_ObservationRowAutoFillsTimestampAndSymbol()        {
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

        [Test]
        public void Executor_WritesRunMetadata_DescribingWhatTheRunConsumed()
        {
            var root = TempRoot();
            var outputRoot = TempRoot();
            WriteFeedFiles(root);

            var job = BuildFeedJob(root, outputRoot);
            job.ScriptHistoryPeriods = 4;
            job.ScriptExposeEvents = true;
            job.RawFields = new List<string> { "bid_depth", "trade_flow" };
            job.StrategyScript = Path.Combine(root, "sma_cross.py");

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var executor = new LocalResearchExecutor(
                source,
                environment: new ResearchEnvironment(dataRoot: root, outputRoot: outputRoot));

            var result = executor.Execute(job);
            Assert.That(result.Succeeded, Is.True, result.Error);

            var path = Path.Combine(outputRoot, job.JobId, "run_metadata.json");
            Assert.That(File.Exists(path), Is.True, "every run must leave an audit trail");

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root0 = document.RootElement;

            Assert.That(root0.GetProperty("schemaVersion").GetInt32(), Is.EqualTo(1));
            Assert.That(root0.GetProperty("jobId").GetString(), Is.EqualTo(job.JobId));
            Assert.That(root0.GetProperty("succeeded").GetBoolean(), Is.True);
            Assert.That(root0.GetProperty("configurationHash").GetString(), Is.EqualTo(job.GetConfigurationHash()));
            Assert.That(root0.GetProperty("dataRoot").GetString(), Is.EqualTo(root));

            var source0 = root0.GetProperty("source");
            Assert.That(source0.GetProperty("mode").GetString(), Is.EqualTo("feed"));
            Assert.That(source0.GetProperty("provider").GetString(), Is.EqualTo("binance"));

            Assert.That(root0.GetProperty("eventTypes").GetArrayLength(), Is.EqualTo(3));
            Assert.That(root0.GetProperty("rawFields").EnumerateArray().Select(e => e.GetString()),
                Is.EquivalentTo(new[] { "bid_depth", "trade_flow" }));

            var contract = root0.GetProperty("pythonContract");
            Assert.That(contract.GetProperty("historyPeriods").GetInt32(), Is.EqualTo(4));
            Assert.That(contract.GetProperty("exposeEvents").GetBoolean(), Is.True);

            var stats = root0.GetProperty("stats");
            Assert.That(stats.GetProperty("eventsProcessed").GetInt64(), Is.GreaterThan(0));
            Assert.That(stats.GetProperty("observationsWritten").GetInt64(), Is.GreaterThan(0));
            Assert.That(stats.GetProperty("outputFiles").GetArrayLength(), Is.GreaterThan(0));

            var timing = root0.GetProperty("timing");
            Assert.That(timing.GetProperty("elapsedSeconds").GetDouble(), Is.GreaterThanOrEqualTo(0));
        }

        [Test]
        public void Executor_WritesRunMetadata_EvenWhenTheRunFails()
        {
            var outputRoot = TempRoot();
            var job = BuildJob();
            job.OutputLocation = outputRoot;
            job.Features = new List<string> { "no_such_feature_exists" };

            var executor = new LocalResearchExecutor(environment: new ResearchEnvironment(outputRoot: outputRoot));
            var result = executor.Execute(job);

            Assert.That(result.Succeeded, Is.False);
            var path = Path.Combine(outputRoot, job.JobId, "run_metadata.json");
            Assert.That(File.Exists(path), Is.True, "a failed run is exactly when the audit trail matters");

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(document.RootElement.GetProperty("succeeded").GetBoolean(), Is.False);
            Assert.That(document.RootElement.GetProperty("error").GetString(), Does.Contain("no_such_feature_exists"));
        }

        [TestCase("Bid_Price", 100d)]
        [TestCase("BID_PRICE", 100d)]
        [TestCase("  bid_price  ", 100d)]
        public void RawFields_ResolveCaseInsensitively_LikePreflightAcceptsThem(string field, double expected)
        {
            // Preflight matches raw field names case-insensitively, so resolution must too;
            // otherwise a job that passes validation throws part-way through the replay.
            var observation = ObservationsTestHelpers.CreateObservationWithTrades();
            Assert.That(Convert.ToDouble(RawFieldValues.For(observation, field)), Is.EqualTo(expected));
        }
    }
}