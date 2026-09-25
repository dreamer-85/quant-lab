namespace QuantConnect.Research.Engine.Ingest.Binance
{
    /// <summary>
    /// Binance public market data helpers: WebSocket endpoint construction, kline interval
    /// mapping and timestamp conversion. All streams are public (no authentication required).
    /// </summary>
    internal static class BinanceApi
    {
        /// <summary>
        /// Legacy spot websocket endpoint (wss://stream.binance.com:9443). Binance is
        /// retiring it in favour of the load-balanced <see cref="SpotWsAlt"/>.
        /// </summary>
        public const string SpotWs = "wss://stream.binance.com:9443";

        /// <summary>
        /// Primary spot websocket endpoint (443, load-balanced); defaults for spot live feeds.
        /// </summary>
        public const string SpotWsAlt = "wss://data-stream.binance.vision";

        public const string UsdmWs = "wss://fstream.binance.com";

        public static string CategoryFor(JobDataSource source)
        {
            var category = string.IsNullOrWhiteSpace(source?.Category) ? "spot" : source.Category;
            return category.ToLowerInvariant();
        }

        public static string WsBase(JobDataSource source)
        {
            if (source != null && !string.IsNullOrWhiteSpace(source.WsEndpoint))
            {
                return source.WsEndpoint;
            }
            return CategoryFor(source) == "usdm" ? UsdmWs : SpotWsAlt;
        }

        /// <summary>
        /// Combined-stream URL: all requested topics are joined into one connection path.
        /// </summary>
        public static string CombinedStreamUri(string baseUrl, IEnumerable<string> topics)
        {
            return $"{baseUrl}/stream?streams={string.Join("/", topics)}";
        }

        /// <summary>
        /// Maps a Lean resolution to the Binance kline interval token.
        /// </summary>
        public static string KlineIntervalFor(QuantConnect.Resolution resolution)
        {
            return resolution switch
            {
                QuantConnect.Resolution.Tick => "1s",
                QuantConnect.Resolution.Second => "1s",
                QuantConnect.Resolution.Hour => "1h",
                QuantConnect.Resolution.Daily => "1d",
                _ => "1m"
            };
        }

        /// <summary>
        /// Maps a Lean resolution to the bar period produced by <see cref="KlineIntervalFor"/>.
        /// </summary>
        public static TimeSpan BarPeriodFor(QuantConnect.Resolution resolution)
        {
            return resolution switch
            {
                QuantConnect.Resolution.Tick => TimeSpan.FromSeconds(1),
                QuantConnect.Resolution.Second => TimeSpan.FromSeconds(1),
                QuantConnect.Resolution.Hour => TimeSpan.FromHours(1),
                QuantConnect.Resolution.Daily => TimeSpan.FromDays(1),
                _ => TimeSpan.FromMinutes(1)
            };
        }

        public static string TradeTopic(string symbol) => $"{symbol.ToLowerInvariant()}@trade";

        public static string BookTickerTopic(string symbol) => $"{symbol.ToLowerInvariant()}@bookTicker";

        public static string KlineTopic(string symbol, string interval) => $"{symbol.ToLowerInvariant()}@kline_{interval}";

        public static DateTime FromUnixMs(long ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
    }
}