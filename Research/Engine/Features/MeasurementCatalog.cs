using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Discovery entry point for the research layer. Unifies the two existing measurement
    /// namespaces into a single, describable catalog:
    ///
    ///   - the feature registry (derived measurements, computed per observation), and
    ///   - the raw fields exposed by <see cref="RawFieldValues"/> (measures taken straight off
    ///     the observation / market state).
    ///
    /// Where a name exists in both (e.g. mid_price, bid_depth), the derived feature wins because
    /// it is what a job actually requests through job.Features and what FeatureResult.Values
    /// contains. Researchers query this catalog instead of hard-coding field access, and the
    /// catalog never invents measurements the observation cannot provide.
    /// </summary>
    public sealed class MeasurementCatalog
    {
        /// <summary>
        /// All currently available measurements, ordered by name. Entries carry enough metadata
        /// (kind, type, description, source, dependencies) for an experiment or a Python-facing
        /// research interface to reason about them.
        /// </summary>
        public static IReadOnlyList<MeasurementDescriptor> Discover()
        {
            var byName = new Dictionary<string, MeasurementDescriptor>(StringComparer.OrdinalIgnoreCase);

            foreach (var featureName in FeatureRegistry.Instance.GetRegisteredNames())
            {
                var feature = FeatureRegistry.Instance.Create(featureName);
                // Index by the registered name - the key a job selects through job.Features. A few
                // parameterized features expose values under a derived instance name (e.g. the
                // "structural_imbalance" registration reports values as "structural_imbalance_10bps");
                // that value name is added as an alias so conditions and rows never reference a name
                // the catalog cannot describe.
                byName[featureName] = new MeasurementDescriptor(
                    featureName,
                    MeasurementKind.Derived,
                    typeof(decimal),
                    feature.Description,
                    "observation",
                    feature.Dependencies);

                if (!string.Equals(feature.Name, featureName, StringComparison.OrdinalIgnoreCase)
                    && !byName.ContainsKey(feature.Name))
                {
                    byName[feature.Name] = new MeasurementDescriptor(
                        feature.Name,
                        MeasurementKind.Derived,
                        typeof(decimal),
                        feature.Description,
                        "observation",
                        feature.Dependencies);
                }
            }

            foreach (var raw in RawFieldValues.Descriptors)
            {
                if (!byName.ContainsKey(raw.Name))
                {
                    byName[raw.Name] = raw;
                }
            }

            return byName.Values
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Gets the descriptor for a single measurement, or throws a helpful exception listing what
        /// is actually available.
        /// </summary>
        public static MeasurementDescriptor Get(string name)
        {
            var all = Discover();
            var match = all.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }

            throw new KeyNotFoundException(
                $"Measurement '{name}' not found. Available measurements: {string.Join(", ", all.Select(d => d.Name))}");
        }

        /// <summary>
        /// Whether a measurement with this name is currently available.
        /// </summary>
        public static bool IsRegistered(string name)
        {
            return !string.IsNullOrWhiteSpace(name)
                && Discover().Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}