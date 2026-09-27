using System.Globalization;
using System.Linq;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Immutable parameter bag for a parameterized feature instance. Values are strings as they
    /// come from job configuration; typed getters parse on demand.
    /// </summary>
    public sealed class FeatureParams
    {
        public static readonly FeatureParams Empty = new(new Dictionary<string, string>());

        private readonly IReadOnlyDictionary<string, string> _values;

        public IReadOnlyDictionary<string, string> Values => _values;

        public FeatureParams(IReadOnlyDictionary<string, string> values)
        {
            _values = values ?? new Dictionary<string, string>();
        }

        public string Get(string key, string defaultValue = "")
        {
            return _values.TryGetValue(key, out var value) && value != null ? value : defaultValue;
        }

        public int GetInt(string key, int defaultValue = 0)
        {
            return int.TryParse(Get(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;
        }

        public decimal GetDecimal(string key, decimal defaultValue = 0m)
        {
            return decimal.TryParse(Get(key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : defaultValue;
        }
    }

    /// <summary>
    /// Resolves per-feature parameter sets from a flat job configuration using the naming scheme
    /// "feature.&lt;featureName&gt;.&lt;parameterKey&gt; = value".
    /// </summary>
    public sealed class FeatureParameters
    {
        /// <summary>
        /// Configuration key prefix for per-feature parameters: "feature.&lt;featureName&gt;.&lt;parameterKey&gt;".
        /// </summary>
        public const string Prefix = "feature.";

        private readonly Dictionary<string, FeatureParams> _byName = new(StringComparer.OrdinalIgnoreCase);

        public FeatureParams For(string featureName)
        {
            return _byName.TryGetValue(featureName, out var parameters) ? parameters : FeatureParams.Empty;
        }

        public static FeatureParameters ParseJobConfig(IDictionary<string, string> config)
        {
            var result = new FeatureParameters();
            if (config == null || config.Count == 0)
                return result;

            foreach (var kvp in config)
            {
                if (string.IsNullOrEmpty(kvp.Key) || !kvp.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rest = kvp.Key.Substring(Prefix.Length);
                if (string.IsNullOrWhiteSpace(rest))
                    continue;

                var dot = rest.IndexOf('.');
                if (dot <= 0 || dot == rest.Length - 1)
                    continue;

                var featureName = rest.Substring(0, dot);
                var paramKey = rest.Substring(dot + 1);
                if (!result._byName.TryGetValue(featureName, out var bag))
                {
                    bag = new FeatureParams(new Dictionary<string, string>());
                    result._byName[featureName] = bag;
                }

                ((Dictionary<string, string>)bag.Values)[paramKey] = kvp.Value;
            }

            return result;
        }
    }

    /// <summary>
    /// Resolves explicit raw observation fields ("RawFields" on a research job) not covered by the
    /// feature engine. Grants guaranteed columns on the observation output rows for later analysis.
    /// Values are point-in-time (no future information).
    /// </summary>
    public static class RawFieldValues
    {
        /// <summary>
        /// Machine-readable descriptions of every raw field this class can resolve. This table is
        /// the single source of truth for the raw-field namespace (names, types, sources); the
        /// <see cref="MeasurementCatalog"/> unifies it with the feature registry.
        /// </summary>
        public static readonly IReadOnlyList<MeasurementDescriptor> Descriptors = new[]
        {
            new MeasurementDescriptor("symbol", MeasurementKind.Raw, typeof(string), "Symbol value", "observation"),
            new MeasurementDescriptor("timestamp", MeasurementKind.Raw, typeof(DateTime), "Observation timestamp", "observation"),
            new MeasurementDescriptor("mid_price", MeasurementKind.Raw, typeof(decimal), "Market state mid price", "state"),
            new MeasurementDescriptor("bid_price", MeasurementKind.Raw, typeof(decimal), "Market state best bid price", "state"),
            new MeasurementDescriptor("ask_price", MeasurementKind.Raw, typeof(decimal), "Market state best ask price", "state"),
            new MeasurementDescriptor("last_price", MeasurementKind.Raw, typeof(decimal), "Market state last traded price", "state"),
            new MeasurementDescriptor("spread", MeasurementKind.Raw, typeof(decimal), "Top-of-book spread", "state"),
            new MeasurementDescriptor("spread_bps", MeasurementKind.Raw, typeof(decimal), "Top-of-book spread in basis points", "state"),
            new MeasurementDescriptor("depth", MeasurementKind.Raw, typeof(decimal), "Total order book depth (bid + ask)", "state"),
            new MeasurementDescriptor("bid_depth", MeasurementKind.Raw, typeof(decimal), "Total order book depth on bid side", "state"),
            new MeasurementDescriptor("ask_depth", MeasurementKind.Raw, typeof(decimal), "Total order book depth on ask side", "state"),
            new MeasurementDescriptor("volume", MeasurementKind.Raw, typeof(decimal), "Traded volume since the previous observation", "events"),
            new MeasurementDescriptor("trade_count", MeasurementKind.Raw, typeof(int), "Number of trades since the previous observation", "events"),
            new MeasurementDescriptor("vwap", MeasurementKind.Raw, typeof(decimal), "Volume weighted average price of the period's trades", "events"),
            new MeasurementDescriptor("open_price", MeasurementKind.Raw, typeof(decimal), "Price at the start of the observation period", "events"),
            new MeasurementDescriptor("high_price", MeasurementKind.Raw, typeof(decimal), "Highest trade price in the observation period", "events"),
            new MeasurementDescriptor("low_price", MeasurementKind.Raw, typeof(decimal), "Lowest trade price in the observation period", "events"),
            new MeasurementDescriptor("close_price", MeasurementKind.Raw, typeof(decimal), "Price at the end of the observation period", "events"),
            new MeasurementDescriptor("trade_flow", MeasurementKind.Raw, typeof(decimal), "Signed notional flow of the period's trades (+buy, -sell)", "events")
        };

        /// <summary>
        /// All raw field names this class can resolve.
        /// </summary>
        public static IReadOnlyList<string> Names { get; } = Descriptors.Select(d => d.Name).ToList();

        public static object For(Observation observation, string field)
        {
            if (observation == null)
                throw new ArgumentNullException(nameof(observation));

            // Preflight accepts raw field names case-insensitively, so resolution has to as well;
            // otherwise "Bid_Price" passes validation and then throws here mid-replay.
            var key = field?.Trim().ToLowerInvariant();
            var state = observation.State;
            switch (key)
            {
                case "symbol": return observation.State?.Symbol?.Value ?? string.Empty;
                case "timestamp": return observation.Timestamp;
                case "mid_price": return state?.MidPrice ?? 0m;
                case "bid_price": return state?.BidPrice ?? 0m;
                case "ask_price": return state?.AskPrice ?? 0m;
                case "last_price": return state?.LastPrice ?? 0m;
                case "spread": return state?.Spread ?? 0m;
                case "spread_bps": return state?.SpreadBps ?? 0m;
                case "depth": return state != null ? state.BidDepth + state.AskDepth : 0m;
                case "bid_depth": return state?.BidDepth ?? 0m;
                case "ask_depth": return state?.AskDepth ?? 0m;
                case "volume": return observation.Volume;
                case "trade_count": return observation.TradeCount;
                case "vwap": return observation.VWAP;
                case "open_price": return observation.OpenPrice;
                case "high_price": return observation.HighPrice;
                case "low_price": return observation.LowPrice;
                case "close_price": return observation.ClosePrice;
                case "trade_flow":
                    return SignedNotionalFlow.Compute(observation.Events);
                default:
                    throw new InvalidOperationException(
                        $"Unknown raw field '{field}'. Supported fields: {string.Join(", ", Names)}");
            }
        }
    }
}