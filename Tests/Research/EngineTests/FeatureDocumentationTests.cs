using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Features;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// docs/features.md is the contract script authors read before writing a condition, so a feature
    /// that exists but is undocumented is as broken as one that is misdocumented. These tests fail
    /// when the two drift apart.
    /// </summary>
    [TestFixture]
    public class FeatureDocumentationTests
    {
        private static string DocsPath => Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "docs", "features.md");

        private static string Docs
        {
            get
            {
                var path = DocsPath;
                Assert.That(File.Exists(path), Is.True, $"docs/features.md not found at {path}");
                return File.ReadAllText(path);
            }
        }

        [Test]
        public void EveryRegisteredFeature_IsDocumented()
        {
            // Only a table entry counts as documentation. A passing prose mention somewhere else in
            // the file is not enough: an author scanning the tables must be able to find it.
            var documented = DocumentedKeys().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var undocumented = FeatureRegistry.Instance.GetRegisteredNames()
                .Where(name => !documented.Contains(name))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.That(undocumented, Is.Empty,
                "these features are selectable in a job but absent from the docs tables: " +
                string.Join(", ", undocumented));
        }

        [Test]
        public void DocumentedFeatures_AllExistInRegistry()
        {
            var registered = FeatureRegistry.Instance.GetRegisteredNames()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var keyColumn = DocumentedKeys().ToList();

            var unknown = keyColumn
                .Where(name => !registered.Contains(name) && !name.Contains('_'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.That(unknown, Is.Empty,
                "docs/features.md advertises names that no job can select: " + string.Join(", ", unknown));
        }

        [Test]
        public void ParameterizedFeatures_DocumentTheirOutputColumnName()
        {
            // The default output column name is the generated one, so a table that claims a
            // parameterized feature emits an unsuffixed column is wrong.
            var rows = Docs.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("| `", StringComparison.Ordinal))
                .ToList();

            foreach (var parameterized in new[] { "structural_imbalance", "liquidity_wall", "resistance", "liquidity_depletion", "replenishment_rate", "depth_persistence" })
            {
                var row = rows.FirstOrDefault(r => FirstCellName(r) == parameterized);
                Assert.That(row, Is.Not.Null, $"{parameterized} is missing from the docs table");

                var actual = FeatureRegistry.Instance.Create(parameterized).Name;
                Assert.That(row, Does.Contain($"`{actual}`"),
                    $"{parameterized} emits column '{actual}', which the docs table does not state");
            }
        }

        [Test]
        public void TradeFlow_IsDocumentedAsSignedNotional()
        {
            Assert.That(Docs, Does.Contain("Signed notional flow"),
                "trade_flow is signed notional (price x quantity, buys +/sells -); saying 'volume' " +
                "leads authors to write thresholds in the wrong unit");
        }

        /// <summary>
        /// Registration keys documented as the first cell of a table row.
        /// </summary>
        private static IEnumerable<string> DocumentedKeys()
        {
            return Docs.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("| `", StringComparison.Ordinal))
                .Select(FirstCellName)
                .Where(name => name != null);
        }

        private static string FirstCellName(string tableRow)
        {
            var first = tableRow.Split('|').Skip(1).FirstOrDefault()?.Trim();
            if (first == null || !first.StartsWith("`", StringComparison.Ordinal))
            {
                return null;
            }

            return first.Trim('`', ' ');
        }
    }
}
