namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Bybit v5 public market data helpers: endpoint construction, resolution mapping and
    /// timestamp conversion. All REST endpoints used are public (no authentication required).
    /// </summary>
    internal static class BybitApi
    {
        public const string DefaultRestBase = "https://api.bybit.com";
        public const string DefaultWsSpot = "wss://stream.bybit.com/v5/public/spot";
        public const string DefaultWsLinear = "wss://stream.bybit.com/v5/public/linear";

        private static readonly int[] AllowedDepths = { 1, 10, 50, 200, 500 };

        public static string CategoryFor(JobDataSource source)
        {
            var category = string.IsNullOrWhiteSpace(source?.Category) ? "spot" : source.Category;
            return category.ToLowerInvariant() == "perpetual" ? "linear" : category.ToLowerInvariant();
        }

        public static string RestBase(JobDataSource source)
        {
            if (source != null && !string.IsNullOrWhiteSpace(source.RestEndpoint))
            {
                return source.RestEndpoint.TrimEnd('/');
            }
            return DefaultRestBase;
        }

        public static string WsBase(JobDataSource source)
        {
            if (source != null && !string.IsNullOrWhiteSpace(source.WsEndpoint))
            {
                return source.WsEndpoint;
            }
            return CategoryFor(source) == "linear" ? DefaultWsLinear : DefaultWsSpot;
        }

        /// <summary>
        /// Maps a Lean resolution to the Bybit kline interval that is >= the requested resolution.
        /// </summary>
        public static string IntervalFor(QuantConnect.Resolution resolution)
        {
            return resolution switch
            {
                QuantConnect.Resolution.Hour => "60",
                QuantConnect.Resolution.Daily => "D",
                _ => "1"
            };
        }

        /// <summary>
        /// Maps a Lean resolution to the bar period produced by <see cref="IntervalFor"/>.
        /// </summary>
        public static TimeSpan PeriodFor(QuantConnect.Resolution resolution)
        {
            return resolution switch
            {
                QuantConnect.Resolution.Hour => TimeSpan.FromHours(1),
                QuantConnect.Resolution.Daily => TimeSpan.FromDays(1),
                _ => TimeSpan.FromMinutes(1)
            };
        }

        public static bool HasKlineInterval(QuantConnect.Resolution resolution)
        {
            return resolution == QuantConnect.Resolution.Minute
                || resolution == QuantConnect.Resolution.Hour
                || resolution == QuantConnect.Resolution.Daily;
        }

        public static Uri KlineUri(
            string baseUrl,
            string category,
            string symbol,
            string interval,
            long startMs,
            long endMs,
            int limit)
        {
            var url = $"{baseUrl}/v5/market/kline?category={category}&symbol={symbol}&interval={interval}" +
                      $"&start={startMs}&end={endMs}&limit={ClampPageSize(limit)}";
            return new Uri(url);
        }

        public static Uri RecentTradeUri(string baseUrl, string category, string symbol, int limit)
        {
            return new Uri($"{baseUrl}/v5/market/recent-trade?category={category}&symbol={symbol}&limit={ClampPageSize(limit)}");
        }

        public static Uri OrderBookUri(string baseUrl, string category, string symbol, int depth)
        {
            return new Uri($"{baseUrl}/v5/market/orderbook?category={category}&symbol={symbol}&limit={ClampDepth(depth)}");
        }

        public static string TradeTopic(string symbol) => $"publicTrade.{symbol}";

        public static string OrderBookTopic(string symbol, int depth) => $"orderbook.{ClampDepth(depth)}.{symbol}";

        public static string KlineTopic(string symbol, string interval) => $"kline.{interval}.{symbol}";

        public static long ToUnixMs(DateTime value)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }

        public static DateTime FromUnixMs(long ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }

        public static int ClampPageSize(int pageSize)
        {
            return Math.Clamp(pageSize, 1, 1000);
        }

        public static int ClampDepth(int depth)
        {
            return AllowedDepths.Contains(depth) ? depth : 1;
        }

        public static int ClampDepth(string depth)
        {
            return int.TryParse(depth, out var parsed) ? ClampDepth(parsed) : 1;
        }
    }
}