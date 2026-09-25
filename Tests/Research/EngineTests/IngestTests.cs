using System.Net;
using System.Text;
using System.Text.Json;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Ingest.Bybit;
using QuantConnect.Research.Engine.Ingest.Binance;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class IngestTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        // ---------------------------------------------------------------------------
        // Normalizer
        // ---------------------------------------------------------------------------

        [Test]
        public void Normalizer_CreatesTradeEvent()
        {
            var ts = new DateTime(2022, 12, 13, 0, 0, 1, DateTimeKind.Utc);
            var evt = MarketEventNormalizer.CreateTrade(
                _symbol, ts, 16500.5m, 0.25m, TradeSide.Buy,
                eventId: "exec-1", sequenceNumber: 100,
                provenance: MarketEventNormalizer.CreateProvenance(_symbol, "bybit", "trade"));

            Assert.That(evt.EventType, Is.EqualTo(MarketEventType.Trade));
            Assert.That(evt.Price, Is.EqualTo(16500.5m));
            Assert.That(evt.Quantity, Is.EqualTo(0.25m));
            Assert.That(evt.Side, Is.EqualTo(TradeSide.Buy));
            Assert.That(evt.SequenceNumber, Is.EqualTo(100));
            Assert.That(evt.EventId, Is.EqualTo("exec-1"));
            Assert.That(evt.Provenance.Provider, Is.EqualTo("bybit"));
            Assert.That(evt.Provenance.FeedType, Is.EqualTo("trade"));
        }

        [Test]
        public void Normalizer_SortsBookLevelsBestFirst()
        {
            var ts = DateTime.UtcNow;
            var evt = MarketEventNormalizer.CreateOrderBookSnapshot(
                _symbol, ts,
                new[] { new OrderBookLevel { Price = 100, Quantity = 1 }, new OrderBookLevel { Price = 103, Quantity = 5 } },
                new[] { new OrderBookLevel { Price = 105, Quantity = 7 }, new OrderBookLevel { Price = 110, Quantity = 2 } });

            Assert.That(evt.Bids[0].Price, Is.EqualTo(103));
            Assert.That(evt.Asks[0].Price, Is.EqualTo(105));
            Assert.That(evt.BestBidPrice, Is.EqualTo(103));
            Assert.That(evt.BestAskPrice, Is.EqualTo(105));
        }

        [Test]
        public void Normalizer_CreatesBarEvent()
        {
            var ts = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
            var evt = MarketEventNormalizer.CreateBar(_symbol, ts, TimeSpan.FromMinutes(1), 10, 12, 9, 11, 100);

            Assert.That(evt.EventType, Is.EqualTo(MarketEventType.Bar));
            Assert.That(evt.Open, Is.EqualTo(10));
            Assert.That(evt.High, Is.EqualTo(12));
            Assert.That(evt.Close, Is.EqualTo(11));
            Assert.That(evt.Period, Is.EqualTo(TimeSpan.FromMinutes(1)));
        }

        // ---------------------------------------------------------------------------
        // Bybit JSON parsers (pure, no network)
        // ---------------------------------------------------------------------------

        [Test]
        public void BybitJson_ParsesKlineRow()
        {
            using var doc = JsonDocument.Parse("[\"1671422400000\",\"16850.50\",\"16860.00\",\"16840.10\",\"16855.20\",\"12.345\"]");
            Assert.That(BybitJson.TryParseKlineRow(doc.RootElement, out var kline), Is.True);
            Assert.That(kline.StartMs, Is.EqualTo(1671422400000));
            Assert.That(kline.Open, Is.EqualTo(16850.50m));
            Assert.That(kline.Close, Is.EqualTo(16855.20m));
            Assert.That(kline.Volume, Is.EqualTo(12.345m));
        }

        [Test]
        public void BybitJson_ParsesRecentTradeRestFields()
        {
            using var doc = JsonDocument.Parse(
                "{\"execId\":\"e1\",\"symbol\":\"BTCUSDT\",\"price\":\"30000.5\",\"size\":\"0.5\",\"side\":\"Sell\",\"time\":\"1671284736302\",\"isBlockTrade\":false}");
            Assert.That(BybitJson.TryParseTradeItem(doc.RootElement, out var trade), Is.True);
            Assert.That(trade.Price, Is.EqualTo(30000.5m));
            Assert.That(trade.Size, Is.EqualTo(0.5m));
            Assert.That(trade.TimeMs, Is.EqualTo(1671284736302));
            Assert.That(trade.Side, Is.EqualTo("Sell"));
            Assert.That(trade.EventId, Is.EqualTo("e1"));
        }

        [Test]
        public void BybitJson_ParsesWsPublicTradeSingleLetterFields()
        {
            using var doc = JsonDocument.Parse(
                "{\"T\":1671284736302,\"s\":\"BTCUSDT\",\"S\":\"Buy\",\"v\":\"1.0\",\"p\":\"30001\",\"L\":\"txn-9\",\"i\":\"match-9\",\"t\":\"tick\"}");
            Assert.That(BybitJson.TryParseTradeItem(doc.RootElement, out var trade), Is.True);
            Assert.That(trade.Price, Is.EqualTo(30001m));
            Assert.That(trade.Size, Is.EqualTo(1.0m));
            Assert.That(trade.TimeMs, Is.EqualTo(1671284736302));
            Assert.That(trade.Side, Is.EqualTo("Buy"));
            Assert.That(trade.EventId, Is.EqualTo("match-9"));
        }

        [Test]
        public void BybitJson_ParsesWsOrderBookFrame()
        {
            var frameJson =
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1671284736302," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"1\"],[\"29990\",\"2\"]],\"a\":[[\"30010\",\"3\"]],\"u\":1,\"seq\":1}}";
            Assert.That(BybitJson.TryParseWsFrame(frameJson, out var frame), Is.True);
            Assert.That(frame.Topic, Is.EqualTo("orderbook.50.BTCUSDT"));
            Assert.That(frame.TsMs, Is.EqualTo(1671284736302));
            Assert.That(frame.Bids.Count, Is.EqualTo(2));
            Assert.That(frame.Asks.Count, Is.EqualTo(1));
            Assert.That(frame.Bids[0].Price, Is.EqualTo(30000m));
            Assert.That(frame.Asks[0].Size, Is.EqualTo(3m));
        }

        [Test]
        public void BybitJson_ParsesWsPublicTradeFrame()
        {
            var frameJson =
                "{\"topic\":\"publicTrade.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1671284736302," +
                "\"data\":[{\"T\":1671284736302,\"s\":\"BTCUSDT\",\"S\":\"Buy\",\"v\":\"0.1\",\"p\":\"30000\",\"L\":\"a\",\"i\":\"b\"}]}";
            Assert.That(BybitJson.TryParseWsFrame(frameJson, out var frame), Is.True);
            Assert.That(frame.Trades.Count, Is.EqualTo(1));
            Assert.That(frame.Trades[0].Price, Is.EqualTo(30000m));
        }

        [Test]
        public void BybitJson_ParsesWsKlineFrame()
        {
            var frameJson =
                "{\"topic\":\"kline.1.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1671284736302," +
                "\"data\":[{\"start\":1671284640000,\"end\":1671284700000,\"interval\":\"1\",\"open\":\"30000\",\"high\":\"30005\",\"low\":\"29995\",\"close\":\"30002\",\"volume\":\"10\",\"turnover\":\"300020\"}]}";
            Assert.That(BybitJson.TryParseWsFrame(frameJson, out var frame), Is.True);
            Assert.That(frame.Klines.Count, Is.EqualTo(1));
            Assert.That(frame.Klines[0].Close, Is.EqualTo(30002m));
        }

        // ---------------------------------------------------------------------------
        // Binance JSON parsers (pure, no network)
        // ---------------------------------------------------------------------------

        [Test]
        public void BinanceJson_ParsesCombinedTradeFrame()
        {
            var frameJson =
                "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":1671284736302,\"s\":\"BTCUSDT\"," +
                "\"t\":1001,\"p\":\"30000.50\",\"q\":\"0.500\",\"T\":1671284736302,\"m\":false,\"M\":true}}";
            Assert.That(BinanceJson.TryParseWsFrame(frameJson, out var frame), Is.True);
            Assert.That(frame.Stream, Is.EqualTo("btcusdt@trade"));
            Assert.That(frame.Trade.HasValue, Is.True);
            Assert.That(frame.Trade.Value.Price, Is.EqualTo(30000.50m));
            Assert.That(frame.Trade.Value.Quantity, Is.EqualTo(0.500m));
            Assert.That(frame.Trade.Value.BuyerIsMaker, Is.False);
            Assert.That(frame.Trade.Value.EventId, Is.EqualTo("1001"));
        }

        [Test]
        public void BinanceJson_ParsesCombinedBookTickerFrame()
        {
            var frameJson =
                "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":400900217,\"s\":\"BTCUSDT\"," +
                "\"b\":\"29990.00\",\"B\":\"2.100\",\"a\":\"30010.00\",\"A\":\"3.200\",\"T\":1671284736302}}";
            Assert.That(BinanceJson.TryParseWsFrame(frameJson, out var frame), Is.True);
            Assert.That(frame.Stream, Is.EqualTo("btcusdt@bookTicker"));
            Assert.That(frame.Quote.HasValue, Is.True);
            Assert.That(frame.Quote.Value.Bid, Is.EqualTo(29990.00m));
            Assert.That(frame.Quote.Value.Ask, Is.EqualTo(30010.00m));
            Assert.That(frame.Quote.Value.BidSize, Is.EqualTo(2.1m));
            Assert.That(frame.Quote.Value.AskSize, Is.EqualTo(3.2m));
            Assert.That(frame.Quote.Value.UpdateId, Is.EqualTo(400900217));
        }

        [Test]
        public void BinanceJson_ParsesCombinedKlineFrame()
        {
            var frameJson =
                "{\"stream\":\"btcusdt@kline_1m\",\"data\":{\"e\":\"kline\",\"E\":1671284736302,\"s\":\"BTCUSDT\"," +
                "\"k\":{\"t\":1671284640000,\"T\":1671284699999,\"s\":\"BTCUSDT\",\"i\":\"1m\",\"o\":\"30000\"," +
                "\"h\":\"30050\",\"l\":\"29990\",\"c\":\"30020\",\"v\":\"12.500\",\"n\":100,\"x\":true}}}";
            Assert.That(BinanceJson.TryParseWsFrame(frameJson, out var frame), Is.True);
            Assert.That(frame.Stream, Is.EqualTo("btcusdt@kline_1m"));
            Assert.That(frame.Bar.HasValue, Is.True);
            Assert.That(frame.Bar.Value.OpenTimeMs, Is.EqualTo(1671284640000));
            Assert.That(frame.Bar.Value.Close, Is.EqualTo(30020m));
            Assert.That(frame.Bar.Value.Volume, Is.EqualTo(12.5m));
        }

        [Test]
        public void BinanceJson_IgnoresPongAndUnknownFrames()
        {
            Assert.That(BinanceJson.TryParseWsFrame("{\"id\":1,\"result\":{}}", out _), Is.False);
            Assert.That(BinanceJson.TryParseWsFrame("{\"stream\":\"btcusdt@trade\",\"data\":{}}", out _), Is.False);
        }

        // ---------------------------------------------------------------------------
        // Bybit API parameter mapping
        // ---------------------------------------------------------------------------

        [Test]
        public void BybitApi_MapsResolutionToInterval()
        {
            Assert.That(BybitApi.IntervalFor(Resolution.Minute), Is.EqualTo("1"));
            Assert.That(BybitApi.IntervalFor(Resolution.Hour), Is.EqualTo("60"));
            Assert.That(BybitApi.IntervalFor(Resolution.Daily), Is.EqualTo("D"));
        }

        [Test]
        public void BybitApi_ClampsDepthAndPageSize()
        {
            Assert.That(BybitApi.ClampDepth("1"), Is.EqualTo(1));
            Assert.That(BybitApi.ClampDepth("50"), Is.EqualTo(50));
            Assert.That(BybitApi.ClampDepth("7"), Is.EqualTo(1));
            Assert.That(BybitApi.ClampPageSize(2000), Is.EqualTo(1000));
        }

        // ---------------------------------------------------------------------------
        // Historical adapter (fake HTTP)
        // ---------------------------------------------------------------------------

        [Test]
        public void HistoricalAdapter_StreamsOrderedBarsAcrossPages()
        {
            var transport = new HttpStub();
            transport.AddKlines(
                "[\"1671408000000\",\"1\",\"2\",\"1\",\"2\",\"10\"]",
                "[\"1671408060000\",\"2\",\"3\",\"2\",\"3\",\"20\"]");

            var job = MinimalJob();
            job.Resolution = Resolution.Minute;
            job.Source = new JobDataSource { Mode = "historical", Provider = "bybit", PageSize = 1 };

            var adapter = new ExchangeDataAdapter(job, http: transport);
            var events = adapter.GetEvents(job, _symbol).ToList();

            Assert.That(events, Is.Not.Empty);
            Assert.That(events.All(e => e.EventType == MarketEventType.Bar), Is.True);
            Assert.That(((BarEvent)events[0]).Close, Is.EqualTo(2m));
            Assert.That(((BarEvent)events[1]).Timestamp, Is.GreaterThan(((BarEvent)events[0]).Timestamp));
            Assert.That(transport.Requests.Count, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void HistoricalAdapter_BarsRespectJobWindow()
        {
            var transport = new HttpStub();
            transport.AddKlines(
                "[\"1671408000000\",\"1\",\"2\",\"1\",\"2\",\"10\"]",
                "[\"1671408120000\",\"5\",\"6\",\"5\",\"6\",\"30\"]",
                "[\"1671408240000\",\"7\",\"8\",\"7\",\"8\",\"40\"]");

            var job = MinimalJob();
            job.Resolution = Resolution.Minute;
            job.StartTime = new DateTime(2022, 12, 19, 0, 2, 0, DateTimeKind.Utc); // skips the 00:00 bar
            job.EndTime = new DateTime(2022, 12, 19, 0, 5, 0, DateTimeKind.Utc);
            job.Source = new JobDataSource { Mode = "historical", Provider = "bybit" };

            var adapter = new ExchangeDataAdapter(job, http: transport);
            var events = adapter.GetEvents(job, _symbol).ToList();

            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That(((BarEvent)events[0]).Timestamp, Is.EqualTo(new DateTime(2022, 12, 19, 0, 2, 0, DateTimeKind.Utc)));
            Assert.That(((BarEvent)events[1]).Timestamp, Is.EqualTo(new DateTime(2022, 12, 19, 0, 4, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void HistoricalAdapter_ReturnsEmptyForDistantTradeReachback()
        {
            var transport = new HttpStub();
            transport.SetRecentTrades("{\"retCode\":0,\"result\":{\"category\":\"spot\",\"symbol\":\"BTCUSDT\",\"list\":[]}}");

            var job = MinimalJob();
            job.EventTypes = new List<MarketEventType> { MarketEventType.Trade };
            job.StartTime = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
            job.EndTime = new DateTime(2022, 12, 13, 0, 5, 0, DateTimeKind.Utc);
            job.Source = new JobDataSource { Mode = "historical", Provider = "bybit" };

            var adapter = new ExchangeDataAdapter(job, http: transport);
            var events = adapter.GetEvents(job, _symbol).ToList();

            Assert.That(events, Is.Empty);
        }

        [Test]
        public void HistoricalAdapter_ExecutorRunsEndToEnd()
        {
            var transport = new HttpStub();
            transport.AddKlines(
                "[\"1671408000000\",\"30000\",\"30050\",\"29990\",\"30020\",\"10\"]",
                "[\"1671408060000\",\"30020\",\"30100\",\"30010\",\"30090\",\"15\"]",
                "[\"1671408120000\",\"30090\",\"30150\",\"30080\",\"30110\",\"20\"]");

            var job = MinimalJob();
            job.Resolution = Resolution.Minute;
            job.OutputLocation = Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
            job.Source = new JobDataSource { Mode = "historical", Provider = "bybit" };

            var adapter = new ExchangeDataAdapter(job, http: transport);
            var executor = new LocalResearchExecutor(adapter, environment: new QuantConnect.Research.Engine.ResearchEnvironment(outputRoot: job.OutputLocation));
            var result = executor.Execute(job);

            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.EventsProcessed, Is.EqualTo(3));
            Assert.That(result.ObservationsWritten, Is.GreaterThanOrEqualTo(1));
        }

        // ---------------------------------------------------------------------------
        // Live adapter (fake WebSocket)
        // ---------------------------------------------------------------------------

        [Test]
        public void LiveAdapter_StreamsTradesQuotesAndBars()
        {
            var ws = new WsStub(
                "{\"topic\":\"publicTrade.BTCUSDT\",\"type\":\"snapshot\",\"ts\":" + Now + "," +
                "\"data\":[{\"T\":" + Now + ",\"s\":\"BTCUSDT\",\"S\":\"Buy\",\"v\":\"0.1\",\"p\":\"30000\"}]}",
                "{\"topic\":\"orderbook.1.BTCUSDT\",\"type\":\"snapshot\",\"ts\":" + Now + "," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"29990\",\"2\"]],\"a\":[[\"30010\",\"3\"]]}}",
                "{\"topic\":\"kline.1.BTCUSDT\",\"type\":\"snapshot\",\"ts\":" + Now + "," +
                "\"data\":[{\"start\":1671284640000,\"end\":1671284700000,\"interval\":\"1\",\"open\":\"30000\",\"high\":\"30005\",\"low\":\"29995\",\"close\":\"30002\",\"volume\":\"10\"}]}");

            var job = MinimalJob();
            job.Resolution = Resolution.Minute;
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            job.EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote, MarketEventType.Bar };
            job.Source = new JobDataSource { Mode = "live", Provider = "bybit", LiveDurationSeconds = 3 };

            var adapter = new ExchangeDataAdapter(job, wsFactory: () => ws);
            var events = EventStreamMerger.Merge(adapter.GetEventStreams(job, _symbol)).ToList();

            Assert.That(events, Is.Not.Empty);
            Assert.That(events.Any(e => e.EventType == MarketEventType.Trade), Is.True);
            Assert.That(events.Any(e => e.EventType == MarketEventType.Quote), Is.True);
            Assert.That(events.Any(e => e.EventType == MarketEventType.Bar), Is.True);
            Assert.That(((QuoteEvent)events.First(e => e.EventType == MarketEventType.Quote)).BidPrice, Is.EqualTo(29990m));
            Assert.That(ws.Closed, Is.EqualTo(1));
        }

        [Test]
        public void LiveAdapter_BybitL1SnapshotsReplaceBookInsteadOfAccumulating()
        {
            var now = Now;
            var ws = new WsStub(
                "{\"topic\":\"orderbook.1.BTCUSDT\",\"type\":\"snapshot\",\"ts\":" + now + "," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"29990\",\"2\"]],\"a\":[[\"30010\",\"3\"]]}}",
                "{\"topic\":\"orderbook.1.BTCUSDT\",\"type\":\"snapshot\",\"ts\":" + (now + 100) + "," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"29980\",\"1\"]],\"a\":[[\"29990\",\"5\"]]}}");

            var job = MinimalJob();
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            job.EventTypes = new List<MarketEventType> { MarketEventType.Quote };
            job.Source = new JobDataSource { Mode = "live", Provider = "bybit", LiveDurationSeconds = 3 };

            var adapter = new ExchangeDataAdapter(job, wsFactory: () => ws);
            var quotes = EventStreamMerger.Merge(adapter.GetEventStreams(job, _symbol))
                .OfType<QuoteEvent>()
                .ToList();

            Assert.That(quotes.Count, Is.EqualTo(2));
            Assert.That(quotes[0].BidPrice, Is.EqualTo(29990m));
            Assert.That(quotes[0].AskPrice, Is.EqualTo(30010m));
            // A renewed depth-1 snapshot replaces the whole book: the stale 29990 best bid must
            // not survive into the next quote (which would cross the book and invert the spread).
            Assert.That(quotes[1].BidPrice, Is.EqualTo(29980m));
            Assert.That(quotes[1].AskPrice, Is.EqualTo(29990m));
            Assert.That(quotes[1].BidPrice, Is.LessThan(quotes[1].AskPrice));
        }

        [Test]
        public void LiveAdapter_RequiresInOrderStreaming()
        {
            var job = MinimalJob();
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.FullSort;
            job.Source = new JobDataSource { Mode = "live", Provider = "bybit" };

            Assert.That(() => new ExchangeDataAdapter(job), Throws.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void LiveAdapter_EmitBookDeltasAndSnapshot()
        {
            var ws = new WsStub(
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1000," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"1\"],[\"29990\",\"2\"]],\"a\":[[\"30010\",\"3\"]],\"u\":1,\"seq\":1}}",
                "{\"topic\":\"publicTrade.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1050," +
                "\"data\":[{\"T\":1050,\"s\":\"BTCUSDT\",\"S\":\"Buy\",\"v\":\"1.0\",\"p\":\"30010\"}]}",
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"delta\",\"ts\":1100," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"0\"],[\"30005\",\"1\"]],\"a\":[[\"30010\",\"5\"]],\"u\":2,\"seq\":2}}");

            var job = MinimalJob();
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            job.EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate, MarketEventType.OrderBookSnapshot };
            job.Source = new JobDataSource { Mode = "live", Provider = "bybit", OrderBookDepth = "50", LiveDurationSeconds = 3 };

            var adapter = new ExchangeDataAdapter(job, wsFactory: () => ws);
            var events = EventStreamMerger.Merge(adapter.GetEventStreams(job, _symbol)).ToList();

            // Every live event must carry a reception timestamp.
            Assert.That(events, Is.Not.Empty);
            Assert.That(events.All(e => e.ArrivalTimestamp.HasValue), Is.True);

            var snapshots = events.OfType<OrderBookSnapshotEvent>().ToList();
            Assert.That(snapshots.Count, Is.EqualTo(1));
            Assert.That(snapshots[0].BestBidPrice, Is.EqualTo(30000m));
            Assert.That(snapshots[0].Bids.Count, Is.EqualTo(2));

            var updates = events.OfType<OrderBookUpdateEvent>().ToList();
            Assert.That(updates.Count, Is.EqualTo(3));

            var remove = updates.Single(u => u.Action == OrderBookUpdateAction.Remove);
            Assert.That(remove.Side, Is.EqualTo(OrderBookSide.Bid));
            Assert.That(remove.Price, Is.EqualTo(30000m));

            var add = updates.Single(u => u.Action == OrderBookUpdateAction.Add);
            Assert.That(add.Side, Is.EqualTo(OrderBookSide.Bid));
            Assert.That(add.Price, Is.EqualTo(30005m));
            Assert.That(add.Quantity, Is.EqualTo(1m));

            var modify = updates.Single(u => u.Action == OrderBookUpdateAction.Modify);
            Assert.That(modify.Side, Is.EqualTo(OrderBookSide.Ask));
            Assert.That(modify.Price, Is.EqualTo(30010m));
            Assert.That(modify.Quantity, Is.EqualTo(5m));
        }

        [Test]
        public void LiveAdapter_UpdateOnlyJobSurfacesInitialBookAsAdds()
        {
            var ws = new WsStub(
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1000," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"1\"],[\"29990\",\"2\"]],\"a\":[[\"30010\",\"3\"]],\"u\":1,\"seq\":1}}",
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"delta\",\"ts\":1200," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"0\"]],\"a\":[[\"30010\",\"4\"]],\"u\":2,\"seq\":2}}");

            var job = MinimalJob();
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            job.EventTypes = new List<MarketEventType> { MarketEventType.OrderBookUpdate };
            job.Source = new JobDataSource { Mode = "live", Provider = "bybit", OrderBookDepth = "50", LiveDurationSeconds = 3 };

            var adapter = new ExchangeDataAdapter(job, wsFactory: () => ws);
            var events = EventStreamMerger.Merge(adapter.GetEventStreams(job, _symbol)).ToList();

            Assert.That(events.OfType<OrderBookSnapshotEvent>(), Is.Empty);
            var updates = events.OfType<OrderBookUpdateEvent>().ToList();

            // Snapshot frame -> 3 adds; delta frame -> 1 remove + 1 modify.
            Assert.That(updates.Count, Is.EqualTo(5));
            Assert.That(updates.Count(u => u.Action == OrderBookUpdateAction.Add), Is.EqualTo(3));
            Assert.That(updates.Count(u => u.Action == OrderBookUpdateAction.Remove), Is.EqualTo(1));
            Assert.That(updates.Count(u => u.Action == OrderBookUpdateAction.Modify), Is.EqualTo(1));
        }

        [Test]
        public void Adapter_RejectsUnknownProvider()
        {
            var job = MinimalJob();
            job.Source = new JobDataSource { Mode = "historical", Provider = "kraken" };
            Assert.That(() => new ExchangeDataAdapter(job), Throws.TypeOf<NotSupportedException>());
        }

        // ---------------------------------------------------------------------------
        // Binance live adapter (fake WebSocket)
        // ---------------------------------------------------------------------------

        [Test]
        public void LiveAdapter_BinanceStreamsTradesQuotesAndBars()
        {
            var ws = new WsStub(
                "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":" + Now + ",\"s\":\"BTCUSDT\",\"t\":1001,\"p\":\"30000\",\"q\":\"0.5\",\"T\":" + Now + ",\"m\":false}}",
                "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":400900217,\"s\":\"BTCUSDT\",\"b\":\"29990\",\"B\":\"2\",\"a\":\"30010\",\"A\":\"3\",\"T\":" + Now + "}}",
                "{\"stream\":\"btcusdt@kline_1m\",\"data\":{\"e\":\"kline\",\"E\":" + Now + ",\"s\":\"BTCUSDT\",\"k\":{\"t\":1671284640000,\"T\":1671284699999,\"s\":\"BTCUSDT\",\"i\":\"1m\",\"o\":\"30000\",\"h\":\"30050\",\"l\":\"29990\",\"c\":\"30020\",\"v\":\"12.5\"}}}");

            var job = MinimalJob();
            job.Resolution = Resolution.Minute;
            job.Venue = "binance";
            job.Source = new JobDataSource { Mode = "live", Provider = "binance", LiveDurationSeconds = 3 };
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            job.EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote, MarketEventType.Bar };

            var binanceSymbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Binance);
            var adapter = new ExchangeDataAdapter(job, wsFactory: () => ws);
            var events = EventStreamMerger.Merge(adapter.GetEventStreams(job, binanceSymbol)).ToList();

            Assert.That(events, Is.Not.Empty);
            Assert.That(events.Any(e => e.EventType == MarketEventType.Trade), Is.True);
            Assert.That(events.Any(e => e.EventType == MarketEventType.Quote), Is.True);
            Assert.That(events.Any(e => e.EventType == MarketEventType.Bar), Is.True);
            Assert.That(((TradeEvent)events.First(e => e.EventType == MarketEventType.Trade)).Price, Is.EqualTo(30000m));
            Assert.That(((QuoteEvent)events.First(e => e.EventType == MarketEventType.Quote)).BidPrice, Is.EqualTo(29990m));
            Assert.That(((BarEvent)events.First(e => e.EventType == MarketEventType.Bar)).Close, Is.EqualTo(30020m));
            Assert.That(((BarEvent)events.First(e => e.EventType == MarketEventType.Bar)).Period, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(events.All(e => e.ArrivalTimestamp.HasValue), Is.True, "every live event carries a reception timestamp");
            Assert.That(ws.Closed, Is.EqualTo(1));
        }

        [Test]
        public void LiveAdapter_BinanceEventDrivenMode_ExecutorRunsEndToEnd()
        {
            var minOpenMs = Now - 60_000;
            var ws = new WsStub(
                "{\"stream\":\"btcusdt@trade\",\"data\":{\"e\":\"trade\",\"E\":" + Now + ",\"s\":\"BTCUSDT\",\"t\":1,\"p\":\"30000\",\"q\":\"0.5\",\"T\":" + Now + ",\"m\":false}}",
                "{\"stream\":\"btcusdt@bookTicker\",\"data\":{\"u\":1,\"s\":\"BTCUSDT\",\"b\":\"29990\",\"B\":\"2\",\"a\":\"30010\",\"A\":\"3\",\"T\":" + Now + "}}",
                "{\"stream\":\"btcusdt@kline_1m\",\"data\":{\"e\":\"kline\",\"E\":" + Now + ",\"s\":\"BTCUSDT\",\"k\":{\"t\":" + minOpenMs + ",\"T\":" + Now + ",\"s\":\"BTCUSDT\",\"i\":\"1m\",\"o\":\"30000\",\"h\":\"30050\",\"l\":\"29990\",\"c\":\"30020\",\"v\":\"12.5\"}}}");

            var job = MinimalJob();
            job.Resolution = Resolution.Minute;
            job.Venue = "binance";
            job.Source = new JobDataSource { Mode = "live", Provider = "binance", LiveDurationSeconds = 3 };
            job.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            job.EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote, MarketEventType.Bar };
            job.ObservationInterval = null;
            job.StartTime = DateTime.UtcNow.AddMinutes(-2);
            job.EndTime = DateTime.UtcNow.AddMinutes(3);
            job.OutputLocation = Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));

            var binanceSymbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Binance);
            job.Symbols = new List<string> { binanceSymbol.Value };
            var adapter = new ExchangeDataAdapter(job, wsFactory: () => ws);
            var executor = new LocalResearchExecutor(adapter, environment: new QuantConnect.Research.Engine.ResearchEnvironment(outputRoot: job.OutputLocation));
            var result = executor.Execute(job);

            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.EventsProcessed, Is.EqualTo(3), "one event per observation, none lost");
            Assert.That(result.ObservationsWritten, Is.EqualTo(3), "event-driven mode: observations == events");
        }

        [Test]
        public void Archive_ReplayReproducesLiveCaptureEventForEvent()
        {
            var archivePath = Path.Combine(
                Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"), "capture-bybit.jsonl");

            var tradeFrame =
                "{\"topic\":\"publicTrade.BTCUSDT\",\"type\":\"snapshot\",\"ts\":1000," +
                "\"data\":[{\"T\":1000,\"s\":\"BTCUSDT\",\"S\":\"Buy\",\"v\":\"0.1\",\"p\":\"30000\"}]}";
            var bookSnapshot =
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"snapshot\",\"ts\":2000," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"1\"],[\"29990\",\"2\"]],\"a\":[[\"30010\",\"3\"]],\"u\":1,\"seq\":1}}";
            var bookDelta =
                "{\"topic\":\"orderbook.50.BTCUSDT\",\"type\":\"delta\",\"ts\":3000," +
                "\"data\":{\"s\":\"BTCUSDT\",\"b\":[[\"30000\",\"0\"],[\"30005\",\"1\"]],\"a\":[[\"30010\",\"5\"]],\"u\":2,\"seq\":2}}";

            // 1. Live capture records the raw frames to an archive.
            var liveJob = MinimalJob();
            liveJob.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            liveJob.EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate, MarketEventType.OrderBookSnapshot };
            liveJob.Source = new JobDataSource { Mode = "live", Provider = "bybit", OrderBookDepth = "50", LiveDurationSeconds = 3, ArchiveFilePath = archivePath };

            var liveAdapter = new ExchangeDataAdapter(liveJob, wsFactory: () => new WsStub(tradeFrame, bookSnapshot, bookDelta));
            var liveEvents = EventStreamMerger.Merge(liveAdapter.GetEventStreams(liveJob, _symbol)).ToList();

            Assert.That(File.Exists(archivePath), Is.True, "live capture should write the archive");

            // 2. Replay the archive through the identical normalization path.
            var replayJob = MinimalJob();
            replayJob.Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming;
            replayJob.EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate, MarketEventType.OrderBookSnapshot };
            replayJob.Source = new JobDataSource { Mode = "archive", Provider = "bybit", OrderBookDepth = "50", ArchiveFilePath = archivePath };

            var archiveSource = new BybitArchiveSource(replayJob, replayJob.Source);
            var replayEvents = EventStreamMerger.Merge(archiveSource.GetEventStreams(replayJob, _symbol)).ToList();

            // 3. The replay is event-for-event identical.
            Assert.That(replayEvents.Count, Is.EqualTo(liveEvents.Count));
            for (var i = 0; i < liveEvents.Count; i++)
            {
                var expected = liveEvents[i];
                var actual = replayEvents[i];
                Assert.That(actual.EventType, Is.EqualTo(expected.EventType), $"event {i} type");
                Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp), $"event {i} timestamp");
                Assert.That(actual.SequenceNumber, Is.EqualTo(expected.SequenceNumber), $"event {i} sequence");
                Assert.That(ToMs(actual.ArrivalTimestamp.Value), Is.EqualTo(ToMs(expected.ArrivalTimestamp.Value)), $"event {i} arrival");
            }

            var expectedUpdates = liveEvents.OfType<OrderBookUpdateEvent>().OrderBy(u => u.Price).ThenBy(u => u.Side).ToList();
            var actualUpdates = replayEvents.OfType<OrderBookUpdateEvent>().OrderBy(u => u.Price).ThenBy(u => u.Side).ToList();
            Assert.That(actualUpdates.Count, Is.EqualTo(expectedUpdates.Count));
            for (var i = 0; i < expectedUpdates.Count; i++)
            {
                Assert.That(actualUpdates[i].Action, Is.EqualTo(expectedUpdates[i].Action), $"update {i} action");
                Assert.That(actualUpdates[i].Quantity, Is.EqualTo(expectedUpdates[i].Quantity), $"update {i} quantity");
            }

            try
            {
                File.Delete(archivePath);
            }
            catch
            {
            }
        }

        private static long ToMs(DateTime dt)
        {
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        }

        // ---------------------------------------------------------------------------
        // Fixture helpers
        // ---------------------------------------------------------------------------

        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

