using QuantConnect.Research.Engine.Events;
using QuantConnect.Securities;

namespace QuantConnect.Research.Engine.MarketState
{
    /// <summary>
    /// Concrete implementation of market state.
    /// Supports both order book and quote-based markets.
    /// </summary>
    public class MarketState : IMarketState, IOrderBookState, IQuoteState
    {
        private List<OrderBookLevel> _bidLevels = new();
        private List<OrderBookLevel> _askLevels = new();
        private List<TradeEvent> _recentTrades = new();
        private List<QuoteEvent> _recentQuotes = new();

        /// <summary>
        /// Symbol for this market state
        /// </summary>
        public Symbol Symbol { get; set; } = Symbol.Empty;

        /// <summary>
        /// Asset class
        /// </summary>
        public SecurityType AssetClass { get; set; }

        /// <summary>
        /// Current timestamp of the state
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Last traded price
        /// </summary>
        public decimal LastPrice { get; set; }

        /// <summary>
        /// Mid price
        /// </summary>
        public decimal MidPrice =>
            BidPrice > 0 && AskPrice > 0
                ? (BidPrice + AskPrice) / 2m
                : LastPrice;

        /// <summary>
        /// Best bid price
        /// </summary>
        public decimal BidPrice { get; set; }

        /// <summary>
        /// Best ask price
        /// </summary>
        public decimal AskPrice { get; set; }

        /// <summary>
        /// Spread
        /// </summary>
        public decimal Spread =>
            BidPrice > 0 && AskPrice > 0
                ? AskPrice - BidPrice
                : 0m;

        /// <summary>
        /// Total volume traded
        /// </summary>
        public decimal Volume { get; set; }

        /// <summary>
        /// Number of trades
        /// </summary>
        public long TradeCount { get; set; }

        /// <summary>
        /// Bid depth (total quantity on bid side)
        /// </summary>
        public decimal BidDepth => _bidLevels.Sum(l => l.Quantity);

        /// <summary>
        /// Ask depth (total quantity on ask side)
        /// </summary>
        public decimal AskDepth => _askLevels.Sum(l => l.Quantity);

        /// <summary>
        /// Depth imbalance (positive = more bids)
        /// </summary>
        public decimal DepthImbalance
        {
            get
            {
                var total = BidDepth + AskDepth;
                return total > 0 ? (BidDepth - AskDepth) / total : 0m;
            }
        }

        /// <summary>
        /// Number of bid levels
        /// </summary>
        public int BidLevels => _bidLevels.Count;

        /// <summary>
        /// Number of ask levels
        /// </summary>
        public int AskLevels => _askLevels.Count;

        /// <summary>
        /// Bid size (for quote-based markets)
        /// </summary>
        public decimal BidSize { get; set; }

        /// <summary>
        /// Ask size (for quote-based markets)
        /// </summary>
        public decimal AskSize { get; set; }

        /// <summary>
        /// Spread in basis points
        /// </summary>
        public decimal SpreadBps =>
            MidPrice > 0
                ? (Spread / MidPrice) * 10000m
                : 0m;

        /// <summary>
        /// Quote update count
        /// </summary>
        public long QuoteUpdateCount { get; set; }

        /// <summary>
        /// Recent trades (within lookback window)
        /// </summary>
        public IReadOnlyList<TradeEvent> RecentTrades => _recentTrades;

        /// <summary>
        /// Recent quotes (within lookback window)
        /// </summary>
        public IReadOnlyList<QuoteEvent> RecentQuotes => _recentQuotes;

        /// <summary>
        /// Bid levels (sorted by price descending)
        /// </summary>
        public IReadOnlyList<OrderBookLevel> BidLevels_List => _bidLevels;

        /// <summary>
        /// Ask levels (sorted by price ascending)
        /// </summary>
        public IReadOnlyList<OrderBookLevel> AskLevels_List => _askLevels;

        /// <summary>
        /// Maximum number of recent trades to keep
        /// </summary>
        public int MaxRecentTrades { get; set; } = 1000;

        /// <summary>
        /// Maximum number of recent quotes to keep
        /// </summary>
        public int MaxRecentQuotes { get; set; } = 1000;

        /// <summary>
        /// Creates a new MarketState
        /// </summary>
        public MarketState()
        {
        }

        /// <summary>
        /// Creates a new MarketState for a symbol
        /// </summary>
        public MarketState(Symbol symbol)
        {
            Symbol = symbol;
            AssetClass = symbol.SecurityType;
        }

