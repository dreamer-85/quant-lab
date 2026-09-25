using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Stage 2: dependency-aware measurement computation. Selecting a derived measurement pulls in
    /// its declared dependencies (transitive closure), and values are computed in dependency order
    /// and exposed through <see cref="FeatureContext.GetMeasurement"/> so derived measurements
    /// compose instead of recomputing each other's math.
    /// </summary>
    [TestFixture]
    public class FeatureDependencyTests
    {
        private const string BaseA = "dep.test.base_a";
        private const string BaseB = "dep.test.base_b";
        private const string Sum = "dep.test.sum";

        [SetUp]
        public void SetUp()
        {
            FeatureRegistry.Instance.Register(BaseA, () => new ConstantFeature(BaseA, 5m));
            FeatureRegistry.Instance.Register(BaseB, () => new ConstantFeature(BaseB, 7m));
            FeatureRegistry.Instance.Register(Sum, () => new SumFeature(Sum, BaseA, BaseB));
        }

        [TearDown]
        public void TearDown()
        {
            var registry = FeatureRegistry.Instance;
            var field = typeof(FeatureRegistry).GetField("_registrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(registry) is Dictionary<string, Func<IFeature>> map)
            {
                map.Remove(BaseA);
                map.Remove(BaseB);
                map.Remove(Sum);
            }
        }

        [Test]
        public void EngineOrdersDependenciesBeforeDependents()
        {
            var engine = new FeatureEngine(new List<IFeature>
            {
                new SumFeature(Sum, BaseA, BaseB),
                new ConstantFeature(BaseA, 5m),
                new ConstantFeature(BaseB, 7m)
            });

            Assert.That(engine.Features.Select(f => f.Name).ToList(),
                Is.EqualTo(new[] { BaseA, BaseB, Sum }));

            var result = engine.Compute(ObservationsTestHelpers.CreateObservationWithTrades());
            Assert.That(result.Values[Sum], Is.EqualTo(12m));
        }

        [Test]
        public void FromNames_ExpandsDependencyClosure()
        {
            var engine = FeatureEngine.FromNames(new[] { Sum });

            Assert.That(engine.Features.Select(f => f.Name).OrderBy(n => n).ToList(),
                Is.EqualTo(new[] { BaseA, BaseB, Sum }));
            Assert.That(engine.Features.Select(f => f.Name).ToList()[^1], Is.EqualTo(Sum), "dependent must run last");

            var result = engine.Compute(ObservationsTestHelpers.CreateObservationWithTrades());
            Assert.That(result.Values[Sum], Is.EqualTo(12m));
        }

        [Test]
        public void FromNames_TransitivelyPullsNestedDependencies()
        {
            const string grand = "dep.test.grand";
            FeatureRegistry.Instance.Register(grand, () => new SumFeature(grand, Sum, BaseB));
            try
            {
                var engine = FeatureEngine.FromNames(new[] { grand });
                Assert.That(engine.Features.Select(f => f.Name).ToList(),
                    Is.EqualTo(new[] { BaseA, BaseB, Sum, grand }));
                Assert.That(engine.Compute(ObservationsTestHelpers.CreateObservationWithTrades()).Values[grand], Is.EqualTo(19m));
            }
            finally
            {
                var registry = FeatureRegistry.Instance;
                var field = typeof(FeatureRegistry).GetField("_registrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(registry) is Dictionary<string, Func<IFeature>> map)
                {
                    map.Remove(grand);
                }
            }
        }

        [Test]
        public void Compute_ExposesPendingMeasurementsDuringComputation()
        {
            var engine = new FeatureEngine(new List<IFeature>
            {
                new ConstantFeature(BaseA, 5m),
                new RecordingFeature("dep.test.recorder", BaseA)
            });

            var result = engine.Compute(ObservationsTestHelpers.CreateObservationWithTrades());
            Assert.That(result.Values["dep.test.recorder"], Is.EqualTo(123m));
        }

        [Test]
        public void MissingDependency_IsRejectedWithClearError()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new FeatureEngine(new List<IFeature> { new SumFeature("dep.test.x", "dep.test.missing_a", "dep.test.missing_b") }));

            Assert.That(ex.Message, Does.Contain("dep.test.x"));
            Assert.That(ex.Message, Does.Contain("dep.test.missing_a"));
        }

        [Test]
        public void CircularDependency_IsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new FeatureEngine(new List<IFeature>
                {
                    new SumFeature("dep.test.cyc_a", "dep.test.cyc_b", "dep.test.cyc_b"),
                    new SumFeature("dep.test.cyc_b", "dep.test.cyc_a", "dep.test.cyc_a")
                }));

            Assert.That(ex.Message, Does.Contain("Circular"));
            Assert.That(ex.Message, Does.Contain("dep.test.cyc_"));
        }

        [Test]
        public void IndependentFeaturesKeepStableRelativeOrder()
        {
            var engine = new FeatureEngine(new List<IFeature>
            {
                new ConstantFeature("dep.test.f1", 1m),
                new ConstantFeature("dep.test.f2", 2m),
                new ConstantFeature("dep.test.f3", 3m)
            });

            Assert.That(engine.Features.Select(f => f.Name).ToList(),
                Is.EqualTo(new[] { "dep.test.f1", "dep.test.f2", "dep.test.f3" }));
        }

        private sealed class ConstantFeature : FeatureBase
        {
            private readonly string _name;
            private readonly decimal _value;
            public ConstantFeature(string name, decimal value) { _name = name; _value = value; }
            public override string Name => _name;
            public override decimal Compute(Observation observation, FeatureContext context) => _value;
        }

        private sealed class SumFeature : FeatureBase
        {
            private readonly string _name;
            private readonly string _left;
            private readonly string _right;
            public SumFeature(string name, string left, string right) { _name = name; _left = left; _right = right; }
            public override string Name => _name;
            public override IReadOnlyList<string> Dependencies => new[] { _left, _right };
            public override decimal Compute(Observation observation, FeatureContext context) =>
                context.GetMeasurement(_left) + context.GetMeasurement(_right);
        }

        /// <summary>Returns a sentinel that proves it could read the dependency's value in-scope.</summary>
        private sealed class RecordingFeature : FeatureBase
        {
            private readonly string _name;
            private readonly string _dep;
            public RecordingFeature(string name, string dep) { _name = name; _dep = dep; }
            public override string Name => _name;
            public override IReadOnlyList<string> Dependencies => new[] { _dep };
            public override decimal Compute(Observation observation, FeatureContext context) =>
                context.HasMeasurement(_dep) && context.GetMeasurement(_dep) == 5m ? 123m : 0m;
        }
    }
}