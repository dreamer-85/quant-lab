using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Ingest.Bybit;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// End-to-end: a fabricated Bybit WS capture archive -> BybitArchiveSource -> replay ->
    /// observations -> LiquidityDetector -> LiquidityTrendExperiment -> persisted event rows with
    /// forward-resolved horizons.
    /// </summary>
    [TestFixture]
    public class LiquidityArchiveE2ETest
    {
        private const long BaseMs = 1767225600000L; // 2026-01-01T00:00:00Z
        private static readonly Symbol Symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        private static string Frame(string topic, long tsMs, string type, List<(decimal, decimal)> bids, List<(decimal, decimal)> asks)
        {
            static object[] Levels(List<(decimal, decimal)> levels) =>
                levels.Select(l => new object[] { l.Item1.ToString("0.00"), l.Item2.ToString("0.00") }).ToArray();

            var data = new Dictionary<string, object>
            {
                ["s"] = "BTCUSDT",
                ["b"] = Levels(bids),
                ["a"] = Levels(asks)
            };
            var frame = new Dictionary<string, object>
            {
                ["topic"] = topic,
                ["type"] = type,
                ["ts"] = tsMs,
                ["data"] = data
            };
            return JsonSerializer.Serialize(frame);
        }

        private static string TradeFrame(long tsMs)
        {
            var frame = new Dictionary<string, object>
            {
                ["topic"] = "publicTrade.BTCUSDT",
                ["ts"] = tsMs,
                ["data"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["execId"] = "e1",
                        ["symbol"] = "BTCUSDT",
                        ["price"] = "99.95",
                        ["size"] = "2",
                        ["side"] = "Sell",
                        ["time"] = tsMs.ToString()
                    }
                }
            };
            return JsonSerializer.Serialize(frame);
        }

        private static string Envelope(long tsMs, string frameJson) =>
            JsonSerializer.Serialize(new Dictionary<string, object> { ["a"] = tsMs, ["f"] = frameJson });

        private static void WriteArchive(string path)
        {
            var bids10 = new[] { (99.95m, 10m), (99.94m, 8m), (99.90m, 5m) }.ToList();
            var asks12 = new[] { (100.05m, 12m), (100.08m, 4m), (100.12m, 2m) }.ToList();
            // t=8s asks keep the same total in-band depth (16) but relocate the best ask by >5bps:
            // 100.05 -> 99.99 (6bps) so Migration fires while in-band depth is unchanged.
            var asksB = new[] { (99.99m, 12m), (100.06m, 4m), (100.10m, 2m) }.ToList();
            // t=12s bid wall: best level 99.93 @ 60 vs an in-band size median of 3.5 (60 >= 5 * 3.5).
            var bidsWall = new[] { (99.93m, 60m), (99.92m, 3m), (99.91m, 4m), (99.90m, 2m) }.ToList();

            using var writer = new StreamWriter(path);
            // t=0 baseline.
            writer.WriteLine(Envelope(Tick(0L), Frame(BookTopic(), Tick(0L), "snapshot", bids10, asks12)));
            // t=1s bid depletion: 99.95 removed.
            writer.WriteLine(Envelope(Tick(1000L), Frame(BookTopic(), Tick(1000L), "snapshot", new[] { (99.94m, 8m), (99.90m, 5m) }.ToList(), asks12)));
            // t=1.5s executed sell at the former best bid.
            writer.WriteLine(Envelope(Tick(1500L), TradeFrame(Tick(1500L))));
            // t=2..4s steady state after depletion.
            for (var t = 2000L; t <= 4000L; t += 1000L)
                writer.WriteLine(Envelope(Tick(t), Frame(BookTopic(), Tick(t), "snapshot", new[] { (99.94m, 8m), (99.90m, 5m) }.ToList(), asks12)));
            // t=5s replenishment: 99.95 restored.
            writer.WriteLine(Envelope(Tick(5000L), Frame(BookTopic(), Tick(5000L), "snapshot", bids10, asks12)));
            // t=6..7s steady.
            for (var t = 6000L; t <= 7000L; t += 1000L)
                writer.WriteLine(Envelope(Tick(t), Frame(BookTopic(), Tick(t), "snapshot", bids10, asks12)));
            // t=8s ask migration: best ask 100.05 -> 99.99 (>5bps), in-band depth unchanged.
            writer.WriteLine(Envelope(Tick(8000L), Frame(BookTopic(), Tick(8000L), "snapshot", bids10, asksB)));
            // t=9..11s steady.
            for (var t = 9000L; t <= 11000L; t += 1000L)
                writer.WriteLine(Envelope(Tick(t), Frame(BookTopic(), Tick(t), "snapshot", bids10, asksB)));
            // t=12s bid wall: 99.93 @ 60 vs small in-band sizes (median 3.5).
            writer.WriteLine(Envelope(Tick(12000L), Frame(BookTopic(), Tick(12000L), "snapshot", bidsWall, asksB)));
        }

        private static long Tick(long offsetMs) => BaseMs + offsetMs;

        private static string BookTopic() => $"orderbook.50.{Symbol.Value}";

        [Test]
        public void ArchiveReplay_DetectsTransitions_ResolvesForwardHorizons()
        {
            var root = Path.Combine(Path.GetTempPath(), "quantlab-e2e-" + Guid.NewGuid().ToString("N")[..8]);
            var archive = Path.Combine(root, "capture.jsonl");
            Directory.CreateDirectory(root);
            WriteArchive(archive);

            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var job = new ResearchJob
            {
                JobId = "e2e-liquidity",
                Dataset = "bybit",
                Symbols = new List<string> { Symbol.Value },
                AssetClass = "crypto",
                Venue = "bybit",
                Resolution = Resolution.Second,
                StartTime = start,
                EndTime = start.AddSeconds(13),
                EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookSnapshot },
                ObservationInterval = TimeSpan.FromSeconds(1),
                Features = new List<string> { "mid_price" },
                ExperimentName = "liquidity_trend",
                ExperimentConfig = new Dictionary<string, string>
                {
                    ["bps_band"] = "10",
                    ["depletion_fraction"] = "0.25",
                    ["depletion_min_size"] = "0.01",
                    ["replenishment_fraction"] = "0.25",
                    ["migration_bps"] = "5",
                    ["wall_multiple"] = "5"
                },
                Horizons = new List<string> { "3s" },
                OutputLocation = root,
                OutputFormat = "csv",
                EnableCheckpointing = false,
                Reorder = ReorderMode.InOrderStreaming,
                Source = new JobDataSource
                {
                    Mode = "archive",
                    Provider = "bybit",
                    Category = "spot",
                    OrderBookDepth = "50",
                    ArchiveFilePath = archive
                }
            };

            try
            {
                var executor = new LocalResearchExecutor(
                    new BybitArchiveSource(job),
                    new LocalFileStore(root),
                    ExperimentFactory.Create);

                var result = executor.Execute(job);

                Assert.That(result.Succeeded, Is.True, result.Error);
                Assert.That(result.ExperimentResult, Is.Not.Null);
                Assert.GreaterOrEqual(Convert.ToDouble(result.ExperimentResult.Metrics["event_count"]), 4);

                var eventsFile = Directory.EnumerateFiles(
                    Path.Combine(root, job.JobId, "experiment"), "liquidity_trend.csv").FirstOrDefault();
                Assert.That(eventsFile, Is.Not.Null, "experiment event rows must be persisted");

                var lines = File.ReadAllLines(eventsFile);
                var header = lines[0].Split(',');
                CollectionAssert.Contains(header, "type");
                CollectionAssert.Contains(header, "ret_3s");
                CollectionAssert.Contains(header, "mfe_3s");
                CollectionAssert.Contains(header, "mae_3s");
                CollectionAssert.Contains(header, "resolved_3s");

                var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
                Assert.That(rows.Count, Is.GreaterThanOrEqualTo(4));

                var types = rows.Select(r => r[Array.IndexOf(header, "type")]).ToList();
                Assert.That(types, Does.Contain("Depletion"));
                Assert.That(types, Does.Contain("Replenishment"));
                Assert.That(types, Does.Contain("Migration"));
                Assert.That(types, Does.Contain("WallFormation"));

                var resolvedCol = Array.IndexOf(header, "resolved_3s");
                var resolved = rows.Select(r => r[resolvedCol]).ToList();
                Assert.That(resolved, Does.Contain("True"), "early events must resolve before the capture ends");
                Assert.That(resolved, Does.Contain("False"), "tail events must be finalized as unresolved");

                CollectionAssert.Contains(header, "net_flow");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}