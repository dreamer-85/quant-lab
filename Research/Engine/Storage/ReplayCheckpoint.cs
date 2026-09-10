namespace QuantConnect.Research.Engine.Storage
{
    /// <summary>
    /// Snapshot of replay progress for a single symbol.
    /// Persisted so interrupted jobs can be re-run deterministically and
    /// completed symbols reused without recomputation.
    /// </summary>
    public class ReplayCheckpoint
    {
        public string JobId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string ConfigurationHash { get; set; } = string.Empty;
        public DateTime? LastObservationTimestamp { get; set; }
        public long ObservationsWritten { get; set; }
        public long EventsProcessed { get; set; }
        public bool Completed { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAtUtc { get; set; }

        /// <summary>
        /// JSON snapshot of market state at checkpoint time (see <see cref="MarketStateSerialization"/>).
        /// Used to resume interrupted replay from the exact state at the checkpoint boundary.
        /// </summary>
        public string StateJson { get; set; }

        public override string ToString()
        {
            var state = Completed ? $"completed at {CompletedAtUtc:O}" : "in progress";
            return $"{Symbol} [{ConfigurationHash[..8]}...] obs={ObservationsWritten} events={EventsProcessed} {state}";
        }
    }
}