using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// Normalizes exchange-agnostic raw market data into the engine's <see cref="MarketEvent"/> model.
    /// Both the REST (historical) and WebSocket (live) connectors funnel through this so that
    /// live-observed and backfilled observations are structurally identical.
    /// </summary>
    public static class MarketEventNormalizer
    {
        /// <summary>
        /// Creates provenance metadata for an exchange-sourced event.
        /// </summary>
        public static DataProvenance CreateProvenance(
            Symbol symbol,
            string provider,
            string feedType,
            TimestampPrecision precision = TimestampPrecision.Milliseconds,
            string datasetVersion = null,
            IDictionary<string, string> metadata = null)
        {
            var provenance = new DataProvenance
            {
                Provider = provider,
                Venue = symbol.ID.Market,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                TimestampPrecision = precision,
                FeedType = feedType
            };

            if (!string.IsNullOrWhiteSpace(datasetVersion))
            {
                provenance.DatasetVersion = datasetVersion;
            }

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    provenance.Metadata[kvp.Key] = kvp.Value;
                }
            }

            return provenance;
        }

        /// <summary>
        /// Creates a trade event.
        /// </summary>
        public static TradeEvent CreateTrade(
            Symbol symbol,
            DateTime timestamp,
            decimal price,
            decimal quantity,
            TradeSide side,
            string eventId = null,
            long? sequenceNumber = null,
            DataProvenance provenance = null)
        {
            return new TradeEvent
            {
                Timestamp = timestamp,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                EventId = eventId ?? string.Empty,
                SequenceNumber = sequenceNumber,
                Provenance = provenance ?? CreateProvenance(symbol, "exchange", "trade"),
                Price = price,
                Quantity = quantity,
                Side = side
            };
        }

        /// <summary>
        /// Creates a top-of-book quote event.
        /// </summary>
        public static QuoteEvent CreateQuote(
            Symbol symbol,
            DateTime timestamp,
            decimal bidPrice,
            decimal bidSize,
            decimal askPrice,
            decimal askSize,
            string eventId = null,
            long? sequenceNumber = null,
            DataProvenance provenance = null)
        {
            return new QuoteEvent
            {
                Timestamp = timestamp,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                EventId = eventId ?? string.Empty,
                SequenceNumber = sequenceNumber,
                Provenance = provenance ?? CreateProvenance(symbol, "exchange", "quote"),
                BidPrice = bidPrice,
                BidSize = bidSize,
                AskPrice = askPrice,
                AskSize = askSize
            };
        }

        /// <summary>
        /// Creates an order book snapshot. Bids are sorted best-first (price descending),
        /// asks best-first (price ascending).
        /// </summary>
        public static OrderBookSnapshotEvent CreateOrderBookSnapshot(
            Symbol symbol,
            DateTime timestamp,
            IEnumerable<OrderBookLevel> bids,
            IEnumerable<OrderBookLevel> asks,
            string eventId = null,
            long? sequenceNumber = null,
            DataProvenance provenance = null)
        {
            var snapshot = new OrderBookSnapshotEvent
            {
                Timestamp = timestamp,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                EventId = eventId ?? string.Empty,
                SequenceNumber = sequenceNumber,
                Provenance = provenance ?? CreateProvenance(symbol, "exchange", "orderbook"),
                Bids = bids.OrderByDescending(l => l.Price).ToList(),
                Asks = asks.OrderBy(l => l.Price).ToList()
            };

            return snapshot;
        }

        /// <summary>
        /// Creates a single-level order book update event (add, modify, or remove).
        /// The quantity is the absolute new size at the price; zero means the level was removed.
        /// </summary>
        public static OrderBookUpdateEvent CreateOrderBookUpdate(
            Symbol symbol,
            DateTime timestamp,
            OrderBookSide side,
            decimal price,
            decimal quantity,
            OrderBookUpdateAction action,
            int? orderCount = null,
            string eventId = null,
            long? sequenceNumber = null,
            DataProvenance provenance = null)
        {
            return new OrderBookUpdateEvent
            {
                Timestamp = timestamp,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                EventId = eventId ?? string.Empty,
                SequenceNumber = sequenceNumber,
                Provenance = provenance ?? CreateProvenance(symbol, "exchange", "orderbook"),
                Side = side,
                Price = price,
                Quantity = quantity,
                Action = action,
                OrderCount = orderCount
            };
        }

        /// <summary>
        /// Creates an OHLCV bar event from an aggregate interval.
        /// </summary>
        public static BarEvent CreateBar(
            Symbol symbol,
            DateTime timestamp,
            TimeSpan period,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            decimal volume,
            string eventId = null,
            long? sequenceNumber = null,
            DataProvenance provenance = null)
        {
            return new BarEvent
            {
                Timestamp = timestamp,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                EventId = eventId ?? string.Empty,
                SequenceNumber = sequenceNumber,
                Provenance = provenance ?? CreateProvenance(symbol, "exchange", "bar"),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume,
                Period = period
            };
        }
    }
}