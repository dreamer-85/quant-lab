using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.MarketState;

namespace QuantConnect.Research.Tests
{
    /// <summary>
    /// A quote states the size resting at the best bid and ask, which is top-of-book depth.
    /// These tests pin that a quote-only feed produces real depth and imbalance, because when it
    /// did not, every depth-derived feature read a flat zero and an imbalance condition could
    /// never fire without an L2 capture that historical feeds cannot supply anyway.
    ///
    /// The validation side of this behaviour is covered by
    /// <see cref="ValidationTests.Preflight_DepthFeatureWithQuotes_WarnsAboutTopOfBookOnly"/>.
    /// </summary>
    [TestFixture]
    public class QuoteDepthTests
    {
        private static readonly Symbol Btc = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
        private static readonly System.DateTime Ts = new System.DateTime(2026, 9, 27, 12, 0, 0, System.DateTimeKind.Utc);

        private static QuoteEvent Quote(decimal bidPrice, decimal bidSize, decimal askPrice, decimal askSize)
        {
            return MarketEventNormalizer.CreateQuote(Btc, Ts, bidPrice, bidSize, askPrice, askSize);
        }

        private static OrderBookSnapshotEvent Book(decimal bidPrice, decimal bidQty, decimal askPrice, decimal askQty)
        {
            return MarketEventNormalizer.CreateOrderBookSnapshot(
                Btc, Ts,
                new List<OrderBookLevel> { new OrderBookLevel { Price = bidPrice, Quantity = bidQty } },
                new List<OrderBookLevel> { new OrderBookLevel { Price = askPrice, Quantity = askQty } });
        }

        [Test]
        public void QuoteSeedsTopOfBookDepth()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(Quote(100m, 3m, 101m, 1m));

            Assert.That(state.BidDepth, Is.EqualTo(3m), "best bid resting size is bid depth");
            Assert.That(state.AskDepth, Is.EqualTo(1m), "best ask resting size is ask depth");
        }

        [Test]
        public void ImbalanceFromQuoteOnlyFeedIsSignCorrect()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(Quote(100m, 3m, 101m, 1m));

            // (3 - 1) / (3 + 1) = 0.5 -> more resting bid size than ask.
            Assert.That(state.DepthImbalance, Is.EqualTo(0.5m).Within(1e-9m));
        }

        [Test]
        public void ImbalanceIsZeroWhenBookHasNoSize()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(Quote(100m, 0m, 101m, 0m));

            Assert.That(state.DepthImbalance, Is.EqualTo(0m), "no size means no depth signal, not a false lean");
            Assert.That(state.BidPrice, Is.EqualTo(100m), "price is still usable without size");
        }

        [Test]
        public void LaterQuoteAtSamePriceReplacesSizeRatherThanAccumulating()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(Quote(100m, 3m, 101m, 1m));
            state.UpdateFromEvent(Quote(100m, 5m, 101m, 1m));

            // A quote is the venue telling us the current total at the top, not a delta.
            // Summing them would invent depth that does not exist.
            Assert.That(state.BidDepth, Is.EqualTo(5m));
            Assert.That(state.DepthImbalance, Is.EqualTo((5m - 1m) / 6m).Within(1e-9m));
        }

        [Test]
        public void MovedTopOfBookLeavesDeeperLevelsIntact()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                Btc, Ts,
                new List<OrderBookLevel>
                {
                    new OrderBookLevel { Price = 100m, Quantity = 3m },
                    new OrderBookLevel { Price = 99m, Quantity = 7m }
                },
                new List<OrderBookLevel> { new OrderBookLevel { Price = 101m, Quantity = 1m } }));

            state.UpdateFromEvent(Quote(100.5m, 2m, 101m, 1m));

            // The quoted level joins the top; the levels behind it keep the sizes L2 last
            // established. A quote is authoritative about the top and about nothing behind it,
            // so the 100 level stays at 3 until an L2 update corrects or removes it.
            Assert.That(state.BidLevels_List.Select(l => l.Price), Is.EqualTo(new[] { 100.5m, 100m, 99m }));
            Assert.That(state.BidDepth, Is.EqualTo(12m), "2 quoted + 3 + 7 from L2");
        }

        [Test]
        public void QuoteDropsLevelsBetterThanTheQuotedTop()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                Btc, Ts,
                new List<OrderBookLevel>
                {
                    new OrderBookLevel { Price = 100.5m, Quantity = 3m },
                    new OrderBookLevel { Price = 100m, Quantity = 7m }
                },
                new List<OrderBookLevel> { new OrderBookLevel { Price = 101m, Quantity = 1m } }));

            // The venue now quotes 100.2 as the best bid, so nothing may rest above it.
            state.UpdateFromEvent(Quote(100.2m, 4m, 101m, 1m));

            Assert.That(state.BidLevels_List.Select(l => l.Price), Is.EqualTo(new[] { 100.2m, 100m }),
                "a better-than-quoted level is stale and would corrupt the top");
            Assert.That(state.BidDepth, Is.EqualTo(11m));
        }

        [Test]
        public void QuoteWithNoSizeDoesNotEraseKnownDepth()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(Book(100m, 3m, 101m, 1m));

            // A size-less quote (some venues omit it) reports price only. Dropping the known
            // level here would make imbalance flicker to zero on every such tick.
            state.UpdateFromEvent(Quote(100m, 0m, 101m, 0m));

            Assert.That(state.BidDepth, Is.EqualTo(3m));
            Assert.That(state.DepthImbalance, Is.EqualTo(0.5m).Within(1e-9m));
        }

        [Test]
        public void OrderBookUpdateAfterQuoteKeepsBookAuthoritative()
        {
            var state = new MarketState(Btc);
            state.UpdateFromEvent(Quote(100m, 3m, 101m, 1m));
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookUpdate(
                Btc, Ts.AddSeconds(1), OrderBookSide.Bid, 100m, 4m, OrderBookUpdateAction.Modify));

            Assert.That(state.BidDepth, Is.EqualTo(4m), "an explicit L2 update supersedes the quoted size");
        }
    }
}
