using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;
using QuantConnect.Research.Engine.Validation;
using MarketStateModel = QuantConnect.Research.Engine.MarketState.MarketState;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Guardrails for the trust properties of the observation clock.
    ///
    /// The defect these exist to catch is nasty precisely because it is invisible: the run succeeds,
    /// the rows look plausible, and every number is measured against a clock that does not match the
    /// data. Each check is verified positively (a real defect is reported) and negatively (a correct
    /// run stays silent), so a guardrail that cries wolf on healthy data gets caught here.
    /// </summary>
    [TestFixture]
    public class ObservationTrustTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");

        private static TradeEvent Trade(DateTime time, decimal price, long seq = 0)
        {
            return new TradeEvent
            {
                Timestamp = time,
                Symbol = _sBybit,
                AssetClass = SecurityType.Crypto,
                Provenance = new DataProvenance
                {
                    Venue = Market.Bybit,
                    Symbol = _sBybit,
                    AssetClass = SecurityType.Crypto,
                    FeedType = "trade"
                },
                SequenceNumber = seq,
                Price = price,
                Quantity = 1
            };
        }

        private static Observation Obs(DateTime at, DateTime? lastEvent, params MarketEvent[] events) =>
            new()
            {
                Timestamp = at,
                Events = events.ToList(),
                LastEventTimestamp = lastEvent,
                Quality = lastEvent.HasValue && lastEvent.Value <= at ? DataQuality.Fresh : DataQuality.Filled,
                State = new MarketStateModel { Symbol = _sBybit, LastPrice = 100m }
            };

        private static ValidationReport RunGrid(IEnumerable<Observation> observations)
        {
            var report = new ValidationReport();
            var check = new ObservationGridCheck();
            foreach (var o in observations)
            {
                check.OnObservation(o, null, report);
            }

            return report;
        }

        private static bool HasFinding(ValidationReport report, ValidationSeverity severity) =>
            report.Findings.Any(f => f.Check == ValidationCheckIds.Grid && f.Severity == severity);

        #region grid: observation clock integrity

        [Test]
        public void Grid_FiresWhenAnObservationCarriesDataFromItsOwnFuture()
        {
            // The exact shape of the historical scheduler defect: the period is stamped earlier than
            // the newest event folded into it.
            var report = RunGrid(new[]
            {
                Obs(_start, _start),
                Obs(_start.AddMinutes(1), _start.AddMinutes(10))
            });

            Assert.That(HasFinding(report, ValidationSeverity.Error), Is.True,
                "an observation reporting a future event is a clock defect and must be an error");
        }

        [Test]
        public void Grid_FiresWhenTimestampsDoNotAdvance()
        {
            var report = RunGrid(new[]
            {
                Obs(_start, _start),
                Obs(_start, _start)
            });

            Assert.That(HasFinding(report, ValidationSeverity.Error), Is.True,
                "a non-advancing clock makes every period-dependent feature undefined");
        }

        [Test]
        public void Grid_FiresWhenStepsAreIrregular()
        {
            var report = RunGrid(new[]
            {
                Obs(_start, _start),
                Obs(_start.AddMinutes(1), _start.AddMinutes(1)),
                Obs(_start.AddMinutes(5), _start.AddMinutes(5))
            });

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Grid), Is.True,
                "an irregular step means observations were dropped, so windows span unequal time");
        }

        [Test]
        public void Grid_WarnsWhenNewestEventMovesBackwards()
        {
            var report = RunGrid(new[]
            {
                Obs(_start, _start.AddMinutes(3)),
                Obs(_start.AddMinutes(4), _start.AddMinutes(1))
            });

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Grid
                && f.Message.Contains("backwards")), Is.True,
                "the source is delivering events out of order");
        }

        [Test]
        public void Grid_IsSilentOnACorrectRun()
        {
            // A real replay, not a hand-built one, so this proves the engine satisfies its own
            // invariant rather than that the check is lenient.
            var events = Enumerable.Range(0, 300)
                .Select(i => Trade(_start.AddSeconds(i * 7), 100m + i, i))
                .ToList();

            var engine = new EventReplayEngine(new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _start.AddMinutes(40),
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = TimeSpan.FromSeconds(5)
            }, new CLOBReconstructor());

            var report = new ValidationReport();
            var check = new ObservationGridCheck();
            var count = 0;
            foreach (var r in engine.Replay(events))
            {
                count++;
                check.OnObservation(new Observation
                {
                    Timestamp = r.Timestamp,
                    Events = r.Events,
                    LastEventTimestamp = r.LastEventTimestamp,
                    Quality = r.Quality,
                    State = r.State as MarketStateModel
                }, null, report);
            }

            Assert.That(count, Is.GreaterThan(100), "the run produced a real series");
            Assert.That(report.Findings.Where(f => f.Check == ValidationCheckIds.Grid), Is.Empty,
                $"a correct grid must be silent, got: {string.Join(" | ", report.Findings.Select(f => f.Message))}");
        }

        [Test]
        public void Grid_RejectsTheOriginalBugShape()
        {
            // Direct regression: the sparse-data scenario from the defect, asserted end to end
            // through the check rather than only through the engine.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };

            var engine = new EventReplayEngine(new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _start.AddMinutes(10),
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = TimeSpan.FromMinutes(1)
            }, new CLOBReconstructor());

            var report = new ValidationReport();
            var check = new ObservationGridCheck();
            foreach (var r in engine.Replay(events))
            {
                check.OnObservation(new Observation
                {
                    Timestamp = r.Timestamp,
                    Events = r.Events,
                    LastEventTimestamp = r.LastEventTimestamp,
                    Quality = r.Quality,
                    State = r.State as MarketStateModel
                }, null, report);
            }

            Assert.That(report.Findings, Is.Empty,
                "the fixed scheduler must satisfy every grid invariant on sparse data");
        }

        #endregion

        #region freshness: cadence vs data

        private static ValidationReport RunFreshness(IEnumerable<decimal> qualities, decimal maxAgeMs = 0m)
        {
            var report = new ValidationReport();
            var check = new DataFreshnessCheck();
            foreach (var q in qualities)
            {
                check.OnRow(new Dictionary<string, decimal>
                {
                    { ObservationTrustColumns.Quality, q },
                    { ObservationTrustColumns.DataAgeMs, maxAgeMs }
                }, report);
            }

            check.Complete(report);
            return report;
        }

        [Test]
        public void Freshness_ReportsWhenMostPeriodsArePadding()
        {
            var report = RunFreshness(Enumerable.Repeat((decimal)(int)DataQuality.Fresh, 2)
                .Concat(Enumerable.Repeat((decimal)(int)DataQuality.Filled, 18)));

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Freshness
                && f.Message.Contains("carried state forward")), Is.True,
                "90% padding means the interval is far finer than the data");
        }

        [Test]
        public void Freshness_ReportsMissingPeriods()
        {
            var report = RunFreshness(new[]
            {
                (decimal)(int)DataQuality.Fresh,
                (decimal)(int)DataQuality.Missing
            });

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Freshness
                && f.Message.Contains("no prior state")), Is.True);
        }

        [Test]
        public void Freshness_ReportsTheWorstDataAge()
        {
            var report = RunFreshness(Enumerable.Repeat((decimal)(int)DataQuality.Fresh, 10), 95_000m);

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Freshness
                && f.Message.Contains("behind the observation clock")), Is.True);
        }

        [Test]
        public void Freshness_IsSilentWhenDataKeepsUpWithTheGrid()
        {
            var report = RunFreshness(Enumerable.Repeat((decimal)(int)DataQuality.Fresh, 50));

            Assert.That(report.Findings.Where(f => f.Check == ValidationCheckIds.Freshness), Is.Empty,
                "a run whose data keeps up with the cadence must not be flagged");
        }

        [Test]
        public void Freshness_ResetClearsPerSymbolState()
        {
            var report = new ValidationReport();
            var check = new DataFreshnessCheck();
            check.OnRow(new Dictionary<string, decimal> { { ObservationTrustColumns.Quality, (int)DataQuality.Filled } }, report);
            check.Reset();
            check.Complete(report);

            Assert.That(report.Findings, Is.Empty, "state must not leak between symbols");
        }

        #endregion

        #region config preflight: cadence sanity

        private static ValidationReport Preflight(ResearchJob job)
        {
            var report = new ValidationReport();
            new JobConfigurationCheck().Preflight(job, report);
            return report;
        }

        [Test]
        public void Config_DoesNotGuessWhetherTheGridMatchesTheData()
        {
            // `resolution` describes the bar resolution the job wants, not the cadence of the events
            // it will replay, so a fine grid on minute bars is a legitimate configuration for a tick
            // replay. Whether the grid actually outruns the data can only be known once the data has
            // been seen, which is the `freshness` check's job. Warning here would be a guess, and a
            // guess that fires on valid jobs is worse than no warning at all.
            var job = new ResearchJob
            {
                StartTime = _start,
                EndTime = _start.AddDays(1),
                Resolution = Resolution.Minute,
                ObservationInterval = TimeSpan.FromMilliseconds(100)
            };

            var report = Preflight(job);

            Assert.That(report.Findings, Is.Empty,
                $"preflight must not infer data cadence from resolution, got: {string.Join(" | ", report.Findings.Select(f => f.Message))}");
        }

        [Test]
        public void Config_WarnsWhenTheProjectedObservationCountIsAbsurd()
        {
            var job = new ResearchJob
            {
                StartTime = _start,
                EndTime = _start.AddDays(365),
                Resolution = Resolution.Daily,
                ObservationInterval = TimeSpan.FromMilliseconds(100)
            };

            var report = Preflight(job);

            Assert.That(report.Findings.Any(f => f.Message.Contains("observation rows per symbol")), Is.True,
                "a year on a 100ms grid is a cost the job should confirm before spending it");
        }

        [Test]
        public void Config_IsSilentWhenTheGridMatchesTheData()
        {
            var job = new ResearchJob
            {
                StartTime = _start,
                EndTime = _start.AddHours(4),
                Resolution = Resolution.Minute,
                ObservationInterval = TimeSpan.FromMinutes(1)
            };

            var report = Preflight(job);

            Assert.That(report.Findings.Where(f => f.Message.Contains("observationInterval")), Is.Empty,
                $"a matched cadence must not be flagged, got: {string.Join(" | ", report.Findings.Select(f => f.Message))}");
        }

        [Test]
        public void Config_SaysNothingWhenRunningEventDriven()
        {
            var job = new ResearchJob
            {
                StartTime = _start,
                EndTime = _start.AddDays(30),
                Resolution = Resolution.Minute,
                ObservationInterval = null
            };

            var report = Preflight(job);

            Assert.That(report.Findings.Where(f => f.Message.Contains("observationInterval")), Is.Empty,
                "there is no grid to reason about in event-driven mode");
        }

        #endregion

        #region registration

        [Test]
        public void EveryTrustCheckIsRegistered_NotJustImplemented()
        {
            // A guardrail that is implemented, unit-tested and documented but never registered is
            // worse than no guardrail: it looks like coverage. Each id must appear in the default
            // enabled set a job actually runs with.
            var enabled = new ResearchValidator(ValidationOptions.FromJob(new ResearchJob
            {
                StartTime = _start,
                EndTime = _start.AddHours(1),
                ExperimentName = "dry-run"
            })).EnabledChecks;

            foreach (var id in new[]
                     {
                         ValidationCheckIds.Grid,
                         ValidationCheckIds.Freshness,
                         ValidationCheckIds.Coverage
                     })
            {
                Assert.That(enabled, Does.Contain(id), $"the {id} check is never registered, so it never runs");
            }
        }

        [Test]
        public void TrustChecksCanBeTurnedOff()
        {
            var options = ValidationOptions.FromJob(new ResearchJob
            {
                StartTime = _start,
                EndTime = _start.AddHours(1),
                ExperimentName = "dry-run",
                ExperimentConfig = new Dictionary<string, string>
                {
                    ["validation.checks"] = "config,grid"
                }
            });

            var enabled = new ResearchValidator(options).EnabledChecks;

            Assert.That(enabled, Does.Contain(ValidationCheckIds.Grid));
            Assert.That(enabled, Does.Not.Contain(ValidationCheckIds.Freshness));
            Assert.That(enabled, Does.Not.Contain(ValidationCheckIds.Coverage));
        }

        #endregion

        #region coverage

        [Test]
        public void Coverage_FiresOnOutOfOrderSourceEvents()
        {
            var report = new ValidationReport();
            var check = new ObservationCoverageCheck();
            check.Observe(_start, _start.AddMinutes(5), 6, lateEvents: 3, truncated: false);
            check.Complete(report);

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Coverage
                && f.Severity == ValidationSeverity.Error), Is.True,
                "late events mean some periods understate the data");
        }

        [Test]
        public void Coverage_IsSilentOnACleanRun()
        {
            var report = new ValidationReport();
            var check = new ObservationCoverageCheck();
            check.SetRequestedWindow(_start, _start.AddMinutes(5), TimeSpan.FromMinutes(1), padToEnd: true);
            check.Observe(_start, _start.AddMinutes(5), 6, lateEvents: 0, truncated: false);
            check.Complete(report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Coverage_SaysAPrefixIsNotAWindow()
        {
            var report = new ValidationReport();
            var check = new ObservationCoverageCheck();
            check.SetRequestedWindow(_start, _start.AddMinutes(60), TimeSpan.FromMinutes(1), padToEnd: true);
            check.Observe(_start, _start.AddMinutes(5), 6, lateEvents: 0, truncated: true);
            check.Complete(report);

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Coverage
                && f.Message.Contains("valid prefix")), Is.True,
                "a run that hit a limit must not be mistaken for a complete window");
        }

        [Test]
        public void Coverage_FiresWhenAQuietTailStopsShortOfAPaddedGrid()
        {
            // With fillForward on, the grid is guaranteed to reach the requested end, so stopping early
            // cannot be explained by the data running out.
            var report = new ValidationReport();
            var check = new ObservationCoverageCheck();
            check.SetRequestedWindow(_start, _start.AddMinutes(60), TimeSpan.FromMinutes(1), padToEnd: true);
            check.Observe(_start, _start.AddMinutes(5), 6, lateEvents: 0, truncated: false);
            check.Complete(report);

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Coverage
                && f.Message.Contains("a fraction of the requested window")), Is.True);
        }

        [Test]
        public void Coverage_DoesNotPenalizeAnIrregularSeriesEndingWithTheData()
        {
            // With fillForward off the series is irregular by design, so it ends where the data ends.
            // Reporting that as a short window would fire on every legitimate unpadded run.
            var report = new ValidationReport();
            var check = new ObservationCoverageCheck();
            check.SetRequestedWindow(_start, _start.AddMinutes(60), TimeSpan.FromMinutes(1), padToEnd: false);
            check.Observe(_start, _start.AddMinutes(5), 6, lateEvents: 0, truncated: false);
            check.Complete(report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Coverage_FiresOnAMissingHead()
        {
            var report = new ValidationReport();
            var check = new ObservationCoverageCheck();
            check.SetRequestedWindow(_start, _start.AddMinutes(5), TimeSpan.FromMinutes(1), padToEnd: true);
            check.Observe(_start.AddMinutes(2), _start.AddMinutes(5), 4, lateEvents: 0, truncated: false);
            check.Complete(report);

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Coverage
                && f.Message.Contains("after the requested start")), Is.True);
        }

        [Test]
        public void Coverage_FiresOnAnEmptyRun()
        {
            var report = new ValidationReport();
            new ObservationCoverageCheck().Complete(report);

            Assert.That(report.Findings.Any(f => f.Check == ValidationCheckIds.Coverage
                && f.Severity == ValidationSeverity.Error), Is.True,
                "an empty output file is a failure, not a result");
        }

        #endregion

        #region end to end: the trust columns reach the output

        [Test]
        public void Executor_PublishesQualityAndAgeColumns()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };
            var source = new ListSource(events);

            var root = Path.Combine(Path.GetTempPath(), "quantlab-trust-" + Guid.NewGuid().ToString("N")[..6]);
            try
            {
                var job = new ResearchJob
                {
                    JobId = "trust",
                    Dataset = "crypto",
                    AssetClass = "crypto",
                    Venue = "bybit",
                    Symbols = new List<string> { _sBybit.Value },
                    StartTime = _start,
                    EndTime = _start.AddMinutes(10),
                    Resolution = Resolution.Minute,
                    EventTypes = new List<MarketEventType> { MarketEventType.Trade },
                    ObservationInterval = TimeSpan.FromMinutes(1),
                    Features = new List<string> { "mid_price" },
                    ExperimentName = "dry-run",
                    OutputFormat = "csv",
                    Reorder = ReorderMode.InOrderStreaming
                };

                var result = new LocalResearchExecutor(source, new LocalFileStore(root)).Execute(job);
                Assert.That(result.Succeeded, Is.True, result.Error);

                var lines = File.ReadAllLines(result.OutputFiles.First(f => f.EndsWith(".csv")));
                var header = lines[0].Split(',');
                Assert.That(header, Does.Contain(ObservationTrustColumns.Quality));
                Assert.That(header, Does.Contain(ObservationTrustColumns.DataAgeMs));

                var qualityIndex = Array.IndexOf(header, ObservationTrustColumns.Quality);
                var ageIndex = Array.IndexOf(header, ObservationTrustColumns.DataAgeMs);
                var rows = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();

                Assert.That(rows.Count, Is.EqualTo(11));
                Assert.That(rows.Count(r => r[qualityIndex] == ((int)DataQuality.Fresh).ToString()), Is.EqualTo(2));
                Assert.That(rows.Count(r => r[qualityIndex] == ((int)DataQuality.Filled).ToString()), Is.EqualTo(9));

                // The padded periods sit between the two trades, so their age grows by one interval
                // each: the further past the last real event, the staler the carried state is. This is
                // what makes the padding visible instead of merely countable.
                var filledAges = rows
                    .Where(r => r[qualityIndex] == ((int)DataQuality.Filled).ToString())
                    .Select(r => decimal.Parse(r[ageIndex]))
                    .ToList();
                Assert.That(filledAges, Is.EqualTo(Enumerable.Range(1, 9).Select(i => i * 60_000m)),
                    "each padded period reports how far past the last real event it sits");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// A minimal in-memory source, so the end-to-end test does not depend on the filesystem
        /// layout or a network feed.
        /// </summary>
        private sealed class ListSource : IEventDataSource
        {
            private readonly List<MarketEvent> _events;

            public ListSource(List<MarketEvent> events) => _events = events;

            public IEnumerable<MarketEvent> GetEvents(ResearchJob job, Symbol symbol) => _events;
        }

        #endregion
    }
}
