using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Depth feature. Total order book depth on both sides.
    /// For order book markets only; returns 0 for quote-based markets.
    /// </summary>
    public class DepthFeature : FeatureBase
    {
        public override string Name => "depth";
        public override string Description => "Total order book depth (bid + ask)";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is IOrderBookState bookState)
            {
                return bookState.BidDepth + bookState.AskDepth;
            }
            return 0m;
        }
    }

    /// <summary>
    /// Bid depth feature. Total quantity on bid side.
    /// </summary>
    public class BidDepthFeature : FeatureBase
    {
        public override string Name => "bid_depth";
        public override string Description => "Total order book depth on bid side";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is IOrderBookState bookState)
            {
                return bookState.BidDepth;
            }
            return 0m;
        }
    }

    /// <summary>
    /// Ask depth feature. Total quantity on ask side.
    /// </summary>
    public class AskDepthFeature : FeatureBase
    {
        public override string Name => "ask_depth";
        public override string Description => "Total order book depth on ask side";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is IOrderBookState bookState)
            {
                return bookState.AskDepth;
            }
            return 0m;
        }
    }

    /// <summary>
    /// Imbalance feature. Normalized order book imbalance.
    /// Positive = more bid depth, negative = more ask depth.
    /// Range: [-1, 1].
    /// </summary>
    public class ImbalanceFeature : FeatureBase
    {
        public override string Name => "imbalance";
        public override string Description => "Normalized order book imbalance [-1, 1]";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is IOrderBookState bookState)
            {
                return bookState.DepthImbalance;
            }
            return 0m;
        }
    }

    /// <summary>
    /// Depth ratio feature. Ratio of bid depth to ask depth.
    /// Values > 1 indicate more bids, < 1 more asks.
    /// </summary>
    public class DepthRatioFeature : FeatureBase
    {
        public override string Name => "depth_ratio";
        public override string Description => "Ratio of bid depth to ask depth";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is IOrderBookState bookState)
            {
                if (bookState.AskDepth == 0) return 0m;
                return bookState.BidDepth / bookState.AskDepth;
            }
            return 0m;
        }
    }

    /// <summary>
    /// Structural imbalance feature. Same as ImbalanceFeature but distinguishes
    /// by computing imbalance from multiple book levels within basis point bands.
    /// </summary>
    public class StructuralImbalanceFeature : FeatureBase
    {
        private readonly decimal _bpsBand;

        /// <summary>
        /// Creates a StructuralImbalanceFeature
        /// </summary>
        /// <param name="bpsBand">Basis point band for depth calculation</param>
        public StructuralImbalanceFeature(decimal bpsBand = 10m)
        {
            _bpsBand = bpsBand;
        }

        public override string Name => $"structural_imbalance_{_bpsBand}bps";
        public override string Description => $"Order book imbalance within {_bpsBand} bps of mid";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is IOrderBookState bookState)
            {
                var bidDepth = bookState.GetBidDepthWithinBps(_bpsBand);
                var askDepth = bookState.GetAskDepthWithinBps(_bpsBand);
                var total = bidDepth + askDepth;

                if (total == 0) return 0m;
                return (bidDepth - askDepth) / total;
            }
            return 0m;
        }
    }

    /// <summary>
    /// Liquidity wall feature. Distance in basis points to the nearest liquidity wall
    /// (a level with significantly larger quantity than surrounding levels).
    /// </summary>
    public class LiquidityWallFeature : FeatureBase
    {
        private readonly decimal _wallThresholdMultiplier;

        public LiquidityWallFeature(decimal wallThresholdMultiplier = 3m)
        {
            _wallThresholdMultiplier = wallThresholdMultiplier;
        }

        public override string Name => $"liquidity_wall_{_wallThresholdMultiplier}x";
        public override string Description => $"Distance (bps) to nearest liquidity wall (qty > {_wallThresholdMultiplier}x typical)";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is not MarketState.MarketState state)
                return 0m;

            var mid = state.MidPrice;
            if (mid <= 0) return 0m;

            var levels = new List<(decimal Price, decimal Quantity)>();

            foreach (var level in state.BidLevels_List)
            {
                levels.Add((level.Price, level.Quantity));
            }
            foreach (var level in state.AskLevels_List)
            {
                levels.Add((level.Price, level.Quantity));
            }

            if (levels.Count < 3) return 0m;

            var avgQuantity = levels.Average(l => l.Quantity);
            if (avgQuantity <= 0) return 0m;

            var wallLevel = levels
                .Where(l => l.Quantity > _wallThresholdMultiplier * avgQuantity)
                .OrderBy(l => Math.Abs(l.Price - mid))
                .FirstOrDefault();

            if (wallLevel.Quantity == 0) return 0m;

            return Math.Abs(wallLevel.Price - mid) / mid * 10000m;
        }
    }

    /// <summary>
    /// Resistance feature. Distance in basis points to the nearest large ask level
    /// that could act as selling resistance.
    /// </summary>
    public class ResistanceFeature : FeatureBase
    {
        private readonly decimal _resistanceFactor;

        public ResistanceFeature(decimal resistanceFactor = 2m)
        {
            _resistanceFactor = resistanceFactor;
        }

        public override string Name => $"resistance_{_resistanceFactor}x";
        public override string Description => $"Distance (bps) to nearest resistance level (ask qty > {_resistanceFactor}x typical)";

        public override decimal Compute(Observation observation, FeatureContext context)
        {
            if (observation.State is not MarketState.MarketState state)
                return 0m;

            var mid = state.MidPrice;
            if (mid <= 0) return 0m;

            var askLevels = state.AskLevels_List.ToList();
            if (askLevels.Count < 2) return 0m;

            var avgAsk = askLevels.Average(l => l.Quantity);
            if (avgAsk <= 0) return 0m;

            var resistance = askLevels
                .Where(l => l.Quantity > _resistanceFactor * avgAsk && l.Price > mid)
                .OrderBy(l => l.Price)
                .FirstOrDefault();

            if (resistance.Quantity == 0) return 0m;

            return (resistance.Price - mid) / mid * 10000m;
        }
    }
}