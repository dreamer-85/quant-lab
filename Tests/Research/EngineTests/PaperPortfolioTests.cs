using QuantConnect.Research.Engine.Portfolio;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// A signal is not a trade, so the paper account is the part of the engine that turns conditions
    /// into positions and positions into money. These tests pin the properties that make those numbers
    /// trustworthy, in order of how badly a mistake would mislead:
    ///
    ///   * the balance sheet identity: equity - starting cash == realized + unrealized, always;
    ///   * a long that rises and a short that falls both make money, and the reverse both lose it;
    ///   * the entry price of a position is its weighted average, and trimming it does not rewrite
    ///     that basis;
    ///   * a reversal is two fills, not one, so the P&L of the trade that ended is not hidden;
    ///   * fees are charged on both sides and end up in the result;
    ///   * a run that ends holding something is liquidated, so return does not depend on where the
    ///     data happened to stop.
    ///
    /// The identity is the important one, and it is checked against randomly generated fill sequences
    /// rather than only hand-picked cases, because that is the only way to catch an off-by-one that
    /// shows up on the fourth partial exit and nowhere else.
    /// </summary>
    [TestFixture]
    public class PaperPortfolioTests
    {
        private static readonly DateTime _t0 = DateTime.Parse("2024-01-01T00:00:00");

        private static DateTime At(int minutes) => _t0.AddMinutes(minutes);

        private static decimal AssertClose(decimal actual, decimal expected, string because)
        {
            Assert.That(actual, Is.EqualTo(expected).Within(0.0000001m), because);
            return actual;
        }

        [Test]
        public void FlatAccount_StartsAtStartingCash()
        {
            var portfolio = new PaperPortfolio(50_000m);

            AssertClose(portfolio.Cash, 50_000m, "no fills, so cash is untouched");
            AssertClose(portfolio.Equity, 50_000m, "a flat account is worth its cash");
            AssertClose(portfolio.TotalReturn, 0m, "a flat account has made nothing");
            Assert.That(portfolio.Fills, Is.Empty);
            Assert.That(portfolio.ClosedTrades, Is.Empty);
        }

        [Test]
        public void OpeningALong_CostsCash_AndMarksNoProfitAtEntry()
        {
            var portfolio = new PaperPortfolio(100_000m);

            var fill = portfolio.Execute("BTCUSDT", 10m, 100m, At(0)).Single();

            Assert.That(fill.Reason, Is.EqualTo(FillReason.Open));
            AssertClose(portfolio.Cash, 100_000m - 1_000m, "buying 10 at 100 pays 1,000");
            AssertClose(portfolio.Equity, 100_000m, "at the entry price the position is worth what it cost");
            AssertClose(portfolio.UnrealizedPnl, 0m, "a fresh position has made nothing yet");
            Assert.That(portfolio.Positions.Single().Side, Is.EqualTo(PositionSide.Long));
        }

        [Test]
        public void LongThatRises_ClosesAtAProfit()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            portfolio.Mark(At(30), "BTCUSDT", 110m);

            AssertClose(portfolio.UnrealizedPnl, 100m, "10 units up 10 is 100 open profit");
            AssertClose(portfolio.Equity, 100_100m, "cash plus the marked position");

            var trade = portfolio.Execute("BTCUSDT", 0m, 110m, At(30)).Single();

            Assert.That(trade.Reason, Is.EqualTo(FillReason.Close));
            AssertClose(portfolio.ClosedTrades.Single().Pnl, 100m, "the round trip booked 100");
            AssertClose(portfolio.Equity, 100_100m, "the profit is now realized, not merely marked");
            AssertClose(portfolio.UnrealizedPnl, 0m, "nothing is open");
        }

        [Test]
        public void LongThatFalls_ClosesAtALoss()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            portfolio.Execute("BTCUSDT", 0m, 90m, At(30));

            var trade = portfolio.ClosedTrades.Single();
            Assert.That(trade.IsWin, Is.False, "a falling long loses");
            AssertClose(trade.Pnl, -100m, "10 units down 10 is a 100 loss");
            AssertClose(portfolio.Equity, 99_900m, "the loss came out of the account");
        }

        [Test]
        public void ShortThatFalls_MakesMoney_AndReceivesCashOnEntry()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", -10m, 100m, At(0));

            AssertClose(portfolio.Cash, 100_000m + 1_000m, "selling 10 at 100 receives 1,000");
            AssertClose(portfolio.Equity, 100_000m, "a short is worth its proceeds until it moves");
            Assert.That(portfolio.Positions.Single().Side, Is.EqualTo(PositionSide.Short));
            Assert.That(portfolio.PositionValue, Is.LessThan(0m), "a short is a liability");

            portfolio.Execute("BTCUSDT", 0m, 90m, At(30));

            var trade = portfolio.ClosedTrades.Single();
            Assert.That(trade.IsWin, Is.True, "a falling short wins");
            AssertClose(trade.Pnl, 100m, "10 units down 10 is 100");
            AssertClose(portfolio.Equity, 100_100m, "proceeds kept, repurchase paid");
        }

        [Test]
        public void ShortThatRises_LosesMoney()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", -10m, 100m, At(0));
            portfolio.Execute("BTCUSDT", 0m, 110m, At(30));

            AssertClose(portfolio.ClosedTrades.Single().Pnl, -100m, "a rising short loses");
            AssertClose(portfolio.Equity, 99_900m, "the loss came out of the account");
        }

        [Test]
        public void Reversal_ProducesTwoFills_SoTheOldTradeIsNotHidden()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));

            var fills = portfolio.Execute("BTCUSDT", -10m, 110m, At(30));

            Assert.That(fills, Has.Count.EqualTo(2), "flipping through flat is two legs, not one net fill");
            Assert.That(fills[0].Reason, Is.EqualTo(FillReason.Close), "the first leg ends the long");
            Assert.That(fills[1].Reason, Is.EqualTo(FillReason.Reverse), "the second opens the short");
            AssertClose(fills[0].RealizedPnl, 100m, "the long that just closed made 100");
            AssertClose(portfolio.ClosedTrades.Single().Pnl, 100m, "and that trade is on the books");
            Assert.That(portfolio.Positions.Single().Side, Is.EqualTo(PositionSide.Short));
        }

        [Test]
        public void IncreasingAPosition_ReaveragesTheEntry()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            portfolio.Execute("BTCUSDT", 20m, 120m, At(10));

            var position = portfolio.Positions.Single();
            AssertClose(position.AverageEntryPrice, 110m, "20 units at a blended 110");
            AssertClose(position.Quantity, 20m, "the position is the sum of both fills");

            portfolio.Execute("BTCUSDT", 0m, 110m, At(20));
            AssertClose(portfolio.ClosedTrades.Single().Pnl, 0m, "exiting at the blended entry breaks even");
        }

        [Test]
        public void TrimmingAPosition_LeavesTheEntryBasisAlone()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));

            var fills = portfolio.Execute("BTCUSDT", 4m, 120m, At(10));
            Assert.That(fills.Single().Reason, Is.EqualTo(FillReason.Reduce));

            var position = portfolio.Positions.Single();
            AssertClose(position.AverageEntryPrice, 100m, "what is left is still the original trade at 100");
            AssertClose(position.Quantity, 4m, "4 remain");
            Assert.That(portfolio.ClosedTrades, Is.Empty, "trimming is not a completed round trip");

            portfolio.Execute("BTCUSDT", 0m, 130m, At(20));
            // 6 units sold at 120 is +20 each, the remaining 4 at 130 is +30 each.
            AssertClose(portfolio.ClosedTrades.Single().Pnl, 120m + 120m, "both exits belong to one trade");
        }

        [Test]
        public void OrderToTheCurrentPosition_IsANoOp_AndChargesNoFee()
        {
            var portfolio = new PaperPortfolio(100_000m, feeBps: 10m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));

            var fills = portfolio.Execute("BTCUSDT", 10m, 100m, At(1));

            Assert.That(fills, Is.Empty, "re-asserting the same target trades nothing");
            Assert.That(portfolio.Fills, Has.Count.EqualTo(1), "no fill is invented");
        }

        [Test]
        public void Fees_AreChargedOnBothSides_AndReduceTheResult()
        {
            const decimal feeBps = 10m; // 0.1%
            var portfolio = new PaperPortfolio(100_000m, feeBps);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            portfolio.Execute("BTCUSDT", 0m, 100m, At(10));

            // 1,000 notional each way at 0.1% is 1 per side.
            AssertClose(portfolio.ClosedTrades.Single().Fees, 2m, "charged entering and exiting");
            AssertClose(portfolio.ClosedTrades.Single().Pnl, -2m, "a flat price round trip loses exactly the fees");
            AssertClose(portfolio.Equity, 99_998m, "the account is down the cost of trading");
        }

        [Test]
        public void Drawdown_MeasuresTheFallFromThePeak()
        {
            var portfolio = new PaperPortfolio(100_000m);
            // 100 units at 100 costs 10,000, leaving 90,000 cash and a position worth 10x the mark.
            portfolio.Execute("BTCUSDT", 100m, 100m, At(0));

            portfolio.Mark(At(1), "BTCUSDT", 200m);
            AssertClose(portfolio.Equity, 90_000m + 20_000m, "cash 90,000 plus 100 units at 200");
            var peak = portfolio.Equity;

            portfolio.Mark(At(2), "BTCUSDT", 150m);
            AssertClose(portfolio.Equity, 90_000m + 15_000m, "marked back down to 150");
            AssertClose(portfolio.MaxDrawdown, (peak - 105_000m) / peak, "a 5,000 fall from a 110,000 peak");

            portfolio.Mark(At(3), "BTCUSDT", 250m);
            AssertClose(portfolio.PeakEquity, 90_000m + 25_000m, "a new high lifts the peak");
            AssertClose(portfolio.MaxDrawdown, (peak - 105_000m) / peak, "the worst drawdown so far is remembered");
        }

        [Test]
        public void LiquidateAll_FlattensSoReturnDoesNotDependOnWhereDataStopped()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            portfolio.Execute("ETHUSDT", -5m, 200m, At(1));

            portfolio.LiquidateAll(At(10));

            Assert.That(portfolio.Positions.Where(p => p.Side != PositionSide.None), Is.Empty,
                "nothing is left open at the end of the run");
            Assert.That(portfolio.ClosedTrades, Has.Count.EqualTo(2), "both positions became completed trades");
            AssertClose(portfolio.UnrealizedPnl, 0m, "all profit and loss is realized");
        }

        [Test]
        public void LiquidateAll_ChargesTheSameFeeAsAnyOtherExit()
        {
            // A settle is a real trade. If it escapes the fee the account charges elsewhere, then
            // every run that ends holding a position reports a better result than it earned.
            var portfolio = new PaperPortfolio(100_000m, 10m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            var feesAfterEntry = portfolio.TotalFees;

            var fills = portfolio.LiquidateAll(At(10));

            Assert.That(fills, Has.Count.EqualTo(1), "settling one position produces one fill");
            Assert.That(portfolio.TotalFees, Is.GreaterThan(feesAfterEntry),
                "the closing fill is charged like any other fill");
            AssertClose(portfolio.ClosedTrades.Sum(t => t.Fees), portfolio.TotalFees,
                "the trade log and the fee total agree about the settle");
        }

        [Test]
        public void Sizing_CommitsAFractionOfEquity()
        {
            var portfolio = new PaperPortfolio(100_000m);

            var quantity = portfolio.SizeForFraction(0.5m, 200m);

            AssertClose(quantity, 250m, "half of 100,000 at 200 is 250 units");
            AssertClose(portfolio.SizeForFraction(0.5m, 200m, leverage: 2m), 500m, "2x notional doubles the units");
            AssertClose(portfolio.SizeForFraction(0m, 200m), 0m, "no fraction, no order");
        }

        [Test]
        public void EquityCurve_IsRecordedEveryMark_AndTracksDrawdown()
        {
            var portfolio = new PaperPortfolio(100_000m);
            portfolio.Execute("BTCUSDT", 10m, 100m, At(0));
            portfolio.Mark(At(1), "BTCUSDT", 100m);
            portfolio.Mark(At(2), "BTCUSDT", 90m);

            Assert.That(portfolio.EquityCurve, Has.Count.EqualTo(2), "one point per mark");
            var last = portfolio.EquityCurve.Last();
            AssertClose(last.Equity, 99_900m, "marked down 100");
            AssertClose(last.DrawdownPct, 0.001m, "10 of 100,000 is 0.1%");
            Assert.That(last.OpenPositions, Is.EqualTo(1));
        }

        [Test]
        public void EquityCurve_IsBoundedOnLongRuns()
        {
            var portfolio = new PaperPortfolio(100_000m, maxCurvePoints: 64);

            for (var i = 0; i < 5_000; i++)
            {
                portfolio.Mark(At(i), "BTCUSDT", 100m + (i % 7));
            }

            Assert.That(portfolio.EquityCurve.Count, Is.LessThanOrEqualTo(65),
                "the curve is compacted rather than growing one row per observation forever");
            Assert.That(portfolio.CurveDecimation, Is.GreaterThan(1), "and it says how far it folded");            AssertClose(portfolio.EquityCurve.Last().Equity, portfolio.Equity,
                "the newest point is always retained");
        }

        [Test]
        public void EquityIdentity_HoldsAcrossRandomFillSequences()
        {
            // The property that makes the equity curve and the trade list agree: for any sequence of
            // opens, adds, trims, reversals and closes, the change in equity is exactly realized plus
            // unrealized profit. Hand-picked cases miss off-by-ones in the fourth partial exit; random
            // walks find them.
            var random = new Random(20240101);

            for (var trial = 0; trial < 200; trial++)
            {
                var feeBps = decimal.Divide(trial % 20, (decimal)20);
                var portfolio = new PaperPortfolio(100_000m, feeBps);
                var current = 0m;

                for (var step = 0; step < 40; step++)
                {
                    var price = Math.Round(50m + (decimal)random.NextDouble() * 100m, 4);
                    var target = (decimal)(random.Next(-30, 31));
                    current = target;

                    portfolio.Execute("SYM", target, price, At(step));
                    portfolio.Mark(At(step), "SYM", price);

                    var identity = (portfolio.Equity - portfolio.StartingCash)
                        - (portfolio.RealizedPnl + portfolio.UnrealizedPnl);

                    AssertClose(identity, 0m,
                        $"trial {trial} step {step}: equity must equal realized + unrealized " +
                        $"(equity {portfolio.Equity}, realized {portfolio.RealizedPnl}, " +
                        $"unrealized {portfolio.UnrealizedPnl})");
                }
            }
        }

        [Test]
        public void EveryClosedTrade_ReconcilesToTheAccount()
        {
            // Each completed round trip must account for exactly the cash that moved, so the sum of
            // trade P&L can be traced back to the balance rather than merely looking plausible.
            var random = new Random(7);
            var portfolio = new PaperPortfolio(100_000m);

            for (var step = 0; step < 200; step++)
            {
                var price = Math.Round(100m + (decimal)random.NextDouble() * 20m, 4);
                portfolio.Execute("SYM", random.Next(-10, 11), price, At(step));
            }

            portfolio.LiquidateAll(At(500));

            var summed = portfolio.ClosedTrades.Sum(t => t.Pnl);
            AssertClose(summed, portfolio.Equity - portfolio.StartingCash,
                "total trade P&L is the total change in equity");
            AssertClose(portfolio.RealizedPnl, portfolio.Equity - portfolio.StartingCash,
                "and realized P&L says the same, since nothing is left open");
            AssertClose(portfolio.ClosedTrades.Sum(t => t.Fees), portfolio.TotalFees,
                "every fee belongs to a trade");
        }

        [Test]
        public void RejectsNonPositivePrice()
        {
            var portfolio = new PaperPortfolio();

            Assert.Throws<ArgumentOutOfRangeException>(() => portfolio.Execute("SYM", 1m, 0m, At(0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => portfolio.Mark(At(0), "SYM", -1m));
        }
    }
}
