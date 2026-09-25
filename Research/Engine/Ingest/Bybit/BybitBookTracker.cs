using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Maintains a cumulative order book (per price level, absolute sizes) and converts order book
    /// WebSocket frames into the engine's order book events. Both the <see cref="BybitLiveSession"/>
    /// and the archive replay source (<see cref="BybitArchiveSource"/>) share this translator so a
    /// replay of a recorded archive is event-for-event identical to the live capture.
    ///
    /// Frame semantics:
    ///   - "snapshot" frames reset the book. When a snapshot consumer is requested the full cumulative
    ///     book is emitted as an <see cref="OrderBookSnapshotEvent"/>; when the job consumes only
    ///     per-level updates the snapshot levels are emitted as add updates so the reconstructed
    ///     state is complete without a snapshot event.
    ///   - "delta" frames mutate individual levels and, when requested, emit an
    ///     <see cref="OrderBookUpdateEvent"/> per changed level with the action classified against the
    ///     previous quantity (Add / Modify / Remove).
    /// </summary>
    internal sealed class BybitBookTracker
    {
        private readonly SortedDictionary<decimal, decimal> _bids =
            new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));
        private readonly SortedDictionary<decimal, decimal> _asks = new();
        private readonly object _lock = new();

        /// <summary>
        /// Applies the frame to the cumulative book and produces the normalized events for it.
        /// The book is mutated atomically under the lookup lock; the returned events are built
        /// from the stable, post-apply book.
        /// </summary>
        public List<MarketEvent> EventsForFrame(
            Symbol symbol,
            BybitWsFrame frame,
            bool quoteMode,
            bool emitSnapshot,
            bool emitUpdates,
            DateTime arrival)
        {
            var events = new List<MarketEvent>();
            var timestamp = BybitApi.FromUnixMs(frame.TsMs);
            var provenance = MarketEventNormalizer.CreateProvenance(symbol, "bybit", "orderbook");

            lock (_lock)
            {
                if (quoteMode)
                {
                    // Bybit depth-1 streams re-send the current top-of-book as full "snapshot"
                    // frames on every change. A snapshot replaces the entire book, so stale
                    // top-of-book levels must be cleared before applying it; otherwise obsolete
                    // best bids/asks accumulate and the reconstructed L1 freezes on them (a
                    // falling market then yields crossed quotes with a negative spread).
                    if (frame.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                    {
                        _bids.Clear();
                        _asks.Clear();
                    }
                    ApplyBookFrame(frame);
                    if (_bids.Count > 0 && _asks.Count > 0)
                    {
                        var quote = MarketEventNormalizer.CreateQuote(
                            symbol,
                            timestamp,
                            _bids.First().Key,
                            _bids.First().Value,
                            _asks.First().Key,
                            _asks.First().Value,
                            sequenceNumber: frame.TsMs,
                            provenance: MarketEventNormalizer.CreateProvenance(symbol, "bybit", "quote"));
                        quote.ArrivalTimestamp = arrival;
                        events.Add(quote);
                    }
                    return events;
                }

                if (frame.Type.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    _bids.Clear();
                    _asks.Clear();
                    ApplyBookFrame(frame);

                    if (emitSnapshot)
                    {
                        var snapshot = MarketEventNormalizer.CreateOrderBookSnapshot(
                            symbol,
                            timestamp,
                            _bids.Select(kvp => new OrderBookLevel { Price = kvp.Key, Quantity = kvp.Value }),
                            _asks.Select(kvp => new OrderBookLevel { Price = kvp.Key, Quantity = kvp.Value }),
                            sequenceNumber: frame.TsMs,
                            provenance: provenance);
                        snapshot.ArrivalTimestamp = arrival;
                        events.Add(snapshot);
                    }
                    else if (emitUpdates)
                    {
                        foreach (var level in frame.Bids)
                        {
                            if (level.Size > 0)
                            {
                                RunAddUpdate(events, symbol, level, OrderBookSide.Bid, frame.TsMs, arrival);
                            }
                        }
                        foreach (var level in frame.Asks)
                        {
                            if (level.Size > 0)
                            {
                                RunAddUpdate(events, symbol, level, OrderBookSide.Ask, frame.TsMs, arrival);
                            }
                        }
                    }
                }
                else
                {
                    foreach (var level in frame.Bids)
                    {
                        var previousQuantity = _bids.TryGetValue(level.Price, out var existing) ? existing : 0m;
                        Set(_bids, level.Price, level.Size);
                        if (emitUpdates)
                        {
                            RunAddUpdate(events, symbol, level, OrderBookSide.Bid, frame.TsMs, arrival, previousQuantity);
                        }
                    }
                    foreach (var level in frame.Asks)
                    {
                        var previousQuantity = _asks.TryGetValue(level.Price, out var existing) ? existing : 0m;
                        Set(_asks, level.Price, level.Size);
                        if (emitUpdates)
                        {
                            RunAddUpdate(events, symbol, level, OrderBookSide.Ask, frame.TsMs, arrival, previousQuantity);
                        }
                    }

                    if (!emitUpdates && emitSnapshot)
                    {
                        var snapshot = MarketEventNormalizer.CreateOrderBookSnapshot(
                            symbol,
                            timestamp,
                            _bids.Select(kvp => new OrderBookLevel { Price = kvp.Key, Quantity = kvp.Value }),
                            _asks.Select(kvp => new OrderBookLevel { Price = kvp.Key, Quantity = kvp.Value }),
                            sequenceNumber: frame.TsMs,
                            provenance: provenance);
                        snapshot.ArrivalTimestamp = arrival;
                        events.Add(snapshot);
                    }
                }
            }

            return events;
        }

        private static void RunAddUpdate(
            List<MarketEvent> events,
            Symbol symbol,
            BybitLevel level,
            OrderBookSide side,
            long tsMs,
            DateTime arrival,
            decimal previousQuantity = 0m)
        {
            var update = MarketEventNormalizer.CreateOrderBookUpdate(
                symbol,
                BybitApi.FromUnixMs(tsMs),
                side,
                level.Price,
                level.Size,
                action: level.Size <= 0
                    ? OrderBookUpdateAction.Remove
                    : previousQuantity <= 0
                        ? OrderBookUpdateAction.Add
                        : OrderBookUpdateAction.Modify,
                orderCount: level.OrderCount,
                sequenceNumber: tsMs,
                provenance: MarketEventNormalizer.CreateProvenance(symbol, "bybit", "orderbook"));
            update.ArrivalTimestamp = arrival;
            events.Add(update);
        }

        /// <summary>
        /// Applies a whole frame (snapshot or delta) to the book dictionaries.
        /// </summary>
        private void ApplyBookFrame(BybitWsFrame frame)
        {
            foreach (var level in frame.Bids)
            {
                Set(_bids, level.Price, level.Size);
            }
            foreach (var level in frame.Asks)
            {
                Set(_asks, level.Price, level.Size);
            }
        }

        private static void Set(SortedDictionary<decimal, decimal> book, decimal price, decimal quantity)
        {
            if (price <= 0)
            {
                return;
            }
            if (quantity <= 0)
            {
                book.Remove(price);
            }
            else
            {
                book[price] = quantity;
            }
        }
    }
}