using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.LocalData
{
    /// <summary>
    /// Deterministic, memory-bounded synthetic event source for benchmarks and tests.
    /// Generates ordered trade and quote streams lazily without materializing the dataset,
    /// so it exercises the exact streaming path used with real data sources.
    /// </summary>
    public class SyntheticStreamingEventSource : IEventDataSource, IStreamingEventSource
    {
        /// <summary>
        /// Number of events to generate across all streams (approximately; trades/quotes alternate).
        /// </summary>
        public long EventCount { get; }

        /// <summary>
        /// Symbol generated events are attributed to
        /// </summary>
        public QuantConnect.Symbol Symbol { get; }

        /// <summary>
        /// Start time of the synthetic dataset
        /// </summary>
        public DateTime StartTime { get; }

        /// <summary>
        /// Nanoseconds between successive events (deterministic cadence)
        /// </summary>
        public long EventStepNs { get; }

        /// <summary>
        /// Base price around which trade prices oscillate
        /// </summary>
        public decimal BasePrice { get; }

        /// <summary>
        /// Trade events share of the stream (0-1); remainder are quotes
        /// </summary>
        public double TradeFraction { get; }

        private readonly long _seed;

        /// <summary>
        /// Creates a new synthetic event source.
        /// </summary>
        /// <param name="eventCount">Total events to generate across all streams</param>
        /// <param name="startTime">Dataset start time</param>
        /// <param name="symbol">Symbol for generated events</param>
        /// <param name="eventStepNs">Cadence between events</param>
        /// <param name="basePrice">Base price</param>
        /// <param name="tradeFraction">Fraction of trade events (rest quote)</param>
        /// <param name="seed">Seed for deterministic price variation</param>
        public SyntheticStreamingEventSource(
            long eventCount,
            DateTime startTime,
            QuantConnect.Symbol symbol = null,
            long eventStepNs = 1_000_000,
            decimal basePrice = 17000m,
            double tradeFraction = 0.5,
            long seed = 42)
        {
            EventCount = eventCount;
            StartTime = startTime;
            Symbol = symbol ?? QuantConnect.Symbol.Create("SYNTH", SecurityType.Crypto, Market.Bybit);
            EventStepNs = eventStepNs;
            BasePrice = basePrice;
            TradeFraction = Math.Clamp(tradeFraction, 0.0, 1.0);
            _seed = seed;
        }

        /// <summary>
        /// Gets the flat event stream (interleaved trade/quote in generation order).
        /// Deterministic: identical source parameters yield identical event order.
        /// </summary>
        public IEnumerable<MarketEvent> GetEvents(ResearchJob job, QuantConnect.Symbol symbol)
        {
            return GenerateFlat(symbol);
        }

        /// <summary>
        /// Gets two ordered sub-streams (trades, quotes) suitable for the incremental merger. Each is
        /// ordered by <see cref="MarketEvent.CompareEvents"/>; ties are impossible within a stream because
        /// generated timestamps are strictly increasing per stream.
        /// </summary>
        public IEnumerable<IEnumerable<MarketEvent>> GetEventStreams(ResearchJob job, QuantConnect.Symbol symbol)
        {
            yield return GenerateTrades(symbol, countForFraction(TradeFraction));
            yield return GenerateQuotes(symbol, countForFraction(1.0 - TradeFraction));
        }

        private long countForFraction(double fraction)
        {
            if (fraction <= 0) return 0;
            var count = (long)(EventCount * fraction);
            return count < 1 && EventCount > 0 ? 1 : count;
        }

        private IEnumerable<MarketEvent> GenerateFlat(QuantConnect.Symbol symbol)
        {
            var seed = _seed;
            var clock = StartTime;
            for (long i = 0; i < EventCount; i++)
            {
                var isTrade = (i & 1) == 0;
                yield return isTrade
                    ? MakeTrade(symbol, clock, i, ref seed)
                    : MakeQuote(symbol, clock, i, ref seed);
                clock = clock.AddTicks(EventStepNs / 100);
            }
        }

        private IEnumerable<MarketEvent> GenerateTrades(QuantConnect.Symbol symbol, long count)
        {
            var seed = _seed;
            var clock = StartTime;
            for (long i = 0; i < count; i++)
            {
                yield return MakeTrade(symbol, clock, i * 2, ref seed);
                clock = clock.AddTicks((EventStepNs / 100) * 2);
            }
        }

        private IEnumerable<MarketEvent> GenerateQuotes(QuantConnect.Symbol symbol, long count)
        {
            var seed = _seed ^ 0x9E3779B9;
            var clock = StartTime.AddTicks(EventStepNs / 100);
            for (long i = 0; i < count; i++)
            {
                yield return MakeQuote(symbol, clock, i * 2 + 1, ref seed);
                clock = clock.AddTicks((EventStepNs / 100) * 2);
            }
        }

        private TradeEvent MakeTrade(QuantConnect.Symbol symbol, DateTime ts, long seq, ref long seed)
        {
            seed = Next(seed);
            var wobble = ((seed % 2001) - 1000) / 1000m;
            return new TradeEvent
            {
                Timestamp = ts,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                Provenance = new DataProvenance
                {
                    Venue = symbol.ID.Market,
                    Symbol = symbol,
                    AssetClass = symbol.SecurityType,
                    FeedType = "synthetic-trade"
                },
                SequenceNumber = seq,
                Price = BasePrice + wobble * 20m,
                Quantity = 1 + (seed % 100)
            };
        }

        private QuoteEvent MakeQuote(QuantConnect.Symbol symbol, DateTime ts, long seq, ref long seed)
        {
            seed = Next(seed);
            var wobble = ((seed % 2001) - 1000) / 1000m;
            var mid = BasePrice + wobble * 20m;
            return new QuoteEvent
            {
                Timestamp = ts,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                Provenance = new DataProvenance
                {
                    Venue = symbol.ID.Market,
                    Symbol = symbol,
                    AssetClass = symbol.SecurityType,
                    FeedType = "synthetic-quote"
                },
                SequenceNumber = seq,
                BidPrice = mid - 0.5m,
                AskPrice = mid + 0.5m,
                BidSize = 1 + (seed % 50),
                AskSize = 1 + (seed % 50)
            };
        }

        /// <summary>
        /// Small deterministic LCG (no allocation, keeps streams reproducible).
        /// </summary>
        private static long Next(long x)
        {
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            return x & 0x7FFFFFFFFFFFFFFF;
        }
    }
}