using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// The observation grid is the engine's clock, and a clock that jumps or runs backwards makes a
    /// backtest meaningless and a live result untrustworthy. These tests pin the properties the grid
    /// must hold, modelled on Lean's <c>DataTimeSynchronizer</c> + <c>SecurityCache</c>:
    ///
    ///   * one observation per grid point, gap-free, so a window of N periods is N x interval of time;
    ///   * observation timestamps never earlier than the newest data they carry;
    ///   * strictly increasing, uniform steps, on the grid anchored at StartTime;
    ///   * identical results whether the stream arrives pre-sorted (backtest) or incrementally (live).
    /// </summary>
    [TestFixture]
    public class ObservationGridTests
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

        private static ReplayConfiguration Config(TimeSpan endOffset, TimeSpan? interval,
            ReorderMode reorder = ReorderMode.FullSort, DateTime? gridAnchor = null)
        {
            return new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _start.Add(endOffset),
                Symbols = new List<Symbol> { _sBybit },
                Venues = new List<string> { "bybit" },
                EventTypes = new List<MarketEventType> { MarketEventType.Trade },
                ObservationInterval = interval,
                GridAnchor = gridAnchor,
                Reorder = reorder
            };
        }

        private static List<ReplayResult> Run(ReplayConfiguration config, IEnumerable<MarketEvent> events)
        {
            return new EventReplayEngine(config, new CLOBReconstructor()).Replay(events).ToList();
        }

        [Test]
        public void OffPhaseGridAnchor_EmitsPeriodsOnTheAnchoredPhase()
        {
            // An explicit anchor is the user's stated phase and need not coincide with the start point.
            // Periods advance by whole intervals from the first open bucket, and events are bucketed by
            // phase from the anchor: if the two disagree, every event is attributed to the wrong
            // window while the output still looks like a clean, evenly spaced grid.
            var anchor = _start.AddSeconds(30);
            var events = new List<MarketEvent> { Trade(_start.AddSeconds(40), 100m, 1) };

            var results = Run(Config(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), gridAnchor: anchor), events);

            Assert.That(results, Is.Not.Empty);
            foreach (var result in results)
            {
                var offset = (result.Timestamp - anchor).Ticks % TimeSpan.FromMinutes(1).Ticks;
                Assert.That(offset, Is.EqualTo(0), $"{result.Timestamp:O} sits on the anchored phase");
            }
            Assert.That(results.All(r => r.Timestamp >= anchor), Is.True,
                "no period may precede the anchor");
        }

        [Test]
        public void OffPhaseGridAnchor_AttributesEventsToTheirOwnPeriod()
        {
            // The trade lands at :40, inside the period that opens at the :30 anchor and closes at 01:30.
            // An observation is stamped at the end of the period it closes, so 01:30 must carry it --
            // the same convention the default grid uses, just on a different phase.
            var anchor = _start.AddSeconds(30);
            var tradeTime = _start.AddSeconds(40);
            var events = new List<MarketEvent> { Trade(tradeTime, 100m, 1) };

            var results = Run(Config(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), gridAnchor: anchor), events);
            var carrier = results.FirstOrDefault(r => r.Events.Any(e => e.Timestamp == tradeTime));

            Assert.That(carrier, Is.Not.Null, "the trade must be carried by some observation");
            Assert.That(carrier.Timestamp, Is.EqualTo(anchor.AddMinutes(1)),
                "the trade belongs to the period opened by the anchor, closed one interval later");
            Assert.That(carrier.Quality, Is.EqualTo(DataQuality.Fresh));
        }

        [Test]
        public void GridAnchorEqualToStartTime_MatchesTheDefaultGrid()
        {
            // Naming the start point explicitly must be indistinguishable from leaving it unset, or
            // every job that sets it would get a different result from the same job that omits it.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(2), 120m, 2)
            };

            var implicitGrid = Run(Config(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1)), events);
            var explicitGrid = Run(Config(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), gridAnchor: _start), events);

            Assert.That(explicitGrid.Select(r => r.Timestamp), Is.EqualTo(implicitGrid.Select(r => r.Timestamp)));
            Assert.That(explicitGrid.Select(r => r.Quality), Is.EqualTo(implicitGrid.Select(r => r.Quality)));
        }

        [Test]
        public void SparseData_ProducesGapFreeGrid()
        {
            // Two trades ten minutes apart on a one-minute grid. The old scheduler emitted two
            // observations and stamped the second 00:01; the grid owes eleven.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };

            var results = Run(Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)), events);

            Assert.That(results.Count, Is.EqualTo(11), "every grid point from start to end is emitted");
            for (var i = 0; i < results.Count; i++)
            {
                Assert.That(results[i].Timestamp, Is.EqualTo(_start.AddMinutes(i)),
                    $"observation {i} sits on the grid");
            }
        }

        [Test]
        public void ObservationIsNeverStampedBeforeTheDataItCarries()
        {
            // The core invariant. A grid point is assigned the events that fall on or before it, so
            // LastEventTimestamp can never be later than the observation's own timestamp.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(7), 170m, 2),
                Trade(_start.AddSeconds(37), 150m, 3)
            };

            var results = Run(Config(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)), events);

            Assert.That(results, Is.Not.Empty);
            foreach (var r in results)
            {
                if (r.LastEventTimestamp.HasValue)
                {
                    Assert.That(r.LastEventTimestamp.Value, Is.LessThanOrEqualTo(r.Timestamp),
                        $"observation at {r.Timestamp:O} must not carry data from {r.LastEventTimestamp:O}");
                }
            }
        }

        [Test]
        public void GridIsStrictlyIncreasingAndUniform()
        {
            var events = Enumerable.Range(0, 200)
                .Select(i => Trade(_start.AddSeconds(i * 17), 100m + i, i))
                .ToList();

            var results = Run(Config(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15)), events);

            Assert.That(results.Count, Is.GreaterThan(1));
            for (var i = 1; i < results.Count; i++)
            {
                var step = results[i].Timestamp - results[i - 1].Timestamp;
                Assert.That(step, Is.GreaterThan(TimeSpan.Zero), $"step {i} must be positive");
                Assert.That(step, Is.EqualTo(TimeSpan.FromSeconds(15)), $"step {i} must equal the interval");
            }
        }

        [Test]
        public void FilledObservationsAreStampedAsFilledAndCarryForwardState()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };

            var results = Run(Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)), events);

            var fresh = results.Where(r => r.Quality == DataQuality.Fresh).ToList();
            var filled = results.Where(r => r.Quality == DataQuality.Filled).ToList();

            Assert.That(fresh.Select(r => r.Timestamp),
                Is.EqualTo(new[] { _start, _start.AddMinutes(10) }),
                "only the two grid points that received a trade are fresh");
            Assert.That(filled.Count, Is.EqualTo(9));
            Assert.That(filled, Has.All.Property("State").Not.Null,
                "a filled observation still carries the previous state");
            Assert.That(filled.Select(r => ((QuantConnect.Research.Engine.MarketState.MarketState)r.State).LastPrice),
                Is.All.EqualTo(100m), "and carries it forward unchanged until the next real trade");
        }

        [Test]
        public void DataQualityDistinguishesStaleFromRealPeriods()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(2), 120m, 2)
            };

            var results = Run(Config(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1)), events);

            Assert.That(results.Select(r => r.Quality), Is.EqualTo(new[]
            {
                DataQuality.Fresh,   // 00:00 receives the 00:00 trade
                DataQuality.Filled,  // 00:01 nothing arrived
                DataQuality.Fresh    // 00:02 receives the 00:02 trade
            }));
        }

        [Test]
        public void FillForwardDisabled_DropsEmptyPeriodsButKeepsStampsCorrect()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };

            var config = Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
            config.FillForward = false;

            var results = Run(config, events);

            Assert.That(results.Count, Is.EqualTo(2), "empty grid points are dropped, not padded");
            Assert.That(results[0].Timestamp, Is.EqualTo(_start));
            Assert.That(results[1].Timestamp, Is.EqualTo(_start.AddMinutes(10)),
                "the surviving stamp is the grid point the event belongs to, not a stale earlier one");
        }

        [Test]
        public void BacktestAndLiveSchedulingAgreeOnIdenticalData()
        {
            // FullSort materializes and sorts (backtest); InOrderStreaming merges lazily (live).
            // Lean's guarantee is that the mode changes only the feed, never the result. This is the
            // test that makes the claim checkable.
            var events = Enumerable.Range(0, 500)
                .Select(i => Trade(_start.AddSeconds(i * 3), 100m + (i % 17), i))
                .ToList();

            var backtest = Run(Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)), events);
            var live = Run(Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), ReorderMode.InOrderStreaming), events);

            Assert.That(live.Count, Is.EqualTo(backtest.Count));
            Assert.That(live.Select(r => r.Timestamp), Is.EqualTo(backtest.Select(r => r.Timestamp)));
            Assert.That(live.Select(r => r.Quality), Is.EqualTo(backtest.Select(r => r.Quality)));
            Assert.That(live.Select(r => r.LastEventTimestamp), Is.EqualTo(backtest.Select(r => r.LastEventTimestamp)));
            Assert.That(live.Select(r => r.Events.Count), Is.EqualTo(backtest.Select(r => r.Events.Count)));
        }

        [Test]
        public void EventDrivenModeIsUnchanged()
        {
            // No interval: the data advances the clock and nothing is aggregated.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };

            var results = Run(Config(TimeSpan.FromMinutes(10), null), events);

            Assert.That(results.Count, Is.EqualTo(2));
            Assert.That(results[0].Timestamp, Is.EqualTo(_start));
            Assert.That(results[1].Timestamp, Is.EqualTo(_start.AddMinutes(10)));
            Assert.That(results.Select(r => r.Quality), Is.All.EqualTo(DataQuality.Fresh));
        }

        [Test]
        public void MaxObservationsBoundsAFillForwardRunaway()
        {
            // A 100ms grid over a day is ~864k rows for a two-trade feed. The cap must stop it
            // cleanly rather than materializing the whole thing.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddDays(1), 110m, 2)
            };

            var config = Config(TimeSpan.FromDays(1), TimeSpan.FromMilliseconds(100));
            config.MaxObservations = 25;

            var results = Run(config, events);

            Assert.That(results.Count, Is.EqualTo(25));
        }

        [Test]
        public void LateEventOnAnOutOfOrderStreamIsCounted()
        {
            // Reorder.InOrderStreaming trusts its input, so an out-of-order event is the source's
            // fault. It must be counted rather than silently misattributed to the current period.
            var events = new List<MarketEvent>
            {
                Trade(_start.AddMinutes(5), 150m, 1),
                Trade(_start.AddMinutes(1), 110m, 2)   // belongs to a grid point already emitted
            };

            var config = Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1), ReorderMode.InOrderStreaming);
            var engine = new EventReplayEngine(config, new CLOBReconstructor());
            var results = engine.Replay(events).ToList();

            Assert.That(engine.LateEvents, Is.EqualTo(1));
            Assert.That(results, Is.Not.Empty);
        }

        [Test]
        public void ConfigurationHashCoversTheNewGridKnobs()
        {
            // Fill-forward changes the row count, so two runs that differ only in it are different
            // research and must not share a hash.
            var a = Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
            var b = Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
            b.FillForward = false;

            Assert.That(a.GetConfigurationHash(), Is.Not.EqualTo(b.GetConfigurationHash()));

            b.FillForward = true;
            b.MaxObservations = 10;
            Assert.That(a.GetConfigurationHash(), Is.Not.EqualTo(b.GetConfigurationHash()));
        }

        [Test]
        public void StatisticsReportTheFillRatio()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(10), 110m, 2)
            };

            var config = Config(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
            var engine = new EventReplayEngine(config, new CLOBReconstructor());
            engine.Replay(events).ToList();

            var stats = engine.GetStatistics();

            Assert.That(stats.ObservationsEmitted, Is.EqualTo(11));
            Assert.That(stats.FilledObservations, Is.EqualTo(9));
            Assert.That(stats.LateEvents, Is.EqualTo(0));
            Assert.That(stats.FilledRatio, Is.EqualTo(9d / 11d).Within(1e-9));
        }
    }
}
