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
    }
}