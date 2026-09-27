using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments.Python;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Portfolio;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Runs a Python strategy script that can actually trade, and writes one row per observation.
    ///
    /// This exists because <see cref="PythonStrategyExperiment"/> can only record what a script
    /// returns: its <c>on_observation</c> hook yields a row for the output and nothing else, so a
    /// script could watch a market, decide to trade, and have the decision vanish. Everything a
    /// strategy needs in order to be a strategy — sizing, execution, an account — had to be
    /// rebuilt in Python around the engine, which is a second implementation of the same
    /// accounting and drifts from it.
    ///
    /// So the script keeps the part only it can do: decide. This experiment owns the part the
    /// account has to own, and gives the script three things it did not have:
    ///
    ///   1. <b>Execution.</b> Return <c>{"order": {"action": "buy"}}</c> from
    ///      <c>on_observation</c> and it fills against a <see cref="PaperPortfolio"/>, at the
    ///      run's configured price, paying the run's configured fee.
    ///   2. <b>Sizing, either way.</b> Include <c>"quantity"</c> and the script has sized the
    ///      order itself; omit it and the account uses the size configured for the run. A script
    ///      that only decides buy-or-not never has to think about sizing, and one that manages
    ///      risk dynamically can still do it.
    ///   3. <b>State.</b> The account's cash, position and P&amp;L are readable from
    ///      <c>observation["account"]</c> at every tick, so a script can size against real equity
    ///      rather than assuming it.
    ///
    /// The main output is one row per observation, which is the point of this experiment: every
    /// tick carries the data the script saw, the decision it made, and what the account did about
    /// it, in the same row. A developer reading the output should be able to reconstruct their own
    /// reasoning from it without re-running anything. Tables for closed trades, the equity curve
    /// and raw fills are emitted alongside.
    ///
    /// Deliberately not modelled: intrabar fills, slippage, borrow, margin. See
    /// <see cref="PositionExperiment"/> for why they are named rather than approximated.
    /// </summary>
    public sealed class ScriptTradeExperiment : ExperimentBase
    {
        private const string TradesTable = "trades";
        private const string PortfolioTable = "portfolio";
        private const string FillsTable = "fills";
        private const string DecisionsTable = "decisions";

        /// <summary>
        /// Data points written on every observation row. This is the "what did the market look
        /// like" half of the row, and it is fixed rather than script-defined so the output stays
        /// comparable between runs and between scripts. Anything else a script wants is added on
        /// top by the script's own returned row.
        /// </summary>
        private static readonly string[] StandardFields =
        {
            "bid_price", "ask_price", "last_price", "mid_price", "spread_bps",
            "bid_depth", "ask_depth", "depth", "imbalance", "depth_ratio",
            "trade_flow", "trade_volume", "vwap", "close_price"
        };

        private readonly ResearchJob _job;
        private readonly Func<IPythonStrategyHost> _hostFactory;
        private IPythonStrategyHost _host;
        private string _scriptPath = string.Empty;

        private readonly List<Dictionary<string, object>> _rows = new();
        private readonly List<Dictionary<string, object>> _trades = new();
        private readonly List<Dictionary<string, object>> _equity = new();
        private readonly List<Dictionary<string, object>> _fills = new();
        private readonly List<Dictionary<string, object>> _decisions = new();
        private readonly Dictionary<string, object> _metrics = new();
        private readonly Dictionary<string, string> _metadata = new();

        /// <summary>
        /// One account per instrument, keyed "<c>venue:symbol</c>". A single PaperPortfolio is
        /// used for the run, but positions are already keyed by that string, so a script trading
        /// two venues in one run keeps the two books apart while sharing one cash balance.
        /// </summary>
        private PaperPortfolio _portfolio;

        private readonly List<ClosedTrade> _closed = new();

        private string _priceField = "mid";
        private decimal _feeBps = 5m;
        private decimal _startingCash = 100000m;
        private decimal _orderQuantity;
        private decimal _positionFraction;
        private bool _emitEveryObservation = true;
        private long _orderCount;
        private long _rejectedOrderCount;
        private DateTime _lastObservationTime;

        public ScriptTradeExperiment(ResearchJob job, Func<IPythonStrategyHost> hostFactory = null)
            : base(maxRetainedObservations: 0, maxRetainedOutcomes: 0)
        {
            _job = job;
            _hostFactory = hostFactory;
        }

        public override string Name => "script_trade";

        public override string Description =>
            "Python strategy script that places orders, with one output row per observation";

        public override string Version => "1.0.0";

        public override List<string> RequiredFeatures => new();

        public override void Initialize(ExperimentContext context)
        {
            base.Initialize(context);

            // ---- script location -------------------------------------------------
            _scriptPath = context.GetConfig("script");
            if (string.IsNullOrWhiteSpace(_scriptPath))
            {
                _scriptPath = _job?.StrategyScript ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(_scriptPath))
            {
                throw new FormatException(
                    "script_trade needs a Python script. Set job 'strategyScript', or the " +
                    "experimentConfig key 'script'.");
            }

            // ---- price -----------------------------------------------------------
            _priceField = (context.GetConfig("price_field", "mid") ?? "mid").Trim().ToLowerInvariant();
            var knownPrices = new[]
            {
                "close", "close_price", "last", "last_price", "mid", "mid_price",
                "bid", "bid_price", "ask", "ask_price"
            };
            if (!knownPrices.Contains(_priceField))
            {
                // Fill at the wrong price is worse than refuse to run: a typo here produces a
                // confident equity curve that is simply measuring the wrong thing.
                throw new FormatException(
                    $"script_trade 'price_field' must be one of {string.Join(", ", knownPrices)}, " +
                    $"got '{_priceField}'.");
            }

            // ---- money -----------------------------------------------------------
            _feeBps = ParseDecimal(context.GetConfig("fee_bps", "5"), "fee_bps", 5m);
            if (_feeBps < 0m)
            {
                throw new FormatException($"script_trade 'fee_bps' cannot be negative, got {_feeBps}.");
            }

            _startingCash = ParseDecimal(context.GetConfig("starting_cash", "100000"), "starting_cash", 100000m);
            if (_startingCash <= 0m)
            {
                throw new FormatException($"script_trade 'starting_cash' must be positive, got {_startingCash}.");
            }

            // Build the account from the run's own cash and fee. A settle at the end of a run
            // goes through the portfolio rather than through this class, so a fee passed only
            // to Execute() leaves the closing trade free -- which quietly flatters every run
            // that ends holding something.
            _portfolio = new PaperPortfolio(_startingCash, _feeBps);

            // Sizing the account applies when a script does not size its own order. The two are
            // alternatives, not a blend: if the script ever sends a quantity, that is the size.
            _orderQuantity = ParseDecimal(context.GetConfig("order_quantity", "0"), "order_quantity", 0m);
            _positionFraction = ParseDecimal(context.GetConfig("position_fraction", "0.5"),
                "position_fraction", 0.5m);
            if (_orderQuantity < 0m)
            {
                throw new FormatException(
                    $"script_trade 'order_quantity' cannot be negative, got {_orderQuantity}. " +
                    "Use a positive size; the order's action decides the direction.");
            }

            if (_positionFraction < 0m || _positionFraction > 1m)
            {
                throw new FormatException(
                    $"script_trade 'position_fraction' must be between 0 and 1, got {_positionFraction}.");
            }

            if (_orderQuantity == 0m && _positionFraction == 0m)
            {
                throw new FormatException(
                    "script_trade has no way to size an order the script does not size itself: " +
                    "set 'order_quantity' to a base-unit amount, or 'position_fraction' to a " +
                    "fraction of equity. Sending orders with neither would silently do nothing.");
            }

            _emitEveryObservation = ParseBool(context.GetConfig("emit_rows", "true"), "emit_rows", true);

            var host = (_hostFactory ?? (() => new PythonNetStrategyHost()))();
            _host = host;
            host.Initialize(_scriptPath, BuildContext(context));
        }

        public override void OnObservation(Observation observation, FeatureResult features)
        {
            if (_host == null)
            {
                throw new InvalidOperationException("ScriptTradeExperiment.OnObservation called before Initialize.");
            }

            _lastObservationTime = observation.Timestamp;
            var closedBefore = 0;
            var instrument = InstrumentKey(observation);
            var price = PriceOf(observation);
            if (price <= 0m)
            {
                // No tradable price this tick. The row is still worth writing: a skipped
                // observation is a fact about the data, and hiding it makes a gap in the output
                // indistinguishable from a gap in the market.
                _portfolio.Mark(observation.Timestamp, instrument, null);
                if (_emitEveryObservation)
                {
                    _rows.Add(BuildObservationRow(observation, features, instrument, null, price,
                        "no tradable price this observation", false));
                }

                return;
            }

            var scriptRow = _host.OnObservation(BuildObservation(observation, features, instrument), BuildFeatures(features));

            var order = StrategyOrder.FromRow(scriptRow);
            var note = string.Empty;
            var traded = false;

            if (order != null)
            {
                (var target, var sizedBy) = ResolveTarget(order, instrument, price);
                if (target.HasValue)
                {
                    closedBefore = _portfolio.ClosedTrades.Count;
                    var fills = _portfolio.Execute(instrument, target.Value, order.Price ?? price,
                        observation.Timestamp, _feeBps);
                    traded = fills.Count > 0;
                    _orderCount++;

                    foreach (var fill in fills)
                    {
                        _fills.Add(FillRow(fill));
                    }

                    // Name the size the order was sized by, because "why is this trade a
                    // different size from the last one" is the first question a script author
                    // has when sizing is decided in two different places.
                    var verb = order.Requested.ToString().ToLowerInvariant();
                    var filledAt = ((double)(fills.Count > 0 ? fills[0].Price : price))
                        .ToString("0.########", CultureInfo.InvariantCulture);
                    var sizeText = sizedBy == SizedBy.Script
                        ? " " + ((double)Abs(target.Value - CurrentQuantity(instrument))).ToString("0.########", CultureInfo.InvariantCulture)
                        : string.Empty;
                    note = $"{verb}{sizeText} at {filledAt} " +
                           $"({(sizedBy == SizedBy.Script ? "script size" : "account size")}" +
                           (string.IsNullOrEmpty(order.Reason) ? string.Empty : $", {order.Reason}") + ")";
                }
                else
                {
                    // An order that resolves to nothing is worth saying out loud: the usual
                    // cause is a script asking to buy with no size configured, which otherwise
                    // looks exactly like a strategy that never signals.
                    _rejectedOrderCount++;
                    var why = _orderQuantity == 0m && _positionFraction == 0m
                        ? "no order size configured"
                        : "resolved to no trade";
                    note = "order ignored: " + why;
                }

                _decisions.Add(new Dictionary<string, object>
                {
                    ["timestamp"] = observation.Timestamp.ToString("O"),
                    ["symbol"] = observation.State?.Symbol?.Value ?? string.Empty,
                    ["venue"] = VenueOf(observation),
                    ["instrument"] = instrument,
                    ["action"] = order.Requested.ToString().ToLowerInvariant(),
                    ["script_quantity"] = order.Quantity,
                    ["account_quantity"] = order.Quantity == null ? Abs(target ?? 0m) : null,
                    ["price"] = (double)(order.Price ?? price),
                    ["reason"] = order.Reason,
                    ["filled"] = traded,
                    ["position_after"] = (double)CurrentQuantity(instrument),
                    ["equity_after"] = (double)_portfolio.Equity
                });
            }

            var point = _portfolio.Mark(observation.Timestamp, instrument, price);
            if (_emitEveryObservation)
            {
                _rows.Add(BuildObservationRow(observation, features, instrument, scriptRow, price, note, traded));
            }
            else if (scriptRow != null)
            {
                _rows.Add(MergeReturnedRow(scriptRow, observation, instrument, price, note, traded));
            }

            if (traded)
            {
                RecordClosedTrades(closedBefore);
            }

            _equity.Add(EquityRow(point, instrument));
        }

        public override void OnOutcome(OutcomeData outcome)
        {
            _host?.OnOutcome(BuildOutcome(outcome));
        }

        public override ExperimentResult Finalize()
        {
            var result = base.Finalize();

            // Close whatever is still open so the reported account is a settled one. A curve
            // that stops mid-position is not a final result, and comparing it against a settled
            // one is comparing two different things.
            var closedBeforeSettle = _portfolio.ClosedTrades.Count;
            foreach (var fill in _portfolio.LiquidateAll(_lastObservationTime))
            {
                // The closing fill is a real fill. Leaving it out of the fill log makes the
                // trade log and the fill log disagree about the same round trip, and a run
                // that ends holding something has to show what closed it.
                _fills.Add(FillRow(fill));
                _orderCount++;
            }
            if (_equity.Count > 0)
            {
                var last = _equity[_equity.Count - 1];
                var lastTime = DateTime.Parse((string)last["timestamp"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                _equity.Add(EquityRow(_portfolio.Mark(lastTime), _equity.Count > 0 ? (string)last["instrument"] : null));
            }

            RecordClosedTrades(closedBeforeSettle);

            var host = _host;
            _host = null;
            try
            {
                var finalized = host?.Finalize();
                if (finalized != null)
                {
                    foreach (var row in finalized.Rows)
                    {
                        _rows.Add(row);
                    }

                    foreach (var kvp in finalized.Metrics)
                    {
                        _metrics[kvp.Key] = kvp.Value;
                    }

                    foreach (var kvp in finalized.Metadata)
                    {
                        _metadata[kvp.Key] = kvp.Value;
                    }
                }
            }
            finally
            {
                try
                {
                    host?.Dispose();
                }
                catch
                {
                    // Best-effort: the Python runtime may already be gone.
                }
            }

            result.Rows = _rows;
            result.AddNamedRows(TradesTable, _trades);
            result.AddNamedRows(PortfolioTable, _equity);
            result.AddNamedRows(FillsTable, _fills);
            result.AddNamedRows(DecisionsTable, _decisions);

            foreach (var kvp in _metrics)
            {
                result.Metrics[kvp.Key] = kvp.Value;
            }

            var closed = _portfolio.ClosedTrades.ToList();
            result.Metrics["starting_cash"] = (double)_portfolio.StartingCash;
            result.Metrics["ending_equity"] = (double)_portfolio.Equity;
            result.Metrics["total_return"] = (double)(_portfolio.Equity / _portfolio.StartingCash - 1m);
            result.Metrics["realized_pnl"] = (double)_portfolio.RealizedPnl;
            result.Metrics["unrealized_pnl"] = (double)_portfolio.UnrealizedPnl;
            result.Metrics["peak_equity"] = (double)_portfolio.PeakEquity;
            result.Metrics["max_drawdown"] = (double)_portfolio.MaxDrawdown;
            result.Metrics["total_fees"] = (double)_portfolio.TotalFees;
            result.Metrics["position_count"] = closed.Count;
            result.Metrics["order_count"] = _orderCount;
            result.Metrics["rejected_order_count"] = _rejectedOrderCount;
            result.Metrics["observations"] = _rows.Count;
            if (closed.Count > 0)
            {
                result.Metrics["win_rate"] = (double)closed.Count(t => t.Pnl > 0m) / closed.Count;
                result.Metrics["net_trade_pnl"] = (double)closed.Sum(t => t.Pnl);
            }
            else
            {
                result.Metrics["win_rate"] = null;
            }

            foreach (var kvp in _metadata)
            {
                result.Metadata[kvp.Key] = kvp.Value;
            }

            return result;
        }

        private enum SizedBy
        {
            Script,
            Account
        }

        private (decimal? Target, SizedBy SizedBy) ResolveTarget(StrategyOrder order, string instrument, decimal price)
        {
            var current = CurrentQuantity(instrument);

            if (order.Quantity.HasValue)
            {
                return (order.ResolveTarget(current, 0m), SizedBy.Script);
            }

            // The account sizes it. An absolute amount is the honest default for a market with a
            // known price; a fraction of equity is for a script that wants size to track P&L.
            var size = _orderQuantity > 0m
                ? _orderQuantity
                : _positionFraction * _portfolio.Equity / price;

            return (order.ResolveTarget(current, size), SizedBy.Account);
        }

        private decimal CurrentQuantity(string instrument)
        {
            return _portfolio.PositionOf(instrument).SignedQuantity;
        }

        /// <summary>
        /// Books the round trips the portfolio appended to its closed-trade log at or after
        /// <paramref name="fromIndex"/>.
        /// </summary>
        /// <remarks>
        /// The caller passes the closed-trade count captured immediately before it executed, so
        /// "which trades are new" is answered by the portfolio's own append-only log rather than
        /// by bookkeeping kept here. Tracking a per-instrument high-water mark instead looks
        /// equivalent and is not: those marks are counts of one instrument's trades, while the
        /// loop indexes the combined list, so the two disagree as soon as a second instrument
        /// trades, and the final settle re-emits the whole log.
        /// </remarks>
        private void RecordClosedTrades(int fromIndex)
        {
            var all = _portfolio.ClosedTrades.ToList();
            for (var i = Math.Max(0, fromIndex); i < all.Count; i++)
            {
                var trade = all[i];
                _trades.Add(new Dictionary<string, object>
                {
                    ["timestamp"] = trade.ExitTime.ToString("O"),
                    ["symbol"] = trade.Symbol,
                    ["venue"] = SplitVenue(trade.Symbol),
                    ["side"] = trade.Side.ToString(),
                    ["quantity"] = (double)Abs(trade.Quantity),
                    ["entry_price"] = (double)trade.EntryPrice,
                    ["exit_price"] = (double)trade.ExitPrice,
                    ["pnl"] = (double)trade.Pnl,
                    ["fees"] = (double)trade.Fees,
                    ["is_win"] = trade.Pnl > 0m,
                    ["holding_period_minutes"] = (trade.ExitTime - trade.EntryTime).TotalMinutes,
                    ["equity_after"] = (double)trade.EquityAfter
                });
            }
        }

        private Dictionary<string, object> BuildObservationRow(
            Observation observation,
            FeatureResult features,
            string instrument,
            Dictionary<string, object> scriptRow,
            decimal price,
            string note,
            bool traded)
        {
            var row = scriptRow != null
                ? new Dictionary<string, object>(scriptRow)
                : new Dictionary<string, object>();

            // The order itself is consumed by the account; leaving it in the row would put a
            // nested dict in the output table.
            row.Remove(StrategyOrder.RowKey);

            var state = observation.State;
            foreach (var field in StandardFields)
            {
                if (row.ContainsKey(field))
                {
                    continue;
                }

                var value = StandardValue(observation, state, field);
                if (value != null)
                {
                    row[field] = value;
                }
            }

            if (features != null)
            {
                foreach (var kvp in features.Values)
                {
                    if (!row.ContainsKey(kvp.Key))
                    {
                        row[kvp.Key] = (double)kvp.Value;
                    }
                }
            }

            row.TryAdd("timestamp", observation.Timestamp.ToString("O"));
            row.TryAdd("symbol", state?.Symbol?.Value ?? string.Empty);
            row["venue"] = VenueOf(observation);
            row["instrument"] = instrument;
            row["data_quality"] = observation.Quality.ToString().ToLowerInvariant();

            var position = _portfolio.PositionOf(instrument);
            row["position"] = (double)position.SignedQuantity;
            row["position_side"] = position.Side.ToString().ToLowerInvariant();
            row["avg_entry_price"] = (double)position.AverageEntryPrice;
            row["position_value"] = (double)(position.SignedQuantity * price);
            row["cash"] = (double)_portfolio.Cash;
            row["equity"] = (double)_portfolio.Equity;
            row["realized_pnl"] = (double)_portfolio.RealizedPnl;
            row["unrealized_pnl"] = (double)_portfolio.UnrealizedPnl;
            row["total_fees"] = (double)_portfolio.TotalFees;
            row["drawdown"] = (double)_portfolio.MaxDrawdown;
            row["executed_price"] = (double)price;
            row["traded"] = traded;
            if (!string.IsNullOrEmpty(note))
            {
                row["note"] = note;
            }

            return row;
        }

        private Dictionary<string, object> MergeReturnedRow(
            Dictionary<string, object> scriptRow,
            Observation observation,
            string instrument,
            decimal price,
            string note,
            bool traded)
        {
            var row = BuildObservationRow(observation, null, instrument, scriptRow, price, note, traded);
            return row;
        }

        private object StandardValue(Observation observation, MarketState.MarketState state, string field)
        {
            switch (field)
            {
                case "bid_price": return state == null ? null : (double)state.BidPrice;
                case "ask_price": return state == null ? null : (double)state.AskPrice;
                case "last_price": return state == null ? null : (double)state.LastPrice;
                case "mid_price": return state == null ? null : (double)state.MidPrice;
                case "spread_bps": return state == null ? null : (double)state.SpreadBps;
                case "bid_depth": return state == null ? null : (double)state.BidDepth;
                case "ask_depth": return state == null ? null : (double)state.AskDepth;
                case "depth": return state == null ? null : (double)(state.BidDepth + state.AskDepth);
                case "imbalance": return state == null ? null : (double)state.DepthImbalance;
                case "trade_volume": return (double)observation.Volume;
                case "vwap": return (double)observation.VWAP;
                case "close_price": return (double)observation.ClosePrice;
                default: return null;
            }
        }

        private Dictionary<string, object> EquityRow(EquityPoint point, string instrument)
        {
            return new Dictionary<string, object>
            {
                ["timestamp"] = point.Timestamp.ToString("O"),
                ["instrument"] = instrument ?? string.Empty,
                ["equity"] = (double)point.Equity,
                ["cash"] = (double)point.Cash,
                ["position_value"] = (double)point.PositionValue,
                ["realized_pnl"] = (double)point.RealizedPnl,
                ["unrealized_pnl"] = (double)point.UnrealizedPnl,
                ["drawdown"] = (double)point.DrawdownPct,
                ["max_drawdown"] = (double)point.MaxDrawdownPct,
                ["peak_equity"] = (double)point.PeakEquity,
                ["gross_exposure"] = (double)point.GrossExposure,
                ["open_positions"] = point.OpenPositions
            };
        }

        private static Dictionary<string, object> FillRow(Fill fill)
        {
            return new Dictionary<string, object>
            {
                ["timestamp"] = fill.Timestamp.ToString("O"),
                ["instrument"] = fill.Symbol,
                ["venue"] = SplitVenue(fill.Symbol),
                ["signed_quantity"] = (double)fill.SignedQuantity,
                ["price"] = (double)fill.Price,
                ["notional"] = (double)fill.Notional,
                ["fee"] = (double)fill.Fee,
                ["realized_pnl"] = (double)fill.RealizedPnl,
                ["reason"] = fill.Reason.ToString().ToLowerInvariant(),
                ["cash_after"] = (double)fill.CashAfter
            };
        }

        // -----------------------------------------------------------------------
        // Keys and names
        // -----------------------------------------------------------------------

        /// <summary>
        /// "<c>venue:symbol</c>", or just the symbol when the data source does not say where the
        /// data came from. One string keeps a two-venue run from netting a Bybit long against a
        /// Binance short inside the same position.
        /// </summary>
        private static string InstrumentKey(Observation observation)
        {
            var symbol = observation.State?.Symbol?.Value ?? string.Empty;
            var venue = VenueOf(observation);
            return string.IsNullOrEmpty(venue) ? symbol : $"{venue}:{symbol}";
        }

        /// <summary>
        /// Where this observation's data came from, taken from the most recent event's provenance.
        /// The market state itself does not record it, so without this a two-venue run could not
        /// tell the two books apart.
        /// </summary>
        private static string VenueOf(Observation observation)
        {
            if (observation?.Events == null)
            {
                return string.Empty;
            }

            for (var i = observation.Events.Count - 1; i >= 0; i--)
            {
                var venue = observation.Events[i]?.Provenance?.Venue;
                if (!string.IsNullOrWhiteSpace(venue))
                {
                    return venue.ToLowerInvariant();
                }
            }

            return string.Empty;
        }

        private static string SplitVenue(string instrument)
        {
            if (string.IsNullOrEmpty(instrument))
            {
                return string.Empty;
            }

            var index = instrument.IndexOf(':');
            return index < 0 ? string.Empty : instrument[..index];
        }

        // -----------------------------------------------------------------------
        // Script-facing payloads
        // -----------------------------------------------------------------------

        /// <summary>
        /// The observation dict handed to the script. Carries the market data the run requested
        /// plus a live view of the account, so a script can size against real equity and see its
        /// own position without maintaining its own bookkeeping.
        /// </summary>
        private Dictionary<string, object> BuildObservation(
            Observation observation, FeatureResult features, string instrument)
        {
            var state = observation.State;
            var row = new Dictionary<string, object>
            {
                ["timestamp"] = observation.Timestamp.ToString("O"),
                ["symbol"] = state?.Symbol?.Value ?? string.Empty,
                ["venue"] = VenueOf(observation),
                ["instrument"] = instrument,
                ["data_quality"] = observation.Quality.ToString().ToLowerInvariant(),
                ["bid_price"] = (double)(state?.BidPrice ?? 0m),
                ["ask_price"] = (double)(state?.AskPrice ?? 0m),
                ["last_price"] = (double)(state?.LastPrice ?? 0m),
                ["mid_price"] = (double)(state?.MidPrice ?? 0m),
                ["spread"] = (double)(state?.Spread ?? 0m),
                ["spread_bps"] = (double)(state?.SpreadBps ?? 0m),
                ["bid_depth"] = (double)(state?.BidDepth ?? 0m),
                ["ask_depth"] = (double)(state?.AskDepth ?? 0m),
                ["depth"] = (double)((state?.BidDepth ?? 0m) + (state?.AskDepth ?? 0m)),
                ["depth_imbalance"] = (double)(state?.DepthImbalance ?? 0m),
                ["volume"] = (double)observation.Volume,
                ["vwap"] = (double)observation.VWAP,
                ["close_price"] = (double)observation.ClosePrice,
                ["trade_count"] = observation.TradeCount
            };

            var account = new Dictionary<string, object>
            {
                ["cash"] = (double)_portfolio.Cash,
                ["equity"] = (double)_portfolio.Equity,
                ["starting_cash"] = (double)_portfolio.StartingCash,
                ["realized_pnl"] = (double)_portfolio.RealizedPnl,
                ["unrealized_pnl"] = (double)_portfolio.UnrealizedPnl,
                ["position"] = (double)CurrentQuantity(instrument),
                ["position_side"] = _portfolio.PositionOf(instrument).Side.ToString().ToLowerInvariant(),
                ["avg_entry_price"] = (double)_portfolio.PositionOf(instrument).AverageEntryPrice,
                ["total_fees"] = (double)_portfolio.TotalFees,
                ["max_drawdown"] = (double)_portfolio.MaxDrawdown
            };
            row["account"] = account;

            return row;
        }

        private static Dictionary<string, object> BuildFeatures(FeatureResult features)
        {
            var result = new Dictionary<string, object>();
            if (features == null)
            {
                return result;
            }

            foreach (var kvp in features.Values)
            {
                result[kvp.Key] = (double)kvp.Value;
            }

            return result;
        }

        private Dictionary<string, object> BuildContext(ExperimentContext context)
        {
            var config = new Dictionary<string, object>();
            foreach (var kvp in context.Configuration)
            {
                config[kvp.Key] = kvp.Value;
            }

            config["starting_cash"] = (double)_startingCash;
            config["fee_bps"] = (double)_feeBps;
            config["price_field"] = _priceField;
            config["order_quantity"] = (double)_orderQuantity;
            config["position_fraction"] = (double)_positionFraction;
            config["symbol"] = _job?.Symbols?.FirstOrDefault() ?? string.Empty;
            config["venue"] = _job?.Venue ?? string.Empty;
            config["job_id"] = _job?.JobId ?? string.Empty;
            config["start_time"] = context.StartTime.ToString("O");
            config["end_time"] = context.EndTime.ToString("O");
            return config;
        }

        private static Dictionary<string, object> BuildOutcome(OutcomeData outcome)
        {
            if (outcome == null)
            {
                return new Dictionary<string, object>();
            }

            return new Dictionary<string, object>
            {
                ["reference_timestamp"] = outcome.ReferenceTimestamp.ToString("O"),
                ["observed_at"] = outcome.ObservedAt?.ToString("O") ?? string.Empty,
                ["future_price"] = (double)outcome.FuturePrice,
                ["reference_price"] = outcome.ReferencePrice.HasValue ? (double)outcome.ReferencePrice.Value : null,
                ["outcome_return"] = outcome.OutcomeReturn.HasValue ? (double)outcome.OutcomeReturn.Value : null,
                ["horizon_seconds"] = outcome.Horizon.TotalSeconds,
                ["symbol"] = outcome.OutcomeState?.Symbol?.Value ?? string.Empty
            };
        }

        // -----------------------------------------------------------------------
        // Config parsing
        // -----------------------------------------------------------------------

        private static decimal ParseDecimal(string text, string key, decimal fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            if (!decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new FormatException($"script_trade '{key}' must be a number, got '{text}'.");
            }

            return value;
        }

        private static bool ParseBool(string text, string key, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "true":
                case "yes":
                case "1":
                    return true;
                case "false":
                case "no":
                case "0":
                    return false;
                default:
                    throw new FormatException($"script_trade '{key}' must be true or false, got '{text}'.");
            }
        }

        /// <summary>
        /// The price the run trades at, read from the configured field. Falls back to the last
        /// traded price when the configured field has no value, because a bid-only or ask-only
        /// book would otherwise refuse to trade at all on a perfectly tradable observation.
        /// </summary>
        private decimal PriceOf(Observation observation, decimal fallback = 0m)
        {
            var state = observation.State;
            if (state != null)
            {
                var value = _priceField switch
                {
                    "close" or "close_price" => observation.ClosePrice,
                    "last" or "last_price" => state.LastPrice,
                    "mid" or "mid_price" => state.MidPrice,
                    "bid" or "bid_price" => state.BidPrice,
                    "ask" or "ask_price" => state.AskPrice,
                    _ => 0m
                };

                if (value > 0m)
                {
                    return value;
                }
            }

            if (fallback > 0m)
            {
                return fallback;
            }

            return observation.ClosePrice > 0m ? observation.ClosePrice : state?.LastPrice ?? 0m;
        }

        private static decimal Abs(decimal value) => value < 0m ? -value : value;
    }
}
