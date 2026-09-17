using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.Liquidity
{
    /// <summary>
    /// Kind of liquidity transition detected per observation window.
    /// </summary>
    public enum LiquidityTransitionType
    {
        /// <summary>Depth within the bps band fell by at least the depletion threshold.</summary>
        Depletion = 0,

        /// <summary>Depth within the bps band rose by at least the replenishment threshold.</summary>
        Replenishment = 1,

        /// <summary>The best price relocated by at least the migration threshold (level pull/migration).</summary>
        Migration = 2,

        /// <summary>A single level now absorbs at least wall-multiple x the typical level size.</summary>
        WallFormation = 3
    }

    /// <summary>
    /// An explainable, point-in-time liquidity transition detected from consecutive observations.
    /// Every field is available at detection time (no future information) except
    /// <see cref="MsUntilReplenishment"/>, which a later replenishment back-fills.
    /// </summary>
    public sealed class LiquidityTransitionEvent
    {
        /// <summary>Observation timestamp at which the transition was detected.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>Symbol (ticker) of the order book.</summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>Type of transition.</summary>
        public LiquidityTransitionType Type { get; set; }

        /// <summary>Order book side most affected.</summary>
        public OrderBookSide Side { get; set; }

        /// <summary>Best price on the affected side at detection time.</summary>
        public decimal Price { get; set; }

        /// <summary>Mid price at detection time.</summary>
        public decimal MidPrice { get; set; }

        /// <summary>Depth (base currency) within the band on the affected side before the transition.</summary>
        public decimal DepthBefore { get; set; }

        /// <summary>Depth (base currency) within the band on the affected side after the transition.</summary>
        public decimal DepthAfter { get; set; }

        /// <summary>DepthAfter - DepthBefore.</summary>
        public decimal DepthDelta { get; set; }

        /// <summary>Bps band used for depths (e.g. 10 = +/-10 bps around mid).</summary>
        public decimal BpsBand { get; set; }

        /// <summary>True when this depletion coincides with taker prints that consumed the liquidity.</summary>
        public bool Executed { get; set; }

        /// <summary>Quote-currency notional of opposite-sided prints at/above (below) the best in this window.</summary>
        public decimal ExecutedVolume { get; set; }

        /// <summary>Buy-taker notional in the observation window (aggressive buy flow).</summary>
        public decimal AggressiveBuyVolume { get; set; }

        /// <summary>Sell-taker notional in the observation window (aggressive sell flow).</summary>
        public decimal AggressiveSellVolume { get; set; }

        /// <summary>AggressiveBuyVolume - AggressiveSellVolume (signed aggressive flow).</summary>
        public decimal NetFlow { get; set; }

        /// <summary>Order book update events in the observation window.</summary>
        public int UpdateCount { get; set; }

        /// <summary>Removed levels in the observation window.</summary>
        public int RemoveCount { get; set; }

        /// <summary>Added levels in the observation window.</summary>
        public int AddCount { get; set; }

        /// <summary>Trade events in the observation window.</summary>
        public int TradeCount { get; set; }

        /// <summary>Spread (bps) at detection time.</summary>
        public decimal SpreadBps { get; set; }

        /// <summary>Milliseconds since the previous detected transition on this symbol (persistence spacing).</summary>
        public long MsSinceLastEvent { get; set; }

        /// <summary>
        /// Back-filled once a replenishment on the same side is later detected: milliseconds from this
        /// depletion until that replenishment. Null until then.
        /// </summary>
        public long? MsUntilReplenishment { get; set; }
    }
}