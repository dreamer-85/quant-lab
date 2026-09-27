using System.Globalization;

namespace QuantConnect.Research.Engine.Portfolio
{
    /// <summary>
    /// A self-contained paper trading account: it takes signed order quantities and prices, and
    /// produces positions, fills, closed trades, and a marked equity curve.
    ///
    /// It exists because a signal is not a trade. The engine previously recorded that a condition held
    /// and what the price did afterwards, which measures the market but not the strategy: it has no
    /// position, no entry or exit price, and no equity. This is where that missing half lives, and it
    /// is deliberately a plain accounting model rather than a brokerage simulation:
    ///
    ///   - one account, cash and short/margin in the same pool, no margin calls or borrow costs
    ///   - fills at the price given, with no slippage or partial-fill probability
    ///   - fees as a basis-point rate on notional, applied per fill
    ///   - equity is the balance sheet identity: cash + signed market value
    ///
    /// That identity is the property worth protecting. It means equity - starting cash always equals
    /// realized + unrealized profit, so the equity curve and the trade list can never disagree about
    /// how much money was made, and it holds for any sequence of partials and reversals rather than
    /// only for the simple open-then-close case.
    /// </summary>
    public sealed class PaperPortfolio
    {
        private const decimal BasisPoints = 10_000m;

        private readonly Dictionary<string, Position> _positions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> _marks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Fill> _fills = new();
        private readonly List<ClosedTrade> _trades = new();
        private readonly List<EquityPoint> _curve = new();
        private readonly int _maxCurvePoints;

        private decimal _cash;
        private decimal _realized;
        private decimal _peakEquity;
        private decimal _maxDrawdown;
        private bool _hasPeak;
        private int _decimation;

        // Tracks the fill that opened the position currently being built, so that a partial close can
        // be turned back into a complete round trip when the position finally flattens.
        private readonly Dictionary<string, PendingTrade> _openTrades = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates a paper account
        /// </summary>
        /// <param name="startingCash">Cash the account starts with</param>
        /// <param name="feeBps">Fee rate in basis points of notional, charged on every fill</param>
        /// <param name="maxCurvePoints">
        /// Soft cap on retained equity points. Beyond it the curve is halved in resolution so a long
        /// run keeps its shape without growing without bound. Defaults to keeping every point.
        /// </param>
        public PaperPortfolio(decimal startingCash = 100_000m, decimal feeBps = 0m, int maxCurvePoints = int.MaxValue)
        {
            if (startingCash <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(startingCash), startingCash,
                    "Starting cash must be positive.");
            }

            if (feeBps < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(feeBps), feeBps,
                    "Fee rate cannot be negative.");
            }

