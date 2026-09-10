using QuantConnect.Securities;

namespace QuantConnect.Research.Engine.MarketState
{
    /// <summary>
    /// Interface for market state representation.
    /// Provides asset-class agnostic access to market data.
    /// </summary>
    public interface IMarketState
    {
        /// <summary>
        /// Symbol for this market state
        /// </summary>
        Symbol Symbol { get; }

        /// <summary>
        /// Asset class
        /// </summary>
        SecurityType AssetClass { get; }

        /// <summary>
        /// Current timestamp of the state
        /// </summary>
        DateTime Timestamp { get; }

        /// <summary>
        /// Last traded price
        /// </summary>
        decimal LastPrice { get; }

        /// <summary>
        /// Mid price (if available)
        /// </summary>
        decimal MidPrice { get; }

        /// <summary>
        /// Best bid price (if available)
        /// </summary>
        decimal BidPrice { get; }

        /// <summary>
        /// Best ask price (if available)
        /// </summary>
        decimal AskPrice { get; }

        /// <summary>
        /// Spread (if available)
        /// </summary>
        decimal Spread { get; }

        /// <summary>
        /// Total volume traded in current period
        /// </summary>
        decimal Volume { get; }

        /// <summary>
        /// Number of trades in current period
        /// </summary>
        long TradeCount { get; }

        /// <summary>
        /// Creates a deep clone of this state
        /// </summary>
        IMarketState Clone();
    }

    /// <summary>
    /// Extended market state for order book markets
    /// </summary>
    public interface IOrderBookState : IMarketState
    {
        /// <summary>
        /// Total bid depth
        /// </summary>
        decimal BidDepth { get; }

        /// <summary>
        /// Total ask depth
        /// </summary>
        decimal AskDepth { get; }

        /// <summary>
        /// Depth imbalance (positive = more bids)
        /// </summary>
        decimal DepthImbalance { get; }

        /// <summary>
        /// Number of bid levels
        /// </summary>
        int BidLevels { get; }

        /// <summary>
        /// Number of ask levels
        /// </summary>
        int AskLevels { get; }

        /// <summary>
        /// Gets bid depth within N basis points of mid
        /// </summary>
        decimal GetBidDepthWithinBps(decimal bps);

        /// <summary>
        /// Gets ask depth within N basis points of mid
        /// </summary>
        decimal GetAskDepthWithinBps(decimal bps);
    }

    /// <summary>
    /// Extended market state for quote-based markets (FX)
    /// </summary>
    public interface IQuoteState : IMarketState
    {
        /// <summary>
        /// Bid size
        /// </summary>
        decimal BidSize { get; }

        /// <summary>
        /// Ask size
        /// </summary>
        decimal AskSize { get; }

        /// <summary>
        /// Spread in basis points
        /// </summary>
        decimal SpreadBps { get; }

        /// <summary>
        /// Quote update count in current period
        /// </summary>
        long QuoteUpdateCount { get; }
    }
}