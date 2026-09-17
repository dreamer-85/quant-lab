using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// Mode-selecting exchange data source for <see cref="ResearchJob"/> "historical"/"live" jobs.
    /// Implements the same <see cref="IEventDataSource"/>/<see cref="IStreamingEventSource"/>
    /// contract as the file and synthetic sources so the replay pipeline is source-agnostic:
    ///
    ///   - "historical": pulls REST backfill (bars, plus near-real-time trades/quotes/order book)
    ///     and replays the normalized stream directly.
    ///   - "live":       subscribes a WebSocket feed and streams normalized events as they arrive.
    ///
    /// Both modes funnel through <see cref="MarketEventNormalizer"/> so live-observed and
    /// backfilled observations share one schema.
    /// </summary>
    public sealed class ExchangeDataAdapter : IEventDataSource, IStreamingEventSource
    {
        private static readonly MarketEventType[] StreamOrder =
        {
            MarketEventType.Trade,
            MarketEventType.Quote,
            MarketEventType.OrderBookUpdate,
            MarketEventType.OrderBookSnapshot,
            MarketEventType.Bar
        };

        private readonly ResearchJob _job;
        private readonly JobDataSource _source;
        private readonly IHttpTransport _http;
        private readonly Func<IWsTransport> _wsFactory;

        public ExchangeDataAdapter(
            ResearchJob job,
            JobDataSource source = null,
            IHttpTransport http = null,
            Func<IWsTransport> wsFactory = null)
        {
            _job = job ?? throw new ArgumentNullException(nameof(job));
            _source = source ?? job.Source ?? new JobDataSource();
            _http = http ?? new HttpClientTransport();
            _wsFactory = wsFactory ?? (() => new ClientWebSocketTransport());

            if (!_source.IsExchangeSource())
            {
                throw new InvalidOperationException(
                    $"ExchangeDataAdapter requires a \"historical\" or \"live\" source (got \"{_source.Mode}\")");
            }

            if (_source.Mode.Equals("archive", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"\"archive\" sources are replayed by {nameof(Bybit.BybitArchiveSource)}, not the ExchangeDataAdapter");
            }

            if (!_source.Provider.Equals("bybit", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Provider \"{_source.Provider}\" is not supported; the adapter supports \"bybit\"");
            }

            if (_source.Mode.Equals("live", StringComparison.OrdinalIgnoreCase) && _job.Reorder == ReorderMode.FullSort)
            {
                throw new InvalidOperationException(
                    "Live streams are never-ending and cannot be fully sorted; set reorder to \"InOrderStreaming\".");
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
        /// Yields one ordered stream per requested event type. Historical streams re-fetch lazily per
        /// type; live streams are fed from per-type WebSocket queues.
        /// </summary>
        public IEnumerable<IEnumerable<MarketEvent>> GetEventStreams(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var types = job.EventTypes
                .Where(t => t == MarketEventType.Trade
                            || t == MarketEventType.Quote
                            || t == MarketEventType.OrderBookUpdate
                            || t == MarketEventType.OrderBookSnapshot
                            || t == MarketEventType.Bar)
                .OrderBy(t => Array.IndexOf(StreamOrder, t))
                .ToList();

            if (types.Count == 0)
            {
                yield break;
            }

            if (_source.Mode.Equals("historical", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var type in types)
                {
                    yield return HistoricalStream(type, job, symbol, _source, _http);
                }
                yield break;
            }

// Live: one shared connection, per-type queues. The capture is optionally recorded to
// an archive so it can be replayed deterministically later (see BybitArchiveSource).
var until = Bybit.BybitLiveSource.ResolveDeadline(job, _source);
var session = new Bybit.BybitLiveSession(
    Bybit.BybitApi.WsBase(_source),
    symbol,
    types,
    Bybit.BybitApi.ClampDepth(_source.OrderBookDepth),
    (int)Bybit.BybitApi.PeriodFor(job.Resolution).TotalMilliseconds,
    _wsFactory,
    CreateArchive(_source));
session.Start(Bybit.BybitLiveSource.BuildTopics(job, _source, symbol.Value));

foreach (var type in types)
{
    yield return session.Stream(type, until, CancellationToken.None);
}
}

/// <summary>
/// Creates the capture archive writer when the source requests one; null otherwise.
/// </summary>
private static Bybit.IWsFrameArchive CreateArchive(JobDataSource source)
{
    return string.IsNullOrWhiteSpace(source?.ArchiveFilePath)
        ? null
        : new Bybit.WsFrameArchiveWriter(source.ArchiveFilePath);
}

        private static IEnumerable<MarketEvent> HistoricalStream(
            MarketEventType type,
            ResearchJob job,
            QuantConnect.Symbol symbol,
            JobDataSource source,
            IHttpTransport http)
        {
            switch (type)
            {
                case MarketEventType.Bar:
                    return Bybit.BybitHistoricalSource.Klines(job, source, symbol, http);
                case MarketEventType.Trade:
                    return Bybit.BybitHistoricalSource.RecentTrades(job, source, symbol, http);
                case MarketEventType.Quote:
                    return Bybit.BybitHistoricalSource.OrderBook(job, source, symbol, http);
                case MarketEventType.OrderBookSnapshot:
                    return Bybit.BybitHistoricalSource.OrderBook(job, source, symbol, http);
                case MarketEventType.OrderBookUpdate:
                    // Bybit v5 REST exposes only point-in-time order book snapshots, never historical
                    // deltas. Order book update streams exist only on the live WebSocket feed (and its
                    // archive), so a historical job requesting updates yields no events.
                    return Enumerable.Empty<MarketEvent>();
                default:
                    return Enumerable.Empty<MarketEvent>();
            }
        }
    }
}