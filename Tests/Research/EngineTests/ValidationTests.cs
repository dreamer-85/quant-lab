using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.MarketState;
using MarketStateModel = QuantConnect.Research.Engine.MarketState.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;
using QuantConnect.Research.Engine.Validation;

namespace QuantConnect.Tests.Research.EngineTests
{
    /// <summary>
    /// Validation guardrails. Every check is verified both positively (a real problem is reported)
    /// and negatively (a clean run stays silent), because a guardrail that fires on healthy data
    /// trains people to ignore it.
    /// </summary>
    [TestFixture]
    public class ValidationTests
    {
        #region trade_flow correctness

        [Test]
        public void TradeFlowFeature_IsSigned_NotTotalVolume()
        {
            var observation = ObservationsTestHelpers.CreateObservationWithTrades();
            var engine = FeatureEngine.FromNames(new[] { "trade_flow", "trade_volume", "net_flow" });

            var result = engine.Compute(observation);

            var flow = result.Get("trade_flow");
            var volume = result.Get("trade_volume");
            var net = result.Get("net_flow");

            Assert.That(volume, Is.EqualTo(observation.Volume));
            Assert.That(flow, Is.Not.EqualTo(volume),
                "trade_flow regressed to unsigned total volume; it must be signed to indicate direction");
            Assert.That(flow, Is.EqualTo(894m), "signed notional: buy 1103 minus sell 209");
            Assert.That(flow, Is.EqualTo(net), "trade_flow and net_flow are two names for the same quantity");
        }

        [Test]
        public void TradeFlowFeature_MatchesRawField()
        {
            var observation = ObservationsTestHelpers.CreateObservationWithTrades();

            var derived = new TradeFlowFeature().Compute(observation, new FeatureContext());
            var raw = (decimal)RawFieldValues.For(observation, "trade_flow");

            Assert.That(derived, Is.EqualTo(raw),
                "the derived and raw namespaces must agree on a shared name, or the output precedence matters");
        }

        [Test]
        public void TradeFlowFeature_UnknownSideContributesNothing()
        {
            var state = BuildState(100m, 101m);
            var observation = new Observation
            {
                Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                State = state,
                Events = new List<MarketEvent>
                {
                    new TradeEvent
                    {
                        Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        Symbol = state.Symbol,
                        Price = 100m,
                        Quantity = 5m,
                        Side = null
                    }
                }
            };

            Assert.That(new TradeFlowFeature().Compute(observation, new FeatureContext()), Is.EqualTo(0m));
            Assert.That(observation.Volume, Is.EqualTo(5m), "total volume still counts the un-sided trade");
        }

        #endregion

        #region parameterized features

        [Test]
        public void StructuralImbalance_AppliesConfiguredBpsBand()
        {
            var parameters = FeatureParameters.ParseJobConfig(new Dictionary<string, string>
            {
                ["feature.structural_imbalance.bps_band"] = "25"
            });

            var configured = FeatureRegistry.Instance.Create("structural_imbalance", parameters.For("structural_imbalance"));
            var byDefault = FeatureRegistry.Instance.Create("structural_imbalance");

            Assert.That(configured.Name, Is.EqualTo("structural_imbalance_25bps"),
                "the configured band must reach the feature instead of being dropped");
            Assert.That(byDefault.Name, Is.EqualTo("structural_imbalance_10bps"));
        }

        [Test]
        public void LiquidityWallAndResistance_ApplyConfiguredParameters()
        {
            var wall = FeatureParameters.ParseJobConfig(new Dictionary<string, string>
            {
                ["feature.liquidity_wall.wall_threshold_multiplier"] = "5"
            });
            var resistance = FeatureParameters.ParseJobConfig(new Dictionary<string, string>
            {
                ["feature.resistance.resistance_factor"] = "4"
            });

            Assert.That(FeatureRegistry.Instance.Create("liquidity_wall", wall.For("liquidity_wall")).Name,
                Is.EqualTo("liquidity_wall_5x"));
            Assert.That(FeatureRegistry.Instance.Create("resistance", resistance.For("resistance")).Name,
                Is.EqualTo("resistance_4x"));
        }

