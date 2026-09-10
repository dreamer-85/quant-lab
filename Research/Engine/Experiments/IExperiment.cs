using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Interface for research experiments.
    /// Experiments define a hypothesis test over observations and feature values.
    /// Experiments must NOT depend on where they run (local vs cloud).
    /// </summary>
    public interface IExperiment
    {
        /// <summary>
        /// Experiment identifier
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Experiment description
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Version of the experiment logic (for reproducibility)
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Features that should be computed before this experiment runs
        /// </summary>
        List<string> RequiredFeatures { get; }

        /// <summary>
        /// Initializes the experiment with runtime context
        /// </summary>
        void Initialize(ExperimentContext context);

        /// <summary>
        /// Called for each observation with its computed feature values
        /// </summary>
        void OnObservation(Observation observation, FeatureResult features);

        /// <summary>
        /// Called with outcome data (future relative to observations) for label computation
        /// </summary>
        void OnOutcome(OutcomeData outcome);

        /// <summary>
        /// Finalizes the experiment and returns results
        /// </summary>
        ExperimentResult Finalize();

        /// <summary>
        /// Resets experiment state
        /// </summary>
        void Reset();
    }

    /// <summary>
    /// Runtime context for an experiment
    /// </summary>
    public class ExperimentContext
    {
        /// <summary>
        /// Experiment configuration
        /// </summary>
        public Dictionary<string, string> Configuration { get; set; } = new();

        /// <summary>
        /// Output sink for experiment data
        /// </summary>
        public IExperimentOutput Output { get; set; }

        /// <summary>
        /// Start time of the experiment
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// End time of the experiment
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// Configuration hash for reproducibility
        /// </summary>
        public string ConfigurationHash { get; set; } = string.Empty;

        /// <summary>
        /// Gets a configuration value
        /// </summary>
        public string GetConfig(string key, string defaultValue = "")
        {
            return Configuration.TryGetValue(key, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// Gets an integer configuration value
        /// </summary>
        public int GetConfigInt(string key, int defaultValue = 0)
        {
            var value = GetConfig(key, null);
            return value != null && int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        /// <summary>
        /// Gets a decimal configuration value
        /// </summary>
        public decimal GetConfigDecimal(string key, decimal defaultValue = 0)
        {
            var value = GetConfig(key, null);
            return value != null && decimal.TryParse(value, out var parsed) ? parsed : defaultValue;
        }
    }

    /// <summary>
    /// Outcome data for label computation.
    /// Contains future information that must ONLY be used for outcome/label calculation,
    /// never for feature computation.
    /// </summary>
    public class OutcomeData
    {
        /// <summary>
        /// Reference timestamp (the time the outcome is measured relative to)
        /// </summary>
        public DateTime ReferenceTimestamp { get; set; }

        /// <summary>
        /// Market state at the outcome timestamp
        /// </summary>
        public MarketState.MarketState OutcomeState { get; set; }

        /// <summary>
        /// Future price at outcome horizon
        /// </summary>
        public decimal FuturePrice { get; set; }

        /// <summary>
        /// Horizon used for the outcome (e.g., 1m, 5m, 10m)
        /// </summary>
        public TimeSpan Horizon { get; set; }

        /// <summary>
        /// Timestamp of the observation that realized the future price (when delivered by
        /// <see cref="DelayedLabelResolver"/>); null for hand-delivered outcomes.
        /// </summary>
        public DateTime? ObservedAt { get; set; }

        /// <summary>
        /// Return over the horizon: (FuturePrice - ReferencePrice) / ReferencePrice
        /// </summary>
        public decimal? OutcomeReturn =>
            ReferencePrice.HasValue && ReferencePrice.Value != 0
                ? (FuturePrice - ReferencePrice.Value) / ReferencePrice.Value
                : null;

        /// <summary>
        /// Reference price (price at the reference timestamp)
        /// </summary>
        public decimal? ReferencePrice { get; set; }
    }

    /// <summary>
    /// Result of an experiment
    /// </summary>
    public class ExperimentResult
    {
        /// <summary>
        /// Experiment name
        /// </summary>
        public string ExperimentName { get; set; } = string.Empty;

        /// <summary>
        /// Experiment version
        /// </summary>
        public string ExperimentVersion { get; set; } = string.Empty;

        /// <summary>
        /// Whether the experiment completed successfully
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Error message if the experiment failed
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// Computed metrics keyed by name
        /// </summary>
        public Dictionary<string, object> Metrics { get; set; } = new();

        /// <summary>
        /// Result rows (for Parquet/CSV output)
        /// Each row is a dictionary of values
        /// </summary>
        public List<Dictionary<string, object>> Rows { get; set; } = new();

        /// <summary>
        /// Configuration hash
        /// </summary>
        public string ConfigurationHash { get; set; } = string.Empty;

        /// <summary>
        /// Metadata about the run
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();

        /// <summary>
        /// Adds a metric
        /// </summary>
        public void AddMetric(string name, object value)
        {
            Metrics[name] = value;
        }

        /// <summary>
        /// Adds a result row
        /// </summary>
        public void AddRow(Dictionary<string, object> row)
        {
            Rows.Add(row);
        }
    }

    /// <summary>
    /// Interface for experiment output sinks
    /// </summary>
    public interface IExperimentOutput
    {
        /// <summary>
        /// Writes a result row to the output
        /// </summary>
        void WriteRow(Dictionary<string, object> row);

        /// <summary>
        /// Writes a metric to the output
        /// </summary>
        void WriteMetric(string name, object value);

        /// <summary>
        /// Finalizes the output
        /// </summary>
        void Finalize();
    }
}