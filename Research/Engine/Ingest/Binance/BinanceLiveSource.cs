using System.Collections.Concurrent;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Ingest.Binance
{
    /// <summary>
    /// Live adapter over Binance's public WebSocket combined-stream feed. One connection
    /// subscribes to the streams requested by the job (@trade, @bookTicker, @kline_*) and
    /// pushes normalized <see cref="MarketEvent"/>s into per-type bounded queues that the
    /// replay engine consumes.
    ///
    /// Live streams are inherently non-deterministic (real-time arrivals); the job should use
    /// <see cref="Replay.ReorderMode.InOrderStreaming"/> so the merger consumes one ordered
    /// sub-stream per type instead of sorting a never-ending stream.
    /// </summary>
    internal static class BinanceLiveSource
    {
        /// <summary>
        /// Resolves the wall-clock deadline for a live stream from the source config and job.
        /// </summary>
        public static DateTime? ResolveDeadline(ResearchJob job, JobDataSource source)
        {
            if (source != null && source.LiveDurationSeconds > 0)
            {
                return DateTime.UtcNow.AddSeconds(source.LiveDurationSeconds);
            }

            var end = DateTime.SpecifyKind(job.EndTime, DateTimeKind.Utc);
            return end > DateTime.UtcNow ? end : (DateTime?)null;
        }

        /// <summary>
        /// Builds the list of WS streams requested by the job for a symbol.
        /// </summary>
        public static List<string> BuildTopics(ResearchJob job, JobDataSource source, string symbol)
        {
            var topics = new List<string>();
            foreach (var type in job.EventTypes)
            {
                switch (type)
                {
                    case MarketEventType.Trade:
                        topics.Add(BinanceApi.TradeTopic(symbol));
                        break;
                    case MarketEventType.Quote:
                        topics.Add(BinanceApi.BookTickerTopic(symbol));
                        break;
                    case MarketEventType.Bar:
                        topics.Add(BinanceApi.KlineTopic(symbol, BinanceApi.KlineIntervalFor(job.Resolution)));
                        break;
                }
            }
            return topics.Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Owns the live Binance WebSocket connection and per-type event queues. Reference-counted
    /// so the connection closes when the last consumer stream finishes (the stream merger opens
    /// one enumerator per sub-stream).
    /// </summary>
    internal sealed class BinanceLiveSession : IDisposable
    {
        private const int QueueCapacity = 65536;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

        private readonly IWsTransport _transport;
        private readonly string _uri;
        private readonly Symbol _symbol;
        private readonly Dictionary<MarketEventType, BlockingCollection<MarketEvent>> _queues;
        private readonly Dictionary<string, MarketEventType> _streamTypes = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly int _klineIntervalMs;

        private int _refs;
        private volatile Exception _fault;
        private Task _receiver;

        public BinanceLiveSession(
            string uri,
            Symbol symbol,
            IEnumerable<MarketEventType> types,
            int klineIntervalMs,
            Func<IWsTransport> transportFactory)
        {
            _uri = uri;
            _symbol = symbol;
            _klineIntervalMs = klineIntervalMs;
            _transport = transportFactory();
            _queues = new Dictionary<MarketEventType, BlockingCollection<MarketEvent>>();
            foreach (var type in types)
            {
                _queues[type] = new BlockingCollection<MarketEvent>(QueueCapacity);
            }
        }

        /// <summary>
        /// Opens the connection (the streams are baked into the URL) and starts the receive loop.
        /// </summary>
        public void Start(IEnumerable<string> topics)
        {
            foreach (var topic in topics)
            {
                if (topic.EndsWith("@trade", StringComparison.Ordinal))
                {
                    _streamTypes[topic] = MarketEventType.Trade;
                }
                else if (topic.EndsWith("@bookTicker", StringComparison.Ordinal))
                {
                    _streamTypes[topic] = MarketEventType.Quote;
                }
                else if (topic.Contains("@kline_", StringComparison.Ordinal))
                {
                    _streamTypes[topic] = MarketEventType.Bar;
                }
            }

            _receiver = Task.Run(() => ReceiveLoopAsync(topics.ToList(), _cts.Token), _cts.Token);
        }

        /// <summary>
        /// Yields events for a single event type until the deadline, the external cancellation
        /// token, or the feed stopping. One consumer per active stream.
        /// </summary>
        public IEnumerable<MarketEvent> Stream(MarketEventType type, DateTime? until, CancellationToken external)
        {
            if (!_queues.TryGetValue(type, out var queue))
            {
                yield break;
            }

            Interlocked.Increment(ref _refs);
            try
            {
                while (true)
                {
                    if (_fault != null)
                    {
                        throw new InvalidOperationException($"BINANCE_LIVE feed faulted: {_fault.Message}", _fault);
                    }
                    if (external.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(external);
                    }
                    if (until.HasValue && DateTime.UtcNow >= until.Value)
                    {
                        yield break;
                    }
                    if (queue.IsAddingCompleted && queue.Count == 0)
                    {
                        yield break;
                    }

                    if (queue.TryTake(out var evt, 250, _cts.Token))
                    {
                        yield return evt;
                    }
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _refs) == 0)
                {
                    Stop();
                }
            }
        }

        private async Task ReceiveLoopAsync(List<string> topics, CancellationToken ct)
        {
            try
            {
                var uri = BinanceApi.CombinedStreamUri(_uri, topics);
                await _transport.ConnectAsync(uri, ct).ConfigureAwait(false);

                var pingTask = Task.Run(() => PingLoopAsync(ct), ct);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var frameJson = await _transport.ReceiveTextAsync(ct).ConfigureAwait(false);
                        if (frameJson == null)
                        {
                            break;
                        }
                        Dispatch(frameJson, DateTime.UtcNow);
                    }
                }
                finally
                {
                    await _transport.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _fault = ex;
            }
            finally
            {
                foreach (var queue in _queues.Values)
                {
                    try
                    {
                        queue.CompleteAdding();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        private async Task PingLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PingInterval, ct).ConfigureAwait(false);
                    await _transport.SendTextAsync("{\"method\":\"PING\"}", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _fault = ex;
            }
        }

        private void Dispatch(string frameJson, DateTime arrivedUtc)
        {
            if (!BinanceJson.TryParseWsFrame(frameJson, out var frame))
            {
                return;
            }

            var type = ResolveType(frame);
            if (!type.HasValue)
            {
                return;
            }

            switch (type.Value)
            {
                case MarketEventType.Trade:
                    EnqueueTrade(frame, arrivedUtc);
                    break;
                case MarketEventType.Quote:
                    EnqueueQuote(frame, arrivedUtc);
                    break;
                case MarketEventType.Bar:
                    EnqueueBar(frame);
                    break;
            }
        }

        private MarketEventType? ResolveType(BinanceWsFrame frame)
        {
            if (_streamTypes.TryGetValue(frame.Stream, out var byStream))
            {
                return byStream;
            }

            // Raw single-stream connection frames carry no "stream" name; resolve by event type.
            if (string.IsNullOrEmpty(frame.Stream))
            {
                return frame.EventName switch
                {
                    "trade" => MarketEventType.Trade,
                    "bookTicker" => MarketEventType.Quote,
                    "kline" => MarketEventType.Bar,
                    _ => (MarketEventType?)null
                };
            }

            return null;
        }

        private void EnqueueTrade(BinanceWsFrame frame, DateTime arrivedUtc)
        {
            if (!frame.Trade.HasValue)
            {
                return;
            }

            var trade = frame.Trade.Value;
            var provenance = MarketEventNormalizer.CreateProvenance(_symbol, "binance", "trade");
            var evt = MarketEventNormalizer.CreateTrade(
                _symbol,
                BinanceApi.FromUnixMs(trade.TimestampMs),
                trade.Price,
                trade.Quantity,
                trade.BuyerIsMaker ? TradeSide.Sell : TradeSide.Buy,
                eventId: trade.EventId,
                sequenceNumber: long.TryParse(trade.EventId, out var tradeId) ? tradeId : trade.TimestampMs,
                provenance: provenance);
            evt.ArrivalTimestamp = arrivedUtc;
            Add(MarketEventType.Trade, evt);
        }

        private void EnqueueQuote(BinanceWsFrame frame, DateTime arrivedUtc)
        {
            if (!frame.Quote.HasValue)
            {
                return;
            }

            var quote = frame.Quote.Value;
            var ts = quote.TimestampMs > 0 ? BinanceApi.FromUnixMs(quote.TimestampMs) : arrivedUtc;
            var provenance = MarketEventNormalizer.CreateProvenance(_symbol, "binance", "quote");
            var evt = MarketEventNormalizer.CreateQuote(
                _symbol,
                ts,
                quote.Bid,
                quote.BidSize,
                quote.Ask,
                quote.AskSize,
                eventId: quote.UpdateId > 0 ? quote.UpdateId.ToString() : null,
                sequenceNumber: quote.UpdateId > 0 ? quote.UpdateId : (long?)null,
                provenance: provenance);
            evt.ArrivalTimestamp = arrivedUtc;
            Add(MarketEventType.Quote, evt);
        }

        private void EnqueueBar(BinanceWsFrame frame)
        {
            if (!frame.Bar.HasValue)
            {
                return;
            }

            var bar = frame.Bar.Value;
            var provenance = MarketEventNormalizer.CreateProvenance(_symbol, "binance", "bar");
            var evt = MarketEventNormalizer.CreateBar(
                _symbol,
                BinanceApi.FromUnixMs(bar.OpenTimeMs),
                TimeSpan.FromMilliseconds(_klineIntervalMs),
                bar.Open,
                bar.High,
                bar.Low,
                bar.Close,
                bar.Volume,
                sequenceNumber: bar.OpenTimeMs,
                provenance: provenance);
            evt.ArrivalTimestamp = DateTime.UtcNow;
            Add(MarketEventType.Bar, evt);
        }

        private void Add(MarketEventType type, MarketEvent evt)
        {
            if (evt.ArrivalTimestamp == null)
            {
                evt.ArrivalTimestamp = DateTime.UtcNow;
            }

            if (_queues.TryGetValue(type, out var queue))
            {
                if (!queue.TryAdd(evt, 1, _cts.Token))
                {
                    _fault = new InvalidOperationException("live queue overflow; consumer too slow");
                }
            }
        }

        /// <summary>
        /// Closes the connection and releases the queues. Safe to call more than once.
        /// </summary>
        public void Stop()
        {
            try
            {
                _cts.Cancel();
                _receiver?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            foreach (var queue in _queues.Values)
            {
                try
                {
                    queue.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}