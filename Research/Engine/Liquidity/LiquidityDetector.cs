using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Observations;
using MState = QuantConnect.Research.Engine.MarketState.MarketState;

namespace QuantConnect.Research.Engine.Liquidity
{
    /// <summary>
    /// Thresholds controlling transition detection. All defaults are expressed in
    /// engine-standard units (bps for prices, base-currency for sizes, ms for time).
    /// </summary>
    public sealed class LiquidityDetectorConfig
    {
        /// <summary>Depth band around mid (bps). Depths are summed within +-this band.</summary>
        public decimal BpsBand { get; set; } = 10m;

        /// <summary>Minimum relative depth loss on a side to count as depletion.</summary>
        public decimal DepletionFraction { get; set; } = 0.25m;

        /// <summary>Absolute (base-currency) floor under a depletion, applied in addition to the fraction.</summary>
        public decimal DepletionMinSize { get; set; } = 0.01m;

        /// <summary>Minimum relative depth gain on a side to count as replenishment.</summary>
        public decimal ReplenishmentFraction { get; set; } = 0.25m;

        /// <summary>Minimum best-price relocation (bps) to count as a migration.</summary>
        public decimal MigrationBps { get; set; } = 5m;

        /// <summary>Best-level size threshold as a multiple of the median in-band level size.</summary>
        public decimal WallMultiple { get; set; } = 5m;

        /// <summary>Back-fill <see cref="LiquidityTransitionEvent.MsUntilReplenishment"/> only within this span.</summary>
        public decimal ReplenishmentLookbackMs { get; set; } = 30000m;

        /// <summary>Max milliseconds between consecutive observations considered "contiguous".</summary>
        public decimal MaxObservationGapMs { get; set; } = 2000m;
    }

    /// <summary>
    /// Stateful, per-symbol detector of liquidity transitions. Consumes each observation
    /// (previous-vs-current state plus the events in the observation window) and emits
    /// point-in-time <see cref="LiquidityTransitionEvent"/> records. Volume/flow fields are
    /// window aggregates shared by every event emitted for the same observation.
    /// </summary>
    public sealed class LiquidityDetector
    {
        private readonly LiquidityDetectorConfig _config;
        private readonly Dictionary<string, SymbolState> _states = new(StringComparer.Ordinal);

        public LiquidityDetector(LiquidityDetectorConfig config = null)
        {
            _config = config ?? new LiquidityDetectorConfig();
        }

        /// <summary>
        /// Processes one observation, returning zero or more transition events.
        /// </summary>
        public List<LiquidityTransitionEvent> Observe(Observation observation)
        {
            var results = new List<LiquidityTransitionEvent>();
            if (observation?.State?.Symbol == null || string.IsNullOrEmpty(observation.State.Symbol.Value))
                return results;

            var symbol = observation.State.Symbol.Value;
            if (!_states.TryGetValue(symbol, out var state))
            {
                state = new SymbolState { Symbol = symbol };
                _states[symbol] = state;
            }
            else
            {
                state.Symbol = symbol;
            }

            var prev = state.Previous;
            state.Previous = observation;

            if (prev?.State == null)
                return results;

            var prevState = prev.State;
            var currState = observation.State;
            if (currState.BidPrice <= 0 || currState.AskPrice <= 0)
                return results;

            // Skip if there is a gap: a missing window invalidates the before/after comparison.
            var gapMs = (observation.Timestamp - prev.Timestamp).TotalMilliseconds;
            if (gapMs < 0 || gapMs > (double)_config.MaxObservationGapMs)
                return results;

            var band = _config.BpsBand;
            var bidBefore = prevState.GetBidDepthWithinBps(band);
            var bidAfter = currState.GetBidDepthWithinBps(band);
            var askBefore = prevState.GetAskDepthWithinBps(band);
            var askAfter = currState.GetAskDepthWithinBps(band);

            // --- Window aggregates (shared by every event of this observation) ---
            var flow = ComputeFlow(observation.Events, prevState);

            var updates = observation.Events.OfType<OrderBookUpdateEvent>().ToList();
            var bidsRemoved = updates.Count(u => u.Side == OrderBookSide.Bid && (u.Action == OrderBookUpdateAction.Remove || u.Quantity == 0));
            var bidsAdded = updates.Count(u => u.Side == OrderBookSide.Bid && u.Action == OrderBookUpdateAction.Add);
            var asksRemoved = updates.Count(u => u.Side == OrderBookSide.Ask && (u.Action == OrderBookUpdateAction.Remove || u.Quantity == 0));
            var asksAdded = updates.Count(u => u.Side == OrderBookSide.Ask && u.Action == OrderBookUpdateAction.Add);

            var now = observation.Timestamp;
            var msSinceLast = state.LastEventTimestamp.HasValue
                ? (long)(now - state.LastEventTimestamp.Value).TotalMilliseconds
                : -1L;

            // --- Side-scoped detectors (depletion / replenishment / migration per side) ---
            DetectSide(state, results, now, msSinceLast, OrderBookSide.Bid, prevState, currState,
                bidBefore, bidAfter, bidsRemoved, bidsAdded, flow, updates.Count);

            DetectSide(state, results, now, msSinceLast, OrderBookSide.Ask, prevState, currState,
                askBefore, askAfter, asksRemoved, asksAdded, flow, updates.Count);

            // --- Wall formation uses the current book's concentration (independent of side flow) ---
            DetectWallFormation(state, results, now, msSinceLast, prevState, currState, band, flow);

            if (results.Count > 0)
                state.LastEventTimestamp = now;

            return results;
        }

