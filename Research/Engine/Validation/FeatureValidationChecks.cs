using System;
using System.Collections.Generic;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Arithmetic identities between computed features, evaluated only when every input is present in
    /// the job's feature selection. This is the check that catches a feature not computing what its
    /// name claims: <c>net_flow</c> must equal aggressive buy notional minus aggressive sell notional,
    /// and <c>trade_flow</c> must equal the same quantity. When these drift apart, one of them is wrong.
    ///
    /// Checks are skipped rather than failed when an input feature was not selected, since a job is
    /// free to compute any subset.
    /// </summary>
    public sealed class FlowIdentityCheck : IObservationCheck
    {
        /// <summary>
        /// Relative tolerance for identity comparisons.
        /// </summary>
        private const decimal Tolerance = 1e-9m;

        /// <summary>
        /// Absolute tolerance floor, so a near-zero expected value is still compared meaningfully
        /// against a small absolute residual rather than a meaningless relative one.
        /// </summary>
        private const decimal AbsoluteFloor = 1e-12m;

        public string Id => ValidationCheckIds.Flow;

        public string Description =>
            "Cross-feature identities: net_flow == aggressive_buy_volume - aggressive_sell_volume and " +
            "trade_flow == the same signed notional flow";

        public void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features, ValidationReport report)
        {
            if (features == null || observation == null)
            {
                return;
            }

            if (!TryGet(features, "aggressive_buy_volume", out var buy)
                || !TryGet(features, "aggressive_sell_volume", out var sell))
            {
                return;
            }

            var expected = buy - sell;

            if (TryGet(features, "net_flow", out var netFlow) && !Close(netFlow, expected))
            {
                Add(report, observation, "net_flow",
                    $"net_flow {netFlow} != aggressive_buy_volume {buy} - aggressive_sell_volume {sell} = {expected}.");
            }

            if (TryGet(features, "trade_flow", out var tradeFlow) && !Close(tradeFlow, expected))
            {
                Add(report, observation, "trade_flow",
                    $"trade_flow {tradeFlow} != aggressive_buy_volume {buy} - aggressive_sell_volume {sell} = {expected}. " +
                    "trade_flow is documented as signed notional flow; an unsigned value here means it is " +
                    "reporting total volume and cannot indicate direction.");
            }
        }

        private static bool TryGet(IReadOnlyDictionary<string, decimal> features, string name, out decimal value)
        {
            return features.TryGetValue(name, out value);
        }

        private static void Add(ValidationReport report, Observation observation, string feature, string message)
        {
            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Flow,
                Severity = ValidationSeverity.Error,
                Symbol = observation.State?.Symbol?.Value,
                Timestamp = observation.Timestamp,
                Message = message
            });
        }

        private static bool Close(decimal left, decimal right)
        {
            var difference = Math.Abs(left - right);
            if (difference == 0m)
            {
                return true;
            }

            var scale = Math.Max(Math.Abs(left), Math.Abs(right));
            return difference <= Math.Max(scale * Tolerance, AbsoluteFloor);
        }
    }

    /// <summary>
    /// Numeric plausibility of computed feature values: no non-finite results, and no negative depth
    /// or size arriving through a feature. Decimal cannot represent NaN or infinity, so this guards
    /// against values that are finite but nonsensical, and against a division producing a
    /// pathological magnitude that later arithmetic will amplify.
    /// </summary>
    public sealed class NumericPlausibilityCheck : IObservationCheck
    {
        /// <summary>
        /// Magnitude above which a normalized feature (an imbalance, a ratio, a bps distance) is
        /// treated as implausible and reported once per column.
        /// </summary>
        private const decimal NormalizedCeiling = 1_000_000m;

        /// <summary>
        /// Features whose value is a normalized quantity expected to sit near unit scale.
        /// </summary>
        private static readonly string[] Normalized =
        {
            "imbalance",
            "structural_imbalance",
            "depth_persistence"
        };

        /// <summary>
        /// Features that describe a book or a traded quantity and cannot be negative.
        /// </summary>
        private static readonly string[] NonNegative =
        {
            "depth",
            "bid_depth",
            "ask_depth",
            "trade_volume",
            "aggressive_buy_volume",
            "aggressive_sell_volume",
            "liquidity_wall",
            "resistance"
        };

        public string Id => ValidationCheckIds.Numeric;

        public string Description =>
            "Numeric plausibility: non-negative book/volume features, and normalized features within a sane magnitude";

        public void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features, ValidationReport report)
        {
            if (features == null || observation == null)
            {
                return;
            }

            foreach (var kvp in features)
            {
                if (Matches(kvp.Key, NonNegative) && kvp.Value < 0)
                {
                    Add(report, observation, kvp.Key, ValidationSeverity.Error,
                        $"{kvp.Key} is negative ({kvp.Value}); this measurement cannot be below zero.");
                }

                if (Matches(kvp.Key, Normalized) && Math.Abs(kvp.Value) > NormalizedCeiling)
                {
                    Add(report, observation, kvp.Key, ValidationSeverity.Warning,
                        $"{kvp.Key} is {kvp.Value}, far outside its expected range near [-1, 1]. " +
                        "This usually means a division by a near-zero depth total.");
                }
            }
        }

        /// <summary>
        /// Whether a column name refers to one of the given feature stems. Parameterized features emit
        /// suffixed value names ("structural_imbalance_10bps", "liquidity_depletion_5obs_10bps"), so an
        /// exact comparison would miss every configured instance.
        /// </summary>
        private static bool Matches(string columnName, string[] stems)
        {
            foreach (var stem in stems)
            {
                if (string.Equals(columnName, stem, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (columnName.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Add(ValidationReport report, Observation observation, string feature,
            ValidationSeverity severity, string message)
        {
            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Numeric,
                Severity = severity,
                Symbol = observation.State?.Symbol?.Value,
                Timestamp = observation.Timestamp,
                Message = message
            });
        }
    }
}
