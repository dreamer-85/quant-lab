using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Runs experiments over observations and feature values.
    /// Orchestrates the feature computation pipeline and experiment callbacks.
    /// </summary>
    public class ExperimentEngine
    {
        private readonly FeatureEngine _featureEngine;
        private readonly IExperiment _experiment;

        /// <summary>
        /// Creates a new ExperimentEngine
        /// </summary>
        public ExperimentEngine(IExperiment experiment, FeatureEngine featureEngine = null)
        {
            _experiment = experiment;
            _featureEngine = featureEngine ?? new FeatureEngine(new List<IFeature>());
        }

        /// <summary>
        /// Runs the experiment over a sequence of observations
        /// </summary>
        public ExperimentResult Run(
            IEnumerable<Observation> observations,
            ExperimentContext context = null)
        {
            context ??= new ExperimentContext();
            _experiment.Initialize(context);

            foreach (var observation in observations)
            {
                var features = _featureEngine.Compute(observation);
                _experiment.OnObservation(observation, features);

                // Check for cancellation
                if (Thread.CurrentThread.IsThreadPoolThread && Thread.CurrentThread.IsBackground)
                {
                    if ((Thread.CurrentThread as Thread)?.IsAlive == false)
                        break;
                }
            }

            var result = _experiment.Finalize();
            result.ConfigurationHash = context.ConfigurationHash;
            return result;
        }

        /// <summary>
        /// Runs the experiment and also feeds outcome data
        /// </summary>
        public ExperimentResult RunWithOutcomes(
            IEnumerable<Observation> observations,
            IEnumerable<OutcomeData> outcomes,
            ExperimentContext context = null)
        {
            context ??= new ExperimentContext();
            _experiment.Initialize(context);

            foreach (var observation in observations)
            {
                var features = _featureEngine.Compute(observation);
                _experiment.OnObservation(observation, features);
            }

            foreach (var outcome in outcomes)
            {
                _experiment.OnOutcome(outcome);
            }

            var result = _experiment.Finalize();
            result.ConfigurationHash = context.ConfigurationHash;
            return result;
        }

        /// <summary>
        /// Runs the experiment in a streaming manner with bounded memory.
        /// Use this for very large datasets.
        /// </summary>
        public ExperimentResult RunStreaming(
            IEnumerable<Observation> observations,
            ExperimentContext context = null,
            int maxBufferedObservations = 10000)
        {
            context ??= new ExperimentContext();
            _experiment.Initialize(context);

            var buffer = new List<Observation>();
            var bufferedFeatures = new List<FeatureResult>();

            foreach (var observation in observations)
            {
                buffer.Add(observation);
                var features = _featureEngine.Compute(observation);
                bufferedFeatures.Add(features);

                if (buffer.Count >= maxBufferedObservations)
                {
                    FlushBuffer(buffer, bufferedFeatures);
                    buffer = new List<Observation>();
                    bufferedFeatures = new List<FeatureResult>();
                }
            }

            // Flush remaining
            if (buffer.Count > 0)
            {
                FlushBuffer(buffer, bufferedFeatures);
            }

            var result = _experiment.Finalize();
            result.ConfigurationHash = context.ConfigurationHash;
            return result;
        }

        /// <summary>
        /// Flushes a buffer of observations to the experiment
        /// </summary>
        private void FlushBuffer(List<Observation> buffer, List<FeatureResult> featureResults)
        {
            for (int i = 0; i < buffer.Count; i++)
            {
                _experiment.OnObservation(buffer[i], featureResults[i]);
            }
        }

        /// <summary>
        /// Creates a config hash for reproducibility
        /// </summary>
        public static string ComputeConfigHash(params string[] components)
        {
            var combined = string.Join("|", components);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}