        /// <summary>
        /// Updates state from a market event
        /// </summary>
        public void UpdateFromEvent(MarketEvent evt)
        {
            if (evt == null) return;

            // Adopt the event's symbol when the state has none yet (reconstruction path always
            // creates states empty and fills them from incoming events).
            if (evt.Symbol != null
                && !string.IsNullOrEmpty(evt.Symbol.Value)
                && (Symbol == null || string.IsNullOrEmpty(Symbol.Value)))
            {
                Symbol = evt.Symbol;
                AssetClass = evt.Symbol.SecurityType;
            }

            // Update timestamp (use latest event timestamp)
            if (evt.Timestamp > Timestamp)
                Timestamp = evt.Timestamp;

            switch (evt)
            {
                case TradeEvent trade:
                    UpdateFromTrade(trade);
                    break;
                case QuoteEvent quote:
                    UpdateFromQuote(quote);
                    break;
                case OrderBookUpdateEvent orderBookUpdate:
                    UpdateFromOrderBookUpdate(orderBookUpdate);
                    break;
                case OrderBookSnapshotEvent snapshot:
                    UpdateFromSnapshot(snapshot);
                    break;
                case BarEvent bar:
                    UpdateFromBar(bar);
                    break;
            }
        }

        /// <summary>
        /// Updates state from a trade event
        /// </summary>
        private void UpdateFromTrade(TradeEvent trade)
        {
            LastPrice = trade.Price;
            Volume += trade.Quantity;
            TradeCount++;

            // Add to recent trades
            _recentTrades.Add(trade);
            if (_recentTrades.Count > MaxRecentTrades)
                _recentTrades.RemoveAt(0);
        }

        /// <summary>
        /// Updates state from a quote event
        /// </summary>
        private void UpdateFromQuote(QuoteEvent quote)
        {
            if (quote.BidPrice > 0)
            {
                BidPrice = quote.BidPrice;
                BidSize = quote.BidSize;
            }

            if (quote.AskPrice > 0)
            {
                AskPrice = quote.AskPrice;
                AskSize = quote.AskSize;
            }

            // A quote states the size resting at the best bid and ask, which is top-of-book
            // depth. Seeding it into the level lists is what makes BidDepth/AskDepth, and
            // therefore imbalance, mean anything on a quote-only feed. Without this the depth
            // sums stay zero unless the feed also carries L2 deltas, and every depth-derived
            // feature silently reads a flat zero.
            SeedTopOfBook(_bidLevels, quote.BidPrice, quote.BidSize, descending: true);
            SeedTopOfBook(_askLevels, quote.AskPrice, quote.AskSize, descending: false);

            QuoteUpdateCount++;

            // Add to recent quotes
            _recentQuotes.Add(quote);
            if (_recentQuotes.Count > MaxRecentQuotes)
                _recentQuotes.RemoveAt(0);
        }

        /// <summary>
        /// Applies a quoted top-of-book price and its resting size to a side's level list.
        ///
        /// The quote is authoritative about the top and about nothing else:
        ///   - a level at the quoted price is replaced, because the venue is reporting the
        ///     current total there, not a delta to add;
        ///   - levels better than the quoted price are dropped, because a venue whose best bid
        ///     is 100.5 is not also resting size above 100.5;
        ///   - levels behind the top are left alone, since a quote carries no information
        ///     about them. This means depth on a quote-only feed is the top level plus
        ///     whatever L2 previously established, and a level the book has moved away from
        ///     keeps its last known size until an L2 update corrects it.
        ///   - a quoted size of zero is not a withdrawal: some venues omit size entirely, and
        ///     treating that as "level removed" would make imbalance flicker to zero on every
        ///     such tick. Zero is ignored unless the price moved.
        /// </summary>
        private static void SeedTopOfBook(List<OrderBookLevel> levels, decimal price, decimal size, bool descending)
        {
            if (price <= 0)
            {
                return;
            }

            // Levels better than the quoted top cannot exist.
            levels.RemoveAll(l => descending ? l.Price > price : l.Price < price);

            if (size <= 0)
            {
                return;
            }

            levels.RemoveAll(l => l.Price == price);
            levels.Add(new OrderBookLevel { Price = price, Quantity = size });
            levels.Sort((a, b) => descending ? b.Price.CompareTo(a.Price) : a.Price.CompareTo(b.Price));
        }

