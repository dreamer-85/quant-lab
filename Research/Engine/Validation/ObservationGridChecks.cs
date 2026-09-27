using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.Observations;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Integrity of the observation clock.
    ///
    /// The observation grid is what turns an event stream into a time series, so a scheduler defect
    /// here does not look like a bug: the run succeeds, the rows look plausible, and every number
    /// downstream is measured against a clock that does not match the data. The historical failure
    /// mode was a scheduler that advanced the grid by one interval per event and skipped the
    /// intervals it jumped over, which produced a series that looked evenly spaced but was stamped up
    /// to one interval earlier than the newest data in it, and which dropped gaps entirely.
    ///
    /// This check states the invariants so that failure is loud. All state is O(1).
    /// </summary>
    public sealed class ObservationGridCheck : IObservationCheck
    {
        private DateTime? _previousTimestamp;
        private TimeSpan? _expectedStep;
        private DateTime? _previousEventTimestamp;
        private int _stepMismatches;

        public string Id => ValidationCheckIds.Grid;

        public string Description =>
            "Observation clock integrity: strictly increasing timestamps, uniform steps, and no " +
            "observation carrying an event stamped later than the observation itself";

        public void OnObservation(Observation observation, IReadOnlyDictionary<string, decimal> features,
            ValidationReport report)
        {
            if (observation == null)
            {
                return;
            }

            var symbol = observation.State?.Symbol?.Value;
            var at = observation.Timestamp;

            if (_previousTimestamp.HasValue)
            {
                var step = at - _previousTimestamp.Value;
                if (step <= TimeSpan.Zero)
                {
                    report.Add(new ValidationFinding
                    {
                        Check = Id,
                        Severity = ValidationSeverity.Error,
                        Symbol = symbol,
                        Timestamp = at,
                        Message =
                            $"Observation timestamps are not strictly increasing: previous {_previousTimestamp:O}, current {at:O} " +
                            $"(step {step}). A non-advancing clock makes every period-dependent feature undefined."
                    });
                }
                else
                {
                    // The first step defines the grid; any later deviation means periods are being
                    // dropped or padded inconsistently.
                    if (!_expectedStep.HasValue)
                    {
                        _expectedStep = step;
                    }
                    else if (step != _expectedStep.Value)
                    {
                        _stepMismatches++;
                        if (_stepMismatches <= 3)
                        {
                            report.Add(new ValidationFinding
                            {
                                Check = Id,
                                Severity = ValidationSeverity.Warning,
                                Symbol = symbol,
                                Timestamp = at,
                                Message =
                                    $"Observation step {_expectedStep} became {step} at {at:O}. With fillForward enabled the " +
                                    "series should be uniform; an irregular step means observations were dropped, which makes " +
                                    "rolling windows span unequal amounts of time."
                            });
                        }
                    }
                }
            }

            // The decisive invariant: an observation is a summary of the past, so it can never contain
            // data stamped after it. A violation means a period was stamped earlier than the events it
            // reports, and every time-based consumer of that row (label horizons, holding periods,
            // time stops) is then anchored to the wrong point.
            if (observation.LastEventTimestamp.HasValue)
            {
                if (observation.LastEventTimestamp.Value > at)
                {
                    report.Add(new ValidationFinding
                    {
                        Check = Id,
                        Severity = ValidationSeverity.Error,
                        Symbol = symbol,
                        Timestamp = at,
                        Message =
                            $"Observation at {at:O} carries an event stamped {observation.LastEventTimestamp:O}, which is in its " +
                            "future. The observation clock has fallen behind the data it is reporting."
                    });
                }
                else if (_previousEventTimestamp.HasValue
                    && observation.LastEventTimestamp.Value < _previousEventTimestamp.Value)
                {
                    report.Add(new ValidationFinding
                    {
                        Check = Id,
                        Severity = ValidationSeverity.Warning,
                        Symbol = symbol,
                        Timestamp = at,
                        Message =
                            $"Newest observed event moved backwards: {observation.LastEventTimestamp:O} after " +
                            $"{_previousEventTimestamp:O}. The source is delivering events out of order."
                    });
                }

                _previousEventTimestamp = observation.LastEventTimestamp.Value;
            }

            _previousTimestamp = at;
        }
    }

    /// <summary>
    /// Whether the observation cadence actually matches the data.
    ///
    /// The grid is uniform in wall-clock time; the feed is not. When the interval is much finer than
    /// the data, most periods are padding, and a researcher who reads "last 20 periods" as a
    /// 20-interval window is actually looking at a much longer stretch of time. Nothing about the
    /// output is invalid, which is exactly why it needs saying out loud.
    /// </summary>
    public sealed class DataFreshnessCheck : IOutputCheck
    {
        /// <summary>
        /// Share of periods above which the cadence mismatch is worth reporting.
        /// </summary>
        private const double FilledRatioThreshold = 0.5d;

        private long _fresh;
        private long _filled;
        private long _missing;
        private double _maxAgeMs;

        public string Id => ValidationCheckIds.Freshness;

        public string Description =>
            "Share of observation periods that carried state forward because no new data arrived, and " +
            "the worst data age seen. High fill ratios mean the observation interval is finer than the " +
            "data cadence, so a fixed-period window spans far more time than intended";

        public void OnRow(IReadOnlyDictionary<string, decimal> row, ValidationReport report)
        {
            if (row == null)
            {
                return;
            }

            if (row.TryGetValue(ObservationTrustColumns.Quality, out var quality))
            {
                switch ((int)quality)
                {
                    case (int)DataQuality.Fresh:
                        _fresh++;
                        break;
                    case (int)DataQuality.Filled:
                        _filled++;
                        break;
                    case (int)DataQuality.Missing:
                        _missing++;
                        break;
                }
            }

            if (row.TryGetValue(ObservationTrustColumns.DataAgeMs, out var ageMs) && (double)ageMs > _maxAgeMs)
            {
                _maxAgeMs = (double)ageMs;
            }
        }

        public void Complete(ValidationReport report)
        {
            var total = _fresh + _filled + _missing;
            if (total == 0)
            {
                return;
            }

            var filledRatio = (double)_filled / total;
            if (filledRatio > FilledRatioThreshold)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"{filledRatio:P1} of observation periods carried state forward with no new data " +
                        $"({_filled} of {total}). The observation interval is finer than the data cadence, so an N-period " +
                        $"rolling window covers much more than N intervals of time. Either raise observationInterval to the " +
                        "data cadence, or set fillForward=false and treat the series as irregular by design."
                });
            }

            if (_missing > 0)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"{_missing} of {total} observation periods had no data and no prior state to carry forward " +
                        "(quality=missing). The stream began after the job's startTime, so those periods carry no market information."
                });
            }

            if (_maxAgeMs > 0)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"Oldest observed state was {_maxAgeMs / 1000d:F1}s behind the observation clock. Periods that old " +
                        "are padding, not measurements, and should not be read as fresh market data."
                });
            }
        }

        public void Reset()
        {
            _fresh = 0;
            _filled = 0;
            _missing = 0;
            _maxAgeMs = 0d;
        }
    }

    /// <summary>
    /// Whether the replay covered the window the job asked for, and whether it stopped short.
    ///
    /// A run that silently covers a fraction of the requested window produces a complete-looking
    /// output file, so coverage is compared against the job's own bounds rather than inferred from
    /// the output. Reaching the end of the data is normal; stopping because a limit was hit is not,
    /// and the two are reported differently because only one of them is a defect.
    /// </summary>
    public sealed class ObservationCoverageCheck : IOutputCheck
    {
        private DateTime? _requestedStart;
        private DateTime? _requestedEnd;
        private TimeSpan? _interval;
        private bool _padToEnd;
        private DateTime? _first;
        private DateTime? _last;
        private long _count;
        private long _lateEvents;
        private bool _truncated;

        public string Id => ValidationCheckIds.Coverage;

        public string Description =>
            "Whether the emitted observations span the window the job requested, and whether replay " +
            "was cut short by a limit rather than by the data ending";

        public void OnRow(IReadOnlyDictionary<string, decimal> row, ValidationReport report)
        {
            // Rows are counted here so a caller driving the check purely from output still knows the
            // run produced data at all. The executor overrides this with the replay's own count.
            if (row != null)
            {
                _count++;
            }
        }

        /// <summary>
        /// Records the coverage observed for one symbol. Called by the executor, which owns the job
        /// bounds and the replay statistics that the per-row interface does not carry.
        /// </summary>
        public void Observe(DateTime first, DateTime last, long observations, long lateEvents, bool truncated)
        {
            _first = first;
            _last = last;
            _count = observations;
            _lateEvents = lateEvents;
            _truncated = truncated;
        }

        /// <summary>
        /// Supplies the window the job asked for, so coverage is judged against intent rather than
        /// against whatever the run happened to produce.
        ///
        /// <paramref name="padToEnd"/> is the replay's fill-forward setting and decides whether a
        /// short tail is meaningful: with padding on, the grid is guaranteed to reach the requested
        /// end, so stopping short means something went wrong; with padding off, the series ends where
        /// the data ends and a short tail is the normal outcome, not a finding.
        /// </summary>
        public void SetRequestedWindow(DateTime start, DateTime end, TimeSpan? interval, bool padToEnd)
        {
            _requestedStart = start;
            _requestedEnd = end;
            _interval = interval;
            _padToEnd = padToEnd;
        }

        public void Complete(ValidationReport report)
        {
            if (_count > 0 && _first.HasValue && _last.HasValue && _last.Value < _first.Value)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Error,
                    Message = $"Observation coverage runs backwards: first {_first:O}, last {_last:O}."
                });
            }

            if (_count == 0)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Error,
                    Message = "No observations were produced, so the run produced an empty output file."
                });
            }
            else if (_requestedStart.HasValue && _first.HasValue && _first.Value > _requestedStart.Value)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"Observations begin at {_first:O}, after the requested start {_requestedStart:O}. The head of " +
                        "the window is absent, so features that need history to warm up start colder than expected."
                });
            }

            // A truncated run is a valid prefix by construction, so it is reported as a prefix rather
            // than as a defect. What matters is that the caller is told the end is a limit and not the
            // end of the data.
            if (_truncated)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"Replay stopped at {_last:O} because a limit was reached (maxObservations, maxEvents or the " +
                        "requested end), not because the data ended. This output is a valid prefix and must not be read " +
                        "as a complete window."
                });
            }
            else if (_padToEnd && _interval.HasValue && _last.HasValue && _requestedEnd.HasValue
                && _last.Value < _requestedEnd.Value - _interval.Value)
            {
                // With padding on, the grid runs to the requested end by construction, so a tail that
                // stops earlier cannot be explained by the data running out.
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Warning,
                    Message =
                        $"Observations end at {_last:O}, more than one interval before the requested end " +
                        $"{_requestedEnd:O}, even though fillForward is enabled and so should have padded up to it. The " +
                        "output covers a fraction of the requested window."
                });
            }

            if (_lateEvents > 0)
            {
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = ValidationSeverity.Error,
                    Message =
                        $"{_lateEvents} event(s) arrived after the observation period they belong to had already been emitted. " +
                        "The source is out of order, so those periods understate the data. Reorder=InOrderStreaming trusts its " +
                        "input, so this is a source-contract violation rather than a scheduling choice."
                });
            }
        }

        public void Reset()
        {
            _first = null;
            _last = null;
            _count = 0;
            _lateEvents = 0;
            _truncated = false;
        }
    }

    /// <summary>
    /// Names of the columns the engine publishes so a row says how much of it is real. Shared by the
    /// writer, the validator and the docs so the names cannot drift apart.
    /// </summary>
    public static class ObservationTrustColumns
    {
        /// <summary>
        /// 0 = missing, 1 = filled (state carried forward), 2 = fresh (new data this period).
        /// </summary>
        public const string Quality = "data_quality";

        /// <summary>
        /// Milliseconds between the observation clock and the newest event it reflects, or -1 when no
        /// event has been seen yet.
        /// </summary>
        public const string DataAgeMs = "data_age_ms";
    }
}
