using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Computes feature values for observations.
    /// Manages the feature history context to enforce temporal causality.
    /// </summary>
    public class FeatureEngine
    {
        private readonly List<IFeature> _features;
        private readonly FeatureContext _context;

        /// <summary>
        /// Gets the list of active features
        /// </summary>
        public IReadOnlyList<IFeature> Features => _features;

        /// <summary>
        /// Current context state
        /// </summary>
        public FeatureContext Context => _context;

        /// <summary>
        /// Creates a new FeatureEngine
        /// </summary>
        public FeatureEngine(IEnumerable<IFeature> features, int maxHistory = 1000)
        {
            _features = features.ToList();
            _context = new FeatureContext { MaxHistory = maxHistory };
        }

        /// <summary>
        /// Creates a FeatureEngine from feature names using the registry, with optional per-feature parameters
        /// </summary>
        public static FeatureEngine FromNames(IEnumerable<string> featureNames, int maxHistory = 1000)
        {
            return FromNames(featureNames, parameters: null, maxHistory: maxHistory);
        }

        /// <summary>
        /// Creates a FeatureEngine from feature names using the registry, applying per-feature parameters
        /// from job configuration
        /// </summary>
        public static FeatureEngine FromNames(IEnumerable<string> featureNames, FeatureParameters parameters, int maxHistory = 1000)
        {
            var features = FeatureRegistry.Instance.CreateMany(featureNames, parameters);
            return new FeatureEngine(features, maxHistory);
        }

        /// <summary>
        /// Computes feature values for a single observation
        /// </summary>
        public FeatureResult Compute(Observation observation)
        {
            _context.CurrentObservation = observation;

            var values = new Dictionary<string, decimal>();
            foreach (var feature in _features)
            {
                var value = feature.Compute(observation, _context);
                values[feature.Name] = value;
            }

            // Add observation to history AFTER computing (enforces temporal causality)
            _context.AddObservation(observation);

            return new FeatureResult
            {
                Timestamp = observation.Timestamp,
                Symbol = observation.State?.Symbol ?? QuantConnect.Symbol.Empty,
                Values = values
            };
        }

        /// <summary>
        /// Computes feature values for a batch of observations
        /// </summary>
        public IEnumerable<FeatureResult> ComputeBatch(IEnumerable<Observation> observations)
        {
            foreach (var observation in observations)
            {
                yield return Compute(observation);
            }
        }

        /// <summary>
        /// Resets all features and context
        /// </summary>
        public void Reset()
        {
            foreach (var feature in _features)
            {
                feature.Reset();
            }
            _context.HistoricalObservations.Clear();
            _context.CurrentObservation = null;
        }
    }

    /// <summary>
    /// Result of a single feature computation
    /// </summary>
    public class FeatureResult
    {
        /// <summary>
        /// Timestamp of the observation
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Symbol of the observation
        /// </summary>
        public QuantConnect.Symbol Symbol { get; set; }

        /// <summary>
        /// Computed feature values keyed by feature name
        /// </summary>
        public Dictionary<string, decimal> Values { get; set; } = new();

        /// <summary>
        /// Gets a feature value by name
        /// </summary>
        public decimal Get(string featureName)
        {
            return Values.TryGetValue(featureName, out var value) ? value : 0m;
        }

        /// <summary>
        /// Converts to a flat CSV-friendly row
        /// </summary>
        public Dictionary<string, object> ToRow()
        {
            var row = new Dictionary<string, object>
            {
                ["timestamp"] = Timestamp,
                ["symbol"] = Symbol.Value
            };

            foreach (var kvp in Values)
            {
                row[kvp.Key] = kvp.Value;
            }

            return row;
        }
    }
}