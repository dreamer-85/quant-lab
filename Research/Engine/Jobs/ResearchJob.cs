using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Ingest;

namespace QuantConnect.Research.Engine.Jobs
{
    /// <summary>
    /// Defines a complete research job.
    /// A job bundles dataset, replay, observation, feature, and experiment configuration.
    /// The same job is executable locally or in the cloud.
    /// </summary>
    public class ResearchJob
    {
        public string JobId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Dataset { get; set; } = string.Empty;
        public List<string> Symbols { get; set; } = new();
        public string AssetClass { get; set; } = "crypto";
        public string Venue { get; set; } = string.Empty;
        public QuantConnect.Resolution Resolution { get; set; } = QuantConnect.Resolution.Minute;
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<MarketEventType> EventTypes { get; set; } = new();
        /// <summary>
        /// Observation cadence on the event stream. Null selects event-driven mode: one
        /// observation is emitted per event (the data advances the engine clock, nothing is
        /// aggregated). A positive value emits observations on a time grid over the events.
        /// </summary>
        public TimeSpan? ObservationInterval { get; set; } = TimeSpan.FromMilliseconds(100);
        public List<string> Features { get; set; } = new();
        public string ExperimentName { get; set; } = string.Empty;
        public Dictionary<string, string> ExperimentConfig { get; set; } = new();
        public List<string> Horizons { get; set; } = new();
        public string OutputLocation { get; set; } = string.Empty;
        public string OutputFormat { get; set; } = "parquet";

        /// <summary>
        /// Path to a Python strategy script (.py) executed by the "python_strategy" experiment.
        /// The script defines a class named <c>Strategy</c> with optional hooks
        /// <c>initialize(context)</c>, <c>on_observation(observation, features)</c>,
        /// <c>on_outcome(outcome)</c> and <c>finalize()</c>. See docs/python-strategies.md.
        /// </summary>
        public string StrategyScript { get; set; } = string.Empty;

        /// <summary>
        /// Explicit raw observation field names to append as columns on every observation output row.
        /// Unlike features (which produce decimal values via the feature engine), raw fields read
        /// directly from the observation state and can be of mixed types (decimal, long, DateTime, string).
        /// </summary>
        public List<string> RawFields { get; set; } = new();
        public string EngineVersion { get; set; } = "1.0.0";
        public long MaxEvents { get; set; } = 0;
        public bool EnableCheckpointing { get; set; } = true;
        public string CheckpointDirectory { get; set; } = string.Empty;
        public Replay.ReorderMode Reorder { get; set; } = Replay.ReorderMode.FullSort;

        /// <summary>
        /// Data-source configuration. When null or "mode":"file" the job reads the Lean zip layout.
        /// Set mode to "historical" to pull live from an exchange REST API, or "live" to subscribe
        /// to a WebSocket feed. The <see cref="ExchangeDataAdapter"/> implements both paths.
        /// </summary>
        public JobDataSource Source { get; set; }

        /// <summary>
        /// Creates the replay configuration from this job
        /// </summary>
        public Replay.ReplayConfiguration CreateReplayConfiguration()
        {
            var symbols = new List<QuantConnect.Symbol>();
            foreach (var ticker in Symbols)
            {
                symbols.Add(QuantConnect.Symbol.Create(ticker, ResolveSecurityType(AssetClass), ResolveMarket(Venue, AssetClass)));
            }

            return new Replay.ReplayConfiguration
            {
                StartTime = StartTime,
                EndTime = EndTime,
                Symbols = symbols,
                Venues = string.IsNullOrEmpty(Venue) ? new List<string>() : new List<string> { Venue },
                EventTypes = EventTypes,
                ObservationInterval = ObservationInterval,
                MaxEvents = MaxEvents,
                EngineVersion = EngineVersion,
                Reorder = Reorder
            };
        }

