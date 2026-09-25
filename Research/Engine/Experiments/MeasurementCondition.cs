using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// A research-layer hypothesis condition: "measurement operator threshold", e.g.
    /// "imbalance &lt; -0.50". Conditions reference measurements by the names the engine exposes
    /// (see MeasurementCatalog) and are kept separate from measurements themselves - a measurement
    /// answers "what can I calculate?", a condition answers "when should the signal fire?".
    /// </summary>
    public sealed class MeasurementCondition
    {
        private static readonly string[] Operators = { "<=", ">=", "==", "!=", "<", ">" };

        /// <summary>
        /// Measurement name the condition inspects.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Comparison operator: &lt;, &lt;=, &gt;, &gt;=, == or !=.
        /// </summary>
        public string Operator { get; }

        /// <summary>
        /// Threshold the measurement is compared against.
        /// </summary>
        public decimal Threshold { get; }

        private MeasurementCondition(string name, string op, decimal threshold)
        {
            Name = name;
            Operator = op;
            Threshold = threshold;
        }

        /// <summary>
        /// Parses "name operator number" text. Whitespace is allowed around each token. Unknown
        /// operators or unparsable numbers throw <see cref="FormatException"/> with a pointer.
        /// </summary>
        public static MeasurementCondition Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FormatException("Condition must not be empty.");
            }

            var trimmed = text.Trim();
            foreach (var op in Operators)
            {
                var index = trimmed.IndexOf(op, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                var name = trimmed.Substring(0, index).Trim();
                var value = trimmed.Substring(index + op.Length).Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new FormatException($"Condition '{text}' is missing a measurement name before '{op}'.");
                }

                if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
                {
                    throw new FormatException(
                        $"Condition '{text}' has an unparsable threshold '{value}' after '{op}'. Use a decimal number (e.g. imbalance < -0.50).");
                }

                return new MeasurementCondition(name, op, threshold);
            }

            throw new FormatException(
                $"Condition '{text}' has no comparison operator. Expected 'name op number' with op in {string.Join(", ", Operators)} (e.g. imbalance < -0.50).");
        }

        /// <summary>
        /// Evaluates the condition against a set of measurement values (e.g.
        /// <see cref="FeatureResult.Values"/>). Returns false when the named measurement is absent,
        /// so a condition silently does not fire until its input is selected and computed.
        /// </summary>
        public bool Evaluate(IReadOnlyDictionary<string, decimal> values)
        {
            if (values == null || !values.TryGetValue(Name, out var value))
            {
                return false;
            }

            return Operator switch
            {
                "<" => value < Threshold,
                "<=" => value <= Threshold,
                ">" => value > Threshold,
                ">=" => value >= Threshold,
                "==" => value == Threshold,
                "!=" => value != Threshold,
                _ => false
            };
        }

        /// <summary>
        /// Text form of the condition (measurement operator threshold).
        /// </summary>
        public override string ToString() => $"{Name} {Operator} {Threshold.ToString(CultureInfo.InvariantCulture)}";
    }
}