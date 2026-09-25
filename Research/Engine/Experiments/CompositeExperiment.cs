using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Runs several experiments against the same observation/outcome stream. The executor builds a
    /// single replay and a single shared <see cref="FeatureEngine"/>, then fans every observation
    /// out to each child experiment — so market state is reconstructed once, not per experiment
    /// (see docs/research-layer.md, "multiple experiments over the same replay").
    ///
    /// Result rows are annotated with their originating experiment via the "experiment" column and
    /// metrics are namespaced as "experimentName.metricName" so outputs stay unambiguous.
    /// </summary>
    public sealed class CompositeExperiment : IExperiment
    {
        private readonly List<IExperiment> _children;

        /// <summary>
        /// Creates a composite over the given experiments.
        /// </summary>
        public CompositeExperiment(IEnumerable<IExperiment> experiments)
        {
            _children = experiments?.ToList() ?? new List<IExperiment>();
        }

        /// <summary>
        /// Children experiment names (used for metrics/row namespacing).
        /// </summary>
        public IReadOnlyList<IExperiment> Children => _children;

        public string Name => "composite";

        public string Description => $"Runs {_children.Count} experiments over one replay: {string.Join(", ", _children.Select(c => c.Name))}";

        public string Version => "1.0.0";

        public List<string> RequiredFeatures => _children
            .SelectMany(c => c.RequiredFeatures ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        public void Initialize(ExperimentContext context)
        {
            foreach (var child in _children)
            {
                child.Initialize(context);
            }
        }

        public void OnObservation(Observation observation, FeatureResult features)
        {
            foreach (var child in _children)
            {
                child.OnObservation(observation, features);
            }
        }

        public void OnOutcome(OutcomeData outcome)
        {
            foreach (var child in _children)
            {
                child.OnOutcome(outcome);
            }
        }

        public ExperimentResult Finalize()
        {
            var results = _children.Select(c => c.Finalize()).ToList();
            var result = new ExperimentResult
            {
                ExperimentName = Name,
                ExperimentVersion = Version,
                IsSuccess = results.All(r => r.IsSuccess),
                ConfigurationHash = results.Select(r => r.ConfigurationHash).FirstOrDefault(h => !string.IsNullOrEmpty(h)) ?? string.Empty
            };

            foreach (var pair in _children.Zip(results, (c, r) => (Experiment: c, Result: r)))
            {
                foreach (var metric in pair.Result.Metrics)
                {
                    result.AddMetric($"{pair.Experiment.Name}.{metric.Key}", metric.Value);
                }

                foreach (var row in pair.Result.Rows)
                {
                    var prefixed = new Dictionary<string, object>(row) { ["experiment"] = pair.Experiment.Name };
                    result.AddRow(prefixed);
                }
            }

            result.Metadata["experiments"] = string.Join(",", _children.Select(c => c.Name));
            return result;
        }

        public void Reset()
        {
            foreach (var child in _children)
            {
                child.Reset();
            }
        }
    }
}