namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Registry for discovering and creating features.
    /// Enables adding new features without modifying the core engine.
    /// </summary>
    public class FeatureRegistry
    {
        private static readonly Lazy<FeatureRegistry> _instance = new(() => new FeatureRegistry(), true);

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static FeatureRegistry Instance => _instance.Value;

        private readonly Dictionary<string, Func<IFeature>> _registrations;
        private readonly object _lock = new();

        /// <summary>
        /// Creates a new FeatureRegistry
        /// </summary>
        public FeatureRegistry()
        {
            _registrations = new Dictionary<string, Func<IFeature>>(StringComparer.OrdinalIgnoreCase);
            RegisterDefaultFeatures();
        }

        /// <summary>
        /// Registers a feature factory
        /// </summary>
        public void Register(string name, Func<IFeature> factory)
        {
            lock (_lock)
            {
                _registrations[name] = factory;
            }
        }

        /// <summary>
        /// Registers multiple features
        /// </summary>
        public void Register(params IFeature[] features)
        {
            foreach (var feature in features)
            {
                Register(feature.Name, () => feature);
            }
        }

        /// <summary>
        /// Creates a feature by name
        /// </summary>
        public IFeature Create(string name)
        {
            lock (_lock)
            {
                if (_registrations.TryGetValue(name, out var factory))
                {
                    return factory();
                }
            }

            throw new KeyNotFoundException($"Feature '{name}' not found in registry.");
        }

        /// <summary>
        /// Creates multiple features by name
        /// </summary>
        public List<IFeature> CreateMany(IEnumerable<string> names)
        {
            var features = new List<IFeature>();
            foreach (var name in names)
            {
                features.Add(Create(name));
            }
            return features;
        }

        /// <summary>
        /// Checks if a feature name is registered
        /// </summary>
        public bool IsRegistered(string name)
        {
            lock (_lock)
            {
                return _registrations.ContainsKey(name);
            }
        }

        /// <summary>
        /// Gets all registered feature names
        /// </summary>
        public List<string> GetRegisteredNames()
        {
            lock (_lock)
            {
                return _registrations.Keys.OrderBy(k => k).ToList();
            }
        }

        /// <summary>
        /// Registers all default features
        /// </summary>
        private void RegisterDefaultFeatures()
        {
            Register("mid_price", () => new MidPriceFeature());
            Register("spread", () => new SpreadFeature());
            Register("spread_bps", () => new SpreadBpsFeature());
            Register("trade_flow", () => new TradeFlowFeature());
            Register("cumulative_flow", () => new CumulativeFlowFeature());
            Register("trade_intensity", () => new TradeIntensityFeature());
            Register("trade_volume", () => new TradeVolumeFeature());
            Register("depth", () => new DepthFeature());
            Register("bid_depth", () => new BidDepthFeature());
            Register("ask_depth", () => new AskDepthFeature());
            Register("imbalance", () => new ImbalanceFeature());
            Register("depth_ratio", () => new DepthRatioFeature());
            Register("liquidity_wall", () => new LiquidityWallFeature());
            Register("resistance", () => new ResistanceFeature());
            Register("structural_imbalance", () => new StructuralImbalanceFeature());
            Register("liquidity_depletion", () => new LiquidityDepletionFeature());
            Register("replenishment_rate", () => new LiquidityReplenishmentRateFeature());
            Register("depth_persistence", () => new DepthPersistenceFeature());
        }
    }
}