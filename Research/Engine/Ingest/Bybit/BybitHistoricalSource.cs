using System.Text.Json;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Historical (REST backfill) adapter over Bybit's public market data endpoints.
    /// Yields normalized <see cref="MarketEvent"/>s directly (no zip staging required).
    ///
    /// What the public REST API can truthfully backfill:
    ///   - kline OHLCV bars over any paginated [start, end] range  (-> BarEvent, resolution based)
    ///   - the last ~1000 public trades                              (-> TradeEvent, near-real-time only)
    ///   - a point-in-time order book snapshot                       (-> QuoteEvent for L1, OrderBookSnapshotEvent for L2)
    /// The public API publishes no deep historical trade/quote stream, so Trade/Quote reachback
    /// is limited to a recent window; bars are the full-depth historical signal.
    /// </summary>
    internal static class BybitHistoricalSource
    {
        private const string RecentWindow = "00:10:00";

        /// <summary>
        /// Determines whether the job window overlaps the live tail the REST API can replay
        /// (trades and order book snapshots are only truthfully reachable near "now").
        /// </summary>
        public static bool HasReachback(DateTime endTimeUtc)
        {
            return endTimeUtc >= DateTime.UtcNow.AddMinutes(-10);
        }

        /// <summary>
        /// Streams kline bars as <see cref="BarEvent"/> over the job's [start, end] window,
        /// paginated lazily so memory stays bounded by page size.
        /// </summary>
        public static IEnumerable<MarketEvent> Klines(
            ResearchJob job,
            JobDataSource source,
            Symbol symbol,
            IHttpTransport http,
            CancellationToken cancellationToken = default)
        {
            if (!BybitApi.HasKlineInterval(job.Resolution))
            {
                throw new NotSupportedException(
                    $"Historical kline backfill does not support resolution {job.Resolution} " +
                    "(choose Minute, Hour or Daily, or use live/file mode for tick data).");
            }

            var baseUrl = BybitApi.RestBase(source);
            var category = BybitApi.CategoryFor(source);
            var interval = BybitApi.IntervalFor(job.Resolution);
            var period = BybitApi.PeriodFor(job.Resolution);
            var pageSize = BybitApi.ClampPageSize(source?.PageSize ?? 1000);
            var startMs = BybitApi.ToUnixMs(job.StartTime);
            var endMs = BybitApi.ToUnixMs(job.EndTime);
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "bybit", "bar",
                datasetVersion: $"kline-{interval}");

            var cursorMs = startMs;
            while (cursorMs < endMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = BybitApi.KlineUri(baseUrl, category, symbol.Value, interval, cursorMs, endMs, pageSize);
                var page = FetchKlines(uri, http, cancellationToken);
                if (page.Count == 0)
                {
                    yield break;
                }

                foreach (var kline in page)
                {
                    if (kline.StartMs > endMs)
                    {
                        yield break;
                    }
                    if (kline.StartMs < startMs)
                    {
                        continue;
                    }

                    yield return MarketEventNormalizer.CreateBar(
                        symbol,
                        BybitApi.FromUnixMs(kline.StartMs),
                        period,
                        kline.Open,
                        kline.High,
                        kline.Low,
                        kline.Close,
                        kline.Volume,
                        sequenceNumber: kline.StartMs,
                        provenance: provenance);
                }

                var nextMs = page[^1].StartMs + (long)period.TotalMilliseconds;
                if (nextMs <= cursorMs)
                {
                    break;
                }
                cursorMs = nextMs;
            }
        }

        /// <summary>
        /// Streams the recent public trade tape as <see cref="TradeEvent"/>. Empty when the job
        /// window does not overlap the reachable recent window.
        /// </summary>
        public static IEnumerable<MarketEvent> RecentTrades(
            ResearchJob job,
            JobDataSource source,
            Symbol symbol,
            IHttpTransport http,
            CancellationToken cancellationToken = default)
        {
            if (!HasReachback(DateTime.SpecifyKind(job.EndTime, DateTimeKind.Utc)))
            {
                Console.WriteLine($"  [bybit] job window predates the public trade reachback window; skipping {symbol.Value} trades");
                yield break;
            }

            var baseUrl = BybitApi.RestBase(source);
            var category = BybitApi.CategoryFor(source);
            var pageSize = BybitApi.ClampPageSize(source?.PageSize ?? 1000);
            var startMs = BybitApi.ToUnixMs(job.StartTime);
            var endMs = BybitApi.ToUnixMs(job.EndTime);
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "bybit", "trade");

            var uri = BybitApi.RecentTradeUri(baseUrl, category, symbol.Value, pageSize);
            foreach (var trade in FetchRecentTrades(uri, http, cancellationToken))
            {
                if (trade.TimeMs < startMs || trade.TimeMs > endMs)
                {
                    continue;
                }

                yield return MarketEventNormalizer.CreateTrade(
                    symbol,
                    BybitApi.FromUnixMs(trade.TimeMs),
                    trade.Price,
                    trade.Size,
                    trade.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? TradeSide.Buy
                        : trade.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? TradeSide.Sell
                        : TradeSide.Unknown,
                    eventId: trade.EventId,
                    sequenceNumber: trade.TimeMs,
                    provenance: provenance);
            }
        }

        /// <summary>
        /// Emits one point-in-time book event: a <see cref="QuoteEvent"/> for depth 1 or an
        /// <see cref="OrderBookSnapshotEvent"/> for deeper books. Empty when the job window does
        /// not overlap the reachable recent window.
        /// </summary>
        public static IEnumerable<MarketEvent> OrderBook(
            ResearchJob job,
            JobDataSource source,
            Symbol symbol,
            IHttpTransport http,
            CancellationToken cancellationToken = default)
        {
            if (!HasReachback(DateTime.SpecifyKind(job.EndTime, DateTimeKind.Utc)))
            {
                yield break;
            }

            var baseUrl = BybitApi.RestBase(source);
            var category = BybitApi.CategoryFor(source);
            var depth = BybitApi.ClampDepth(source?.OrderBookDepth);
            var uri = BybitApi.OrderBookUri(baseUrl, category, symbol.Value, depth);
            var book = FetchOrderBook(uri, http, cancellationToken);
            if (book == null || (book.Bids.Count == 0 && book.Asks.Count == 0))
            {
                yield break;
            }

            var timestamp = BybitApi.FromUnixMs(book.TsMs);
            if (depth == 1)
            {
                var bestBid = book.Bids.OrderByDescending(l => l.Price).FirstOrDefault();
                var bestAsk = book.Asks.OrderBy(l => l.Price).FirstOrDefault();
                yield return MarketEventNormalizer.CreateQuote(
                    symbol,
                    timestamp,
                    bestBid.Price,
                    bestBid.Size,
                    bestAsk.Price,
                    bestAsk.Size,
                    sequenceNumber: book.TsMs,
                    provenance: MarketEventNormalizer.CreateProvenance(symbol, "bybit", "quote"));
            }
            else
            {
                yield return MarketEventNormalizer.CreateOrderBookSnapshot(
                    symbol,
                    timestamp,
                    book.Bids.Select(l => new OrderBookLevel { Price = l.Price, Quantity = l.Size, OrderCount = l.OrderCount }),
                    book.Asks.Select(l => new OrderBookLevel { Price = l.Price, Quantity = l.Size, OrderCount = l.OrderCount }),
                    sequenceNumber: book.TsMs,
                    provenance: MarketEventNormalizer.CreateProvenance(symbol, "bybit", "orderbook"));
            }
        }

        private static List<BybitKline> FetchKlines(Uri uri, IHttpTransport http, CancellationToken ct)
        {
            using var doc = FetchJson(uri, http, ct);
            var result = EnsureResult(doc, "kline");
            var list = new List<BybitKline>();
            if (result.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in listElement.EnumerateArray())
                {
                    if (BybitJson.TryParseKlineRow(row, out var kline))
                    {
                        list.Add(kline);
                    }
                }
            }
            return list.OrderBy(k => k.StartMs).ToList();
        }

        private static List<BybitTrade> FetchRecentTrades(Uri uri, IHttpTransport http, CancellationToken ct)
        {
            using var doc = FetchJson(uri, http, ct);
            var result = EnsureResult(doc, "recent-trade");
            var list = new List<BybitTrade>();
            if (result.TryGetProperty("list", out var listElement) && listElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in listElement.EnumerateArray())
                {
                    if (BybitJson.TryParseTradeItem(item, out var trade))
                    {
                        list.Add(trade);
                    }
                }
            }
            return list.OrderBy(t => t.TimeMs).ToList();
        }

        private static BybitOrderBook FetchOrderBook(Uri uri, IHttpTransport http, CancellationToken ct)
        {
            using var doc = FetchJson(uri, http, ct);
            var result = EnsureResult(doc, "orderbook");
            var idElement = result.TryGetProperty("ts", out var ts) ? ts : default;
            var bids = new List<BybitLevel>();
            var asks = new List<BybitLevel>();
            if (result.TryGetProperty("b", out var b) && b.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in b.EnumerateArray())
                {
                    if (BybitJson.TryParseLevel(item, out var level))
                    {
                        bids.Add(level);
                    }
                }
            }
            if (result.TryGetProperty("a", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in a.EnumerateArray())
                {
                    if (BybitJson.TryParseLevel(item, out var level))
                    {
                        asks.Add(level);
                    }
                }
            }
            return new BybitOrderBook
            {
                TsMs = idElement.GetInt64(),
                Bids = bids,
                Asks = asks
            };
        }

        private static JsonDocument FetchJson(Uri uri, IHttpTransport http, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = http.SendAsync(request, ct).GetAwaiter().GetResult();
            var json = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return JsonDocument.Parse(json);
        }

        private static JsonElement EnsureResult(JsonDocument doc, string endpoint)
        {
            var root = doc.RootElement;
            var retCode = root.TryGetProperty("retCode", out var code) ? code.GetInt64() : 0;
            if (retCode != 0)
            {
                var retMsg = root.TryGetProperty("retMsg", out var msg) ? msg.GetString() : string.Empty;
                throw new InvalidOperationException($"Bybit {endpoint} request failed: retCode={retCode} retMsg={retMsg}");
            }
            return root.TryGetProperty("result", out var result) ? result : default;
        }
    }

    /// <summary>
    /// Parsed order book snapshot.
    /// </summary>
    internal class BybitOrderBook
    {
        public long TsMs { get; set; }

        public List<BybitLevel> Bids { get; set; } = new();

        public List<BybitLevel> Asks { get; set; } = new();
    }
}