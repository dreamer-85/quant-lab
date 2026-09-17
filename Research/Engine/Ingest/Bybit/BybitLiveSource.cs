using System.Collections.Concurrent;
using System.Text.Json;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Live adapter over Bybit's public WebSocket v5 feed. One connection subscribes to the
    /// topics requested by the job (publicTrade, orderbook.depth, kline) and pushes normalized
    /// <see cref="MarketEvent"/>s into per-type bounded queues that the replay engine consumes.
    ///
    /// Live streams are inherently non-deterministic (real-time arrivals); the job should use
    /// <see cref="Replay.ReorderMode.InOrderStreaming"/> so the merger consumes one ordered
    /// sub-stream per type instead of sorting a never-ending stream.
    /// </summary>
    internal static class BybitLiveSource
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
        /// Builds the list of WS topics requested by the job for a symbol.
        /// </summary>
        public static List<string> BuildTopics(ResearchJob job, JobDataSource source, string symbol)
        {
            var topics = new List<string>();
            foreach (var type in job.EventTypes)
            {
                switch (type)
                {
                    case MarketEventType.Trade:
                        topics.Add(BybitApi.TradeTopic(symbol));
                        break;
                    case MarketEventType.Quote:
                    case MarketEventType.OrderBookSnapshot:
                    case MarketEventType.OrderBookUpdate:
                        topics.Add(BybitApi.OrderBookTopic(symbol, BybitApi.ClampDepth(source?.OrderBookDepth)));
                        break;
                    case MarketEventType.Bar:
                        if (BybitApi.HasKlineInterval(job.Resolution))
                        {
                            topics.Add(BybitApi.KlineTopic(symbol, BybitApi.IntervalFor(job.Resolution)));
                        }
                        break;
                }
            }
            return topics.Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Owns the live WebSocket connection and per-type event queues. Reference-counted so the
    /// connection closes when the last consumer stream finishes (the stream merger opens one
    /// enumerator per sub-stream).
    /// </summary>
    internal sealed class BybitLiveSession : IDisposable
    {
        private const int QueueCapacity = 65536;
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

        private readonly IWsTransport _transport;
        private readonly string _uri;
        private readonly Symbol _symbol;
        private readonly Dictionary<MarketEventType, BlockingCollection<MarketEvent>> _queues;
        private readonly Dictionary<string, MarketEventType> _topicTypes = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly int _depth;
        private readonly int _klineIntervalMs;
        private readonly IWsFrameArchive _archive;

        private readonly BybitBookTracker _book = new();
        private int _refs;
        private volatile Exception _fault;
        private Task _receiver;

        public BybitLiveSession(
            string uri,
            Symbol symbol,
            IEnumerable<MarketEventType> types,
            int depth,
            int klineIntervalMs,
            Func<IWsTransport> transportFactory,
            IWsFrameArchive archive = null)
        {
            _uri = uri;
            _symbol = symbol;
            _depth = depth;
            _klineIntervalMs = klineIntervalMs;
            _transport = transportFactory();
            _archive = archive;
            _queues = new Dictionary<MarketEventType, BlockingCollection<MarketEvent>>();
            foreach (var type in types)
            {
                _queues[type] = new BlockingCollection<MarketEvent>(QueueCapacity);
            }
        }

        /// <summary>
        /// Opens the connection, subscribes and starts the receive loop.
        /// </summary>
        public void Start(IEnumerable<string> topics)
        {
            foreach (var topic in topics)
            {
                if (topic.StartsWith("publicTrade", StringComparison.Ordinal))
                {
                    _topicTypes[topic] = MarketEventType.Trade;
                }
                else if (topic.StartsWith("kline", StringComparison.Ordinal))
                {
                    _topicTypes[topic] = MarketEventType.Bar;
                }
                else if (topic.StartsWith("orderbook", StringComparison.Ordinal))
                {
                    _topicTypes[topic] = _depth == 1 ? MarketEventType.Quote : MarketEventType.OrderBookSnapshot;
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
                        throw new InvalidOperationException($"BYBIT_LIVE feed faulted: {_fault.Message}", _fault);
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
                await _transport.ConnectAsync(_uri, ct).ConfigureAwait(false);

                var subscribePayload = JsonSerializer.Serialize(new { op = "subscribe", args = topics });
                await _transport.SendTextAsync(subscribePayload, ct).ConfigureAwait(false);

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
                    await _transport.SendTextAsync("{\"op\":\"ping\"}", ct).ConfigureAwait(false);
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
            if (!BybitJson.TryParseWsFrame(frameJson, out var frame))
            {
                return;
            }

            if (!_topicTypes.TryGetValue(frame.Topic, out var type))
            {
                return;
            }

            // Record the raw frame plus its reception time so the capture can be replayed
            // byte-for-byte (archive replay reproduces these exact events).
            _archive?.Record(frameJson, arrivedUtc);

            switch (type)
            {
                case MarketEventType.Trade:
                    EnqueueTrades(frame, arrivedUtc);
                    break;
                case MarketEventType.Bar:
                    EnqueueBars(frame, arrivedUtc);
                    break;
                case MarketEventType.Quote:
                case MarketEventType.OrderBookSnapshot:
                    EnqueueBook(type, frame, arrivedUtc);
                    break;
            }
        }

        private void EnqueueTrades(BybitWsFrame frame, DateTime arrivedUtc)
        {
            var provenance = MarketEventNormalizer.CreateProvenance(_symbol, "bybit", "trade");
            foreach (var trade in frame.Trades)
            {
                var evt = MarketEventNormalizer.CreateTrade(
                    _symbol,
                    BybitApi.FromUnixMs(trade.TimeMs),
                    trade.Price,
                    trade.Size,
                    trade.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? TradeSide.Buy
                        : trade.Side.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? TradeSide.Sell
                        : TradeSide.Unknown,
                    eventId: trade.EventId,
                    sequenceNumber: trade.TimeMs,
                    provenance: provenance);
                evt.ArrivalTimestamp = arrivedUtc;
                Add(MarketEventType.Trade, evt);
            }
        }

        private void EnqueueBars(BybitWsFrame frame, DateTime arrivedUtc)
        {
            var period = TimeSpan.FromMilliseconds(_klineIntervalMs);
            var provenance = MarketEventNormalizer.CreateProvenance(_symbol, "bybit", "bar");
            foreach (var kline in frame.Klines)
            {
                var evt = MarketEventNormalizer.CreateBar(
                    _symbol,
                    BybitApi.FromUnixMs(kline.StartMs),
                    period,
                    kline.Open,
                    kline.High,
                    kline.Low,
                    kline.Close,
                    kline.Volume,
                    sequenceNumber: kline.StartMs,
                    provenance: provenance);
                evt.ArrivalTimestamp = arrivedUtc;
                Add(MarketEventType.Bar, evt);
            }
        }

        private void EnqueueBook(MarketEventType type, BybitWsFrame frame, DateTime arrivedUtc)
        {
            var snapshotRequested = _queues.ContainsKey(MarketEventType.OrderBookSnapshot);
            var updatesRequested = _queues.ContainsKey(MarketEventType.OrderBookUpdate);

            foreach (var evt in _book.EventsForFrame(
                _symbol,
                frame,
                quoteMode: type == MarketEventType.Quote,
                emitSnapshot: snapshotRequested,
                emitUpdates: updatesRequested,
                arrival: arrivedUtc))
            {
                Add(evt.EventType, evt);
            }
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

            _archive?.Complete();

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

            // The adapter hands capture ownership to the session; release the archive file once the
            // last consumer stops (Stop is idempotent and only reached at teardown via ref-counting).
            _archive?.Dispose();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}