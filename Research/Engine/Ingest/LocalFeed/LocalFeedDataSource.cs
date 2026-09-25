using System.Globalization;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;

namespace QuantConnect.Research.Engine.Ingest.LocalFeed
{
    /// <summary>
    /// Reads market data staged by the DataFeeds tooling (DataFeeds/Historical|Live entry scripts)
    /// and normalizes it into the engine's <see cref="MarketEvent"/> model, so feed output and
    /// engine replay share one pipeline.
    ///
    /// The reader expects the DataFeeds on-disk layout under a feed root (by default the job's
    /// data root, i.e. ``--data-dir``)::
    ///
    ///     <feed_root>/<market>/<provider>/<symbol>/
    ///         bars_<seconds>.csv     timestamp_ms,open,high,low,close,volume
    ///         trades.csv             timestamp_ms,price,size,side,trade_id
    ///         quotes.csv             timestamp_ms,bid_price,bid_size,ask_price,ask_size
    ///         book_updates.csv       timestamp_ms,side,price,quantity,action
    ///
    /// market = asset class ("crypto" / "forex"), provider = "okx"|"bybit"|"binance"|"deriv".
    /// Each file must be sorted ascending by timestamp; sub-streams are exposed through
    /// <see cref="IStreamingEventSource"/> so the replay engine merges them deterministically
    /// with no materialization.
    /// </summary>
    public sealed class LocalFeedDataSource : IEventDataSource, IStreamingEventSource
    {
        private readonly ResearchJob _job;
        private readonly JobDataSource _source;
        private readonly string _feedRoot;

        public LocalFeedDataSource(ResearchJob job, JobDataSource source, string feedRoot = null)
        {
            _job = job ?? throw new ArgumentNullException(nameof(job));
            _source = source ?? job.Source ?? new JobDataSource();
            _feedRoot = string.IsNullOrWhiteSpace(feedRoot) ? "." : feedRoot;

            if (!_source.Mode.Equals("feed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"LocalFeedDataSource requires a \"feed\" source (got \"{_source.Mode}\")");
            }
        }

        public IEnumerable<MarketEvent> GetEvents(ResearchJob job, QuantConnect.Symbol symbol)
        {
            return EventStreamMerger.Merge(GetEventStreams(job, symbol));
        }

        /// <summary>
        /// Yields one ordered, lazily-parsed sub-stream per requested event type whose feed file
        /// exists in the DataFeeds layout.
        /// </summary>
        public IEnumerable<IEnumerable<MarketEvent>> GetEventStreams(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var dir = ResolveSymbolDir(job, symbol);
            if (dir == null)
            {
                Console.WriteLine($"  [feed] no {_source.Provider} data for {symbol.Value} under {_feedRoot} (market={MarketLabel(job)}); skipping");
                yield break;
            }

            var types = job.EventTypes.Where(IsStagedType).Distinct().ToList();
            var resolutionSeconds = ResolutionSeconds(job.Resolution);

            foreach (var type in types)
            {
                switch (type)
                {
                    case MarketEventType.Bar:
                        var barFile = ResolveBarFile(dir, resolutionSeconds);
                        if (barFile != null)
                        {
                            yield return ReadBars(barFile, symbol, job, resolutionSeconds);
                        }
                        break;
                    case MarketEventType.Trade:
                        var tradeFile = Path.Combine(dir.FullName, "trades.csv");
                        if (File.Exists(tradeFile))
                        {
                            yield return ReadTrades(tradeFile, symbol);
                        }
                        break;
                    case MarketEventType.Quote:
                        var quoteFile = Path.Combine(dir.FullName, "quotes.csv");
                        if (File.Exists(quoteFile))
                        {
                            yield return ReadQuotes(quoteFile, symbol);
                        }
                        break;
                    case MarketEventType.OrderBookUpdate:
                        var bookFile = Path.Combine(dir.FullName, "book_updates.csv");
                        if (File.Exists(bookFile))
                        {
                            yield return ReadBookUpdates(bookFile, symbol);
                        }
                        break;
                }
            }
        }

        private static bool IsStagedType(MarketEventType type)
        {
            return type == MarketEventType.Bar
                || type == MarketEventType.Trade
                || type == MarketEventType.Quote
                || type == MarketEventType.OrderBookUpdate;
        }

        /// <summary>
        /// Matches the directory whose normalized name equals the job ticker. Provider-native
        /// separators ("-", "_", "/") are ignored so BTCUSDT matches both BTCUSDT and BTC-USDT.
        /// </summary>
        private DirectoryInfo ResolveSymbolDir(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var providerRoot = Path.Combine(_feedRoot, MarketLabel(job), ProviderLabel());
            if (!Directory.Exists(providerRoot))
            {
                return null;
            }

            var ticker = Normalize(symbol.Value);
            foreach (var candidate in Directory.EnumerateDirectories(providerRoot))
            {
                if (Normalize(Path.GetFileName(candidate)) == ticker)
                {
                    return new DirectoryInfo(candidate);
                }
            }
            return null;
        }

        private string MarketLabel(ResearchJob job)
        {
            return string.IsNullOrWhiteSpace(job.AssetClass) ? "crypto" : job.AssetClass.ToLowerInvariant();
        }

