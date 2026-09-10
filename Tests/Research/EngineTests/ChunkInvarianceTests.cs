using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Verifies that chunked processing with state carry-forward produces identical
    /// market state at observation boundaries as unchunked processing.
    ///
    /// Chunking splits events at observation boundaries. Each chunk is replayed
    /// independently with the previous chunk's final state as initial state.
    /// The engine's StartTime for each chunk is set to the chunk's boundary timestamp,
    /// so observations are emitted at exactly the same timestamps as the full run.
    ///
    /// What must match: state-derived values (lastPrice, bidPrice, askPrice, volume)
    /// at each observation timestamp. The final state after all chunks must also match.
    /// </summary>
    [TestFixture]
    public class ChunkInvarianceTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");
        private static readonly DateTime _end = DateTime.Parse("2022-12-13T00:50:00");

        private static List<MarketEvent> GenerateSyntheticEvents(int eventCount)
        {
            var events = new List<MarketEvent>();
            var ts = _start;
            var basePrice = 17000m;

            for (var i = 0; i < eventCount; i++)
            {
                if (i % 2 == 0)
                {
                    events.Add(new TradeEvent
                    {
                        Timestamp = ts,
                        Symbol = _sBybit,
                        AssetClass = SecurityType.Crypto,
                        Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _sBybit, AssetClass = SecurityType.Crypto, FeedType = "trade" },
                        SequenceNumber = i,
                        Price = basePrice + (i % 100) * 0.1m,
                        Quantity = 1 + (i % 10)
                    });
                }
                else
                {
                    events.Add(new QuoteEvent
                    {
                        Timestamp = ts,
                        Symbol = _sBybit,
                        AssetClass = SecurityType.Crypto,
                        Provenance = new DataProvenance { Venue = Market.Bybit, Symbol = _sBybit, AssetClass = SecurityType.Crypto, FeedType = "quote" },
                        SequenceNumber = i,
                        BidPrice = basePrice + (i % 100) * 0.1m - 0.5m,
                        AskPrice = basePrice + (i % 100) * 0.1m + 0.5m
                    });
                }
                ts = ts.AddSeconds(10);
            }
            return events;
        }

        private static (Dictionary<DateTime, string> StateByTs, MarketState FinalState) RunReplay(
            ReplayConfiguration config, IEnumerable<MarketEvent> events)
        {
            var engine = new EventReplayEngine(config,
                MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            var stateByTs = new Dictionary<DateTime, string>();

            foreach (var result in engine.Replay(events))
            {
                var s = result.State as MarketState;
                stateByTs[result.Timestamp] = string.Join("|",
                    s?.LastPrice.ToString("F4"), s?.BidPrice.ToString("F4"),
                    s?.AskPrice.ToString("F4"), s?.Volume.ToString("F4"));
            }
            return (stateByTs, engine.FinalState);
        }

        /// <summary>
        /// Splits events at observation boundaries. Each chunk's ChunkStart is the
        /// observation boundary it covers. Boundary events go to the NEXT chunk
        /// (engine semantics: event at T is part of observation at T).
        /// </summary>
        private static List<(List<MarketEvent> Events, DateTime Boundary)> SplitAtBoundaries(
            List<MarketEvent> events, DateTime start, TimeSpan interval)
        {
            var result = new List<(List<MarketEvent>, DateTime)>();
            var next = start + interval;
            var chunk = new List<MarketEvent>();
            var chunkStart = start;

            foreach (var evt in events)
            {
                while (evt.Timestamp >= next)
                {
                    if (chunk.Count > 0) { result.Add((chunk, chunkStart)); chunk = new(); chunkStart = next; }
                    next += interval;
                }
                chunk.Add(evt);
            }
            if (chunk.Count > 0) result.Add((chunk, chunkStart));
            return result;
        }

        private static void AssertStateMatch(
            Dictionary<DateTime, string> full, Dictionary<DateTime, string> chunked, string label)
        {
            foreach (var kv in full)
            {
                Assert.IsTrue(chunked.ContainsKey(kv.Key),
                    $"{label}: observation at {kv.Key:O} missing");
                Assert.AreEqual(kv.Value, chunked[kv.Key],
                    $"{label} at {kv.Key:O}: full={kv.Value} chunked={chunked[kv.Key]}");
            }
        }

        [Test]
        public void ChunkInvariance_FullSort_100()
        {
            var events = GenerateSyntheticEvents(100);
            var interval = TimeSpan.FromMinutes(1);
            var cfg = new ReplayConfiguration
            {
                StartTime = _start, EndTime = _end,
                Symbols = new() { _sBybit }, ObservationInterval = interval,
                Reorder = ReorderMode.FullSort
            };

            var (full, fullFinal) = RunReplay(cfg, events);
            var chunks = SplitAtBoundaries(events, _start, interval);
            var chunked = new Dictionary<DateTime, string>();
            MarketState carry = null;

            foreach (var (evts, boundary) in chunks)
            {
                var (s, f) = RunReplay(new ReplayConfiguration
                {
                    StartTime = boundary, EndTime = _end,
                    Symbols = new() { _sBybit }, ObservationInterval = interval,
                    Reorder = ReorderMode.FullSort, InitialState = carry
                }, evts);
                foreach (var kv in s) chunked[kv.Key] = kv.Value;
                carry = f;
            }

            AssertStateMatch(full, chunked, "FullSort");
            Assert.AreEqual(fullFinal?.LastPrice, carry?.LastPrice, "Final LastPrice");
            Assert.AreEqual(fullFinal?.BidPrice, carry?.BidPrice, "Final BidPrice");
            Assert.AreEqual(fullFinal?.AskPrice, carry?.AskPrice, "Final AskPrice");
        }

        [Test]
        public void ChunkInvariance_InOrderStreaming_100()
        {
            var events = GenerateSyntheticEvents(100);
            var interval = TimeSpan.FromMinutes(1);
            var cfg = new ReplayConfiguration
            {
                StartTime = _start, EndTime = _end,
                Symbols = new() { _sBybit }, ObservationInterval = interval,
                Reorder = ReorderMode.InOrderStreaming
            };

            var (full, fullFinal) = RunReplay(cfg, events);
            var chunks = SplitAtBoundaries(events, _start, interval);
            var chunked = new Dictionary<DateTime, string>();
            MarketState carry = null;

            foreach (var (evts, boundary) in chunks)
            {
                var (s, f) = RunReplay(new ReplayConfiguration
                {
                    StartTime = boundary, EndTime = _end,
                    Symbols = new() { _sBybit }, ObservationInterval = interval,
                    Reorder = ReorderMode.InOrderStreaming, InitialState = carry
                }, evts);
                foreach (var kv in s) chunked[kv.Key] = kv.Value;
                carry = f;
            }

            AssertStateMatch(full, chunked, "InOrderStreaming");
            Assert.AreEqual(fullFinal?.LastPrice, carry?.LastPrice, "Final LastPrice");
            Assert.AreEqual(fullFinal?.BidPrice, carry?.BidPrice, "Final BidPrice");
            Assert.AreEqual(fullFinal?.AskPrice, carry?.AskPrice, "Final AskPrice");
        }

        [Test]
        public void ChunkInvariance_DifferentChunkCounts_500()
        {
            var events = GenerateSyntheticEvents(500);
            var interval = TimeSpan.FromMinutes(1);
            var end = _start + TimeSpan.FromSeconds(500 * 10 + 60);

            var cfg = new ReplayConfiguration
            {
                StartTime = _start, EndTime = end,
                Symbols = new() { _sBybit }, ObservationInterval = interval,
                Reorder = ReorderMode.InOrderStreaming
            };
            var (full, _) = RunReplay(cfg, events);

            var boundaries = SplitAtBoundaries(events, _start, interval);

            void RunChunked(int boundariesPerChunk)
            {
                var chunked = new Dictionary<DateTime, string>();
                MarketState carry = null;
                var i = 0;
                while (i < boundaries.Count)
                {
                    var take = Math.Min(boundariesPerChunk, boundaries.Count - i);
                    var chunkEvents = boundaries.Skip(i).Take(take).SelectMany(b => b.Item1).ToList();
                    var chunkStart = boundaries[i].Item2;
                    var (s, f) = RunReplay(new ReplayConfiguration
                    {
                        StartTime = chunkStart, EndTime = end,
                        Symbols = new() { _sBybit }, ObservationInterval = interval,
                        Reorder = ReorderMode.InOrderStreaming, InitialState = carry
                    }, chunkEvents);
                    foreach (var kv in s) chunked[kv.Key] = kv.Value;
                    carry = f;
                    i += take;
                }
                AssertStateMatch(full, chunked, $"{boundariesPerChunk}-boundaries/chunk");
            }

            RunChunked(3);
            RunChunked(5);
            RunChunked(10);
        }

        [Test]
        public void ChunkInvariance_FinalStateCarriesAcross300()
        {
            var events = GenerateSyntheticEvents(300);
            var interval = TimeSpan.FromMinutes(1);

            var (_, fullFinal) = RunReplay(new ReplayConfiguration
            {
                StartTime = _start, EndTime = _end,
                Symbols = new() { _sBybit }, ObservationInterval = interval,
                Reorder = ReorderMode.InOrderStreaming
            }, events);

            MarketState carry = null;
            var offset = 0;
            while (offset < events.Count)
            {
                var take = Math.Min(50, events.Count - offset);
                var chunk = events.Skip(offset).Take(take).ToList();
                var (_, f) = RunReplay(new ReplayConfiguration
                {
                    StartTime = _start, EndTime = _end,
                    Symbols = new() { _sBybit }, ObservationInterval = interval,
                    Reorder = ReorderMode.InOrderStreaming, InitialState = carry
                }, chunk);
                carry = f;
                offset += take;
            }

            Assert.AreEqual(fullFinal?.LastPrice, carry?.LastPrice, "Final LastPrice");
            Assert.AreEqual(fullFinal?.BidPrice, carry?.BidPrice, "Final BidPrice");
            Assert.AreEqual(fullFinal?.AskPrice, carry?.AskPrice, "Final AskPrice");
            Assert.AreEqual(fullFinal?.Volume, carry?.Volume, "Final Volume");
        }
    }
}