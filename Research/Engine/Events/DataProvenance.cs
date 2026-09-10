using QuantConnect.Securities;

namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Tracks the provenance/origin of market data for research integrity
    /// </summary>
    public class DataProvenance
    {
        /// <summary>
        /// Data provider name (e.g., "Binance", "Interactive Brokers", "Polygon")
        /// </summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary>
        /// Venue or source identifier (e.g., "binance", "NYSE", "CME")
        /// </summary>
        public string Venue { get; set; } = string.Empty;

        /// <summary>
        /// The symbol being traded
        /// </summary>
        public Symbol Symbol { get; set; } = Symbol.Empty;

        /// <summary>
        /// Asset class classification
        /// </summary>
        public SecurityType AssetClass { get; set; }

        /// <summary>
        /// Timestamp precision of the data source
        /// </summary>
        public TimestampPrecision TimestampPrecision { get; set; } = TimestampPrecision.Milliseconds;

        /// <summary>
        /// Type of data feed (trade, quote, orderbook, etc.)
        /// </summary>
        public string FeedType { get; set; } = string.Empty;

        /// <summary>
        /// Data version or dataset identifier
        /// </summary>
        public string DatasetVersion { get; set; } = string.Empty;

        /// <summary>
        /// Additional metadata as key-value pairs
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Precision of timestamps in the data
    /// </summary>
    public enum TimestampPrecision
    {
        /// <summary>
        /// Timestamps in days
        /// </summary>
        Days,

        /// <summary>
        /// Timestamps in hours
        /// </summary>
        Hours,

        /// <summary>
        /// Timestamps in minutes
        /// </summary>
        Minutes,

        /// <summary>
        /// Timestamps in seconds
        /// </summary>
        Seconds,

        /// <summary>
        /// Timestamps in milliseconds
        /// </summary>
        Milliseconds,

        /// <summary>
        /// Timestamps in microseconds
        /// </summary>
        Microseconds,

        /// <summary>
        /// Timestamps in nanoseconds
        /// </summary>
        Nanoseconds
    }
}