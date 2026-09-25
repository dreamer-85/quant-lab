using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;

namespace QuantConnect.Research.Engine.Replay
{
    /// <summary>
    /// Deterministic event replay engine.
    /// Given the same input events and configuration, produces identical output.
    /// </summary>
    public class EventReplayEngine
    {
        private readonly ReplayConfiguration _config;
        private readonly IMarketStateReconstructor _stateReconstructor;
        private readonly Dictionary<string, object> _checkpoints;
        private long _eventsProcessed;
        private DateTime? _lastProcessedTimestamp;

        /// <summary>
        /// Number of events processed so far
        /// </summary>
        public long EventsProcessed => _eventsProcessed;

        /// <summary>
        /// Last processed event timestamp
        /// </summary>
        public DateTime? LastProcessedTimestamp => _lastProcessedTimestamp;

        /// <summary>
        /// Final market state after replay completes. Used for state carry-forward in chunked execution.
        /// </summary>
        public MarketState.MarketState FinalState { get; private set; }

        /// <summary>
        /// Creates a new EventReplayEngine
        /// </summary>
        public EventReplayEngine(ReplayConfiguration config, IMarketStateReconstructor stateReconstructor = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _stateReconstructor = stateReconstructor;
            _checkpoints = new Dictionary<string, object>();
            _eventsProcessed = 0;
        }

        /// <summary>
        /// Replays events deterministically, yielding state snapshots at observation intervals
        /// </summary>
        public IEnumerable<ReplayResult> Replay(IEnumerable<MarketEvent> events)
        {
            // Validate configuration
            var validation = _config.Validate();
            if (!validation.IsValid)
                throw new InvalidOperationException($"Invalid configuration: {validation}");

            // Sort events deterministically
            var sortedEvents = PrepareEvents(events);

            // Initialize state: resume from initial state if provided, otherwise create fresh
            MarketState.MarketState currentState = null;
            if (_config.InitialState != null)
            {
                currentState = (MarketState.MarketState)_config.InitialState.Clone();
                _eventsProcessed = _config.InitialEventsProcessed;
            }
            else if (_stateReconstructor != null)
            {
                currentState = _stateReconstructor.CreateInitialState();
            }

            // Track observation intervals. When resuming, the first observation time is
            // supplied explicitly so the grid stays aligned with the original run.
            DateTime? nextObservationTime = _config.ObservationInterval.HasValue
                ? (_config.InitialNextObservationTime ?? _config.StartTime)
                : null;

            var eventsSinceLastObservation = new List<MarketEvent>();

            foreach (var evt in sortedEvents)
            {
                // Check limits
                if (_config.MaxEvents > 0 && _eventsProcessed >= _config.MaxEvents)
                    break;

                // Skip events outside time range
                if (evt.Timestamp < _config.StartTime || evt.Timestamp > _config.EndTime)
                    continue;

                // Update state if reconstructor provided
                if (_stateReconstructor != null && currentState != null)
                {
                    _stateReconstructor.UpdateState(currentState, evt);
                }

                // Track event
                _eventsProcessed++;
                _lastProcessedTimestamp = evt.Timestamp;
                eventsSinceLastObservation.Add(evt);

                // Event-driven mode (no observation grid): emit one observation per event.
                // The data itself advances the engine clock (Lean-style), so nothing is
                // aggregated or collapsed — used by live streams and by replay jobs that
                // want a per-event window instead of interval aggregation.
                if (!_config.ObservationInterval.HasValue)
                {
                    yield return new ReplayResult
                    {
                        Timestamp = evt.Timestamp,
                        State = currentState?.Clone(),
                        Events = new List<MarketEvent> { evt },
                        EventsProcessed = _eventsProcessed
                    };
                    eventsSinceLastObservation.Clear();
                    continue;
                }

                // Check if we should emit an observation
                if (nextObservationTime.HasValue && evt.Timestamp >= nextObservationTime.Value)
                {
                    // Emit observation
                    yield return new ReplayResult
                    {
                        Timestamp = nextObservationTime.Value,
                        State = currentState?.Clone(),
                        Events = new List<MarketEvent>(eventsSinceLastObservation),
                        EventsProcessed = _eventsProcessed
                    };

                    // Reset for next observation
                    eventsSinceLastObservation.Clear();

                    // Calculate next observation time
                    if (_config.ObservationInterval.HasValue)
                    {
                        nextObservationTime = nextObservationTime.Value + _config.ObservationInterval.Value;

                        // Skip ahead if we're behind
                        while (nextObservationTime.HasValue && nextObservationTime.Value < evt.Timestamp)
                        {
                            nextObservationTime += _config.ObservationInterval.Value;
                        }
                    }
                }

                // Create checkpoint periodically
                if (_eventsProcessed % 10000 == 0)
                {
                    CreateCheckpoint();
                }
            }

            // Emit final observation if there are remaining events
            if (eventsSinceLastObservation.Count > 0)
            {
                yield return new ReplayResult
                {
                    Timestamp = _lastProcessedTimestamp ?? _config.EndTime,
                    State = currentState?.Clone(),
                    Events = eventsSinceLastObservation,
                    EventsProcessed = _eventsProcessed
                };
            }

            // Capture final state for chunked/resumed execution.
            // This executes after all yields complete (iterator dispose path).
            FinalState = currentState;
        }

