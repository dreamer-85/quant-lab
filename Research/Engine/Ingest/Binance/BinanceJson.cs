using System.Globalization;
using System.Text.Json;

namespace QuantConnect.Research.Engine.Ingest.Binance
{
    /// <summary>
    /// Parsed Binance WebSocket payloads (public market data streams).
    /// Parsers are pure so they can be unit-tested without any network.
    /// </summary>
    internal static class BinanceJson
    {
        /// <summary>
        /// Parses a WebSocket frame into a typed view. Accepts both combined-stream frames
        /// ({"stream":"btcusdt@trade","data":{...}}) and raw single-stream payloads.
        /// Returns false for subscription pongs, control frames and unknown event types.
        /// </summary>
        public static bool TryParseWsFrame(string json, out BinanceWsFrame frame)
        {
            frame = default;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement data;
            string stream = string.Empty;
            if (root.TryGetProperty("data", out var wrapped) && root.TryGetProperty("stream", out var streamElement))
            {
                stream = streamElement.GetString() ?? string.Empty;
                data = wrapped;
            }
            else
            {
                data = root;
            }

            if (data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var eventName = GetString(data, "e") ?? string.Empty;
            var result = new BinanceWsFrame { Stream = stream, EventName = eventName };

            switch (eventName)
            {
                case "trade":
                    if (!TryParseTrade(data, out var trade))
                    {
                        return false;
                    }
                    result.Trade = trade;
                    break;
                case "kline":
                    if (!data.TryGetProperty("k", out var kline) || !TryParseBar(kline, out var bar))
                    {
                        return false;
                    }
                    result.Bar = bar;
                    break;
                default:
                    // bookTicker frames carry no event name (only u/s/b/B/a/A); detect by shape.
                    if (TryParseQuote(data, out var quote))
                    {
                        result.Quote = quote;
                    }
                    else
                    {
                        // Pongs, subscription acks and unknown streams are not market frames.
                        return false;
                    }
                    break;
            }

            frame = result;
            return true;
        }

        private static bool TryParseTrade(JsonElement data, out BinanceTrade trade)
        {
            trade = default;
            if (!TryGetMs(data, "T", "E", out var tsMs)
                || !TryGetDecimal(data, "p", out var price)
                || !TryGetDecimal(data, "q", out var quantity))
            {
                return false;
            }

            trade = new BinanceTrade
            {
                TimestampMs = tsMs,
                Price = price,
                Quantity = quantity,
                BuyerIsMaker = data.TryGetProperty("m", out var m) && m.ValueKind == JsonValueKind.True,
                EventId = GetString(data, "t") ?? tsMs.ToString(CultureInfo.InvariantCulture)
            };
            return true;
        }

        private static bool TryParseQuote(JsonElement data, out BinanceQuote quote)
        {
            quote = default;
            if (!TryGetDecimal(data, "b", out var bid)
                || !TryGetDecimal(data, "B", out var bidSize)
                || !TryGetDecimal(data, "a", out var ask)
                || !TryGetDecimal(data, "A", out var askSize))
            {
                return false;
            }

            long updateId = 0;
            if (data.TryGetProperty("u", out var u) && u.ValueKind == JsonValueKind.Number)
            {
                updateId = u.GetInt64();
            }

            quote = new BinanceQuote
            {
                TimestampMs = TryGetMs(data, "T", "E", out var tsMs) ? tsMs : 0L,
                Bid = bid,
                BidSize = bidSize,
                Ask = ask,
                AskSize = askSize,
                UpdateId = updateId
            };
            return true;
        }

        private static bool TryParseBar(JsonElement k, out BinanceBar bar)
        {
            bar = default;
            if (!k.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number
                || !TryGetDecimal(k, "o", out var open)
                || !TryGetDecimal(k, "h", out var high)
                || !TryGetDecimal(k, "l", out var low)
                || !TryGetDecimal(k, "c", out var close)
                || !TryGetDecimal(k, "v", out var volume))
            {
                return false;
            }

            bar = new BinanceBar
            {
                OpenTimeMs = t.GetInt64(),
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            };
            return true;
        }

        private static bool TryGetMs(JsonElement element, string first, string second, out long ms)
        {
            if (TryGetString(element, first, out var a) && long.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out ms))
            {
                return true;
            }
            if (TryGetString(element, second, out var b) && long.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out ms))
            {
                return true;
            }
            ms = 0;
            return false;
        }

        private static bool TryGetDecimal(JsonElement element, string name, out decimal value)
        {
            if (TryGetString(element, name, out var text)
                && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
            value = 0;
            return false;
        }

        private static bool TryGetString(JsonElement element, string name, out string value)
        {
            if (element.TryGetProperty(name, out var property))
            {
                value = property.ValueKind switch
                {
                    JsonValueKind.String => property.GetString(),
                    JsonValueKind.Number => property.GetRawText(),
                    _ => null
                };
                return value != null;
            }
            value = null;
            return false;
        }

        private static string GetString(JsonElement element, string name)
        {
            return TryGetString(element, name, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Parsed Binance market-data frame (one of Trade/Quote/Bar is populated).
    /// </summary>
    public class BinanceWsFrame
    {
        public string Stream { get; set; } = string.Empty;

        public string EventName { get; set; } = string.Empty;

        public BinanceTrade? Trade { get; set; }

        public BinanceQuote? Quote { get; set; }

        public BinanceBar? Bar { get; set; }
    }

    public readonly struct BinanceTrade
    {
        public long TimestampMs { get; init; }

        public decimal Price { get; init; }

        public decimal Quantity { get; init; }

        public bool BuyerIsMaker { get; init; }

        public string EventId { get; init; }
    }

    public readonly struct BinanceQuote
    {
        public long TimestampMs { get; init; }

        public decimal Bid { get; init; }

        public decimal BidSize { get; init; }

        public decimal Ask { get; init; }

        public decimal AskSize { get; init; }

        public long UpdateId { get; init; }
    }

    public readonly struct BinanceBar
    {
        public long OpenTimeMs { get; init; }

        public decimal Open { get; init; }

        public decimal High { get; init; }

        public decimal Low { get; init; }

        public decimal Close { get; init; }

        public decimal Volume { get; init; }
    }
}