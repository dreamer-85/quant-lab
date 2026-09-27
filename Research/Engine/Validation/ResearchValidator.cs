using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Runs the enabled validation checks over a job. Owns the check set and the report, and is the
    /// only type the executor needs to talk to.
    ///
    /// Lifecycle: <see cref="Preflight"/> once before the replay, <see cref="OnObservation"/> per
    /// observation, then <see cref="Complete"/> once after the replay. State is bounded by the number
    /// of enabled checks, never by the number of observations.
    /// </summary>
    public sealed class ResearchValidator
    {
        private readonly List<IPreflightCheck> _preflightChecks = new();
        private readonly List<IObservationCheck> _observationChecks = new();
        private readonly List<IOutputCheck> _outputChecks = new();
        private readonly ObservationCoverageCheck _coverageCheck = new();
        private readonly ValidationReport _report;

        /// <summary>
        /// The accumulated findings.
        /// </summary>
        public ValidationReport Report => _report;

        /// <summary>
        /// Whether validation is running at all.
        /// </summary>
        public bool Enabled { get; }

        /// <summary>
        /// Whether the configured mode allows an Error finding to fail the run.
        /// </summary>
        public bool CanFail { get; }

        /// <summary>
        /// Ids of the checks that are actually running, for the run manifest.
        /// </summary>
        public IReadOnlyList<string> EnabledChecks => _preflightChecks.Select(c => c.Id)
            .Concat(_observationChecks.Select(c => c.Id))
            .Concat(_outputChecks.Select(c => c.Id))
            .ToList();

        /// <summary>
        /// Creates a validator with the built-in check set, filtered by the job's options.
        /// </summary>
        public ResearchValidator(ValidationOptions options = null)
        {
            var resolved = options ?? ValidationOptions.Default;
            Enabled = resolved.Mode != ValidationMode.Off;
            CanFail = resolved.Mode == ValidationMode.Fail;
            _report = new ValidationReport(resolved.MaxFindingsPerCheck);

            if (!Enabled)
            {
                return;
            }

            var candidates = new List<object>
            {
                new JobConfigurationCheck(),
                new MarketStateInvariantCheck(),
                new ObservationCoherenceCheck(),
                new FlowIdentityCheck(),
                new NumericPlausibilityCheck(),
                new DegenerateColumnCheck(),
                new DuplicateColumnCheck(),
                new ObservationGridCheck(),
                new DataFreshnessCheck()
            };

            // The coverage check needs the job bounds and the replay counters, which the per-row
            // interface does not carry, so it is driven explicitly rather than through OnRow.
            if (resolved.IsEnabled(_coverageCheck.Id))
            {
                _outputChecks.Add(_coverageCheck);
            }

            foreach (var candidate in candidates)
            {
                if (!resolved.IsEnabled(IdOf(candidate)))
                {
                    continue;
                }

                switch (candidate)
                {
                    case IPreflightCheck preflight:
                        _preflightChecks.Add(preflight);
                        break;
                    case IObservationCheck observationCheck:
                        _observationChecks.Add(observationCheck);
                        break;
                    case IOutputCheck outputCheck:
                        _outputChecks.Add(outputCheck);
                        break;
                }
            }
        }

        /// <summary>
        /// Runs the pre-flight checks. Cheap enough to always run, and it is where the findings that
        /// save a wasted replay come from.
        /// </summary>
        public void Preflight(ResearchJob job)
        {
            if (!Enabled)
            {
                return;
            }

            foreach (var check in _preflightChecks)
            {
                check.Preflight(job, _report);
            }
        }

        /// <summary>
        /// Validates one observation and the output row it produced. The row is optional because a
        /// caller may validate without writing output.
        /// </summary>
        public void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features,
            IReadOnlyDictionary<string, decimal> outputRow = null)
        {
            if (!Enabled)
            {
                return;
            }

            _report.CountObservation();

            foreach (var check in _observationChecks)
            {
                check.OnObservation(observation, features, _report);
            }

            if (outputRow != null)
            {
                foreach (var check in _outputChecks)
                {
                    check.OnRow(outputRow, _report);
                }
            }
        }

        /// <summary>
        /// Runs the whole-run checks. Call once per symbol, before the validator is reset.
        /// </summary>
        public void Complete()
        {
            if (!Enabled)
            {
                return;
            }

            foreach (var check in _outputChecks)
            {
                check.Complete(_report);
            }
        }

        /// <summary>
        /// Records what one symbol's replay actually covered. Feeds the <c>coverage</c> check, which
        /// cannot work from output rows alone because the row set says nothing about what was asked for.
        /// </summary>
        public void ObserveCoverage(DateTime firstObservation, DateTime lastObservation,
            long observations, long lateEvents, bool truncated)
        {
            if (Enabled)
            {
                _coverageCheck.Observe(firstObservation, lastObservation, observations, lateEvents, truncated);
            }
        }

        /// <summary>
        /// Supplies the window the job requested so coverage can be judged against intent. Called once
        /// per run, before replay, because the check needs the bounds to interpret what replay reports.
        /// </summary>
        public void SetRequestedWindow(DateTime start, DateTime end, TimeSpan? interval, bool fillForward)
        {
            if (Enabled)
            {
                _coverageCheck.SetRequestedWindow(start, end, interval, fillForward);
            }
        }

        /// <summary>
        /// Clears per-run state so the same validator can be reused for the next symbol.
        /// </summary>
        public void Reset()
        {
            foreach (var check in _outputChecks)
            {
                check.Reset();
            }
        }

        /// <summary>
        /// Throws when the configured mode is <see cref="ValidationMode.Fail"/> and an Error finding
        /// was raised. A no-op in every other mode, so warn-by-default can never take down a run.
        /// </summary>
        public void ThrowIfFatal()
        {
            if (!CanFail)
            {
                return;
            }

            var errors = _report.Findings.Where(f => f.Severity == ValidationSeverity.Error).ToList();
            if (errors.Count == 0)
            {
                return;
            }

            var detail = string.Join("; ", errors.Take(10).Select(f => f.ToString()));
            var overflow = errors.Count > 10 ? $" (+{errors.Count - 10} more)" : string.Empty;
            throw new InvalidOperationException(
                $"Validation failed with {errors.Count} error(s) (validation.mode=fail). {detail}{overflow}. " +
                "Set validation.mode=warn to run without failing.");
        }

        private static string IdOf(object check)
        {
            return check switch
            {
                IPreflightCheck preflight => preflight.Id,
                IObservationCheck observationCheck => observationCheck.Id,
                IOutputCheck outputCheck => outputCheck.Id,
                _ => string.Empty
            };
        }
    }
}
