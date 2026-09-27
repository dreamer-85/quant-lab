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
        private readonly Dictionary<string, Func<FeatureParams, IFeature>> _parameterizedRegistrations;
        private readonly object _lock = new();

        /// <summary>
        /// Creates a new FeatureRegistry
        /// </summary>
        public FeatureRegistry()
        {
            _registrations = new Dictionary<string, Func<IFeature>>(StringComparer.OrdinalIgnoreCase);
            _parameterizedRegistrations = new Dictionary<string, Func<FeatureParams, IFeature>>(StringComparer.OrdinalIgnoreCase);
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
        /// Registers a parameterized feature factory. The factory receives a <see cref="FeatureParams"/>
        /// bag (parsed from "feature.&lt;name&gt;.&lt;param&gt;" job configuration keys) and returns an
        /// instance with those parameters applied.
        /// </summary>
        public void RegisterParameterized(string name, Func<FeatureParams, IFeature> factory)
        {
            lock (_lock)
            {
                _parameterizedRegistrations[name] = factory;
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
        /// Creates a feature by name with default parameters
        /// </summary>
        public IFeature Create(string name)
        {
            return Create(name, FeatureParams.Empty);
        }

        /// <summary>
        /// Creates a feature by name, applying the given parameter set for parameterized features
        /// </summary>
        public IFeature Create(string name, FeatureParams parameters)
        {
            lock (_lock)
            {
                if (_parameterizedRegistrations.TryGetValue(name, out var parameterized))
                {
                    return parameterized(parameters ?? FeatureParams.Empty);
                }

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
            return CreateMany(names, null);
        }

        /// <summary>
        /// Creates multiple features by name, applying per-feature parameters resolved from job config
        /// </summary>
        public List<IFeature> CreateMany(IEnumerable<string> names, FeatureParameters parameters)
        {
            var features = new List<IFeature>();
            foreach (var name in names)
            {
                features.Add(Create(name, parameters?.For(name)));
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
                return _registrations.ContainsKey(name) || _parameterizedRegistrations.ContainsKey(name);
            }
        }

        /// <summary>
        /// Gets all registered feature names
        /// </summary>
        public List<string> GetRegisteredNames()
        {
            lock (_lock)
            {
                return _registrations.Keys
                    .Concat(_parameterizedRegistrations.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToList();
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
            RegisterParameterized("structural_imbalance", p =>
                new StructuralImbalanceFeature(p.GetDecimal("bps_band", 10m)));
            RegisterParameterized("liquidity_wall", p =>
                new LiquidityWallFeature(p.GetDecimal("wall_threshold_multiplier", 3m)));
            RegisterParameterized("resistance", p =>
                new ResistanceFeature(p.GetDecimal("resistance_factor", 2m)));
            Register("aggressive_buy_volume", () => new AggressiveBuyVolumeFeature());
            Register("aggressive_sell_volume", () => new AggressiveSellVolumeFeature());
            Register("net_flow", () => new NetFlowFeature());

            RegisterParameterized("liquidity_depletion", p =>
                new LiquidityDepletionFeature(p.GetInt("lookback_periods", 5), p.GetDecimal("bps_band", 10m)));
            RegisterParameterized("replenishment_rate", p =>
                new LiquidityReplenishmentRateFeature(p.GetInt("window_size", 10), p.GetDecimal("bps_band", 10m)));
            RegisterParameterized("depth_persistence", p =>
                new DepthPersistenceFeature(p.GetInt("window_size", 20)));
        }
    }
}