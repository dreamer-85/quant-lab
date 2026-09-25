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
        /// Gets the list of active features in dependency order (dependencies always precede the
        /// features that read them).
        /// </summary>
        public IReadOnlyList<IFeature> Features => _features;

        /// <summary>
        /// Current context state
        /// </summary>
        public FeatureContext Context => _context;

        /// <summary>
        /// Creates a new FeatureEngine. Features are ordered by their declared <see cref="IFeature.Dependencies"/>.
        /// </summary>
        public FeatureEngine(IEnumerable<IFeature> features, int maxHistory = 1000)
        {
            _features = OrderByDependencies(features);
            _context = new FeatureContext { MaxHistory = maxHistory };
        }

        /// <summary>
        /// Creates a FeatureEngine from feature names using the registry, expanding each feature's
        /// declared dependencies (transitive closure) so selecting a derived measurement
        /// automatically computes everything it needs - and nothing else.
        /// </summary>
        public static FeatureEngine FromNames(IEnumerable<string> featureNames, int maxHistory = 1000)
        {
            return FromNames(featureNames, parameters: null, maxHistory: maxHistory);
        }

        /// <summary>
        /// Creates a FeatureEngine from feature names using the registry, applying per-feature
        /// parameters from job configuration. Declared dependencies of the selected features are
        /// added automatically (with default parameters).
        /// </summary>
        public static FeatureEngine FromNames(IEnumerable<string> featureNames, FeatureParameters parameters, int maxHistory = 1000)
        {
            var registry = FeatureRegistry.Instance;
            var instances = new List<IFeature>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(featureNames ?? Enumerable.Empty<string>());

            while (pending.Count > 0)
            {
                var name = pending.Dequeue();
                if (name == null || !seen.Add(name))
                {
                    continue;
                }

                var instance = registry.Create(name, parameters?.For(name) ?? FeatureParams.Empty);
                instances.Add(instance);

                foreach (var dependency in instance.Dependencies)
                {
                    pending.Enqueue(dependency);
                }
            }

            return new FeatureEngine(instances, maxHistory);
        }

        /// <summary>
        /// Computes feature values for a single observation. Values are produced in dependency
        /// order and recorded on <see cref="FeatureContext.CurrentMeasurements"/> so derived
        /// measurements can read their inputs by name.
        /// </summary>
        public FeatureResult Compute(Observation observation)
        {
            _context.CurrentObservation = observation;
            _context.CurrentMeasurements = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in _features)
            {
                var value = feature.Compute(observation, _context);
                values[feature.Name] = value;
                _context.CurrentMeasurements[feature.Name] = value;
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
            _context.CurrentMeasurements = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns the features in dependency order via a stable depth-first topological sort.
        /// Independent features keep their original relative order; cycles and references to
        /// measurements that were not selected are rejected with a clear error.
        /// </summary>
        private static List<IFeature> OrderByDependencies(IEnumerable<IFeature> features)
        {
            var list = features.ToList();
            var byName = new Dictionary<string, IFeature>(StringComparer.OrdinalIgnoreCase);
            foreach (var feature in list)
            {
                if (feature?.Name == null)
                {
                    throw new InvalidOperationException("A feature in the engine has a null name.");
                }

                byName[feature.Name] = feature;
            }

            var state = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<IFeature>(list.Count);

            void Visit(IFeature feature)
            {
                if (state.TryGetValue(feature.Name, out var mark))
                {
                    if (mark == 1)
                    {
                        throw new InvalidOperationException(
                            $"Circular measurement dependency detected involving '{feature.Name}'.");
                    }

                    return;
                }

                state[feature.Name] = 1;
                foreach (var dependency in feature.Dependencies)
                {
                    if (dependency == null)
                    {
                        continue;
                    }

                    if (!byName.TryGetValue(dependency, out var dependencyFeature))
                    {
                        throw new InvalidOperationException(
                            $"Measurement '{feature.Name}' depends on '{dependency}', which is not part of the selected measurements. " +
                            "Add it to the job features or declare it as a dependency of another selected feature.");
                    }

                    Visit(dependencyFeature);
                }

                state[feature.Name] = 2;
                ordered.Add(feature);
            }

            foreach (var feature in list)
            {
                Visit(feature);
            }

            return ordered;
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