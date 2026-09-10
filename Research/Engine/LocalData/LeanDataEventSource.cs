using QuantConnect;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.LocalData
{
    /// <summary>
    /// Reads events from the local Lean data directory.
    /// Maps job event types to Lean data tick types and streams files by date.
    /// </summary>
    public class LeanDataEventSource : IEventDataSource, IStreamingEventSource
    {
        private readonly LeanDataEventReader _reader;

        /// <summary>
        /// Creates a new LeanDataEventSource
        /// </summary>
        public LeanDataEventSource(LeanDataEventReader reader)
        {
            _reader = reader ?? new LeanDataEventReader();
        }

        /// <summary>
        /// Gets the event stream for a symbol from local zip files
        /// </summary>
        public IEnumerable<MarketEvent> GetEvents(ResearchJob job, Symbol symbol)
        {
            foreach (var stream in GetEventStreams(job, symbol))
            {
                foreach (var evt in stream)
                {
                    yield return evt;
                }
            }
        }

        /// <summary>
        /// Gets one ordered sub-stream per Lean tick type. Each sub-stream is naturally ordered by
        /// timestamp (per-day files read in ascending order); callers merge them (e.g. with
        /// <see cref="EventStreamMerger"/>) to obtain a single globally ordered stream.
        /// </summary>
        public IEnumerable<IEnumerable<MarketEvent>> GetEventStreams(ResearchJob job, Symbol symbol)
        {
            foreach (var tickType in ResolveTickTypes(job.EventTypes))
            {
                IEnumerable<MarketEvent> batch;
                try
                {
                    batch = _reader.Read(symbol, job.Resolution, tickType, job.StartTime, job.EndTime);
                }
                catch (Exception)
                {
                    continue;
                }

                yield return batch;
            }
        }

        /// <summary>
        /// Maps job event types to Lean tick types
        /// </summary>
        private static List<TickType> ResolveTickTypes(List<MarketEventType> eventTypes)
        {
            if (eventTypes == null || eventTypes.Count == 0)
            {
                return new List<TickType> { TickType.Trade, TickType.Quote };
            }

            var result = new HashSet<TickType>();
            foreach (var eventType in eventTypes)
            {
                switch (eventType)
                {
                    case MarketEventType.Trade:
                    case MarketEventType.Bar:
                        result.Add(TickType.Trade);
                        break;
                    case MarketEventType.Quote:
                    case MarketEventType.OrderBookSnapshot:
                        result.Add(TickType.Quote);
                        break;
                }
            }

            return result.Count == 0 ? new List<TickType> { TickType.Trade } : result.ToList();
        }
    }
}