using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NUnit.Framework;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Guards the deploy/cloud/jobs research job files (used on the VM) against JSON drift:
    /// they must deserialize cleanly with the engine's binding options and validate.
    /// </summary>
    [TestFixture]
    public class ResearchJobFileTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "deploy", "cloud", "jobs")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate repo root (deploy/cloud/jobs)");
        }

        private static string JobPath(string name) =>
            Path.Combine(FindRepoRoot(), "deploy", "cloud", "jobs", name);

        [Test]
        public void LiveCaptureJob_DeserializesAndValidates()
        {
            var job = JsonSerializer.Deserialize<ResearchJob>(File.ReadAllText(JobPath("bybit-live-capture-btcusdt.json")), JsonOptions);

            Assert.That(job, Is.Not.Null);
            Assert.That(job.Source.Mode, Is.EqualTo("live"));
            Assert.That(job.Source.OrderBookDepth, Is.EqualTo("50"));
            Assert.That(job.Source.ArchiveFilePath, Is.EqualTo("/opt/quantlab/data/archives/liquidity/bybit-btcusdt-live.jsonl"));
            Assert.That(job.ExperimentName, Is.EqualTo("dry-run"));
            Assert.That(job.Validate(out var errors), Is.True, string.Join("; ", errors));
        }

        [Test]
        public void LiquidityTrendJob_DeserializesAndValidates()
        {
            var job = JsonSerializer.Deserialize<ResearchJob>(File.ReadAllText(JobPath("bybit-liquidity-trend-btcusdt.json")), JsonOptions);

            Assert.That(job, Is.Not.Null);
            Assert.That(job.Source.Mode, Is.EqualTo("archive"));
            Assert.That(job.Source.ArchiveFilePath, Is.EqualTo("/opt/quantlab/data/archives/liquidity/bybit-btcusdt-live.jsonl"));
            Assert.That(job.ExperimentName, Is.EqualTo("liquidity_trend"));
            CollectionAssert.AreEquivalent(new[] { "5m", "15m", "1h" }, job.Horizons);
            Assert.That(job.ExperimentConfig, Contains.Key("bps_band"));
            Assert.That(job.RawFields, Does.Contain("bid_depth").And.Contain("trade_flow"));
            Assert.That(job.Validate(out var errors), Is.True, string.Join("; ", errors));
        }

        [Test]
        public void JobFile_RawFields_ParticipateInConfigHash()
        {
            var with = new ResearchJob { RawFields = new() { "mid_price" } };
            var without = new ResearchJob { RawFields = new() };
            Assert.That(with.GetConfigurationHash(), Is.Not.EqualTo(without.GetConfigurationHash()));
        }
    }
}