using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Experiments.Python;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// docs/python-strategies.md is the contract a script author codes against. A payload key that
    /// exists but is undocumented is as broken as one that is documented but absent, and both fail
    /// silently at runtime. These tests drive the real serializer and require the doc to name every
    /// key it produces, so the two cannot drift apart.
    /// </summary>
    [TestFixture]
    public class PythonPayloadDocumentationTests
    {
        private static string DocsPath => Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "docs", "python-strategies.md");

        private static string Docs
        {
            get
            {
                var path = DocsPath;
                Assert.That(File.Exists(path), Is.True, $"docs/python-strategies.md not found at {path}");
                return File.ReadAllText(path);
            }
        }

        private sealed class CapturingHost : IPythonStrategyHost
        {
            public Dictionary<string, object> Context;
            public readonly List<Dictionary<string, object>> Observations = new();

            public void Initialize(string scriptPath, Dictionary<string, object> context) => Context = context;

            public Dictionary<string, object> OnObservation(
                Dictionary<string, object> observation, Dictionary<string, object> features)
            {
                Observations.Add(observation);
                return null;
            }

            public void OnOutcome(Dictionary<string, object> outcome) { }
            public StrategyFinalizeResult Finalize() => new();
            public void Dispose() { }
        }

        private static ResearchJob BuildJob()
        {
            return new ResearchJob
            {
                JobId = "doc-test",
                Dataset = "datafeeds",
                Symbols = new List<string> { "BTCUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                Resolution = Resolution.Minute,
                StartTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2025, 1, 1, 0, 10, 0, DateTimeKind.Utc),
                ObservationInterval = TimeSpan.FromMinutes(1),
                ExperimentName = "python_strategy",
                StrategyScript = "strategy.py",
                OutputFormat = "csv",
                EnableCheckpointing = false
            };
        }

        private static Dictionary<string, object> Capture(ResearchJob job, Observation observation)
        {
            var host = new CapturingHost();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());
            experiment.OnObservation(observation, new FeatureResult());
            return host.Observations.Single();
        }

        private static void AssertDocumented(string key)
        {
            Assert.That(Docs, Does.Contain($"`{key}`"),
                $"payload key '{key}' is produced by the engine but not named in docs/python-strategies.md");
        }

        [Test]
        public void EveryEventList_IsDocumented()
        {
            var job = BuildJob();
            job.ScriptExposeEvents = true;
            var observation = ObservationsTestHelpers.CreateObservationWithTrades();
            observation.Events.Add(MarketEventNormalizer.CreateOrderBookUpdate(
                observation.State.Symbol, observation.Timestamp,
                OrderBookSide.Bid, 100m, 1m, OrderBookUpdateAction.Modify));

            var payload = Capture(job, observation);

            foreach (var key in new[]
                     {
                         "bars", "trades", "quotes", "orderbook", "orderbook_snapshots",
                         "custom_events", "events", "bid_levels", "ask_levels", "event_count"
                     })
            {
                AssertDocumented(key);
            }

            Assert.That(payload.ContainsKey("event_count"), Is.True);
        }

        [Test]
        public void EveryScalarKey_IsDocumented()
        {
            var payload = Capture(BuildJob(), ObservationsTestHelpers.CreateObservationWithTrades());

            foreach (var key in payload.Keys.Where(k => payload[k] is double or int))
            {
                AssertDocumented(key);
            }
        }

        [Test]
        public void SnapshotAndCustomEvent_Keys_AreDocumented()
        {
            foreach (var observation in new[]
                     {
                         ObservationsTestHelpers.CreateObservationWithSnapshot(),
                         ObservationsTestHelpers.CreateObservationWithCustomEvent()
                     })
            {
                var payload = Capture(BuildJob(), observation);
                foreach (var list in payload.Where(kv => kv.Value is List<object>))
                {
                    foreach (var entry in (List<object>)list.Value)
                    {
                        foreach (var key in ((Dictionary<string, object>)entry).Keys)
                        {
                            AssertDocumented(key);
                        }
                    }
                }
            }
        }

        [Test]
        public void ContextKeys_AreDocumented()
        {
            var job = BuildJob();
            job.RawFields = new List<string> { "bid_price" };
            job.ScriptHistoryPeriods = 3;
            job.ScriptExposeEvents = true;

            var host = new CapturingHost();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            foreach (var key in host.Context.Keys)
            {
                AssertDocumented(key);
            }
        }

        [Test]
        public void HistoryEntryKeys_AreDocumented()
        {
            var job = BuildJob();
            job.Features = new List<string> { "mid_price" };
            job.ScriptHistoryPeriods = 2;
            var features = new FeatureResult();
            features.Values["mid_price"] = 100.5m;

            var host = new CapturingHost();
            var experiment = new PythonStrategyExperiment(job, () => host);
            experiment.Initialize(job.CreateExperimentContext());

            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts), features);
            experiment.OnObservation(ObservationsTestHelpers.CreateTradesObservationFor("BTCUSDT", ts.AddMinutes(1)), new FeatureResult());

            var history = (List<object>)host.Observations[1]["history"];

            // Feature names are carried into history under their own names by design, and the doc
            // describes that as a rule rather than enumerating the registry, so they are not pinned
            // individually. Everything the engine adds itself is pinned.
            foreach (var key in ((Dictionary<string, object>)history[0]).Keys
                         .Where(k => !job.Features.Contains(k)))
            {
                AssertDocumented(key);
            }

            Assert.That(((Dictionary<string, object>)history[0]).ContainsKey("mid_price"), Is.True,
                "a feature value must reach the history entry under its own name");
        }

        /// <summary>
        /// The job schema is the other half of the contract: an option the engine honours but the
        /// schema omits cannot be discovered.
        /// </summary>
        [Test]
        public void ScriptContractOptions_AreInTheJobSchema()
        {
            var schema = File.ReadAllText(Path.Combine(
                TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "docs", "research-job.md"));

            foreach (var key in new[] { "rawFields", "scriptHistoryPeriods", "scriptExposeEvents" })
            {
                Assert.That(schema, Does.Contain($"\"{key}\""),
                    $"job key '{key}' is honoured by the engine but absent from the docs/research-job.md schema");
            }
        }
    }
}
