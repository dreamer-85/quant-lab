using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Features
{
    /// <summary>
    /// Base class for features providing common helpers and state management.
    /// </summary>
    public abstract class FeatureBase : IFeature
    {
        /// <summary>
        /// Unique feature identifier
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Feature description
        /// </summary>
        public virtual string Description => Name;

        /// <summary>
        /// Other measurements this feature reads from. Empty by default; derived measurements that
        /// read previously computed values through <see cref="FeatureContext.GetMeasurement"/>
        /// should list them so the engine orders computation correctly.
        /// </summary>
        public virtual IReadOnlyList<string> Dependencies => Array.Empty<string>();

        /// <summary>
        /// Computes the feature value from market state.
        /// This method MUST NOT use any future information.
        /// </summary>
        public abstract decimal Compute(Observation observation, FeatureContext context);

        /// <summary>
        /// Resets feature internal state (for when state window changes)
        /// </summary>
        public virtual void Reset()
        {
        }

        /// <summary>
        /// Returns a string representation
        /// </summary>
        public override string ToString() => Name;
    }
}