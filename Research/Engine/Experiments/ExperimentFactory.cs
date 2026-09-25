using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Experiments.Python;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Creates the experiment instance(s) declared by a research job. experimentName is either a
    /// single experiment or a comma-separated list, in which case a
    /// <see cref="CompositeExperiment"/> runs all of them over one replay.
    ///
    /// For a single unknown name the result is null (the experiment path is disabled and only
    /// observation output is produced, preserving legacy behavior); an unknown name inside a
    /// multi-experiment list is rejected because composing an experiment that does not exist
    /// would silently drop it otherwise.
    /// </summary>
    public static class ExperimentFactory
    {
        public static IExperiment Create(ResearchJob job)
        {
            var raw = job?.ExperimentName?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            var names = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.ToLowerInvariant())
                .Where(n => !string.Equals(n, "dry-run", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (names.Count == 0)
            {
                return null;
            }

            var experiments = names.Select(name => CreateSingle(name, job)).ToList();

            // Single-name jobs keep the legacy contract: unknown names disable the experiment.
            if (names.Count == 1)
            {
                return experiments[0];
            }

            if (experiments.Any(e => e == null))
            {
                var missing = names.Zip(experiments, (n, e) => e == null ? n : null).First(n => n != null);
                throw new InvalidOperationException($"Unknown experiment '{missing}' in experimentName list '{job.ExperimentName}'. Known experiments: liquidity_trend, hypothesis, python_strategy.");
            }

            return new CompositeExperiment(experiments);
        }

        private static IExperiment CreateSingle(string name, ResearchJob job)
        {
            return name switch
            {
                "liquidity_trend" => new LiquidityTrendExperiment(),
                "python_strategy" => new PythonStrategyExperiment(job),
                "hypothesis" => new HypothesisExperiment(),
                _ => null
            };
        }
    }
}