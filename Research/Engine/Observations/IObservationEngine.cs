using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.Observations
{
    /// <summary>
    /// Interface for observation engines that convert event streams to observations
    /// </summary>
    public interface IObservationEngine
    {
        /// <summary>
        /// Generates observations from a stream of events
        /// </summary>
        IEnumerable<Observation> GenerateObservations(IEnumerable<MarketEvent> events);

        /// <summary>
        /// Gets the observation interval
        /// </summary>
        TimeSpan ObservationInterval { get; }
    }
}