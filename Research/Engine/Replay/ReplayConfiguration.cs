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
        /// The point the observation grid is anchored to, i.e. every grid point is
        /// <c>GridAnchor + n * ObservationInterval</c>. Null means <see cref="StartTime"/>.
        ///
        /// This is deliberately separate from <see cref="StartTime"/> because a resumed or chunked run
        /// restarts partway through the window: its StartTime is the resume boundary (often one tick
        /// past the last written observation), which is not a multiple of the interval away from the
        /// original start. Anchoring the grid on the resume boundary would shift every subsequent grid
        /// point by that remainder, so the tail would no longer line up with the run it is continuing.
        /// A resumed run must pass the original job start here.
        /// </summary>
        public DateTime? GridAnchor { get; set; }

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
        /// Whether grid points that receive no new events are still emitted, carrying the previous
        /// state forward. The analogue of Lean's <c>SecurityCache.CanFillForward</c>.
        ///
        /// When true (default) the observation sequence is gap-free and uniform in wall-clock time,
        /// so a "20 period" rolling window means 20 x <see cref="ObservationInterval"/> of time
        /// whether or not the feed was busy. Each such period is stamped
        /// <see cref="Observations.DataQuality.Filled"/> so a filled period is never mistaken for a
        /// real one.
        ///
        /// When false only periods that actually received events are emitted, which keeps the row
        /// count proportional to the data but makes the observation series irregular: period
        /// boundaries then follow data arrival, and a window covers an unpredictable amount of
        /// time. Backtest and live runs of the same feed still agree, because both use the same
        /// rule; they just no longer sit on a fixed clock.
        /// </summary>
        public bool FillForward { get; set; } = true;

        /// <summary>
        /// Hard ceiling on emitted observations (0 = unlimited).
        ///
        /// Exists because <see cref="FillForward"/> makes the row count a function of the clock
        /// rather than the data: a 100ms interval over a wide window is millions of rows even for a
        /// thin feed. Set this to bound a long window deliberately instead of discovering the cost
        /// afterwards. Replay stops cleanly at the limit.
        /// </summary>
        public long MaxObservations { get; set; } = 0;

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
                (GridAnchor ?? StartTime).ToString("O"),
                EndTime.ToString("O"),
                string.Join(",", Symbols.Select(s => s.Value)),
                string.Join(",", Venues),
                string.Join(",", EventTypes.Select(e => e.ToString())),
                ObservationInterval?.ToString() ?? "null",
                FillForward.ToString(),
                MaxObservations.ToString(),
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

            if (MaxObservations < 0)
                result.Errors.Add("MaxObservations must be non-negative");

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