using System;
using System.Collections.Generic;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    internal static class ObservationsTestHelpers
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        /// <summary>
        /// Observation with buy trades at 100.00x11 and 3.00x1 (aggressive buy notional 1103)
        /// and sell trades at 200.00x1 and 9.00x1 (aggressive sell notional 209).
        /// </summary>
        public static Observation CreateObservationWithTrades()
        {
            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new MarketState(_symbol);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                _symbol, ts,
                new List<OrderBookLevel> { new() { Price = 100.00m, Quantity = 100m } },
                new List<OrderBookLevel> { new() { Price = 101.00m, Quantity = 100m } }));

            return new Observation
            {
                Timestamp = ts,
                State = state,
                Events = new List<MarketEvent>
                {
                    MarketEventNormalizer.CreateTrade(_symbol, ts, 100.00m, 11m, TradeSide.Buy),
                    MarketEventNormalizer.CreateTrade(_symbol, ts, 3.00m, 1m, TradeSide.Buy),
                    MarketEventNormalizer.CreateTrade(_symbol, ts, 200.00m, 1m, TradeSide.Sell),
                    MarketEventNormalizer.CreateTrade(_symbol, ts, 9.00m, 1m, TradeSide.Sell)
                }
            };
        }

        /// <summary>
        /// Observation with two one-minute bars only (no trades/quotes).
        /// </summary>
        public static Observation CreateObservationWithBars()
        {
            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new MarketState(_symbol);
            var bars = new List<BarEvent>
            {
                new() { Timestamp = ts, Symbol = _symbol, Open = 99m, High = 105m, Low = 98m, Close = 103m, Volume = 10m, Period = TimeSpan.FromMinutes(1) },
                new() { Timestamp = ts.AddMinutes(1), Symbol = _symbol, Open = 103m, High = 108m, Low = 101m, Close = 107m, Volume = 12m, Period = TimeSpan.FromMinutes(1) }
            };

            foreach (var bar in bars)
            {
                state.UpdateFromEvent(bar);
            }

            return new Observation
            {
                Timestamp = bars[^1].Timestamp,
                State = state,
                Events = new List<MarketEvent>(bars)
            };
        }

        /// <summary>
        /// Observation carrying two order book level updates, on top of a book snapshot so the
        /// reconstructed state has levels to expose.
        /// </summary>
        public static Observation CreateObservationWithOrderBookUpdates()
        {
            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new MarketState(_symbol);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                _symbol, ts,
                new List<OrderBookLevel> { new() { Price = 99m, Quantity = 3m } },
                new List<OrderBookLevel> { new() { Price = 101m, Quantity = 4m } }));

            var updates = new List<MarketEvent>
            {
                MarketEventNormalizer.CreateOrderBookUpdate(
                    _symbol, ts, OrderBookSide.Bid, 99m, 3m, OrderBookUpdateAction.Add),
                MarketEventNormalizer.CreateOrderBookUpdate(
                    _symbol, ts, OrderBookSide.Ask, 101m, 4m, OrderBookUpdateAction.Add)
            };

            foreach (var update in updates)
            {
                state.UpdateFromEvent(update);
            }

            return new Observation
            {
                Timestamp = ts,
                State = state,
                Events = updates
            };
        }

        /// <summary>
        /// Observation whose period contains an order book snapshot, the event type the Bybit
        /// sources emit for L2. Used to prove the snapshot itself reaches the script, not only the
        /// levels the engine reconstructs from it.
        /// </summary>
        public static Observation CreateObservationWithSnapshot()
        {
            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new MarketState(_symbol);
            var snapshot = MarketEventNormalizer.CreateOrderBookSnapshot(
                _symbol, ts,
                new List<OrderBookLevel> { new() { Price = 99m, Quantity = 3m } },
                new List<OrderBookLevel> { new() { Price = 101m, Quantity = 4m } });
            state.UpdateFromEvent(snapshot);

            return new Observation
            {
                Timestamp = ts,
                State = state,
                Events = new List<MarketEvent> { snapshot }
            };
        }

        /// <summary>
        /// Observation whose period contains a liquidation (modelled as a custom market event), the
        /// shape a script previously had no way to detect.
        /// </summary>
        public static Observation CreateObservationWithCustomEvent()
        {
            var ts = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var state = new MarketState(_symbol);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                _symbol, ts,
                new List<OrderBookLevel> { new() { Price = 100m, Quantity = 50m } },
                new List<OrderBookLevel> { new() { Price = 101m, Quantity = 50m } }));

            var liquidation = new CustomMarketEvent
            {
                Timestamp = ts,
                Symbol = _symbol,
                CustomEventType = "Liquidation",
                Value = 25000m,
                Data = new Dictionary<string, object> { ["side"] = "sell" }
            };

            return new Observation
            {
                Timestamp = ts,
                State = state,
                Events = new List<MarketEvent> { liquidation }
            };
        }

        /// <summary>
        /// Observation for a caller-supplied symbol, so multi-symbol behaviour can be tested.
        /// </summary>
        public static Observation CreateTradesObservationFor(string symbolValue, DateTime ts)
        {
            var symbol = Symbol.Create(symbolValue, SecurityType.Crypto, Market.Bybit);
            var state = new MarketState(symbol);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                symbol, ts,
                new List<OrderBookLevel> { new() { Price = 100m, Quantity = 10m } },
                new List<OrderBookLevel> { new() { Price = 101m, Quantity = 10m } }));

            return new Observation
            {
                Timestamp = ts,
                State = state,
                Events = new List<MarketEvent>
                {
                    MarketEventNormalizer.CreateTrade(symbol, ts, 100.5m, 2m, TradeSide.Buy)
                }
            };
        }
    }
}