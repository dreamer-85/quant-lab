using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Research.Engine.Validation
{
    /// <summary>
    /// Detects columns that carry no information. The dominant real-world case is an order-book
    /// feature on a run with no order book data: every value is 0.0, the column looks healthy in the
    /// output, and any condition or threshold built on it silently never fires.
    ///
    /// Keeps only O(columns) counters, never the series, so the engine's bounded-memory guarantee is
    /// preserved.
    /// </summary>
    public sealed class DegenerateColumnCheck : IOutputCheck
    {
        /// <summary>
        /// A column with fewer than this many rows is not judged; a two-row run cannot distinguish a
        /// constant from a coincidence.
        /// </summary>
        private const int MinimumRows = 3;

        private readonly Dictionary<string, ColumnStats> _columns = new(StringComparer.OrdinalIgnoreCase);
        private long _rows;

        public string Id => ValidationCheckIds.Degenerate;

        public string Description =>
            "Columns that are constant (always the same value) or identically zero, which usually mean " +
            "the underlying data was never staged";

        public void OnRow(IReadOnlyDictionary<string, decimal> row, ValidationReport report)
        {
            if (row == null)
            {
                return;
            }

            _rows++;
            foreach (var kvp in row)
            {
                if (!_columns.TryGetValue(kvp.Key, out var stats))
                {
                    stats = new ColumnStats(kvp.Value);
                    _columns[kvp.Key] = stats;
                    continue;
                }

                stats.Add(kvp.Value);
            }
        }

        public void Complete(ValidationReport report)
        {
            if (_rows < MinimumRows)
            {
                return;
            }

            foreach (var kvp in _columns.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                var stats = kvp.Value;
                if (stats.Varying)
                {
                    continue;
                }

                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = stats.IsZero ? ValidationSeverity.Error : ValidationSeverity.Warning,
                    Message = stats.IsZero
                        ? $"Column '{kvp.Key}' is 0 for all {stats.Count:N0} observation(s). " +
                          "If this is an order book measurement, the run has no book data: add " +
                          "'OrderBookUpdate' to eventTypes and stage book_updates.csv (a websocket " +
                          "capture, since no REST endpoint serves historical L2 deltas). If it is a trade " +
                          "measurement, the window contains no trades."
                        : $"Column '{kvp.Key}' is constant at {stats.First} for all {stats.Count:N0} observation(s), " +
                          "so it carries no signal."
                });
            }
        }

        public void Reset()
        {
            _columns.Clear();
            _rows = 0;
        }

        private sealed class ColumnStats
        {
            private decimal _first;

            public decimal First => _first;
            public int Count { get; private set; }
            public bool Varying { get; private set; }
            public bool IsZero { get; private set; }

            public ColumnStats(decimal first)
            {
                _first = first;
                Count = 1;
                IsZero = first == 0m;
            }

            public void Add(decimal value)
            {
                Count++;
                if (value != _first)
                {
                    Varying = true;
                }

                if (value != 0m)
                {
                    IsZero = false;
                }
            }
        }
    }

    /// <summary>
    /// Detects two or more columns whose values are identical across every observation. This is the
    /// check that catches a feature which does not compute what its name claims: a "signed flow"
    /// feature that returns total volume is byte-identical to the volume feature, and would otherwise
    /// be indistinguishable from a working directional signal.
    ///
    /// Identity is decided by a 64-bit FNV-1a hash accumulated over each column's values, so the
    /// check needs O(columns) memory rather than O(columns x rows). Columns are reported as identical
    /// when their hashes agree and they cover the same number of observations.
    /// </summary>
    public sealed class DuplicateColumnCheck : IOutputCheck
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private readonly Dictionary<string, ColumnHash> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ulong, List<string>> _byHash = new();

        public string Id => ValidationCheckIds.Duplicate;

        public string Description =>
            "Two or more columns with identical values across every observation, which usually means one " +
            "is not computing what its name claims";

        public void OnRow(IReadOnlyDictionary<string, decimal> row, ValidationReport report)
        {
            if (row == null)
            {
                return;
            }

            foreach (var kvp in row)
            {
                if (!_columns.TryGetValue(kvp.Key, out var column))
                {
                    column = new ColumnHash();
                    _columns[kvp.Key] = column;
                }

                column.Add(kvp.Value);
            }
        }

        public void Complete(ValidationReport report)
        {
            _byHash.Clear();
            foreach (var kvp in _columns)
            {
                if (!_byHash.TryGetValue(kvp.Value.Hash, out var names))
                {
                    names = new List<string>();
                    _byHash[kvp.Value.Hash] = names;
                }

                names.Add(kvp.Key);
            }

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _byHash.Values.Where(g => g.Count > 1).OrderBy(g => g[0], StringComparer.OrdinalIgnoreCase))
            {
                var names = group.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var first = _columns[names[0]];
                if (first.Count < 3)
                {
                    continue;
                }

                var key = string.Join("|", names);
                if (!reported.Add(key))
                {
                    continue;
                }

                var identical = names.All(n => _columns[n].Count == first.Count);
                report.Add(new ValidationFinding
                {
                    Check = Id,
                    Severity = identical ? ValidationSeverity.Warning : ValidationSeverity.Info,
                    Message = identical
                        ? $"Column(s) [{string.Join(", ", names)}] have identical values across all " +
                          $"{first.Count:N0} observation(s). If these are meant to be different measurements, " +
                          "one of them is not computing what its name claims."
                        : $"Column(s) [{string.Join(", ", names)}] hash identically but have differing row " +
                          "counts, so they diverge. Verify the shorter column is not padded or truncated."
                });
            }
        }

        public void Reset()
        {
            _columns.Clear();
            _byHash.Clear();
        }

        private sealed class ColumnHash
        {
            private ulong _hash = FnvOffset;

            public ulong Hash => _hash;
            public int Count { get; private set; }

            public void Add(decimal value)
            {
                Count++;

                var bits = decimal.GetBits(value);
                for (var i = 0; i < bits.Length; i++)
                {
                    _hash ^= unchecked((uint)bits[i]);
                    _hash *= FnvPrime;
                }
            }
        }
    }
}
