namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Represents a bid/ask quote update.
    /// Used for FX, equity quotes, and order book top-of-book updates.
    /// </summary>
    public class QuoteEvent : MarketEvent
    {
        /// <summary>
        /// Event type is Quote
        /// </summary>
        public override MarketEventType EventType => MarketEventType.Quote;

        /// <summary>
        /// Bid price (best bid)
        /// </summary>
        public decimal BidPrice { get; set; }

        /// <summary>
        /// Bid size/quantity at best bid
        /// </summary>
        public decimal BidSize { get; set; }

        /// <summary>
        /// Ask price (best ask)
        /// </summary>
        public decimal AskPrice { get; set; }

        /// <summary>
        /// Ask size/quantity at best ask
        /// </summary>
        public decimal AskSize { get; set; }

        /// <summary>
        /// Mid price (calculated as (BidPrice + AskPrice) / 2)
        /// Returns 0 if either price is 0
        /// </summary>
        public decimal MidPrice =>
            BidPrice > 0 && AskPrice > 0
                ? (BidPrice + AskPrice) / 2m
                : 0m;

        /// <summary>
        /// Spread (calculated as AskPrice - BidPrice)
        /// Returns 0 if either price is 0
        /// </summary>
        public decimal Spread =>
            BidPrice > 0 && AskPrice > 0
                ? AskPrice - BidPrice
                : 0m;

        /// <summary>
        /// Spread in basis points
        /// Returns 0 if mid price is 0
        /// </summary>
        public decimal SpreadBps =>
            MidPrice > 0
                ? (Spread / MidPrice) * 10000m
                : 0m;

        /// <summary>
        /// Creates a deep clone of this quote event
        /// </summary>
        public override MarketEvent Clone()
        {
            return new QuoteEvent
            {
                Timestamp = Timestamp,
                Symbol = Symbol,
                AssetClass = AssetClass,
                Provenance = new DataProvenance
                {
                    Provider = Provenance.Provider,
                    Venue = Provenance.Venue,
                    Symbol = Provenance.Symbol,
                    AssetClass = Provenance.AssetClass,
                    TimestampPrecision = Provenance.TimestampPrecision,
                    FeedType = Provenance.FeedType,
                    DatasetVersion = Provenance.DatasetVersion,
                    Metadata = new Dictionary<string, string>(Provenance.Metadata)
                },
                SequenceNumber = SequenceNumber,
                ArrivalTimestamp = ArrivalTimestamp,
                EventId = EventId,
                IsValidated = IsValidated,
                BidPrice = BidPrice,
                BidSize = BidSize,
                AskPrice = AskPrice,
                AskSize = AskSize
            };
        }

        /// <summary>
        /// Converts to Lean's Tick type for compatibility
        /// </summary>
        public QuantConnect.Data.Market.Tick ToTick()
        {
            return new QuantConnect.Data.Market.Tick
            {
                Time = Timestamp,
                Symbol = Symbol,
                BidPrice = BidPrice,
                BidSize = BidSize,
                AskPrice = AskPrice,
                AskSize = AskSize,
                TickType = TickType.Quote,
                Exchange = Provenance.Venue
            };
        }

        /// <summary>
        /// Creates a QuoteEvent from a Lean Tick
        /// </summary>
        public static QuoteEvent FromTick(Data.Market.Tick tick, DataProvenance provenance = null)
        {
            return new QuoteEvent
            {
                Timestamp = tick.Time,
                Symbol = tick.Symbol,
                AssetClass = tick.Symbol.SecurityType,
                Provenance = provenance ?? new DataProvenance
                {
                    Venue = tick.Symbol.ID.Market,
                    Symbol = tick.Symbol,
                    AssetClass = tick.Symbol.SecurityType,
                    FeedType = "quote"
                },
                BidPrice = tick.BidPrice,
                BidSize = tick.BidSize,
                AskPrice = tick.AskPrice,
                AskSize = tick.AskSize
            };
        }

        /// <summary>
        /// Creates a QuoteEvent from a Lean QuoteBar
        /// </summary>
        public static QuoteEvent FromQuoteBar(Data.Market.QuoteBar bar, DataProvenance provenance = null)
        {
            return new QuoteEvent
            {
                Timestamp = bar.Time,
                Symbol = bar.Symbol,
                AssetClass = bar.Symbol.SecurityType,
                Provenance = provenance ?? new DataProvenance
                {
                    Venue = bar.Symbol.ID.Market,
                    Symbol = bar.Symbol,
                    AssetClass = bar.Symbol.SecurityType,
                    FeedType = "quote"
                },
                BidPrice = bar.Bid?.Close ?? 0m,
                BidSize = bar.LastBidSize,
                AskPrice = bar.Ask?.Close ?? 0m,
                AskSize = bar.LastAskSize
            };
        }
    }
}