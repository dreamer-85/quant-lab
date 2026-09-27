namespace QuantConnect.Research.Engine.Portfolio
{
    /// <summary>
    /// Why a fill happened, which is what turns a stream of quantity changes into readable trades.
    /// Without this a reversal of +10 to -10 is just two signed numbers; with it, it reads as a close
    /// and an open.
    /// </summary>
    public enum FillReason
    {
        /// <summary>
        /// Flat to long, or flat to short
        /// </summary>
        Open,

        /// <summary>
        /// Adding to an existing position in the same direction, re-averaging the entry
        /// </summary>
        Increase,

        /// <summary>
        /// Partially closing a position, realizing part of its profit or loss. The average entry is
        /// left untouched, so what remains is still the original trade.
        /// </summary>
        Reduce,

        /// <summary>
        /// Flattening a position completely
        /// </summary>
        Close,

        /// <summary>
        /// Flipping through flat in one order, closing the old position and opening the opposite one
        /// </summary>
        Reverse
    }

    /// <summary>
    /// A single executed order leg against the paper account.
    ///
    /// Fills are the record of intent becoming a position: they are what a broker would report, and
    /// they are the only place a price is agreed. A trade in the usual sense is the pair of fills
    /// that opens and then flattens a position, which
    /// <see cref="PaperPortfolio.ClosedTrades"/> pairs up.
    /// </summary>
    public sealed class Fill
    {
        /// <summary>
        /// Time the fill was executed
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Symbol filled
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Signed quantity: positive bought, negative sold
        /// </summary>
        public decimal SignedQuantity { get; set; }

        /// <summary>
        /// Price the fill executed at
        /// </summary>
        public decimal Price { get; set; }

        /// <summary>
        /// Notional value of the fill
        /// </summary>
        public decimal Notional => Math.Abs(SignedQuantity) * Price;

        /// <summary>
        /// Fee charged on this fill
        /// </summary>
        public decimal Fee { get; set; }

        /// <summary>
        /// Profit and loss realized by this fill, net of fee. Zero when opening or adding to a
        /// position, since nothing has been sold against a basis yet.
        /// </summary>
        public decimal RealizedPnl { get; set; }

        /// <summary>
        /// Why the fill happened
        /// </summary>
        public FillReason Reason { get; set; }

        /// <summary>
        /// Net change in cash caused by this fill
        /// </summary>
        public decimal CashDelta { get; set; }

        /// <summary>
        /// Cash on hand immediately after the fill
        /// </summary>
        public decimal CashAfter { get; set; }

        /// <summary>
        /// Signed position quantity immediately after the fill
        /// </summary>
        public decimal PositionAfter { get; set; }

        /// <summary>
        /// Average entry price of the position after the fill
        /// </summary>
        public decimal AverageEntryAfter { get; set; }

        /// <summary>
        /// Total equity immediately after the fill, marking any remaining position at this price
        /// </summary>
        public decimal EquityAfter { get; set; }
    }

    /// <summary>
    /// A complete round trip: a position that was opened and then flattened.
    ///
    /// This is the unit that answers "did that trade make money", which a bare fill cannot. It pairs
    /// the opening fill with the fill that returned the position to flat, and carries the net result
    /// including every fee paid along the way.
    /// </summary>
    public sealed class ClosedTrade
    {
        /// <summary>
        /// Symbol traded
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Side of the position held during the trade
        /// </summary>
        public PositionSide Side { get; set; }

        /// <summary>
        /// Time the position was first opened
        /// </summary>
        public DateTime EntryTime { get; set; }

        /// <summary>
        /// Time the position was flattened
        /// </summary>
        public DateTime ExitTime { get; set; }

        /// <summary>
        /// Price the position was opened at
        /// </summary>
        public decimal EntryPrice { get; set; }

        /// <summary>
        /// Price the position was flattened at
        /// </summary>
        public decimal ExitPrice { get; set; }

        /// <summary>
        /// Peak quantity held during the trade
        /// </summary>
        public decimal Quantity { get; set; }

        /// <summary>
        /// Time the position was held, i.e. exit minus entry
        /// </summary>
        public TimeSpan HoldingPeriod => ExitTime - EntryTime;

        /// <summary>
        /// Net profit and loss of the round trip, after every fee paid
        /// </summary>
        public decimal Pnl { get; set; }

        /// <summary>
        /// Fees paid across the whole round trip
        /// </summary>
        public decimal Fees { get; set; }

        /// <summary>
        /// Profit and loss as a fraction of the capital committed to the position
        /// </summary>
        public decimal ReturnPct { get; set; }

        /// <summary>
        /// Whether the trade ended with a profit
        /// </summary>
        public bool IsWin => Pnl > 0m;

        /// <summary>
        /// Equity immediately after the exit
        /// </summary>
        public decimal EquityAfter { get; set; }
    }

    /// <summary>
    /// A marked-to-market snapshot of the account at one point in time.
    ///
    /// The curve of these is the equity curve, and it is the only honest way to show the effect of a
    /// strategy: the number that changes as a result of trading, as opposed to the number that
    /// changes merely because the market moved while flat.
    /// </summary>
    public sealed class EquityPoint
    {
        /// <summary>
        /// Time of the snapshot
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Cash on hand
        /// </summary>
        public decimal Cash { get; set; }

        /// <summary>
        /// Signed market value of open positions at the mark price
        /// </summary>
        public decimal PositionValue { get; set; }

        /// <summary>
        /// Cash plus position value
        /// </summary>
        public decimal Equity { get; set; }

        /// <summary>
        /// Profit and loss on open positions, net of entry fees
        /// </summary>
        public decimal UnrealizedPnl { get; set; }

        /// <summary>
        /// Profit and loss already booked to closed trades
        /// </summary>
        public decimal RealizedPnl { get; set; }

        /// <summary>
        /// Peak equity reached up to this point
        /// </summary>
        public decimal PeakEquity { get; set; }

        /// <summary>
        /// Fall from the peak as a positive fraction, so 0.10 is a 10% drawdown
        /// </summary>
        public decimal DrawdownPct { get; set; }

        /// <summary>
        /// Deepest drawdown seen up to this point
        /// </summary>
        public decimal MaxDrawdownPct { get; set; }

        /// <summary>
        /// Gross notional currently exposed
        /// </summary>
        public decimal GrossExposure { get; set; }

        /// <summary>
        /// Number of positions held at this moment
        /// </summary>
        public int OpenPositions { get; set; }
    }
}
