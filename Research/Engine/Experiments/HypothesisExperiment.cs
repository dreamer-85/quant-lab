using System.Globalization;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Declarative, measurement-driven hypothesis experiment, kept deliberately small:
    ///
    ///   inputs      the measurements selected by the job (job.Features)
    ///   condition   measurement operator threshold, e.g. "imbalance &lt; -0.50"
    ///   signal      a label attached to every observation where the condition holds
    ///   outcome     forward return after N observations (1, 5, 20 by default), resolved with the
    ///               same temporal-causality guarantee as the delayed-label infrastructure
    ///   evaluation  per-horizon count, mean return, up rate and (optionally) continuation rate
    ///
    /// Config keys (job.ExperimentConfig):
    ///   condition                    required, e.g. "imbalance < -0.50"
    ///   signal                       optional label; defaults to a sanitized condition text
    ///   outcome_observation_horizons "1,5,20" comma list of observation counts
    ///   direction                    "up" | "down" | "none" - continuation direction (optional)
    ///
    /// Rows are emitted only for observations where the condition holds and are resolved in place
    /// as later observations realize their forward returns, so no future information ever reaches
    /// the signal row itself. Memory is bounded: only rows waiting on their longest horizon (at
    /// most one pending label per horizon per observation) are held.
    /// </summary>
    public sealed class HypothesisExperiment : ExperimentBase
    {
        private const string DefaultHorizons = "1,5,20";

        private MeasurementCondition _condition;
        private string _conditionText = string.Empty;
        private string _signal = string.Empty;
        private string _direction = string.Empty;
        private readonly List<int> _horizons = new();

        private readonly List<Dictionary<string, object>> _rows = new();
        private readonly Dictionary<string, Dictionary<string, object>> _rowByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<int, HorizonCounters> _counters = new();
        private List<ObservationCountLabelResolver> _resolvers = new();
        private long _observationCount;

        public HypothesisExperiment()
            : base(maxRetainedObservations: 0, maxRetainedOutcomes: 0)
        {
        }

        public override string Name => "hypothesis";

        public override string Description => "Condition-triggered signal with forward returns over N-observation horizons";

        public override string Version => "1.0.0";

        /// <summary>
        /// The measurement the condition inspects. The job must select it (job.Features) for the
        /// condition to ever fire.
        /// </summary>
        public override List<string> RequiredFeatures =>
            _condition != null ? new List<string> { _condition.Name } : new List<string>();

        public override void Initialize(ExperimentContext context)
        {
            base.Initialize(context);

            _conditionText = context.GetConfig("condition");
            if (string.IsNullOrWhiteSpace(_conditionText))
            {
                throw new InvalidOperationException("The 'hypothesis' experiment requires a 'condition' config, e.g. \"imbalance < -0.50\".");
            }

            _condition = MeasurementCondition.Parse(_conditionText);
            _signal = context.GetConfig("signal");
            if (string.IsNullOrWhiteSpace(_signal))
            {
                _signal = Sanitize(_conditionText);
            }

            _direction = context.GetConfig("direction", "none").Trim().ToLowerInvariant();
            if (_direction != "none" && _direction != "up" && _direction != "down")
            {
                throw new InvalidOperationException($"The 'hypothesis' experiment 'direction' must be up, down or none (got '{_direction}').");
            }

            var horizonText = context.GetConfig("outcome_observation_horizons", DefaultHorizons);
            _horizons.Clear();
            foreach (var token in horizonText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps) || steps < 1)
                {
                    throw new InvalidOperationException($"Invalid outcome_observation_horizons entry '{token}' (expected positive integers).");
                }

                _horizons.Add(steps);
            }

            _resolvers = _horizons.Select(h => new ObservationCountLabelResolver(h)).ToList();
            Reset();
        }

        public override void OnObservation(Observation observation, FeatureResult features)
        {
            _observationCount++;

            var values = features?.Values;
            if (_condition.Evaluate(values))
            {
                var row = BuildRow(observation, values);
                _rows.Add(row);
                var symbol = observation.State?.Symbol?.Value ?? string.Empty;
                _rowByKey[Key(symbol, observation.Timestamp)] = row;
            }

            // Advance every horizon; joins only affect rows whose reference timestamp is this run's.
            for (var i = 0; i < _resolvers.Count; i++)
            {
                foreach (var outcome in _resolvers[i].OnObservation(observation))
                {
                    ApplyOutcome(_horizons[i], outcome);
                }
            }
        }

        public override ExperimentResult Finalize()
        {
            var result = base.Finalize();

            result.Rows = _rows;
            result.Metadata["condition"] = _conditionText;
            result.Metadata["signal"] = _signal;
            result.Metadata["horizons"] = string.Join(",", _horizons);
            result.Metadata["direction"] = _direction;

            result.AddMetric("observation_count", _observationCount);
            result.AddMetric($"{_signal}_trigger_count", _rows.Count);

            foreach (var horizon in _horizons)
            {
                if (!_counters.TryGetValue(horizon, out var counters))
                {
                    continue;
                }

                var prefix = $"{_signal}_o{horizon}";
                result.AddMetric($"{prefix}_count", counters.Count);
                result.AddMetric($"{prefix}_mean_return", counters.Count > 0 ? Math.Round(counters.SumReturn / counters.Count, 8) : 0d);
                result.AddMetric($"{prefix}_up_rate", counters.Count > 0 ? Math.Round(counters.UpCount / (double)counters.Count, 6) : 0d);
                if (_direction != "none")
                {
                    result.AddMetric($"{prefix}_continuation_rate",
                        counters.Count > 0 ? Math.Round(counters.ContinuationCount / (double)counters.Count, 6) : 0d);
                }
            }

            return result;
        }

        public override void Reset()
        {
            base.Reset();
            _rows.Clear();
            _rowByKey.Clear();
            _counters.Clear();
            _observationCount = 0;
        }

        private Dictionary<string, object> BuildRow(Observation observation, IReadOnlyDictionary<string, decimal> values)
        {
            var row = new Dictionary<string, object>
            {
                ["timestamp"] = observation.Timestamp,
                ["symbol"] = observation.State?.Symbol?.Value ?? string.Empty,
                ["signal"] = _signal,
                ["condition"] = _conditionText
            };

            if (values != null)
            {
                foreach (var kvp in values)
                {
                    row[kvp.Key] = (double)kvp.Value;
                }
            }

            foreach (var horizon in _horizons)
            {
                row[$"ret_o{horizon}"] = 0m;
                row[$"resolved_o{horizon}"] = false;
            }

            return row;
        }

        private void ApplyOutcome(int steps, OutcomeData outcome)
        {
            var symbol = outcome.OutcomeState?.Symbol?.Value ?? string.Empty;
            if (!_rowByKey.TryGetValue(Key(symbol, outcome.ReferenceTimestamp), out var row))
            {
                return;
            }

            row[$"ret_o{steps}"] = outcome.OutcomeReturn ?? 0m;
            row[$"resolved_o{steps}"] = true;

            if (!_counters.TryGetValue(steps, out var counters))
            {
                counters = new HorizonCounters();
                _counters[steps] = counters;
            }

            var ret = outcome.OutcomeReturn ?? 0m;
            counters.Count++;
            counters.SumReturn += (double)ret;
            if (ret > 0m)
            {
                counters.UpCount++;
            }

            if (_direction == "up" && ret > 0m)
            {
                counters.ContinuationCount++;
            }
            else if (_direction == "down" && ret < 0m)
            {
                counters.ContinuationCount++;
            }
        }

        private static string Key(string symbol, DateTime timestamp)
        {
            return $"{symbol}|{timestamp.Ticks.ToString(CultureInfo.InvariantCulture)}";
        }

        private static string Sanitize(string text)
        {
            var chars = text.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '.').Select(c => char.ToLowerInvariant(c)).ToArray();
            return new string(chars);
        }

        private sealed class HorizonCounters
        {
            public long Count;
            public double SumReturn;
            public long UpCount;
            public long ContinuationCount;
        }
    }
}