namespace QuantConnect.Research.Engine.Portfolio
{
    /// <summary>
    /// A position in one symbol, aggregated across fills.
    ///
    /// A position is the thing a trader actually holds, as opposed to a signal. It only exists
    /// between a fill that opened (or added to) it and a fill that reduced (or closed) it, and its
    /// lifetime is what makes a trade measurable: a trade is the round trip from opening a position
    /// to flattening it, with the entry and exit both recorded as fills.
    ///
    /// Quantity is carried signed (positive long, negative short) so that moving from +10 to -10 is
    /// a close of 10 and an open of 10 rather than a single opaque 20-lot trade, which is how a real
    /// order management system accounts for it. Direction is reported as Lean's
    /// <see cref="PositionSide"/>, whose <c>None</c> means flat, rather than a second enum for the
    /// same concept.
    /// </summary>
    public sealed class Position
    {
        /// <summary>
        /// Symbol held
        /// </summary>
        public string Symbol { get; }

        /// <summary>
        /// Signed quantity: positive when long, negative when short
        /// </summary>
        public decimal SignedQuantity { get; internal set; }

        /// <summary>
        /// Side implied by <see cref="SignedQuantity"/>, <see cref="PositionSide.None"/> when flat
        /// </summary>
        public PositionSide Side =>
            SignedQuantity > 0m ? PositionSide.Long
                : SignedQuantity < 0m ? PositionSide.Short
                : PositionSide.None;

        /// <summary>
        /// Whether no position is held
        /// </summary>
        public bool IsFlat => SignedQuantity == 0m;

        /// <summary>
        /// Quantity on the long side of the position, always positive
        /// </summary>
        public decimal Quantity => Math.Abs(SignedQuantity);

        /// <summary>
        /// Volume-weighted average entry price across the fills currently making up the position.
        /// Reducing a position leaves the average entry unchanged; only adding to it re-averages.
        /// </summary>
        public decimal AverageEntryPrice { get; internal set; }

        /// <summary>
        /// Time of the first fill that opened this position, i.e. the entry of the trade
        /// </summary>
        public DateTime EntryTime { get; internal set; }

        /// <summary>
        /// Time of the most recent fill that changed the quantity
        /// </summary>
        public DateTime LastFillTime { get; internal set; }

        /// <summary>
        /// Total fees paid on the fills currently making up this position, netted against a
        /// proportional share when the position is reduced
        /// </summary>
        public decimal FeesPaid { get; internal set; }

        /// <summary>
        /// Realized profit and loss booked to this symbol, net of fees
        /// </summary>
        public decimal RealizedPnl { get; internal set; }

        /// <summary>
        /// Creates a flat position for the given symbol
        /// </summary>
        public Position(string symbol)
        {
            Symbol = symbol;
        }

        /// <summary>
        /// Unrealized profit and loss at the given mark price
        /// </summary>
        public decimal UnrealizedPnl(decimal mark)
        {
            if (Side == PositionSide.None)
            {
                return 0m;
            }

            return SignedQuantity * (mark - AverageEntryPrice);
        }

        /// <summary>
        /// Signed market value at the given mark price. A short is a liability, so it is negative:
        /// equity is cash plus this, which is the balance sheet identity and is what makes
        /// equity - starting cash equal realized + unrealized for any sequence of fills.
        /// </summary>
        public decimal MarketValue(decimal mark) => SignedQuantity * mark;
    }
}
