using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments.Python
{
    /// <summary>
    /// Runs a user-supplied Python strategy script over observation/feature/outcome streams.
    ///
    /// Contract (see docs/python-strategies.md): the script module must define a class named
    /// <c>Strategy</c> with optional hooks
    ///   <c>initialize(context)</c>                 receives job/run metadata + experiment config
    ///   <c>on_observation(observation, features)</c> may return a row dict appended to the output
    ///   <c>on_outcome(outcome)</c>                 receives resolved forward labels
    ///   <c>finalize()</c>                          may return {"rows": [...], "metrics": {...}, "metadata": {...}}
    ///
    /// The script path comes from <see cref="ResearchJob.StrategyScript"/>, overridable via the
    /// experimentConfig key "script". The class instantiated from the module defaults to
    /// <c>Strategy</c> and is overridable via the experimentConfig key "class" (the base-class
    /// research API <c>quantlab.research.ResearchStrategy</c> uses this).
    /// Observation rows returned from on_observation get "timestamp" and "symbol" auto-filled
    /// from the current observation when absent.
    ///
    /// This experiment owns NO data computation itself: raw observation fields and the explicitly
    /// requested features are passed straight to the script so users can write signals, backtests
    /// and ML feature engineering entirely in Python.
    /// </summary>
    public sealed class PythonStrategyExperiment : ExperimentBase
    {
        private readonly ResearchJob _job;
        private readonly Func<IPythonStrategyHost> _hostFactory;
        private IPythonStrategyHost _host;
        private string _scriptPath = string.Empty;
        private readonly List<Dictionary<string, object>> _rows = new();
        private readonly Dictionary<string, object> _metrics = new();
        private readonly Dictionary<string, string> _metadata = new();
        private long _observationCount;
        private long _outcomeCount;

        /// <summary>
        /// Per-symbol ring of previous periods exposed to the script as "history". Keyed by symbol
        /// because a single experiment instance serves every symbol in the job.
        /// </summary>
        private readonly Dictionary<string, LinkedList<Dictionary<string, object>>> _history =
            new(StringComparer.Ordinal);

        private readonly int _historyPeriods;
        private readonly bool _exposeEvents;

        public PythonStrategyExperiment(ResearchJob job, Func<IPythonStrategyHost> hostFactory = null)
            : base(maxRetainedObservations: 0, maxRetainedOutcomes: 0)
        {
            _job = job;
            _hostFactory = hostFactory;
            _historyPeriods = Math.Max(0, job?.ScriptHistoryPeriods ?? 0);
            _exposeEvents = job?.ScriptExposeEvents ?? false;
        }

        public override string Name => "python_strategy";

        public override string Description => "User-supplied Python strategy script";

        public override string Version => "1.0.0";

        public override List<string> RequiredFeatures => new();

        public override void Initialize(ExperimentContext context)
        {
            base.Initialize(context);

            var scriptPath = context.GetConfig("script");
            if (string.IsNullOrWhiteSpace(scriptPath))
            {
                scriptPath = _job?.StrategyScript ?? string.Empty;
            }

            _scriptPath = scriptPath;
            var host = (_hostFactory ?? (() => new PythonNetStrategyHost()))();
            _host = host;
            host.Initialize(scriptPath, BuildContext(context));
        }

        public override void OnObservation(Observation observation, FeatureResult features)
        {
            _observationCount++;
            var row = _host.OnObservation(BuildObservation(observation, features), BuildFeatures(features));
            if (row != null)
            {
                row.TryAdd("timestamp", observation.Timestamp.ToString("O"));
                row.TryAdd("symbol", observation.State?.Symbol?.Value ?? string.Empty);
                _rows.Add(row);
            }
        }

        public override void OnOutcome(OutcomeData outcome)
        {
            _outcomeCount++;
            _host.OnOutcome(BuildOutcome(outcome));
        }

        public override ExperimentResult Finalize()
        {
            var result = base.Finalize();

            // Strategy errors propagate so the executor fails the job visibly and nothing partial
            // is written as if it had succeeded.
            var host = _host;
            _host = null;
            try
            {
                var finalized = host?.Finalize();
                if (finalized != null)
                {
                    _rows.AddRange(finalized.Rows);
                    foreach (var kvp in finalized.Metrics)
                    {
                        _metrics[kvp.Key] = kvp.Value;
                    }

                    foreach (var kvp in finalized.Metadata)
                    {
                        _metadata[kvp.Key] = kvp.Value;
                    }
                }
            }
            finally
            {
                try
                {
                    host?.Dispose();
                }
                catch
                {
                    // Best-effort: the Python runtime may already be unavailable.
                }
            }

            result.Rows = _rows;
            result.Metrics = _metrics;
            result.Metadata = _metadata;
            result.AddMetric("observation_count", _observationCount);
            result.AddMetric("outcome_count", _outcomeCount);
            result.AddMetric("row_count", _rows.Count);
            result.Metadata["strategy_script"] = _scriptPath;

            return result;
        }

        public override void Reset()
        {
            base.Reset();
            _rows.Clear();
            _metrics.Clear();
            _metadata.Clear();
            _observationCount = 0;
            _outcomeCount = 0;
            _scriptPath = string.Empty;
            _history.Clear();

            var host = _host;
            _host = null;
            try
            {
                host?.Dispose();
            }
            catch
            {
                // Best-effort: the Python runtime may already be unavailable.
            }
        }

        private Dictionary<string, object> BuildContext(ExperimentContext context)
        {
            var config = new Dictionary<string, object>();
            foreach (var kvp in context.Configuration)
            {
                config[kvp.Key] = kvp.Value;
            }

            return new Dictionary<string, object>
            {
                ["job_id"] = _job?.JobId ?? string.Empty,
                ["dataset"] = _job?.Dataset ?? string.Empty,
                ["asset_class"] = _job?.AssetClass ?? string.Empty,
                ["venue"] = _job?.Venue ?? string.Empty,
                ["resolution"] = _job?.Resolution.ToString() ?? string.Empty,
                ["symbols"] = _job?.Symbols ?? new List<string>(),
                ["start"] = context.StartTime.ToString("O"),
                ["end"] = context.EndTime.ToString("O"),
                ["observation_interval_seconds"] = _job?.ObservationInterval?.TotalSeconds ?? 0d,
                ["features"] = _job?.Features ?? new List<string>(),
                ["raw_fields"] = _job?.RawFields ?? new List<string>(),
                ["history_periods"] = _historyPeriods,
                ["expose_events"] = _exposeEvents,
                ["horizons"] = _job?.Horizons ?? new List<string>(),
                ["experiment_name"] = _job?.ExperimentName ?? string.Empty,
                ["configuration_hash"] = context.ConfigurationHash ?? string.Empty,
                ["config"] = config
            };
        }

        private Dictionary<string, object> BuildObservation(Observation observation, FeatureResult features)
        {
            var state = observation.State;
            var dict = new Dictionary<string, object>
            {
                ["timestamp"] = observation.Timestamp.ToString("O"),
                ["symbol"] = state?.Symbol?.Value ?? string.Empty,
                ["open"] = (double)observation.OpenPrice,
                ["high"] = (double)observation.HighPrice,
                ["low"] = (double)observation.LowPrice,
                ["close"] = (double)observation.ClosePrice,
                ["volume"] = (double)observation.Volume,
                ["vwap"] = (double)observation.VWAP,
                ["trade_count"] = observation.TradeCount,
                ["quote_count"] = observation.QuoteCount,
                ["last_price"] = (double)(state?.LastPrice ?? 0m),
                ["bid"] = (double)(state?.BidPrice ?? 0m),
                ["ask"] = (double)(state?.AskPrice ?? 0m),
                ["mid"] = (double)(state?.MidPrice ?? 0m),
                ["spread"] = (double)(state?.Spread ?? 0m),
                ["spread_bps"] = (double)(state?.SpreadBps ?? 0m),
                ["bid_size"] = (double)(state?.BidSize ?? 0m),
                ["ask_size"] = (double)(state?.AskSize ?? 0m),
                ["bid_depth"] = (double)(state?.BidDepth ?? 0m),
                ["ask_depth"] = (double)(state?.AskDepth ?? 0m),
                ["depth_imbalance"] = (double)(state?.DepthImbalance ?? 0m)
            };

            ApplyPeriodPrices(dict, observation, state);

            // How much of this period is real. A period that carried the previous state forward is
            // stamped "filled" and its period-scoped aggregates describe an empty period, so a script
            // can skip padding instead of trading on a window it believes is fresh.
            dict["data_quality"] = QualityName(observation.Quality);
            dict["is_filled"] = observation.IsFilled;
            dict["data_age_ms"] = observation.DataAge?.TotalMilliseconds ?? -1d;
            dict["last_event_timestamp"] = observation.LastEventTimestamp?.ToString("O");

            // Every event in the period, regardless of type. This is the only way a script can
            // account for the period's events as a whole (there is no unified list otherwise) and
            // the only way to see custom events such as Funding/Liquidation/Auction.
            var allEvents = observation.Events ?? new List<MarketEvent>();
            dict["event_count"] = allEvents.Count;

            var bars = new List<object>();
            var trades = new List<object>();
            var quotes = new List<object>();
            var bookUpdates = new List<object>();
            var bookSnapshots = new List<object>();
            var customEvents = new List<object>();
            var unified = _exposeEvents ? new List<object>() : null;

            foreach (var evt in allEvents)
            {
                var serialized = SerializeEvent(evt);
                if (serialized == null)
                {
                    // An event type with no documented payload. It still counts toward event_count
                    // and still appears in the unified list with just its discriminator, so a
                    // script is never silently blind to it.
                    serialized = new Dictionary<string, object>
                    {
                        ["type"] = EventTypeName(evt.EventType),
                        ["timestamp"] = evt.Timestamp.ToString("O")
                    };
                }
                else
                {
                    serialized["type"] = EventTypeName(evt.EventType);
                }

                unified?.Add(serialized);

                switch (evt)
                {
                    case BarEvent:
                        bars.Add(serialized);
                        break;
                    case TradeEvent:
                        trades.Add(serialized);
                        break;
                    case QuoteEvent:
                        quotes.Add(serialized);
                        break;
                    case OrderBookUpdateEvent:
                        bookUpdates.Add(serialized);
                        break;
                    case OrderBookSnapshotEvent:
                        bookSnapshots.Add(serialized);
                        break;
                    case CustomMarketEvent:
                        customEvents.Add(serialized);
                        break;
                }
            }

            SetIfNotEmpty(dict, "bars", bars);
            SetIfNotEmpty(dict, "trades", trades);
            SetIfNotEmpty(dict, "quotes", quotes);
            SetIfNotEmpty(dict, "orderbook", bookUpdates);
            SetIfNotEmpty(dict, "orderbook_snapshots", bookSnapshots);
            SetIfNotEmpty(dict, "custom_events", customEvents);
            if (unified != null)
            {
                dict["events"] = unified;
            }

            AddBookLevels(dict, state);
            AddRawFields(dict, observation, features);
            AddHistory(dict, observation, features);
            return dict;
        }

        /// <summary>
        /// The single place an event's payload shape is defined. The typed lists and the unified
        /// "events" list share it so the two can never drift apart.
        /// </summary>
        private static Dictionary<string, object> SerializeEvent(MarketEvent evt)
        {
            switch (evt)
            {
                case BarEvent bar:
                    return new Dictionary<string, object>
                    {
                        ["timestamp"] = bar.Timestamp.ToString("O"),
                        ["open"] = (double)bar.Open,
                        ["high"] = (double)bar.High,
                        ["low"] = (double)bar.Low,
                        ["close"] = (double)bar.Close,
                        ["volume"] = (double)bar.Volume
                    };
                case TradeEvent trade:
                    return new Dictionary<string, object>
                    {
                        ["timestamp"] = trade.Timestamp.ToString("O"),
                        ["price"] = (double)trade.Price,
                        ["size"] = (double)trade.Quantity,
                        ["side"] = trade.Side?.ToString().ToLowerInvariant() ?? "unknown",
                        ["trade_id"] = trade.EventId
                    };
                case QuoteEvent quote:
                    return new Dictionary<string, object>
                    {
                        ["timestamp"] = quote.Timestamp.ToString("O"),
                        ["bid_price"] = (double)quote.BidPrice,
                        ["bid_size"] = (double)quote.BidSize,
                        ["ask_price"] = (double)quote.AskPrice,
                        ["ask_size"] = (double)quote.AskSize
                    };
                case OrderBookUpdateEvent update:
                    return new Dictionary<string, object>
                    {
                        ["timestamp"] = update.Timestamp.ToString("O"),
                        ["side"] = update.Side.ToString().ToLowerInvariant(),
                        ["price"] = (double)update.Price,
                        ["quantity"] = (double)update.Quantity,
                        ["action"] = update.Action.ToString().ToLowerInvariant(),
                        ["order_count"] = update.OrderCount
                    };
                case OrderBookSnapshotEvent snapshot:
                    return new Dictionary<string, object>
                    {
                        ["timestamp"] = snapshot.Timestamp.ToString("O"),
                        ["bids"] = LevelRows(snapshot.Bids),
                        ["asks"] = LevelRows(snapshot.Asks),
                        ["best_bid"] = (double)snapshot.BestBidPrice,
                        ["best_ask"] = (double)snapshot.BestAskPrice,
                        ["best_bid_size"] = (double)snapshot.BestBidQuantity,
                        ["best_ask_size"] = (double)snapshot.BestAskQuantity,
                        ["mid"] = (double)snapshot.MidPrice
                    };
                case CustomMarketEvent custom:
                    var payload = new Dictionary<string, object>
                    {
                        ["timestamp"] = custom.Timestamp.ToString("O"),
                        ["custom_type"] = custom.CustomEventType,
                        ["value"] = (double)custom.Value
                    };
                    if (custom.Data != null && custom.Data.Count > 0)
                    {
                        payload["data"] = custom.Data;
                    }

                    return payload;
                default:
                    return null;
            }
        }

        private static string EventTypeName(MarketEventType type)
        {
            return type switch
            {
                MarketEventType.Trade => "trade",
                MarketEventType.Quote => "quote",
                MarketEventType.Bar => "bar",
                MarketEventType.OrderBookUpdate => "orderbook_update",
                MarketEventType.OrderBookSnapshot => "orderbook_snapshot",
                _ => type.ToString().ToLowerInvariant()
            };
        }

        /// <summary>
        /// Adds the raw fields the job asked for, so <c>context["raw_fields"]</c> is a real
        /// availability guarantee rather than a list of names the script cannot read. Mirrors the
        /// output-row rule that a derived feature wins a name collision, so "raw" and the CSV agree.
        /// </summary>
        private void AddRawFields(Dictionary<string, object> dict, Observation observation, FeatureResult features)
        {
            var requested = _job?.RawFields;
            if (requested == null || requested.Count == 0)
            {
                return;
            }

            var raw = new Dictionary<string, object>();
            foreach (var field in requested)
            {
                if (features?.Values != null && features.Values.ContainsKey(field))
                {
                    // Same precedence as the observation output row; preflight reports the collision.
                    continue;
                }

                var value = RawFieldValues.For(observation, field);
                raw[field] = value is decimal d ? (double)d : value;
            }

            dict["raw"] = raw;
        }

        /// <summary>
        /// Adds the previous periods for this symbol so a script can compute its own indicators
        /// without reimplementing a rolling window. Oldest first, excludes the current period, and
        /// is keyed by symbol because one experiment instance serves every symbol in the job.
        /// </summary>
        private void AddHistory(Dictionary<string, object> dict, Observation observation, FeatureResult features)
        {
            var capacity = _historyPeriods;
            if (capacity <= 0)
            {
                return;
            }

            var symbol = observation.State?.Symbol?.Value ?? string.Empty;
            if (!_history.TryGetValue(symbol, out var rows))
            {
                rows = new LinkedList<Dictionary<string, object>>();
                _history[symbol] = rows;
            }

            // Emitted before the current period is appended, so "history" is strictly previous
            // periods, and trimmed afterwards so the ring holds exactly `capacity` entries.
            dict["history"] = new List<object>(rows);
            rows.AddLast(ScalarSnapshot(observation, features));
            while (rows.Count > capacity)
            {
                rows.RemoveFirst();
            }
        }

        /// <summary>
        /// The scalar + feature view of one period that forms a history entry.
        ///
        /// Mirrors the live payload: the OHLC values are the same ones
        /// <see cref="ApplyPeriodPrices"/> publishes for the current period, so a script reading
        /// <c>observation["close"]</c> and <c>observation["history"][-1]["close"]</c> is comparing the
        /// same convention rather than two. Each entry also carries its own quality stamp, so a script
        /// rolling over N periods can see how many of them were padding.
        /// </summary>
        private static Dictionary<string, object> ScalarSnapshot(Observation observation, FeatureResult features)
        {
            var state = observation.State;
            var row = new Dictionary<string, object>
            {
                ["timestamp"] = observation.Timestamp.ToString("O"),
                ["data_quality"] = QualityName(observation.Quality),
                ["data_age_ms"] = observation.DataAge?.TotalMilliseconds ?? -1d
            };

            // Same source of truth as the current period's OHLC, including the state-price fallback.
            var period = new Dictionary<string, object>();
            ApplyPeriodPrices(period, observation, state);
            foreach (var key in new[] { "open", "high", "low", "close" })
            {
                row[key] = period.TryGetValue(key, out var value) ? value : 0d;
            }

            row["volume"] = (double)observation.Volume;
            row["vwap"] = (double)observation.VWAP;
            row["trade_count"] = observation.TradeCount;
            row["last_price"] = (double)(state?.LastPrice ?? 0m);
            row["mid"] = (double)(state?.MidPrice ?? 0m);
            row["bid"] = (double)(state?.BidPrice ?? 0m);
            row["ask"] = (double)(state?.AskPrice ?? 0m);
            row["spread"] = (double)(state?.Spread ?? 0m);
            row["bid_depth"] = (double)(state?.BidDepth ?? 0m);
            row["ask_depth"] = (double)(state?.AskDepth ?? 0m);

            if (features?.Values != null)
            {
                foreach (var kvp in features.Values)
                {
                    row[kvp.Key] = (double)kvp.Value;
                }
            }

            return row;
        }

        /// <summary>
        /// The script-facing name of a data quality stamp.
        /// </summary>
        private static string QualityName(DataQuality quality)
        {
            switch (quality)
            {
                case DataQuality.Fresh:
                    return "fresh";
                case DataQuality.Filled:
                    return "filled";
                default:
                    return "missing";
            }
        }

        private static void SetIfNotEmpty(Dictionary<string, object> dict, string key, List<object> rows)
        {
            if (rows.Count > 0)
            {
                dict[key] = rows;
            }
        }

        /// <summary>
        /// Adds the reconstructed top-of-book levels, so a script can see book shape (and the levels a
        /// wall/resistance feature is measured against) rather than only the depth aggregates. Only
        /// present when the state actually carries levels.
        /// </summary>
        private static void AddBookLevels(Dictionary<string, object> dict, MarketState.MarketState state)
        {
            if (state == null)
            {
                return;
            }

            var bids = LevelRows(state.BidLevels_List);
            var asks = LevelRows(state.AskLevels_List);
            if (bids.Count == 0 && asks.Count == 0)
            {
                return;
            }

            dict["bid_levels"] = bids;
            dict["ask_levels"] = asks;
        }

        private static List<object> LevelRows(IEnumerable<OrderBookLevel> levels)
        {
            var rows = new List<object>();
            if (levels == null)
            {
                return rows;
            }

            foreach (var level in levels)
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["price"] = (double)level.Price,
                    ["quantity"] = (double)level.Quantity
                });
            }

            return rows;
        }

        private static void ApplyPeriodPrices(Dictionary<string, object> dict, Observation observation, MarketState.MarketState state)
        {
            if (state == null)
            {
                return;
            }

            decimal open = 0m, close = 0m, high = 0m, low = 0m;
            if (observation.Events is { Count: > 0 })
            {
                open = EventPrice(observation.Events[0], opening: true);
                close = EventPrice(observation.Events[^1], opening: false);

                var lowSet = false;
                foreach (var evt in observation.Events)
                {
                    switch (evt)
                    {
                        case TradeEvent trade:
                            if (trade.Price > 0m)
                            {
                                high = Math.Max(high, trade.Price);
                                low = lowSet ? Math.Min(low, trade.Price) : trade.Price;
                                lowSet = true;
                            }

                            break;
                        case BarEvent bar:
                            if (bar.High > 0m)
                            {
                                high = Math.Max(high, bar.High);
                            }

                            if (bar.Low > 0m)
                            {
                                low = lowSet ? Math.Min(low, bar.Low) : bar.Low;
                                lowSet = true;
                            }

                            break;
                    }
                }
            }

            var last = state.LastPrice;
            if (open <= 0m) open = last;
            if (close <= 0m) close = last;
            if (high <= 0m) high = last;
            if (low <= 0m) low = last;

            dict["open"] = (double)open;
            dict["high"] = (double)high;
            dict["low"] = (double)low;
            dict["close"] = (double)close;
        }

        private static decimal EventPrice(MarketEvent evt, bool opening)
        {
            return evt switch
            {
                TradeEvent t => t.Price,
                QuoteEvent q => q.MidPrice,
                BarEvent b => opening ? b.Open : b.Close,
                _ => 0m
            };
        }

        private static Dictionary<string, object> BuildFeatures(FeatureResult features)
        {
            var dict = new Dictionary<string, object>();
            if (features?.Values != null)
            {
                foreach (var kvp in features.Values)
                {
                    dict[kvp.Key] = (double)kvp.Value;
                }
            }

            return dict;
        }

        private static Dictionary<string, object> BuildOutcome(OutcomeData outcome)
        {
            var dict = new Dictionary<string, object>
            {
                ["reference_timestamp"] = outcome.ReferenceTimestamp.ToString("O"),
                ["horizon_seconds"] = outcome.Horizon.TotalSeconds,
                ["future_price"] = (double)outcome.FuturePrice
            };

            if (outcome.ReferencePrice.HasValue)
            {
                dict["reference_price"] = (double)outcome.ReferencePrice.Value;
            }

            if (outcome.OutcomeReturn.HasValue)
            {
                dict["outcome_return"] = (double)outcome.OutcomeReturn.Value;
            }

            if (outcome.ObservedAt.HasValue)
            {
                dict["observed_at"] = outcome.ObservedAt.Value.ToString("O");
            }

            return dict;
        }
    }
}