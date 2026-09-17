using System.Text.Json;

namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Parsed Bybit REST / WebSocket payloads (public market data endpoints).
    /// Parsers are pure so they can be unit-tested without any network.
    /// </summary>
    internal static class BybitJson
    {
        /// <summary>
        /// Parses a kline row: [start(ms), open, high, low, close, volume, turnover].
        /// </summary>
        public static bool TryParseKlineRow(JsonElement row, out BybitKline kline)
        {
            kline = default;
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 6)
            {
                return false;
            }

            var values = row.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
            if (!long.TryParse(values[0], out var startMs)
                || !decimal.TryParse(values[1], out var open)
                || !decimal.TryParse(values[2], out var high)
                || !decimal.TryParse(values[3], out var low)
                || !decimal.TryParse(values[4], out var close)
                || !decimal.TryParse(values[5], out var volume))
            {
                return false;
            }

            kline = new BybitKline
            {
                StartMs = startMs,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            };
            return true;
        }

        /// <summary>
        /// Parses a recent-trade item. Accepts both the REST field names
        /// (execId, symbol, price, size, side, time) and the WebSocket publicTrade single-letter
        /// field names (i/L, s, p, v, S, T).
        /// </summary>
        public static bool TryParseTradeItem(JsonElement item, out BybitTrade trade)
        {
            trade = default;
            if (item.ValueKind != JsonValueKind.Object
                || !decimal.TryParse(GetStringAny(item, "price", "p"), out var price)
                || !decimal.TryParse(GetStringAny(item, "size", "v"), out var size)
                || !long.TryParse(GetStringAny(item, "time", "T"), out var timeMs))
            {
                return false;
            }

            trade = new BybitTrade
            {
                EventId = GetStringAny(item, "execId", "i", "L") ?? string.Empty,
                Symbol = GetStringAny(item, "symbol", "s") ?? string.Empty,
                Price = price,
                Size = size,
                Side = GetStringAny(item, "side", "S") ?? string.Empty,
                TimeMs = timeMs,
                IsBlockTrade = item.GetPropertyOrNull("isBlockTrade")?.GetBoolean() ?? false
            };
            return true;
        }

        private static string GetStringAny(JsonElement item, params string[] names)
        {
            foreach (var name in names)
            {
                var property = item.GetPropertyOrNull(name);
                if (property.HasValue)
                {
                    var value = GetStringValue(property.Value);
                    if (value != null)
                    {
                        return value;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Reads a property value as text, tolerating both JSON strings and numbers
        /// (Bybit switches between the two across its REST and WebSocket payloads).
        /// </summary>
        private static string GetStringValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null
            };
        }

        private static string GetStringValue(JsonElement? element)
        {
            return element.HasValue ? GetStringValue(element.Value) : null;
        }

        /// <summary>
        /// Parses a level item: [price, size, ...(optional order count)].
        /// </summary>
        public static bool TryParseLevel(JsonElement item, out BybitLevel level)
        {
            level = default;
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 2)
            {
                return false;
            }

            var values = item.EnumerateArray().Select(GetStringValue).ToArray();
            if (!decimal.TryParse(values[0], out var price) || !decimal.TryParse(values[1], out var size))
            {
                return false;
            }

            int? orderCount = null;
            if (values.Length > 2 && int.TryParse(values[2], out var count))
            {
                orderCount = count;
            }

            level = new BybitLevel { Price = price, Size = size, OrderCount = orderCount };
            return true;
        }

        /// <summary>
        /// Parses a public WebSocket frame into a typed view.
        /// </summary>
        public static bool TryParseWsFrame(string json, out BybitWsFrame frame)
        {
            frame = default;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("topic", out var topicElement) || string.IsNullOrWhiteSpace(topicElement.GetString()))
            {
                return false;
            }

            var topic = topicElement.GetString();
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            var tsMs = root.TryGetProperty("ts", out var tsElement) ? tsElement.GetInt64() : 0L;
            var data = root.TryGetProperty("data", out var dataElement) ? dataElement : default;

            var result = new BybitWsFrame
            {
                Topic = topic,
                Type = type,
                TsMs = tsMs,
                Trades = new List<BybitTrade>(),
                Bids = new List<BybitLevel>(),
                Asks = new List<BybitLevel>()
            };

            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                // publicTrade / kline frames wrap their payload(s) in an array.
                foreach (var item in data.EnumerateArray())
                {
                    if (topic.StartsWith("publicTrade", StringComparison.Ordinal)
                        && TryParseTradeItem(item, out var trade))
                    {
                        result.Trades.Add(trade);
                    }
                    else if (topic.StartsWith("kline", StringComparison.Ordinal)
                        && TryParseKlineItem(item, out var kline))
                    {
                        result.Klines.Add(kline);
                    }
                }
            }
            else if (data.ValueKind == JsonValueKind.Object)
            {
                // orderbook frames carry {s, b, a, u, seq}.
                if (data.TryGetProperty("b", out var bidsElement) && bidsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in bidsElement.EnumerateArray())
                    {
                        if (TryParseLevel(item, out var level))
                        {
                            result.Bids.Add(level);
                        }
                    }
                }
                if (data.TryGetProperty("a", out var asksElement) && asksElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in asksElement.EnumerateArray())
                    {
                        if (TryParseLevel(item, out var level))
                        {
                            result.Asks.Add(level);
                        }
                    }
                }
            }

            frame = result;
            return result.Trades.Count > 0 || result.Klines.Count > 0 || result.Bids.Count > 0 || result.Asks.Count > 0;
        }

        private static bool TryParseKlineItem(JsonElement item, out BybitKline kline)
        {
            kline = default;
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!long.TryParse(GetStringValue(item.GetPropertyOrNull("start")), out var startMs)
                || !decimal.TryParse(GetStringValue(item.GetPropertyOrNull("open")), out var open)
                || !decimal.TryParse(GetStringValue(item.GetPropertyOrNull("high")), out var high)
                || !decimal.TryParse(GetStringValue(item.GetPropertyOrNull("low")), out var low)
                || !decimal.TryParse(GetStringValue(item.GetPropertyOrNull("close")), out var close)
                || !decimal.TryParse(GetStringValue(item.GetPropertyOrNull("volume")), out var volume))
            {
                return false;
            }

            kline = new BybitKline
            {
                StartMs = startMs,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            };
            return true;
        }
    }

    /// <summary>
    /// Helper to read an optional property from a JSON element.
    /// </summary>
    internal static class JsonElementExtensions
    {
        public static JsonElement? GetPropertyOrNull(this JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var property))
            {
                return property;
            }
            return null;
        }
    }

    /// <summary>
    /// Parsed kline row.
    /// </summary>
    public readonly struct BybitKline
    {
        public long StartMs { get; init; }

        public decimal Open { get; init; }

        public decimal High { get; init; }

        public decimal Low { get; init; }

        public decimal Close { get; init; }

        public decimal Volume { get; init; }
    }

    /// <summary>
    /// Parsed recent trade.
    /// </summary>
    public readonly struct BybitTrade
    {
        public string EventId { get; init; }

        public string Symbol { get; init; }

        public decimal Price { get; init; }

        public decimal Size { get; init; }

        public string Side { get; init; }

        public long TimeMs { get; init; }

        public bool IsBlockTrade { get; init; }
    }

    /// <summary>
    /// Parsed order book level.
    /// </summary>
    public readonly struct BybitLevel
    {
        public decimal Price { get; init; }

        public decimal Size { get; init; }

        public int? OrderCount { get; init; }
    }

    /// <summary>
    /// Parsed public WebSocket frame.
    /// </summary>
    public class BybitWsFrame
    {
        public string Topic { get; set; }

        public string Type { get; set; }

        public long TsMs { get; set; }

        public List<BybitTrade> Trades { get; set; } = new();

        public List<BybitKline> Klines { get; set; } = new();

        public List<BybitLevel> Bids { get; set; } = new();

        public List<BybitLevel> Asks { get; set; } = new();
    }
}