        /// <summary>
        /// Creates the experiment context from this job
        /// </summary>
        public ExperimentContext CreateExperimentContext()
        {
            return new ExperimentContext
            {
                Configuration = new Dictionary<string, string>(ExperimentConfig),
                StartTime = StartTime,
                EndTime = EndTime,
                ConfigurationHash = GetConfigurationHash()
            };
        }

        /// <summary>
        /// Computes a hash of the job configuration for reproducibility
        /// </summary>
        public string GetConfigurationHash()
        {
            var components = new[]
            {
                Dataset,
                string.Join(",", Symbols),
                AssetClass,
                Venue,
                Resolution.ToString(),
                StartTime.ToString("O"),
                EndTime.ToString("O"),
                string.Join(",", EventTypes.Select(e => e.ToString())),
                ObservationInterval?.ToString() ?? "null",
                string.Join(",", Features),
                ExperimentName,
                string.Join(",", Horizons),
                string.Join(",", RawFields),
                EngineVersion,
                Reorder.ToString(),
                StrategyScript,
                Source == null
                    ? string.Empty
                    : string.Join("|",
                        Source.Mode,
                        Source.Provider,
                        Source.Category,
                        Source.OrderBookDepth,
                        Source.PageSize,
                        Source.LiveDurationSeconds,
                        Source.ArchiveFilePath)
            };

            var combined = string.Join("|", components);
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Validates the job configuration
        /// </summary>
        public bool Validate(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrEmpty(Dataset))
                errors.Add("Dataset is required");

            if (Symbols.Count == 0)
                errors.Add("At least one symbol is required");

            if (StartTime >= EndTime)
                errors.Add("StartTime must be before EndTime");

            if (ObservationInterval.HasValue && ObservationInterval.Value <= TimeSpan.Zero)
                errors.Add("ObservationInterval must be positive");

            if (string.IsNullOrEmpty(ExperimentName))
                errors.Add("Experiment name is required");

            if (string.Equals(ExperimentName?.Trim(), "python_strategy", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(StrategyScript))
                errors.Add("StrategyScript is required for python_strategy experiments");

            if (Source != null && !Source.Validate(out var sourceError))
                errors.Add($"Invalid source: {sourceError}");

            return errors.Count == 0;
        }

        /// <summary>
        /// Resolves SecurityType from asset class string
        /// </summary>
        private static QuantConnect.SecurityType ResolveSecurityType(string assetClass)
        {
            return assetClass.ToLowerInvariant() switch
            {
                "crypto" => QuantConnect.SecurityType.Crypto,
                "fx" or "forex" => QuantConnect.SecurityType.Forex,
                "equity" => QuantConnect.SecurityType.Equity,
                "future" or "futures" => QuantConnect.SecurityType.Future,
                "cryptofuture" => QuantConnect.SecurityType.CryptoFuture,
                "cfd" => QuantConnect.SecurityType.Cfd,
                "index" => QuantConnect.SecurityType.Index,
                "option" => QuantConnect.SecurityType.Option,
                _ => QuantConnect.SecurityType.Base
            };
        }

        /// <summary>
        /// Resolves Market from venue string
        /// </summary>
        private static string ResolveMarket(string venue, string assetClass)
        {
            if (venue.Equals("binance", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.Binance;
            if (venue.Equals("coinbase", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.Coinbase;
            if (venue.Equals("oanda", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.Oanda;
            if (venue.Equals("usa", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.USA;
            if (venue.Equals("cme", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.CME;
            if (venue.Equals("bybit", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.Bybit;
            if (venue.Equals("okx", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.Okx;
            if (venue.Equals("deriv", StringComparison.OrdinalIgnoreCase)) return QuantConnect.Market.Deriv;

            return ResolveSecurityType(assetClass) switch
            {
                QuantConnect.SecurityType.Crypto => QuantConnect.Market.Binance,
                QuantConnect.SecurityType.Forex => QuantConnect.Market.Oanda,
                QuantConnect.SecurityType.Equity => QuantConnect.Market.USA,
                QuantConnect.SecurityType.Future => QuantConnect.Market.CME,
                _ => QuantConnect.Market.USA
            };
        }
    }
}