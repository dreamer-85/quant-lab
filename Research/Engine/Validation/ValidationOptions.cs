using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Validation configuration, resolved from flat <c>validation.*</c> job configuration keys.
    /// Deliberately part of <c>experimentConfig</c> rather than a typed job property: validation
    /// cannot change a run's results, so it must stay out of
    /// <see cref="ResearchJob.GetConfigurationHash"/> and must never invalidate a checkpoint.
    ///
    /// Recognized keys:
    ///   validation.mode              off | warn | fail          (default: warn)
    ///   validation.max_findings      retained findings per check (default: 20)
    ///   validation.checks            comma list of check ids     (default: all enabled checks)
    /// </summary>
    public sealed class ValidationOptions
    {
        /// <summary>
        /// Configuration key prefix.
        /// </summary>
        public const string Prefix = "validation.";

        /// <summary>
        /// Mode key.
        /// </summary>
        public const string ModeKey = Prefix + "mode";

        /// <summary>
        /// Max findings key.
        /// </summary>
        public const string MaxFindingsKey = Prefix + "max_findings";

        /// <summary>
        /// Checks key.
        /// </summary>
        public const string ChecksKey = Prefix + "checks";

        /// <summary>
        /// Whether validation runs, and whether findings can fail a job.
        /// </summary>
        public ValidationMode Mode { get; }

        /// <summary>
        /// Maximum findings retained per check.
        /// </summary>
        public int MaxFindingsPerCheck { get; }

        /// <summary>
        /// Explicitly enabled check ids, or null to enable every registered check.
        /// </summary>
        public IReadOnlyCollection<string> Checks { get; }

        /// <summary>
        /// Whether a check id is enabled under these options.
        /// </summary>
        public bool IsEnabled(string checkId)
        {
            return Checks == null || Checks.Contains(checkId, StringComparer.OrdinalIgnoreCase);
        }

        public ValidationOptions(ValidationMode mode, int maxFindingsPerCheck, IReadOnlyCollection<string> checks)
        {
            Mode = mode;
            MaxFindingsPerCheck = Math.Max(1, maxFindingsPerCheck);
            Checks = checks;
        }

        /// <summary>
        /// Default options: validate everything, warn but never fail.
        /// </summary>
        public static ValidationOptions Default => new(ValidationMode.Warn, 20, null);

        /// <summary>
        /// Resolves options from a job's flat experiment configuration. An unparseable
        /// <c>validation.mode</c> falls back to <see cref="ValidationMode.Warn"/> and the bad value
        /// is returned as a preflight finding rather than throwing, so a typo in a guardrail cannot
        /// take down a research run.
        /// </summary>
        public static ValidationOptions FromJob(ResearchJob job, ICollection<ValidationFinding> preflight = null)
        {
            var config = job?.ExperimentConfig;
            if (config == null || config.Count == 0)
            {
                return Default;
            }

            var mode = ValidationMode.Warn;
            if (config.TryGetValue(ModeKey, out var rawMode) && !string.IsNullOrWhiteSpace(rawMode))
            {
                switch (rawMode.Trim().ToLowerInvariant())
                {
                    case "off":
                    case "false":
                    case "none":
                        mode = ValidationMode.Off;
                        break;
                    case "warn":
                    case "warnings":
                        mode = ValidationMode.Warn;
                        break;
                    case "fail":
                    case "strict":
                        mode = ValidationMode.Fail;
                        break;
                    default:
                        preflight?.Add(new ValidationFinding
                        {
                            Check = ValidationCheckIds.Config,
                            Severity = ValidationSeverity.Warning,
                            Message = $"Unrecognized '{ModeKey}' value '{rawMode}'. Expected off|warn|fail. Falling back to 'warn'."
                        });
                        break;
                }
            }

            var maxFindings = 20;
            if (config.TryGetValue(MaxFindingsKey, out var rawMax)
                && int.TryParse(rawMax, out var parsedMax))
            {
                maxFindings = Math.Max(1, parsedMax);
            }

            IReadOnlyCollection<string> checks = null;
            if (config.TryGetValue(ChecksKey, out var rawChecks) && !string.IsNullOrWhiteSpace(rawChecks))
            {
                checks = rawChecks
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            return new ValidationOptions(mode, maxFindings, checks);
        }
    }

    /// <summary>
    /// Stable identifiers for the built-in validation checks, usable in
    /// <c>validation.checks</c>.
    /// </summary>
    public static class ValidationCheckIds
    {
        /// <summary>
        /// Job configuration intent: unusable feature/field names, measurements that cannot fire,
        /// parameters with no effect, features that need data the job never subscribes to.
        /// </summary>
        public const string Config = "config";

        /// <summary>
        /// Mathematical identities that must hold on the reconstructed market state.
        /// </summary>
        public const string MarketState = "market_state";

        /// <summary>
        /// Coherence of the per-period price summary (OHLC, vwap, volume).
        /// </summary>
        public const string Observation = "observation";

        /// <summary>
        /// Arithmetic identities between computed features (e.g. net flow = buy notional - sell notional).
        /// </summary>
        public const string Flow = "flow";

        /// <summary>
        /// Numeric plausibility: non-finite values, negative quantities, negative depth.
        /// </summary>
        public const string Numeric = "numeric";

        /// <summary>
        /// Columns that carry no information (constant zero, e.g. a depth feature on a job with no
        /// order book data).
        /// </summary>
        public const string Degenerate = "degenerate";

        /// <summary>
        /// Two or more columns whose values are identical across every observation, which usually
        /// means one of them is not computing what its name claims.
        /// </summary>
        public const string Duplicate = "duplicate";

        /// <summary>
        /// Integrity of the observation clock: strictly increasing timestamps, uniform steps, and no
        /// observation carrying data from its own future. This is the check that catches a scheduler
        /// which stamps periods earlier than the data they contain.
        /// </summary>
        public const string Grid = "grid";

        /// <summary>
        /// Whether the observation cadence matches the data. Reports the share of periods that
        /// carried state forward because no new data arrived, which is what a "20 period" window
        /// silently turns into a much longer stretch of time.
        /// </summary>
        public const string Freshness = "freshness";

        /// <summary>
        /// Whether the engine emitted anywhere near the observations the job's window and cadence
        /// imply, and whether the replay stopped short of the requested range.
        /// </summary>
        public const string Coverage = "coverage";
    }
}
