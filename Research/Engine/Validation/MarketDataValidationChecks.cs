using System;
using System.Collections.Generic;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Mathematical identities that must hold on the reconstructed market state of every
    /// observation: the book must be internally consistent, prices must be ordered, and the
    /// normalized quantities must stay inside their documented ranges. A violation means the state
    /// reconstruction or the feature math is wrong, not that the market moved oddly.
    ///
    /// Every comparison uses a relative tolerance so it is scale-free and does not trip on decimal
    /// rounding at large price magnitudes.
    /// </summary>
    public sealed class MarketStateInvariantCheck : IObservationCheck
    {
        /// <summary>
        /// Relative tolerance for identity comparisons.
        /// </summary>
        private const decimal Tolerance = 1e-9m;

        public string Id => ValidationCheckIds.MarketState;

        public string Description =>
            "Book and price identities: ask >= bid, mid == (bid + ask) / 2, depth == bid_depth + ask_depth, " +
            "normalized imbalance within [-1, 1], depth_ratio consistent with the depths";

        public void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features, ValidationReport report)
        {
            if (observation?.State is not MarketState.MarketState state)
            {
                return;
            }

            var symbol = state.Symbol?.Value;
            var at = observation.Timestamp;

            var bid = state.BidPrice;
            var ask = state.AskPrice;

            if (bid > 0 && ask > 0)
            {
                if (ask < bid)
                {
                    Add(report, symbol, at, ValidationSeverity.Error,
                        $"Crossed book: ask {ask} < bid {bid}.");
                }

                var expectedMid = (bid + ask) / 2m;
                if (!Close(state.MidPrice, expectedMid))
                {
                    Add(report, symbol, at, ValidationSeverity.Error,
                        $"mid {state.MidPrice} != (bid {bid} + ask {ask}) / 2 = {expectedMid}.");
                }

                if (state.MidPrice > 0)
                {
                    var expectedSpread = ask - bid;
                    if (!Close(state.Spread, expectedSpread))
                    {
                        Add(report, symbol, at, ValidationSeverity.Error,
                            $"spread {state.Spread} != ask - bid = {expectedSpread}.");
                    }

                    var expectedBps = state.Spread / state.MidPrice * 10000m;
                    if (!Close(state.SpreadBps, expectedBps))
                    {
                        Add(report, symbol, at, ValidationSeverity.Error,
                            $"spread_bps {state.SpreadBps} != spread / mid * 10000 = {expectedBps}.");
                    }
                }
            }
            else if (bid > 0 && ask == 0)
            {
                Add(report, symbol, at, ValidationSeverity.Warning,
                    $"One-sided book: bid {bid} present but ask is 0. mid/spread fall back to last price, " +
                    "so spread reads 0 rather than 'unknown'.");
            }

            var bidDepth = state.BidDepth;
            var askDepth = state.AskDepth;
            if (bidDepth < 0 || askDepth < 0)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"Negative book depth: bid_depth {bidDepth}, ask_depth {askDepth}.");
            }

            var totalDepth = bidDepth + askDepth;
            if (totalDepth > 0)
            {
                if (state.DepthImbalance < -1m || state.DepthImbalance > 1m)
                {
                    Add(report, symbol, at, ValidationSeverity.Error,
                        $"imbalance {state.DepthImbalance} is outside [-1, 1].");
                }

                var expectedImbalance = (bidDepth - askDepth) / totalDepth;
                if (!Close(state.DepthImbalance, expectedImbalance))
                {
                    Add(report, symbol, at, ValidationSeverity.Error,
                        $"imbalance {state.DepthImbalance} != (bid_depth - ask_depth) / depth = {expectedImbalance}.");
                }
            }
            else if (state.DepthImbalance != 0m)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"imbalance {state.DepthImbalance} is non-zero but total book depth is 0.");
            }

            if (state.BidSize < 0 || state.AskSize < 0)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"Negative top-of-book size: bid_size {state.BidSize}, ask_size {state.AskSize}.");
            }

            if (bid > 0 && state.BidSize < 0 || ask > 0 && state.AskSize < 0)
            {
                Add(report, symbol, at, ValidationSeverity.Warning,
                    "Top-of-book price present with zero size; the quote may be a crossed or stale level.");
            }
        }

        private static void Add(ValidationReport report, string symbol, DateTime at, ValidationSeverity severity, string message)
        {
            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.MarketState,
                Severity = severity,
                Symbol = symbol,
                Timestamp = at,
                Message = message
            });
        }

        /// <summary>
        /// Relative equality within <see cref="Tolerance"/>, treating two zeros as equal.
        /// </summary>
        private static bool Close(decimal left, decimal right)
        {
            var difference = Math.Abs(left - right);
            if (difference == 0m)
            {
                return true;
            }

            var scale = Math.Max(Math.Abs(left), Math.Abs(right));
            return difference <= scale * Tolerance;
        }
    }

    /// <summary>
    /// Coherence of the per-period price summary. A period's high must bound its open, low and
    /// close; vwap must lie inside the traded range; and a period that saw no trades must not report
    /// a fabricated OHLC. These are the values a strategy most often reads directly.
    /// </summary>
    public sealed class ObservationCoherenceCheck : IObservationCheck
    {
        /// <summary>
        /// Relative tolerance for range comparisons.
        /// </summary>
        private const decimal Tolerance = 1e-9m;

        public string Id => ValidationCheckIds.Observation;

        public string Description =>
            "Per-period coherence: high >= max(open, low, close), low <= min(open, high, close), " +
            "close within [low, high], vwap within [low, high], non-negative volume";

        public void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features, ValidationReport report)
        {
            if (observation == null)
            {
                return;
            }

            var symbol = observation.State?.Symbol?.Value;
            var at = observation.Timestamp;
            var high = observation.HighPrice;
            var low = observation.LowPrice;

            if (observation.Volume < 0)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"Negative period volume {observation.Volume}.");
            }

            var hasTrades = observation.TradeCount > 0;
            if (!hasTrades)
            {
                if (observation.Volume != 0m)
                {
                    Add(report, symbol, at, ValidationSeverity.Warning,
                        $"Period reports volume {observation.Volume} but contains no trade events.");
                }

                return;
            }

            if (high <= 0 || low <= 0)
            {
                Add(report, symbol, at, ValidationSeverity.Warning,
                    $"Period with {observation.TradeCount} trade(s) reports non-positive range (low {low}, high {high}).");
                return;
            }

            if (high < low)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"Inverted range: high {high} < low {low}.");
                return;
            }

            var open = observation.OpenPrice;
            var close = observation.ClosePrice;

            // OpenPrice and ClosePrice read a trade price when the boundary event is a trade and a
            // quote mid otherwise, while High/Low are trade-only. Comparing a quote-derived open
            // against a trade-derived range mixes two price series (they legitimately differ by a
            // tick), so only endpoints that actually came from a trade are range-checked.
            var openIsTrade = observation.Events.Count > 0 && observation.Events[0] is TradeEvent;
            var closeIsTrade = observation.Events.Count > 0 && observation.Events[observation.Events.Count - 1] is TradeEvent;

            if (open == 0m)
            {
                Add(report, symbol, at, ValidationSeverity.Warning,
                    $"Period with {observation.TradeCount} trade(s) reports open = 0. Observation.OpenPrice " +
                    "only reads the first event when it is a trade or quote, so a period that opens with a " +
                    "book update or bar yields 0 rather than a price. Do not use open as a price without " +
                    "guarding it.");
            }
            else if (openIsTrade && high < open - high * Tolerance)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"open {open} exceeds period high {high}.");
            }
            else if (openIsTrade && low > open + low * Tolerance)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"open {open} is below period low {low}.");
            }

            if (closeIsTrade && close > high + high * Tolerance)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"close {close} exceeds period high {high}.");
            }

            if (closeIsTrade && close < low - low * Tolerance)
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"close {close} is below period low {low}.");
            }

            var vwap = observation.VWAP;
            if (vwap > 0 && (vwap > high + high * Tolerance || vwap < low - low * Tolerance))
            {
                Add(report, symbol, at, ValidationSeverity.Error,
                    $"vwap {vwap} falls outside the traded range [{low}, {high}].");
            }
        }

        private static void Add(ValidationReport report, string symbol, DateTime at, ValidationSeverity severity, string message)
        {
            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Observation,
                Severity = severity,
                Symbol = symbol,
                Timestamp = at,
                Message = message
            });
        }
    }
}
