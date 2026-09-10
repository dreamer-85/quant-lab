namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Represents a custom/user-defined market event.
    /// Provides extensibility for event types not covered by the standard types.
    /// </summary>
    public class CustomMarketEvent : MarketEvent
    {
        /// <summary>
        /// Event type is Custom
        /// </summary>
        public override MarketEventType EventType => MarketEventType.Custom;

        /// <summary>
        /// Custom event type name (e.g., "Funding", "Liquidation", "Auction")
        /// </summary>
        public string CustomEventType { get; set; } = string.Empty;

        /// <summary>
        /// Event value (generic numeric value)
        /// </summary>
        public decimal Value { get; set; }

        /// <summary>
        /// Additional data as key-value pairs
        /// </summary>
        public Dictionary<string, object> Data { get; set; } = new();

        /// <summary>
        /// Creates a deep clone of this custom event
        /// </summary>
        public override MarketEvent Clone()
        {
            return new CustomMarketEvent
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
                CustomEventType = CustomEventType,
                Value = Value,
                Data = new Dictionary<string, object>(Data)
            };
        }
    }

    /// <summary>
    /// Predefined custom event types for common market events
    /// </summary>
    public static class CustomEventTypes
    {
        /// <summary>
        /// Funding rate event (crypto perpetuals)
        /// </summary>
        public const string Funding = "Funding";

        /// <summary>
        /// Liquidation event
        /// </summary>
        public const string Liquidation = "Liquidation";

        /// <summary>
        /// Auction event
        /// </summary>
        public const string Auction = "Auction";

        /// <summary>
        /// Index rebalance event
        /// </summary>
        public const string IndexRebalance = "IndexRebalance";

        /// <summary>
        /// Corporate action event
        /// </summary>
        public const string CorporateAction = "CorporateAction";
    }
}