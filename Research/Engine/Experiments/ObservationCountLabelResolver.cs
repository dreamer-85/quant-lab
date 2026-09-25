using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Resolves forward-return labels after a fixed NUMBER of observations, mirroring the bounded,
    /// temporal-causality-correct discipline of <see cref="DelayedLabelResolver"/> (which works in
    /// wall-clock time). An observation enqueued for resolution is resolved on the observation
    /// <see cref="Steps"/> entries later, using that observation's label price as the future price.
    ///
    /// The pending buffer is bounded by the step count, so memory never grows with the stream.
    /// Resolved labels are delivered as <see cref="OutcomeData"/> (Horizon is TimeSpan.Zero for
    /// count-derived outcomes - the step count lives in the consuming experiment's column).
    /// </summary>
    public sealed class ObservationCountLabelResolver
    {
        private readonly Queue<Pending> _pending = new();

        /// <summary>
        /// Number of observations waited before each label resolves.
        /// </summary>
        public int Steps { get; }

        /// <summary>
        /// Number of labels resolved so far (ObservationCount after Steps observations).
        /// </summary>
        public int ResolutionCount { get; private set; }

        /// <summary>
        /// Number of observations currently pending resolution.
        /// </summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Number of labels that could not be resolved (stream ended before Steps observations).
        /// </summary>
        public int UnresolvedCount { get; private set; }

        private sealed class Pending
        {
            public DateTime ReferenceTimestamp;
            public decimal ReferencePrice;
            public int Remaining;
        }

        /// <summary>
        /// Creates a resolver that waits <paramref name="steps"/> observations per label.
        /// </summary>
        public ObservationCountLabelResolver(int steps)
        {
            if (steps < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(steps), "Steps must be at least 1");
            }

            Steps = steps;
        }

        /// <summary>
        /// Advances the resolver with a new observation. Pending labels whose step count elapsed are
        /// resolved using this observation's price; the current observation is then enqueued for
        /// future resolution.
        /// </summary>
        public IEnumerable<OutcomeData> OnObservation(Observation observation)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            var now = observation.Timestamp;

            var resolved = new List<OutcomeData>();
            var remaining = _pending.Count;
            for (var i = 0; i < remaining; i++)
            {
                var entry = _pending.Dequeue();
                entry.Remaining--;
                if (entry.Remaining <= 0)
                {
                    ResolutionCount++;
                    resolved.Add(MakeOutcome(entry, observation, now));
                }
                else
                {
                    _pending.Enqueue(entry);
                }
            }

            _pending.Enqueue(new Pending
            {
                ReferenceTimestamp = now,
                ReferencePrice = DelayedLabelResolver.LabelPrice(observation),
                Remaining = Steps
            });

            return resolved;
        }

        /// <summary>
        /// Finalizes the stream. Labels whose step count extended past the end of the stream are
        /// dropped and counted in <see cref="UnresolvedCount"/>.
        /// </summary>
        public void Complete()
        {
            UnresolvedCount = _pending.Count;
            _pending.Clear();
        }

        private static OutcomeData MakeOutcome(Pending entry, Observation futureObservation, DateTime observedAt)
        {
            return new OutcomeData
            {
                ReferenceTimestamp = entry.ReferenceTimestamp,
                ReferencePrice = entry.ReferencePrice,
                FuturePrice = DelayedLabelResolver.LabelPrice(futureObservation),
                OutcomeState = futureObservation.State,
                Horizon = TimeSpan.Zero,
                ObservedAt = observedAt
            };
        }
    }
}