        /// <summary>
        /// Prepares events for deterministic processing.
        /// In streaming mode the filtered events are yielded lazily without materialization;
        /// otherwise the full filtered list is sorted deterministically.
        /// </summary>
        private IEnumerable<MarketEvent> PrepareEvents(IEnumerable<MarketEvent> events)
        {
            if (_config.Reorder == ReorderMode.InOrderStreaming)
            {
                return PrepareEventsStreaming(events);
            }

            return PrepareEventsSorted(events);
        }

        /// <summary>
        /// Lazily filters the ordered source without materializing it.
        /// </summary>
        private IEnumerable<MarketEvent> PrepareEventsStreaming(IEnumerable<MarketEvent> events)
        {
            foreach (var evt in events)
            {
                if (PassesFilter(evt))
                {
                    yield return evt;
                }
            }
        }

        /// <summary>
        /// Filters and deterministically sorts the full event list.
        /// Each event is stamped with a source-order ordinal before sorting so that
        /// equal-timestamp, unsequenced ties are resolved deterministically (stable sort).
        /// </summary>
        private List<MarketEvent> PrepareEventsSorted(IEnumerable<MarketEvent> events)
        {
            var eventList = new List<MarketEvent>();
            long ordinal = 0;

            foreach (var evt in events)
            {
                // Apply filters
                if (!PassesFilter(evt))
                    continue;

                evt.OrderOrdinal = ordinal++;
                eventList.Add(evt);
            }

            // Sort deterministically
            eventList.Sort(MarketEvent.CompareEvents);

            return eventList;
        }

        /// <summary>
        /// Checks if an event passes the configured filters
        /// </summary>
        private bool PassesFilter(MarketEvent evt)
        {
            // Symbol filter
            if (_config.Symbols.Count > 0 && !_config.Symbols.Contains(evt.Symbol))
                return false;

            // Venue filter
            if (_config.Venues.Count > 0 && !_config.Venues.Contains(evt.Provenance.Venue))
                return false;

            // Event type filter. Trade and Bar are treated as equivalent: minute/resolution
            // trade data is surfaced as BarEvent (EventType=Bar) while tick trade data is
            // TradeEvent (EventType=Trade); both come from the TickType.Trade feed.
            if (_config.EventTypes.Count > 0)
            {
                var matches = _config.EventTypes.Contains(evt.EventType);
                if (!matches)
                {
                    var isTradeCategory = evt.EventType == MarketEventType.Trade || evt.EventType == MarketEventType.Bar;
                    if (isTradeCategory
                        && (_config.EventTypes.Contains(MarketEventType.Trade) || _config.EventTypes.Contains(MarketEventType.Bar)))
                    {
                        matches = true;
                    }
                }
                if (!matches)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Creates a checkpoint for restart capability
        /// </summary>
        private void CreateCheckpoint()
        {
            _checkpoints["lastTimestamp"] = _lastProcessedTimestamp;
            _checkpoints["eventsProcessed"] = _eventsProcessed;
            _checkpoints["configHash"] = _config.GetConfigurationHash();
        }

        /// <summary>
        /// Gets checkpoint data for restart
        /// </summary>
        public Dictionary<string, object> GetCheckpoint()
        {
            return new Dictionary<string, object>(_checkpoints);
        }

        /// <summary>
        /// Gets processing statistics
        /// </summary>
        public ReplayStatistics GetStatistics()
        {
            return new ReplayStatistics
            {
                EventsProcessed = _eventsProcessed,
                LastProcessedTimestamp = _lastProcessedTimestamp,
                StartTime = _config.StartTime,
                EndTime = _config.EndTime,
                ConfigurationHash = _config.GetConfigurationHash()
            };
        }
    }

    /// <summary>
    /// Result of a single replay step
    /// </summary>
    public class ReplayResult
    {
        /// <summary>
        /// Timestamp of this observation
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Market state at this observation (if state reconstructor provided)
        /// </summary>
        public IMarketState State { get; set; }

        /// <summary>
        /// Events that occurred since the last observation
        /// </summary>
        public List<MarketEvent> Events { get; set; } = new();

        /// <summary>
        /// Total events processed so far
        /// </summary>
        public long EventsProcessed { get; set; }
    }

    /// <summary>
    /// Statistics about a replay run
    /// </summary>
    public class ReplayStatistics
    {
        public long EventsProcessed { get; set; }
        public DateTime? LastProcessedTimestamp { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string ConfigurationHash { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"Events: {EventsProcessed}, TimeRange: {StartTime:O} to {EndTime:O}, " +
                   $"ConfigHash: {ConfigurationHash[..8]}...";
        }
    }
}