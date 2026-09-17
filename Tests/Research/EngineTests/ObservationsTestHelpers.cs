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
    }
}