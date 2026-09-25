using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Experiments.Python;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Stage 3: declarative hypotheses. A condition (measurement op threshold) gates a signal row;
    /// forward returns after N observations are resolved with the same causality guarantee as the
    /// delayed-label infrastructure; evaluation aggregates land on ExperimentResult.
    /// </summary>
    [TestFixture]
    public class HypothesisTests
    {
        private static readonly Symbol _symbol = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);

        // ---- MeasurementCondition ---------------------------------------------------------

        [TestCase("imbalance < -0.50", -0.60, true)]
        [TestCase("imbalance < -0.50", -0.40, false)]
        [TestCase("trade_volume > 100", 150d, true)]
        [TestCase("spread_bps <= 5", 5d, true)]
        [TestCase("spread_bps <= 5", 6d, false)]
        [TestCase("ratio == 2.5", 2.5, true)]
        [TestCase("ratio != 2.5", 2.6, true)]
        [TestCase("mid >= 100", 100d, true)]
        public void Condition_EvaluatesOverMeasurements(string text, double value, bool expected)
        {
            var condition = MeasurementCondition.Parse(text);
            Assert.That(condition.Evaluate(new Dictionary<string, decimal> { [condition.Name] = (decimal)value }), Is.EqualTo(expected));
        }

        [Test]
        public void Condition_MissingMeasurementDoesNotFire()
        {
            var condition = MeasurementCondition.Parse("imbalance < -0.50");
            Assert.That(condition.Evaluate(new Dictionary<string, decimal> { ["something_else"] = -0.99m }), Is.False);
            Assert.That(condition.Evaluate(null), Is.False);
        }

        [TestCase("imbalance")]
        [TestCase("imbalance -0.50")]
        [TestCase("imbalance < ten")]
        [TestCase("")]
        public void Condition_InvalidTextThrows(string text)
        {
            Assert.Throws<FormatException>(() => MeasurementCondition.Parse(text));
        }

        // ---- ObservationCountLabelResolver ------------------------------------------------

        [Test]
        public void CountResolver_ResolvesLabelAfterNGivenObservations()
        {
            var resolver = new ObservationCountLabelResolver(steps: 2);
            var observations = Enumerable.Range(0, 5).Select(i => Obs(2024, 1, 1, i, 100m + i)).ToList();
            var outcomes = new List<OutcomeData>();
            foreach (var obs in observations)
            {
                outcomes.AddRange(resolver.OnObservation(obs));
            }

            // obs 0 referenced at obs 2, obs 1 at obs 3, obs 2 at obs 4; obs 3/4 still pending.
            Assert.That(outcomes, Has.Count.EqualTo(3));
            Assert.That(outcomes[0].ReferenceTimestamp, Is.EqualTo(observations[0].Timestamp));
            Assert.That(outcomes[0].FuturePrice, Is.EqualTo(102m));
            Assert.That(outcomes[0].ReferencePrice, Is.EqualTo(100m));
            Assert.That(outcomes[0].OutcomeReturn, Is.EqualTo(0.02m));
            Assert.That(outcomes[1].ReferenceTimestamp, Is.EqualTo(observations[1].Timestamp));
            Assert.That(outcomes[2].ReferenceTimestamp, Is.EqualTo(observations[2].Timestamp));
            Assert.That(resolver.PendingCount, Is.EqualTo(2));

            resolver.Complete();
            Assert.That(resolver.UnresolvedCount, Is.EqualTo(2));
            Assert.That(resolver.ResolutionCount, Is.EqualTo(3));
        }

        [TestCase(0)]
        [TestCase(-3)]
        public void CountResolver_RejectsNonPositiveSteps(int steps)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ObservationCountLabelResolver(steps));
        }

        // ---- HypothesisExperiment ---------------------------------------------------------

        [Test]
        public void Hypothesis_TriggersRowsAndResolvesForwardReturns()
        {
            var experiment = new HypothesisExperiment();
            var context = new ExperimentContext
            {
                Configuration = new Dictionary<string, string>
                {
                    ["condition"] = "imbalance < -0.50",
                    ["signal"] = "bearish_pressure",
                    ["outcome_observation_horizons"] = "1,2"
                }
            };
            experiment.Initialize(context);

            // imbalance fires on i%2==0 with mid drifting up by one each step.
            for (var i = 0; i < 8; i++)
            {
                experiment.OnObservation(
                    Obs(2024, 2, 1, i, 100m + i),
                    Features(new Dictionary<string, decimal>
                    {
                        ["imbalance"] = i % 2 == 0 ? -0.6m : 0.4m
                    }));
            }

            var result = experiment.Finalize();

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Metrics["bearish_pressure_trigger_count"], Is.EqualTo(4L));
            Assert.That(result.Rows, Has.Count.EqualTo(4));

            var first = result.Rows.First();
            Assert.That(first["signal"], Is.EqualTo("bearish_pressure"));
            Assert.That(first["condition"], Is.EqualTo("imbalance < -0.50"));
            Assert.That(first["imbalance"], Is.EqualTo(-0.6d));
            Assert.That(first.ContainsKey("timestamp"), Is.True);
            Assert.That(first["symbol"], Is.EqualTo("BTCUSDT"));

            // Horizon 1 resolved for all 4 triggers (obs 6 -> obs 7 last). Horizon 2 resolved for
            // 3 triggers (obs 0->2, 2->4, 4->6; obs 6 needs obs 8 which does not exist).
            Assert.That(result.Rows.Count(r => (bool)r["resolved_o1"]), Is.EqualTo(4));
            Assert.That(result.Rows.Count(r => (bool)r["resolved_o2"]), Is.EqualTo(3));

            // obs 0 trigger: o1 return = (101-100)/100 = +0.01, o2 = (102-100)/100 = +0.02.
            var row0 = result.Rows[0];
            Assert.That((decimal)row0["ret_o1"], Is.EqualTo(0.01m));
            Assert.That((decimal)row0["ret_o2"], Is.EqualTo(0.02m));

            Assert.That(result.Metrics["bearish_pressure_o1_count"], Is.EqualTo(4L));
            Assert.That(result.Metrics["bearish_pressure_o1_up_rate"].ToString(), Does.StartWith("1"));
        }

        [Test]
        public void Hypothesis_ContinuationRateRespectsDirection()
        {
            var experiment = new HypothesisExperiment();
            experiment.Initialize(new ExperimentContext
            {
                Configuration = new Dictionary<string, string>
                {
                    ["condition"] = "imbalance < -0.50",
                    ["signal"] = "bearish",
                    ["outcome_observation_horizons"] = "1",
                    ["direction"] = "down"
                }
            });

            // Mids drift UP (returns positive), so down-continuation must be 0.
            for (var i = 0; i < 5; i++)
            {
                experiment.OnObservation(Obs(2024, 3, 1, i, 10m + i), Features(new Dictionary<string, decimal> { ["imbalance"] = -0.6m }));
            }

            var result = experiment.Finalize();
            Assert.That(result.Metrics["bearish_o1_continuation_rate"], Is.EqualTo(0d));
        }

        [Test]
        public void Hypothesis_RequiresCondition()
        {
            var experiment = new HypothesisExperiment();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                experiment.Initialize(new ExperimentContext { Configuration = new Dictionary<string, string>() }));
            Assert.That(ex.Message, Does.Contain("condition"));
        }

        [Test]
        public void Hypothesis_RequiredFeaturesExposeConditionMeasurement()
        {
            var experiment = new HypothesisExperiment();
            experiment.Initialize(new ExperimentContext
            {
                Configuration = new Dictionary<string, string> { ["condition"] = "imbalance < -0.50" }
            });
            Assert.That(experiment.RequiredFeatures, Is.EqualTo(new[] { "imbalance" }));
        }

        // ---- ExperimentFactory / CompositeExperiment --------------------------------------

        [Test]
        public void Factory_MapsHypothesis()
        {
            var job = new ResearchJob { ExperimentName = "hypothesis", ExperimentConfig = new Dictionary<string, string> { ["condition"] = "x < 1" } };
            var experiment = ExperimentFactory.Create(job);
            Assert.That(experiment, Is.InstanceOf<HypothesisExperiment>());
        }

        [Test]
        public void Factory_CommaListBuildsComposite()
        {
            var job = new ResearchJob
            {
                ExperimentName = "hypothesis,liquidity_trend",
                ExperimentConfig = new Dictionary<string, string> { ["condition"] = "x < 1" }
            };
            var experiment = ExperimentFactory.Create(job);
            Assert.That(experiment, Is.InstanceOf<CompositeExperiment>());
            var composite = (CompositeExperiment)experiment;
            Assert.That(composite.Children.Select(c => c.Name), Is.EqualTo(new[] { "hypothesis", "liquidity_trend" }));
        }

        [Test]
        public void Factory_UnknownNameInListThrows()
        {
            var job = new ResearchJob { ExperimentName = "hypothesis,not-a-thing" };
            var ex = Assert.Throws<InvalidOperationException>(() => ExperimentFactory.Create(job));
            Assert.That(ex.Message, Does.Contain("not-a-thing"));
        }

        [Test]
        public void Composite_FansOutAndNamespacesResults()
        {
            var a = new RecordingExperiment("exp_a", 10m);
            var b = new RecordingExperiment("exp_b", 20m);
            var composite = new CompositeExperiment(new IExperiment[] { a, b });
            var context = new ExperimentContext();
            composite.Initialize(context);
            var obs = Obs(2024, 4, 1, 0, 1m);
            composite.OnObservation(obs, Features(new Dictionary<string, decimal> { ["mid"] = 1m }));
            composite.OnOutcome(new OutcomeData { ReferenceTimestamp = obs.Timestamp });

            var result = composite.Finalize();

            Assert.That(result.ExperimentName, Is.EqualTo("composite"));
            Assert.That(result.Rows, Has.Count.EqualTo(2));
            Assert.That(result.Rows.Select(r => r["experiment"]), Is.EqualTo(new[] { "exp_a", "exp_b" }));
            Assert.That(result.Metrics["exp_a.count"], Is.EqualTo(1L));
            Assert.That(result.Metrics["exp_b.observation_count"], Is.EqualTo(1L));
            Assert.That(result.Metadata["experiments"], Is.EqualTo("exp_a,exp_b"));
        }

        // ---- helpers ----------------------------------------------------------------------

        private static Observation Obs(int year, int month, int day, int step, decimal lastPrice)
        {
            var state = new MarketState(_symbol);
            state.UpdateFromEvent(new TradeEvent { Timestamp = _baseTs(year, month, day).AddMinutes(step), Symbol = _symbol, Price = lastPrice, Quantity = 1m });

            return new Observation
            {
                Timestamp = _baseTs(year, month, day).AddMinutes(step),
                State = state
            };
        }

        private static DateTime _baseTs(int year, int month, int day)
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }

        private static FeatureResult Features(Dictionary<string, decimal> values)
        {
            return new FeatureResult { Timestamp = DateTime.MinValue, Values = values };
        }

        private sealed class RecordingExperiment : ExperimentBase
        {
            private readonly string _name;
            private readonly decimal _rowValue;
            private long _recordedObservations;
            private long _recordedOutcomes;

            public RecordingExperiment(string name, decimal rowValue) : base(0, 0)
            {
                _name = name;
                _rowValue = rowValue;
            }

            public override string Name => _name;

            public override void OnObservation(Observation observation, FeatureResult features)
            {
                _recordedObservations++;
            }

            public override void OnOutcome(OutcomeData outcome)
            {
                _recordedOutcomes++;
            }

            public override ExperimentResult Finalize()
            {
                var result = base.Finalize();
                result.Metrics["count"] = _recordedObservations;
                result.Metrics["observation_count"] = _recordedObservations;
                result.Metrics["outcome_count"] = _recordedOutcomes;
                result.Rows.Add(new Dictionary<string, object> { ["value"] = _rowValue });
                return result;
            }
        }
    }
}