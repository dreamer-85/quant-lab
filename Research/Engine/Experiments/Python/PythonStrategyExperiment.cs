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

        public PythonStrategyExperiment(ResearchJob job, Func<IPythonStrategyHost> hostFactory = null)
            : base(maxRetainedObservations: 0, maxRetainedOutcomes: 0)
        {
            _job = job;
            _hostFactory = hostFactory;
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
            var row = _host.OnObservation(BuildObservation(observation), BuildFeatures(features));
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
                ["horizons"] = _job?.Horizons ?? new List<string>(),
                ["experiment_name"] = _job?.ExperimentName ?? string.Empty,
                ["configuration_hash"] = context.ConfigurationHash ?? string.Empty,
                ["config"] = config
            };
        }

        private static Dictionary<string, object> BuildObservation(Observation observation)
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

            if (observation.Events is { Count: > 0 })
            {
                var bars = new List<object>();
                var trades = new List<object>();
                var quotes = new List<object>();
                foreach (var evt in observation.Events)
                {
                    switch (evt)
                    {
                        case BarEvent bar:
                            bars.Add(new Dictionary<string, object>
                            {
                                ["timestamp"] = bar.Timestamp.ToString("O"),
                                ["open"] = (double)bar.Open,
                                ["high"] = (double)bar.High,
                                ["low"] = (double)bar.Low,
                                ["close"] = (double)bar.Close,
                                ["volume"] = (double)bar.Volume
                            });
                            break;
                        case TradeEvent trade:
                            trades.Add(new Dictionary<string, object>
                            {
                                ["timestamp"] = trade.Timestamp.ToString("O"),
                                ["price"] = (double)trade.Price,
                                ["size"] = (double)trade.Quantity,
                                ["side"] = trade.Side?.ToString().ToLowerInvariant() ?? "unknown",
                                ["trade_id"] = trade.EventId
                            });
                            break;
                        case QuoteEvent quote:
                            quotes.Add(new Dictionary<string, object>
                            {
                                ["timestamp"] = quote.Timestamp.ToString("O"),
                                ["bid_price"] = (double)quote.BidPrice,
                                ["bid_size"] = (double)quote.BidSize,
                                ["ask_price"] = (double)quote.AskPrice,
                                ["ask_size"] = (double)quote.AskSize
                            });
                            break;
                    }
                }

                if (bars.Count > 0)
                {
                    dict["bars"] = bars;
                }

                if (trades.Count > 0)
                {
                    dict["trades"] = trades;
                }

                if (quotes.Count > 0)
                {
                    dict["quotes"] = quotes;
                }
            }

            return dict;
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