using QuantConnect.Research.Engine.Experiments.Python;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Exercises the real pythonnet host against a locally installed CPython.
    /// Skipped (Assert.Ignore) when no compatible Python runtime is available so the core suite
    /// stays green on machines without Python.
    /// </summary>
    [TestFixture]
    public class PythonNetStrategyIntegrationTests
    {
        private const string Script =
            "class Strategy:\n" +
            "    def initialize(self, context):\n" +
            "        self.dataset = context.get('dataset', '')\n" +
            "        self.count = 0\n" +
            "\n" +
            "    def on_observation(self, observation, features):\n" +
            "        self.count += 1\n" +
            "        return {\n" +
            "            'close': float(observation['close']),\n" +
            "            'mid_feature': float(features.get('mid_price', 0.0)),\n" +
            "            'has_trades': bool(observation.get('trades')),\n" +
            "        }\n" +
            "\n" +
            "    def on_outcome(self, outcome):\n" +
            "        self.last_outcome = dict(outcome)\n" +
            "\n" +
            "    def finalize(self):\n" +
            "        return {\n" +
            "            'rows': [{'note': 'finalize', 'count': self.count}],\n" +
            "            'metrics': {'observations': self.count, 'dataset_len': len(self.dataset)},\n" +
            "            'metadata': {'strategy_version': '1.0'},\n" +
            "        }\n";

        private static string WriteScript()
        {
            var dir = Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "probe_strategy.py");
            File.WriteAllText(script, Script);
            return script;
        }

        private const string CustomNameScript =
            "class CustomStrategy:\n" +
            "    def on_observation(self, observation, features):\n" +
            "        return {'name': 'custom'}\n";

        private static string WriteCustomNameScript()
        {
            var dir = Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "custom_name_strategy.py");
            File.WriteAllText(script, CustomNameScript);
            return script;
        }

        private static void SkipWhenRuntimeUnavailable(StrategyScriptException ex)
        {
            if (ex.Message.Contains("Failed to initialize the Python runtime", StringComparison.Ordinal))
            {
                Assert.Ignore("Python runtime unavailable on this machine: " + ex.Message);
            }

            throw ex;
        }

        [Test]
        public void ResolveStrategyClassName_ReadsConfigClass_ElseDefaultsToStrategy()
        {
            Assert.That(
                PythonNetStrategyHost.ResolveStrategyClassName(new Dictionary<string, object>
                {
                    ["config"] = new Dictionary<string, object> { ["class"] = "MyStrategy" }
                }),
                Is.EqualTo("MyStrategy"));

            Assert.That(PythonNetStrategyHost.ResolveStrategyClassName(null), Is.EqualTo("Strategy"));
            Assert.That(
                PythonNetStrategyHost.ResolveStrategyClassName(new Dictionary<string, object>
                {
                    ["config"] = new Dictionary<string, object> { ["window"] = "20" }
                }),
                Is.EqualTo("Strategy"));
        }

        [Test]
        public void Host_RunsAllHooksOnRealRuntime()
        {
            var scriptPath = WriteScript();
            var host = new PythonNetStrategyHost();
            try
            {
                host.Initialize(scriptPath, new Dictionary<string, object>
                {
                    ["dataset"] = "unit-test",
                    ["config"] = new Dictionary<string, object> { ["threshold"] = "0.5" }
                });
            }
            catch (StrategyScriptException ex)
            {
                SkipWhenRuntimeUnavailable(ex);
            }

            try
            {
                var observation1 = new Dictionary<string, object>
                {
                    ["timestamp"] = "2024-01-01T00:00:00.0000000Z",
                    ["symbol"] = "BTCUSDT",
                    ["close"] = 42010.0,
                    ["trades"] = new List<object>
                    {
                        new Dictionary<string, object> { ["price"] = 42005.0, ["side"] = "buy" }
                    }
                };
                var observation2 = new Dictionary<string, object>
                {
                    ["timestamp"] = "2024-01-01T00:01:00.0000000Z",
                    ["symbol"] = "BTCUSDT",
                    ["close"] = 42090.0
                };
                var features = new Dictionary<string, object> { ["mid_price"] = 42007.5 };

                var row = host.OnObservation(observation1, features);
                Assert.That(row, Is.Not.Null);
                Assert.That(row["close"], Is.EqualTo(42010.0));
                Assert.That(row["mid_feature"], Is.EqualTo(42007.5));
                Assert.That(row["has_trades"], Is.True);

                host.OnObservation(observation2, features);
                host.OnOutcome(new Dictionary<string, object>
                {
                    ["reference_timestamp"] = "2024-01-01T00:01:00.0000000Z",
                    ["horizon_seconds"] = 60.0,
                    ["future_price"] = 42100.0
                });

                var finalized = host.Finalize();
                Assert.That(finalized.Metrics["observations"], Is.EqualTo(2L));
                Assert.That(finalized.Metrics["dataset_len"], Is.EqualTo(9L));
                Assert.That(finalized.Rows.Single()["count"], Is.EqualTo(2L));
                Assert.That(finalized.Metadata["strategy_version"], Is.EqualTo("1.0"));
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void Host_LoadsCustomClassNameFromConfig()
        {
            var script = WriteCustomNameScript();
            var host = new PythonNetStrategyHost();
            try
            {
                host.Initialize(script, new Dictionary<string, object>
                {
                    ["config"] = new Dictionary<string, object> { ["class"] = "CustomStrategy" }
                });
            }
            catch (StrategyScriptException ex)
            {
                SkipWhenRuntimeUnavailable(ex);
            }

            try
            {
                var row = host.OnObservation(
                    new Dictionary<string, object> { ["close"] = 1.0, ["timestamp"] = "2024-01-01T00:00:00.0000000Z" },
                    new Dictionary<string, object>());
                Assert.That(row, Is.Not.Null);
                Assert.That(row["name"], Is.EqualTo("custom"));
            }
            finally
            {
                host.Dispose();
            }
        }

        [Test]
        public void Host_ThrowsStrategyScriptExceptionWhenScriptMissing()
        {
            // Path validation happens before any Python runtime is touched.
            var host = new PythonNetStrategyHost();
            try
            {
                Assert.Throws<StrategyScriptException>(() =>
                    host.Initialize(Path.Combine(Path.GetTempPath(), "quantlab-tests", "does_not_exist.py"), new()));
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}