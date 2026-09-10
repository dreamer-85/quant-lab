using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Base class for experiments providing common infrastructure.
    /// Observation/feature retention is bounded (<see cref="MaxRetainedObservations"/>) so that
    /// long-running streaming experiments never grow memory with dataset size. Experiments that
    /// need full history must aggregate incrementally or stream via <see cref="IExperimentOutput"/>.
    /// </summary>
    public abstract class ExperimentBase : IExperiment
    {
        private readonly int _retentionLimit;
        private readonly int _outcomeLimit;
        protected ExperimentContext _context;
        protected List<Observation> _observations = new();
        protected List<FeatureResult> _featureResults = new();
        protected List<OutcomeData> _outcomes = new();

        /// <summary>
        /// Maximum number of observations/feature results retained in memory.
        /// Older entries are evicted (FIFO) beyond this cap. Default 100,000.
        /// Set to 0 to retain nothing (aggregate-only experiments).
        /// </summary>
        public int MaxRetainedObservations { get; }

        /// <summary>
        /// Maximum number of outcomes (labels) retained in memory. Older entries are evicted (FIFO)
        /// beyond this cap. Default 100,000. Set to 0 to retain nothing (metric-only experiments).
        /// Experiments that need the full label set must aggregate incrementally instead of retaining.
        /// </summary>
        public int MaxRetainedOutcomes { get; }

        /// <summary>
        /// Number of outcomes currently retained
        /// </summary>
        public int OutcomeCount => _outcomes.Count;

        protected ExperimentBase(int maxRetainedObservations = 100_000, int maxRetainedOutcomes = 100_000)
        {
            _retentionLimit = Math.Max(0, maxRetainedObservations);
            _outcomeLimit = Math.Max(0, maxRetainedOutcomes);
            MaxRetainedObservations = _retentionLimit;
            MaxRetainedOutcomes = _outcomeLimit;
        }

        /// <summary>
        /// Experiment identifier
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Experiment description
        /// </summary>
        public virtual string Description => Name;

        /// <summary>
        /// Version of the experiment logic (for reproducibility)
        /// </summary>
        public virtual string Version => "1.0.0";

        /// <summary>
        /// Features that should be computed before this experiment runs
        /// </summary>
        public virtual List<string> RequiredFeatures => new();

        /// <summary>
        /// Initializes the experiment with runtime context
        /// </summary>
        public virtual void Initialize(ExperimentContext context)
        {
            _context = context;
            Reset();
        }

        /// <summary>
        /// Called for each observation with its computed feature values
        /// </summary>
        public virtual void OnObservation(Observation observation, FeatureResult features)
        {
            _observations.Add(observation);
            _featureResults.Add(features);

            // Bound memory: evict oldest beyond the retention limit.
            if (_retentionLimit > 0 && _observations.Count > _retentionLimit)
            {
                var excess = _observations.Count - _retentionLimit;
                _observations.RemoveRange(0, excess);
                _featureResults.RemoveRange(0, excess);
            }
            else if (_retentionLimit == 0)
            {
                _observations.Clear();
                _featureResults.Clear();
            }
        }

        /// <summary>
        /// Called with outcome data for label computation
        /// </summary>
        public virtual void OnOutcome(OutcomeData outcome)
        {
            _outcomes.Add(outcome);

            // Bound memory: evict oldest outcomes beyond the retention limit.
            if (_outcomeLimit > 0 && _outcomes.Count > _outcomeLimit)
            {
                _outcomes.RemoveRange(0, _outcomes.Count - _outcomeLimit);
            }
            else if (_outcomeLimit == 0)
            {
                _outcomes.Clear();
            }
        }

        /// <summary>
        /// Finalizes the experiment and returns results
        /// </summary>
        public virtual ExperimentResult Finalize()
        {
            return new ExperimentResult
            {
                ExperimentName = Name,
                ExperimentVersion = Version,
                IsSuccess = true,
                ConfigurationHash = _context?.ConfigurationHash ?? string.Empty
            };
        }

        /// <summary>
        /// Resets experiment state
        /// </summary>
        public virtual void Reset()
        {
            _observations.Clear();
            _featureResults.Clear();
            _outcomes.Clear();
        }

        /// <summary>
        /// Gets the manually observable recent observations for analysis
        /// </summary>
        protected IReadOnlyList<Observation> Observations => _observations;

        /// <summary>
        /// Gets the feature results for observations
        /// </summary>
        protected IReadOnlyList<FeatureResult> FeatureResults => _featureResults;

        /// <summary>
        /// Gets outcome data
        /// </summary>
        protected IReadOnlyList<OutcomeData> Outcomes => _outcomes;

        /// <summary>
        /// Computes the return between two prices
        /// </summary>
        protected static decimal? ComputeReturn(decimal referencePrice, decimal futurePrice)
        {
            if (referencePrice == 0) return null;
            return (futurePrice - referencePrice) / referencePrice;
        }
    }
}