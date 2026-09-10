using System.Globalization;

namespace QuantConnect.Research.Engine.Experiments
{
    /// <summary>
    /// Parses horizon strings such as "60s", "5m", "1h", "2d", or a bare number of seconds ("300").
    /// </summary>
    public static class HorizonParser
    {
        /// <summary>
        /// Parses a horizon specification into a positive TimeSpan.
        /// </summary>
        /// <exception cref="FormatException">When the text is empty, has an unknown unit, or is non-positive.</exception>
        public static TimeSpan Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException("Horizon cannot be empty");

            var token = text.Trim().ToLowerInvariant();
            double amount;
            TimeSpan span;

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            {
                span = TimeSpan.FromSeconds(amount);
            }
            else
            {
                var unit = token[^1];
                var amountText = token[..^1];
                if (!double.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
                {
                    throw new FormatException($"Invalid horizon '{text}'");
                }

                span = unit switch
                {
                    's' => TimeSpan.FromSeconds(1),
                    'm' => TimeSpan.FromMinutes(1),
                    'h' => TimeSpan.FromHours(1),
                    'd' => TimeSpan.FromDays(1),
                    _ => throw new FormatException($"Invalid horizon unit '{unit}' in '{text}'")
                };

                span = TimeSpan.FromTicks((long)Math.Round(amount * span.Ticks));
            }

            if (span <= TimeSpan.Zero)
                throw new FormatException($"Horizon must be positive: '{text}'");

            return span;
        }
    }
}