namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Defines the type of market event
    /// </summary>
    public enum MarketEventType
    {
        /// <summary>
        /// Individual trade execution
        /// </summary>
        Trade,

        /// <summary>
        /// Bid/Ask quote update
        /// </summary>
        Quote,

        /// <summary>
        /// Order book level update (add/remove/change)
        /// </summary>
        OrderBookUpdate,

        /// <summary>
        /// Full order book snapshot
        /// </summary>
        OrderBookSnapshot,

        /// <summary>
        /// OHLC bar aggregation
        /// </summary>
        Bar,

        /// <summary>
        /// Funding rate event (crypto perpetuals)
        /// </summary>
        Funding,

        /// <summary>
        /// Liquidation event
        /// </summary>
        Liquidation,

        /// <summary>
        /// Auction event
        /// </summary>
        Auction,

        /// <summary>
        /// Custom/user-defined event
        /// </summary>
        Custom
    }
}