using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Catches the ways a job can ask for something the engine cannot deliver, before the replay
    /// starts. Every finding here is a configuration mistake that would otherwise surface as a
    /// silently constant column, a condition that never fires, or a parameter that does nothing.
    /// </summary>
    public sealed class JobConfigurationCheck : IPreflightCheck
    {
        /// <summary>
        /// Features whose value is a function of order book depth, and therefore requires the job to
        /// subscribe to order book events. Without them these features are identically zero.
        /// </summary>
        private static readonly HashSet<string> DepthDependentFeatures = new(StringComparer.OrdinalIgnoreCase)
        {
            "depth",
            "bid_depth",
            "ask_depth",
            "imbalance",
            "depth_ratio",
            "structural_imbalance",
            "liquidity_wall",
            "resistance",
            "liquidity_depletion",
            "replenishment_rate",
            "depth_persistence"
        };

        /// <summary>
        /// Feature registrations that are aliases of another feature with identical values, so
        /// selecting both yields two identical columns.
        /// </summary>
        private static readonly (string First, string Second)[] AliasPairs =
        {
            ("trade_flow", "net_flow")
        };

        public string Id => ValidationCheckIds.Config;

        public string Description =>
            "Job configuration intent: unknown feature/field names, measurements that cannot fire, " +
            "ignored parameters, and features that need event data the job never subscribes to";

        public void Preflight(ResearchJob job, ValidationReport report)
        {
            if (job == null)
            {
                return;
            }

            CheckFeatureNames(job, report);
            CheckRawFieldNames(job, report);
            CheckDepthDependencies(job, report);
            CheckFeatureParameters(job, report);
            CheckRawFieldCollisions(job, report);
            CheckAliasPairs(job, report);
            CheckHypothesisCondition(job, report);
            CheckObservationCadence(job, report);
        }

        /// <summary>
        /// Observation grid cost, checked before a single event is read.
        ///
        /// With fill-forward the row count follows the clock rather than the data, so two jobs that
        /// look equivalent can differ by orders of magnitude in the rows they produce, and the default
        /// interval is far finer than most feeds. Only the cost signal lives here: whether the cadence
        /// actually matches the data cannot be known until the data is seen, so it is measured by the
        /// <c>freshness</c> check during the run instead of guessed from configuration here.
        /// </summary>
        private static void CheckObservationCadence(ResearchJob job, ValidationReport report)
        {
            if (!job.ObservationInterval.HasValue)
            {
                return;
            }

            var interval = job.ObservationInterval.Value;
            if (interval <= TimeSpan.Zero)
            {
                return;
            }

            var window = job.EndTime - job.StartTime;
            if (window <= TimeSpan.Zero)
            {
                return;
            }

            var projected = window.Ticks / interval.Ticks + 1;
            if (projected > MaxProjectedObservations)
            {
                report.Add(new ValidationFinding
                {
                    Check = ValidationCheckIds.Config,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"observationInterval {Describe(interval)} over a {Describe(window)} window implies about " +
                        $"{projected:N0} observation rows per symbol. With fillForward enabled the row count follows the " +
                        "clock rather than the data, so this is what the run will attempt. Set maxObservations to bound " +
                        "it, or widen observationInterval toward the cadence the data actually has."
                });
            }
        }

        /// <summary>
        /// Above this many projected observations per symbol, the grid is worth confirming.
        /// </summary>
        private const long MaxProjectedObservations = 10_000_000;


        private static string Describe(TimeSpan value)
        {
            if (value.TotalSeconds < 1d)
            {
                return $"{value.TotalMilliseconds:N0}ms";
            }

            if (value.TotalMinutes < 1d)
            {
                return $"{value.TotalSeconds:N0}s";
            }

            if (value.TotalHours < 1d)
            {
                return $"{value.TotalMinutes:N0}m";
            }

            return $"{value.TotalHours:N1}h";
        }

        /// <summary>
        /// Messages for every feature or raw field the job names that does not exist. These are not
        /// suspicious results, they are names that will throw during replay, so the executor uses this
        /// to fail the job up front with the same "did you mean" guidance preflight reports.
        /// </summary>
        public static IReadOnlyList<string> UnresolvableNames(ResearchJob job)
        {
            return UnresolvableFeatures(job).Concat(UnresolvableRawFields(job)).ToList();
        }

        /// <summary>
        /// Selected feature names that do not exist in the registry.
        /// </summary>
        public static IReadOnlyList<string> UnresolvableFeatures(ResearchJob job)
        {
            var messages = new List<string>();
            if (job == null)
            {
                return messages;
            }

            var registry = FeatureRegistry.Instance;
            foreach (var feature in job.Features ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(feature) && !registry.IsRegistered(feature))
                {
                    messages.Add(DescribeUnknownFeature(feature, registry.GetRegisteredNames()));
                }
            }

            return messages;
        }

        /// <summary>
        /// Requested raw field names that are not known measurements.
        /// </summary>
        public static IReadOnlyList<string> UnresolvableRawFields(ResearchJob job)
        {
            var messages = new List<string>();
            if (job == null)
            {
                return messages;
            }

            foreach (var field in job.RawFields ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(field)
                    && !RawFieldValues.Names.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    messages.Add(DescribeUnknownRawField(field));
                }
            }

            return messages;
        }

        private static string DescribeUnknownFeature(string feature, IEnumerable<string> knownNames)
        {
            var suggestion = Closest(feature, knownNames);
            return suggestion == null
                ? $"Feature '{feature}' is not registered and will fail the run."
                : $"Feature '{feature}' is not registered. Did you mean '{suggestion}'?";
        }

        private static string DescribeUnknownRawField(string field)
        {
            var suggestion = Closest(field, RawFieldValues.Names);
            return suggestion == null
                ? $"Raw field '{field}' is not a known measurement and will fail the run. " +
                  $"Known fields: {string.Join(", ", RawFieldValues.Names)}"
                : $"Raw field '{field}' is not a known measurement. Did you mean '{suggestion}'? " +
                  $"Known fields: {string.Join(", ", RawFieldValues.Names)}";
        }

        /// <summary>
        /// Every selected feature must exist in the registry. <see cref="FeatureRegistry.Create"/>
        /// already throws, but a near-miss name deserves a "did you mean" rather than a KeyNotFound.
        /// </summary>
        private static void CheckFeatureNames(ResearchJob job, ValidationReport report)
        {
            foreach (var message in UnresolvableFeatures(job))
            {
                report.Add(new ValidationFinding
                {
                    Check = ValidationCheckIds.Config,
                    Severity = ValidationSeverity.Error,
                    Message = message
                });
            }
        }

        /// <summary>
        /// Raw field names are resolved at observation time and throw on an unknown name, so validate
        /// them up front with the same help the runtime would give.
        /// </summary>
        private static void CheckRawFieldNames(ResearchJob job, ValidationReport report)
        {
            foreach (var message in UnresolvableRawFields(job))
            {
                report.Add(new ValidationFinding
                {
                    Check = ValidationCheckIds.Config,
                    Severity = ValidationSeverity.Error,
                    Message = message
                });
            }
        }

        /// <summary>
        /// Depth-dependent features are identically zero unless the job subscribes to order book
        /// events. This is the single most common cause of a "my imbalance condition never fires" run.
        /// </summary>
        private static void CheckDepthDependencies(ResearchJob job, ValidationReport report)
        {
            var eventTypes = job.EventTypes ?? new List<MarketEventType>();
            var hasBook = eventTypes.Contains(MarketEventType.OrderBookUpdate)
                          || eventTypes.Contains(MarketEventType.OrderBookSnapshot);
            if (hasBook)
            {
                return;
            }

            var affected = (job.Features ?? new List<string>())
                .Where(DepthDependentFeatures.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (affected.Count == 0)
            {
                return;
            }

            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Config,
                Severity = ValidationSeverity.Error,
                Message = $"Job selects order-book-dependent feature(s) [{string.Join(", ", affected)}] but " +
                          $"eventTypes is [{string.Join(", ", eventTypes)}], which contains no order book type. " +
                          "These features will be constant 0.0 for every observation. Add " +
                          "'OrderBookUpdate' and stage book_updates.csv (feed mode), or drop the features. " +
                          "Note that no exchange REST endpoint serves historical L2 deltas, so " +
                          "book_updates.csv must come from a websocket capture."
            });
        }

        /// <summary>
        /// Flags <c>feature.&lt;name&gt;.&lt;param&gt;</c> keys that name a parameter the feature does
        /// not read, which is otherwise indistinguishable from a typo.
        /// </summary>
        private static void CheckFeatureParameters(ResearchJob job, ValidationReport report)
        {
            var config = job.ExperimentConfig;
            if (config == null || config.Count == 0)
            {
                return;
            }

            var parameters = FeatureParameters.ParseJobConfig(config);
            foreach (var featureName in (job.Features ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!FeatureRegistry.Instance.IsRegistered(featureName))
                {
                    continue;
                }

                var configured = parameters.For(featureName);
                if (configured.Values.Count == 0)
                {
                    continue;
                }

                var feature = FeatureRegistry.Instance.Create(featureName, configured);
                var supported = SupportedParameterNames(feature);
                foreach (var key in configured.Values.Keys)
                {
                    if (supported.Contains(key))
                    {
                        continue;
                    }

                    report.Add(new ValidationFinding
                    {
                        Check = ValidationCheckIds.Config,
                        Severity = ValidationSeverity.Warning,
                        Message = supported.Count == 0
                            ? $"'{FeatureParameters.Prefix}{featureName}.{key}' is set but feature '{featureName}' takes no parameters, so it has no effect."
                            : $"'{FeatureParameters.Prefix}{featureName}.{key}' is not a parameter of '{featureName}' and is ignored. " +
                              $"Supported: {string.Join(", ", supported.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => FeatureParameters.Prefix + featureName + "." + k))}"
                    });
                }
            }
        }

        /// <summary>
        /// A raw field that shares a name with a computed feature is redundant: the engine keeps the
        /// feature value, so the raw column silently never appears. Say so rather than let the user
        /// hunt for a missing column.
        /// </summary>
        private static void CheckRawFieldCollisions(ResearchJob job, ValidationReport report)
        {
            var features = job.Features ?? new List<string>();
            if (features.Count == 0)
            {
                return;
            }

            foreach (var field in job.RawFields ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(field)
                    || !features.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                report.Add(new ValidationFinding
                {
                    Check = ValidationCheckIds.Config,
                    Severity = ValidationSeverity.Warning,
                    Message = $"Raw field '{field}' is also a requested feature. Derived measurements take " +
                              "precedence on the output row, so the raw field is not added as a separate column. " +
                              "Drop it from rawFields, or rename the feature if you wanted the raw value."
                });
            }
        }

        /// <summary>
        /// Two selected features that always produce the same values make it look like there are two
        /// independent signals when there is one.
        /// </summary>
        private static void CheckAliasPairs(ResearchJob job, ValidationReport report)
        {
            var features = job.Features ?? new List<string>();
            foreach (var (first, second) in AliasPairs)
            {
                if (features.Contains(first, StringComparer.OrdinalIgnoreCase)
                    && features.Contains(second, StringComparer.OrdinalIgnoreCase))
                {
                    report.Add(new ValidationFinding
                    {
                        Check = ValidationCheckIds.Config,
                        Severity = ValidationSeverity.Warning,
                        Message = $"Features '{first}' and '{second}' are two names for the same signed " +
                                  "notional flow and will produce identical columns. Keep one."
                    });
                }
            }
        }

        /// <summary>
        /// A hypothesis condition is evaluated against computed feature values only, so a condition
        /// naming a raw-only measurement can never fire.
        /// </summary>
        private static void CheckHypothesisCondition(ResearchJob job, ValidationReport report)
        {
            if (job.ExperimentName == null
                || !job.ExperimentName.Split(',')
                    .Any(n => n.Trim().Equals("hypothesis", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (!job.ExperimentConfig.TryGetValue("condition", out var condition)
                || string.IsNullOrWhiteSpace(condition))
            {
                return;
            }

            var measurement = condition.Split('<', '>', '=', '!').FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(measurement))
            {
                return;
            }

            var parameters = FeatureParameters.ParseJobConfig(job.ExperimentConfig);
            var selected = new HashSet<string>(
                (job.Features ?? new List<string>()).Select(f => f.Trim()),
                StringComparer.OrdinalIgnoreCase);

            if (selected.Contains(measurement))
            {
                return;
            }

            var isFeature = FeatureRegistry.Instance.IsRegistered(measurement);
            var isRawField = RawFieldValues.Names.Contains(measurement, StringComparer.OrdinalIgnoreCase);
            var suggestion = Closest(measurement, FeatureRegistry.Instance.GetRegisteredNames());

            string message;
            ValidationSeverity severity;

            if (isFeature)
            {
                // A real feature that the job simply forgot to select: the most likely mistake and
                // the easiest to fix, so name the exact job change.
                message = $"Hypothesis condition '{condition}' measures '{measurement}', which is a registered " +
                          $"feature the job did not select, so the condition can never fire. Add '{measurement}' " +
                          "to job.features.";
                severity = ValidationSeverity.Error;
            }
            else if (isRawField)
            {
                // Raw fields are output columns, not feature values, and they have no feature
                // equivalent, so the advice is to pick a feature rather than to promote the field.
                message = $"Hypothesis condition '{condition}' measures '{measurement}', which is a raw field, not a " +
                          "computed feature. Conditions are evaluated against feature values only, so this can never " +
                          $"fire, and '{measurement}' cannot be added to job.features because it is not a " +
                          "registered feature. Use a registered feature instead" +
                          (suggestion == null ? "." : $"; the closest is '{suggestion}'.") +
                          " Conditions: " + DescribeConditionOptions();
                severity = ValidationSeverity.Error;
            }
            else
            {
                message = $"Hypothesis condition '{condition}' measures '{measurement}', which is not a registered " +
                          $"feature. This can never fire. Add it to job.features. Selected: " +
                          $"{(selected.Count == 0 ? "(none)" : string.Join(", ", selected.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)))}";
                severity = ValidationSeverity.Warning;
            }

            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Config,
                Severity = severity,
                Message = message
            });
        }

        /// <summary>
        /// A short sample of condition-usable measurement names, so a dead condition can be repaired
        /// without leaving the error message to go read the registry.
        /// </summary>
        private static string DescribeConditionOptions()
        {
            const int sampleSize = 12;
            var names = FeatureRegistry.Instance.GetRegisteredNames().Take(sampleSize).ToList();
            return names.Count == 0
                ? "no features are registered"
                : "available features include: " + string.Join(", ", names);
        }

        /// <summary>
        /// Parameter names a feature actually reads, resolved from the features that consume a
        /// <see cref="FeatureParams"/> bag. Derived by construction so a newly parameterized feature
        /// is covered without updating this list.
        /// </summary>
        private static HashSet<string> SupportedParameterNames(IFeature feature)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (feature is StructuralImbalanceFeature)
            {
                names.Add("bps_band");
            }

            if (feature is LiquidityWallFeature)
            {
                names.Add("wall_threshold_multiplier");
            }

            if (feature is ResistanceFeature)
            {
                names.Add("resistance_factor");
            }

            if (feature is LiquidityDepletionFeature)
            {
                names.Add("lookback_periods");
                names.Add("bps_band");
            }

            if (feature is LiquidityReplenishmentRateFeature)
            {
                names.Add("window_size");
                names.Add("bps_band");
            }

            if (feature is DepthPersistenceFeature)
            {
                names.Add("window_size");
            }

            return names;
        }

        /// <summary>
        /// Nearest registered name to a misspelling, by edit distance, within a threshold that scales
        /// with the length of the input. Returns null when nothing is close enough to suggest.
        /// </summary>
        private static string Closest(string candidate, IEnumerable<string> options)
        {
            var best = options
                .Select(o => (Name: o, Distance: Levenshtein(candidate.ToLowerInvariant(), o.ToLowerInvariant())))
                .OrderBy(x => x.Distance)
                .FirstOrDefault();

            var threshold = Math.Max(2, candidate.Length / 3);
            return best.Name != null && best.Distance <= threshold ? best.Name : null;
        }

        private static int Levenshtein(string left, string right)
        {
            if (left.Length == 0)
            {
                return right.Length;
            }

            if (right.Length == 0)
            {
                return left.Length;
            }

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];

            for (var j = 0; j <= right.Length; j++)
            {
                previous[j] = j;
            }

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }
    }
}
