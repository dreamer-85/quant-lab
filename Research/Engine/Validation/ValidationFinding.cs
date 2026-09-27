using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// How seriously a validation finding should be taken.
    /// </summary>
    public enum ValidationSeverity
    {
        /// <summary>
        /// Informational: the run is fine, but something is worth knowing (e.g. a configured
        /// parameter that has no effect).
        /// </summary>
        Info,

        /// <summary>
        /// The output is probably not what the job author intended, but it is still usable.
        /// </summary>
        Warning,

        /// <summary>
        /// The output is mathematically or semantically wrong. Under
        /// <see cref="ValidationMode.Fail"/> this aborts the run.
        /// </summary>
        Error
    }

    /// <summary>
    /// Controls whether validation runs and whether findings can fail a job.
    /// </summary>
    public enum ValidationMode
    {
        /// <summary>
        /// Validation does not run at all.
        /// </summary>
        Off,

        /// <summary>
        /// Validation runs and reports findings, but never fails the job. The default.
        /// </summary>
        Warn,

        /// <summary>
        /// Validation runs and the job fails if any <see cref="ValidationSeverity.Error"/> finding
        /// is raised.
        /// </summary>
        Fail
    }

    /// <summary>
    /// A single validation finding. Findings are capped per check so a systematic problem cannot
    /// grow the report without bound.
    /// </summary>
    public sealed class ValidationFinding
    {
        /// <summary>
        /// Identifier of the check that raised the finding.
        /// </summary>
        public string Check { get; set; }

        /// <summary>
        /// Severity of the finding.
        /// </summary>
        public ValidationSeverity Severity { get; set; }

        /// <summary>
        /// Human-readable explanation of what is wrong and, where possible, how to fix it.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Symbol the finding relates to, when it is symbol-scoped.
        /// </summary>
        public string Symbol { get; set; }

        /// <summary>
        /// Observation timestamp the finding relates to, when it is observation-scoped.
        /// </summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>
        /// How many times this exact finding was raised. A systematic problem (a wrong formula applied
        /// to every row) is reported once with a count rather than once per observation, so the report
        /// stays readable and the reader can see the blast radius.
        /// </summary>
        public int Occurrences { get; set; } = 1;

        /// <summary>
        /// Earliest observation timestamp this finding was raised at, when observation-scoped.
        /// </summary>
        public DateTime? FirstTimestamp { get; set; }

        /// <summary>
        /// Latest observation timestamp this finding was raised at, when observation-scoped.
        /// </summary>
        public DateTime? LastTimestamp { get; set; }

        public override string ToString()
        {
            var scope = Symbol == null ? string.Empty : $" [{Symbol}]";
            var times = Occurrences > 1 ? $" (x{Occurrences})" : string.Empty;
            return $"{Severity.ToString().ToUpperInvariant()} {Check}{scope}{times}: {Message}";
        }
    }

    /// <summary>
    /// Accumulates validation findings for a run. Findings are keyed by (check, message) so a
    /// systematic problem is reported once with an occurrence count instead of once per observation,
    /// and each distinct finding is capped so the report cannot grow without bound. Memory is bounded
    /// by the number of distinct problems, not by dataset size.
    /// </summary>
    public sealed class ValidationReport
    {
        private readonly Dictionary<string, int> _raisedByCheck = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _retainedByCheck = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ValidationFinding> _byKey = new(StringComparer.Ordinal);
        private readonly List<ValidationFinding> _findings = new();
        private readonly int _maxFindingsPerCheck;

        /// <summary>
        /// Findings retained for the report, in the order they were first raised.
        /// </summary>
        public IReadOnlyList<ValidationFinding> Findings => _findings;

        /// <summary>
        /// Number of observations the validator inspected.
        /// </summary>
        public long ObservationsChecked { get; private set; }

        /// <summary>
        /// Total number of findings raised per check, including repeats of an already-reported finding
        /// and findings suppressed by the cap.
        /// </summary>
        public IReadOnlyDictionary<string, int> RaisedCounts => _raisedByCheck;

        /// <summary>
        /// Creates a report that retains at most <paramref name="maxFindingsPerCheck"/> distinct
        /// findings for each check.
        /// </summary>
        public ValidationReport(int maxFindingsPerCheck = 20)
        {
            _maxFindingsPerCheck = Math.Max(1, maxFindingsPerCheck);
        }

        /// <summary>
        /// Records a finding, merging it into an existing finding with the same check and message.
        /// Returns true when the finding was retained, false when it was suppressed by the per-check cap.
        /// </summary>
        public bool Add(ValidationFinding finding)
        {
            if (finding == null)
            {
                return false;
            }

            _raisedByCheck.TryGetValue(finding.Check, out var raised);
            _raisedByCheck[finding.Check] = raised + 1;

            // Symbol is part of the key so a symbol-scoped finding stays accurately scoped; the
            // occurrence count still shows how many rows of that symbol are affected.
            var key = finding.Check + " " + finding.Symbol + " " + finding.Message;
            if (_byKey.TryGetValue(key, out var existing))
            {
                existing.Occurrences++;
                if (finding.Timestamp.HasValue)
                {
                    var at = finding.Timestamp.Value;
                    existing.FirstTimestamp = Min(existing.FirstTimestamp, at);
                    existing.LastTimestamp = Max(existing.LastTimestamp, at);
                }

                return true;
            }

            finding.FirstTimestamp = finding.Timestamp;
            finding.LastTimestamp = finding.Timestamp;

            if (_retainedByCheck.TryGetValue(finding.Check, out var retained) && retained >= _maxFindingsPerCheck)
            {
                return false;
            }

            _findings.Add(finding);
            _byKey[key] = finding;
            _retainedByCheck[finding.Check] = retained + 1;
            return true;
        }

        /// <summary>
        /// Counts an inspected observation.
        /// </summary>
        public void CountObservation()
        {
            ObservationsChecked++;
        }

        /// <summary>
        /// Number of retained findings at a given severity.
        /// </summary>
        public int CountAt(ValidationSeverity severity)
        {
            return _findings.Count(f => f.Severity == severity);
        }

        /// <summary>
        /// Whether any retained finding has the given severity.
        /// </summary>
        public bool HasSeverity(ValidationSeverity severity)
        {
            return _findings.Any(f => f.Severity == severity);
        }

        /// <summary>
        /// A one-line summary suitable for the console, naming the highest severity raised.
        /// </summary>
        public string Summarize()
        {
            if (_findings.Count == 0)
            {
                return ObservationsChecked == 0
                    ? "validation: no observations checked"
                    : $"validation: clean across {ObservationsChecked:N0} observation(s)";
            }

            var errors = TotalAt(ValidationSeverity.Error);
            var warnings = TotalAt(ValidationSeverity.Warning);

            return $"validation: {errors} error(s), {warnings} warning(s) across {ObservationsChecked:N0} observation(s)";
        }

        /// <summary>
        /// Total occurrences of retained findings at a given severity, counting repeats of a
        /// systematic problem as the number of rows affected.
        /// </summary>
        private int TotalAt(ValidationSeverity severity)
        {
            return _findings.Where(f => f.Severity == severity).Sum(f => f.Occurrences);
        }

        private static DateTime? Min(DateTime? current, DateTime candidate)
        {
            return !current.HasValue || candidate < current.Value ? candidate : current;
        }

        private static DateTime? Max(DateTime? current, DateTime candidate)
        {
            return !current.HasValue || candidate > current.Value ? candidate : current;
        }
    }
}
