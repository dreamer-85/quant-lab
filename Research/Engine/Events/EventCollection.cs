using System.Collections;

namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// A collection of market events with ordering guarantees.
    /// Ensures events are sorted by timestamp for deterministic processing.
    /// </summary>
    public class EventCollection : IEnumerable<MarketEvent>
    {
        private readonly List<MarketEvent> _events;
        private bool _isSorted;
        private DateTime? _minTimestamp;
        private DateTime? _maxTimestamp;

        /// <summary>
        /// Number of events in the collection
        /// </summary>
        public int Count => _events.Count;

        /// <summary>
        /// Minimum timestamp in the collection
        /// </summary>
        public DateTime? MinTimestamp => _minTimestamp;

        /// <summary>
        /// Maximum timestamp in the collection
        /// </summary>
        public DateTime? MaxTimestamp => _maxTimestamp;

        /// <summary>
        /// Creates a new empty EventCollection
        /// </summary>
        public EventCollection()
        {
            _events = new List<MarketEvent>();
            _isSorted = true;
        }

        /// <summary>
        /// Creates a new EventCollection from existing events
        /// </summary>
        public EventCollection(IEnumerable<MarketEvent> events)
        {
            _events = new List<MarketEvent>(events);
            _isSorted = false;
            RecalculateBounds();
        }

        /// <summary>
        /// Adds an event to the collection
        /// </summary>
        public void Add(MarketEvent marketEvent)
        {
            if (marketEvent == null)
                throw new ArgumentNullException(nameof(marketEvent));

            _events.Add(marketEvent);
            _isSorted = false;

            // Update bounds
            if (!_minTimestamp.HasValue || marketEvent.Timestamp < _minTimestamp.Value)
                _minTimestamp = marketEvent.Timestamp;
            if (!_maxTimestamp.HasValue || marketEvent.Timestamp > _maxTimestamp.Value)
                _maxTimestamp = marketEvent.Timestamp;
        }

        /// <summary>
        /// Adds multiple events to the collection
        /// </summary>
        public void AddRange(IEnumerable<MarketEvent> events)
        {
            foreach (var evt in events)
            {
                Add(evt);
            }
        }

        /// <summary>
        /// Sorts events by timestamp and sequence number
        /// </summary>
        public void Sort()
        {
            if (!_isSorted)
            {
                _events.Sort(MarketEvent.CompareEvents);
                _isSorted = true;
            }
        }

        /// <summary>
        /// Gets events within a time range
        /// </summary>
        public EventCollection GetEventsInRange(DateTime start, DateTime end)
        {
            Sort();
            return new EventCollection(
                _events.Where(e => e.Timestamp >= start && e.Timestamp <= end));
        }

        /// <summary>
        /// Gets events for a specific symbol
        /// </summary>
        public EventCollection GetEventsForSymbol(QuantConnect.Symbol symbol)
        {
            return new EventCollection(
                _events.Where(e => e.Symbol == symbol));
        }

        /// <summary>
        /// Gets events of a specific type
        /// </summary>
        public EventCollection GetEventsOfType(MarketEventType type)
        {
            return new EventCollection(
                _events.Where(e => e.EventType == type));
        }

        /// <summary>
        /// Removes duplicate events based on EventId
        /// </summary>
        public int RemoveDuplicates()
        {
            var beforeCount = _events.Count;
            var seen = new HashSet<string>();
            _events.RemoveAll(e =>
            {
                if (string.IsNullOrEmpty(e.EventId))
                    return false;
                return !seen.Add(e.EventId);
            });
            return beforeCount - _events.Count;
        }

        /// <summary>
        /// Validates event ordering and identifies issues
        /// </summary>
        public EventValidationResult Validate()
        {
            Sort();
            var result = new EventValidationResult();

            for (int i = 0; i < _events.Count; i++)
            {
                var evt = _events[i];

                // Check for out-of-order events
                if (i > 0)
                {
                    var prev = _events[i - 1];
                    if (evt.Timestamp < prev.Timestamp)
                    {
                        result.OutOfOrderEvents.Add((prev, evt));
                    }
                    else if (evt.Timestamp == prev.Timestamp &&
                             evt.SequenceNumber.HasValue && prev.SequenceNumber.HasValue &&
                             evt.SequenceNumber.Value < prev.SequenceNumber.Value)
                    {
                        result.OutOfOrderEvents.Add((prev, evt));
                    }
                }

                // Check for duplicate timestamps with different sequence numbers
                if (i > 0 && evt.Timestamp == _events[i - 1].Timestamp)
                {
                    result.DuplicateTimestamps.Add(evt.Timestamp);
                }

                // Check for missing EventIds
                if (string.IsNullOrEmpty(evt.EventId))
                {
                    result.MissingEventIds.Add(evt);
                }
            }

            result.DuplicateTimestamps = result.DuplicateTimestamps.Distinct().ToList();
            return result;
        }

        /// <summary>
        /// Recalculates timestamp bounds
        /// </summary>
        private void RecalculateBounds()
        {
            _minTimestamp = _events.Count > 0 ? _events.Min(e => e.Timestamp) : null;
            _maxTimestamp = _events.Count > 0 ? _events.Max(e => e.Timestamp) : null;
        }

        /// <summary>
        /// Returns an enumerator that iterates through the collection
        /// </summary>
        public IEnumerator<MarketEvent> GetEnumerator()
        {
            Sort();
            return _events.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    /// <summary>
    /// Result of event collection validation
    /// </summary>
    public class EventValidationResult
    {
        /// <summary>
        /// Pairs of events that are out of order
        /// </summary>
        public List<(MarketEvent First, MarketEvent Second)> OutOfOrderEvents { get; set; } = new();

        /// <summary>
        /// Timestamps that appear multiple times
        /// </summary>
        public List<DateTime> DuplicateTimestamps { get; set; } = new();

        /// <summary>
        /// Events missing EventIds
        /// </summary>
        public List<MarketEvent> MissingEventIds { get; set; } = new();

        /// <summary>
        /// Whether the collection is valid (no out-of-order events)
        /// </summary>
        public bool IsValid => OutOfOrderEvents.Count == 0;

        /// <summary>
        /// Summary of validation issues
        /// </summary>
        public override string ToString()
        {
            return $"Valid: {IsValid}, OutOfOrder: {OutOfOrderEvents.Count}, " +
                   $"DuplicateTimestamps: {DuplicateTimestamps.Count}, MissingIds: {MissingEventIds.Count}";
        }
    }
}