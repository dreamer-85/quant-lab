using System;
using System.Collections.Generic;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Classification of a measurement's origin.
    /// </summary>
    public enum MeasurementKind
    {
        /// <summary>
        /// Taken directly from the observation: a field of the market state or a summary of the
        /// events that occurred since the previous observation (e.g. mid_price, bid_depth, volume).
        /// </summary>
        Raw,

        /// <summary>
        /// Computed from other measurements (possibly itself derived). Researchers add derived
        /// measurements without touching the core engine (e.g. imbalance from bid/ask depth).
        /// </summary>
        Derived
    }

    /// <summary>
    /// Machine-readable description of a single measurement the research layer can use.
    /// The research layer discovers available measurements through
    /// <see cref="MeasurementCatalog.Discover"/> instead of hard-coding field access.
    /// </summary>
    public sealed class MeasurementDescriptor
    {
        /// <summary>
        /// Unique measurement name (the string requested in job.Features / job.RawFields or
        /// referenced by an experiment condition).
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Whether this measurement is raw (read off the observation) or derived (computed).
        /// </summary>
        public MeasurementKind Kind { get; }

        /// <summary>
        /// Value type of the measurement (decimal for numeric measurements, string/DateTime for
        /// identity columns such as symbol/timestamp).
        /// </summary>
        public Type ValueType { get; }

        /// <summary>
        /// Human-readable description of what the measurement represents.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Where the value comes from: "observation" (whole observation), "state" (point-in-time
        /// market state), "events" (summary of the period's events), or "none".
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Names of other measurements this measurement reads from. Empty for raw measurements.
        /// Declared dependencies let the engine compute values in the correct order and compute
        /// only the transitive closure of what was selected.
        /// </summary>
        public IReadOnlyList<string> Dependencies { get; }

        /// <summary>
        /// Creates a measurement descriptor.
        /// </summary>
        public MeasurementDescriptor(
            string name,
            MeasurementKind kind,
            Type valueType,
            string description,
            string source,
            IReadOnlyList<string> dependencies = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind;
            ValueType = valueType ?? typeof(decimal);
            Description = string.IsNullOrWhiteSpace(description) ? name : description;
            Source = string.IsNullOrWhiteSpace(source) ? "observation" : source;
            Dependencies = dependencies ?? Array.Empty<string>();
        }

        /// <summary>
        /// Returns the measurement name.
        /// </summary>
        public override string ToString() => Name;
    }
}