        private void DetectSide(
            SymbolState state,
            List<LiquidityTransitionEvent> results,
            DateTime now,
            long msSinceLast,
            OrderBookSide side,
            MState prevState,
            MState currState,
            decimal depthBefore,
            decimal depthAfter,
            int removed,
            int added,
            WindowFlow flow,
            int updateCount)
        {
            var isBid = side == OrderBookSide.Bid;
            var bestBefore = isBid ? prevState.BidPrice : prevState.AskPrice;
            var bestAfter = isBid ? currState.BidPrice : currState.AskPrice;
            var depthDelta = depthAfter - depthBefore;

            var threshold = Math.Max(
                _config.DepletionFraction * Math.Max(depthBefore, 0m),
                _config.DepletionMinSize);

            var emitted = false;

            // --- Depletion ---
            if (depthBefore > 0m && depthAfter < depthBefore && -depthDelta >= threshold)
            {
                var executedVolume = isBid ? flow.SellNotionalExecutedAgainstBid : flow.BuyNotionalExecutedAgainstAsk;

                var evt = new LiquidityTransitionEvent
                {
                    Timestamp = now,
                    Symbol = state.Symbol,
                    Type = LiquidityTransitionType.Depletion,
                    Side = side,
                    Price = bestAfter,
                    MidPrice = currState.MidPrice,
                    DepthBefore = depthBefore,
                    DepthAfter = depthAfter,
                    DepthDelta = depthDelta,
                    BpsBand = _config.BpsBand,
                    Executed = executedVolume > 0m,
                    ExecutedVolume = executedVolume,
                    AggressiveBuyVolume = flow.BuyVolume,
                    AggressiveSellVolume = flow.SellVolume,
                    NetFlow = flow.NetFlow,
                    UpdateCount = updateCount,
                    RemoveCount = removed,
                    AddCount = added,
                    TradeCount = flow.TradeCount,
                    SpreadBps = currState.SpreadBps,
                    MsSinceLastEvent = msSinceLast
                };

                results.Add(evt);

                state.LastDepletion[side] = (now, evt);
                emitted = true;
            }

            // --- Replenishment ---
            var replenishThreshold = Math.Max(
                _config.ReplenishmentFraction * Math.Max(depthBefore, 0m),
                0m);

            if (depthAfter > depthBefore && depthDelta >= replenishThreshold)
            {
                var evt = new LiquidityTransitionEvent
                {
                    Timestamp = now,
                    Symbol = state.Symbol,
                    Type = LiquidityTransitionType.Replenishment,
                    Side = side,
                    Price = bestAfter,
                    MidPrice = currState.MidPrice,
                    DepthBefore = depthBefore,
                    DepthAfter = depthAfter,
                    DepthDelta = depthDelta,
                    BpsBand = _config.BpsBand,
                    Executed = false,
                    ExecutedVolume = 0m,
                    AggressiveBuyVolume = flow.BuyVolume,
                    AggressiveSellVolume = flow.SellVolume,
                    NetFlow = flow.NetFlow,
                    UpdateCount = updateCount,
                    RemoveCount = removed,
                    AddCount = added,
                    TradeCount = flow.TradeCount,
                    SpreadBps = currState.SpreadBps,
                    MsSinceLastEvent = msSinceLast
                };

                results.Add(evt);

                // Back-fill the preceding depletion's duration (replenishment after depletion).
                if (state.LastDepletion.TryGetValue(side, out var lastDepletion))
                {
                    var elapsed = (long)(now - lastDepletion.Time).TotalMilliseconds;
                    if (elapsed >= 0 && elapsed <= _config.ReplenishmentLookbackMs)
                    {
                        lastDepletion.Event.MsUntilReplenishment = elapsed;
                        state.LastDepletion.Remove(side);
                    }
                }

                emitted = true;
            }

            // --- Migration (best price relocation), only if the side did not already emit. ---
            if (!emitted && bestBefore > 0m && bestAfter > 0m && bestAfter != bestBefore)
            {
                var moveBps = Math.Abs((bestAfter - bestBefore) / bestBefore) * 10000m;
                if (moveBps >= _config.MigrationBps)
                {
                    results.Add(new LiquidityTransitionEvent
                    {
                        Timestamp = now,
                        Symbol = state.Symbol,
                        Type = LiquidityTransitionType.Migration,
                        Side = side,
                        Price = bestAfter,
                        MidPrice = currState.MidPrice,
                        DepthBefore = depthBefore,
                        DepthAfter = depthAfter,
                        DepthDelta = depthDelta,
                        BpsBand = _config.BpsBand,
                        Executed = false,
                        ExecutedVolume = 0m,
                        AggressiveBuyVolume = flow.BuyVolume,
                        AggressiveSellVolume = flow.SellVolume,
                        NetFlow = flow.NetFlow,
                        UpdateCount = updateCount,
                        RemoveCount = removed,
                        AddCount = added,
                        TradeCount = flow.TradeCount,
                        SpreadBps = currState.SpreadBps,
                        MsSinceLastEvent = msSinceLast
                    });
                }
            }
        }

