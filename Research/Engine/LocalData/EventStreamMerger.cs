using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.LocalData
{
    /// <summary>
    /// Deterministically merges multiple time-ordered event streams into a single globally ordered
    /// stream without materializing. Each input stream must already be ordered by
    /// <see cref="MarketEvent.CompareEvents"/>. Equal-key ties (same timestamp and no distinguishing
    /// sequence numbers) are resolved by input stream ordinal so output is fully deterministic.
    /// Memory is bounded by the number of active streams, not the dataset size.
    /// </summary>
    public static class EventStreamMerger
    {
        /// <summary>
        /// Merges ordered event streams into one globally ordered lazy stream.
        /// </summary>
        /// <param name="streams">Ordered event sub-streams, e.g. one per Lean tick type.</param>
        public static IEnumerable<MarketEvent> Merge(IEnumerable<IEnumerable<MarketEvent>> streams)
        {
            var enumerators = streams
                .Select(s => s?.GetEnumerator())
                .Where(e => e != null)
                .ToList();

            try
            {
                var active = new List<(int Ordinal, IEnumerator<MarketEvent> Enumerator)>();
                for (var i = 0; i < enumerators.Count; i++)
                {
                    if (enumerators[i].MoveNext())
                    {
                        active.Add((i, enumerators[i]));
                    }
                }

                while (active.Count > 0)
                {
                    var best = 0;
                    for (var i = 1; i < active.Count; i++)
                    {
                        var cmp = MarketEvent.CompareEvents(active[i].Enumerator.Current, active[best].Enumerator.Current);
                        if (cmp < 0 || (cmp == 0 && active[i].Ordinal < active[best].Ordinal))
                        {
                            best = i;
                        }
                    }

                    var (_, enumerator) = active[best];
                    yield return enumerator.Current;

                    if (!enumerator.MoveNext())
                    {
                        active.RemoveAt(best);
                    }
                }
            }
            finally
            {
                foreach (var e in enumerators)
                {
                    e.Dispose();
                }
            }
        }
    }
}