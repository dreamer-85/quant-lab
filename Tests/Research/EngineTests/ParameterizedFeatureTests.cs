using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class ParameterizedFeatureTests
    {
        [Test]
        public void Registry_ParameterizedFeature_AppliesConfiguredParams()
        {
            var registry = new FeatureRegistry();
            var stored = registry.Create("liquidity_depletion");
            var configured = registry.Create("liquidity_depletion",
                new FeatureParams(new Dictionary<string, string>
                {
                    ["lookback_periods"] = "3",
                    ["bps_band"] = "20"
                }));

            Assert.That(stored.Name, Is.EqualTo("liquidity_depletion_5obs_10bps"));
            Assert.That(configured.Name, Is.EqualTo("liquidity_depletion_3obs_20bps"));
        }

        [Test]
        public void Registry_CreateMany_UsesFeatureParametersLookup()
        {
            var registry = new FeatureRegistry();
            var parameters = FeatureParameters.ParseJobConfig(new Dictionary<string, string>
            {
                ["feature.depth_persistence.window_size"] = "50"
            });

            var features = registry.CreateMany(new[] { "depth_persistence", "mid_price" }, parameters);

            Assert.That(features[0].Name, Is.EqualTo("depth_persistence_50obs"));
            Assert.That(features[1].Name, Is.EqualTo("mid_price"));
        }

        [Test]
        public void FeatureParameters_ParseJobConfig_IgnoresNonFeatureKeys()
        {
            var parameters = FeatureParameters.ParseJobConfig(new Dictionary<string, string>
            {
                ["horizons"] = "60s",
                ["feature.liquidity_depletion.lookback_periods"] = "4",
                ["feature.liquidity_depletion.bps_band"] = "50"
            });

            Assert.That(parameters.For("liquidity_depletion").Values.Count, Is.EqualTo(2));
            Assert.That(parameters.For("liquidity_depletion").GetInt("lookback_periods", 0), Is.EqualTo(4));
            Assert.That(parameters.For("liquidity_depletion").GetDecimal("bps_band", 0m), Is.EqualTo(50m));
            Assert.That(parameters.For("mid_price").Values.Count, Is.EqualTo(0));
        }

        [Test]
        public void FlowFeatures_ComputeSignedAggressiveNotional()
        {
            var engine = FeatureEngine.FromNames(new[] { "aggressive_buy_volume", "aggressive_sell_volume", "net_flow" });
            var observation = ObservationsTestHelpers.CreateObservationWithTrades();

            var result = engine.Compute(observation);

            var buy = result.Get("aggressive_buy_volume");
            var sell = result.Get("aggressive_sell_volume");
            var net = result.Get("net_flow");

            Assert.That(buy, Is.EqualTo(1103.00m));
            Assert.That(sell, Is.EqualTo(209.00m));
            Assert.That(net, Is.EqualTo(894.00m));
        }

        [Test]
        public void RawFieldValues_SignedFlowMatchesNetFlow()
        {
            var observation = ObservationsTestHelpers.CreateObservationWithTrades();

            var rawFlow = (decimal)RawFieldValues.For(observation, "trade_flow");
            var net = new NetFlowFeature().Compute(observation, new FeatureContext());

            Assert.That(rawFlow, Is.EqualTo(net));
        }

        [Test]
        public void Executor_AppendsRawFieldColumns_ForSyntheticJob()
        {
            var outputRoot = Path.Combine(Path.GetTempPath(), "quantlab-pft-" + Guid.NewGuid().ToString("N")[..8]);
            var job = new ResearchJob
            {
                JobId = "pft01",
                Dataset = "synthetic",
                Symbols = new List<string> { "ETHUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2025, 1, 1, 0, 1, 0, DateTimeKind.Utc),
                EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = TimeSpan.FromMilliseconds(1000),
                Features = new List<string> { "net_flow", "aggressive_buy_volume", "liquidity_depletion" },
                ExperimentConfig = new Dictionary<string, string>
                {
                    ["feature.liquidity_depletion.lookback_periods"] = "3"
                },
                ExperimentName = "dry-run",
                RawFields = new List<string> { "mid_price", "trade_flow", "trade_count" },
                OutputLocation = outputRoot,
                OutputFormat = "csv",
                Reorder = ReorderMode.InOrderStreaming
            };

            try
            {
                Directory.CreateDirectory(outputRoot);
                var source = new SyntheticStreamingEventSource(
                    200,
                    job.StartTime,
                    Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                    eventStepNs: 10_000_000_000L,
                    basePrice: 17000m,
                    tradeFraction: 0.5,
                    seed: 7);
                var executor = new LocalResearchExecutor(
                    source,
                    new LocalFileStore(outputRoot));

                var result = executor.Execute(job);

                Assert.That(result.Succeeded, Is.True, result.Error);

                var outputFile = result.OutputFiles.FirstOrDefault(f => f.EndsWith(".csv"));
                Assert.That(outputFile, Is.Not.Null);

                var header = File.ReadLines(outputFile).First().Split(',');
                CollectionAssert.Contains(header, "mid_price");
                CollectionAssert.Contains(header, "trade_flow");
                CollectionAssert.Contains(header, "trade_count");
                CollectionAssert.Contains(header, "net_flow");
                CollectionAssert.Contains(header, "aggressive_buy_volume");
                CollectionAssert.Contains(header, "liquidity_depletion_3obs_10bps");

                var body = File.ReadLines(outputFile).Skip(1).Take(50).ToList();
                Assert.That(body, Is.Not.Empty);
                Assert.That(body.All(line => !string.IsNullOrWhiteSpace(line.Split(',')[0])), Is.True);
            }
            finally
            {
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }
    }
}