        private void DetectWallFormation(
            SymbolState state,
            List<LiquidityTransitionEvent> results,
            DateTime now,
            long msSinceLast,
            MState prevState,
            MState currState,
            decimal band,
            WindowFlow flow)
        {
            var bidsMed = MedianOfInBand(currState.BidLevels_List, currState.MidPrice, band, isBid: true);
            var asksMed = MedianOfInBand(currState.AskLevels_List, currState.MidPrice, band, isBid: false);

            DetectWallOnSide(state, results, now, msSinceLast, OrderBookSide.Bid, prevState, currState, bidsMed, flow);
            DetectWallOnSide(state, results, now, msSinceLast, OrderBookSide.Ask, prevState, currState, asksMed, flow);
        }

        private void DetectWallOnSide(
            SymbolState state,
            List<LiquidityTransitionEvent> results,
            DateTime now,
            long msSinceLast,
            OrderBookSide side,
            MState prevState,
            MState currState,
            decimal medianSize,
            WindowFlow flow)
        {
            if (medianSize <= 0m)
                return;

            var isBid = side == OrderBookSide.Bid;
            var bestAfter = isBid ? currState.BidPrice : currState.AskPrice;
            var bestBefore = isBid ? prevState.BidPrice : prevState.AskPrice;
            if (bestAfter <= 0m)
                return;

            var levels = isBid ? currState.BidLevels_List : currState.AskLevels_List;
            var bestLevel = levels.FirstOrDefault(l => l.Price == bestAfter);
            if (bestLevel == null || bestLevel.Quantity < _config.WallMultiple * medianSize)
                return;

            // Only count a newly formed wall when the previous best level did not already qualify.
            var prevLevel = bestBefore > 0m
                ? (isBid ? prevState.BidLevels_List : prevState.AskLevels_List).FirstOrDefault(l => l.Price == bestBefore)
                : null;
            if (prevLevel != null && prevLevel.Quantity >= _config.WallMultiple * medianSize)
                return;

            var depthBefore = isBid ? prevState.GetBidDepthWithinBps(_config.BpsBand) : prevState.GetAskDepthWithinBps(_config.BpsBand);
            var depthAfter = isBid ? currState.GetBidDepthWithinBps(_config.BpsBand) : currState.GetAskDepthWithinBps(_config.BpsBand);

            results.Add(new LiquidityTransitionEvent
            {
                Timestamp = now,
                Symbol = state.Symbol,
                Type = LiquidityTransitionType.WallFormation,
                Side = side,
                Price = bestAfter,
                MidPrice = currState.MidPrice,
                DepthBefore = depthBefore,
                DepthAfter = depthAfter,
                DepthDelta = depthAfter - depthBefore,
                BpsBand = _config.BpsBand,
                Executed = false,
                ExecutedVolume = 0m,
                AggressiveBuyVolume = flow.BuyVolume,
                AggressiveSellVolume = flow.SellVolume,
                NetFlow = flow.NetFlow,
                UpdateCount = 0,
                RemoveCount = 0,
                AddCount = 0,
                TradeCount = flow.TradeCount,
                SpreadBps = currState.SpreadBps,
                MsSinceLastEvent = msSinceLast
            });
        }