        [Test]
        public void Catalog_DescribesConfiguredInstanceName()
        {
            var parameters = FeatureParameters.ParseJobConfig(new Dictionary<string, string>
            {
                ["feature.structural_imbalance.bps_band"] = "25"
            });

            var descriptor = MeasurementCatalog.Get("structural_imbalance_25bps", parameters);

            Assert.That(descriptor.Kind, Is.EqualTo(MeasurementKind.Derived));
            Assert.That(MeasurementCatalog.IsRegistered("structural_imbalance_25bps"), Is.False,
                "IsRegistered is a default-parameter probe and must not claim the configured alias");
        }

        #endregion

        #region metadata

        [Test]
        public void TradeCount_RawDescriptor_DeclaresInt()
        {
            var descriptor = MeasurementCatalog.Get("trade_count");
            Assert.That(descriptor.Kind, Is.EqualTo(MeasurementKind.Raw));
            Assert.That(descriptor.ValueType, Is.EqualTo(typeof(int)),
                "the runtime returns Observation.TradeCount, which is an int");
        }

        [Test]
        public void TradeFlow_CatalogResolvesToDerivedNamespace()
        {
            var descriptor = MeasurementCatalog.Get("trade_flow");
            Assert.That(descriptor.Kind, Is.EqualTo(MeasurementKind.Derived),
                "trade_flow is a registered feature, so the derived namespace describes it");
        }

        #endregion

        #region preflight: configuration intent

        [Test]
        public void Preflight_UnknownFeatureName_SuggestsClosest()
        {
            var report = new ValidationReport();
            var check = new JobConfigurationCheck();

            check.Preflight(BuildJob(features: new List<string> { "mid_pric" }), report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Error));
            Assert.That(report.Findings[0].Message, Does.Contain("mid_price"));
        }

        [Test]
        public void Preflight_DepthFeatureWithoutBookEvents_IsError()
        {
            var report = new ValidationReport();
            var check = new JobConfigurationCheck();

            check.Preflight(BuildJob(
                features: new List<string> { "imbalance" },
                eventTypes: new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote }), report);

