using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Liquidity depletion feature. Measures the change in depth over recent observations.
    /// Negative values indicate depletion, positive values indicate accumulation.
    /// </summary>
    public class LiquidityDepletionFeature : FeatureBase
    {
        private readonly int _lookbackPeriods;
        private readonly decimal _bpsBand;

        public LiquidityDepletionFeature(int lookbackPeriods = 5, decimal bpsBand = 10m)
        {
            _lookbackPeriods = lookbackPeriods;
            _bpsBand = bpsBand;
        }

        public override string Name => $"liquidity_depletion_{_lookbackPeriods}obs_{_bpsBand}bps";
        public override string Description => $"Change in depth over {_lookbackPeriods} observations within {_bpsBand} bps";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is not IOrderBookState state)
                return 0m;

            var currentDepth = state.GetBidDepthWithinBps(_bpsBand) + state.GetAskDepthWithinBps(_bpsBand);

            if (context.HistoricalObservations.Count <= 1)
                return 0m;

            // Find the observation N periods ago
            var lookbackIndex = Math.Max(0, context.HistoricalObservations.Count - 1 - _lookbackPeriods);
            var pastObservation = context.HistoricalObservations[lookbackIndex];

            if (pastObservation.State is not IOrderBookState pastState)
                return 0m;

            var pastDepth = pastState.GetBidDepthWithinBps(_bpsBand) + pastState.GetAskDepthWithinBps(_bpsBand);

            return currentDepth - pastDepth;
        }
    }

    /// <summary>
    /// Liquidity replenishment rate feature. Measures the rate at which depth returns
    /// after depletion. Uses a rolling window of observations.
    /// </summary>
    public class LiquidityReplenishmentRateFeature : FeatureBase
    {
        private readonly int _windowSize;
        private readonly decimal _bpsBand;

        public LiquidityReplenishmentRateFeature(int windowSize = 10, decimal bpsBand = 10m)
        {
            _windowSize = windowSize;
            _bpsBand = bpsBand;
        }

        public override string Name => $"replenishment_rate_{_windowSize}obs_{_bpsBand}bps";
        public override string Description => $"Rate of depth change over {_windowSize} observations within {_bpsBand} bps";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is not IOrderBookState state)
                return 0m;

            var obs = context.HistoricalObservations;

            if (obs.Count <= 1)
                return 0m;

            // Compute total depth of the last windowSize observations
            var window = obs.Skip(Math.Max(0, obs.Count - _windowSize)).ToList();

            if (window.Count < 2)
                return 0m;

            decimal firstDepth = 0m;
            decimal lastDepth = 0m;

            if (window[0].State is IOrderBookState firstState)
            {
                firstDepth = firstState.GetBidDepthWithinBps(_bpsBand) + firstState.GetAskDepthWithinBps(_bpsBand);
            }

            if (window[window.Count - 1].State is IOrderBookState lastState)
            {
                lastDepth = lastState.GetBidDepthWithinBps(_bpsBand) + lastState.GetAskDepthWithinBps(_bpsBand);
            }

            var totalChange = lastDepth - firstDepth;
            var avgChange = totalChange / window.Count;

            return avgChange;
        }
    }

    /// <summary>
    /// Depth persistence feature. Measures how consistently depth levels persist
    /// across observations (0 = highly volatile, 1 = highly stable).
    /// </summary>
    public class DepthPersistenceFeature : FeatureBase
    {
        private readonly int _windowSize;

        public DepthPersistenceFeature(int windowSize = 20)
        {
            _windowSize = windowSize;
        }

        public override string Name => $"depth_persistence_{_windowSize}obs";
        public override string Description => $"Persistence/stability of depth over {_windowSize} observations";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            var obs = context.HistoricalObservations;

            if (obs.Count < 2)
                return 1m;

            var window = obs.Skip(Math.Max(0, obs.Count - _windowSize)).ToList();

            var depths = new List<decimal>();
            foreach (var o in window)
            {
                if (o.State is IOrderBookState state)
                {
                    depths.Add(state.BidDepth + state.AskDepth);
                }
            }

            if (depths.Count < 2)
                return 1m;

            var mean = depths.Average();
            if (mean == 0) return 0m;

            var variance = depths.Select(d => d - mean)
                .Select(d => d * d)
                .Average();
            var stdDev = (decimal)Math.Sqrt((double)variance);
            var cv = stdDev / mean;

            // Persistence is inverse of coefficient of variation, normalized to [0, 1]
            return Math.Max(0m, 1m - cv);
        }
    }
}