using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Mid price feature. Returns the current mid price of the market state.
    /// </summary>
    public class MidPriceFeature : FeatureBase
    {
        public override string Name => "mid_price";
        public override string Description => "Mid price of the market";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return observation.State?.MidPrice ?? 0m;
        }
    }

    /// <summary>
    /// Spread feature. Returns the bid-ask spread in price terms.
    /// </summary>
    public class SpreadFeature : FeatureBase
    {
        public override string Name => "spread";
        public override string Description => "Bid-ask spread in price terms";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return observation.State?.Spread ?? 0m;
        }
    }

    /// <summary>
    /// Spread basis points feature. Returns the bid-ask spread relative to mid.
    /// </summary>
    public class SpreadBpsFeature : FeatureBase
    {
        public override string Name => "spread_bps";
        public override string Description => "Bid-ask spread in basis points relative to mid";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            return observation.State?.SpreadBps ?? 0m;
        }
    }
}