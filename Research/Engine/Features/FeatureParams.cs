using System.Globalization;
using QuantConnect.Research.Engine.Events;
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
        private const string Prefix = "feature.";

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
        public static object For(Observation observation, string field)
        {
            if (observation == null)
                throw new ArgumentNullException(nameof(observation));

            var state = observation.State;
            switch (field)
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
                    decimal flow = 0m;
                    if (observation.Events != null)
                    {
                        foreach (var evt in observation.Events)
                        {
                            if (evt is not TradeEvent trade)
                                continue;
                            flow += trade.Side switch
                            {
                                TradeSide.Buy => trade.Price * trade.Quantity,
                                TradeSide.Sell => -(trade.Price * trade.Quantity),
                                _ => 0m
                            };
                        }
                    }
                    return flow;
                default:
                    throw new InvalidOperationException(
                        $"Unknown raw field '{field}'. Supported fields: symbol, timestamp, mid_price, bid_price, ask_price, " +
                        "last_price, spread, spread_bps, depth, bid_depth, ask_depth, volume, trade_count, vwap, " +
                        "open_price, high_price, low_price, close_price, trade_flow");
            }
        }
    }
}