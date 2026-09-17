using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Liquidity;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class LiquidityTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static MarketState MakeState(string symbol, (decimal price, decimal qty)[] bids, (decimal price, decimal qty)[] asks, DateTime ts)
        {
            var sym = Symbol.Create(symbol, SecurityType.Crypto, Market.Bybit);
            var state = new MarketState(sym);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                sym, ts,
                bids.Select(b => new OrderBookLevel { Price = b.price, Quantity = b.qty }).ToList(),
                asks.Select(a => new OrderBookLevel { Price = a.price, Quantity = a.qty }).ToList()));
            return state;
        }

        private static TradeEvent Sell(string symbol, DateTime ts, decimal price, decimal qty)
        {
            return MarketEventNormalizer.CreateTrade(
                Symbol.Create(symbol, SecurityType.Crypto, Market.Bybit), ts, price, qty, TradeSide.Sell);
        }

        private static TradeEvent Buy(string symbol, DateTime ts, decimal price, decimal qty)
        {
            return MarketEventNormalizer.CreateTrade(
                Symbol.Create(symbol, SecurityType.Crypto, Market.Bybit), ts, price, qty, TradeSide.Buy);
        }

        private static OrderBookUpdateEvent Update(string symbol, DateTime ts, OrderBookSide side, decimal price, decimal qty, OrderBookUpdateAction action)
        {
            return MarketEventNormalizer.CreateOrderBookUpdate(
                Symbol.Create(symbol, SecurityType.Crypto, Market.Bybit), ts, side, price, qty, action);
        }

        private static Observation Obs(DateTime ts, MarketState state, List<MarketEvent> events = null)
        {
            return new Observation
            {
                Timestamp = ts,
                State = state,
                Events = events ?? new List<MarketEvent>()
            };
        }

        // ---------------------------------------------------------------------------
        // Detector
        // ---------------------------------------------------------------------------

        [Test]
        public void Detector_EmitsExecutedDepletion_AndBackfillsReplenishmentDuration()
        {
            var t0 = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
            var detector = new LiquidityDetector();

            var before = MakeState("BTCUSDT",
                new[] { (100.00m, 10m), (99.95m, 5m) },
                new[] { (100.05m, 8m), (100.10m, 4m) }, t0);
            var after = MakeState("BTCUSDT",
                new[] { (99.95m, 5m) },
                new[] { (100.05m, 8m), (100.10m, 4m) }, t0 + TimeSpan.FromMilliseconds(100));

            Assert.That(detector.Observe(Obs(t0, before)), Is.Empty);

            var depletionEvents = detector.Observe(Obs(t0 + TimeSpan.FromMilliseconds(100), after, new List<MarketEvent>
            {
                Sell("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), 100.00m, 2m),
                Update("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), OrderBookSide.Bid, 100.00m, 0m, OrderBookUpdateAction.Remove)
            }));

            Assert.That(depletionEvents.Count, Is.EqualTo(1));
            var depletion = depletionEvents[0];
            Assert.That(depletion.Type, Is.EqualTo(LiquidityTransitionType.Depletion));
            Assert.That(depletion.Side, Is.EqualTo(OrderBookSide.Bid));
            Assert.That(depletion.Executed, Is.True, "sell prints at the best bid should mark the depletion as executed");
            Assert.That(depletion.ExecutedVolume, Is.EqualTo(200m));
            Assert.That(depletion.DepthBefore, Is.EqualTo(15m));
            Assert.That(depletion.DepthAfter, Is.EqualTo(5m));
            Assert.That(depletion.AggressiveSellVolume, Is.EqualTo(200m));
            Assert.That(depletion.NetFlow, Is.EqualTo(-200m));
            Assert.That(depletion.MsSinceLastEvent, Is.EqualTo(-1));

            var restored = MakeState("BTCUSDT",
                new[] { (100.00m, 8m), (99.95m, 5m) },
                new[] { (100.05m, 8m), (100.10m, 4m) }, t0 + TimeSpan.FromMilliseconds(200));

            var replenishEvents = detector.Observe(Obs(t0 + TimeSpan.FromMilliseconds(200), restored, new List<MarketEvent>
            {
                Update("BTCUSDT", t0 + TimeSpan.FromMilliseconds(200), OrderBookSide.Bid, 100.00m, 8m, OrderBookUpdateAction.Add)
            }));

            Assert.That(replenishEvents.Count, Is.EqualTo(1));
            Assert.That(replenishEvents[0].Type, Is.EqualTo(LiquidityTransitionType.Replenishment));
            Assert.That(replenishEvents[0].Side, Is.EqualTo(OrderBookSide.Bid));
            Assert.That(replenishEvents[0].MsUntilReplenishment, Is.Null);

            // The earlier depletion row is back-filled with the replenishment duration.
            Assert.That(depletion.MsUntilReplenishment, Is.EqualTo(100));
        }

        [Test]
        public void Detector_EmitsMigration_WhenBestBidRelocatesWithinBand()
        {
            var t0 = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
            var detector = new LiquidityDetector();

            var before = MakeState("BTCUSDT",
                new[] { (100.00m, 10m), (99.96m, 3m) },
                new[] { (100.05m, 8m) }, t0);
            var after = MakeState("BTCUSDT",
                new[] { (99.95m, 10m), (99.94m, 3m) },
                new[] { (100.05m, 8m) }, t0 + TimeSpan.FromMilliseconds(100));

            detector.Observe(Obs(t0, before));

            var events = detector.Observe(Obs(t0 + TimeSpan.FromMilliseconds(100), after, new List<MarketEvent>
            {
                Update("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), OrderBookSide.Bid, 100.00m, 0m, OrderBookUpdateAction.Remove),
                Update("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), OrderBookSide.Bid, 99.95m, 10m, OrderBookUpdateAction.Add)
            }));

            var migration = events.FirstOrDefault(e => e.Type == LiquidityTransitionType.Migration && e.Side == OrderBookSide.Bid);
            Assert.That(migration, Is.Not.Null, "best bid relocation >= 5bps without depth loss should be a migration");
            Assert.That(migration.Price, Is.EqualTo(99.95m));
            Assert.That(events.Any(e => e.Type == LiquidityTransitionType.Depletion), Is.False);
        }

        [Test]
        public void Detector_EmitsWallFormation_WhenBestLevelIsManyTimesTheMedian()
        {
            var t0 = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
            var detector = new LiquidityDetector();

            var before = MakeState("BTCUSDT",
                new[] { (99.95m, 0.5m), (99.90m, 1m) },
                new[] { (100.05m, 8m) }, t0);
            var after = MakeState("BTCUSDT",
                new[] { (100.00m, 40m), (99.98m, 1m), (99.95m, 1m), (99.90m, 1m) },
                new[] { (100.05m, 8m) }, t0 + TimeSpan.FromMilliseconds(100));

            detector.Observe(Obs(t0, before));

            var events = detector.Observe(Obs(t0 + TimeSpan.FromMilliseconds(100), after, new List<MarketEvent>
            {
                Update("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), OrderBookSide.Bid, 100.00m, 40m, OrderBookUpdateAction.Add)
            }));

            var wall = events.FirstOrDefault(e => e.Type == LiquidityTransitionType.WallFormation && e.Side == OrderBookSide.Bid);
            Assert.That(wall, Is.Not.Null, "a best level of 40x the median in-band level should form a wall");
            Assert.That(wall.Price, Is.EqualTo(100.00m));
        }

        // ---------------------------------------------------------------------------
        // Experiment + factory
        // ---------------------------------------------------------------------------

        [Test]
        public void Experiment_ResolvesForwardHorizonOutcomesOnDepletionRows()
        {
            var t0 = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
            var experiment = new LiquidityTrendExperiment();
            experiment.Initialize(new ExperimentContext
            {
                Configuration = new Dictionary<string, string> { ["horizons"] = "60s" },
                StartTime = t0,
                EndTime = t0 + TimeSpan.FromMinutes(2)
            });

            var before = MakeState("BTCUSDT",
                new[] { (100.00m, 10m), (99.95m, 5m) },
                new[] { (100.05m, 8m), (100.10m, 4m) }, t0);
            experiment.OnObservation(Obs(t0, before), new FeatureResult());

            var after = MakeState("BTCUSDT",
                new[] { (99.95m, 5m) },
                new[] { (100.05m, 8m), (100.10m, 4m) }, t0 + TimeSpan.FromMilliseconds(100));
            experiment.OnObservation(Obs(t0 + TimeSpan.FromMilliseconds(100), after, new List<MarketEvent>
            {
                Sell("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), 100.00m, 3m),
                Update("BTCUSDT", t0 + TimeSpan.FromMilliseconds(100), OrderBookSide.Bid, 100.00m, 0m, OrderBookUpdateAction.Remove)
            }), new FeatureResult());

            // Steady rising mid over the next minute plus one beat, so the 60s horizon from the event
// (t0+0.1s) has an observation at-or-after its target. Bid ticks up 1 cent per second, ask fixed.
            var bidPrice = 99.95m;
            for (var s = 1; s <= 61; s++)
            {
                var ts = t0 + TimeSpan.FromSeconds(s);
                bidPrice = 99.95m + 0.01m * s;
                var state = MakeState("BTCUSDT",
                    new[] { (bidPrice, 5m) },
                    new[] { (100.05m, 8m), (100.10m, 4m) }, ts);
                experiment.OnObservation(Obs(ts, state), new FeatureResult());
            }

            var result = experiment.Finalize();
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(string.Join("|", result.Rows.Select(r => $"{r["type"]}_{r["side"]}_{((DateTime)r["timestamp"]).ToString("O")}")),
                Is.EqualTo("Depletion_Bid_" + (t0 + TimeSpan.FromMilliseconds(100)).ToString("O")), 
                "only the single planned depletion should be emitted");

            var row = result.Rows[0];
            Assert.That(row["type"], Is.EqualTo("Depletion"));
            Assert.That(row["side"], Is.EqualTo("Bid"));
            Assert.That((bool)row["resolved_60s"], Is.True,
                "horizon elapsed within the stream must resolve; row=" + string.Join(",", row.Select(kv => $"{kv.Key}={kv.Value}")));

            var midRef = 100.00m;
            var midEnd = (100.55m + 100.05m) / 2m; // bid at t+60s = 99.95 + 0.60
            var expected = (double)((midEnd - midRef) / midRef);
            Assert.That((double)row["ret_60s"], Is.EqualTo(expected).Within(1e-9));
            Assert.That((double)row["mfe_60s"], Is.GreaterThanOrEqualTo(expected));
            Assert.That((double)row["mae_60s"], Is.GreaterThanOrEqualTo(-0.001));

            Assert.That(result.Metrics["depletion_count"], Is.EqualTo(1L));
            Assert.That(result.Metrics["executed_depletion_count"], Is.EqualTo(1L));
            Assert.That(result.Metrics["event_count"], Is.EqualTo(1L));
        }

        [Test]
        public void Factory_ResolvesLiquidityTrendJob()
        {
            var job = new QuantConnect.Research.Engine.Jobs.ResearchJob
            {
                ExperimentName = "liquidity_trend",
                Horizons = new List<string> { "60s" }
            };

            var experiment = ExperimentFactory.Create(job);
            Assert.That(experiment, Is.Not.Null);
            Assert.That(experiment.Name, Is.EqualTo("liquidity_trend"));
        }

        [Test]
        public void Factory_ReturnsNull_ForDryRunAndUnknownNames()
        {
            Assert.That(ExperimentFactory.Create(new QuantConnect.Research.Engine.Jobs.ResearchJob { ExperimentName = "dry-run" }), Is.Null);
            Assert.That(ExperimentFactory.Create(new QuantConnect.Research.Engine.Jobs.ResearchJob { ExperimentName = "not-a-thing" }), Is.Null);
            Assert.That(ExperimentFactory.Create(new QuantConnect.Research.Engine.Jobs.ResearchJob()), Is.Null);
        }
    }
}