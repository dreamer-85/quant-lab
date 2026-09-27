using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.LocalData;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Verifies checkpoint/resume: continuing a replay from a persisted market-state snapshot
    /// (boundary-aligned with state carried forward) yields output identical to an uninterrupted run.
    /// </summary>
    [TestFixture]
    public class ResumeTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");
        private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

        private static SyntheticStreamingEventSource MakeSource(int eventCount) =>
            new(eventCount, _start, _sBybit, eventStepNs: 10_000_000_000L, basePrice: 17000m, tradeFraction: 0.5, seed: 7);

        private static string StateKey(MarketState s) =>
            string.Join("|", s?.LastPrice.ToString("F4"), s?.BidPrice.ToString("F4"),
                s?.AskPrice.ToString("F4"), s?.Volume.ToString("F4"));

        private static (MarketState Final, Dictionary<DateTime, string> StateByTs, long EventsProcessed) RunReplay(
            ReplayConfiguration config, IEnumerable<MarketEvent> events)
        {
            var engine = new EventReplayEngine(config, MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            var stateByTs = new Dictionary<DateTime, string>();
            foreach (var r in engine.Replay(events))
                stateByTs[r.Timestamp] = StateKey(r.State as MarketState);
            return (engine.FinalState, stateByTs, engine.EventsProcessed);
        }

        private static MarketState StateAtAndEvents(DateTime target, ReplayConfiguration cfg, IEnumerable<MarketEvent> events,
            out long eventsProcessed)
        {
            var engine = new EventReplayEngine(cfg, MarketStateReconstructorFactory.Create(SecurityType.Crypto));
            MarketState at = null;
            eventsProcessed = 0;
            foreach (var r in engine.Replay(events))
            {
                if (r.Timestamp > target) break;
                at = r.State as MarketState;
                eventsProcessed = r.EventsProcessed;
            }
            return at;
        }

        private static DateTime ResumeNextObservationTime(DateTime resumeFrom, TimeSpan interval, DateTime gridAnchor)
        {
            var elapsed = resumeFrom - gridAnchor;
            var steps = (long)Math.Floor(elapsed.Ticks / (double)interval.Ticks) + 1;
            return gridAnchor + TimeSpan.FromTicks(steps * interval.Ticks);
        }

        [Test]
        public void Resume_SerializationRoundTrip_PreservesAllState()
        {
            var events = MakeSource(300);
            var cfg = new ReplayConfiguration
            {
                StartTime = _start, EndTime = _start.AddMinutes(55),
                Symbols = new() { _sBybit }, ObservationInterval = _interval,
                Reorder = ReorderMode.InOrderStreaming
            };
            var (final, _, _) = RunReplay(cfg, events.GetEvents(null, _sBybit));

            var json = MarketStateSerialization.ToJson(final);
            Assert.IsNotNull(json);
            var restored = MarketStateSerialization.FromJson(json);

            Assert.IsNotNull(restored);
            Assert.AreEqual(final.LastPrice, restored.LastPrice, "LastPrice");
            Assert.AreEqual(final.BidPrice, restored.BidPrice, "BidPrice");
            Assert.AreEqual(final.AskPrice, restored.AskPrice, "AskPrice");
            Assert.AreEqual(final.Volume, restored.Volume, "Volume");
            Assert.AreEqual(final.TradeCount, restored.TradeCount, "TradeCount");
            Assert.AreEqual(final.QuoteUpdateCount, restored.QuoteUpdateCount, "QuoteUpdateCount");
            Assert.AreEqual(final.RecentTrades.Count, restored.RecentTrades.Count, "RecentTrades count");
            Assert.AreEqual(final.RecentQuotes.Count, restored.RecentQuotes.Count, "RecentQuotes count");
        }

        [Test]
        public void Resume_SeededContinuation_MatchesFullRun_Tail()
        {
            var events = MakeSource(500);
            var end = _start.AddSeconds(500 * 10 + 60);
            var cfg = new ReplayConfiguration
            {
                StartTime = _start, EndTime = end,
                Symbols = new() { _sBybit }, ObservationInterval = _interval,
                Reorder = ReorderMode.InOrderStreaming
            };
            var stream = events.GetEvents(null, _sBybit);

            var (fullFinal, fullMap, fullEvents) = RunReplay(cfg, stream);

            // Capture state + event count at an interruption boundary.
            var boundary = _start.AddMinutes(3);
            var boundaryState = StateAtAndEvents(boundary, cfg, stream, out var headEvents);
            var checkpointState = MarketStateSerialization.FromJson(MarketStateSerialization.ToJson(boundaryState));

            // Resume as a seeded continuation from the boundary: the persisted state already contains
            // everything through the boundary, so the resumed run skips boundary events (StartTime just
            // past the boundary) and emits the next observation on the original grid.
            var resumeCfg = new ReplayConfiguration
            {
                StartTime = boundary.AddTicks(1), EndTime = end,
                Symbols = new() { _sBybit }, ObservationInterval = _interval,
                Reorder = ReorderMode.InOrderStreaming,
                InitialState = checkpointState,
                InitialEventsProcessed = headEvents,
                InitialNextObservationTime = ResumeNextObservationTime(boundary, _interval, cfg.StartTime)
            };
            var (resumeFinal, resumeMap, resumeEvents) = RunReplay(resumeCfg, stream);

            foreach (var kv in fullMap.Where(k => k.Key > boundary))
            {
                Assert.IsTrue(resumeMap.ContainsKey(kv.Key), $"resume missing {kv.Key:O}");
                Assert.AreEqual(kv.Value, resumeMap[kv.Key],
                    $"at {kv.Key:O}: full={kv.Value} resume={resumeMap[kv.Key]}");
            }

            Assert.AreEqual(fullEvents, resumeEvents, "total events processed");
            Assert.AreEqual(fullFinal.LastPrice, resumeFinal.LastPrice, "Final LastPrice");
            Assert.AreEqual(fullFinal.BidPrice, resumeFinal.BidPrice, "Final BidPrice");
            Assert.AreEqual(fullFinal.AskPrice, resumeFinal.AskPrice, "Final AskPrice");
            Assert.AreEqual(fullFinal.Volume, resumeFinal.Volume, "Final Volume");
        }

        [Test]
        public void Resume_ExecutorResume_HeadPlusTail_EqualsFull()
        {
            AssertResumeParity(_ => { });
        }

        [Test]
        public void Resume_FillForwardFalse_HeadPlusTail_EqualsFull()
        {
            // A resumed tail must not re-enable padding. The tail inherits the head's configuration
            // explicitly, so a run with padding off stays irregular across the boundary instead of
            // acquiring a uniform grid at the seam.
            AssertResumeParity(job => job.FillForward = false);
        }

        [Test]
        public void Resume_ObservationCap_HeadPlusTail_EqualsFull()
        {
            // The cap is a budget for the run, not for each segment. The head spends its share and the
            // tail gets only the remainder, so head + tail is still exactly the cap rather than
            // double-counting it at the seam.
            AssertResumeParity(job => job.MaxObservations = 10, expectedTotalObservations: 10);
        }

        /// <summary>
        /// Runs one resume scenario end to end and asserts the head plus the tail reproduce an
        /// uninterrupted run exactly. Shared by the default, no-padding and capped variants so all
        /// three prove the same parity property under different grid settings.
        /// </summary>
        private static void AssertResumeParity(Action<ResearchJob> configure, int? expectedTotalObservations = null)
        {
            var source = MakeSource(400);
            var end = _start.AddSeconds(400 * 10 + 60);
            var job = new ResearchJob
            {
                JobId = "resumejob",
                Symbols = new() { _sBybit.Value },
                Dataset = "crypto",
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = _start,
                EndTime = end,
                Resolution = Resolution.Minute,
                EventTypes = new() { MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = _interval,
                Features = new() { "mid_price", "spread", "trade_volume" },
                ExperimentName = "dry-run",
                OutputFormat = "csv",
                EnableCheckpointing = false,
                Reorder = ReorderMode.InOrderStreaming
            };
            configure(job);

            var rootA = Path.Combine(Path.GetTempPath(), "quantlab-resume-a-" + Guid.NewGuid().ToString("N")[..6]);
            var rootB = Path.Combine(Path.GetTempPath(), "quantlab-resume-b-" + Guid.NewGuid().ToString("N")[..6]);

            try
            {
                // Leg A: uninterrupted full run (no checkpointing), writing to root A.
                job.OutputLocation = rootA;
                var execFull = new LocalResearchExecutor(source, new LocalFileStore(rootA));
                var fullResult = execFull.Execute(job);
                Assert.IsTrue(fullResult.Succeeded, fullResult.Error);
                var fullPath = fullResult.OutputFiles.First(f => f.EndsWith(".csv"));
                var fullRows = ReadRows(fullPath);
                Assert.Greater(fullRows.Count, 0, "full run produced no rows");

                if (expectedTotalObservations.HasValue)
                {
                    Assert.AreEqual(expectedTotalObservations.Value, fullRows.Count,
                        "the cap must bound an uninterrupted run");
                }

                // Reference boundary snapshot from the SAME merged streams the executor consumes,
                // so the persisted state is consistent with the events the resume leg will process.
                var cfg = new ReplayConfiguration
                {
                    StartTime = _start, EndTime = end,
                    Symbols = new() { _sBybit }, ObservationInterval = _interval,
                    FillForward = job.FillForward, MaxObservations = job.MaxObservations,
                    Reorder = ReorderMode.InOrderStreaming
                };
                var stream = EventStreamMerger.Merge(source.GetEventStreams(job, _sBybit));
                var boundary = _start.AddMinutes(2);
                var boundaryState = StateAtAndEvents(boundary, cfg, stream, out var headEvents);

                // Seed an in-progress checkpoint at the exact path the executor's checkpoint manager uses.
                job.OutputLocation = rootB;
                job.EnableCheckpointing = true;
                var checkpointDir = Path.Combine(job.OutputLocation, job.JobId, "checkpoints");
                new ReplayCheckpointManager(new LocalFileStore(rootB), checkpointDir).Save(new ReplayCheckpoint
                {
                    JobId = job.JobId,
                    Symbol = _sBybit.Value,
                    ConfigurationHash = job.GetConfigurationHash(),
                    LastObservationTimestamp = boundary,
                    ObservationsWritten = fullRows.Count(r => DateTime.Parse(r.Timestamp) <= boundary),
                    EventsProcessed = headEvents,
                    Completed = false,
                    StateJson = MarketStateSerialization.ToJson(boundaryState)
                });

                // Leg B: resume — the executor continues from the checkpoint and writes only the tail.
                var execResume = new LocalResearchExecutor(source, new LocalFileStore(rootB));
                var resumeResult = execResume.Execute(job);
                Assert.IsTrue(resumeResult.Succeeded, resumeResult.Error);
                var tailPath = resumeResult.OutputFiles.First(f => f.EndsWith(".csv"));
                var tailRows = ReadRows(tailPath);

                Assert.AreEqual(fullResult.EventsProcessed, resumeResult.EventsProcessed, "total events");

                // The resume leg writes only the tail: full run minus the observations already written
                // before the checkpoint boundary.
                var headCount = fullRows.Count(r => DateTime.Parse(r.Timestamp) <= boundary);
                Assert.AreEqual(fullResult.ObservationsWritten - headCount, resumeResult.ObservationsWritten,
                    "rows written by resumed run");

                // Provenance: the resume leg starts at the first observation AFTER the boundary,
                // not from the beginning of the dataset.
                Assert.Greater(tailRows.Count, 0, "resumed run produced no rows");
                var fullAfter = fullRows.Where(r => DateTime.Parse(r.Timestamp) > boundary).ToList();
                Assert.AreEqual(fullAfter.Count, tailRows.Count, "tail row count");

                if (expectedTotalObservations.HasValue)
                {
                    Assert.AreEqual(expectedTotalObservations.Value, headCount + tailRows.Count,
                        "the cap must bound the head and the tail together");
                }

                // The tail must exactly equal the full run's rows at timestamps after the boundary.
                for (var i = 0; i < fullAfter.Count; i++)
                {
                    Assert.AreEqual(fullAfter[i].Timestamp, tailRows[i].Timestamp, $"timestamp row {i}");
                    Assert.AreEqual(fullAfter[i].Mid, tailRows[i].Mid, $"mid row {i}");
                    Assert.AreEqual(fullAfter[i].Spread, tailRows[i].Spread, $"spread row {i}");
                    Assert.AreEqual(fullAfter[i].Volume, tailRows[i].Volume, $"volume row {i}");
                }
            }
            finally
            {
                if (Directory.Exists(rootA)) Directory.Delete(rootA, true);
                if (Directory.Exists(rootB)) Directory.Delete(rootB, true);
            }
        }

        private static List<(string Timestamp, string Mid, string Spread, string Volume)> ReadRows(string path) =>
            ReadRowsByColumn(path, "timestamp", "mid_price", "spread", "trade_volume");

        /// <summary>
        /// Reads the named columns by header name rather than by index, so adding a column to the
        /// output cannot silently shift the values a test is comparing.
        /// </summary>
        private static List<(string Timestamp, string Mid, string Spread, string Volume)> ReadRowsByColumn(
            string path, string timestamp, string mid, string spread, string volume)
        {
            var lines = File.ReadAllLines(path);
            var header = lines[0].Split(',');
            var index = header
                .Select((name, i) => (Name: name.Trim(), Index: i))
                .ToDictionary(c => c.Name, c => c.Index, StringComparer.OrdinalIgnoreCase);

            foreach (var required in new[] { timestamp, mid, spread, volume })
            {
                Assert.IsTrue(index.ContainsKey(required), $"output is missing the '{required}' column: {lines[0]}");
            }

            return lines.Skip(1).Where(l => l.Length > 0).Select(line =>
            {
                var parts = line.Split(',');
                return (
                    parts[index[timestamp]],
                    parts[index[mid]],
                    parts[index[spread]],
                    parts[index[volume]]);
            }).ToList();
        }
    }
}