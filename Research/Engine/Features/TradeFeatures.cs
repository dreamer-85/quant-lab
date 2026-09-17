using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Trade flow feature. Cumulative signed volume in the observation period.
    /// Positive flow implies buying pressure, negative implies selling pressure.
    /// </summary>
    public class TradeFlowFeature : FeatureBase
    {
        public override string Name => "trade_flow";
        public override string Description => "Cumulative volume in observation period (positive = buying)";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return observation.Volume;
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
    /// current observation period. Positive = net buying pressure.
    /// </summary>
    public class NetFlowFeature : FeatureBase
    {
        public override string Name => "net_flow";
        public override string Description => "Taker buy notional minus taker sell notional";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            decimal net = 0m;
            if (observation.Events == null)
                return net;

            foreach (var evt in observation.Events)
            {
                if (evt is not TradeEvent trade)
                    continue;

                switch (trade.Side)
                {
                    case TradeSide.Buy:
                        net += trade.Price * trade.Quantity;
                        break;
                    case TradeSide.Sell:
                        net -= trade.Price * trade.Quantity;
                        break;
                }
            }

            return net;
        }
    }
}