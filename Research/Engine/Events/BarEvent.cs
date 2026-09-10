namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Represents an OHLCV bar event.
    /// Used for aggregated price data over time periods.
    /// </summary>
    public class BarEvent : MarketEvent
    {
        /// <summary>
        /// Event type is Bar
        /// </summary>
        public override MarketEventType EventType => MarketEventType.Bar;

        /// <summary>
        /// Opening price of the bar
        /// </summary>
        public decimal Open { get; set; }

        /// <summary>
        /// Highest price during the bar period
        /// </summary>
        public decimal High { get; set; }

        /// <summary>
        /// Lowest price during the bar period
        /// </summary>
        public decimal Low { get; set; }

        /// <summary>
        /// Closing price of the bar
        /// </summary>
        public decimal Close { get; set; }

        /// <summary>
        /// Volume traded during the bar period
        /// </summary>
        public decimal Volume { get; set; }

        /// <summary>
        /// Time period covered by this bar
        /// </summary>
        public TimeSpan Period { get; set; }

        /// <summary>
        /// End time of the bar (Time + Period)
        /// </summary>
        public DateTime EndTime => Timestamp + Period;

        /// <summary>
        /// Typical price ((High + Low + Close) / 3)
        /// </summary>
        public decimal TypicalPrice => (High + Low + Close) / 3m;

        /// <summary>
        /// Price change (Close - Open)
        /// </summary>
        public decimal PriceChange => Close - Open;

        /// <summary>
        /// Price change percentage
        /// Returns 0 if Open is 0
        /// </summary>
        public decimal PriceChangePercent => Open != 0 ? (Close - Open) / Open * 100m : 0m;

        /// <summary>
        /// Bar range (High - Low)
        /// </summary>
        public decimal Range => High - Low;

        /// <summary>
        /// Creates a deep clone of this bar event
        /// </summary>
        public override MarketEvent Clone()
        {
            return new BarEvent
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
                Open = Open,
                High = High,
                Low = Low,
                Close = Close,
                Volume = Volume,
                Period = Period
            };
        }

        /// <summary>
        /// Converts to Lean's TradeBar type for compatibility
        /// </summary>
        public QuantConnect.Data.Market.TradeBar ToTradeBar()
        {
            return new QuantConnect.Data.Market.TradeBar
            {
                Time = Timestamp,
                Symbol = Symbol,
                Open = Open,
                High = High,
                Low = Low,
                Close = Close,
                Volume = Volume,
                Period = Period
            };
        }

        /// <summary>
        /// Creates a BarEvent from a Lean TradeBar
        /// </summary>
        public static BarEvent FromTradeBar(Data.Market.TradeBar bar, DataProvenance provenance = null)
        {
            return new BarEvent
            {
                Timestamp = bar.Time,
                Symbol = bar.Symbol,
                AssetClass = bar.Symbol.SecurityType,
                Provenance = provenance ?? new DataProvenance
                {
                    Venue = bar.Symbol.ID.Market,
                    Symbol = bar.Symbol,
                    AssetClass = bar.Symbol.SecurityType,
                    FeedType = "bar"
                },
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume,
                Period = bar.Period
            };
        }
    }
}