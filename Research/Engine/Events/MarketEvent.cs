using QuantConnect.Securities;

namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Base class for all market events in the research engine.
    /// Provides common metadata and ordering guarantees for deterministic replay.
    /// </summary>
    public abstract class MarketEvent
    {
        /// <summary>
        /// Exchange timestamp of the event (when it occurred at the venue)
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// The symbol this event relates to
        /// </summary>
        public Symbol Symbol { get; set; } = Symbol.Empty;

        /// <summary>
        /// Type of market event
        /// </summary>
        public abstract MarketEventType EventType { get; }

        /// <summary>
        /// Asset class for this event
        /// </summary>
        public SecurityType AssetClass { get; set; }

        /// <summary>
        /// Data provenance tracking
        /// </summary>
        public DataProvenance Provenance { get; set; } = new();

        /// <summary>
        /// Sequence number from the exchange (if available)
        /// Used for ordering when timestamps are identical
        /// </summary>
        public long? SequenceNumber { get; set; }

        /// <summary>
        /// Arrival timestamp (when the event was received by the system)
        /// Used for latency analysis
        /// </summary>
        public DateTime? ArrivalTimestamp { get; set; }

        /// <summary>
        /// Event identifier for deduplication
        /// </summary>
        public string EventId { get; set; } = string.Empty;

        /// <summary>
        /// Monotonically increasing ordinal assigned in source order before sorting.
        /// Provides a deterministic final tiebreaker for equal-timestamp events that have
        /// no distinguishing sequence numbers, turning FullSort into a stable sort of the
        /// source order. Leave unset (0) to rely on document semantics.
        /// </summary>
        public long OrderOrdinal { get; set; }

        /// <summary>
        /// Whether this event has been validated
        /// </summary>
        public bool IsValidated { get; set; }

        /// <summary>
        /// Creates a deep clone of this event
        /// </summary>
        public abstract MarketEvent Clone();

        /// <summary>
        /// Gets the effective timestamp for ordering purposes.
        /// Prioritizes exchange timestamp, falls back to arrival timestamp.
        /// </summary>
        public DateTime GetOrderingTimestamp()
        {
            return Timestamp;
        }

        /// <summary>
        /// Compares two events for ordering.
        /// Uses timestamp first, then sequence number if available, then source-ordinal.
        /// Equal-timestamp unsequenced events with no ordinal are considered equal (0);
        /// callers must resolve such ties themselves (stream ordinal in the merger, or the
        /// stable sort guarantee from assigned <see cref="OrderOrdinal"/>).
        /// </summary>
        public static int CompareEvents(MarketEvent a, MarketEvent b)
        {
            if (a == null) return 1;
            if (b == null) return -1;

            var timestampComparison = a.Timestamp.CompareTo(b.Timestamp);
            if (timestampComparison != 0)
                return timestampComparison;

            // If timestamps are equal, use sequence number if available
            if (a.SequenceNumber.HasValue && b.SequenceNumber.HasValue)
            {
                var seqComparison = a.SequenceNumber.Value.CompareTo(b.SequenceNumber.Value);
                if (seqComparison != 0)
                    return seqComparison;
            }

            // Deterministic tiebreaker: source ordinal (stable sort)
            if (a.OrderOrdinal != b.OrderOrdinal)
                return a.OrderOrdinal.CompareTo(b.OrderOrdinal);

            // Undistinguishable: equal key, caller resolves order
            return 0;
        }
    }
}