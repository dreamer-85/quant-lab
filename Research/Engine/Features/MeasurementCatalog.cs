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
        /// research interface to reason about them. Parameterized features are described with their
        /// default parameters; use <see cref="Discover(FeatureParameters)"/> to describe them as the
        /// job actually configures them.
        /// </summary>
        public static IReadOnlyList<MeasurementDescriptor> Discover()
        {
            return Discover(parameters: null);
        }

        /// <summary>
        /// All currently available measurements, describing parameterized features with the parameter
        /// values resolved from job configuration rather than their defaults. A feature selected by
        /// the job resolves to the value name it will actually emit (e.g. "structural_imbalance_25bps"
        /// for "feature.structural_imbalance.bps_band=25"); the default-parameter alias is still
        /// described so a name that is valid but not selected remains discoverable.
        /// </summary>
        public static IReadOnlyList<MeasurementDescriptor> Discover(FeatureParameters parameters)
        {
            var byName = new Dictionary<string, MeasurementDescriptor>(StringComparer.OrdinalIgnoreCase);

            foreach (var featureName in FeatureRegistry.Instance.GetRegisteredNames())
            {
                // Always register the default-parameter instance under its registration key: that is
                // the name a job selects through job.Features, so it must always be described.
                byName[featureName] = Describe(featureName, FeatureRegistry.Instance.Create(featureName));

                var configured = parameters?.For(featureName);
                if (configured is { Values.Count: > 0 })
                {
                    // Also describe the configured instance under the value name it will emit, so the
                    // catalog names the column the run actually produces.
                    var configuredFeature = FeatureRegistry.Instance.Create(featureName, configured);
                    if (!string.Equals(configuredFeature.Name, featureName, StringComparison.OrdinalIgnoreCase))
                    {
                        byName[configuredFeature.Name] = Describe(configuredFeature.Name, configuredFeature);
                    }
                }

                // A few parameterized features expose values under a derived instance name (e.g. the
                // "structural_imbalance" registration reports values as "structural_imbalance_10bps");
                // that value name is added as an alias so conditions and rows never reference a name
                // the catalog cannot describe.
                var defaultFeature = FeatureRegistry.Instance.Create(featureName);
                if (!string.Equals(defaultFeature.Name, featureName, StringComparison.OrdinalIgnoreCase))
                {
                    byName[defaultFeature.Name] = Describe(defaultFeature.Name, defaultFeature);
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
        /// Builds a descriptor for a derived feature, indexed under the name it reports values as.
        /// </summary>
        private static MeasurementDescriptor Describe(string name, IFeature feature)
        {
            return new MeasurementDescriptor(
                name,
                MeasurementKind.Derived,
                typeof(decimal),
                feature.Description,
                "observation",
                feature.Dependencies);
        }

        /// <summary>
        /// Gets the descriptor for a single measurement, or throws a helpful exception listing what
        /// is actually available.
        /// </summary>
        public static MeasurementDescriptor Get(string name)
        {
            return Get(name, parameters: null);
        }

        /// <summary>
        /// Gets the descriptor for a single measurement as the given job configuration resolves it,
        /// or throws a helpful exception listing what is actually available.
        /// </summary>
        public static MeasurementDescriptor Get(string name, FeatureParameters parameters)
        {
            var all = Discover(parameters);
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