            Assert.That(report.Findings.Select(f => f.Message), Has.Some.Contains("OrderBookUpdate"));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Error));
        }

        [Test]
        public void Preflight_DepthFeatureWithBookEvents_IsSilent()
        {
            var report = new ValidationReport();
            var check = new JobConfigurationCheck();

            check.Preflight(BuildJob(
                features: new List<string> { "imbalance" },
                eventTypes: new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate }), report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Preflight_UnknownRawField_IsError()
        {
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(BuildJob(rawFields: new List<string> { "vwapp" }), report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Message, Does.Contain("vwap"));
        }

        [Test]
        public void Preflight_UnsupportedFeatureParameter_Warns()
        {
            var job = BuildJob(
                features: new List<string> { "structural_imbalance" },
                eventTypes: new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate });
            job.ExperimentConfig["feature.structural_imbalance.window_size"] = "50";
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(job, report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Warning));
            Assert.That(report.Findings[0].Message, Does.Contain("bps_band"));
        }

        [Test]
        public void Preflight_SupportedFeatureParameter_IsSilent()
        {
            var job = BuildJob(
                features: new List<string> { "structural_imbalance" },
                eventTypes: new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate });
            job.ExperimentConfig["feature.structural_imbalance.bps_band"] = "25";
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(job, report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Preflight_RawFieldCollidingWithFeature_Warns()
        {
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(
                BuildJob(features: new List<string> { "mid_price" }, rawFields: new List<string> { "mid_price" }), report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Message, Does.Contain("precedence"));
        }

        [Test]
        public void Preflight_TradeFlowAndNetFlowTogether_Warns()
        {
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(
                BuildJob(features: new List<string> { "trade_flow", "net_flow" }), report);

            Assert.That(report.Findings.Select(f => f.Message), Has.Some.Contains("identical columns"));
        }

        [Test]
        public void Preflight_HypothesisConditionOnRawOnlyField_IsError()
        {
            var job = BuildJob(features: new List<string> { "mid_price" });
            job.ExperimentName = "hypothesis";
            job.ExperimentConfig["condition"] = "vwap < 100";
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(job, report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Error));
            Assert.That(report.Findings[0].Message, Does.Contain("never"));
        }

        [Test]
        public void Preflight_HypothesisConditionOnSelectedFeature_IsSilent()
        {
            var job = BuildJob(
                features: new List<string> { "imbalance" },
                eventTypes: new List<MarketEventType> { MarketEventType.Trade, MarketEventType.OrderBookUpdate });
            job.ExperimentName = "hypothesis";
            job.ExperimentConfig["condition"] = "imbalance < -0.50";
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(job, report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Preflight_HypothesisConditionOnUnselectedRegisteredFeature_NamesTheJobFix()
        {
            var job = BuildJob(features: new List<string> { "mid_price" });
            job.ExperimentName = "hypothesis";
            job.ExperimentConfig["condition"] = "imbalance < -0.50";
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(job, report);

            var finding = report.Findings.Single(f => f.Check == ValidationCheckIds.Config);
            Assert.That(finding.Severity, Is.EqualTo(ValidationSeverity.Error));
            Assert.That(finding.Message, Does.Contain("did not select"));
            Assert.That(finding.Message, Does.Contain("Add 'imbalance' to job.features"),
                "a registered feature only needs selecting, so say exactly that");
        }

        [Test]
        public void Preflight_HypothesisConditionOnRawField_DoesNotTellUserToAddItToFeatures()
        {
            // Regression: the old message said "Add 'trade_count' to job.features" for a raw field,
            // but raw fields are not in the feature registry, so following that advice fails the run.
            var job = BuildJob(features: new List<string> { "mid_price" });
            job.ExperimentName = "hypothesis";
            job.ExperimentConfig["condition"] = "trade_count > 0";
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(job, report);

            var finding = report.Findings.Single(f => f.Check == ValidationCheckIds.Config);
            Assert.That(finding.Severity, Is.EqualTo(ValidationSeverity.Error));
            Assert.That(finding.Message, Does.Contain("not a registered feature"));
            Assert.That(finding.Message, Does.Contain("available features include"),
                "a dead condition must be repairable from the message alone");
            Assert.That(JobConfigurationCheck.UnresolvableNames(job), Does.Not.Contain("trade_count"),
                "trade_count is a valid raw field, just not a valid condition target");
        }

        [Test]
        public void Preflight_CleanJob_IsSilent()
        {
            var report = new ValidationReport();

            new JobConfigurationCheck().Preflight(
                BuildJob(features: new List<string> { "mid_price", "spread", "trade_volume" }), report);

            Assert.That(report.Findings, Is.Empty);
        }

        #endregion

        #region observation checks

        [Test]
        public void MarketState_CrossedBook_IsError()
        {
            var report = new ValidationReport();
            var state = BuildState(101m, 100m);
            state.BidPrice = 101m;
            state.AskPrice = 100m;

            new MarketStateInvariantCheck().OnObservation(
                new Observation { Timestamp = Now, State = state }, null, report);

            Assert.That(report.Findings.Select(f => f.Message), Has.Some.Contains("Crossed book"));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Error));
        }

        [Test]
        public void MarketState_ConsistentBook_IsSilent()
        {
            var report = new ValidationReport();

            new MarketStateInvariantCheck().OnObservation(
                new Observation { Timestamp = Now, State = BuildState(100m, 101m) }, null, report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void MarketState_OneSidedBook_WarnsRatherThanErrors()
        {
            var report = new ValidationReport();
            var state = BuildState(100m, 0m);

            new MarketStateInvariantCheck().OnObservation(
                new Observation { Timestamp = Now, State = state }, null, report);

            var finding = report.Findings.Single();
            Assert.That(finding.Severity, Is.EqualTo(ValidationSeverity.Warning));
            Assert.That(finding.Message, Does.Contain("One-sided book"));
        }

        [Test]
        public void MarketState_BalancedBook_HasZeroImbalance_AndNoFinding()
        {
            var report = new ValidationReport();
            var state = BuildState(100m, 101m);
            state.UpdateFromEvent(MarketEventNormalizer.CreateOrderBookSnapshot(
                state.Symbol, Now,
                new List<OrderBookLevel> { new() { Price = 100m, Quantity = 5m } },
                new List<OrderBookLevel> { new() { Price = 101m, Quantity = 5m } }));

            new MarketStateInvariantCheck().OnObservation(
                new Observation { Timestamp = Now, State = state }, null, report);

            Assert.That(state.DepthImbalance, Is.EqualTo(0m));
            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Observation_PeriodOpeningWithNonTradeEvent_ReportsZeroOpen()
        {
            var report = new ValidationReport();
            var observation = new Observation
            {
                Timestamp = Now,
                State = BuildState(100m, 101m),
                Events = new List<MarketEvent>
                {
                    new BarEvent
                    {
                        Timestamp = Now, Symbol = Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                        Open = 50m, High = 60m, Low = 40m, Close = 55m, Volume = 1m,
                        Period = TimeSpan.FromMinutes(1)
                    },
                    Trade(100m, 1m)
                }
            };

            new ObservationCoherenceCheck().OnObservation(observation, null, report);

            var finding = report.Findings.Single();
            Assert.That(finding.Severity, Is.EqualTo(ValidationSeverity.Warning));
            Assert.That(finding.Message, Does.Contain("open = 0"));
        }

        [Test]
        public void Observation_CoherentPeriod_IsSilent()
        {
            var report = new ValidationReport();
            var observation = new Observation
            {
                Timestamp = Now,
                State = BuildState(100m, 101m),
                Events = new List<MarketEvent> { Trade(100m, 1m), Trade(102m, 2m), Trade(101m, 1m) }
            };

            new ObservationCoherenceCheck().OnObservation(observation, null, report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Observation_QuoteDerivedOpen_IsNotRangeCheckedAgainstTrades()
        {
            // Regression: OpenPrice takes a quote mid when the period opens with a quote, while
            // High/Low are trade-only. Mid and last-trade differ by a tick, so range-checking a
            // quote-derived open against the traded range produced a false error on healthy data.
            var report = new ValidationReport();
            var observation = new Observation
            {
                Timestamp = Now,
                State = BuildState(100m, 101m),
                Events = new List<MarketEvent> { Quote(100m, 101m), Trade(101.5m, 1m), Trade(103m, 1m) }
            };

            new ObservationCoherenceCheck().OnObservation(observation, null, report);

            Assert.That(observation.OpenPrice, Is.EqualTo(100.5m), "open came from the quote mid");
            Assert.That(observation.LowPrice, Is.EqualTo(101.5m), "low came from the trades");
            Assert.That(report.Findings, Is.Empty,
                "a quote-derived open must not be compared against a trade-derived range");
        }

        [Test]
        public void Flow_UnsignedTradeFlow_IsError()
        {
            var report = new ValidationReport();
            var features = new Dictionary<string, decimal>
            {
                ["aggressive_buy_volume"] = 100m,
                ["aggressive_sell_volume"] = 40m,
                ["trade_flow"] = 500m
            };

            new FlowIdentityCheck().OnObservation(
                new Observation { Timestamp = Now, State = BuildState(100m, 101m) }, features, report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Message, Does.Contain("60"));
        }

        [Test]
        public void Flow_ConsistentIdentities_AreSilent()
        {
            var report = new ValidationReport();
            var features = new Dictionary<string, decimal>
            {
                ["aggressive_buy_volume"] = 100m,
                ["aggressive_sell_volume"] = 40m,
                ["trade_flow"] = 60m,
                ["net_flow"] = 60m
            };

            new FlowIdentityCheck().OnObservation(
                new Observation { Timestamp = Now, State = BuildState(100m, 101m) }, features, report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Flow_SkippedWhenInputsNotSelected()
        {
            var report = new ValidationReport();

            new FlowIdentityCheck().OnObservation(
                new Observation { Timestamp = Now, State = BuildState(100m, 101m) },
                new Dictionary<string, decimal> { ["trade_flow"] = 999m },
                report);

            Assert.That(report.Findings, Is.Empty, "a job may compute any subset of features");
        }

        [Test]
        public void Numeric_NegativeDepth_IsError()
        {
            var report = new ValidationReport();
            var observation = new Observation { Timestamp = Now, State = BuildState(100m, 101m) };

            new NumericPlausibilityCheck().OnObservation(observation,
                new Dictionary<string, decimal> { ["bid_depth"] = -1m }, report);

            Assert.That(report.Findings, Has.Count.EqualTo(1));
            Assert.That(report.Findings[0].Severity, Is.EqualTo(ValidationSeverity.Error));
        }

        [Test]
        public void Numeric_NegativeDepth_MatchesParameterizedValueName()
        {
            var report = new ValidationReport();
            var observation = new Observation { Timestamp = Now, State = BuildState(100m, 101m) };

            new NumericPlausibilityCheck().OnObservation(observation,
                new Dictionary<string, decimal> { ["liquidity_wall_3x"] = -1m }, report);

            Assert.That(report.Findings, Has.Count.EqualTo(1),
                "parameterized features emit suffixed value names and must still be checked");
        }

        #endregion

        #region output checks

        [Test]
        public void Degenerate_ConstantZeroColumn_IsError()
        {
            var check = new DegenerateColumnCheck();
            var report = new ValidationReport();

            for (var i = 0; i < 5; i++)
            {
                check.OnRow(new Dictionary<string, decimal> { ["imbalance"] = 0m, ["mid_price"] = 100m + i }, report);
            }

            check.Complete(report);

            var finding = report.Findings.Single();
            Assert.That(finding.Check, Is.EqualTo(ValidationCheckIds.Degenerate));
            Assert.That(finding.Severity, Is.EqualTo(ValidationSeverity.Error));
            Assert.That(finding.Message, Does.Contain("imbalance"));
            Assert.That(finding.Message, Does.Contain("book_updates.csv"));
        }

        [Test]
        public void Degenerate_VaryingColumn_IsSilent()
        {
            var check = new DegenerateColumnCheck();
            var report = new ValidationReport();

            for (var i = 0; i < 5; i++)
            {
                check.OnRow(new Dictionary<string, decimal> { ["mid_price"] = 100m + i }, report);
            }

            check.Complete(report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Degenerate_TooFewRows_IsSilent()
        {
            var check = new DegenerateColumnCheck();
            var report = new ValidationReport();

            check.OnRow(new Dictionary<string, decimal> { ["imbalance"] = 0m }, report);
            check.Complete(report);

            Assert.That(report.Findings, Is.Empty, "two rows cannot distinguish a constant from a coincidence");
        }

        [Test]
        public void Duplicate_IdenticalColumns_AreReported()
        {
            var check = new DuplicateColumnCheck();
            var report = new ValidationReport();

            for (var i = 0; i < 5; i++)
            {
                var value = 100m + i;
                check.OnRow(new Dictionary<string, decimal> { ["trade_volume"] = value, ["trade_flow"] = value }, report);
            }

            check.Complete(report);

            var finding = report.Findings.Single();
            Assert.That(finding.Check, Is.EqualTo(ValidationCheckIds.Duplicate));
            Assert.That(finding.Message, Does.Contain("trade_flow").And.Contain("trade_volume"));
        }

        [Test]
        public void Duplicate_DistinctColumns_AreSilent()
        {
            var check = new DuplicateColumnCheck();
            var report = new ValidationReport();

            for (var i = 0; i < 5; i++)
            {
                check.OnRow(new Dictionary<string, decimal>
                {
                    ["trade_volume"] = 100m + i,
                    ["trade_flow"] = (100m + i) * -1m
                }, report);
            }

            check.Complete(report);

            Assert.That(report.Findings, Is.Empty);
        }

        [Test]
        public void Report_CapsFindingsPerCheck_ButStillCounts()
        {
            var report = new ValidationReport(maxFindingsPerCheck: 2);
            var check = new DegenerateColumnCheck();

            for (var i = 0; i < 10; i++)
            {
                check.OnRow(new Dictionary<string, decimal> { ["a"] = 1m, ["b"] = 2m, ["c"] = 3m }, report);
            }

            check.Complete(report);

            Assert.That(report.Findings, Has.Count.EqualTo(2), "capped at maxFindingsPerCheck");
            Assert.That(report.RaisedCounts[ValidationCheckIds.Degenerate], Is.EqualTo(3),
                "the true count is still reported so a systematic problem is not undercounted");
        }

        [Test]
        public void Report_MergesRepeatedIdenticalFindings_IntoOneWithCount()
        {
            // A wrong formula applied to every row must not produce one finding per row: the report
            // has to stay readable while still showing how many rows are affected.
            var report = new ValidationReport();
            var check = new ObservationCoherenceCheck();

            for (var i = 0; i < 50; i++)
            {
                check.OnObservation(new Observation
                {
                    Timestamp = Now.AddMinutes(i),
                    State = BuildState(100m, 101m),
                    Events = new List<MarketEvent>
                    {
                        new BarEvent
                        {
                            Timestamp = Now.AddMinutes(i),
                            Symbol = Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                            Open = 50m, High = 60m, Low = 40m, Close = 55m, Volume = 1m,
                            Period = TimeSpan.FromMinutes(1)
                        },
                        Trade(100m, 1m)
                    }
                }, null, report);
            }

            var finding = report.Findings.Single();
            Assert.That(finding.Occurrences, Is.EqualTo(50), "every affected row is counted");
            Assert.That(finding.FirstTimestamp, Is.EqualTo(Now));
            Assert.That(finding.LastTimestamp, Is.EqualTo(Now.AddMinutes(49)));
            Assert.That(report.Summarize(), Does.Contain("50 warning(s)"),
                "the summary reports blast radius, not just the number of distinct problems");
            Assert.That(finding.ToString(), Does.Contain("(x50)"));
        }

        [Test]
        public void Report_KeepsSymbolsSeparately_WhenSameMessageOnDifferentSymbols()
        {
            var report = new ValidationReport();

            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Observation, Severity = ValidationSeverity.Warning,
                Symbol = "BTCUSDT", Message = "same problem"
            });
            report.Add(new ValidationFinding
            {
                Check = ValidationCheckIds.Observation, Severity = ValidationSeverity.Warning,
                Symbol = "ETHUSDT", Message = "same problem"
            });

            Assert.That(report.Findings, Has.Count.EqualTo(2),
                "symbol scope is preserved so each symbol's blast radius is reported separately");
        }

        #endregion

        #region options and modes

        [Test]
        public void Options_DefaultToWarn()
        {
            var options = ValidationOptions.FromJob(BuildJob());
            Assert.That(options.Mode, Is.EqualTo(ValidationMode.Warn));
            Assert.That(options.IsEnabled(ValidationCheckIds.Degenerate), Is.True);
        }

        [Test]
        public void Options_ParseModeAndChecks()
        {
            var job = BuildJob();
            job.ExperimentConfig[ValidationOptions.ModeKey] = "fail";
            job.ExperimentConfig[ValidationOptions.ChecksKey] = "config, degenerate";

            var options = ValidationOptions.FromJob(job);

            Assert.That(options.Mode, Is.EqualTo(ValidationMode.Fail));
            Assert.That(options.IsEnabled(ValidationCheckIds.Degenerate), Is.True);
            Assert.That(options.IsEnabled(ValidationCheckIds.Duplicate), Is.False);
        }

        [Test]
        public void Options_UnknownMode_FallsBackToWarn_AndReports()
        {
            var job = BuildJob();
            job.ExperimentConfig[ValidationOptions.ModeKey] = "loud";
            var preflight = new List<ValidationFinding>();

            var options = ValidationOptions.FromJob(job, preflight);

            Assert.That(options.Mode, Is.EqualTo(ValidationMode.Warn));
            Assert.That(preflight, Has.Count.EqualTo(1), "a typo in a guardrail setting must be visible");
        }

        [Test]
        public void Validator_ModeOff_RunsNothing()
        {
            var validator = new ResearchValidator(new ValidationOptions(ValidationMode.Off, 20, null));

            validator.Preflight(BuildJob(features: new List<string> { "mid_pric" }));
            validator.Complete();

            Assert.That(validator.Report.Findings, Is.Empty);
            Assert.That(validator.Enabled, Is.False);
            Assert.That(validator.EnabledChecks, Is.Empty);
        }

        [Test]
        public void Validator_WarnMode_NeverThrows()
        {
            var validator = EnabledValidator(ValidationMode.Warn, null);
            validator.Preflight(BuildJob(features: new List<string> { "mid_pric" }));

            Assert.DoesNotThrow(() => validator.ThrowIfFatal());
        }

        [Test]
        public void Validator_FailMode_ThrowsOnError()
        {
            var validator = EnabledValidator(ValidationMode.Fail, null);
            validator.Preflight(BuildJob(features: new List<string> { "mid_pric" }));

            var ex = Assert.Throws<InvalidOperationException>(() => validator.ThrowIfFatal());
            Assert.That(ex.Message, Does.Contain("validation.mode=fail"));
        }

        [Test]
        public void Validator_CheckFilter_ExcludesDisabledChecks()
        {
            var validator = EnabledValidator(ValidationMode.Warn, new List<string> { ValidationCheckIds.Config });

            validator.Preflight(BuildJob(features: new List<string> { "mid_pric" }));

            Assert.That(validator.EnabledChecks, Is.EqualTo(new[] { ValidationCheckIds.Config }));
        }

        [Test]
        public void ValidationConfig_IsNotPartOfConfigurationHash()
        {
            var job = BuildJob();
            var before = job.GetConfigurationHash();

            job.ExperimentConfig[ValidationOptions.ModeKey] = "fail";
            job.ExperimentConfig[ValidationOptions.MaxFindingsKey] = "5";

            Assert.That(job.GetConfigurationHash(), Is.EqualTo(before),
                "validation cannot change results, so it must not invalidate a checkpoint");
        }

        #endregion

        #region executor integration

        [Test]
        public void Executor_WritesValidationReport_AlongsideOutputs()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                var job = SyntheticJob(outputRoot, features: new List<string> { "mid_price", "spread" });
                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.True, result.Error);

                var reportPath = Path.Combine(outputRoot, job.JobId, "validation_report.json");
                Assert.That(File.Exists(reportPath), Is.True);

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
                Assert.That(doc.RootElement.GetProperty("observationsChecked").GetInt64(), Is.GreaterThan(0));
                Assert.That(doc.RootElement.GetProperty("mode").GetString(), Is.EqualTo("Warn"));
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        [Test]
        public void Executor_UnknownFeature_FailsFastWithSuggestion_EvenInWarnMode()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                var job = SyntheticJob(outputRoot, features: new List<string> { "mid_pric" });
                job.ExperimentConfig[ValidationOptions.ModeKey] = "warn";

                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Error, Does.Contain("Job cannot run"));
                Assert.That(result.Error, Does.Contain("mid_pric"));
                Assert.That(result.Error, Does.Not.Contain("KeyNotFoundException"),
                    "a typo must be reported as a typo, not as a crash from inside the replay loop");
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        [Test]
        public void Executor_UnknownRawField_FailsFastWithSuggestion()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                var job = SyntheticJob(outputRoot, features: new List<string> { "mid_price" });
                job.RawFields.Add("trade_coun");

                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Error, Does.Contain("Raw field"));
                Assert.That(result.Error, Does.Contain("trade_count"),
                    "the nearest real raw field name should be offered");
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        [Test]
        public void Executor_ValidationOff_ProducesNoReport()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                var job = SyntheticJob(outputRoot, features: new List<string> { "mid_price" });
                job.ExperimentConfig[ValidationOptions.ModeKey] = "off";

                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.True, result.Error);
                Assert.That(File.Exists(Path.Combine(outputRoot, job.JobId, "validation_report.json")), Is.False);
                Assert.That(result.ValidationFindings, Is.Empty);
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        [Test]
        public void Executor_FailMode_FailsJobOnErrorFinding()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                // Selects a depth feature without subscribing to order book events: a preflight error.
                var job = SyntheticJob(outputRoot, features: new List<string> { "imbalance" });
                job.ExperimentConfig[ValidationOptions.ModeKey] = "fail";

                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.False);
                Assert.That(result.Error, Does.Contain("validation"));
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        [Test]
        public void Executor_WarnMode_RunsDespiteErrorFinding()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                var job = SyntheticJob(outputRoot, features: new List<string> { "imbalance" });

                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.True, result.Error);
                Assert.That(result.ValidationFindings.Select(f => f.Message),
                    Has.Some.Contains("constant 0.0").Or.Some.Contains("order-book-dependent"));
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        [Test]
        public void Executor_RawFieldDoesNotOverwriteFeatureColumn()
        {
            var outputRoot = NewOutputRoot();
            try
            {
                // mid_price is both a feature and a raw field; the derived value must win and the
                // column must still exist exactly once.
                var job = SyntheticJob(outputRoot,
                    features: new List<string> { "mid_price" },
                    rawFields: new List<string> { "mid_price", "trade_count" });

                var result = RunSynthetic(job, outputRoot);

                Assert.That(result.Succeeded, Is.True, result.Error);

                var outputFile = result.OutputFiles.First(f => f.EndsWith(".csv"));
                var header = File.ReadLines(outputFile).First().Split(',');

                Assert.That(header.Count(h => h == "mid_price"), Is.EqualTo(1));
                Assert.That(header, Does.Contain("trade_count"));

                var body = File.ReadLines(outputFile).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                Assert.That(body, Is.Not.Empty);
                foreach (var line in body)
                {
                    var cells = line.Split(',');
                    var midIndex = Array.IndexOf(header, "mid_price");
                    Assert.That(decimal.Parse(cells[midIndex], System.Globalization.CultureInfo.InvariantCulture),
                        Is.GreaterThan(0m), "the feature value must survive, not be replaced by a raw field");
                }
            }
            finally
            {
                Cleanup(outputRoot);
            }
        }

        #endregion

        #region helpers

        private static readonly DateTime Now = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static ResearchValidator EnabledValidator(ValidationMode mode, IReadOnlyCollection<string> checks)
        {
            return new ResearchValidator(new ValidationOptions(mode, 20, checks));
        }

        private static ResearchJob BuildJob(
            List<string> features = null,
            List<string> rawFields = null,
            List<MarketEventType> eventTypes = null)
        {
            return new ResearchJob
            {
                JobId = "vtest",
                Dataset = "synthetic",
                Symbols = new List<string> { "ETHUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = Now,
                EndTime = Now.AddMinutes(1),
                EventTypes = eventTypes ?? new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = TimeSpan.FromSeconds(1),
                Features = features ?? new List<string> { "mid_price" },
                RawFields = rawFields ?? new List<string>(),
                ExperimentName = "dry-run",
                OutputFormat = "csv",
                Reorder = ReorderMode.InOrderStreaming
            };
        }

        private static string NewOutputRoot()
        {
            return Path.Combine(Path.GetTempPath(), "quantlab-val-" + Guid.NewGuid().ToString("N")[..8]);
        }

        private static void Cleanup(string root)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private static MarketStateModel BuildState(decimal bid, decimal ask)
        {
            return new MarketStateModel
            {
                Symbol = Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                BidPrice = bid,
                AskPrice = ask,
                BidSize = 1m,
                AskSize = 1m
            };
        }

        private static TradeEvent Trade(decimal price, decimal quantity)
        {
            return new TradeEvent
            {
                Timestamp = Now,
                Symbol = Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                Price = price,
                Quantity = quantity,
                Side = TradeSide.Buy
            };
        }

        private static QuoteEvent Quote(decimal bid, decimal ask)
        {
            return new QuoteEvent
            {
                Timestamp = Now,
                Symbol = Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                BidPrice = bid,
                AskPrice = ask,
                BidSize = 1m,
                AskSize = 1m
            };
        }

        private static ResearchJob SyntheticJob(string outputRoot, List<string> features, List<string> rawFields = null)
        {
            var job = new ResearchJob
            {
                JobId = "valrun",
                Dataset = "synthetic",
                Symbols = new List<string> { "ETHUSDT" },
                AssetClass = "crypto",
                Venue = "bybit",
                StartTime = Now,
                EndTime = Now.AddMinutes(1),
                EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote },
                ObservationInterval = TimeSpan.FromSeconds(1),
                Features = features,
                RawFields = rawFields ?? new List<string>(),
                ExperimentName = "dry-run",
                OutputLocation = outputRoot,
                OutputFormat = "csv",
                Reorder = ReorderMode.InOrderStreaming
            };
            return job;
        }

        private static LocalExecutionResult RunSynthetic(ResearchJob job, string outputRoot)
        {
            Directory.CreateDirectory(outputRoot);
            var source = new SyntheticStreamingEventSource(
                200,
                job.StartTime,
                Symbol.Create("ETHUSDT", SecurityType.Crypto, Market.Bybit),
                eventStepNs: 10_000_000_000L,
                basePrice: 17000m,
                tradeFraction: 0.5,
                seed: 7);

            return new LocalResearchExecutor(source, new LocalFileStore(outputRoot)).Execute(job);
        }

        #endregion
    }
}
