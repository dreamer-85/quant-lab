using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class StreamingReplayTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");
        private static readonly DateTime _end = DateTime.Parse("2022-12-13T00:05:00");

        private static TradeEvent Trade(DateTime time, decimal price, long seq = 0, decimal quantity = 1)
        {
            return new TradeEvent
            {
                Timestamp = time,
                Symbol = _sBybit,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _sBybit, AssetClass = SecurityType.Crypto, FeedType = "trade" },
                SequenceNumber = seq,
                Price = price,
                Quantity = quantity
            };
        }

        private static QuoteEvent Quote(DateTime time, decimal bid, decimal ask)
        {
            return new QuoteEvent
            {
                Timestamp = time,
                Symbol = _sBybit,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _sBybit, AssetClass = SecurityType.Crypto, FeedType = "quote" },
                BidPrice = bid,
                AskPrice = ask
            };
        }

        private static ReplayConfiguration BuildConfig(ReorderMode reorder)
        {
            return new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _end,
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = TimeSpan.FromMinutes(1),
                Reorder = reorder
            };
        }

        private static ReplayConfiguration BuildPerEventConfig(ReorderMode reorder)
        {
            return new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _end,
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = null,
                Reorder = reorder
            };
        }

        private static List<ReplayResult> Run(ReorderMode reorder, IEnumerable<MarketEvent> events)
        {
            var engine = new EventReplayEngine(BuildConfig(reorder), MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            return engine.Replay(events).ToList();
        }

        private static List<ReplayResult> RunPerEvent(ReorderMode reorder, IEnumerable<MarketEvent> events)
        {
            var engine = new EventReplayEngine(BuildPerEventConfig(reorder), MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            return engine.Replay(events).ToList();
        }

        private static string EventSignature(MarketEvent evt)
        {
            return string.Join(":",
                evt.Timestamp.ToString("HH:mm:ss.fff"),
                evt.EventType,
                evt.SequenceNumber?.ToString() ?? "-",
                (evt as TradeEvent)?.Price.ToString() ?? (evt as QuoteEvent)?.BidPrice.ToString() ?? "-");
        }

        private static string ResultSignature(ReplayResult result)
        {
            var state = result.State as MarketState;
            return string.Join("|",
                result.Timestamp.ToString("O"),
                state?.LastPrice.ToString() ?? "-",
                state?.BidPrice.ToString() ?? "-",
                state?.AskPrice.ToString() ?? "-",
                result.Events.Count,
                string.Join(",", result.Events.Select(EventSignature)));
        }

        [Test]
        public void StreamingReplay_MatchesFullSort_OnDistinctKeyOrderedStreams()
        {
            var tradeStream = new List<MarketEvent>
            {
                Trade(_start.AddMilliseconds(500), 17200, 1),
                Trade(_start.AddMinutes(1).AddMilliseconds(500), 17250, 2),
                Trade(_start.AddMinutes(2).AddMilliseconds(500), 17300, 3)
            };
            var quoteStream = new List<MarketEvent>
            {
                Quote(_start, 17199m, 17201m),
                Quote(_start.AddMinutes(1).AddMilliseconds(250), 17249m, 17251m),
                Quote(_start.AddMinutes(3).AddMilliseconds(100), 17299m, 17301m)
            };

            var merged = EventStreamMerger.Merge(new[] { tradeStream, quoteStream }).ToList();

            var fullSort = Run(ReorderMode.FullSort, merged);
            var streaming = Run(ReorderMode.InOrderStreaming, merged);

            CollectionAssert.AreEqual(
                fullSort.Select(ResultSignature).ToList(),
                streaming.Select(ResultSignature).ToList());
            Assert.AreEqual(fullSort.Count, streaming.Count);
            Assert.GreaterOrEqual(fullSort.Count, 3);
        }

        [Test]
        public void StreamingReplay_TiesResolvedDeterministicallyByStreamOrdinal()
        {
            var tradeStream = new List<MarketEvent>
            {
                Trade(_start, 17200, 0),
                Trade(_start.AddMinutes(1), 17250, 0)
            };
            var quoteStream = new List<MarketEvent>
            {
                Quote(_start, 17199m, 17201m)
            };

            var merged = EventStreamMerger.Merge(new[] { tradeStream, quoteStream }).ToList();
            CollectionAssert.AreEqual(
                new[] { EventSignature(tradeStream[0]), EventSignature(quoteStream[0]), EventSignature(tradeStream[1]) },
                merged.Select(EventSignature).ToList());

            var run1 = Run(ReorderMode.InOrderStreaming, merged);
            var run2 = Run(ReorderMode.InOrderStreaming, merged);

            // Same equal-key input yields byte-identical replay output across runs.
            CollectionAssert.AreEqual(run1.Select(ResultSignature).ToList(), run2.Select(ResultSignature).ToList());

            // The tied quote is processed before the next minute's trade (state reflects its bid/ask).
            Assert.AreEqual(17201m, (run1.Last().State as MarketState)?.AskPrice);
        }

        [Test]
        public void Merger_ConcatenatedChunksOfSortedStream_EqualWholeStream()
        {
            var events = new List<MarketEvent>();
            for (var i = 0; i < 100; i++)
            {
                events.Add(Trade(_start.AddSeconds(i * 2), 17000 + i, i));
            }

            var chunked = new List<IEnumerable<MarketEvent>>();
            for (var i = 0; i < events.Count; i += 10)
            {
                chunked.Add(events.Skip(i).Take(10));
            }

            var mergedEventStream = EventStreamMerger.Merge(chunked);
            CollectionAssert.AreEqual(
                events.Select(EventSignature).ToList(),
                mergedEventStream.Select(EventSignature).ToList());

            var fullSort = Run(ReorderMode.FullSort, mergedEventStream);
            var streaming = Run(ReorderMode.InOrderStreaming, mergedEventStream);
            CollectionAssert.AreEqual(fullSort.Select(ResultSignature).ToList(), streaming.Select(ResultSignature).ToList());
        }

        [Test]
        public void StreamingReplay_IsLazy_OnlyAdvancesUntilFirstObservation()
        {
            var maxProduced = -1;

            IEnumerable<MarketEvent> LazyEvents()
            {
                for (var i = 0; i < 1000; i++)
                {
                    maxProduced = i;
                    yield return Trade(_start.AddSeconds(i * 0.1d), 17000 + i, i);
                }
            }

            // First observation is emitted as soon as the first in-range event is seen.
            var streamingEngine = new EventReplayEngine(BuildConfig(ReorderMode.InOrderStreaming), MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            var first = streamingEngine.Replay(EventStreamMerger.Merge(new[] { LazyEvents() })).First();
            Assert.AreEqual(_start, first.Timestamp);
            Assert.LessOrEqual(maxProduced, 0, "Streaming replay must not advance the source beyond the first result");

            // Reset and confirm the full-sort path is eager (materializes everything before first result).
            maxProduced = -1;
            var fullSortEngine = new EventReplayEngine(BuildConfig(ReorderMode.FullSort), MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            fullSortEngine.Replay(EventStreamMerger.Merge(new[] { LazyEvents() })).First();
            Assert.AreEqual(999, maxProduced, "Full-sort replay must consume the whole source before yielding");
        }

        // ---------------------------------------------------------------------------
        // Event-driven mode (ObservationInterval == null): one observation per event
        // ---------------------------------------------------------------------------

        [Test]
        public void EventDrivenMode_EmitsOneObservationPerEvent()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start.AddSeconds(1), 17200, 1),
                Trade(_start.AddSeconds(2), 17250, 2),
                Quote(_start.AddSeconds(3), 17249m, 17251m)
            };

            var results = RunPerEvent(ReorderMode.InOrderStreaming, events).ToList();

            Assert.AreEqual(3, results.Count, "one observation per event, nothing aggregated or dropped");
            Assert.AreEqual(_start.AddSeconds(1), results[0].Timestamp);
            Assert.AreEqual(_start.AddSeconds(2), results[1].Timestamp);
            Assert.AreEqual(_start.AddSeconds(3), results[2].Timestamp);
            Assert.AreEqual(1, results[0].Events.Count);
            Assert.AreEqual(1, results[2].Events.Count);

            // The observation carries the aggregated state as-of that event (Len-style clock,
            // state accrues across events).
            Assert.AreEqual(17250m, (results[1].State as MarketState)?.LastPrice);
            Assert.AreEqual(17251m, (results[2].State as MarketState)?.AskPrice);
        }

        [Test]
        public void EventDrivenMode_FullSortMatchesStreaming()
        {
            var events = new List<MarketEvent>
            {
                Quote(_start.AddSeconds(1), 17199m, 17201m),
                Trade(_start.AddSeconds(2), 17200, 1),
                Quote(_start.AddSeconds(3), 17199m, 17201m),
                Trade(_start.AddSeconds(4), 17250, 2)
            };

            var fullSort = RunPerEvent(ReorderMode.FullSort, events);
            var streaming = RunPerEvent(ReorderMode.InOrderStreaming, events);

            CollectionAssert.AreEqual(
                fullSort.Select(ResultSignature).ToList(),
                streaming.Select(ResultSignature).ToList());
            Assert.AreEqual(fullSort.Count, 4);
        }

        [Test]
        public void EventDrivenMode_AppliesEventTypeAndTimeFilters()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start.AddSeconds(1), 17200, 1),
                Trade(_start.AddSeconds(2), 17250, 2),
                Quote(_start.AddSeconds(3), 17249m, 17251m)
            };
            var config = BuildPerEventConfig(ReorderMode.InOrderStreaming);
            config.EventTypes = new List<MarketEventType> { MarketEventType.Trade };
            config.EndTime = _start.AddSeconds(2);

            var engine = new EventReplayEngine(config, MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            var results = engine.Replay(events).ToList();

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(_start.AddSeconds(1), results[0].Timestamp);
            Assert.AreEqual(_start.AddSeconds(2), results[1].Timestamp);
        }
    }
}