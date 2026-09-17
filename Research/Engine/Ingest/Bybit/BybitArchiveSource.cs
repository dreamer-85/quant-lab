using System.Collections.Concurrent;
using System.Text.Json;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Replays a recorded WebSocket capture (<see cref="WsFrameArchiveWriter"/> JSON-lines archive)
    /// through the exact live-normalization path. Each archived raw frame is re-normalized using the
    /// same <see cref="BybitBookTracker"/> and arrival timestamps as the live session, so the event
    /// stream produced here is event-for-event identical to the live capture.
    ///
    /// Backed by <see cref="JobDataSource"/> "archive" mode. The job must repeat the live capture's
    /// config (symbol, depth, event types); the replay engine should use
    /// <see cref="ReorderMode.InOrderStreaming"/> to reproduce the live merge order, or
    /// <see cref="ReorderMode.FullSort"/> for an equivalent deterministic ordering.
    /// </summary>
    public sealed class BybitArchiveSource : IEventDataSource, IStreamingEventSource
    {
        private readonly ResearchJob _job;
        private readonly JobDataSource _source;

        public BybitArchiveSource(ResearchJob job, JobDataSource source = null)
        {
            _job = job ?? throw new ArgumentNullException(nameof(job));
            _source = source ?? job.Source ?? new JobDataSource();

            if (!_source.Mode.Equals("archive", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"BybitArchiveSource requires an \"archive\" source (got \"{_source.Mode}\")");
            }
        }

        /// <summary>
        /// Merges the ordered per-type sub-streams into one globally ordered stream.
        /// </summary>
        public IEnumerable<MarketEvent> GetEvents(ResearchJob job, QuantConnect.Symbol symbol)
        {
            return EventStreamMerger.Merge(GetEventStreams(job, symbol));
        }

        /// <summary>
        /// Yields one ordered sub-stream per requested event type, each fed from the archive in
        /// recorded (arrival) order.
        /// </summary>
        public IEnumerable<IEnumerable<MarketEvent>> GetEventStreams(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var types = job.EventTypes
                .Where(t => t == MarketEventType.Trade
                            || t == MarketEventType.Quote
                            || t == MarketEventType.OrderBookUpdate
                            || t == MarketEventType.OrderBookSnapshot
                            || t == MarketEventType.Bar)
                .OrderBy(t => Array.IndexOf(_streamOrder, t))
                .ToList();

            if (types.Count == 0)
            {
                yield break;
            }

            var session = new BybitArchiveSession(
                _source.ArchiveFilePath,
                symbol,
                types,
                BybitApi.ClampDepth(_source.OrderBookDepth),
                (int)BybitApi.PeriodFor(job.Resolution).TotalMilliseconds);
            session.Start();

            foreach (var type in types)
            {
                yield return session.Stream(type);
            }
        }

        private static readonly MarketEventType[] _streamOrder =
        {
            MarketEventType.Trade,
            MarketEventType.Quote,
            MarketEventType.OrderBookUpdate,
            MarketEventType.OrderBookSnapshot,
            MarketEventType.Bar
        };
    }

    /// <summary>
    /// Owns the archive file read and per-type event queues. Structure mirrors
    /// <see cref="BybitLiveSession"/>: one Task reads lines, dispatches them through the shared
    /// frame-to-event translators, and completes the queues at end of file.
    /// </summary>
    internal sealed class BybitArchiveSession : IDisposable
    {
        private const int QueueCapacity = 65536;

        private readonly string _path;
        private readonly Symbol _symbol;
        private readonly Dictionary<MarketEventType, BlockingCollection<MarketEvent>> _queues;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _depth;
        private readonly int _klineIntervalMs;
        private readonly BybitBookTracker _book = new();

        private int _refs;
        private volatile Exception _fault;
        private Task _reader;

        public BybitArchiveSession(
            string path,
            Symbol symbol,
            IEnumerable<MarketEventType> types,
            int depth,
            int klineIntervalMs)
        {
            _path = path;
            _symbol = symbol;
            _depth = depth;
            _klineIntervalMs = klineIntervalMs;
            _queues = new Dictionary<MarketEventType, BlockingCollection<MarketEvent>>();
            foreach (var type in types)
            {
                _queues[type] = new BlockingCollection<MarketEvent>(QueueCapacity);
            }
        }

        /// <summary>
        /// Starts reading the archive in the background.
        /// </summary>
        public void Start()
        {
            _reader = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
        }

        /// <summary>
        /// Yields events for one event type until the archive is exhausted or cancellation.
        /// </summary>
        public IEnumerable<MarketEvent> Stream(MarketEventType type, CancellationToken external = default)
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
                        throw new InvalidOperationException($"ARCHIVE feed faulted: {_fault.Message}", _fault);
                    }
                    if (external.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(external);
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

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                using var reader = new StreamReader(_path);
                while (!ct.IsCancellationRequested)
                {
                    var line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("a", out var arrivalElement)
                        || !root.TryGetProperty("f", out var frameElement))
                    {
                        continue;
                    }

                    var arrival = BybitApi.FromUnixMs(arrivalElement.GetInt64());
                    var frameJson = frameElement.GetString();
                    if (string.IsNullOrWhiteSpace(frameJson))
                    {
                        continue;
                    }

                    Dispatch(frameJson, arrival);
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

        private void Dispatch(string frameJson, DateTime arrival)
        {
            if (!BybitJson.TryParseWsFrame(frameJson, out var frame))
            {
                return;
            }

            // Skip frames for symbols other than the one this session replays.
            if (!frame.Topic.EndsWith(_symbol.Value, StringComparison.Ordinal))
            {
                return;
            }

            var type = TypeForTopic(frame.Topic);
            if (type == null)
            {
                return;
            }

            switch (type.Value)
            {
                case MarketEventType.Trade:
                    EnqueueTrades(frame, arrival);
                    break;
                case MarketEventType.Bar:
                    EnqueueBars(frame, arrival);
                    break;
                case MarketEventType.Quote:
                case MarketEventType.OrderBookSnapshot:
                    EnqueueBook(frame, arrival);
                    break;
            }
        }

        private void EnqueueTrades(BybitWsFrame frame, DateTime arrival)
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
                evt.ArrivalTimestamp = arrival;
                Add(MarketEventType.Trade, evt);
            }
        }

        private void EnqueueBars(BybitWsFrame frame, DateTime arrival)
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
                evt.ArrivalTimestamp = arrival;
                Add(MarketEventType.Bar, evt);
            }
        }

        private void EnqueueBook(BybitWsFrame frame, DateTime arrival)
        {
            var snapshotRequested = _queues.ContainsKey(MarketEventType.OrderBookSnapshot);
            var updatesRequested = _queues.ContainsKey(MarketEventType.OrderBookUpdate);

            foreach (var evt in _book.EventsForFrame(
                _symbol,
                frame,
                quoteMode: _depth == 1,
                emitSnapshot: snapshotRequested,
                emitUpdates: updatesRequested,
                arrival: arrival))
            {
                Add(evt.EventType, evt);
            }
        }

        private MarketEventType? TypeForTopic(string topic)
        {
            if (topic.StartsWith("publicTrade", StringComparison.Ordinal))
            {
                return MarketEventType.Trade;
            }
            if (topic.StartsWith("kline", StringComparison.Ordinal))
            {
                return MarketEventType.Bar;
            }
            if (topic.StartsWith("orderbook", StringComparison.Ordinal))
            {
                return _depth == 1 ? MarketEventType.Quote : MarketEventType.OrderBookSnapshot;
            }
            return null;
        }

        private void Add(MarketEventType type, MarketEvent evt)
        {
            if (_queues.TryGetValue(type, out var queue))
            {
                if (!queue.TryAdd(evt, 1, _cts.Token))
                {
                    _fault = new InvalidOperationException("archive queue overflow; consumer too slow");
                }
            }
        }

        public void Stop()
        {
            try
            {
                _cts.Cancel();
                _reader?.Wait(TimeSpan.FromSeconds(10));
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