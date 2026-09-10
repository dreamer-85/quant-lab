using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Resolves forward-return labels at a fixed horizon with bounded memory.
    /// Only observations whose horizon target is still in the future are retained, so the pending
    /// buffer is bounded by the number of observations that fit inside one horizon window and never
    /// grows with dataset size.
    /// </summary>
    public sealed class DelayedLabelResolver
    {
        private readonly Queue<Pending> _pending = new();
        private readonly int _maxPending;

        /// <summary>
        /// Horizon this resolver waits before realizing each label
        /// </summary>
        public TimeSpan Horizon { get; }

        /// <summary>
        /// Number of labels resolved so far
        /// </summary>
        public int ResolutionCount { get; private set; }

        /// <summary>
        /// Number of observations currently pending resolution
        /// </summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Number of labels that could not be resolved (horizon extends past the end of the stream)
        /// </summary>
        public int UnresolvedCount { get; private set; }

        private sealed class Pending
        {
            public DateTime ReferenceTimestamp;
            public decimal ReferencePrice;
            public DateTime TargetTimestamp;
        }

        /// <summary>
        /// Creates a resolver. When <paramref name="maxPendingObservations"/> is positive the pending
        /// buffer is hard-capped; overshoot is resolved immediately against the current observation.
        /// </summary>
        public DelayedLabelResolver(TimeSpan horizon, int maxPendingObservations = 0)
        {
            if (horizon <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(horizon), "Horizon must be positive");
            Horizon = horizon;
            _maxPending = Math.Max(0, maxPendingObservations);
        }

        /// <summary>
        /// Advances the resolver with a new observation. Labels whose horizon target has been reached
        /// are resolved (yielded as outcomes) using the current observation's price as the future price;
        /// the current observation is then enqueued for future resolution.
        /// </summary>
        public IEnumerable<OutcomeData> OnObservation(Observation observation)
        {
            var now = observation.Timestamp;

            while (_pending.Count > 0 && _pending.Peek().TargetTimestamp <= now)
            {
                var entry = _pending.Dequeue();
                ResolutionCount++;
                yield return MakeOutcome(entry, observation, now, Horizon);
            }

            _pending.Enqueue(new Pending
            {
                ReferenceTimestamp = now,
                ReferencePrice = LabelPrice(observation),
                TargetTimestamp = now + Horizon
            });

            // Hard safety cap: never allow the pending buffer to exceed the configured maximum.
            while (_maxPending > 0 && _pending.Count > _maxPending)
            {
                var entry = _pending.Dequeue();
                ResolutionCount++;
                yield return MakeOutcome(entry, observation, now, Horizon);
            }
        }

        /// <summary>
        /// Finalizes the stream. Labels whose horizon extends beyond the final observation cannot be
        /// resolved (no future data exists) and are dropped; they are counted in <see cref="UnresolvedCount"/>.
        /// </summary>
        public void Complete()
        {
            UnresolvedCount = _pending.Count;
            _pending.Clear();
        }

        /// <summary>
        /// The label price convention: the mid price of the observation's market state (same as the
        /// mid_price feature). Falls back to 0 when no state is available.
        /// </summary>
        public static decimal LabelPrice(Observation observation)
        {
            return observation.State?.MidPrice ?? 0m;
        }

        private static OutcomeData MakeOutcome(Pending entry, Observation futureObservation, DateTime observedAt, TimeSpan horizon)
        {
            return new OutcomeData
            {
                ReferenceTimestamp = entry.ReferenceTimestamp,
                ReferencePrice = entry.ReferencePrice,
                FuturePrice = LabelPrice(futureObservation),
                OutcomeState = futureObservation.State,
                Horizon = horizon,
                ObservedAt = observedAt
            };
        }
    }
}