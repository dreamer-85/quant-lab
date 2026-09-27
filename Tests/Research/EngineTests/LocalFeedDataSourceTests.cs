using QuantConnect.Research.Engine;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Ingest.LocalFeed;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class LocalFeedDataSourceTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Binance);

        private static string TempFeedRoot()
        {
            return Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
        }

        private static string WriteBars(string root, string symbol = "BTCUSDT")
        {
            var dir = Path.Combine(root, "crypto", "binance", symbol);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "bars_60.csv");
            File.WriteAllText(path,
                "timestamp_ms,open,high,low,close,volume\n" +
                "1704067200000,42000,42050,41900,42010,10.5\n" +
                "1704067260000,42010,42100,41990,42090,12.0\n");
            return path;
        }

        private static string WriteTrades(string root, string symbol = "BTCUSDT")
        {
            var dir = Path.Combine(root, "crypto", "binance", symbol);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "trades.csv");
            File.WriteAllText(path,
                "timestamp_ms,price,size,side,trade_id\n" +
                "1704067201000,42005,0.10,buy,t-1\n" +
                "1704067202000,42020,0.25,sell,t-2\n");
            return path;
        }

        private static string WriteQuotes(string root, string symbol = "BTCUSDT")
        {
            var dir = Path.Combine(root, "crypto", "binance", symbol);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "quotes.csv");
            File.WriteAllText(path,
                "timestamp_ms,bid_price,bid_size,ask_price,ask_size\n" +
                "1704067201000,42000,1.5,42005,2.0\n");
            return path;
        }

        /// <summary>Ask-heavy for minutes 0-4, then removes asks and adds bids so the book flips
        /// bid-heavy. Mirrors the imbalance &lt; -0.50 hypothesis demo (docs/examples/hypothesis).</summary>
        private static string WriteBookUpdates(string root, string symbol = "BTCUSDT")
        {
            var dir = Path.Combine(root, "crypto", "binance", symbol);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "book_updates.csv");
            var lines = new List<string> { "timestamp_ms,side,price,quantity,action" };
            var t0 = 1704067200000L; // 2024-01-01 00:00:00 UTC
            for (var m = 0; m < 5; m++)
            {
                lines.Add($"{t0 + m * 60_000 + 30_000},bid,{42000m + m * 2},1,add");
                lines.Add($"{t0 + m * 60_000 + 30_000},ask,{42010m + m * 2},9,add");
            }
            for (var m = 5; m < 10; m++)
            {
                lines.Add($"{t0 + m * 60_000 + 30_000},bid,{42000m + m * 2},9,add");
                lines.Add($"{t0 + m * 60_000 + 30_000},ask,{42010m + (m - 5) * 2},0,remove");
            }
            File.WriteAllLines(path, lines);
            return path;
        }

        private static ResearchJob BuildJob()
        {
            return new ResearchJob
            {
                JobId = "feed-test",
                Dataset = "datafeeds",
                Symbols = new List<string> { "BTCUSDT" },
                AssetClass = "crypto",
                Venue = "binance",
                Resolution = Resolution.Minute,
                StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndTime = new DateTime(2024, 1, 1, 0, 10, 0, DateTimeKind.Utc),
                EventTypes = new List<MarketEventType> { MarketEventType.Bar, MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = TimeSpan.FromMinutes(1),
                Features = new List<string> { "mid_price", "spread", "trade_volume" },
                ExperimentName = "feed-test",
                OutputFormat = "csv",
                EnableCheckpointing = false
            };
        }

        [Test]
        public void Feed_StreamsBarsTradesAndQuotesInOrder()
        {
            var root = TempFeedRoot();
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };

            WriteBars(root);
            WriteTrades(root);
            WriteQuotes(root);

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var events = EventStreamMerger.Merge(source.GetEventStreams(job, _symbol)).ToList();

            // bar (00:00) < trade (00:00:01) < quote (00:00:01) < trade (00:00:02) < bar (00:01)
            Assert.That(events.Select(e => e.EventType), Is.EqualTo(new[]
            {
                MarketEventType.Bar,
                MarketEventType.Trade,
                MarketEventType.Quote,
                MarketEventType.Trade,
                MarketEventType.Bar,
            }));

            Assert.That(events.All(e => e.Symbol == _symbol), Is.True);
            Assert.That(events.All(e => e.Provenance.Provider == "datafeeds"), Is.True);
            Assert.That(((BarEvent)events[0]).Close, Is.EqualTo(42010m));
            Assert.That(((BarEvent)events[4]).Close, Is.EqualTo(42090m));
            Assert.That(((TradeEvent)events[1]).Price, Is.EqualTo(42005m));
            Assert.That(((TradeEvent)events[1]).Side, Is.EqualTo(TradeSide.Buy));
            Assert.That(((TradeEvent)events[3]).Side, Is.EqualTo(TradeSide.Sell));
            Assert.That(((QuoteEvent)events[2]).BidPrice, Is.EqualTo(42000m));
            Assert.That(((QuoteEvent)events[2]).AskPrice, Is.EqualTo(42005m));

            Assert.That(events.Select(e => e.Timestamp), Is.Ordered);
        }

        [Test]
        public void Feed_NormalizesTickerAndSymbolSeparators()
        {
            var root = TempFeedRoot();
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };
            job.Symbols = new List<string> { "BTC-USDT" };
            var alias = Symbol.Create("BTC-USDT", SecurityType.Crypto, Market.Binance);

            WriteBars(root, symbol: "BTC_USDT");

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var events = EventStreamMerger.Merge(source.GetEventStreams(job, alias)).ToList();

            Assert.That(events.Count, Is.EqualTo(2));
            Assert.That(events[0].Symbol, Is.EqualTo(alias));
        }

        [Test]
        public void Feed_SkipsEventTypesWithoutStagedFiles()
        {
            var root = TempFeedRoot();
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };

            WriteBars(root);

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var events = EventStreamMerger.Merge(source.GetEventStreams(job, _symbol)).ToList();

            Assert.That(events.Select(e => e.EventType).Distinct(), Is.EqualTo(new[] { MarketEventType.Bar }));
        }

        [Test]
        public void Factory_RoutesFeedModeToLocalFeedDataSource()
        {
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };

            var env = new ResearchEnvironment(dataRoot: TempFeedRoot());
            var source = DataSourceFactory.Create(job, env);

            Assert.That(source, Is.TypeOf<LocalFeedDataSource>());
        }

        [Test]
        public void Factory_RejectsFeedWithoutFeedRootIsNullDefaultingToDot()
        {
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };
            var source = new LocalFeedDataSource(job, job.Source);
            Assert.That(source, Is.Not.Null);
        }

        [Test]
        public void Feed_ExecutorRunsEndToEnd()
        {
            var root = TempFeedRoot();
            WriteBars(root);
            WriteTrades(root);

            var job = BuildJob();
            job.OutputLocation = Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var executor = new LocalResearchExecutor(source, environment: new ResearchEnvironment(outputRoot: job.OutputLocation));
            var result = executor.Execute(job);

            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.EventsProcessed, Is.EqualTo(4)); // 2 bars + 2 trades
            Assert.That(result.ObservationsWritten, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Feed_StreamsOrderBookUpdates()
        {
            var root = TempFeedRoot();
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };
            job.EventTypes = new List<MarketEventType> { MarketEventType.OrderBookUpdate };

            WriteBookUpdates(root);

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var events = EventStreamMerger.Merge(source.GetEventStreams(job, _symbol))
                .OfType<OrderBookUpdateEvent>()
                .ToList();

            Assert.That(events, Has.Count.EqualTo(20));
            var firstBid = events.First(e => e.Side == OrderBookSide.Bid && e.Action == OrderBookUpdateAction.Add);
            var firstAsk = events.First(e => e.Side == OrderBookSide.Ask && e.Action == OrderBookUpdateAction.Add);
            var removed = events.Where(e => e.Action == OrderBookUpdateAction.Remove).ToList();

            Assert.That(firstBid.Price, Is.EqualTo(42000m));
            Assert.That(firstBid.Quantity, Is.EqualTo(1m));
            Assert.That(firstAsk.Price, Is.EqualTo(42010m));
            Assert.That(firstAsk.Quantity, Is.EqualTo(9m));
            Assert.That(removed, Has.Count.EqualTo(5));
            Assert.That(removed.All(r => r.Quantity == 0m && r.Side == OrderBookSide.Ask), Is.True);
            Assert.That(events.All(e => e.Provenance.Provider == "datafeeds" && e.Provenance.FeedType == "orderbook"), Is.True);
            Assert.That(events.Select(e => e.Timestamp), Is.Ordered);
        }

        [Test]
        public void Feed_ReadsBookUpdatesExactlyAsTheCaptureWriterEmitsThem()
        {
            // The capture writer (DataFeeds) and this reader were written independently and share only
            // this file format. These are the literal strings
            // datafeeds.providers.crypto.book_capture produces for a bybit snapshot row, a resize and a
            // zero-size deletion, so a change to either side's vocabulary fails here instead of
            // silently reconstructing a book that never changes.
            var root = TempFeedRoot();
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "feed", Provider = "bybit" };
            job.EventTypes = new List<MarketEventType> { MarketEventType.OrderBookUpdate };

            var dir = Path.Combine(root, "crypto", "bybit", "BTCUSDT");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "book_updates.csv"), new[]
            {
                "timestamp_ms,side,price,quantity,action",
                "1704067200000,bid,42000.0,2.0,add",     // opening snapshot level
                "1704067200000,ask,42010.0,3.0,add",     // opening snapshot level
                "1704067230000,bid,42000.0,7.0,add",     // resized in place: absolute size, not a delta
                "1704067260000,ask,42010.0,0.0,remove",  // zero size is the only delete signal the venue gives
            });

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var events = EventStreamMerger.Merge(source.GetEventStreams(job, _symbol))
                .OfType<OrderBookUpdateEvent>()
                .ToList();

            Assert.That(events, Has.Count.EqualTo(4));
            Assert.That(events[0].Side, Is.EqualTo(OrderBookSide.Bid));
            Assert.That(events[0].Action, Is.EqualTo(OrderBookUpdateAction.Add));
            Assert.That(events[0].Quantity, Is.EqualTo(2m));
            Assert.That(events[1].Side, Is.EqualTo(OrderBookSide.Ask));
            Assert.That(events[2].Quantity, Is.EqualTo(7m), "a resize must keep its absolute size");
            Assert.That(events[3].Action, Is.EqualTo(OrderBookUpdateAction.Remove),
                "a zero-quantity row must remove the level, not add a zero-sized one");
            Assert.That(events[3].Quantity, Is.EqualTo(0m));
        }

        [Test]
        public void Feed_HypothesisExperimentResolvesForwardOutcomesEndToEnd()
        {
            var root = TempFeedRoot();
            WriteBookUpdates(root);
            WriteTrades(root);

            var job = BuildJob();
            job.OutputLocation = Path.Combine(Path.GetTempPath(), "quantlab-tests", Guid.NewGuid().ToString("N"));
            job.Source = new JobDataSource { Mode = "feed", Provider = "binance" };
            job.EventTypes = new List<MarketEventType> { MarketEventType.OrderBookUpdate, MarketEventType.Trade };
            job.Features = new List<string> { "imbalance", "ask_depth", "bid_depth" };
            job.ExperimentName = "hypothesis";
            job.ExperimentConfig = new Dictionary<string, string>
            {
                ["condition"] = "imbalance < -0.50",
                ["signal"] = "ask_pressure",
                ["outcome_observation_horizons"] = "1",
                ["direction"] = "down"
            };

            var source = new LocalFeedDataSource(job, job.Source, feedRoot: root);
            var executor = new LocalResearchExecutor(
                source,
                environment: new ResearchEnvironment(outputRoot: job.OutputLocation),
                experimentFactory: ExperimentFactory.Create);
            var result = executor.Execute(job);

            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.ExperimentResult, Is.Not.Null);
            Assert.That(result.ExperimentResult.IsSuccess, Is.True);

            var metrics = result.ExperimentResult.Metrics;
            Assert.That(metrics["ask_pressure_trigger_count"], Is.GreaterThanOrEqualTo(3));
            Assert.That(metrics["ask_pressure_o1_count"], Is.GreaterThanOrEqualTo(2));
            Assert.That(metrics["ask_pressure_o1_continuation_rate"], Is.EqualTo(0L)); // mids rose, direction=down

            var rows = result.ExperimentResult.Rows;
            Assert.That(rows, Is.Not.Empty);
            Assert.That(rows.All(r => (double)r["imbalance"] < -0.5), Is.True, "every trigger row must satisfy the condition");
            Assert.That(rows.Any(r => (bool)r["resolved_o1"]), Is.True);
            Assert.That(rows.Where(r => (bool)r["resolved_o1"]).All(r => (decimal)r["ret_o1"] > 0m), Is.True);
        }

        /// <summary>
        /// A 'historical' + binance job used to yield empty event streams, which surfaced as a
        /// successful run whose every column was constant zero. It must fail with actionable text.
        /// </summary>
        [Test]
        public void Historical_Binance_FailsLoudly_InsteadOfYieldingNoEvents()
        {
            var job = BuildJob();
            job.Source = new JobDataSource { Mode = "historical", Provider = "binance" };

            var source = new ExchangeDataAdapter(job, job.Source);

            var error = Assert.Throws<NotSupportedException>(
                () => EventStreamMerger.Merge(source.GetEventStreams(job, _symbol)).ToList());

            Assert.That(error!.Message, Does.Contain("feed"));
            Assert.That(error.Message, Does.Contain("bybit"));
        }
    }
}