private static ResearchJob MinimalJob()
        {
            return new ResearchJob
            {
                JobId = "ingest-test",
                Dataset = "bybit",
                Symbols = new List<string> { _symbol.Value },
                AssetClass = "crypto",
                Venue = "bybit",
                Resolution = Resolution.Minute,
                StartTime = new DateTime(2022, 12, 19, 0, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2022, 12, 19, 0, 5, 0, DateTimeKind.Utc),
                EventTypes = new List<MarketEventType> { MarketEventType.Bar },
                ObservationInterval = TimeSpan.FromMinutes(1),
                Features = new List<string> { "mid_price", "spread", "trade_volume" },
                ExperimentName = "ingest-test",
                OutputFormat = "csv",
                EnableCheckpointing = false,
                Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.FullSort
            };
        }

        private sealed class HttpStub : IHttpTransport
        {
            private readonly List<(long StartMs, string Row)> _klineRows = new();
            private string _recentTradeBody = "{\"retCode\":0,\"result\":{\"category\":\"spot\",\"symbol\":\"BTCUSDT\",\"list\":[]}}";
            private string _orderBookBody;

            public List<Uri> Requests { get; } = new();

            public void AddKlines(params string[] rows)
            {
                foreach (var row in rows)
                {
                    using var doc = JsonDocument.Parse(row);
                    long.TryParse(doc.RootElement[0].GetString(), out var startMs);
                    _klineRows.Add((startMs, row));
                }
                _klineRows.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            }

            public void SetRecentTrades(string body) => _recentTradeBody = body;
            public void SetOrderBook(string body) => _orderBookBody = body;

            public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri);
                var url = request.RequestUri.ToString();
                string body;
                if (url.IndexOf("market/kline", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    body = BuildKlinesPage(url);
                }
                else if (url.IndexOf("recent-trade", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    body = _recentTradeBody;
                }
                else if (url.IndexOf("orderbook", StringComparison.OrdinalIgnoreCase) >= 0 && _orderBookBody != null)
                {
                    body = _orderBookBody;
                }
                else
                {
                    body = "{\"retCode\":0,\"result\":{\"category\":\"spot\",\"symbol\":\"BTCUSDT\",\"list\":[]}}";
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
            }

            private string BuildKlinesPage(string url)
            {
                var query = url.Substring(url.IndexOf('?') + 1);
                long startMs = 0;
                var limit = 1000;
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (pair.Substring(0, eq).Equals("start", StringComparison.OrdinalIgnoreCase))
                    {
                        long.TryParse(pair.Substring(eq + 1), out startMs);
                    }
                    else if (pair.Substring(0, eq).Equals("limit", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(pair.Substring(eq + 1), out limit);
                    }
                }
                limit = QuantConnect.Research.Engine.Ingest.Bybit.BybitApi.ClampPageSize(limit);

                var pageRows = _klineRows
                    .Where(r => r.StartMs >= startMs)
                    .Take(limit)
                    .Select(r => r.Row);

                var list = string.Join(",", pageRows);
                return "{\"retCode\":0,\"result\":{\"category\":\"spot\",\"symbol\":\"BTCUSDT\",\"list\":[" + list + "]}}";
            }
        }

        private sealed class WsStub : IWsTransport
        {
            private readonly Queue<string> _frames;

            public WsStub(params string[] frames)
            {
                _frames = new Queue<string>(frames);
            }

            public int Closed { get; private set; }

            public List<string> Sent { get; } = new();

            public Task ConnectAsync(string uri, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task SendTextAsync(string message, CancellationToken cancellationToken = default)
            {
                Sent.Add(message);
                return Task.CompletedTask;
            }

            public Task<string> ReceiveTextAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(_frames.Count > 0 ? _frames.Dequeue() : null);
            }

            public Task CloseAsync()
            {
                Closed++;
                return Task.CompletedTask;
            }
        }
    }
}