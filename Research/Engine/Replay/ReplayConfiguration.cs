namespace QuantConnect.Research.Engine.Replay
{
    /// <summary>
    /// Configuration for deterministic event replay.
    /// Same configuration + same input = same output.
    /// </summary>
    public class ReplayConfiguration
    {
        /// <summary>
        /// Start time for replay
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// End time for replay
        /// </summary>
        public DateTime EndTime { get; set; }

        /// <summary>
        /// Symbols to include in replay (empty means all)
        /// </summary>
        public List<QuantConnect.Symbol> Symbols { get; set; } = new();

        /// <summary>
        /// Venue/source filter (empty means all)
        /// </summary>
        public List<string> Venues { get; set; } = new();

        /// <summary>
        /// Event types to include (empty means all)
        /// </summary>
        public List<Events.MarketEventType> EventTypes { get; set; } = new();

        /// <summary>
        /// Observation frequency (e.g., 100ms, 1s, 1m)
        /// Null means process all events without interval aggregation
        /// </summary>
        public TimeSpan? ObservationInterval { get; set; }

        /// <summary>
        /// Whether to preserve original event ordering
        /// </summary>
        public bool PreserveOriginalOrdering { get; set; } = true;

        /// <summary>
        /// Ordering strategy used during replay. Affects determinism guarantees and memory profile.
        /// </summary>
        public ReorderMode Reorder { get; set; } = ReorderMode.FullSort;

        /// <summary>
        /// Maximum number of events to process (0 = unlimited)
        /// </summary>
        public long MaxEvents { get; set; } = 0;

        /// <summary>
        /// Random seed for deterministic processing (if any randomization is used)
        /// </summary>
        public int? RandomSeed { get; set; }

        /// <summary>
        /// Engine version for reproducibility tracking
        /// </summary>
        public string EngineVersion { get; set; } = "1.0.0";

        /// <summary>
        /// Initial market state for resumed/chunked execution.
        /// When set, the replay engine uses this as the starting state instead of creating fresh state.
        /// Not included in the configuration hash (runtime state, not research semantics).
        /// </summary>
        public MarketState.MarketState InitialState { get; set; }

        /// <summary>
        /// Starting counter for EventsProcessed when resuming from a checkpoint.
        /// Not included in the configuration hash (runtime state, not research semantics).
        /// </summary>
        public long InitialEventsProcessed { get; set; }

        /// <summary>
        /// First observation time for resumed execution. When set, the engine starts emitting
        /// observations at this (grid-aligned) time instead of at <see cref="StartTime"/>, so the
        /// resumed run's observation grid stays aligned with the original uninterrupted run.
        /// Not included in the configuration hash (runtime state, not research semantics).
        /// </summary>
        public DateTime? InitialNextObservationTime { get; set; }

        /// <summary>
        /// Creates a hash of this configuration for reproducibility.
        /// InitialState, InitialEventsProcessed and InitialNextObservationTime are excluded
        /// (runtime state, not research semantics).
        /// </summary>
        public string GetConfigurationHash()
        {
            var components = new[]
            {
                StartTime.ToString("O"),
                EndTime.ToString("O"),
                string.Join(",", Symbols.Select(s => s.Value)),
                string.Join(",", Venues),
                string.Join(",", EventTypes.Select(e => e.ToString())),
                ObservationInterval?.ToString() ?? "null",
                PreserveOriginalOrdering.ToString(),
                Reorder.ToString(),
                MaxEvents.ToString(),
                RandomSeed?.ToString() ?? "null",
                EngineVersion
            };

            var combined = string.Join("|", components);
            return ComputeHash(combined);
        }

        /// <summary>
        /// Computes a deterministic hash string
        /// </summary>
        private static string ComputeHash(string input)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Validates the configuration
        /// </summary>
        public ValidationResult Validate()
        {
            var result = new ValidationResult();

            if (StartTime >= EndTime)
                result.Errors.Add("StartTime must be before EndTime");

            if (ObservationInterval.HasValue && ObservationInterval.Value <= TimeSpan.Zero)
                result.Errors.Add("ObservationInterval must be positive");

            if (MaxEvents < 0)
                result.Errors.Add("MaxEvents must be non-negative");

            return result;
        }
    }

    /// <summary>
    /// Determines how the replay engine orders events before processing.
    /// </summary>
    public enum ReorderMode
    {
        /// <summary>
        /// Sort the full filtered event list before replaying (deterministic for any input order;
        /// materializes the dataset in memory).
        /// </summary>
        FullSort = 0,

        /// <summary>
        /// Stream events online assuming every input sub-stream is already ordered by
        /// <see cref="Events.MarketEvent.CompareEvents"/>. Sub-streams are merged incrementally so
        /// memory stays bounded by the number of streams, not the dataset size. Equal-key ties are
        /// resolved by input stream ordinal for determinism.
        /// </summary>
        InOrderStreaming = 1
    }

    /// <summary>
    /// Result of configuration validation
    /// </summary>
    public class ValidationResult
    {
        public List<string> Errors { get; set; } = new();
        public bool IsValid => Errors.Count == 0;

        public override string ToString()
        {
            return IsValid ? "Valid" : $"Invalid: {string.Join(", ", Errors)}";
        }
    }
}