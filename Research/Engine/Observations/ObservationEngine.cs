using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;

namespace QuantConnect.Research.Engine.Observations
{
    /// <summary>
    /// Generates interval-based observations from event streams.
    /// Converts continuous event flow to periodic snapshots while preserving event data.
    /// </summary>
    public class ObservationEngine : IObservationEngine
    {
        private readonly IMarketStateReconstructor _stateReconstructor;
        private readonly TimeSpan _interval;
        private readonly bool _includeState;
        private readonly bool _includeEvents;

        /// <summary>
        /// Gets the observation interval
        /// </summary>
        public TimeSpan ObservationInterval => _interval;

        /// <summary>
        /// Creates a new ObservationEngine
        /// </summary>
        /// <param name="interval">Observation interval (e.g., 100ms, 1s, 1m)</param>
        /// <param name="stateReconstructor">Market state reconstructor</param>
        /// <param name="includeState">Whether to include reconstructed state in observations</param>
        /// <param name="includeEvents">Whether to include raw events in observations</param>
        public ObservationEngine(
            TimeSpan interval,
            IMarketStateReconstructor stateReconstructor = null,
            bool includeState = true,
            bool includeEvents = true)
        {
            _interval = interval;
            _stateReconstructor = stateReconstructor;
            _includeState = includeState;
            _includeEvents = includeEvents;
        }

        /// <summary>
        /// Generates observations from a stream of sorted events
        /// </summary>
        public IEnumerable<Observation> GenerateObservations(IEnumerable<MarketEvent> events)
        {
            if (events == null)
                yield break;

            // Initialize state if reconstructor provided
            MarketState.MarketState currentState = null;
            if (_includeState && _stateReconstructor != null)
            {
                currentState = _stateReconstructor.CreateInitialState();
            }

            // Sort events deterministically
            var sortedEvents = events
                .OrderBy(e => e.Timestamp)
                .ToList();

            if (sortedEvents.Count == 0)
                yield break;

            // Determine observation boundaries
            var firstTimestamp = sortedEvents[0].Timestamp;
            var lastTimestamp = sortedEvents[sortedEvents.Count - 1].Timestamp;

            var currentObservationStart = AlignToInterval(firstTimestamp, _interval);
            var pendingEvents = new List<MarketEvent>();

            foreach (var evt in sortedEvents)
            {
                // Update continuous state
                if (currentState != null)
                    currentState.UpdateFromEvent(evt);

                // Check if this event belongs in current observation window
                var eventBucket = AlignToInterval(evt.Timestamp, _interval);

                if (eventBucket > currentObservationStart)
                {
                    // Close current observation
                    var observation = CreateObservation(
                        currentObservationStart,
                        pendingEvents,
                        currentState);

                    if (observation != null)
                        yield return observation;

                    // Fill gaps if observations are missing
                    while (currentObservationStart < eventBucket)
                    {
                        currentObservationStart += _interval;
                    }

                    pendingEvents = new List<MarketEvent>();
                }

                pendingEvents.Add(evt);
            }

            // Emit final observation
            if (pendingEvents.Count > 0)
            {
                var finalObservation = CreateObservation(
                    currentObservationStart,
                    pendingEvents,
                    currentState);

                if (finalObservation != null)
                    yield return finalObservation;
            }
        }

        /// <summary>
        /// Creates an observation from an event batch and state
        /// </summary>
        private Observation CreateObservation(
            DateTime timestamp,
            List<MarketEvent> events,
            MarketState.MarketState state)
        {
            if (events.Count == 0)
                return null;

            return new Observation
            {
                Timestamp = timestamp,
                State = _includeState ? state?.CloneTyped() : null,
                Events = _includeEvents ? new List<MarketEvent>(events) : new List<MarketEvent>()
            };
        }

        /// <summary>
        /// Aligns a timestamp to an interval boundary
        /// </summary>
        private static DateTime AlignToInterval(DateTime timestamp, TimeSpan interval)
        {
            var ticks = timestamp.Ticks;
            var intervalTicks = interval.Ticks;
            var aligned = (ticks / intervalTicks) * intervalTicks;
            return new DateTime(aligned, DateTimeKind.Unspecified);
        }
    }
}