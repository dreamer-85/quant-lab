using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Stage 1: measurement discovery. The research layer discovers what it can measure through
    /// <see cref="MeasurementCatalog"/> instead of hard-coding field access.
    /// </summary>
    [TestFixture]
    public class MeasurementCatalogTests
    {
        [Test]
        public void Discover_UnifiesFeatureAndRawFieldNamespaces()
        {
            var names = MeasurementCatalog.Discover().Select(d => d.Name).ToList();

            Assert.That(names, Does.Contain("imbalance"));          // derived feature
            Assert.That(names, Does.Contain("bid_depth"));          // derived feature
            Assert.That(names, Does.Contain("trade_volume"));       // derived feature
            Assert.That(names, Does.Contain("mid_price"));          // both namespaces -> deduped
            Assert.That(names, Does.Contain("vwap"));               // raw-only field
            Assert.That(names, Does.Contain("trade_flow"));         // raw-only field
            Assert.That(names, Is.Ordered.Ascending);
        }

        [Test]
        public void Discover_DuplicateNameResolvesToDerivedFeature()
        {
            var mid = MeasurementCatalog.Get("mid_price");
            Assert.That(mid.Kind, Is.EqualTo(MeasurementKind.Derived));
            Assert.That(mid.ValueType, Is.EqualTo(typeof(decimal)));
        }

        [Test]
        public void RawOnlyFieldCarriesRawMetadata()
        {
            var vwap = MeasurementCatalog.Get("vwap");
            Assert.That(vwap.Kind, Is.EqualTo(MeasurementKind.Raw));
            Assert.That(vwap.Source, Is.EqualTo("events"));
            Assert.That(vwap.Dependencies, Is.Empty);
        }

        [Test]
        public void DerivedFeatureCarriesDescriptionAndEmptyDependenciesByDefault()
        {
            var imbalance = MeasurementCatalog.Get("imbalance");
            Assert.That(imbalance.Kind, Is.EqualTo(MeasurementKind.Derived));
            Assert.That(imbalance.Description, Is.Not.Empty);
            Assert.That(imbalance.Source, Is.EqualTo("observation"));
            Assert.That(imbalance.Dependencies, Is.Empty);
        }

        [Test]
        public void EveryRegisteredFeatureExistsInTheCatalog()
        {
            var all = MeasurementCatalog.Discover();
            foreach (var name in FeatureRegistry.Instance.GetRegisteredNames())
            {
                Assert.That(all.Any(d => d.Name == name),
                    Is.True, $"feature '{name}' missing from catalog");
            }
        }

        [Test]
        public void DiscoverReflectsLateRegistrations()
        {
            var unique = "catalog.test.late";
            FeatureRegistry.Instance.Register(unique, () => new ConstantFeature(unique, 1m));
            try
            {
                Assert.That(MeasurementCatalog.IsRegistered(unique), Is.True);
                Assert.That(MeasurementCatalog.Get(unique).Name, Is.EqualTo(unique));
            }
            finally
            {
                // Best-effort cleanup only; the name is unique so even a leftover harms nothing.
                var registry = FeatureRegistry.Instance;
                var field = typeof(FeatureRegistry).GetField(
                    "_registrations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field?.GetValue(registry) is System.Collections.Generic.Dictionary<string, System.Func<IFeature>> map)
                {
                    map.Remove(unique);
                }
            }
        }

        [Test]
        public void Get_UnknownNameThrowsWithCatalog()
        {
            var ex = Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => MeasurementCatalog.Get("no_such_measurement"));
            Assert.That(ex.Message, Does.Contain("no_such_measurement"));
            Assert.That(ex.Message, Does.Contain("Available measurements"));
            Assert.That(ex.Message, Does.Contain("imbalance"));
        }

        [Test]
        public void RawFieldValues_ErrorListsEveryKnownName()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() => RawFieldValues.For(new Observation(), "no_such_raw_field"));
            Assert.That(ex.Message, Does.Contain("vwap"));
            Assert.That(ex.Message, Does.Contain("trade_flow"));
        }

        private sealed class ConstantFeature : FeatureBase
        {
            private readonly decimal _value;
            public ConstantFeature(string name, decimal value) { _name = name; _value = value; }
            private readonly string _name;
            public override string Name => _name;
            public override decimal Compute(Observation observation, FeatureContext context) => _value;
        }
    }
}