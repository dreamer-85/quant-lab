using System.Collections.Generic;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// A check that inspects the job configuration before any data is replayed, so a job that
    /// cannot produce what it asks for is reported in milliseconds rather than after the run.
    /// </summary>
    public interface IPreflightCheck
    {
        /// <summary>
        /// Stable check id, usable in <c>validation.checks</c>.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// What the check looks for.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Inspects the job and appends any findings.
        /// </summary>
        void Preflight(ResearchJob job, ValidationReport report);
    }

    /// <summary>
    /// A check that inspects every observation as it is emitted. Implementations must be O(1) in
    /// memory: the engine's bounded-memory guarantee applies to validation too.
    /// </summary>
    public interface IObservationCheck
    {
        /// <summary>
        /// Stable check id, usable in <c>validation.checks</c>.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// What the check looks for.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Inspects one observation together with the feature values computed for it.
        /// </summary>
        void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features, ValidationReport report);
    }

    /// <summary>
    /// A check that runs once after the replay, over the columns that were actually produced.
    /// Implementations must keep only bounded per-column state (counts, hashes), never the series.
    /// </summary>
    public interface IOutputCheck
    {
        /// <summary>
        /// Stable check id, usable in <c>validation.checks</c>.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// What the check looks for.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Inspects one emitted output row. Called for every observation, before
        /// <see cref="Complete"/>.
        /// </summary>
        void OnRow(IReadOnlyDictionary<string, decimal> row, ValidationReport report);

        /// <summary>
        /// Produces findings that can only be determined across the whole run.
        /// </summary>
        void Complete(ValidationReport report);

        /// <summary>
        /// Clears per-run state so a check instance can be reused across symbols.
        /// </summary>
        void Reset();
    }
}
