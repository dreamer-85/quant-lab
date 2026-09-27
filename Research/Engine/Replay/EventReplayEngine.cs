using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Replay
{
    /// <summary>
    /// Deterministic event replay engine.
    /// Given the same input events and configuration, produces identical output.
    ///
    /// Observation scheduling follows Lean's <c>DataTimeSynchronizer</c> model. The replay clock is a
    /// uniform grid over <see cref="ReplayConfiguration.ObservationInterval"/>; each event is assigned
    /// to the first grid point at or after its own timestamp, and grid points that receive no events
    /// are either emitted with the previous state carried forward or skipped, per
    /// <see cref="ReplayConfiguration.FillForward"/>. Two properties follow, and both are what make
    /// a backtest and a live run of the same feed comparable:
    ///
    /// 1. Observation timestamps are strictly increasing and lie on the grid, so an observation is
    ///    never stamped earlier than the newest data it contains.
    /// 2. The grid is a function of the clock and the configuration only, never of where data
    ///    happened to arrive, so the same window produces the same observations in either mode.
    ///
    /// The scheduler is a single forward pass with O(1) state, so it is equally valid for a finite
    /// sorted backtest stream and a never-ending live stream.
    /// </summary>
    public class EventReplayEngine
    {
        private readonly ReplayConfiguration _config;
        private readonly IMarketStateReconstructor _stateReconstructor;
        private readonly Dictionary<string, object> _checkpoints;
        private long _eventsProcessed;
        private long _observationsEmitted;
        private long _filledObservations;
        private long _lateEvents;
        private bool _truncated;
        private DateTime? _lastProcessedTimestamp;
        private DateTime? _lastEventTimestamp;

        /// <summary>
        /// Number of events processed so far
        /// </summary>
        public long EventsProcessed => _eventsProcessed;

        /// <summary>
        /// Number of observations yielded so far.
        /// </summary>
        public long ObservationsEmitted => _observationsEmitted;

        /// <summary>
        /// Number of observations that carried the previous state forward because no event landed on
        /// their grid point. A large share of these means the observation interval is finer than the
        /// data cadence; the <c>freshness</c> validation check reports the ratio.
        /// </summary>
        public long FilledObservations => _filledObservations;

        /// <summary>
        /// Events that arrived after the grid point they belong to had already been emitted. Only
        /// possible on an out-of-order stream; a sorted stream produces zero. Non-zero means the
        /// source violated its ordering contract and one period's data is understated.
        /// </summary>
        public long LateEvents => _lateEvents;

        /// <summary>
        /// Last processed event timestamp
        /// </summary>
        public DateTime? LastProcessedTimestamp => _lastProcessedTimestamp;

        /// <summary>
        /// Timestamp of the first observation emitted, or null if the stream produced none.
        /// </summary>
        public DateTime? FirstObservationTimestamp { get; private set; }

        /// <summary>
        /// Timestamp of the last observation emitted, or null if the stream produced none. Equal to
        /// the last grid point emitted, which is not necessarily the last event.
        /// </summary>
        public DateTime? LastObservationTimestamp { get; private set; }

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
        /// Replays events deterministically, yielding state snapshots on the observation grid.
        ///
        /// The grid starts at <see cref="ReplayConfiguration.InitialNextObservationTime"/> when resuming,
        /// otherwise at <see cref="ReplayConfiguration.StartTime"/>, and advances by exactly
        /// <see cref="ReplayConfiguration.ObservationInterval"/>. An event is attributed to the first
        /// grid point at or after its timestamp, so an observation's timestamp is never earlier than
        /// the newest event it carries.
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
            var hasState = false;
            if (_config.InitialState != null)
            {
                currentState = (MarketState.MarketState)_config.InitialState.Clone();
                _eventsProcessed = _config.InitialEventsProcessed;
                hasState = true;
            }
            else if (_stateReconstructor != null)
            {
                currentState = _stateReconstructor.CreateInitialState();
            }

            // Resuming means the prior chunk already established a frontier, so the observation that
            // follows the resume point is already Fresh relative to carried state.
            if (hasState)
            {
                _lastEventTimestamp = currentState?.Timestamp;
            }

            var interval = _config.ObservationInterval;

            // The next grid point to emit. Null only in event-driven mode.
            DateTime? nextObservationTime = interval.HasValue
                ? (_config.InitialNextObservationTime ?? _config.StartTime)
                : null;

            // A resumed run must not re-emit grid points the previous chunk already wrote.
            var alreadyFilledThrough = hasState && interval.HasValue ? _config.StartTime.AddTicks(-1) : (DateTime?)null;

            // The grid point currently open for accumulation, and the events assigned to it. A bucket
            // stays open until the frontier passes it, which is how several events that belong to the
            // same period end up in one observation instead of being scattered across periods.
            var openBucket = nextObservationTime;
            var openEvents = new List<MarketEvent>();
            var truncated = false;
            var stop = false;

            foreach (var evt in sortedEvents)
            {
                // Check limits
                if (_config.MaxEvents > 0 && _eventsProcessed >= _config.MaxEvents)
                {
                    truncated = true;
                    _truncated = true;
                    break;
                }

                if (AtObservationLimit())
                {
                    Console.WriteLine($"TRACE capbreak at {evt.Timestamp:HH:mm:ss} obs={_observationsEmitted}");
                    truncated = true;
                    _truncated = true;
                    break;
                }

                // Skip events outside time range
                if (evt.Timestamp < _config.StartTime || evt.Timestamp > _config.EndTime)
                    continue;

                // Event-driven mode (no observation grid): emit one observation per event. The data
                // advances the engine clock, so nothing is aggregated or collapsed — used by live
                // streams and by replay jobs that want a per-event window instead of a grid.
                if (!interval.HasValue)
                {
                    ApplyEvent(evt, currentState, ref hasState);
                    _eventsProcessed++;
                    _lastProcessedTimestamp = evt.Timestamp;
                    AdvanceLastEvent(evt.Timestamp);
                    yield return Build(evt.Timestamp, DataQuality.Fresh, new List<MarketEvent> { evt }, currentState);
                    continue;
                }

                // A clock tick says only that time has passed. It closes the periods that have fully
                // elapsed and is then discarded: it is not attributed to a period, not applied to
                // state, and not counted. Its timestamp is floored to the grid rather than ceiled,
                // because the period containing "now" is still open — closing it early would make
                // every event arriving later in that period look late.
                var isClock = evt is ClockTickEvent;
                var bucket = isClock
                    ? FloorToGrid(evt.Timestamp, interval.Value)
                    : CeilToGrid(evt.Timestamp, interval.Value);

                if (isClock)
                {
                    // A tick behind the frontier moves nothing and says nothing about the source.
                    if (!bucket.HasValue || bucket.Value <= openBucket.Value)
                    {
                        continue;
                    }

                    var closed = CloseThrough(bucket.Value, ref openBucket, openEvents, currentState,
                        hasState, interval.Value, out var hitLimit);
                    Console.WriteLine($"TRACE clock tick {evt.Timestamp:HH:mm:ss} open={openBucket:HH:mm:ss} through={bucket:HH:mm:ss} emitted={closed.Count} hit={hitLimit} obs={_observationsEmitted}");
                    foreach (var closedResult in closed)
                    {
                        yield return closedResult;
                    }

                    if (hitLimit)
                    {
                        truncated = true;
                        _truncated = true;
                        break;
                    }

                    continue;
                }

                if (bucket.HasValue && bucket.Value < openBucket.Value)
                {
                    // The period this event belongs to was already closed, so the source broke its
                    // ordering contract. Count it: that period's data is understated, and the
                    // violation is reported rather than silently absorbed.
                    _lateEvents++;
                }
                else if (bucket.HasValue && bucket.Value > openBucket.Value)
                {
                    // The frontier moved past the open period. Close it, then pad or drop the empty
                    // grid points in between so the series stays gap-free and uniform.
                    var closed = CloseThrough(bucket.Value, ref openBucket, openEvents, currentState,
                        hasState, interval.Value, out var hitLimit);
                    foreach (var closedResult in closed)
                    {
                        yield return closedResult;
                    }

                    if (hitLimit || AtObservationLimit())
                    {
                        truncated = true;
                        _truncated = true;
                        stop = true;
                        break;
                    }
                }

                ApplyEvent(evt, currentState, ref hasState);
                _eventsProcessed++;
                _lastProcessedTimestamp = evt.Timestamp;
                AdvanceLastEvent(evt.Timestamp);
                openEvents.Add(evt);

                // Create checkpoint periodically
                if (_eventsProcessed % 10000 == 0)
                {
                    CreateCheckpoint();
                }
            }

            if (stop)
            {
                // The observation cap was reached while padding. The open period is abandoned rather
                // than reported, so the state handed back matches what was actually emitted.
                FinalState = currentState;
                yield break;
            }

            // Close the final open period, then pad the remainder of the window so the series covers
            // the range that was asked for rather than stopping at the last event. Truncation is
            // excluded: a deliberately cut-short run must not invent trailing periods.
            if (interval.HasValue)
            {
                if (openEvents.Count > 0)
                {
                    yield return Build(openBucket.Value, DataQuality.Fresh,
                        new List<MarketEvent>(openEvents), currentState);
                    openEvents.Clear();
                    openBucket += interval.Value;
                }

                if (_config.FillForward && !truncated && openBucket.Value <= _config.EndTime
                    && !(alreadyFilledThrough.HasValue && openBucket.Value <= alreadyFilledThrough.Value))
                {
                    while (openBucket.Value <= _config.EndTime && !AtObservationLimit())
                    {
                        yield return Build(openBucket.Value,
                            hasState ? DataQuality.Filled : DataQuality.Missing,
                            new List<MarketEvent>(),
                            currentState);
                        openBucket += interval.Value;
                    }
                }
            }

            // Capture final state for chunked/resumed execution.
            // This executes after all yields complete (iterator dispose path).
            FinalState = currentState;
        }

        /// <summary>
        /// The observation ceiling, treating an unset <see cref="ReplayConfiguration.MaxObservations"/>
        /// as unlimited.
        /// </summary>
        private long MaxEmitted()
        {
            return _config.MaxObservations > 0 ? _config.MaxObservations : long.MaxValue;
        }

        private bool AtObservationLimit()
        {
            return _observationsEmitted >= MaxEmitted();
        }

        /// <summary>
        /// The point the observation grid is anchored to.
        ///
        /// An explicit <see cref="ReplayConfiguration.GridAnchor"/> wins. Otherwise a resumed run
        /// anchors on its own <see cref="ReplayConfiguration.InitialNextObservationTime"/>: since grid
        /// points are <c>anchor + n * interval</c>, that reproduces exactly the grid the original run
        /// was on, without the caller having to restate the original start. Only when neither is set
        /// does the grid start at <see cref="ReplayConfiguration.StartTime"/>.
        /// </summary>
        private DateTime GridOrigin => _config.GridAnchor
            ?? _config.InitialNextObservationTime
            ?? _config.StartTime;

        /// <summary>
        /// The first grid point at or after <paramref name="timestamp"/>, on the grid anchored at
        /// <see cref="GridOrigin"/>. Null when the timestamp precedes the first
        /// grid point (only reachable on a resumed run, where earlier grid points are already written).
        /// </summary>
        private DateTime? CeilToGrid(DateTime timestamp, TimeSpan interval)
        {
            var origin = GridOrigin;
            if (timestamp <= origin)
            {
                return origin;
            }

            var elapsed = timestamp - origin;
            var steps = (long)Math.Ceiling(elapsed.Ticks / (double)interval.Ticks);
            return origin + TimeSpan.FromTicks(steps * interval.Ticks);
        }

        /// <summary>
        /// The start of the period containing <paramref name="timestamp"/>: the last grid point at or
        /// before it. The counterpart of <see cref="CeilToGrid"/>, used by clock ticks to find the
        /// period still in progress rather than the next one to open.
        /// </summary>
        private DateTime? FloorToGrid(DateTime timestamp, TimeSpan interval)
        {
            var origin = GridOrigin;
            if (timestamp < origin)
            {
                return origin;
            }

            var elapsed = timestamp - origin;
            var steps = (long)Math.Floor(elapsed.Ticks / (double)interval.Ticks);
            return origin + TimeSpan.FromTicks(steps * interval.Ticks);
        }

        /// <summary>
        /// Closes the open period and every grid point before <paramref name="through"/>, emitting
        /// padding only when fill-forward is on.
        ///
        /// Shared by the event and clock paths on purpose. "Which periods are complete" is the
        /// subtlest rule in the engine, and two copies of it drift apart the first time one of them
        /// is edited.
        /// </summary>
        private List<ReplayResult> CloseThrough(DateTime? through, ref DateTime? openBucket,
            List<MarketEvent> openEvents, MarketState.MarketState state, bool hasState,
            TimeSpan interval, out bool hitLimit)
        {
            var emitted = new List<ReplayResult>();
            hitLimit = false;

            if (openEvents.Count > 0 || _config.FillForward)
            {
                emitted.Add(Build(openBucket.GetValueOrDefault(),
                    openEvents.Count > 0 ? DataQuality.Fresh : (hasState ? DataQuality.Filled : DataQuality.Missing),
                    new List<MarketEvent>(openEvents), state));
                openEvents.Clear();
            }

            openBucket += interval;

            while (openBucket < through && !AtObservationLimit())
            {
                if (_config.FillForward)
                {
                    emitted.Add(Build(openBucket.GetValueOrDefault(),
                        hasState ? DataQuality.Filled : DataQuality.Missing,
                        new List<MarketEvent>(), state));
                }

                openBucket += interval;
            }

            // A leftover gap means the cap cut the run short, which the caller reports as truncation
            // rather than silently presenting a short series as a complete one.
            hitLimit = openBucket < through;
            return emitted;
        }

        /// <summary>
        /// Builds a replay result, stamping the data quality and the newest event it reflects.
        /// Every observation the engine yields is counted here, so the totals and the
        /// <see cref="ReplayConfiguration.MaxObservations"/> ceiling cannot drift apart.
        /// </summary>
        private ReplayResult Build(DateTime timestamp, DataQuality quality, List<MarketEvent> events,
            MarketState.MarketState state)
        {
            _observationsEmitted++;
            if (quality == DataQuality.Filled)
            {
                _filledObservations++;
            }

            FirstObservationTimestamp ??= timestamp;
            LastObservationTimestamp = timestamp;

            return new ReplayResult
            {
                Timestamp = timestamp,
                State = state?.Clone(),
                Events = events,
                LastEventTimestamp = _lastEventTimestamp,
                Quality = quality,
                EventsProcessed = _eventsProcessed
            };
        }

        /// <summary>
        /// Folds an event into the reconstructed state.
        /// </summary>
        private void ApplyEvent(MarketEvent evt, MarketState.MarketState state, ref bool hasState)
        {
            if (_stateReconstructor == null || state == null)
            {
                return;
            }

            _stateReconstructor.UpdateState(state, evt);
            hasState = true;
        }

        /// <summary>
        /// Advances the newest-observed-event watermark. Monotonic: a late event on an out-of-order
        /// stream must not make the watermark go backwards.
        /// </summary>
        private void AdvanceLastEvent(DateTime timestamp)
        {
            if (!_lastEventTimestamp.HasValue || timestamp > _lastEventTimestamp.Value)
            {
                _lastEventTimestamp = timestamp;
            }
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
            _checkpoints["observationsEmitted"] = _observationsEmitted;
            _checkpoints["filledObservations"] = _filledObservations;
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
                ObservationsEmitted = _observationsEmitted,
                FilledObservations = _filledObservations,
                LateEvents = _lateEvents,
                Truncated = _truncated,
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
        /// Whether this grid point received new data or carried the previous state forward.
        /// </summary>
        public Observations.DataQuality Quality { get; set; } = Observations.DataQuality.Fresh;

        /// <summary>
        /// Timestamp of the newest event incorporated as of this observation. Never later than
        /// <see cref="Timestamp"/>.
        /// </summary>
        public DateTime? LastEventTimestamp { get; set; }

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
        public long ObservationsEmitted { get; set; }
        public long FilledObservations { get; set; }
        public long LateEvents { get; set; }

        /// <summary>
        /// True when replay stopped because it hit a limit (the observation cap, the event cap, or
        /// an explicit end) rather than because the data ran out. A truncated run is a valid prefix,
        /// not a complete window, and callers must not treat its end as the end of the data.
        /// </summary>
        public bool Truncated { get; set; }
        public DateTime? LastProcessedTimestamp { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string ConfigurationHash { get; set; } = string.Empty;

        /// <summary>
        /// Share of observations that carried the previous state forward because no event landed on
        /// their grid point. Zero when replay runs event-driven, or when data is denser than the
        /// observation interval.
        /// </summary>
        public double FilledRatio => ObservationsEmitted > 0
            ? (double)FilledObservations / ObservationsEmitted
            : 0d;

        public override string ToString()
        {
            return $"Events: {EventsProcessed}, Observations: {ObservationsEmitted} " +
                   $"(filled {FilledObservations}, {FilledRatio:P1}), TimeRange: {StartTime:O} to {EndTime:O}, " +
                   $"ConfigHash: {ConfigurationHash[..8]}...";
        }
    }
}