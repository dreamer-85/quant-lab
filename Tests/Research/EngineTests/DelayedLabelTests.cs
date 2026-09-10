using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Verifies the delayed-label pipeline: bounded pending state (never dataset-size), correct
    /// forward-return resolution at the declared horizon, FIFO-bounded outcome retention, and
    /// executor wiring (one resolved label per observation whose horizon has been reached).
    /// </summary>
    [TestFixture]
    public class DelayedLabelTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");

        private static Observation Obs(DateTime ts, decimal mid) => new()
        {
            Timestamp = ts,
            State = new MarketState
            {
                LastPrice = mid,
                BidPrice = mid - 0.1m,
                AskPrice = mid + 0.1m
            }
        };

        private static SyntheticStreamingEventSource MakeSource(int eventCount) =>
            new(eventCount, _start, _sBybit, eventStepNs: 10_000_000_000L, basePrice: 17000m, tradeFraction: 0.5, seed: 7);

        private sealed class RecordingExperiment : ExperimentBase
        {
            public RecordingExperiment(int maxObservations = 100_000, int maxOutcomes = 100_000)
                : base(maxObservations, maxOutcomes)
            {
            }

            public override string Name => "recording";

            public IReadOnlyList<Observation> ObservedObservations => _observations;
            public IReadOnlyList<OutcomeData> OutcomeList => _outcomes;
        }

        [TestCase("60s", 60.0)]
        [TestCase("5m", 300.0)]
        [TestCase("1h", 3600.0)]
        [TestCase("2d", 172800.0)]
        [TestCase("300", 300.0)]
        [TestCase("0.5h", 1800.0)]
        public void HorizonParser_Parses_Units(string text, double expectedSeconds)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), HorizonParser.Parse(text));
        }

        [TestCase("")]
        [TestCase("xm")]
        [TestCase("0s")]
        [TestCase("-5m")]
        [TestCase("5g")]
        public void HorizonParser_Rejects_Invalid(string text)
        {
            Assert.Throws<FormatException>(() => HorizonParser.Parse(text));
        }

        [Test]
        public void DelayedLabelResolver_Resolves_ForwardReturn_AtHorizon()
        {
            var resolver = new DelayedLabelResolver(TimeSpan.FromMinutes(5));
            var start = new DateTime(2022, 12, 13, 10, 0, 0);

            var resolved0 = resolver.OnObservation(Obs(start, 100m)).ToList();
            Assert.AreEqual(0, resolved0.Count, "no label before the horizon elapses");
            Assert.AreEqual(1, resolver.PendingCount);

            var resolved5 = resolver.OnObservation(Obs(start.AddMinutes(5), 110m)).ToList();
            Assert.AreEqual(1, resolved5.Count);
            var label = resolved5[0];
            Assert.AreEqual(start, label.ReferenceTimestamp);
            Assert.AreEqual(100m, label.ReferencePrice);
            Assert.AreEqual(110m, label.FuturePrice);
            Assert.AreEqual(TimeSpan.FromMinutes(5), label.Horizon);
            Assert.AreEqual(start.AddMinutes(5), label.ObservedAt);
            Assert.AreEqual(0.1m, label.OutcomeReturn, "forward return must be (110-100)/100");
            Assert.AreEqual(1, resolver.ResolutionCount);
        }

        [Test]
        public void DelayedLabelResolver_Pending_BoundedByHorizonWindow()
        {
            var horizon = TimeSpan.FromMinutes(5);
            var resolver = new DelayedLabelResolver(horizon);
            var start = new DateTime(2022, 12, 13, 10, 0, 0);
            const int count = 1000;
            var maxPending = 0;

            for (var i = 0; i < count; i++)
            {
                resolver.OnObservation(Obs(start.AddMinutes(i), 100m + i)).ToList();
                maxPending = Math.Max(maxPending, resolver.PendingCount);
            }

            // Pending holds only the observations inside the last horizon window (5), never dataset size.
            Assert.AreEqual(5, maxPending, "pending must stay bounded by the horizon window");
            Assert.AreEqual(count - 5, resolver.ResolutionCount, "first count-5 labels resolved");
            Assert.AreEqual(5, resolver.PendingCount, "last 5 labels still pending");

            resolver.Complete();
            Assert.AreEqual(0, resolver.PendingCount);
            Assert.AreEqual(5, resolver.UnresolvedCount);
        }

        [Test]
        public void DelayedLabelResolver_Complete_DropsTailLabels()
        {
            var resolver = new DelayedLabelResolver(TimeSpan.FromMinutes(10));
            var start = new DateTime(2022, 12, 13, 10, 0, 0);

            for (var i = 0; i < 5; i++)
            {
                resolver.OnObservation(Obs(start.AddMinutes(i), 100m + i)).ToList();
            }

            Assert.AreEqual(5, resolver.PendingCount);
            Assert.AreEqual(0, resolver.ResolutionCount);

            resolver.Complete();
            Assert.AreEqual(5, resolver.UnresolvedCount);
            Assert.AreEqual(0, resolver.PendingCount);
        }

        [Test]
        public void ExperimentBase_Outcomes_Capped_ByRetentionLimit()
        {
            var exp = new RecordingExperiment(maxOutcomes: 5);
            var start = new DateTime(2022, 12, 13, 10, 0, 0);
            for (var i = 0; i < 10; i++)
            {
                exp.OnOutcome(new OutcomeData { ReferenceTimestamp = start.AddMinutes(i) });
            }

            Assert.AreEqual(5, exp.OutcomeCount);
            Assert.AreEqual(start.AddMinutes(5), exp.OutcomeList[0].ReferenceTimestamp);
            Assert.AreEqual(start.AddMinutes(9), exp.OutcomeList[^1].ReferenceTimestamp);
        }

        [Test]
        public void Executor_WithHorizons_DeliversResolvedLabels_BoundedAndDeterministic()
        {
            var end = _start.AddSeconds(400 * 10 + 60);
            var job = new ResearchJob
            {
                JobId = "labels",
                Symbols = new() { _sBybit.Value },
                Dataset = "crypto",
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = _start,
                EndTime = end,
                Resolution = Resolution.Minute,
                EventTypes = new() { MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = TimeSpan.FromMinutes(1),
                Features = new() { "mid_price" },
                ExperimentName = "dry-run",
                OutputFormat = "csv",
                Horizons = new() { "5m" },
                Reorder = ReorderMode.InOrderStreaming
            };
            var source = MakeSource(400);
            var root1 = Path.Combine(Path.GetTempPath(), "quantlab-labels-a-" + Guid.NewGuid().ToString("N")[..6]);
            var root2 = Path.Combine(Path.GetTempPath(), "quantlab-labels-b-" + Guid.NewGuid().ToString("N")[..6]);

try
            {
                var exp1 = new RecordingExperiment();
                job.OutputLocation = root1;
                job.EnableCheckpointing = false;
                var result1 = new LocalResearchExecutor(source, new LocalFileStore(root1), _ => exp1).Execute(job);
                Assert.IsTrue(result1.Succeeded, result1.Error);

                var csv = result1.OutputFiles.First(f => f.EndsWith(".csv"));
                var rows = File.ReadAllLines(csv).Skip(1).Count();
                Assert.AreEqual(rows, exp1.ObservedObservations.Count, "one observation per written row");
                Assert.Greater(exp1.ObservedObservations.Count, 0);

                var lastTs = exp1.ObservedObservations.Max(o => o.Timestamp);
                var expectedLabels = exp1.ObservedObservations.Count(o => o.Timestamp + TimeSpan.FromMinutes(5) <= lastTs);
                Assert.AreEqual(expectedLabels, exp1.OutcomeList.Count,
                    "one resolved label per observation whose horizon has elapsed");

                foreach (var o in exp1.OutcomeList)
                {
                    Assert.AreEqual(TimeSpan.FromMinutes(5), o.Horizon);
                    Assert.Greater(o.ReferencePrice, 0);
                    Assert.Greater(o.FuturePrice, 0);
                    Assert.IsTrue(o.ObservedAt >= o.ReferenceTimestamp + o.Horizon,
                        "label realized only after its horizon");
                    Assert.AreEqual((o.FuturePrice - o.ReferencePrice) / o.ReferencePrice, o.OutcomeReturn);
                }

                var exp2 = new RecordingExperiment();
                job.OutputLocation = root2;
                var result2 = new LocalResearchExecutor(source, new LocalFileStore(root2), _ => exp2).Execute(job);
                Assert.IsTrue(result2.Succeeded, result2.Error);
                Assert.AreEqual(exp1.OutcomeList.Count, exp2.OutcomeList.Count, "deterministic label count");
                Assert.AreEqual(exp1.ObservedObservations.Count, exp2.ObservedObservations.Count, "deterministic observation count");
                for (var i = 0; i < exp1.OutcomeList.Count; i++)
                {
                    Assert.AreEqual(exp1.OutcomeList[i].ReferenceTimestamp, exp2.OutcomeList[i].ReferenceTimestamp);
                    Assert.AreEqual(exp1.OutcomeList[i].FuturePrice, exp2.OutcomeList[i].FuturePrice);
                }
            }
            finally
            {
                if (Directory.Exists(root1)) Directory.Delete(root1, true);
                if (Directory.Exists(root2)) Directory.Delete(root2, true);
            }
        }

        [Test]
        public void Executor_InvalidHorizon_FailsJob()
        {
            var job = new ResearchJob
            {
                JobId = "bad-horizon",
                Symbols = new() { _sBybit.Value },
                Dataset = "crypto",
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = _start,
                EndTime = _start.AddMinutes(10),
                Resolution = Resolution.Minute,
                ObservationInterval = TimeSpan.FromMinutes(1),
                Features = new() { "mid_price" },
                ExperimentName = "dry-run",
                OutputFormat = "csv",
                Horizons = new() { "nope" },
                Reorder = ReorderMode.InOrderStreaming
            };
            var root = Path.Combine(Path.GetTempPath(), "quantlab-labels-bad-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var result = new LocalResearchExecutor(MakeSource(100), new LocalFileStore(root), _ => new RecordingExperiment()).Execute(job);
                Assert.IsFalse(result.Succeeded, "invalid horizon must fail the job");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