        /// <summary>
        /// Updates state from an order book update
        /// </summary>
        private void UpdateFromOrderBookUpdate(OrderBookUpdateEvent update)
        {
            var levels = update.Side == OrderBookSide.Bid ? _bidLevels : _askLevels;

            var existingLevel = levels.FirstOrDefault(l => l.Price == update.Price);

            if (update.Action == OrderBookUpdateAction.Remove || update.Quantity == 0)
            {
                if (existingLevel != null)
                    levels.Remove(existingLevel);
            }
            else if (existingLevel != null)
            {
                existingLevel.Quantity = update.Quantity;
                existingLevel.OrderCount = update.OrderCount;
            }
            else
            {
                levels.Add(new OrderBookLevel
                {
                    Price = update.Price,
                    Quantity = update.Quantity,
                    OrderCount = update.OrderCount
                });
            }

            // Re-sort levels
            _bidLevels.Sort((a, b) => b.Price.CompareTo(a.Price)); // Descending
            _askLevels.Sort((a, b) => a.Price.CompareTo(b.Price)); // Ascending

            // Update best bid/ask
            BidPrice = _bidLevels.Count > 0 ? _bidLevels[0].Price : 0m;
            AskPrice = _askLevels.Count > 0 ? _askLevels[0].Price : 0m;
        }

        /// <summary>
        /// Updates state from a full snapshot
        /// </summary>
        private void UpdateFromSnapshot(OrderBookSnapshotEvent snapshot)
        {
            _bidLevels.Clear();
            _bidLevels.AddRange(snapshot.Bids.Select(b => b.Clone()));

            _askLevels.Clear();
            _askLevels.AddRange(snapshot.Asks.Select(a => a.Clone()));

            BidPrice = snapshot.BestBidPrice;
            AskPrice = snapshot.BestAskPrice;
        }

        /// <summary>
        /// Updates state from a bar event
        /// </summary>
        private void UpdateFromBar(BarEvent bar)
        {
            LastPrice = bar.Close;
            Volume += bar.Volume;
        }

        /// <summary>
        /// Gets bid depth within N basis points of mid
        /// </summary>
        public decimal GetBidDepthWithinBps(decimal bps)
        {
            if (MidPrice <= 0) return 0m;

            var threshold = MidPrice * (1 - bps / 10000m);
            return _bidLevels.Where(l => l.Price >= threshold).Sum(l => l.Quantity);
        }

        /// <summary>
        /// Gets ask depth within N basis points of mid
        /// </summary>
        public decimal GetAskDepthWithinBps(decimal bps)
        {
            if (MidPrice <= 0) return 0m;

            var threshold = MidPrice * (1 + bps / 10000m);
            return _askLevels.Where(l => l.Price <= threshold).Sum(l => l.Quantity);
        }

        /// <summary>
        /// Creates a deep clone of this state
        /// </summary>
        public IMarketState Clone()
        {
            return CloneTyped();
        }

        /// <summary>
        /// Restores order book levels (used by <see cref="MarketStateSerialization"/> on resume).
        /// </summary>
        internal void RestoreBook(IEnumerable<OrderBookLevel> bids, IEnumerable<OrderBookLevel> asks)
        {
            _bidLevels = new List<OrderBookLevel>(bids);
            _askLevels = new List<OrderBookLevel>(asks);
            if (_bidLevels.Count > 0) BidPrice = _bidLevels[0].Price;
            if (_askLevels.Count > 0) AskPrice = _askLevels[0].Price;
        }

        /// <summary>
        /// Restores recent trade history (used by <see cref="MarketStateSerialization"/> on resume).
        /// </summary>
        internal void RestoreRecentTrades(IEnumerable<TradeEvent> trades)
        {
            _recentTrades = new List<TradeEvent>(trades);
        }

        /// <summary>
        /// Restores recent quote history (used by <see cref="MarketStateSerialization"/> on resume).
        /// </summary>
        internal void RestoreRecentQuotes(IEnumerable<QuoteEvent> quotes)
        {
            _recentQuotes = new List<QuoteEvent>(quotes);
        }

        /// <summary>
        /// Creates a deep clone with full type
        /// </summary>
        public MarketState CloneTyped()
        {
            return new MarketState
            {
                Symbol = Symbol,
                AssetClass = AssetClass,
                Timestamp = Timestamp,
                LastPrice = LastPrice,
                BidPrice = BidPrice,
                AskPrice = AskPrice,
                Volume = Volume,
                TradeCount = TradeCount,
                BidSize = BidSize,
                AskSize = AskSize,
                QuoteUpdateCount = QuoteUpdateCount,
                MaxRecentTrades = MaxRecentTrades,
                MaxRecentQuotes = MaxRecentQuotes,
                _bidLevels = _bidLevels.Select(l => l.Clone()).ToList(),
                _askLevels = _askLevels.Select(l => l.Clone()).ToList(),
                _recentTrades = new List<TradeEvent>(_recentTrades.Select(t => (TradeEvent)t.Clone())),
                _recentQuotes = new List<QuoteEvent>(_recentQuotes.Select(q => (QuoteEvent)q.Clone()))
            };
        }
    }
}