        private string ProviderLabel()
        {
            return string.IsNullOrWhiteSpace(_source.Provider) ? "bybit" : _source.Provider.ToLowerInvariant();
        }

        private static string Normalize(string value)
        {
            var chars = value.Where(c => c != '-' && c != '_' && c != '/').ToArray();
            return new string(chars).ToLowerInvariant();
        }

        private static int ResolutionSeconds(QuantConnect.Resolution resolution)
        {
            return resolution switch
            {
                QuantConnect.Resolution.Second => 1,
                QuantConnect.Resolution.Minute => 60,
                QuantConnect.Resolution.Hour => 3600,
                QuantConnect.Resolution.Daily => 86400,
                _ => 60
            };
        }

        /// <summary>
        /// Picks the bar file matching the job resolution; falls back to any bars_*.csv.
        /// </summary>
        private static string ResolveBarFile(DirectoryInfo dir, int resolutionSeconds)
        {
            var expected = Path.Combine(dir.FullName, $"bars_{resolutionSeconds}.csv");
            if (File.Exists(expected))
            {
                return expected;
            }
            foreach (var file in dir.EnumerateFiles("bars_*.csv").OrderBy(f => f.Name))
            {
                return file.FullName;
            }
            return null;
        }

        private static IEnumerable<MarketEvent> ReadBars(string path, QuantConnect.Symbol symbol, ResearchJob job, int resolutionSeconds)
        {
            var period = TimeSpan.FromSeconds(resolutionSeconds);
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "datafeeds", "bar",
                datasetVersion: Path.GetFileName(path));
            using var reader = new StreamReader(path);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("timestamp_ms", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = Split(line);
                if (parts.Length < 6)
                {
                    continue;
                }
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    continue;
                }

                yield return MarketEventNormalizer.CreateBar(
                    symbol,
                    FromUnixMs(ms),
                    period,
                    Decimal(parts[1]),
                    Decimal(parts[2]),
                    Decimal(parts[3]),
                    Decimal(parts[4]),
                    Decimal(parts[5]),
                    sequenceNumber: ms,
                    provenance: provenance);
            }
        }

        private static IEnumerable<MarketEvent> ReadTrades(string path, QuantConnect.Symbol symbol)
        {
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "datafeeds", "trade");
            using var reader = new StreamReader(path);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("timestamp_ms", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = Split(line);
                if (parts.Length < 3 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    continue;
                }
                var side = parts.Length > 3 ? parts[3].ToLowerInvariant() : "unknown";

                yield return MarketEventNormalizer.CreateTrade(
                    symbol,
                    FromUnixMs(ms),
                    Decimal(parts[1]),
                    Decimal(parts[2]),
                    side.Equals("buy", StringComparison.Ordinal) ? TradeSide.Buy
                        : side.Equals("sell", StringComparison.Ordinal) ? TradeSide.Sell
                        : TradeSide.Unknown,
                    eventId: parts.Length > 4 ? parts[4] : null,
                    sequenceNumber: ms,
                    provenance: provenance);
            }
        }

        private static IEnumerable<MarketEvent> ReadQuotes(string path, QuantConnect.Symbol symbol)
        {
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "datafeeds", "quote");
            using var reader = new StreamReader(path);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("timestamp_ms", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = Split(line);
                if (parts.Length < 5 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    continue;
                }

                yield return MarketEventNormalizer.CreateQuote(
                    symbol,
                    FromUnixMs(ms),
                    Decimal(parts[1]),
                    Decimal(parts[2]),
                    Decimal(parts[3]),
                    Decimal(parts[4]),
                    sequenceNumber: ms,
                    provenance: provenance);
            }
        }

        /// <summary>
        /// Order book level updates (timestamp, side bid/ask, price, quantity, optional action
        /// add|modify|remove, default add). Each row carries the new absolute size at the price.
        /// </summary>
        private static IEnumerable<MarketEvent> ReadBookUpdates(string path, QuantConnect.Symbol symbol)
        {
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "datafeeds", "orderbook");
            using var reader = new StreamReader(path);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("timestamp_ms", StringComparison.Ordinal))
                {
                    continue;
                }
                var parts = Split(line);
                if (parts.Length < 4 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                {
                    continue;
                }

                var side = parts[1].Equals("ask", StringComparison.OrdinalIgnoreCase)
                    ? OrderBookSide.Ask
                    : OrderBookSide.Bid;
                var action = OrderBookUpdateAction.Add;
                if (parts.Length > 4)
                {
                    action = parts[4].ToLowerInvariant() switch
                    {
                        "remove" => OrderBookUpdateAction.Remove,
                        "modify" => OrderBookUpdateAction.Modify,
                        _ => OrderBookUpdateAction.Add
                    };
                }

                yield return MarketEventNormalizer.CreateOrderBookUpdate(
                    symbol,
                    FromUnixMs(ms),
                    side,
                    Decimal(parts[2]),
                    Decimal(parts[3]),
                    action,
                    sequenceNumber: ms,
                    provenance: provenance);
            }
        }

        private static string[] Split(string line)
        {
            return line.Split(',', StringSplitOptions.TrimEntries);
        }

        private static decimal Decimal(string value)
        {
            return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0m;
        }

        private static DateTime FromUnixMs(long ms)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
    }
}