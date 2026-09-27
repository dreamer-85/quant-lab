using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// The live observation clock.
    ///
    /// Backtest and live share one replay path, so the only thing that can make them disagree is the
    /// frontier. In backtest it moves because events arrive; in live a quiet market produces none, so
    /// without a clock the two diverge precisely when the market is uninteresting — and the
    /// divergence is invisible, because a stalled run still produces valid-looking rows, just fewer
    /// of them and covering a different span of time.
    /// </summary>
    [TestFixture]
    public class ObservationClockTests
    {
        private static readonly Symbol _sBybit = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = DateTime.Parse("2022-12-13T00:00:00");

        private static TradeEvent Trade(DateTime time, decimal price, long seq) =>
            new()
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

        private static List<ReplayResult> Run(IEnumerable<MarketEvent> events, TimeSpan? interval,
            DateTime end, out EventReplayEngine engine, bool fillForward = true)
        {
            engine = new EventReplayEngine(new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = end,
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = interval,
                FillForward = fillForward
            }, MarketStateReconstructorFactory.Create(SecurityType.Crypto));

            return engine.Replay(events).ToList();
        }

        #region boundary arithmetic

        [Test]
        public void FirstBoundary_LandsOnTheGrid()
        {
            var origin = _start;

            Assert.AreEqual(_start.AddMinutes(1),
                ObservationClockStream.FirstBoundaryOnOrAfter(origin, TimeSpan.FromMinutes(1), _start.AddSeconds(1)));
            Assert.AreEqual(_start.AddMinutes(1),
                ObservationClockStream.FirstBoundaryOnOrAfter(origin, TimeSpan.FromMinutes(1), _start.AddMinutes(1)));
            Assert.AreEqual(_start.AddMinutes(2),
                ObservationClockStream.FirstBoundaryOnOrAfter(origin, TimeSpan.FromMinutes(1), _start.AddMinutes(1).AddTicks(1)));
            Assert.AreEqual(origin,
                ObservationClockStream.FirstBoundaryOnOrAfter(origin, TimeSpan.FromMinutes(1), _start.AddSeconds(-5)));
        }

        [Test]
        public void Boundaries_MatchTheEngineGrid()
        {
            // The clock's grid must be the engine's grid. If the two phases differ, a tick closes a
            // period the engine has not opened and every boundary run shows phantom late events.
            var origin = _start.AddSeconds(7);
            var interval = TimeSpan.FromSeconds(13);
            var from = _start;

            var clock = ObservationClockStream.FirstBoundaryOnOrAfter(origin, interval, from);
            var engineGrid = origin + TimeSpan.FromTicks(
                (long)Math.Ceiling((clock - origin).Ticks / (double)interval.Ticks) * interval.Ticks);

            Assert.AreEqual(clock, engineGrid,
                "the clock must tick on the points the engine's grid actually uses");
        }

        #endregion

        #region clock stream

        /// <summary>
        /// A clock that jumps forward a fixed step each time it is read, so the wait loop resolves
        /// without real time passing. A frozen clock would hang the stream, which is itself the
        /// contract: a tick is never emitted ahead of the clock.
        /// </summary>
        private sealed class TickingClock
        {
            private readonly TimeSpan _step;
            private DateTime _now;

            public TickingClock(DateTime start, TimeSpan step)
            {
                _now = start;
                _step = step;
            }

            public DateTime Now()
            {
                var current = _now;
                _now += _step;
                return current;
            }
        }

        private static readonly TimeSpan _fastPoll = TimeSpan.FromMilliseconds(1);

        [Test]
        public void Boundaries_EmitOneTickPerBoundaryInOrder()
        {
            var clock = new TickingClock(_start, TimeSpan.FromSeconds(30));
            var ticks = ObservationClockStream
                .Boundaries(_start, TimeSpan.FromMinutes(1), _start, _start.AddMinutes(5),
                    clock.Now, _fastPoll, CancellationToken.None)
                .Take(5)
                .ToList();

            Assert.AreEqual(5, ticks.Count);
            Assert.IsTrue(ticks.All(t => t is ClockTickEvent), "the stream must carry only ticks");
            Assert.AreEqual(
                new[]
                {
                    _start.AddMinutes(1), _start.AddMinutes(2), _start.AddMinutes(3),
                    _start.AddMinutes(4), _start.AddMinutes(5)
                },
                ticks.Select(t => t.Timestamp).ToArray());
        }

        [Test]
        public void Boundaries_NeverTickBeforeTheirTime()
        {
            // Emitting early would close a period that has not finished, so every later real event in
            // it would be reported as a source-contract violation the source never broke.
            var clock = new TickingClock(_start, TimeSpan.FromSeconds(10));
            var early = new List<string>();

            foreach (var tick in ObservationClockStream.Boundaries(
                         _start, TimeSpan.FromMinutes(1), _start, _start.AddMinutes(3),
                         clock.Now, _fastPoll, CancellationToken.None))
            {
                if (tick.Timestamp > clock.Now())
                {
                    early.Add($"{tick.Timestamp:O} vs {clock.Now():O}");
                }
            }

            Assert.IsEmpty(early, "a tick must never be emitted ahead of the clock");
        }

        [Test]
        public void Boundaries_StopAtTheDeadline()
        {
            var clock = new TickingClock(_start, TimeSpan.FromSeconds(30));
            var ticks = ObservationClockStream
                .Boundaries(_start, TimeSpan.FromMinutes(1), _start, _start.AddMinutes(10),
                    clock.Now, _fastPoll, CancellationToken.None)
                .ToList();

            Assert.IsNotEmpty(ticks);
            Assert.AreEqual(_start.AddMinutes(10), ticks.Last().Timestamp);
            Assert.IsFalse(ticks.Any(t => t.Timestamp > _start.AddMinutes(10)),
                "a live run must not tick past the deadline it was given");
        }

        [Test]
        public void Boundaries_StopOnCancellation()
        {
            var clock = new TickingClock(_start, TimeSpan.FromSeconds(30));
            using var cts = new CancellationTokenSource();
            var seen = 0;

            foreach (var _ in ObservationClockStream.Boundaries(
                         _start, TimeSpan.FromMinutes(1), _start, null,
                         clock.Now, _fastPoll, cts.Token))
            {
                seen++;
                if (seen == 3)
                {
                    cts.Cancel();
                }
            }

            Assert.AreEqual(3, seen, "cancellation must end the stream rather than wait out the deadline");
        }

        [Test]
        public void Boundaries_AreEmptyWithoutAGrid()
        {
            var ticks = ObservationClockStream
                .Boundaries(_start, TimeSpan.Zero, _start, _start.AddMinutes(5),
                    () => _start, _fastPoll, CancellationToken.None)
                .ToList();

            Assert.IsEmpty(ticks, "event-driven mode has no periods for a clock to keep moving");
        }

        [Test]
        public void Boundaries_DoNotPadUpToAPastStartTime()
        {
            // A live job's startTime is routinely in the past. Ticking from then would emit a padding
            // run for a period the run never observed.
            var start = _start.AddHours(5);
            var clock = new TickingClock(start, TimeSpan.FromSeconds(30));
            var ticks = ObservationClockStream
                .Boundaries(_start, TimeSpan.FromMinutes(1), start, start.AddMinutes(2),
                    clock.Now, _fastPoll, CancellationToken.None)
                .ToList();

            Assert.IsNotEmpty(ticks);
            Assert.IsFalse(ticks.Any(t => t.Timestamp < start), "no tick may predate the live start");
        }

        #endregion

        #region engine behaviour

        [Test]
        public void ATickIsNotMarketData()
        {
            // The tick exists only to move the frontier. If it ever became data it would inflate the
            // event count, appear in a strategy's event lists, and make a quiet period look measured.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                new ClockTickEvent(_start.AddMinutes(1))
            };

            var results = Run(events, TimeSpan.FromMinutes(1), _start.AddMinutes(1), out var engine);

            Assert.AreEqual(1, engine.EventsProcessed, "a tick must not be counted as a processed event");
            Assert.IsFalse(results.SelectMany(r => r.Events).Any(e => e is ClockTickEvent),
                "a tick must never appear in an observation's events");
            Assert.AreEqual(1, results[0].Events.Count, "the period holds only its real event");
            Assert.AreEqual(DataQuality.Fresh, results[0].Quality, "the period still holds the real trade");
        }

        [Test]
        public void ATickDoesNotCloseThePeriodStillInProgress()
        {
            // A tick at the boundary closes what came before it and leaves its own period open, so a
            // real event later in that period is not mistaken for a late one.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                new ClockTickEvent(_start.AddMinutes(1)),
                Trade(_start.AddMinutes(1).AddSeconds(30), 110m, 2)
            };

            var results = Run(events, TimeSpan.FromMinutes(1), _start.AddMinutes(2), out var engine);

            Assert.AreEqual(0, engine.GetStatistics().LateEvents, "a real event in an open period is not late");

            // 00:00 holds the first trade; the tick at 00:01 closes it. The trade at 00:01:30 belongs
            // to the 00:02 period, so 00:01 is padding and 00:02 is the measured one.
            Assert.AreEqual(DataQuality.Fresh, results[0].Quality);
            Assert.AreEqual(DataQuality.Filled, results[1].Quality, "the period a tick landed on is closed, not measured");
            Assert.AreEqual(DataQuality.Fresh, results[2].Quality, "the trade after the tick lands in the next period");
            Assert.AreEqual(1, results[2].Events.Count);
        }

        [Test]
        public void ATickBehindTheFrontierChangesNothing()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                new ClockTickEvent(_start),
                Trade(_start.AddSeconds(30), 105m, 2)
            };

            var results = Run(events, TimeSpan.FromMinutes(1), _start.AddMinutes(2), out var engine);

            Assert.AreEqual(0, engine.GetStatistics().LateEvents, "a clock going backwards is not a source violation");

            // A tick at 00:00 is a no-op: the 00:00 period is still open, so it changes nothing and
            // the two trades still produce the same three periods an un-ticked run would.
            Assert.AreEqual(3, results.Count);
            Assert.AreEqual(new[]
            {
                _start, _start.AddMinutes(1), _start.AddMinutes(2)
            }, results.Select(r => r.Timestamp).ToArray());
        }

        [Test]
        public void SilenceProducesPaddingPeriods()
        {
            // The point of the clock: a quiet market still advances the frontier, so the run covers
            // the time that passed rather than stopping at the last trade.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                new ClockTickEvent(_start.AddMinutes(1)),
                new ClockTickEvent(_start.AddMinutes(2)),
                new ClockTickEvent(_start.AddMinutes(3))
            };

            var results = Run(events, TimeSpan.FromMinutes(1), _start.AddMinutes(3), out var engine);

            Assert.AreEqual(
                new[] { _start, _start.AddMinutes(1), _start.AddMinutes(2), _start.AddMinutes(3) },
                results.Select(r => r.Timestamp).ToArray(),
                "each boundary closes one period, and the window end is padded to the requested end");
            Assert.AreEqual(DataQuality.Fresh, results[0].Quality, "the one period that measured something");
            Assert.IsTrue(results.Skip(1).All(r => r.Quality == DataQuality.Filled),
                "the quiet periods must be reported as padding, not as measurements");
            Assert.AreEqual(3, engine.GetStatistics().FilledObservations);
            Assert.AreEqual(1, engine.EventsProcessed, "three clock ticks and one trade: only the trade is data");
        }

        [Test]
        public void LiveClock_DeliversQuietPeriodsBeforeTheStreamEnds()
        {
            // The live property, stated as a test. A live run has no end of data, so tail padding can
            // never rescue it: if the quiet periods only appeared when the stream finished, a real
            // live run would emit nothing at all while the market is quiet. Consume lazily and check
            // that the period arrives while later events are still to come.
            var trades = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(20), 120m, 2)
            };

            var withClock = new List<MarketEvent>();
            for (var minute = 1; minute <= 20; minute++)
            {
                withClock.Add(new ClockTickEvent(_start.AddMinutes(minute)));
            }
            withClock.InsertRange(1, trades);

            var engine = new EventReplayEngine(new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _start.AddMinutes(20),
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = TimeSpan.FromMinutes(1)
            }, MarketStateReconstructorFactory.Create(SecurityType.Crypto));

            var seenBeforeTheSecondTrade = new List<ReplayResult>();
            foreach (var r in engine.Replay(withClock.OrderBy(e => e.Timestamp).ThenBy(e => e.SequenceNumber)))
            {
                if (r.Timestamp >= _start.AddMinutes(20))
                {
                    break;
                }

                seenBeforeTheSecondTrade.Add(r);
            }

            Assert.AreEqual(20, seenBeforeTheSecondTrade.Count,
                "the 20 quiet periods must be delivered while the run is still going, not at the end");
            Assert.IsTrue(seenBeforeTheSecondTrade.Skip(1).All(r => r.Quality == DataQuality.Filled),
                "and they must be reported as padding, not as measurements");
        }

        [Test]
        public void LiveClock_AndBacktest_DescribeTheSamePeriods()
        {
            // Same events, same grid, same qualities either way: the clock changes *when* periods are
            // published, never which ones exist or what they contain. Without that, a live run and a
            // backtest over the same data would be two different experiments.
            var trades = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                Trade(_start.AddMinutes(20), 120m, 2)
            };

            var withClock = new List<MarketEvent>();
            for (var minute = 1; minute <= 20; minute++)
            {
                withClock.Add(new ClockTickEvent(_start.AddMinutes(minute)));
            }
            withClock.InsertRange(1, trades);

            var interval = TimeSpan.FromMinutes(1);
            var end = _start.AddMinutes(20);
            var ticked = Run(withClock.OrderBy(e => e.Timestamp).ThenBy(e => e.SequenceNumber), interval, end, out _);
            var untiled = Run(trades, interval, end, out _);

            Assert.AreEqual(21, ticked.Count);
            Assert.AreEqual(untiled.Count, ticked.Count);
            Assert.AreEqual(
                untiled.Select(r => r.Timestamp).ToArray(),
                ticked.Select(r => r.Timestamp).ToArray(),
                "both paths must agree on which periods exist");
            Assert.AreEqual(
                untiled.Select(r => r.Quality).ToArray(),
                ticked.Select(r => r.Quality).ToArray(),
                "both paths must agree on which periods are measured and which are padding");
        }

        [Test]
        public void LiveClock_DoesNotResurrectPaddingWhenFillForwardIsOff()
        {
            // With padding off, the clock must not be a way to smuggle it back in: a quiet period is
            // simply absent, and only periods containing data exist in either mode.
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                new ClockTickEvent(_start.AddMinutes(1)),
                new ClockTickEvent(_start.AddMinutes(2)),
                Trade(_start.AddMinutes(3), 130m, 2)
            };

            var results = Run(events, TimeSpan.FromMinutes(1), _start.AddMinutes(3), out _, fillForward: false);

            Assert.AreEqual(2, results.Count, "only the two periods that contain data may exist");
            Assert.IsTrue(results.All(r => r.Quality == DataQuality.Fresh));
        }

        [Test]
        public void LiveClock_RespectsTheObservationCap()
        {
            var events = new List<MarketEvent> { Trade(_start, 100m, 1) };
            for (var minute = 1; minute <= 50; minute++)
            {
                events.Add(new ClockTickEvent(_start.AddMinutes(minute)));
            }

            var engine = new EventReplayEngine(new ReplayConfiguration
            {
                StartTime = _start,
                EndTime = _start.AddMinutes(50),
                Symbols = new List<Symbol> { _sBybit },
                ObservationInterval = TimeSpan.FromMinutes(1),
                MaxObservations = 5
            }, MarketStateReconstructorFactory.Create(SecurityType.Crypto));

            var results = engine.Replay(events.OrderBy(e => e.Timestamp)).ToList();

            Assert.AreEqual(5, results.Count, "a clock must not bypass the observation cap");
            Assert.IsTrue(engine.GetStatistics().Truncated, "stopping on the cap is truncation, not completion");
        }

        [Test]
        public void LiveClock_IsInertInEventDrivenMode()
        {
            var events = new List<MarketEvent>
            {
                Trade(_start, 100m, 1),
                new ClockTickEvent(_start.AddMinutes(1))
            };

            var results = Run(events, null, _start.AddMinutes(5), out var engine);

            Assert.AreEqual(1, results.Count, "event-driven mode has no grid, so a tick has nothing to close");
            Assert.AreEqual(1, engine.EventsProcessed);
        }

        #endregion
    }
}
