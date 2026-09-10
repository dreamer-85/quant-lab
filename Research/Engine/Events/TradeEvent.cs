namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Represents an individual trade execution.
    /// This is the most granular trade data available.
    /// </summary>
    public class TradeEvent : MarketEvent
    {
        /// <summary>
        /// Event type is Trade
        /// </summary>
        public override MarketEventType EventType => MarketEventType.Trade;

        /// <summary>
        /// Trade price
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// Trade quantity/size
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// Trade side (buy/sell) if known
        /// </summary>
        public TradeSide? Side { get; set; }

        /// <summary>
        /// Sale condition or trade flags (e.g., "regular", "odd lot", "cross")
        /// </summary>
        public string SaleCondition { get; set; } = string.Empty;

        /// <summary>
        /// Whether this trade is considered suspicious
        /// </summary>
        public bool Suspicious { get; set; }

        /// <summary>
        /// Creates a deep clone of this trade event
        /// </summary>
        public override MarketEvent Clone()
        {
            return new TradeEvent
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
                Price = Price,
                Quantity = Quantity,
                Side = Side,
                SaleCondition = SaleCondition,
                Suspicious = Suspicious
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
                Value = Price,
                Quantity = Quantity,
                TickType = TickType.Trade,
                Exchange = Provenance.Venue,
                SaleCondition = SaleCondition,
                Suspicious = Suspicious
            };
        }

        /// <summary>
        /// Creates a TradeEvent from a Lean Tick
        /// </summary>
        public static TradeEvent FromTick(Data.Market.Tick tick, DataProvenance provenance = null)
        {
            return new TradeEvent
            {
                Timestamp = tick.Time,
                Symbol = tick.Symbol,
                AssetClass = tick.Symbol.SecurityType,
                Provenance = provenance ?? new DataProvenance
                {
                    Venue = tick.Symbol.ID.Market,
                    Symbol = tick.Symbol,
                    AssetClass = tick.Symbol.SecurityType,
                    FeedType = "trade"
                },
                Price = tick.Value,
                Quantity = tick.Quantity,
                SaleCondition = tick.SaleCondition,
                Suspicious = tick.Suspicious
            };
        }
    }

    /// <summary>
    /// Trade execution side
    /// </summary>
    public enum TradeSide
    {
        /// <summary>
        /// Buy/initiating buy
        /// </summary>
        Buy,

        /// <summary>
        /// Sell/initiating sell
        /// </summary>
        Sell,

        /// <summary>
        /// Unknown/undetermined
        /// </summary>
        Unknown
    }
}