        private static decimal MedianOfInBand(IReadOnlyList<OrderBookLevel> levels, decimal mid, decimal band, bool isBid)
        {
            if (levels == null || levels.Count == 0 || mid <= 0m)
                return 0m;

            var row = new List<decimal>();
            foreach (var lvl in levels)
            {
                var dist = isBid
                    ? (mid - lvl.Price) / mid * 10000m
                    : (lvl.Price - mid) / mid * 10000m;
                if (dist >= 0m && dist <= band)
                    row.Add(lvl.Quantity);
            }
            if (row.Count == 0)
                return 0m;

            row.Sort();
            return row[(int)Math.Floor(row.Count / 2d)];
        }

        private sealed class WindowFlow
        {
            public decimal BuyVolume;
            public decimal SellVolume;
            public decimal NetFlow => BuyVolume - SellVolume;
            public decimal BuyNotionalExecutedAgainstAsk;
            public decimal SellNotionalExecutedAgainstBid;
            public int TradeCount;
        }

        private static WindowFlow ComputeFlow(List<MarketEvent> events, MState prevState)
        {
            var flow = new WindowFlow();
            if (events == null || events.Count == 0)
                return flow;

            foreach (var evt in events)
            {
                if (evt is not TradeEvent trade || trade.Quantity <= 0m)
                    continue;

                flow.TradeCount++;
                var notional = trade.Price * trade.Quantity;

                switch (trade.Side)
                {
                    case TradeSide.Buy:
                        flow.BuyVolume += notional;
                        // Buy market prints actively consume ask-side liquidity.
                        if (prevState.AskPrice > 0m && trade.Price >= prevState.AskPrice)
                            flow.BuyNotionalExecutedAgainstAsk += notional;
                        break;
                    case TradeSide.Sell:
                        flow.SellVolume += notional;
                        if (prevState.BidPrice > 0m && trade.Price <= prevState.BidPrice)
                            flow.SellNotionalExecutedAgainstBid += notional;
                        break;
                }
            }

            return flow;
        }

        private sealed class SymbolState
        {
            public string Symbol;
            public Observation Previous;
            public DateTime? LastEventTimestamp;

            public readonly Dictionary<OrderBookSide, (DateTime Time, LiquidityTransitionEvent Event)> LastDepletion = new()
            {
                [OrderBookSide.Bid] = default,
                [OrderBookSide.Ask] = default
            };
        }
    }
}