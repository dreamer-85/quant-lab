using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.LocalData
{
    /// <summary>
    /// Provides market events for a job and symbol.
    /// Implementations must yield deterministic, memory-bounded event streams.
    /// </summary>
    public interface IEventDataSource
    {
        /// <summary>
        /// Gets the event stream for a symbol
        /// </summary>
        IEnumerable<MarketEvent> GetEvents(ResearchJob job, QuantConnect.Symbol symbol);
    }

    /// <summary>
    /// Optional source capability: exposes per-tick-type ordered sub-streams so callers can merge
    /// and replay without materializing the dataset (see <see cref="EventStreamMerger"/>).
    /// Each returned stream must be ordered by <see cref="Events.MarketEvent.CompareEvents"/>.
    /// </summary>
    public interface IStreamingEventSource
    {
        /// <summary>
        /// Gets one ordered sub-stream per data type for a symbol
        /// </summary>
        IEnumerable<IEnumerable<MarketEvent>> GetEventStreams(ResearchJob job, QuantConnect.Symbol symbol);
    }
}