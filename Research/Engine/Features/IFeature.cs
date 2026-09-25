namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Contract for a single feature.
    /// Features transform market state into a numeric value.
    /// Features are temporal-causality constrained (only see data up to current time).
    /// </summary>
    public interface IFeature
    {
        /// <summary>
        /// Unique feature identifier
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Feature description
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Names of other measurements this feature reads from. Declared dependencies let the
        /// <see cref="FeatureEngine"/> compute values in dependency order and compute only the
        /// transitive closure of what a job selected. Raw measurements (fields read straight off
        /// the observation/state) return an empty list.
        /// </summary>
        IReadOnlyList<string> Dependencies { get; }

        /// <summary>
        /// Computes the feature value from market state.
        /// This method MUST NOT use any future information.
        /// </summary>
        decimal Compute(Observations.Observation observation, FeatureContext context);

        /// <summary>
        /// Resets feature internal state (for when state window changes)
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Context for feature computation.
    /// Provides access to historical observations and labels.
    /// </summary>
    public class FeatureContext
    {
        /// <summary>
        /// Current observation being processed
        /// </summary>
        public Observations.Observation CurrentObservation { get; set; }

        /// <summary>
        /// Historical observations up to (and including) current.
        /// Index 0 is oldest, Count-1 is current.
        /// </summary>
        public List<Observations.Observation> HistoricalObservations { get; set; } = new();

        /// <summary>
        /// Maximum number of historical observations to retain
        /// </summary>
        public int MaxHistory { get; set; } = 1000;

        /// <summary>
        /// Adds an observation to history
        /// </summary>
        public void AddObservation(Observations.Observation obs)
        {
            HistoricalObservations.Add(obs);
            if (HistoricalObservations.Count > MaxHistory)
            {
                HistoricalObservations.RemoveAt(0);
            }
        }

        /// <summary>
        /// Gets the current observation index (0-based)
        /// </summary>
        public int CurrentIndex => HistoricalObservations.Count - 1;

        /// <summary>
        /// Measurements computed so far for the current observation, keyed by measurement name and
        /// in dependency order (dependencies are always present before the dependent). The engine
        /// repopulates this before each <see cref="IFeature.Compute"/> call, so derived measurements
        /// can read their inputs with <see cref="GetMeasurement"/> instead of recomputing them.
        /// </summary>
        public Dictionary<string, decimal> CurrentMeasurements { get; set; } = new();

        /// <summary>
        /// Whether a measurement value is available for the current observation.
        /// </summary>
        public bool HasMeasurement(string name)
        {
            return name != null && CurrentMeasurements.ContainsKey(name);
        }

        /// <summary>
        /// Gets a previously computed measurement value for the current observation. Returns 0 when
        /// the measurement has not been computed yet (or was not selected).
        /// </summary>
        public decimal GetMeasurement(string name)
        {
            return name != null && CurrentMeasurements.TryGetValue(name, out var value) ? value : 0m;
        }
    }
}