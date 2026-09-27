using Parquet;
using QuantConnect.Research.Engine;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Storage;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class DataStoreWiringTests
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

        /// <summary>
        /// Minimal in-memory IDataStore used to prove the pipeline writes/reads through the store abstraction
        /// without any file system involvement. Keys are normalized to forward slashes.
        /// </summary>
        internal sealed class MemoryStore : IDataStore
        {
            private readonly Dictionary<string, byte[]> _files = new();

            private static string Key(string path) => path.Replace('\\', '/');

            public string StoreName => "memory";
            public int Count => _files.Count;

            public Stream OpenRead(string path)
            {
                lock (_files)
                {
                    return new MemoryStream(_files[Key(path)], writable: false);
                }
            }

            public Stream OpenWrite(string path)
            {
                lock (_files)
                {
                    var key = Key(path);
                    _files[key] = Array.Empty<byte>();
                    return new MemoryWriteStream(this, key);
                }
            }

            public bool Exists(string path)
            {
                lock (_files) { return _files.ContainsKey(Key(path)); }
            }

            public void Delete(string path)
            {
                lock (_files) { _files.Remove(Key(path)); }
            }

            public IEnumerable<string> List(string directoryPath)
            {
                lock (_files)
                {
                    var prefix = Key(directoryPath);
                    return _files.Keys
                        .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                        .OrderBy(k => k)
                        .ToList();
                }
            }

            public void CreateDirectory(string path)
            {
            }

            public long GetSize(string path)
            {
                lock (_files) { return _files.TryGetValue(Key(path), out var bytes) ? bytes.LongLength : 0; }
            }

            public byte[] ReadBytes(string path)
            {
                lock (_files) { return _files.TryGetValue(Key(path), out var bytes) ? bytes : null; }
            }

            private sealed class MemoryWriteStream : MemoryStream
            {
                private readonly MemoryStore _store;
                private readonly string _path;

                public MemoryWriteStream(MemoryStore store, string path)
                {
                    _store = store;
                    _path = path;
                }

                protected override void Dispose(bool disposing)
                {
                    base.Dispose(disposing);
                    if (disposing)
                    {
                        lock (_store._files)
                        {
                            _store._files[_path] = ToArray();
                        }
                    }
                }
            }
        }

        private static ResearchJob BuildJob()
        {
            return new ResearchJob
            {
                JobId = "storetest",
                Dataset = "test",
                Symbols = new List<string> { "BTCUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = _start,
                EndTime = _end,
                EventTypes = new List<MarketEventType> { MarketEventType.Trade },
                ObservationInterval = TimeSpan.FromSeconds(60),
                Features = new List<string> { "mid_price", "trade_volume" },
                ExperimentName = "audit",
                EnableCheckpointing = true,
                OutputLocation = ""
            };
        }

        [Test]
        public void ExecutorWritesThroughProvidedStore()
        {
            var store = new MemoryStore();
            var job = BuildJob();
            var executor = new LocalResearchExecutor(
                new FakeEventSource(
                    Trade(_start, 17200, 1, 1),
                    Trade(_start.AddMinutes(2), 17205, 2, 2),
                    Trade(_start.AddMinutes(4), 17210, 3, 3)),
                outputStore: store,
                environment: new ResearchEnvironment(outputRoot: "memroot"));

            var result = executor.Execute(job);

            Assert.IsTrue(result.Succeeded);
            Assert.IsNotEmpty(result.OutputFiles);
            foreach (var output in result.OutputFiles)
            {
                Assert.IsTrue(store.Exists(output), $"Store does not contain {output}");
            }

            var bytes = store.ReadBytes(result.OutputFiles[0]);
            Assert.IsNotNull(bytes);
            Assert.IsTrue(bytes.Length > 0);

            var (rowCount, columns) = ReadParquetSummary(store, result.OutputFiles[0]);
            Assert.AreEqual(result.ObservationsWritten, rowCount);
            Assert.Contains("mid_price", columns);
            Assert.Contains("trade_volume", columns);
        }

        [Test]
        public void CheckpointsPersistThroughStore()
        {
            var store = new MemoryStore();
            var job = BuildJob();
            var executor = new LocalResearchExecutor(
                new FakeEventSource(
                    Trade(_start, 17200, 1, 1),
                    Trade(_start.AddMinutes(2), 17205, 2, 2),
                    Trade(_start.AddMinutes(4), 17210, 3, 3)),
                outputStore: store,
                environment: new ResearchEnvironment(outputRoot: "memroot"));

            var first = executor.Execute(job);
            Assert.IsTrue(first.Succeeded);
            Assert.AreEqual(1, first.SymbolsProcessed);

            var second = executor.Execute(job);
            Assert.IsTrue(second.Succeeded);
            Assert.AreEqual(0, second.SymbolsProcessed, "Second run should reuse the completed symbol");
            Assert.AreEqual(1, second.SymbolsReused);

            // Roots are normalized to absolute, so the checkpoint key is rooted too. Building it
            // the same way keeps this test asserting "the checkpoint persisted under the output
            // root" instead of pinning one particular spelling of a relative path.
            var checkpointPath = Path.Combine(Path.GetFullPath("memroot"), "storetest", "checkpoints", "BTCUSDT.json");
            Assert.IsTrue(store.Exists(checkpointPath), $"Checkpoint not found: {checkpointPath}");
        }

        private static (long RowCount, List<string> Columns) ReadParquetSummary(MemoryStore store, string path)
        {
            using var stream = store.OpenRead(path);
            using var reader = ParquetReader.CreateAsync(stream, null, false, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            var columns = reader.Schema.GetDataFields().Select(f => f.Name).ToList();
            long total = 0;
            for (var i = 0; i < reader.RowGroupCount; i++)
            {
                using var group = reader.OpenRowGroupReader(i);
                total += group.RowCount;
            }
            return (total, columns);
        }
    }
}