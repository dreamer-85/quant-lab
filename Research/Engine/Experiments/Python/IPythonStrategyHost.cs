namespace QuantConnect.Research.Engine.Experiments.Python
{
    /// <summary>
    /// Hosts a user-written Python strategy script behind the experiment, isolating pythonnet
    /// interop from experiment logic so tests can substitute a fake host.
    /// All dictionaries crossing this boundary use JSON-friendly CLR values:
    /// double, long, bool, string, List&lt;object&gt;, Dictionary&lt;string, object&gt; (nulls omitted).
    /// </summary>
    public interface IPythonStrategyHost : IDisposable
    {
        /// <summary>
        /// Loads the script and calls its optional initialize(context) hook.
        /// </summary>
        void Initialize(string scriptPath, Dictionary<string, object> context);

        /// <summary>
        /// Calls on_observation(observation, features). Returns the row the script wants appended
        /// to the experiment output, or null when the script returns None or the hook is absent.
        /// </summary>
        Dictionary<string, object> OnObservation(Dictionary<string, object> observation, Dictionary<string, object> features);

        /// <summary>
        /// Calls on_outcome(outcome). No-op when the hook is absent.
        /// </summary>
        void OnOutcome(Dictionary<string, object> outcome);

        /// <summary>
        /// Calls finalize() and collects rows, metrics and metadata from its return value.
        /// </summary>
        StrategyFinalizeResult Finalize();
    }

    /// <summary>
    /// Rows, metrics and metadata collected from a strategy script's finalize() hook.
    /// </summary>
    public sealed class StrategyFinalizeResult
    {
        /// <summary>
        /// Result rows the script wants written to the experiment output
        /// </summary>
        public List<Dictionary<string, object>> Rows { get; } = new();

        /// <summary>
        /// Scalar metrics keyed by name
        /// </summary>
        public Dictionary<string, object> Metrics { get; } = new();

        /// <summary>
        /// Free-form string metadata about the run
        /// </summary>
        public Dictionary<string, string> Metadata { get; } = new();
    }
}