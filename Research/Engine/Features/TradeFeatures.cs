using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Single source of truth for signed trade flow, shared by the derived <c>trade_flow</c> feature,
    /// the derived <c>net_flow</c> feature and the raw <c>trade_flow</c> field so all three agree by
    /// construction. See <see cref="SignedNotionalFlow"/>.
    /// </summary>
    internal static class SignedNotionalFlow
    {
        /// <summary>
        /// Signed notional flow over a period's trades: +price*quantity for taker buys, -price*quantity
        /// for taker sells, 0 for trades with an unknown side. Positive implies net buying pressure.
        /// </summary>
        public static decimal Compute(IEnumerable<MarketEvent> events)
        {
            decimal flow = 0m;
            if (events == null)
            {
                return flow;
            }

            foreach (var evt in events)
            {
                if (evt is not TradeEvent trade)
                {
                    continue;
                }

                flow += trade.Side switch
                {
                    TradeSide.Buy => trade.Price * trade.Quantity,
                    TradeSide.Sell => -(trade.Price * trade.Quantity),
                    _ => 0m
                };
            }

            return flow;
        }
    }

    /// <summary>
    /// Trade flow feature. Signed notional flow in the observation period.
    /// Positive flow implies buying pressure, negative implies selling pressure.
    /// Numerically identical to the raw <c>trade_flow</c> field and to <see cref="NetFlowFeature"/>;
    /// trades with an unknown side contribute nothing, so a period with no side information yields 0.
    /// </summary>
    public class TradeFlowFeature : FeatureBase
    {
        public override string Name => "trade_flow";
        public override string Description => "Signed notional flow in observation period (+buy, -sell)";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return SignedNotionalFlow.Compute(observation?.Events);
        }
    }

    /// <summary>
    /// Cumulative flow feature. Running sum of signed volume over history.
    /// Requires trade direction information (trade aggressor side).
    /// </summary>
    public class CumulativeFlowFeature : FeatureBase
    {
        private decimal _cumulativeFlow;

        public override string Name => "cumulative_flow";
        public override string Description => "Running sum of signed volume (needs aggressor side)";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            var signedVolume = 0m;

            foreach (var evt in observation.Events)
            {
                if (evt is TradeEvent trade)
                {
                    switch (trade.Side)
                    {
                        case TradeSide.Buy:
                            signedVolume += trade.Quantity;
                            break;
                        case TradeSide.Sell:
                            signedVolume -= trade.Quantity;
                            break;
                    }
                }
            }

            _cumulativeFlow += signedVolume;
            return _cumulativeFlow;
        }

        public override void Reset()
        {
            _cumulativeFlow = 0m;
        }
    }

    /// <summary>
    /// Trade intensity feature. Number of trades per observation period.
    /// </summary>
    public class TradeIntensityFeature : FeatureBase
    {
        public override string Name => "trade_intensity";
        public override string Description => "Number of trades in observation period";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return observation.TradeCount;
        }
    }

    /// <summary>
    /// Trade volume feature. Total volume traded in observation period.
    /// </summary>
    public class TradeVolumeFeature : FeatureBase
    {
        public override string Name => "trade_volume";
        public override string Description => "Total volume traded in observation period";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return observation.Volume;
        }
    }

    /// <summary>
    /// Aggressive buy volume feature. Notional (price * quantity) of trades where the taker
    /// crossed the spread to buy, in the current observation period.
    /// </summary>
    public class AggressiveBuyVolumeFeature : FeatureBase
    {
        public override string Name => "aggressive_buy_volume";
        public override string Description => "Taker buy notional in observation period";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            decimal notional = 0m;
            if (observation.Events == null)
                return notional;

            foreach (var evt in observation.Events)
            {
                if (evt is TradeEvent trade && trade.Side == TradeSide.Buy)
                    notional += trade.Price * trade.Quantity;
            }

            return notional;
        }
    }

    /// <summary>
    /// Aggressive sell volume feature. Notional (price * quantity) of trades where the taker
    /// crossed the spread to sell, in the current observation period.
    /// </summary>
    public class AggressiveSellVolumeFeature : FeatureBase
    {
        public override string Name => "aggressive_sell_volume";
        public override string Description => "Taker sell notional in observation period";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            decimal notional = 0m;
            if (observation.Events == null)
                return notional;

            foreach (var evt in observation.Events)
            {
                if (evt is TradeEvent trade && trade.Side == TradeSide.Sell)
                    notional += trade.Price * trade.Quantity;
            }

            return notional;
        }
    }

    /// <summary>
    /// Net aggressive flow feature. Aggressive buy notional minus aggressive sell notional in the
    /// current observation period. Positive = net buying pressure. An alias of <see cref="TradeFlowFeature"/>:
    /// both name the same signed notional flow.
    /// </summary>
    public class NetFlowFeature : FeatureBase
    {
        public override string Name => "net_flow";
        public override string Description => "Taker buy notional minus taker sell notional";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return SignedNotionalFlow.Compute(observation?.Events);
        }
    }
}