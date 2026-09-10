using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Storage;
using QuantConnect.Research.Engine.Jobs;
using ResearchMarketState = global::QuantConnect.Research.Engine.MarketState.MarketState;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class EventReplayTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");
        private static readonly DateTime _end = DateTime.Parse("2022-12-13T00:05:00");

        private static TradeEvent Trade(DateTime time, decimal price, decimal quantity = 1, long seq = 0, string venue = "bybit")
        {
            return new TradeEvent
            {
                Timestamp = time,
                Symbol = _sBybit,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = venue, Symbol = _sBybit, AssetClass = SecurityType.Crypto, FeedType = "trade" },
                SequenceNumber = seq,
                Price = price,
                Quantity = quantity
            };
        }

        [Test]
        public void SameInputSameConfigProducesIdenticalOutput()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 17200, 1, 1),
                Trade(_start, 17201, 2, 2),
                Trade(_start.AddMinutes(1), 17250, 3, 3),
                Trade(_start.AddMinutes(2), 17200, 1, 4)
            };

            var run1 = RunOnce(events);
            var run2 = RunOnce(events);

            CollectionAssert.AreEqual(run1.Results.Select(ResultSignature).ToList(), run2.Results.Select(ResultSignature).ToList());
            Assert.AreEqual(run1.Stats.EventsProcessed, run2.Stats.EventsProcessed);
            Assert.AreEqual(run1.Stats.ConfigurationHash, run2.Stats.ConfigurationHash);
        }

        [Test]
        public void ConfigurationHashSensitiveToSettings()
        {
            var config = BuildConfig();
            var original = config.GetConfigurationHash();

            var config2 = BuildConfig();
            config2.EngineVersion = "2.0.0";
            Assert.AreNotEqual(original, config2.GetConfigurationHash());

            var config3 = BuildConfig();
            config3.Venues.Add("binance");
            Assert.AreNotEqual(original, config3.GetConfigurationHash());
        }

        [Test]
        public void EventsOutsideTimeRangeAreSkipped()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start.AddMinutes(-1), 17000, 1, 1),
                Trade(_start.AddMinutes(1), 17250, 1, 2),
                Trade(_end.AddMinutes(1), 17500, 1, 3)
            };

            var stats = RunOnce(events).Stats;
            Assert.AreEqual(1, stats.EventsProcessed);
        }

        [Test]
        public void MaxEventsTruncatesReplay()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 17200, 1, 1),
                Trade(_start.AddMinutes(1), 17250, 1, 2),
                Trade(_start.AddMinutes(2), 17300, 1, 3),
                Trade(_start.AddMinutes(3), 17350, 1, 4)
            };

            var config = BuildConfig();
            config.MaxEvents = 2;

            var engine = new EventReplayEngine(config, new CLOBReconstructor());
            var results = engine.Replay(events).ToList();
            Assert.AreEqual(2, engine.EventsProcessed);
            Assert.AreEqual(2, engine.GetStatistics().EventsProcessed);
            Assert.AreEqual(2, results.Count);
        }

        [Test]
        public void VenueFilterRetainsOnlyMatchingEvents()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 17200, 1, 1, "bybit"),
                Trade(_start.AddMinutes(1), 17250, 1, 2, "binance"),
                Trade(_start.AddMinutes(2), 17300, 1, 3, "bybit")
            };

            var config = BuildConfig();
            config.Venues.Add("bybit");

            var engine = new EventReplayEngine(config, new CLOBReconstructor());
            var results = engine.Replay(events).ToList();
            Assert.AreEqual(2, engine.EventsProcessed);
            Assert.IsTrue(results.SelectMany(r => r.Events).All(e => e.Provenance.Venue == "bybit"));
        }

        [Test]
        public void DefaultProvenanceVenueIsPopulationByFactories()
        {
            var provenance = new DataProvenance();
            Assert.AreEqual(string.Empty, provenance.Venue);

            var trade = Trade(_start, 17200);
            Assert.AreEqual(Market.Bybit, trade.Provenance.Venue);

            var quote = new QuoteEvent
            {
                Timestamp = _start,
                Symbol = _sBybit,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Symbol = _sBybit, AssetClass = SecurityType.Crypto, Venue = Market.Bybit },
                BidPrice = 17199m,
                BidSize = 1,
                AskPrice = 17201m,
                AskSize = 1
            };
            Assert.AreEqual(Market.Bybit, quote.Provenance.Venue);
        }

        private static (List<ReplayResult> Results, ReplayStatistics Stats) RunOnce(List<MarketEvent> events)
        {
            var engine = new EventReplayEngine(BuildConfig(), new CLOBReconstructor());
            var results = engine.Replay(events).ToList();
            return (results, engine.GetStatistics());
        }

        private static ReplayConfiguration BuildConfig()
        {
            return new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _end,
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = TimeSpan.FromMinutes(1)
            };
        }

        private static string ResultSignature(ReplayResult result)
        {
            var state = result.State as ResearchMarketState;
            return string.Join("|",
                result.Timestamp.ToString("O"),
                state?.LastPrice.ToString() ?? "-",
                state?.BidPrice.ToString() ?? "-",
                state?.AskPrice.ToString() ?? "-",
                state?.Volume.ToString() ?? "-",
                state?.TradeCount.ToString() ?? "-",
                result.Events.Count);
        }
    }

    [TestFixture]
    public class ObservationEngineTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        private static TradeEvent Trade(DateTime time, decimal price, decimal quantity, long seq)
        {
            return new TradeEvent
            {
                Timestamp = time,
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto },
                SequenceNumber = seq,
                Price = price,
                Quantity = quantity
            };
        }

        [Test]
        public void ObservationsAreIntervalAligned()
        {
            var events = new[] { Trade(DateTime.Parse("2022-01-01T00:00:20"), 100, 1, 1) };
            var dates = DateTime.Parse("2022-01-01T00:00:00");

            var engine = new ObservationEngine(TimeSpan.FromHours(1), new CLOBReconstructor());
            var observation = engine.GenerateObservations(events).Single();
            Assert.AreEqual(dates, observation.Timestamp);
        }

        [Test]
        public void ChunkBoundariesDoNotChangeBucketing()
        {
            var day = DateTime.Parse("2022-01-01T00:00:00");
            var chunk1 = new List<MarketEvent>
            {
                Trade(day.AddSeconds(20), 100, 1, 1),
                Trade(day.AddSeconds(40), 101, 2, 2)
            };
            var chunk2 = new List<MarketEvent>
            {
                Trade(day.AddHours(1).AddSeconds(10), 102, 1, 3),
                Trade(day.AddHours(1).AddSeconds(30), 103, 2, 4)
            };

            var engine = new ObservationEngine(TimeSpan.FromHours(1), new CLOBReconstructor());
            var full = engine.GenerateObservations(chunk1.Concat(chunk2)).ToList();

            var c1 = engine.GenerateObservations(chunk1).ToList();
            var c2 = engine.GenerateObservations(chunk2).ToList();

            Assert.AreEqual(2, full.Count);
            Assert.AreEqual(1, c1.Count);
            Assert.AreEqual(1, c2.Count);

            Assert.AreEqual(full[0].Timestamp, c1[0].Timestamp);
            Assert.AreEqual(full[1].Timestamp, c2[0].Timestamp);
            CollectionAssert.AreEqual(full[0].Events.Select(e => e.EventId).ToList(), c1[0].Events.Select(e => e.EventId).ToList());
            CollectionAssert.AreEqual(full[1].Events.Select(e => e.EventId).ToList(), c2[0].Events.Select(e => e.EventId).ToList());
        }

        [Test]
        public void GapBetweenEventsDoesNotCreateEmptyObservations()
        {
            var day = DateTime.Parse("2022-01-01T00:00:00");
            var events = new List<MarketEvent>
            {
                Trade(day.AddSeconds(20), 100, 1, 1),
                Trade(day.AddHours(3).AddSeconds(10), 103, 1, 2)
            };

            var engine = new ObservationEngine(TimeSpan.FromHours(1), new CLOBReconstructor());
            var observations = engine.GenerateObservations(events).ToList();
            Assert.AreEqual(2, observations.Count);
            Assert.AreEqual(day, observations[0].Timestamp);
            Assert.AreEqual(day.AddHours(3), observations[1].Timestamp);
        }

        [Test]
        public void ObservationAggregationsAreComputedFromEvents()
        {
            var day = DateTime.Parse("2022-01-01T00:00:00");
            var events = new List<MarketEvent>
            {
                Trade(day.AddSeconds(0), 100, 1, 1),
                Trade(day.AddSeconds(1), 110, 2, 2),
                Trade(day.AddSeconds(2), 105, 3, 3)
            };

            var engine = new ObservationEngine(TimeSpan.FromMinutes(1), new CLOBReconstructor());
            var observation = engine.GenerateObservations(events).Single();

            Assert.AreEqual(3, observation.TradeCount);
            Assert.AreEqual(0, observation.QuoteCount);
            Assert.AreEqual(6, observation.Volume);
            Assert.AreEqual(100, observation.OpenPrice);
            Assert.AreEqual(105, observation.ClosePrice);
            Assert.AreEqual(110, observation.HighPrice);
            Assert.AreEqual(100, observation.LowPrice);
            Assert.AreEqual((100m * 1 + 110m * 2 + 105m * 3) / 6m, observation.VWAP);
        }

        [Test]
        public void EmptyEventStreamProducesNoObservations()
        {
            var engine = new ObservationEngine(TimeSpan.FromHours(1), new CLOBReconstructor());
            Assert.AreEqual(0, engine.GenerateObservations(null).Count());
            Assert.AreEqual(0, engine.GenerateObservations(new List<MarketEvent>()).Count());
        }
    }

    [TestFixture]
    public class FeatureEngineTests
    {
        private sealed class ProbeFeature : IFeature
        {
            public string Name => "probe";
            public string Description => "Lookahead probe";
            public List<ProbeSnapshot> Snapshots { get; } = new();

            public decimal Compute(Observation observation, FeatureContext context)
            {
                Snapshots.Add(new ProbeSnapshot
                {
                    HistoryCount = context.HistoricalObservations.Count,
                    CurrentIndex = context.CurrentIndex,
                    CurrentTimestamp = context.CurrentObservation?.Timestamp,
                    LastHistoricalTimestamp = context.HistoricalObservations.Count > 0
                        ? context.HistoricalObservations.Max(o => o.Timestamp)
                        : null
                });
                return 1m;
            }

            public void Reset() { }
        }

        private sealed class ProbeSnapshot
        {
            public int HistoryCount { get; set; }
            public int CurrentIndex { get; set; }
            public DateTime? CurrentTimestamp { get; set; }
            public DateTime? LastHistoricalTimestamp { get; set; }
        }

        [Test]
        public void FeatureCannotSeeCurrentOrFutureObservations()
        {
            var day = DateTime.Parse("2022-01-01T00:00:00");
            var observations = Enumerable.Range(0, 3)
                .Select(i => new Observation { Timestamp = day.AddMinutes(i) })
                .ToList();

            var probe = new ProbeFeature();
            var engine = new FeatureEngine(new IFeature[] { probe });

            engine.Compute(observations[0]);
            Assert.AreEqual(0, probe.Snapshots[0].HistoryCount);
            Assert.AreEqual(-1, probe.Snapshots[0].CurrentIndex);
            Assert.AreEqual(day, probe.Snapshots[0].CurrentTimestamp);

            engine.Compute(observations[1]);
            Assert.AreEqual(1, probe.Snapshots[1].HistoryCount);
            Assert.AreEqual(0, probe.Snapshots[1].CurrentIndex);
            Assert.AreEqual(observations[0].Timestamp, probe.Snapshots[1].LastHistoricalTimestamp);
            Assert.AreEqual(day.AddMinutes(1), probe.Snapshots[1].CurrentTimestamp);

            engine.Compute(observations[2]);
            Assert.AreEqual(2, probe.Snapshots[2].HistoryCount);
            Assert.AreEqual(1, probe.Snapshots[2].CurrentIndex);
            Assert.AreEqual(observations[1].Timestamp, probe.Snapshots[2].LastHistoricalTimestamp);
            Assert.AreEqual(day.AddMinutes(2), probe.Snapshots[2].CurrentTimestamp);
        }

        [Test]
        public void DefaultRegistryContainsExpectedFeatures()
        {
            var registry = FeatureRegistry.Instance;
            Assert.IsTrue(registry.IsRegistered("mid_price"));
            Assert.IsTrue(registry.IsRegistered("spread_bps"));
            Assert.IsTrue(registry.IsRegistered("trade_intensity"));
            Assert.IsTrue(registry.IsRegistered("trade_volume"));
            Assert.IsTrue(registry.IsRegistered("depth"));
            Assert.IsTrue(registry.IsRegistered("imbalance"));
        }

        [Test]
        public void FeatureBatchResultsAreDeterministic()
        {
            var day = DateTime.Parse("2022-01-01T00:00:00");
            var observations = Enumerable.Range(0, 5)
                .Select(i => new Observation { Timestamp = day.AddMinutes(i) })
                .ToList();

            var first = ComputeMidPrice(observations);
            var second = ComputeMidPrice(observations);
            CollectionAssert.AreEqual(
                first.Select(r => r.Get("mid_price")).ToList(),
                second.Select(r => r.Get("mid_price")).ToList());
        }

        private static List<FeatureResult> ComputeMidPrice(List<Observation> observations)
        {
            var engine = FeatureEngine.FromNames(new[] { "mid_price", "spread_bps" });
            return engine.ComputeBatch(observations).ToList();
        }
    }

    [TestFixture]
    public class BookReconstructionTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _t = DateTime.Parse("2022-01-01T00:00:00");

        private static OrderBookUpdateEvent Update(OrderBookSide side, decimal price, decimal quantity, int seq)
        {
            return new OrderBookUpdateEvent
            {
                Timestamp = _t.AddMilliseconds(seq),
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto },
                SequenceNumber = seq,
                Side = side,
                Price = price,
                Quantity = quantity,
                Action = OrderBookUpdateAction.Add
            };
        }

        private static ResearchMarketState StateWith(params MarketEvent[] events)
        {
            var state = new ResearchMarketState(_symbol);
            foreach (var evt in events)
            {
                state.UpdateFromEvent(evt);
            }
            return state;
        }

        [Test]
        public void OrderBookLevelsAreAddedModifiedAndRemoved()
        {
            var state = StateWith(
                Update(OrderBookSide.Bid, 100m, 2, 1),
                Update(OrderBookSide.Bid, 99m, 1, 2),
                Update(OrderBookSide.Ask, 101m, 1.5m, 3));

            Assert.AreEqual(2, state.BidLevels);
            Assert.AreEqual(1, state.AskLevels);
            Assert.AreEqual(100m, state.BidPrice);
            Assert.AreEqual(101m, state.AskPrice);
            Assert.AreEqual(100.5m, state.MidPrice);
            Assert.AreEqual(1m, state.Spread);
            Assert.AreEqual(3m, state.BidDepth);
            Assert.AreEqual(1.5m, state.AskDepth);

            var modify = Update(OrderBookSide.Bid, 100m, 5, 4);
            modify.Action = OrderBookUpdateAction.Modify;
            state.UpdateFromEvent(modify);
            Assert.AreEqual(6m, state.BidDepth);

            var remove = Update(OrderBookSide.Bid, 100m, 0, 5);
            remove.Action = OrderBookUpdateAction.Remove;
            state.UpdateFromEvent(remove);
            Assert.AreEqual(1m, state.BidDepth);
            Assert.AreEqual(99m, state.BidPrice);
            Assert.AreEqual(1, state.BidLevels);
        }

        [Test]
        public void BookLevelsAreSortedByPrice()
        {
            var state = StateWith(
                Update(OrderBookSide.Bid, 99m, 1, 1),
                Update(OrderBookSide.Bid, 100m, 2, 2),
                Update(OrderBookSide.Ask, 102m, 1, 3),
                Update(OrderBookSide.Ask, 101m, 2, 4));

            CollectionAssert.AreEqual(new[] { 100m, 99m }, state.BidLevels_List.Select(l => l.Price).ToList());
            CollectionAssert.AreEqual(new[] { 101m, 102m }, state.AskLevels_List.Select(l => l.Price).ToList());
        }

        [Test]
        public void SnapshotReplacesFullBook()
        {
            var snapshot = new OrderBookSnapshotEvent
            {
                Timestamp = _t.AddSeconds(1),
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto },
                Bids = new List<OrderBookLevel>
                {
                    new() { Price = 100m, Quantity = 10m },
                    new() { Price = 99m, Quantity = 20m }
                },
                Asks = new List<OrderBookLevel>
                {
                    new() { Price = 101m, Quantity = 12m },
                    new() { Price = 102m, Quantity = 25m }
                }
            };

            var state = StateWith(
                Update(OrderBookSide.Bid, 50m, 5, 1),
                snapshot);

            Assert.AreEqual(2, state.BidLevels);
            Assert.AreEqual(2, state.AskLevels);
            Assert.AreEqual(100m, state.BidPrice);
            Assert.AreEqual(101m, state.AskPrice);
            Assert.AreEqual(30m, state.BidDepth);
            Assert.AreEqual(37m, state.AskDepth);
        }

        [Test]
        public void DepthWindowsAreComputedRelativeToMid()
        {
            var state = StateWith(
                Update(OrderBookSide.Bid, 10.0m, 100, 1),
                Update(OrderBookSide.Bid, 9.9m, 99, 2),
                Update(OrderBookSide.Ask, 10.1m, 101, 3),
                Update(OrderBookSide.Ask, 10.2m, 102, 4));

            Assert.AreEqual(100m, state.GetBidDepthWithinBps(100));
            Assert.AreEqual(101m, state.GetAskDepthWithinBps(100));

            Assert.AreEqual(199m, state.GetBidDepthWithinBps(200));
            Assert.AreEqual(203m, state.GetAskDepthWithinBps(200));
        }

        [Test]
        public void QuoteUpdatesPreserveUnchangedSide()
        {
            var state = StateWith(new QuoteEvent
            {
                Timestamp = _t,
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto },
                BidPrice = 100m,
                BidSize = 5,
                AskPrice = 101m,
                AskSize = 7
            });

            state.UpdateFromEvent(new QuoteEvent
            {
                Timestamp = _t.AddSeconds(1),
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto },
                BidPrice = 0m,
                BidSize = 0,
                AskPrice = 101.5m,
                AskSize = 8
            });

            Assert.AreEqual(100m, state.BidPrice);
            Assert.AreEqual(5m, state.BidSize);
            Assert.AreEqual(101.5m, state.AskPrice);
            Assert.AreEqual(8m, state.AskSize);
        }

        [Test]
        public void BarUpdateAdvancesLastPriceAndVolume()
        {
            var state = StateWith(new BarEvent
            {
                Timestamp = _t,
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto },
                Open = 100m,
                High = 105m,
                Low = 99m,
                Close = 103m,
                Volume = 42m,
                Period = TimeSpan.FromMinutes(1)
            });

            Assert.AreEqual(103m, state.LastPrice);
            Assert.AreEqual(42m, state.Volume);
        }
    }

    [TestFixture]
    public class MarketEventFactoryTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _t = DateTime.Parse("2022-12-13T00:00:00");

        [Test]
        public void TradeBarFactoryPopulatesDefaultProvenanceVenueFromSymbol()
        {
            var bar = new Data.Market.TradeBar
            {
                Time = _t,
                Symbol = _symbol,
                Open = 17200m,
                High = 17250m,
                Low = 17150m,
                Close = 17220m,
                Volume = 100m,
                Period = TimeSpan.FromMinutes(1)
            };

            var evt = BarEvent.FromTradeBar(bar);
            Assert.AreEqual(Market.Bybit, evt.Provenance.Venue);
            Assert.AreEqual("bar", evt.Provenance.FeedType);
            Assert.AreEqual(TimestampPrecision.Milliseconds, evt.Provenance.TimestampPrecision);
            Assert.AreEqual(17220m, evt.Close);
            Assert.AreEqual(100m, evt.Volume);
        }

        [Test]
        public void TradeTickFactoryPopulatesDefaultVenueAndFields()
        {
            var tick = new Data.Market.Tick
            {
                Time = _t,
                Symbol = _symbol,
                Value = 17220m,
                Quantity = 1.5m,
                TickType = TickType.Trade,
                Exchange = Market.Bybit
            };

            var evt = TradeEvent.FromTick(tick);
            Assert.AreEqual(Market.Bybit, evt.Provenance.Venue);
            Assert.AreEqual("trade", evt.Provenance.FeedType);
            Assert.AreEqual(17220m, evt.Price);
            Assert.AreEqual(1.5m, evt.Quantity);
        }

        [Test]
        public void QuoteTickFactoryPopulatesDefaultVenueAndPrices()
        {
            var tick = new Data.Market.Tick
            {
                Time = _t,
                Symbol = _symbol,
                BidPrice = 17219m,
                BidSize = 2,
                AskPrice = 17221m,
                AskSize = 3,
                TickType = TickType.Quote,
                Exchange = Market.Bybit
            };

            var evt = QuoteEvent.FromTick(tick);
            Assert.AreEqual(Market.Bybit, evt.Provenance.Venue);
            Assert.AreEqual("quote", evt.Provenance.FeedType);
            Assert.AreEqual(17219m, evt.BidPrice);
            Assert.AreEqual(17221m, evt.AskPrice);
            Assert.AreEqual(17220m, evt.MidPrice);
        }

        [Test]
        public void CloneRoundTripPreservesAllFields()
        {
            var trade = new TradeEvent
            {
                Timestamp = _t,
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _symbol, AssetClass = SecurityType.Crypto, FeedType = "trade" },
                SequenceNumber = 42,
                Price = 17220m,
                Quantity = 3m,
                Side = TradeSide.Buy,
                SaleCondition = "regular",
                Suspicious = true,
                EventId = "abc"
            };

            var clone = (TradeEvent)trade.Clone();
            Assert.AreEqual(trade.Timestamp, clone.Timestamp);
            Assert.AreEqual(trade.Symbol, clone.Symbol);
            Assert.AreEqual(trade.Price, clone.Price);
            Assert.AreEqual(trade.Quantity, clone.Quantity);
            Assert.AreEqual(trade.Side, clone.Side);
            Assert.AreEqual(trade.SaleCondition, clone.SaleCondition);
            Assert.AreEqual(trade.Suspicious, clone.Suspicious);
            Assert.AreEqual(trade.SequenceNumber, clone.SequenceNumber);
            Assert.AreEqual(trade.EventId, clone.EventId);
            Assert.AreEqual(trade.Provenance.Venue, clone.Provenance.Venue);
        }

        [Test]
        public void OrderingUsesTimestampThenSequenceNumber()
        {
            var earlier = new TradeEvent { Timestamp = _t, SequenceNumber = 1 };
            var sameTimeHighSeq = new TradeEvent { Timestamp = _t, SequenceNumber = 2 };
            var later = new TradeEvent { Timestamp = _t.AddSeconds(1), SequenceNumber = 1 };

            Assert.IsTrue(MarketEvent.CompareEvents(earlier, sameTimeHighSeq) < 0);
            Assert.IsTrue(MarketEvent.CompareEvents(sameTimeHighSeq, later) < 0);
        }
    }

    [TestFixture]
    public class CheckpointManagerTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "QuantLabTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static ResearchJob BuildJob()
        {
            return new ResearchJob
            {
                JobId = "checkpoint-test",
                Dataset = "btcusdt",
                Symbols = new List<string> { "BTCUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = DateTime.Parse("2022-12-13T00:00:00"),
                EndTime = DateTime.Parse("2022-12-13T23:59:59"),
                EventTypes = new List<MarketEventType> { MarketEventType.Trade },
                ObservationInterval = TimeSpan.FromMinutes(1),
                Features = new List<string> { "mid_price" },
                ExperimentName = "checkpoint-test-experiment",
                EnableCheckpointing = true
            };
        }

        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        [Test]
        public void SaveThenLoadCompleted()
        {
            var manager = new ReplayCheckpointManager(_directory);
            var job = BuildJob();

            manager.Save(new ReplayCheckpoint
            {
                JobId = job.JobId,
                Symbol = _symbol.Value,
                ConfigurationHash = job.GetConfigurationHash(),
                LastObservationTimestamp = DateTime.Parse("2022-12-13T12:00:00"),
                ObservationsWritten = 720,
                EventsProcessed = 1440,
                Completed = true,
                CompletedAtUtc = DateTime.UtcNow
            });

            Assert.IsTrue(File.Exists(manager.CheckpointPath(_symbol)));

            var checkpoint = manager.TryLoadCompleted(job, _symbol);
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(720, checkpoint.ObservationsWritten);
            Assert.AreEqual(1440, checkpoint.EventsProcessed);
            Assert.IsTrue(checkpoint.Completed);
        }

        [Test]
        public void LoadRejectsCheckpointFromDifferentConfiguration()
        {
            var manager = new ReplayCheckpointManager(_directory);
            var job = BuildJob();

            manager.Save(new ReplayCheckpoint
            {
                JobId = job.JobId,
                Symbol = _symbol.Value,
                ConfigurationHash = "different-hash",
                ObservationsWritten = 720,
                Completed = true
            });

            Assert.IsNull(manager.TryLoadCompleted(job, _symbol));
        }

        [Test]
        public void LoadRejectsIncompleteCheckpoint()
        {
            var manager = new ReplayCheckpointManager(_directory);
            var job = BuildJob();

            manager.Save(new ReplayCheckpoint
            {
                JobId = job.JobId,
                Symbol = _symbol.Value,
                ConfigurationHash = job.GetConfigurationHash(),
                ObservationsWritten = 300,
                Completed = false
            });

            Assert.IsNull(manager.TryLoadCompleted(job, _symbol));

            var raw = manager.TryLoad(job, _symbol);
            Assert.IsNotNull(raw);
            Assert.IsFalse(raw.Completed);
        }

        [Test]
        public void LoadReturnsNullForMissingCheckpoint()
        {
            var manager = new ReplayCheckpointManager(_directory);
            Assert.IsNull(manager.TryLoad(BuildJob(), _symbol));
        }
    }
}