            if (maxCurvePoints < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(maxCurvePoints), maxCurvePoints,
                    "Retain at least two equity points to draw a curve.");
            }

            StartingCash = startingCash;
            FeeBps = feeBps;
            _maxCurvePoints = maxCurvePoints;
            _cash = startingCash;
        }

        /// <summary>
        /// Cash the account started with
        /// </summary>
        public decimal StartingCash { get; }

        /// <summary>
        /// Fee rate in basis points of notional charged on every fill
        /// </summary>
        public decimal FeeBps { get; }

        /// <summary>
        /// Cash on hand
        /// </summary>
        public decimal Cash => _cash;

        /// <summary>
        /// Every fill executed against this account, in order
        /// </summary>
        public IReadOnlyList<Fill> Fills => _fills;

        /// <summary>
        /// Complete round trips, in the order they closed
        /// </summary>
        public IReadOnlyList<ClosedTrade> ClosedTrades => _trades;

        /// <summary>
        /// Marked equity curve
        /// </summary>
        public IReadOnlyList<EquityPoint> EquityCurve => _curve;

        /// <summary>
        /// Positions currently held
        /// </summary>
        public IReadOnlyCollection<Position> Positions => _positions.Values;

        /// <summary>
        /// Equity with open positions marked at the last seen price
        /// </summary>
        public decimal Equity => _cash + PositionValue;

        /// <summary>
        /// Signed market value of open positions. Negative when net short.
        /// </summary>
        public decimal PositionValue
        {
            get
            {
                var total = 0m;
                foreach (var position in _positions.Values)
                {
                    if (position.Side == PositionSide.None)
                    {
                        continue;
                    }

                    if (!_marks.TryGetValue(position.Symbol, out var mark))
                    {
                        // Never marked: fall back to entry so an unpriced position does not read as
                        // a gain or a loss of nothing.
                        mark = position.AverageEntryPrice;
                    }

                    total += position.MarketValue(mark);
                }

                return total;
            }
        }

        /// <summary>
        /// Profit and loss on open positions, net of entry fees
        /// </summary>
        public decimal UnrealizedPnl
        {
            get
            {
                var total = 0m;
                foreach (var position in _positions.Values)
                {
                    if (position.Side == PositionSide.None)
                    {
                        continue;
                    }

                    var mark = _marks.TryGetValue(position.Symbol, out var m) ? m : position.AverageEntryPrice;
                    total += position.UnrealizedPnl(mark);
                }

                return total;
            }
        }

        /// <summary>
        /// Realized profit and loss booked so far, net of every fee charged, including fees on
        /// positions that are still open. Slicing this running total, rather than summing closed
        /// trades, is what keeps a trade's P&L exact even when fees were charged while it was open.
        /// </summary>
        public decimal RealizedPnl => _realized;

        /// <summary>
        /// Realized profit and loss that has been converted into a completed round trip
        /// </summary>
        public decimal RealizedTradePnl => _trades.Sum(t => t.Pnl);

        /// <summary>
        /// Total fees charged across every fill
        /// </summary>
        public decimal TotalFees => _fills.Sum(f => f.Fee);

        /// <summary>
        /// Gross notional currently exposed, ignoring direction
        /// </summary>
        public decimal GrossExposure => _positions.Values
            .Where(p => p.Side != PositionSide.None)
            .Sum(p => p.Quantity * (MarkOf(p.Symbol)));

        /// <summary>
        /// Total return since the start of the run as a fraction of starting cash
        /// </summary>
        public decimal TotalReturn => (Equity - StartingCash) / StartingCash;

        /// <summary>
        /// Deepest fall from a prior equity peak, as a positive fraction
        /// </summary>
        public decimal MaxDrawdown => _maxDrawdown;

        /// <summary>
        /// Peak equity reached so far
        /// </summary>
        public decimal PeakEquity => _hasPeak ? _peakEquity : StartingCash;

        /// <summary>
        /// Fraction of closed trades that ended with a profit
        /// </summary>
        public decimal? WinRate
        {
            get
            {
                if (_trades.Count == 0)
                {
                    return null;
                }

                return (decimal)_trades.Count(t => t.IsWin) / _trades.Count;
            }
        }

        /// <summary>
        /// Size an order by committing a fraction of current equity to it, given a price.
        ///
        /// This is the sizing a strategy almost always wants, and spelling it out here keeps every
        /// strategy from inventing its own, subtly different, percentage of something.
        /// </summary>
        /// <param name="fraction">Fraction of equity to commit, e.g. 0.5m for half the account</param>
        /// <param name="price">Price the order would execute at</param>
        /// <param name="leverage">Notional multiple of committed capital. 1m is unlevered.</param>
        public decimal SizeForFraction(decimal fraction, decimal price, decimal leverage = 1m)
        {
            if (fraction <= 0m)
            {
                return 0m;
            }

            if (price <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(price), price, "Price must be positive.");
            }

            var notional = Math.Max(0m, Equity) * fraction * leverage;
            return RoundQuantity(notional / price);
        }

        /// <summary>
        /// Execute an order, moving the target position to a signed quantity.
        ///
        /// The target is expressed as a position rather than a delta, so the caller does not have to
        /// track what it already holds to know whether this order opens, adds, trims or flips. Every
        /// path through here produces fills whose cash effects sum exactly to the change in equity.
        /// </summary>
        /// <param name="symbol">Symbol to trade</param>
        /// <param name="signedQuantity">
        /// Target position size: positive to be long, negative to be short, zero to flatten. A value
        /// between the current position and flat trims it.
        /// </param>
        /// <param name="price">Execution price</param>
        /// <param name="timestamp">Execution time</param>
        /// <param name="feeBps">Fee rate override in basis points; null uses the account rate</param>
        /// <returns>The fills produced, which is two for a reversal and empty for a no-op</returns>
        public IReadOnlyList<Fill> Execute(
            string symbol,
            decimal signedQuantity,
            decimal price,
            DateTime timestamp,
            decimal? feeBps = null)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol is required.", nameof(symbol));
            }

            if (price <= 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(price), price,
                    "Price must be positive; a zero or negative mark cannot be traded.");
            }

            _marks[symbol] = price;

            var current = PositionOf(symbol).SignedQuantity;
            if (signedQuantity == current)
            {
                // Already at the target. Emitting a fill would invent a trade and a fee, so this is
                // explicitly a no-op rather than a zero-quantity fill.
                return Array.Empty<Fill>();
            }

            var rate = feeBps ?? FeeBps;
            if (rate < 0m)
            {
                throw new ArgumentOutOfRangeException(nameof(rate), rate, "Fee rate cannot be negative.");
            }

            var produced = new List<Fill>(2);

            // A flip passes through flat: close what is held, then open the other side. Reporting it
            // as one net fill would hide the realized P&L of the trade that ended.
            if (current != 0m && signedQuantity != 0m && Math.Sign(current) != Math.Sign(signedQuantity))
            {
                produced.Add(ApplyFill(symbol, 0m, price, timestamp, rate, FillReason.Close));
                produced.Add(ApplyFill(symbol, signedQuantity, price, timestamp, rate, FillReason.Reverse));
                return produced;
            }

            var sameDirection = current != 0m && Math.Sign(current) == Math.Sign(signedQuantity);
            var increasing = sameDirection && Math.Abs(signedQuantity) > Math.Abs(current);
            var reducing = sameDirection && Math.Abs(signedQuantity) < Math.Abs(current);

            var reason = current == 0m ? FillReason.Open
                : increasing ? FillReason.Increase
                : reducing ? FillReason.Reduce
                : FillReason.Close;

            produced.Add(ApplyFill(symbol, signedQuantity, price, timestamp, rate, reason));
            return produced;
        }

        /// <summary>
        /// Update the mark price for a symbol and record an equity point.
        ///
        /// Called once per observation, which is what makes the equity curve a record of what the
        /// account was worth at every point in the run rather than only at the moments it traded.
        /// </summary>
        public EquityPoint Mark(DateTime timestamp, string symbol = null, decimal? price = null)
        {
            if (price.HasValue)
            {
                if (price.Value <= 0m)
                {
                    throw new ArgumentOutOfRangeException(nameof(price), price.Value,
                        "Mark price must be positive.");
                }

                if (string.IsNullOrWhiteSpace(symbol))
                {
                    throw new ArgumentException("A symbol is required when marking a price.", nameof(symbol));
                }

                _marks[symbol] = price.Value;
            }

            var equity = Equity;

            if (!_hasPeak || equity > _peakEquity)
            {
                _peakEquity = equity;
                _hasPeak = true;
            }

            var drawdown = _peakEquity > 0m ? (_peakEquity - equity) / _peakEquity : 0m;
            if (drawdown > _maxDrawdown)
            {
                _maxDrawdown = drawdown;
            }

            var point = new EquityPoint
            {
                Timestamp = timestamp,
                Cash = _cash,
                PositionValue = PositionValue,
                Equity = equity,
                UnrealizedPnl = UnrealizedPnl,
                RealizedPnl = RealizedPnl,
                PeakEquity = _peakEquity,
                DrawdownPct = drawdown,
                MaxDrawdownPct = _maxDrawdown,
                GrossExposure = GrossExposure,
                OpenPositions = _positions.Values.Count(p => p.Side != PositionSide.None)
            };

            _curve.Add(point);
            CompactCurve();
            return point;
        }

        /// <summary>
        /// Flatten every open position at the given price, as a run would at its end.
        ///
        /// Without this an open position at the end of a run is unrealized forever and the reported
        /// return depends on where the data was cut, which makes two runs of the same strategy
        /// incomparable.
        /// </summary>
        public IReadOnlyList<Fill> LiquidateAll(DateTime timestamp, string symbol = null, decimal? price = null)
        {
            var targets = new List<Position>();
            foreach (var position in _positions.Values)
            {
                if (position.Side == PositionSide.None)
                {
                    continue;
                }

                if (symbol != null && !string.Equals(symbol, position.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                targets.Add(position);
            }

            if (targets.Count == 0)
            {
                return Array.Empty<Fill>();
            }

            var produced = new List<Fill>();
            foreach (var position in targets)
            {
                var mark = price ?? MarkOf(position.Symbol);
                produced.AddRange(Execute(position.Symbol, 0m, mark, timestamp));
            }

            return produced;
        }

        private Fill ApplyFill(
            string symbol,
            decimal targetQuantity,
            decimal price,
            DateTime timestamp,
            decimal rate,
            FillReason reason)
        {
            var position = PositionOf(symbol);
            var realizedBeforeFill = _realized;
            var current = position.SignedQuantity;
            var delta = targetQuantity - current;
            var quantity = Math.Abs(delta);
            var notional = quantity * price;
            var fee = RoundMoney(notional * rate / BasisPoints);
            var sign = Math.Sign(delta);

            var realized = 0m;

            // A single cash rule covers every case: the signed delta is the side of the trade, so
            // buying a long or covering a short pays out, and selling a short or selling a long
            // receives. Reversing therefore needs no special handling here either.
            var cashDelta = -sign * notional - fee;

            if (reason != FillReason.Open && reason != FillReason.Increase && reason != FillReason.Reverse)
            {
                // Closing or trimming: the part of the position being sold carries profit or loss
                // against its own average entry.
                var closingQuantity = Math.Min(quantity, position.Quantity);
                var perUnit = position.Side == PositionSide.Long
                    ? price - position.AverageEntryPrice
                    : position.AverageEntryPrice - price;
                realized = RoundMoney(closingQuantity * perUnit);
            }

            // Fees are a realized cost the moment they are charged, on entry as well as on exit.
            // Attributing them to the position instead would leave the account holding a cost that
            // is in no trade and no P&L figure, and the balance sheet identity
            // (equity - starting == realized + unrealized) would fail by exactly the entry fees.
            _realized = RoundMoney(_realized + realized - fee);

            _cash = RoundMoney(_cash + cashDelta);

            position.FeesPaid = RoundMoney(position.FeesPaid + fee);

            var wasFlat = current == 0m;
            var previousEntry = position.AverageEntryPrice;
            var previousQuantity = Math.Abs(current);
            var newQuantity = Math.Abs(targetQuantity);

            position.SignedQuantity = targetQuantity;

            if (wasFlat)
            {
                position.AverageEntryPrice = price;
                position.EntryTime = timestamp;
            }
            else if (reason == FillReason.Increase)
            {
                // Re-average only when adding. A trim must leave the entry alone, otherwise the
                // remaining part of the original trade would be re-based and its P&L rewritten.
                var carriedCost = previousEntry * previousQuantity;
                position.AverageEntryPrice = RoundPrice((carriedCost + notional) / newQuantity);
            }

            position.LastFillTime = timestamp;

            if (targetQuantity == 0m)
            {
                BookClosedTrade(position, price, timestamp, fee);
                position.AverageEntryPrice = 0m;
                position.FeesPaid = 0m;
            }
            else
            {
                TrackOpenTrade(position, reason, price, fee, realizedBeforeFill);
            }

            var fill = new Fill
            {
                Timestamp = timestamp,
                Symbol = symbol,
                SignedQuantity = delta,
                Price = price,
                Fee = fee,
                RealizedPnl = RoundMoney(realized - fee),
                Reason = reason,
                CashDelta = cashDelta,
                CashAfter = _cash,
                PositionAfter = position.SignedQuantity,
                AverageEntryAfter = position.AverageEntryPrice
            };

            fill.EquityAfter = _cash + PositionValue;
            _fills.Add(fill);
            return fill;
        }

        /// <summary>
        /// Close out the pending round trip for a symbol now that its position has flattened.
        /// </summary>
        private void BookClosedTrade(
            Position position,
            decimal exitPrice,
            DateTime timestamp,
            decimal feeThisFill)
        {
            if (!_openTrades.TryGetValue(position.Symbol, out var pending))
            {
                return;
            }

            _openTrades.Remove(position.Symbol);

            // The round trip's P&L is everything booked to this symbol while the trade was open, not
            // just this final fill: the entry fee, any partial exits and the exit fee all belong to
            // it, and taking a slice of the running total is what guarantees the trades sum back to
            // the change in equity.
            var trade = new ClosedTrade
            {
                Symbol = position.Symbol,
                Side = pending.Side,
                EntryTime = pending.EntryTime,
                ExitTime = timestamp,
                EntryPrice = pending.EntryPrice,
                ExitPrice = exitPrice,
                Quantity = pending.PeakQuantity,
                Fees = pending.FeesPaid + feeThisFill,
                Pnl = RoundMoney(_realized - pending.RealizedAtOpen),
                EquityAfter = _cash + PositionValue
            };

            var committed = pending.PeakQuantity * pending.EntryPrice;
            if (committed != 0m)
            {
                trade.ReturnPct = trade.Pnl / committed;
            }

            _trades.Add(trade);
        }

        private void TrackOpenTrade(
            Position position,
            FillReason reason,
            decimal price,
            decimal fee,
            decimal realizedBeforeFill)
        {
            var side = position.Side;
            if (reason == FillReason.Open || reason == FillReason.Reverse)
            {
                _openTrades[position.Symbol] = new PendingTrade
                {
                    Side = side,
                    EntryTime = position.EntryTime,
                    EntryPrice = price,
                    PeakQuantity = position.Quantity,
                    FeesPaid = fee,
                    // The running total as it stood before this fill was charged, so the slice taken
                    // on close covers the entry fee and every exit of the whole trade.
                    RealizedAtOpen = realizedBeforeFill
                };
                return;
            }

            if (reason == FillReason.Increase && _openTrades.TryGetValue(position.Symbol, out var pending))
            {
                pending.FeesPaid += fee;
                pending.PeakQuantity = Math.Max(pending.PeakQuantity, position.Quantity);
            }
        }

        /// <summary>
        /// The account's position in one instrument, creating a flat one if this is the first
        /// time the instrument has been touched. Public so a strategy driving this portfolio can
        /// read its own position without maintaining a second copy of the truth.
        /// </summary>
        public Position PositionOf(string symbol)
        {
            if (!_positions.TryGetValue(symbol, out var position))
            {
                position = new Position(symbol);
                _positions[symbol] = position;
            }

            return position;
        }

        private decimal MarkOf(string symbol) =>
            _marks.TryGetValue(symbol, out var mark)
                ? mark
                : _positions.TryGetValue(symbol, out var position) ? position.AverageEntryPrice : 0m;

        /// <summary>
        /// Halve the resolution of the retained curve once it outgrows its cap, keeping the shape
        /// rather than the tail. Without this a long live run grows one row per observation forever.
        /// </summary>
        private void CompactCurve()
        {
            if (_curve.Count <= _maxCurvePoints)
            {
                return;
            }

            var compacted = new List<EquityPoint>((_curve.Count + 1) / 2);
            for (var i = 0; i < _curve.Count; i += 2)
            {
                compacted.Add(_curve[i]);
            }

            // Always keep the newest point, otherwise a chart ends before the present.
            if (compacted[compacted.Count - 1] != _curve[_curve.Count - 1])
            {
                compacted.Add(_curve[_curve.Count - 1]);
            }

            _curve.Clear();
            _curve.AddRange(compacted);
            _decimation++;
        }

        /// <summary>
        /// Number of observations folded into each retained curve point, 1 before any compaction
        /// </summary>
        public int CurveDecimation => 1 << _decimation;

        private static decimal RoundMoney(decimal value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

        private static decimal RoundPrice(decimal value) => Math.Round(value, 10, MidpointRounding.AwayFromZero);

        private static decimal RoundQuantity(decimal value) => Math.Round(value, 8, MidpointRounding.AwayFromZero);

        private sealed class PendingTrade
        {
            public PositionSide Side { get; set; }
            public DateTime EntryTime { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal PeakQuantity { get; set; }
            public decimal FeesPaid { get; set; }
            public decimal RealizedAtOpen { get; set; }
        }
    }
}
