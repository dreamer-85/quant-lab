using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuantConnect.Research.Engine.Portfolio
{
    /// <summary>
    /// What a strategy script asked for on one observation.
    ///
    /// A script signals by returning a dict from <c>on_observation</c> that carries an "order"
    /// key. The intent is deliberately separate from the target position it resolves to, because
    /// the two are answered by different people: the script says buy or sell and, if it cares,
    /// how much; the account decides the rest.
    ///
    /// Sizing precedence, which is the whole point of keeping quantity optional:
    ///   1. <see cref="Quantity"/> present  -> the script computed the size itself, so the
    ///      account uses it verbatim. This is how a script does dynamic equity allocation.
    ///   2. <see cref="Quantity"/> absent   -> the account uses the size configured for the run
    ///      (the UI's order quantity, or a fraction of equity), so a script that only decides
    ///      "buy or not" does not have to think about sizing at all.
    ///
    /// Returning no "order" key at all means no trade, which is the common case.
    /// </summary>
    public sealed class StrategyOrder
    {
        public enum Action
        {
            /// <summary>No trade.</summary>
            None,

            /// <summary>Increase the long position by the resolved size.</summary>
            Buy,

            /// <summary>Increase the short position by the resolved size.</summary>
            Sell,

            /// <summary>Flatten the position, in either direction.</summary>
            Close
        }

        /// <summary>What the script asked for.</summary>
        public Action Requested { get; private set; } = Action.None;

        /// <summary>
        /// Base-unit size the script computed, or null when it left sizing to the account.
        /// A script that returns 0 for every order has asked for no trade.
        /// </summary>
        public decimal? Quantity { get; private set; }

        /// <summary>
        /// Explicit execution price, or null to fill at the run's configured price field.
        /// </summary>
        public decimal? Price { get; private set; }

        /// <summary>
        /// The script's own words for this decision, carried onto the fill so the UI log can
        /// say why a trade happened instead of only that it did.
        /// </summary>
        public string Reason { get; private set; } = string.Empty;

        /// <summary>True when the script asked for something actionable.</summary>
        public bool IsActionable => Requested != Action.None;

        /// <summary>
        /// The order key inside a returned script row. Reserving one key keeps the signal and the
        /// order separable: a row can carry both a decision and the data that justified it.
        /// </summary>
        public const string RowKey = "order";

        /// <summary>
        /// Reads the "order" key out of a row a script returned. Returns null when the row has
        /// no order, which is the normal case and is not an error.
        /// </summary>
        /// <exception cref="StrategyOrderFormatException">
        /// The order is present but unreadable. Throwing rather than guessing is deliberate: a
        /// misread order is a silent strategy change, and the row is the only place the
        /// developer's intent exists.
        /// </exception>
        public static StrategyOrder FromRow(Dictionary<string, object> row)
        {
            if (row == null || !row.TryGetValue(RowKey, out var raw) || raw == null)
            {
                return null;
            }

            if (raw is not Dictionary<string, object> order)
            {
                throw new StrategyOrderFormatException(
                    $"'{RowKey}' must be a dict, e.g. {{\"action\": \"buy\", \"quantity\": 0.5}}; " +
                    $"got {Describe(raw)}.");
            }

            return FromDict(order);
        }

        /// <summary>Reads an order from an already-unwrapped dict.</summary>
        public static StrategyOrder FromDict(Dictionary<string, object> order)
        {
            if (order == null)
            {
                return null;
            }

            var parsed = new StrategyOrder();

            if (order.TryGetValue("action", out var actionValue) && actionValue != null)
            {
                parsed.Requested = ParseAction(actionValue);
            }

            if (order.TryGetValue("quantity", out var quantityValue) && quantityValue != null)
            {
                parsed.Quantity = ParseDecimal(quantityValue, "quantity");
                if (parsed.Quantity.Value < 0m)
                {
                    throw new StrategyOrderFormatException(
                        $"'quantity' cannot be negative ({parsed.Quantity.Value}); direction comes " +
                        "from 'action', so use action 'sell' with a positive quantity to size a " +
                        "short, or omit it to let the account size the order.");
                }
            }

            if (order.TryGetValue("price", out var priceValue) && priceValue != null)
            {
                parsed.Price = ParseDecimal(priceValue, "price");
                if (parsed.Price.Value <= 0m)
                {
                    throw new StrategyOrderFormatException(
                        $"'price' must be positive; got {parsed.Price.Value}. Omit it to fill at " +
                        "the run's configured price field.");
                }
            }

            if (order.TryGetValue("reason", out var reasonValue) && reasonValue != null)
            {
                parsed.Reason = Convert.ToString(reasonValue, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            // A zero size is a deliberate "do not trade this tick", distinct from sending no
            // order at all, so it is honoured rather than rejected.
            //
            // Only for buy and sell: a close is defined as "flat", so a size means nothing to it,
            // and letting a zero cancel a close would make a script that computes its size
            // dynamically unable to close at all when that size rounds to zero.
            if (parsed.Quantity == 0m && parsed.Requested != Action.Close)
            {
                parsed.Requested = Action.None;
            }

            return parsed.Requested == Action.None ? null : parsed;
        }

        /// <summary>
        /// Resolves the order into a signed target quantity for the account.
        /// </summary>
        /// <param name="currentQuantity">Signed quantity already held in this instrument</param>
        /// <param name="accountQuantity">
        /// Size to use when the script did not specify one: either an absolute base-unit amount
        /// or a fraction of equity, depending on how the run was configured.
        /// </param>
        /// <returns>Signed target quantity, or null when the order resolves to no trade</returns>
        public decimal? ResolveTarget(decimal currentQuantity, decimal accountQuantity)
        {
            if (!IsActionable)
            {
                return null;
            }

            switch (Requested)
            {
                case Action.Close:
                    return 0m;

                case Action.Buy:
                {
                    var size = Quantity ?? accountQuantity;
                    if (size <= 0m)
                    {
                        return null;
                    }

                    // Buy means "be more long". A script sending a bare 'sell' repeatedly should
                    // not silently flip a long into a growing short, so the size is added to the
                    // current position rather than replacing it; flipping is what a deliberate
                    // negative target is for.
                    return currentQuantity + size;
                }

                case Action.Sell:
                {
                    var size = Quantity ?? accountQuantity;
                    if (size <= 0m)
                    {
                        return null;
                    }

                    return currentQuantity - size;
                }

                default:
                    return null;
            }
        }

        private static Action ParseAction(object value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant();
            switch (text)
            {
                case null:
                case "":
                case "none":
                case "hold":
                case "flat":
                    return Action.None;
                case "buy":
                case "long":
                case "open_long":
                    return Action.Buy;
                case "sell":
                case "short":
                case "open_short":
                    return Action.Sell;
                case "close":
                case "exit":
                case "flatten":
                    return Action.Close;
                default:
                    throw new StrategyOrderFormatException(
                        $"Unknown order action '{text}'. Use 'buy', 'sell', 'close', or omit it.");
            }
        }

        private static decimal ParseDecimal(object value, string field)
        {
            switch (value)
            {
                case decimal d:
                    return d;
                case double dd:
                    return (decimal)dd;
                case float f:
                    return (decimal)f;
                case int i:
                    return i;
                case long l:
                    return l;
                case bool:
                    throw new StrategyOrderFormatException($"'{field}' must be a number, not a bool.");
                case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    throw new StrategyOrderFormatException(
                        $"'{field}' must be a number; got {Describe(value)}.");
            }
        }

        private static string Describe(object value)
        {
            return value == null ? "None" : $"{value.GetType().Name} ({value})";
        }
    }

    /// <summary>
    /// Raised when a script's order cannot be read. Thrown with the offending value named so the
    /// developer can see which line of their script produced it.
    /// </summary>
    public sealed class StrategyOrderFormatException : Exception
    {
        public StrategyOrderFormatException(string message) : base(message)
        {
        }

        public StrategyOrderFormatException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
