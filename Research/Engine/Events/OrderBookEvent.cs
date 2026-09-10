namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Represents an order book level update (add, remove, or modify).
    /// Used for centralized limit order book (CLOB) markets.
    /// </summary>
    public class OrderBookUpdateEvent : MarketEvent
    {
        /// <summary>
        /// Event type is OrderBookUpdate
        /// </summary>
        public override MarketEventType EventType => MarketEventType.OrderBookUpdate;

        /// <summary>
        /// Side of the order book update
        /// </summary>
        public OrderBookSide Side { get; set; }

        /// <summary>
        /// Price level being updated
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// New quantity at this price level.
        /// Zero means the level was removed.
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// Type of update operation
        /// </summary>
        public OrderBookUpdateAction Action { get; set; }

        /// <summary>
        /// Number of orders at this price level (if available from exchange)
        /// </summary>
        public int? OrderCount { get; set; }

        /// <summary>
        /// Creates a deep clone of this order book event
        /// </summary>
        public override MarketEvent Clone()
        {
            return new OrderBookUpdateEvent
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
                Side = Side,
                Price = Price,
                Quantity = Quantity,
                Action = Action,
                OrderCount = OrderCount
            };
        }
    }

    /// <summary>
    /// Order book side
    /// </summary>
    public enum OrderBookSide
    {
        /// <summary>
        /// Bid side (buyers)
        /// </summary>
        Bid,

        /// <summary>
        /// Ask side (sellers)
        /// </summary>
        Ask
    }

    /// <summary>
    /// Type of order book update
    /// </summary>
    public enum OrderBookUpdateAction
    {
        /// <summary>
        /// New price level added
        /// </summary>
        Add,

        /// <summary>
        /// Existing price level modified (quantity changed)
        /// </summary>
        Modify,

        /// <summary>
        /// Price level removed (quantity became zero)
        /// </summary>
        Remove
    }
}