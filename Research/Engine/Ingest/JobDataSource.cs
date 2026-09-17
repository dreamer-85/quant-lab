using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Ingest
{
    /// <summary>
    /// Configures how a job obtains market data.
    /// Modes:
    ///   - "file"       (default) reads the pre-staged Lean zip layout (existing behavior).
    ///   - "historical" pulls a data range from an exchange REST API and replays it directly.
    ///   - "live"       subscribes to an exchange WebSocket feed and streams events as they arrive.
    ///   - "archive"    replays a recorded WebSocket capture (JSON-lines archive) through the same
    ///                  live-normalization path, reproducing the capture event-for-event.
    /// The engine consumes the normalized <see cref="Events.MarketEvent"/> stream produced by the
    /// <see cref="ExchangeDataAdapter"/>/<see cref="Bybit.BybitArchiveSource"/>, so all modes share
    /// one replay pipeline.
    /// </summary>
    public class JobDataSource
    {
        /// <summary>
        /// Mode: "file", "historical", "live", or "archive". Defaults to "file".
        /// </summary>
        public string Mode { get; set; } = "file";

        /// <summary>
        /// Exchange provider identifier (currently "bybit").
        /// </summary>
        public string Provider { get; set; } = "bybit";

        /// <summary>
        /// Exchange market category (e.g. "spot" or "linear"). Empty selects the provider default.
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Order book depth to request. "1" produces top-of-book quotes; deeper values
        /// ("10"/"50") produce L2 order book snapshots.
        /// </summary>
        public string OrderBookDepth { get; set; } = "1";

        /// <summary>
        /// Maximum page size for REST pagination (provider clamps to its own limits).
        /// </summary>
        public int PageSize { get; set; } = 1000;

        /// <summary>
        /// How long a live stream runs before yielding its last event. 0 disables the wall-clock
        /// cutoff; the stream then runs until cancelled or the job's EndTime is reached.
        /// </summary>
        public int LiveDurationSeconds { get; set; } = 0;

        /// <summary>
        /// Optional override of the REST base endpoint (used by tests and private deployments).
        /// </summary>
        public string RestEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// Optional override of the WebSocket endpoint (used by tests).
        /// </summary>
        public string WsEndpoint { get; set; } = string.Empty;

        /// <summary>
        /// Path to a recorded WebSocket capture archive (JSON-lines). Used by "archive" mode to
        /// replay a live capture deterministically; also consumed by "live" mode as the destination
        /// where the capture is written as it streams.
        /// </summary>
        public string ArchiveFilePath { get; set; } = string.Empty;

        /// <summary>
        /// True when this source is an exchange-backed adapter or archive replay rather than the
        /// Lean zip store.
        /// </summary>
        public bool IsExchangeSource()
        {
            return Mode != null
                && (Mode.Equals("historical", StringComparison.OrdinalIgnoreCase)
                    || Mode.Equals("live", StringComparison.OrdinalIgnoreCase)
                    || Mode.Equals("archive", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Validates the source against the referencing job.
        /// </summary>
        public bool Validate(out string error)
        {
            error = string.Empty;
            if (!IsExchangeSource())
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(Provider))
            {
                error = "Source provider is required for historical/live/archive modes";
                return false;
            }

            if (Mode.Equals("live", StringComparison.OrdinalIgnoreCase) && LiveDurationSeconds < 0)
            {
                error = "LiveDurationSeconds must be non-negative";
                return false;
            }

            if (Mode.Equals("archive", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(ArchiveFilePath))
            {
                error = "ArchiveFilePath is required for archive mode";
                return false;
            }

            if (PageSize <= 0 || PageSize > 1000)
            {
                error = "PageSize must be between 1 and 1000";
                return false;
            }

            return true;
        }
    }
}