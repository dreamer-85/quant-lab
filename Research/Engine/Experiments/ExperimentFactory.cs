using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Creates the experiment instance declared by a research job.
    /// Unregistered or empty names produce null, which disables the experiment path and preserves
    /// the legacy behavior (observation output only).
    /// </summary>
    public static class ExperimentFactory
    {
        public static IExperiment Create(ResearchJob job)
        {
            var name = job?.ExperimentName?.Trim();
            if (string.IsNullOrEmpty(name) || string.Equals(name, "dry-run", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name.ToLowerInvariant() switch
            {
                "liquidity_trend" => new LiquidityTrendExperiment(),
                _ => null
            };
        }
    }
}