using QuantConnect.Research.Engine.Portfolio;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// A script that returns an order has handed control of a real position to a string, so these
    /// tests pin the two things a mistake would hide: a malformed order fails loudly instead of
    /// quietly doing nothing, and sizing has exactly one answer when both the script and the run
    /// have an opinion.
    /// </summary>
    [TestFixture]
    public class StrategyOrderTests
    {
        // The action arrives as a plain string, which also keeps System.Action free for the
        // lambdas Assert.Throws needs.
        private static Dictionary<string, object> Row(string action, object quantity = null, object price = null)
        {
            var order = new Dictionary<string, object> { ["action"] = action };
            if (quantity != null)
            {
                order["quantity"] = quantity;
            }

            if (price != null)
            {
                order["price"] = price;
            }

            return new Dictionary<string, object> { ["order"] = order };
        }

        [Test]
        public void ScriptSizeWinsOverAccountSize()
        {
            // The whole reason sizing is resolvable in one place: when a strategy manages its own
            // risk, the run's order size must not also apply and quietly double it.
            var order = StrategyOrder.FromRow(Row("buy", 2m));

            Assert.That(order.Quantity, Is.EqualTo(2m));
            Assert.That(order.ResolveTarget(currentQuantity: 0m, accountQuantity: 10m), Is.EqualTo(2m),
                "the script's size is the size, even when the account has an opinion");
        }

        [Test]
        public void OmittedSizeFallsBackToTheAccount()
        {
            var order = StrategyOrder.FromRow(Row("buy"));

            Assert.That(order.Quantity, Is.Null);
            Assert.That(order.ResolveTarget(currentQuantity: 0m, accountQuantity: 3m), Is.EqualTo(3m),
                "with no script size, the run's size applies");
        }

        [Test]
        public void CloseFlattensRegardlessOfSize()
        {
            var order = StrategyOrder.FromRow(Row("close"));

            Assert.That(order.ResolveTarget(currentQuantity: 12.5m, accountQuantity: 3m), Is.EqualTo(0m));
            Assert.That(order.ResolveTarget(currentQuantity: -12.5m, accountQuantity: 3m), Is.EqualTo(0m),
                "close flattens a short as well as a long");
        }

        [Test]
        public void BuyAddsToTheCurrentPositionInsteadOfReplacingIt()
        {
            var order = StrategyOrder.FromRow(Row("buy", 1m));

            Assert.That(order.ResolveTarget(currentQuantity: 3m, accountQuantity: 99m), Is.EqualTo(4m),
                "a repeated bare 'buy' accumulates rather than resetting to the order size");
        }

        [Test]
        public void SellSubtractsFromTheCurrentPosition()
        {
            var order = StrategyOrder.FromRow(Row("sell", 1m));

            Assert.That(order.ResolveTarget(currentQuantity: 3m, accountQuantity: 99m), Is.EqualTo(2m));
        }

        [Test]
        public void AZeroSizeMeansDoNotTradeThisTick()
        {
            // A size of zero is a script saying "nothing to do here", which is not the same as a
            // malformed order. Reporting it as a trade would inflate the trade count with a round
            // trip that never happened, so it resolves to no order at all.
            Assert.That(StrategyOrder.FromRow(Row("buy", 0m)), Is.Null);
            Assert.That(StrategyOrder.FromRow(Row("sell", 0m)), Is.Null);
        }

        [Test]
        public void AZeroSizeDoesNotCancelAClose()
        {
            // A close means "flat" and ignores size, so a script that sizes dynamically and
            // computes zero for a tick it does not want to act on must still be able to flatten.
            var order = StrategyOrder.FromRow(Row("close", 0m));

            Assert.That(order, Is.Not.Null);
            Assert.That(order.ResolveTarget(currentQuantity: 4m, accountQuantity: 5m), Is.EqualTo(0m));
        }

        [Test]
        public void ARejectedOrderFailsLoudly()
        {
            // Silently ignoring a malformed order is the worst outcome available: the run looks
            // like it traded, and the reason it did not is only visible in a field nobody reads.
            Assert.Throws<StrategyOrderFormatException>(() => StrategyOrder.FromRow(Row("hodl")));
            Assert.Throws<StrategyOrderFormatException>(() => StrategyOrder.FromRow(Row("buy", -1m)),
                "direction comes from the action, not from the sign of the size");
            Assert.Throws<StrategyOrderFormatException>(() => StrategyOrder.FromRow(Row("buy", "lots")),
                "a size that is not a number is an error, not a default");
            Assert.Throws<StrategyOrderFormatException>(() => StrategyOrder.FromRow(Row("buy", 1m, -5m)),
                "a negative limit price cannot trade");
        }

        [Test]
        public void AReasonIsCarriedThroughForTheTradeLog()
        {
            var order = StrategyOrder.FromRow(Row("buy", 1m));
            Assert.That(order.Reason, Is.Empty, "a reason is optional");

            var withReason = StrategyOrder.FromDict(new Dictionary<string, object>
            {
                ["action"] = "buy",
                ["quantity"] = 1m,
                ["reason"] = "book bid-heavy"
            });
            Assert.That(withReason.Reason, Is.EqualTo("book bid-heavy"));
        }

        [Test]
        public void AReturnedRowWithoutAnOrderIsNotAnOrder()
        {
            // The common case: a script that only wants to record something, or has nothing to
            // say this observation. That is not malformed and must not throw.
            Assert.That(StrategyOrder.FromRow(new Dictionary<string, object> { ["signal"] = 1 }), Is.Null);
            Assert.That(StrategyOrder.FromRow(null), Is.Null);
        }
    }
}
