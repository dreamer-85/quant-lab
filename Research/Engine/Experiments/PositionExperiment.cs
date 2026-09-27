using System.Globalization;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Portfolio;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Turns a signal into an actual traded position and reports what it did to the account.
    ///
    /// This is the experiment that answers the question a research result normally dodges. A
    /// hypothesis reports that a condition held and what the price did next, which measures the
    /// market; it cannot say whether trading the signal made money, because it never holds anything
    /// and never pays a fee. Here the signal opens a position, the position is marked every
    /// observation so the equity curve reflects being in the market, and it is closed on a rule
    /// rather than at the end of the data, so a result does not quietly depend on where the run was
    /// cut.
    ///
    /// Config keys (job.ExperimentConfig):
    ///   entry_condition     required, e.g. "imbalance &lt; -0.50"
    ///   exit_condition      optional; when given, the position closes as soon as it holds
    ///   holding_observations  observations to hold before exiting when there is no exit condition;
    ///                          default 20
    ///   direction           "long" | "short", default "long"
    ///   position_fraction   fraction of equity committed per position, default 0.5
    ///   starting_cash       account size, default 100000
    ///   fee_bps             fee per fill in basis points of notional, default 0
    ///   min_hold_observations  observations to hold regardless, to avoid one-tick noise trades;
    ///                          default 1
    ///   price_field         observation field used as the execution price, default "close"
    ///
    /// Output tables: <c>trades</c> (one row per completed round trip) and <c>portfolio</c> (the
    /// marked equity curve). The main rows are the per-observation position state.
    ///
    /// Deliberately not modelled: intrabar fills, slippage, borrow costs and margin. They are real
    /// and they matter, but they are a separate concern from whether the engine can hold a position
    /// and account for it honestly, and a plausible-looking number built on a wrong identity is worse
    /// than an honest one with its simplifications named.
    /// </summary>
    public sealed class PositionExperiment : ExperimentBase
    {
        private const string TradesTable = "trades";
        private const string PortfolioTable = "portfolio";
        private const string FillsTable = "fills";

        private string _entryConditionText = string.Empty;
        private string _exitConditionText = string.Empty;
        private MeasurementCondition _entryCondition;
        private MeasurementCondition _exitCondition;
        private int _holdingObservations = 20;
        private int _minHoldObservations = 1;
        private decimal _positionFraction = 0.5m;
        private decimal _startingCash = 100_000m;
        private decimal _feeBps;
        private bool _allowLong = true;
        private bool _allowShort;
        private string _priceField = "close";

        private PaperPortfolio _portfolio;
        private string _symbol = string.Empty;
        private int _observationsSinceEntry;
        private bool _inPosition;
        private int _entryCount;
        private int _exitCount;
        private DateTime _lastObservationTime;
        private decimal _lastPrice;

        /// <summary>
        /// The account this experiment traded on
        /// </summary>
        public PaperPortfolio Portfolio => _portfolio;

        public override string Name => "position";

        public override string Description =>
            "Trades a condition as a real position and reports fills, closed trades and the equity curve.";

        public override List<string> RequiredFeatures => new();

        public override void Initialize(ExperimentContext context)
        {
            base.Initialize(context);

            _entryConditionText = context.GetConfig("entry_condition");
            _exitConditionText = context.GetConfig("exit_condition");
            _holdingObservations = Math.Max(1, context.GetConfigInt("holding_observations", 20));
            _minHoldObservations = Math.Max(0, context.GetConfigInt("min_hold_observations", 1));
            _positionFraction = ParsePositive(context.GetConfig("position_fraction", "0.5"), 0.5m);
            _startingCash = ParsePositive(context.GetConfig("starting_cash", "100000"), 100_000m);
            _feeBps = ParseNonNegative(context.GetConfig("fee_bps", "0"));
            _priceField = (context.GetConfig("price_field", "close") ?? "close").Trim().ToLowerInvariant();

            // Reject an unknown price rather than silently trading at close: a typo in the field name
            // would otherwise produce a confident equity curve measured at the wrong price.
            var known = new[] { "close", "last", "last_price", "mid", "mid_price", "bid", "bid_price", "ask", "ask_price" };
            if (!known.Contains(_priceField))
            {
                throw new FormatException(
                    $"position experiment 'price_field' must be one of {string.Join(", ", known)}, got '{_priceField}'.");
            }

            var direction = (context.GetConfig("direction", "long") ?? "long").Trim().ToLowerInvariant();
            if (direction != "long" && direction != "short")
            {
                throw new FormatException(
                    $"position experiment 'direction' must be 'long' or 'short', got '{direction}'.");
            }

            _allowLong = direction == "long";
            _allowShort = direction == "short";

            if (string.IsNullOrWhiteSpace(_entryConditionText))
            {
                throw new FormatException(
                    "position experiment requires 'entry_condition', e.g. \"imbalance < -0.50\".");
            }

            _entryCondition = MeasurementCondition.Parse(_entryConditionText);
            _exitCondition = string.IsNullOrWhiteSpace(_exitConditionText)
                ? null
                : MeasurementCondition.Parse(_exitConditionText);

            _portfolio = new PaperPortfolio(_startingCash, _feeBps);
        }

        public override void OnObservation(Observation observation, FeatureResult features)
        {
            base.OnObservation(observation, features);

            _lastObservationTime = observation.Timestamp;
            var symbol = observation.State?.Symbol?.Value ?? _symbol;
            _symbol = symbol;

            var price = PriceOf(observation, observation.ClosePrice);
            if (price <= 0m)
            {
                // Nothing tradable to mark against: skip rather than trade a price of zero, which
                // would divide the sizing by zero and invent a position of no size.
                return;
            }

            _lastPrice = price;

            var values = features?.Values;
            var entrySignal = _entryCondition.Evaluate(values);
            var exitSignal = _exitCondition != null && _exitCondition.Evaluate(values);

            if (!_inPosition)
            {
                if (entrySignal)
                {
                    OpenPosition(price, observation.Timestamp);
                }
            }
            else
            {
                _observationsSinceEntry++;

                // Mark first, so the equity curve records what the open position was worth on every
                // observation, not only on the ones that trade.
                _portfolio.Mark(observation.Timestamp, symbol, price);

                var heldLongEnough = _observationsSinceEntry >= _minHoldObservations;
                var timeExit = _exitCondition == null && _observationsSinceEntry >= _holdingObservations;
                var conditionExit = _exitCondition != null && exitSignal && heldLongEnough;

                if (conditionExit || timeExit)
                {
                    _portfolio.Execute(symbol, 0m, price, observation.Timestamp);
                    _inPosition = false;
                    _exitCount++;
                }
            }
        }

        public override ExperimentResult Finalize()
        {
            var result = base.Finalize();

            // A position still open at the end of the data has no exit, so its profit or loss would
            // never be realized and the reported return would depend on where the run was truncated.
            // Closing it at the last observed price is what makes two runs comparable.
            if (_inPosition && _lastPrice > 0m)
            {
                _portfolio.Execute(_symbol, 0m, _lastPrice, _lastObservationTime);
                _inPosition = false;
            }

            // The last point on the curve was marked before that final exit, so the account's settled
            // value is not on it yet: without this the curve ends mid-position and disagrees with the
            // ending_equity metric by the value of whatever was still open. Only appended when the
            // curve is genuinely stale, so a run that ended flat gains no duplicate row.
            var lastPoint = _portfolio.EquityCurve.LastOrDefault();
            if (_lastPrice > 0m && (lastPoint == null || lastPoint.OpenPositions > 0))
            {
                _portfolio.Mark(_lastObservationTime, _symbol, _lastPrice);
            }

            result.Rows = BuildPositionRows();
            result.AddNamedRows(TradesTable, BuildTradeRows());
            result.AddNamedRows(PortfolioTable, BuildPortfolioRows());
            result.AddNamedRows(FillsTable, BuildFillRows());

            result.AddMetric("starting_cash", _portfolio.StartingCash);
            result.AddMetric("ending_equity", _portfolio.Equity);
            result.AddMetric("total_return", _portfolio.TotalReturn);
            result.AddMetric("realized_pnl", _portfolio.RealizedPnl);
            result.AddMetric("unrealized_pnl", _portfolio.UnrealizedPnl);
            result.AddMetric("peak_equity", _portfolio.PeakEquity);
            result.AddMetric("max_drawdown", _portfolio.MaxDrawdown);
            result.AddMetric("gross_exposure", _portfolio.GrossExposure);
            result.AddMetric("total_fees", _portfolio.TotalFees);
            result.AddMetric("position_count", _portfolio.ClosedTrades.Count);
            result.AddMetric("fill_count", _portfolio.Fills.Count);
            result.AddMetric("entry_count", _entryCount);
            result.AddMetric("exit_count", _exitCount);
            result.AddMetric("win_rate", _portfolio.WinRate);
            result.AddMetric("net_trade_pnl", _portfolio.RealizedTradePnl);
            result.AddMetric("average_trade_pnl",
                _portfolio.ClosedTrades.Count == 0
                    ? (decimal?)null
                    : _portfolio.RealizedTradePnl / _portfolio.ClosedTrades.Count);
            result.AddMetric("largest_win", _portfolio.ClosedTrades.Count == 0
                ? (decimal?)null
                : _portfolio.ClosedTrades.Max(t => t.Pnl));
            result.AddMetric("largest_loss", _portfolio.ClosedTrades.Count == 0
                ? (decimal?)null
                : _portfolio.ClosedTrades.Min(t => t.Pnl));

            result.Metadata["entry_condition"] = _entryConditionText;
            result.Metadata["exit_condition"] = _exitConditionText;
            result.Metadata["direction"] = _allowShort ? "short" : "long";
            result.Metadata["holding_observations"] = _holdingObservations.ToString(CultureInfo.InvariantCulture);
            result.Metadata["position_fraction"] = _positionFraction.ToString(CultureInfo.InvariantCulture);
            result.Metadata["fee_bps"] = _feeBps.ToString(CultureInfo.InvariantCulture);
            result.Metadata["price_field"] = _priceField;
            result.Metadata["equity_identity"] =
                "equity - starting_cash == realized_pnl + unrealized_pnl";

            return result;
        }

        public override void Reset()
        {
            base.Reset();
            _portfolio = null;
            _inPosition = false;
            _observationsSinceEntry = 0;
            _entryCount = 0;
            _exitCount = 0;
            _lastObservationTime = default;
            _lastPrice = 0m;
        }

        private void OpenPosition(decimal price, DateTime timestamp)
        {
            var quantity = _portfolio.SizeForFraction(_positionFraction, price);
            if (quantity == 0m)
            {
                // Not enough capital to commit anything meaningful at this price; standing aside is
                // the honest response and is recorded as no trade rather than a dust fill.
                return;
            }

            var signed = _allowShort ? -quantity : quantity;
            _portfolio.Execute(_symbol, signed, price, timestamp);
            _portfolio.Mark(timestamp, _symbol, price);
            _inPosition = true;
            _observationsSinceEntry = 0;
            _entryCount++;
        }

        private decimal PriceOf(Observation observation, decimal fallback)
        {
            var state = observation.State;
            return _priceField switch
            {
                "last" or "last_price" => state?.LastPrice ?? 0m,
                "mid" or "mid_price" => state?.MidPrice ?? 0m,
                "bid" or "bid_price" => state?.BidPrice ?? 0m,
                "ask" or "ask_price" => state?.AskPrice ?? 0m,
                // "close" is the last event's price, but an observation carrying no events reports a
                // close of zero. Falling back to the maintained last price keeps a quiet period
                // markable instead of silently untradeable.
                _ => fallback > 0m ? fallback : state?.LastPrice ?? 0m
            };
        }

        private List<Dictionary<string, object>> BuildPositionRows()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var point in _portfolio.EquityCurve)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["timestamp"] = point.Timestamp,
                    ["symbol"] = _symbol,
                    ["cash"] = point.Cash,
                    ["position_value"] = point.PositionValue,
                    ["equity"] = point.Equity,
                    ["unrealized_pnl"] = point.UnrealizedPnl,
                    ["realized_pnl"] = point.RealizedPnl,
                    ["drawdown"] = point.DrawdownPct,
                    ["gross_exposure"] = point.GrossExposure,
                    ["open_positions"] = point.OpenPositions
                });
            }

            return rows;
        }

        private List<Dictionary<string, object>> BuildTradeRows()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var trade in _portfolio.ClosedTrades)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["symbol"] = trade.Symbol,
                    ["side"] = trade.Side.ToString(),
                    ["entry_time"] = trade.EntryTime,
                    ["exit_time"] = trade.ExitTime,
                    ["entry_price"] = trade.EntryPrice,
                    ["exit_price"] = trade.ExitPrice,
                    ["quantity"] = trade.Quantity,
                    ["holding_period_minutes"] = trade.HoldingPeriod.TotalMinutes,
                    ["pnl"] = trade.Pnl,
                    ["fees"] = trade.Fees,
                    ["return_pct"] = trade.ReturnPct,
                    ["is_win"] = trade.IsWin,
                    ["equity_after"] = trade.EquityAfter
                });
            }

            return rows;
        }

        private List<Dictionary<string, object>> BuildPortfolioRows()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var point in _portfolio.EquityCurve)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["timestamp"] = point.Timestamp,
                    ["equity"] = point.Equity,
                    ["cash"] = point.Cash,
                    ["position_value"] = point.PositionValue,
                    ["realized_pnl"] = point.RealizedPnl,
                    ["unrealized_pnl"] = point.UnrealizedPnl,
                    ["peak_equity"] = point.PeakEquity,
                    ["drawdown"] = point.DrawdownPct,
                    ["max_drawdown"] = point.MaxDrawdownPct,
                    ["gross_exposure"] = point.GrossExposure,
                    ["open_positions"] = point.OpenPositions
                });
            }

            return rows;
        }

        private List<Dictionary<string, object>> BuildFillRows()
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var fill in _portfolio.Fills)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["timestamp"] = fill.Timestamp,
                    ["symbol"] = fill.Symbol,
                    ["quantity"] = fill.SignedQuantity,
                    ["price"] = fill.Price,
                    ["notional"] = fill.Notional,
                    ["fee"] = fill.Fee,
                    ["realized_pnl"] = fill.RealizedPnl,
                    ["reason"] = fill.Reason.ToString(),
                    ["cash_after"] = fill.CashAfter,
                    ["position_after"] = fill.PositionAfter,
                    ["average_entry_after"] = fill.AverageEntryAfter,
                    ["equity_after"] = fill.EquityAfter
                });
            }

            return rows;
        }

        private static decimal ParsePositive(string text, decimal fallback)
        {
            if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                value > 0m)
            {
                return value;
            }

            return fallback;
        }

        private static decimal ParseNonNegative(string text)
        {
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                   value >= 0m
                ? value
                : 0m;
        }
    }
}
