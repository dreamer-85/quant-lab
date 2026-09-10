namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Represents a full order book snapshot.
    /// Contains the complete state of the order book at a point in time.
    /// </summary>
    public class OrderBookSnapshotEvent : MarketEvent
    {
        /// <summary>
        /// Event type is OrderBookSnapshot
        /// </summary>
        public override MarketEventType EventType => MarketEventType.OrderBookSnapshot;

        /// <summary>
        /// Bid levels sorted by price descending (best bid first)
        /// Each tuple is (Price, Quantity)
        /// </summary>
        public List<OrderBookLevel> Bids { get; set; } = new();

        /// <summary>
        /// Ask levels sorted by price ascending (best ask first)
        /// Each tuple is (Price, Quantity)
        /// </summary>
        public List<OrderBookLevel> Asks { get; set; } = new();

        /// <summary>
        /// Best bid price (highest bid)
        /// Returns 0 if no bids
        /// </summary>
        public decimal BestBidPrice => Bids.Count > 0 ? Bids[0].Price : 0m;

        /// <summary>
        /// Best ask price (lowest ask)
        /// Returns 0 if no asks
        /// </summary>
        public decimal BestAskPrice => Asks.Count > 0 ? Asks[0].Price : 0m;

        /// <summary>
        /// Best bid quantity
        /// Returns 0 if no bids
        /// </summary>
        public decimal BestBidQuantity => Bids.Count > 0 ? Bids[0].Quantity : 0m;

        /// <summary>
        /// Best ask quantity
        /// Returns 0 if no asks
        /// </summary>
        public decimal BestAskQuantity => Asks.Count > 0 ? Asks[0].Quantity : 0m;

        /// <summary>
        /// Mid price (average of best bid and best ask)
        /// Returns 0 if either side is empty
        /// </summary>
        public decimal MidPrice =>
            BestBidPrice > 0 && BestAskPrice > 0
                ? (BestBidPrice + BestAskPrice) / 2m
                : 0m;

        /// <summary>
        /// Spread (best ask - best bid)
        /// Returns 0 if either side is empty
        /// </summary>
        public decimal Spread =>
            BestBidPrice > 0 && BestAskPrice > 0
                ? BestAskPrice - BestBidPrice
                : 0m;

        /// <summary>
        /// Total bid depth (sum of all bid quantities)
        /// </summary>
        public decimal TotalBidDepth => Bids.Sum(l => l.Quantity);

        /// <summary>
        /// Total ask depth (sum of all ask quantities)
        /// </summary>
        public decimal TotalAskDepth => Asks.Sum(l => l.Quantity);

        /// <summary>
        /// Depth imbalance (positive means more bids, negative means more asks)
        /// Calculated as (TotalBidDepth - TotalAskDepth) / (TotalBidDepth + TotalAskDepth)
        /// Returns 0 if total depth is 0
        /// </summary>
        public decimal DepthImbalance
        {
            get
            {
                var total = TotalBidDepth + TotalAskDepth;
                return total > 0
                    ? (TotalBidDepth - TotalAskDepth) / total
                    : 0m;
            }
        }

        /// <summary>
        /// Creates a deep clone of this order book snapshot
        /// </summary>
        public override MarketEvent Clone()
        {
            return new OrderBookSnapshotEvent
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
                Bids = Bids.Select(b => b.Clone()).ToList(),
                Asks = Asks.Select(a => a.Clone()).ToList()
            };
        }

        /// <summary>
        /// Gets bid depth within a specified number of basis points from mid price
        /// </summary>
        public decimal GetBidDepthWithinBps(decimal bps)
        {
            if (MidPrice <= 0) return 0m;

            var threshold = MidPrice * (1 - bps / 10000m);
            return Bids.Where(b => b.Price >= threshold).Sum(b => b.Quantity);
        }

        /// <summary>
        /// Gets ask depth within a specified number of basis points from mid price
        /// </summary>
        public decimal GetAskDepthWithinBps(decimal bps)
        {
            if (MidPrice <= 0) return 0m;

            var threshold = MidPrice * (1 + bps / 10000m);
            return Asks.Where(a => a.Price <= threshold).Sum(a => a.Quantity);
        }

        /// <summary>
        /// Gets the price level with the highest quantity on the bid side
        /// </summary>
        public OrderBookLevel GetLargestBidLevel()
        {
            return Bids.OrderByDescending(b => b.Quantity).FirstOrDefault();
        }

        /// <summary>
        /// Gets the price level with the highest quantity on the ask side
        /// </summary>
        public OrderBookLevel GetLargestAskLevel()
        {
            return Asks.OrderByDescending(a => a.Quantity).FirstOrDefault();
        }
    }

    /// <summary>
    /// Represents a single price level in the order book
    /// </summary>
    public class OrderBookLevel
    {
        /// <summary>
        /// Price at this level
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// Quantity/size at this level
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// Number of orders at this level (if available)
        /// </summary>
        public int? OrderCount { get; set; }

        /// <summary>
        /// Creates a clone of this level
        /// </summary>
        public OrderBookLevel Clone()
        {
            return new OrderBookLevel
            {
                Price = Price,
                Quantity = Quantity,
                OrderCount = OrderCount
            };
        }

        public override string ToString()
        {
            return $"Price: {Price}, Qty: {Quantity}";
        }
    }
}