using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;

namespace QuantConnect.Research.Engine.Observations
{
    /// <summary>
    /// Represents a point-in-time observation of market state.
    /// Observations are generated at regular intervals from event streams.
    /// </summary>
    public class Observation
    {
        /// <summary>
        /// Timestamp of this observation
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Market state at observation time
        /// </summary>
        public MarketState.MarketState State { get; set; }

        /// <summary>
        /// Events that occurred since the previous observation
        /// </summary>
        public List<MarketEvent> Events { get; set; } = new();

        /// <summary>
        /// Whether this period received fresh data or carried the previous state forward.
        /// See <see cref="Observations.DataQuality"/>.
        /// </summary>
        public DataQuality Quality { get; set; } = DataQuality.Fresh;

        /// <summary>
        /// Timestamp of the newest event incorporated into this observation, or null when no event
        /// has been observed yet. Never greater than <see cref="Timestamp"/>: an observation can
        /// never contain data from its own future. The difference is the observation's data age and
        /// is the honest measure of how stale the state is when the grid runs ahead of the feed.
        /// </summary>
        public DateTime? LastEventTimestamp { get; set; }

        /// <summary>
        /// True when this period carried the previous state forward with no new events.
        /// </summary>
        public bool IsFilled => Quality == DataQuality.Filled;

        /// <summary>
        /// Wall-clock age of the state at this observation, or null when no event has been seen.
        /// </summary>
        public TimeSpan? DataAge => LastEventTimestamp.HasValue
            ? Timestamp - LastEventTimestamp.Value
            : (TimeSpan?)null;

        /// <summary>
        /// Number of trades since last observation
        /// </summary>
        public int TradeCount => Events.Count(e => e.EventType == MarketEventType.Trade);

        /// <summary>
        /// Number of quote updates since last observation
        /// </summary>
        public int QuoteCount => Events.Count(e => e.EventType == MarketEventType.Quote);

        /// <summary>
        /// Total volume since last observation
        /// </summary>
        public decimal Volume => Events
            .OfType<TradeEvent>()
            .Sum(t => t.Quantity);

        /// <summary>
        /// Price at start of observation period
        /// </summary>
        public decimal OpenPrice => Events.Count > 0
            ? Events.First() is TradeEvent firstTrade
                ? firstTrade.Price
                : Events.First() is QuoteEvent firstQuote
                    ? firstQuote.MidPrice
                    : 0m
            : 0m;

        /// <summary>
        /// Price at end of observation period
        /// </summary>
        public decimal ClosePrice => Events.Count > 0
            ? Events.Last() is TradeEvent lastTrade
                ? lastTrade.Price
                : Events.Last() is QuoteEvent lastQuote
                    ? lastQuote.MidPrice
                    : 0m
            : 0m;

        /// <summary>
        /// Highest price during observation period
        /// </summary>
        public decimal HighPrice => Events
            .OfType<TradeEvent>()
            .Select(t => t.Price)
            .DefaultIfEmpty(0m)
            .Max();

        /// <summary>
        /// Lowest price during observation period
        /// </summary>
        public decimal LowPrice => Events
            .OfType<TradeEvent>()
            .Select(t => t.Price)
            .DefaultIfEmpty(0m)
            .Min();

        /// <summary>
        /// VWAP (Volume Weighted Average Price) for the observation period
        /// </summary>
        public decimal VWAP
        {
            get
            {
                var trades = Events.OfType<TradeEvent>().ToList();
                if (trades.Count == 0) return 0m;

                var totalVolume = trades.Sum(t => t.Quantity);
                if (totalVolume == 0) return 0m;

                return trades.Sum(t => t.Price * t.Quantity) / totalVolume;
            }
        }

        /// <summary>
        /// Creates a deep clone of this observation
        /// </summary>
        public Observation Clone()
        {
            return new Observation
            {
                Timestamp = Timestamp,
                State = State?.CloneTyped(),
                Events = Events.Select(e => e.Clone()).ToList(),
                Quality = Quality,
                LastEventTimestamp = LastEventTimestamp
            };
        }
    }
}