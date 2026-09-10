using QuantConnect.Research.Engine;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Storage;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class ResearchEnvironmentTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");
        private static readonly DateTime _end = DateTime.Parse("2022-12-13T00:10:00");

        private static TradeEvent Trade(DateTime time, decimal price, decimal quantity = 1, long seq = 0)
        {
            return new TradeEvent
            {
                Timestamp = time,
                Symbol = _symbol,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance { Venue = "bybit", Symbol = _symbol, AssetClass = SecurityType.Crypto, FeedType = "trade" },
                SequenceNumber = seq,
                Price = price,
                Quantity = quantity
            };
        }

        private sealed class FakeEventSource : IEventDataSource
        {
            private readonly List<MarketEvent> _events;
            public FakeEventSource(params MarketEvent[] events) { _events = events.ToList(); }
            public IEnumerable<MarketEvent> GetEvents(ResearchJob job, Symbol symbol) => _events;
        }

        private static ResearchJob BuildJob()
        {
            return new ResearchJob
            {
                JobId = "envroot",
                Dataset = "test",
                Symbols = new List<string> { "BTCUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = _start,
                EndTime = _end,
                EventTypes = new List<MarketEventType> { MarketEventType.Trade },
                ObservationInterval = TimeSpan.FromSeconds(60),
                Features = new List<string> { "mid_price" },
                ExperimentName = "audit",
                EnableCheckpointing = false
            };
        }

        [Test]
        public void ExplicitRootsWinOverEnvironmentAndDefaults()
        {
            var env = new ResearchEnvironment(
                dataRoot: "D",
                outputRoot: "O",
                cacheRoot: "C",
                tempRoot: "T");

            Assert.AreEqual("D", env.DataRoot);
            Assert.AreEqual("O", env.OutputRoot);
            Assert.AreEqual("C", env.CacheRoot);
            Assert.AreEqual("T", env.TempRoot);
        }

        [Test]
        public void EnvironmentVariablesAreHonoredWhenExplicitValuesAbsent()
        {
            var previousData = Environment.GetEnvironmentVariable(ResearchEnvironment.DataRootEnvVar);
            var previousOutput = Environment.GetEnvironmentVariable(ResearchEnvironment.OutputRootEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(ResearchEnvironment.DataRootEnvVar, "A_DATA");
                Environment.SetEnvironmentVariable(ResearchEnvironment.OutputRootEnvVar, "A_OUTPUT");

                var env = new ResearchEnvironment();

                Assert.AreEqual("A_DATA", env.DataRoot);
                Assert.AreEqual("A_OUTPUT", env.OutputRoot);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ResearchEnvironment.DataRootEnvVar, previousData);
                Environment.SetEnvironmentVariable(ResearchEnvironment.OutputRootEnvVar, previousOutput);
            }
        }

        [Test]
        public void DefaultsPreservePriorBehavior()
        {
            var prevOutput = Environment.GetEnvironmentVariable(ResearchEnvironment.OutputRootEnvVar);
            try
            {
                Environment.SetEnvironmentVariable(ResearchEnvironment.OutputRootEnvVar, null);
                var env = new ResearchEnvironment();

                Assert.AreEqual(Path.Combine(Path.GetTempPath(), "QuantLab"), env.OutputRoot);
                Assert.AreEqual(Globals.DataFolder, env.DataRoot);
                Assert.AreEqual(Path.GetTempPath(), env.TempRoot);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ResearchEnvironment.OutputRootEnvVar, prevOutput);
            }
        }

        [Test]
        public void ExecutorInfersOutputRootFromEnvironmentWhenJobLocationEmpty()
        {
            var job = BuildJob();
            job.OutputLocation = string.Empty;

            var env = new ResearchEnvironment(outputRoot: Path.Combine(Path.GetTempPath(), "QuantLab_EnvRootTest"));
            var executor = new LocalResearchExecutor(
                new FakeEventSource(Trade(_start, 17200, 1, 1), Trade(_start.AddMinutes(5), 17250, 2, 2)),
                environment: env);

            var result = executor.Execute(job);

            Assert.IsTrue(result.Succeeded);
            Assert.IsNotEmpty(result.OutputFiles);
            foreach (var output in result.OutputFiles)
            {
                var full = Path.GetFullPath(output);
                Assert.IsTrue(full.StartsWith(Path.GetFullPath(env.OutputRoot)), $"Expected output under env root, got {output}");
                Assert.IsTrue(full.Contains(job.JobId));
            }
        }

        [Test]
        public void ExecutorPrefersExplicitJobOutputLocation()
        {
            var job = BuildJob();
            var explicitRoot = Path.Combine(Path.GetTempPath(), "QuantLab_ExplicitRootTest");
            job.OutputLocation = explicitRoot;

            var executor = new LocalResearchExecutor(
                new FakeEventSource(Trade(_start, 17200, 1, 1), Trade(_start.AddMinutes(5), 17250, 2, 2)),
                environment: new ResearchEnvironment(outputRoot: Path.Combine(Path.GetTempPath(), "QuantLab_OtherRoot")));

            var result = executor.Execute(job);

            Assert.IsTrue(result.Succeeded);
            Assert.IsNotEmpty(result.OutputFiles);
            foreach (var output in result.OutputFiles)
            {
                Assert.IsTrue(Path.GetFullPath(output).StartsWith(Path.GetFullPath(explicitRoot)));
            }
        }
    }
}