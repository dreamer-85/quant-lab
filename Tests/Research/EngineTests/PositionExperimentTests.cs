using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Portfolio;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// A hypothesis says a condition held and what the price did next. That measures the market, not
    /// the strategy, because it never holds anything: there is no position, no entry or exit price,
    /// and no equity. These tests cover the experiment that closes that gap, from the signal to a
    /// filled entry, a held position, an exit, and an account that reconciles.
    ///
    /// What matters most:
    ///   * a position is opened on the signal and closed on a rule, not at the end of the data;
    ///   * the account obeys the balance sheet identity, so the equity curve and the trade log agree;
    ///   * the reported equity is the account's, not a sum of per-signal returns;
    ///   * a run that ends while still holding is liquidated, so results do not depend on truncation;
    ///   * misconfiguration is rejected loudly rather than trading at a default price.
    /// </summary>
    [TestFixture]
    public class PositionExperimentTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly DateTime _start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static Observation Obs(int step, decimal price)
        {
            var timestamp = _start.AddMinutes(step);
            var state = new MarketState(_symbol);
            var trade = new TradeEvent
            {
                Timestamp = timestamp,
                Symbol = _symbol,
                Price = price,
                Quantity = 1m
            };
            state.UpdateFromEvent(trade);

            return new Observation
            {
                Timestamp = timestamp,
                State = state,
                Events = new List<MarketEvent> { trade }
            };
        }

        private static FeatureResult Features(decimal imbalance) =>
            new() { Values = new Dictionary<string, decimal> { ["imbalance"] = imbalance } };

        private static PositionExperiment Run(
            IEnumerable<(int Step, decimal Price, decimal Imbalance)> series,
            Action<Dictionary<string, string>> configure = null)
        {
            var configuration = new Dictionary<string, string> { ["entry_condition"] = "imbalance < 0" };
            configure?.Invoke(configuration);

            var experiment = new PositionExperiment();
            experiment.Initialize(new ExperimentContext
            {
                Configuration = configuration,
                StartTime = _start,
                EndTime = _start.AddMinutes(1000)
            });

            foreach (var (step, price, imbalance) in series)
            {
                experiment.OnObservation(Obs(step, price), Features(imbalance));
            }

            experiment.Finalize();
            return experiment;
        }

        private static List<(int, decimal, decimal)> RisesThenFalls(
            int count, decimal from, decimal to, decimal stepSize)
        {
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < count; i++)
            {
                var price = from + (to - from) * i / (decimal)Math.Max(1, count - 1);
                series.Add((i, price, i % 2 == 0 ? -0.5m : 0.5m));
            }

            return series;
        }

        [Test]
        public void OpensAPosition_WhenTheEntryConditionHolds()
        {
            var experiment = Run(RisesThenFalls(60, 100m, 110m, 0m));

            Assert.That(experiment.Portfolio.Fills, Is.Not.Empty, "the alternating signal produced trades");
            Assert.That(experiment.Portfolio.ClosedTrades, Is.Not.Empty, "and completed round trips");
            Assert.That(experiment.Portfolio.ClosedTrades.All(t => t.Side == PositionSide.Long),
                "direction defaults to long");
        }

        [Test]
        public void TheAccountReconciles_SoEquityEqualsRealizedPlusUnrealized()
        {
            var experiment = Run(RisesThenFalls(200, 100m, 140m, 0m),
                c => c["fee_bps"] = "5");

            var portfolio = experiment.Portfolio;
            var identity = (portfolio.Equity - portfolio.StartingCash)
                - (portfolio.RealizedPnl + portfolio.UnrealizedPnl);

            Assert.That(identity, Is.EqualTo(0m).Within(0.0001m),
                "equity - starting cash must equal realized + unrealized, fees included");
        }

        [Test]
        public void TradeListSumsToTheChangeInEquity()
        {
            var experiment = Run(RisesThenFalls(300, 100m, 160m, 0m), c => c["fee_bps"] = "5");
            var portfolio = experiment.Portfolio;

            Assert.That(portfolio.ClosedTrades.Sum(t => t.Pnl),
                Is.EqualTo(portfolio.Equity - portfolio.StartingCash).Within(0.0001m),
                "every completed trade accounts for the whole change in equity");
        }

        [Test]
        public void RecordsEntryAndExit_OnTheSameTrade()
        {
            var experiment = Run(RisesThenFalls(120, 100m, 150m, 0m));
            var trade = experiment.Portfolio.ClosedTrades.First();

            Assert.That(trade.EntryTime, Is.Not.EqualTo(trade.ExitTime), "a trade has an entry and an exit");
            Assert.That(trade.ExitTime, Is.GreaterThan(trade.EntryTime), "the exit comes after the entry");
            Assert.That(trade.EntryPrice, Is.GreaterThan(0m), "the entry price was recorded");
            Assert.That(trade.ExitPrice, Is.GreaterThan(0m), "the exit price was recorded");
            Assert.That(trade.HoldingPeriod, Is.GreaterThan(TimeSpan.Zero));
        }

        [Test]
        public void MakesMoney_WhenATradeRidesARisingLong()
        {
            // Enter once, hold through a steady rise, exit on the time rule. A rising long must win,
            // otherwise the sign convention is inverted and every number downstream is backwards.
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 60; i++)
            {
                series.Add((i, 100m + i, i == 0 ? -0.5m : 0.5m));
            }

            var experiment = Run(series, c => c["holding_observations"] = "30");

            Assert.That(experiment.Portfolio.ClosedTrades, Has.Count.EqualTo(1), "entered once, held, exited once");
            var trade = experiment.Portfolio.ClosedTrades.Single();
            Assert.That(trade.IsWin, Is.True, "a long through a rising market wins");
            Assert.That(trade.Pnl, Is.GreaterThan(0m));
        }

        [Test]
        public void LosesMoney_WhenALongRidesAFallingMarket()
        {
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 60; i++)
            {
                series.Add((i, 100m - i, i == 0 ? -0.5m : 0.5m));
            }

            var experiment = Run(series, c => c["holding_observations"] = "30");

            Assert.That(experiment.Portfolio.ClosedTrades.Single().IsWin, Is.False,
                "a long through a falling market loses");
        }

        [Test]
        public void ShortMakesMoney_WhenTheMarketFalls()
        {
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 60; i++)
            {
                series.Add((i, 100m - i, i == 0 ? -0.5m : 0.5m));
            }

            var experiment = Run(series, c =>
            {
                c["direction"] = "short";
                c["holding_observations"] = "30";
            });

            var trade = experiment.Portfolio.ClosedTrades.Single();
            Assert.That(trade.Side, Is.EqualTo(PositionSide.Short), "the position was short");
            Assert.That(trade.IsWin, Is.True, "a short through a falling market wins");
        }

        [Test]
        public void FeesReduceTheResult()
        {
            var rising = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 60; i++)
            {
                rising.Add((i, 100m + i, i == 0 ? -0.5m : 0.5m));
            }

            var free = Run(rising, c => c["holding_observations"] = "30");
            var costed = Run(rising, c =>
            {
                c["holding_observations"] = "30";
                c["fee_bps"] = "20";
            });

            Assert.That(costed.Portfolio.ClosedTrades.Single().Pnl,
                Is.LessThan(free.Portfolio.ClosedTrades.Single().Pnl),
                "the same trade earns less once fees are charged");
            Assert.That(costed.Portfolio.TotalFees, Is.GreaterThan(0m), "and the fees were actually charged");
        }

        [Test]
        public void APositionStillOpenAtTheEnd_IsLiquidated()
        {
            // The signal fires once and never again, so without end-of-run liquidation the position
            // would never be realized and the result would depend on where the data was cut.
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 200; i++)
            {
                series.Add((i, 100m + i, i == 0 ? -0.5m : 0.5m));
            }

            var experiment = Run(series, c => c["holding_observations"] = "5000");

            Assert.That(experiment.Portfolio.Positions.Where(p => !p.IsFlat), Is.Empty,
                "nothing is left open when the run ends");
            Assert.That(experiment.Portfolio.UnrealizedPnl, Is.EqualTo(0m), "all profit is realized");
            Assert.That(experiment.Portfolio.ClosedTrades, Has.Count.EqualTo(1), "the open position became a trade");
        }

        [Test]
        public void PublishesEquityMetrics_NotJustSignalCounts()
        {
            var experiment = Run(RisesThenFalls(200, 100m, 150m, 0m), c => c["fee_bps"] = "5");
            var result = experiment.Finalize();

            foreach (var metric in new[]
                     {
                         "ending_equity", "total_return", "realized_pnl", "max_drawdown",
                         "position_count", "fill_count", "win_rate", "total_fees", "peak_equity"
                     })
            {
                Assert.That(result.Metrics.ContainsKey(metric), Is.True, $"expected a {metric} metric");
            }

            Assert.That((decimal)result.Metrics["ending_equity"],
                Is.EqualTo(experiment.Portfolio.Equity).Within(0.0001m),
                "the reported equity is the account's equity");
            Assert.That((decimal)result.Metrics["realized_pnl"],
                Is.EqualTo(experiment.Portfolio.RealizedPnl).Within(0.0001m));
            Assert.That((decimal)result.Metrics["max_drawdown"], Is.GreaterThanOrEqualTo(0m));
        }

        [Test]
        public void PublishesTradePortfolioAndFillTables_ForTheUserToRead()
        {
            var experiment = Run(RisesThenFalls(200, 100m, 150m, 0m));
            var result = experiment.Finalize();

            Assert.That(result.NamedRows.ContainsKey("trades"), Is.True, "a trade log is published");
            Assert.That(result.NamedRows.ContainsKey("portfolio"), Is.True, "an equity curve is published");
            Assert.That(result.NamedRows.ContainsKey("fills"), Is.True, "a fill log is published");

            var trade = result.NamedRows["trades"].First();
            foreach (var column in new[] { "entry_time", "exit_time", "entry_price", "exit_price", "pnl", "quantity" })
            {
                Assert.That(trade.ContainsKey(column), Is.True, $"a trade row carries {column}");
            }

            var point = result.NamedRows["portfolio"].First();
            foreach (var column in new[] { "timestamp", "equity", "cash", "drawdown" })
            {
                Assert.That(point.ContainsKey(column), Is.True, $"an equity point carries {column}");
            }
        }

        [Test]
        public void EquityCurveIsRecorded_EveryObservationWhileHolding()
        {
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 60; i++)
            {
                series.Add((i, 100m + i, i == 0 ? -0.5m : 0.5m));
            }

            var experiment = Run(series, c => c["holding_observations"] = "50");

            // Marked on entry and on every observation while the position is open, not only on exit.
            Assert.That(experiment.Portfolio.EquityCurve.Count, Is.GreaterThan(40),
                "the curve shows what the account was worth while in the market");
        }

        [Test]
        public void TheEquityCurveEndsAtTheSettledValue_NotMidPosition()
        {
            // Regression: the curve is marked before the final exit, so without a closing valuation it
            // ends while a position is still open and the curve disagrees with ending_equity by the
            // value of that position. The two must not be allowed to tell different stories.
            var series = new List<(int, decimal, decimal)>();
            for (var i = 0; i < 200; i++)
            {
                series.Add((i, 100m + (i % 3), i == 0 ? -0.5m : 0.5m));
            }

            var experiment = Run(series);
            var result = experiment.Finalize();
            var lastPoint = experiment.Portfolio.EquityCurve.Last();

            Assert.That(lastPoint.OpenPositions, Is.EqualTo(0), "the run ends flat");
            Assert.That(lastPoint.PositionValue, Is.EqualTo(0m), "so the last point carries no position value");
            Assert.That(lastPoint.Equity, Is.EqualTo(experiment.Portfolio.Equity),
                "the curve ends on the account's actual equity");
            Assert.That((decimal)result.Metrics["ending_equity"],
                Is.EqualTo(experiment.Portfolio.Equity).Within(0.0001m),
                "and the reported metric agrees with it");
        }

        [Test]
        public void RejectsAMissingEntryCondition()
        {
            var experiment = new PositionExperiment();

            var ex = Assert.Throws<FormatException>(() => experiment.Initialize(new ExperimentContext
            {
                Configuration = new Dictionary<string, string>()
            }));

            Assert.That(ex.Message, Does.Contain("entry_condition"));
        }

        [Test]
        public void RejectsAnUnknownDirection()
        {
            var experiment = new PositionExperiment();

            var ex = Assert.Throws<FormatException>(() => experiment.Initialize(new ExperimentContext
            {
                Configuration = new Dictionary<string, string>
                {
                    ["entry_condition"] = "imbalance < 0",
                    ["direction"] = "sideways"
                }
            }));

            Assert.That(ex.Message, Does.Contain("direction"));
        }

        [Test]
        public void RejectsAnUnknownPriceField_SoATypoCannotTradeAtTheWrongPrice()
        {
            var experiment = new PositionExperiment();

            var ex = Assert.Throws<FormatException>(() => experiment.Initialize(new ExperimentContext
            {
                Configuration = new Dictionary<string, string>
                {
                    ["entry_condition"] = "imbalance < 0",
                    ["price_field"] = "cloes"
                }
            }));

            Assert.That(ex.Message, Does.Contain("price_field"),
                "a misspelled price field is a configuration error, not a silent fallback to close");
        }

        [Test]
        public void IsReachableThroughTheFactory()
        {
            var job = new ResearchJob { ExperimentName = "position" };

            Assert.That(ExperimentFactory.Create(job), Is.InstanceOf<PositionExperiment>(),
                "a job can name the position experiment like any other");
        }
    }
}
