using System.Text.Json;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Securities;

namespace QuantConnect.Research.Engine.MarketState
{
    /// <summary>
    /// Serializes <see cref="MarketState"/> to/from a compact JSON snapshot for checkpointing/resume.
    /// The snapshot captures every field that can influence observation, feature, or label output —
    /// including bounded recent trades/quotes and order book levels — so a resumed execution is
    /// bit-identical to an uninterrupted one (see ChunkInvariance semantics).
    /// </summary>
    public static class MarketStateSerialization
    {
        private sealed class LevelSnapshot
        {
            public decimal Price { get; set; }
            public decimal Quantity { get; set; }
            public int? OrderCount { get; set; }
        }

        private sealed class TradeSnapshot
        {
            public DateTime Timestamp { get; set; }
            public decimal Price { get; set; }
            public decimal Quantity { get; set; }
            public long? SequenceNumber { get; set; }
        }

        private sealed class QuoteSnapshot
        {
            public DateTime Timestamp { get; set; }
            public decimal BidPrice { get; set; }
            public decimal BidSize { get; set; }
            public decimal AskPrice { get; set; }
            public decimal AskSize { get; set; }
            public long? SequenceNumber { get; set; }
        }

        private sealed class StateSnapshot
        {
            public string Symbol { get; set; } = string.Empty;
            public string AssetClass { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
            public decimal LastPrice { get; set; }
            public decimal BidPrice { get; set; }
            public decimal AskPrice { get; set; }
            public decimal Volume { get; set; }
            public long TradeCount { get; set; }
            public long QuoteUpdateCount { get; set; }
            public decimal BidSize { get; set; }
            public decimal AskSize { get; set; }
            public int MaxRecentTrades { get; set; } = 1000;
            public int MaxRecentQuotes { get; set; } = 1000;
            public List<LevelSnapshot> BidLevels { get; set; } = new();
            public List<LevelSnapshot> AskLevels { get; set; } = new();
            public List<TradeSnapshot> RecentTrades { get; set; } = new();
            public List<QuoteSnapshot> RecentQuotes { get; set; } = new();
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Serializes market state to a compact JSON string, or null when state is null.
        /// </summary>
        public static string ToJson(MarketState state)
        {
            if (state == null) return null;

            var snapshot = new StateSnapshot
            {
                Symbol = state.Symbol?.Value ?? string.Empty,
                AssetClass = state.AssetClass.ToString(),
                Timestamp = state.Timestamp,
                LastPrice = state.LastPrice,
                BidPrice = state.BidPrice,
                AskPrice = state.AskPrice,
                Volume = state.Volume,
                TradeCount = state.TradeCount,
                QuoteUpdateCount = state.QuoteUpdateCount,
                BidSize = state.BidSize,
                AskSize = state.AskSize,
                MaxRecentTrades = state.MaxRecentTrades,
                MaxRecentQuotes = state.MaxRecentQuotes,
                BidLevels = state.BidLevels_List.Select(l => new LevelSnapshot
                {
                    Price = l.Price, Quantity = l.Quantity, OrderCount = l.OrderCount
                }).ToList(),
                AskLevels = state.AskLevels_List.Select(l => new LevelSnapshot
                {
                    Price = l.Price, Quantity = l.Quantity, OrderCount = l.OrderCount
                }).ToList(),
                RecentTrades = state.RecentTrades.Select(t => new TradeSnapshot
                {
                    Timestamp = t.Timestamp, Price = t.Price, Quantity = t.Quantity,
                    SequenceNumber = t.SequenceNumber
                }).ToList(),
                RecentQuotes = state.RecentQuotes.Select(q => new QuoteSnapshot
                {
                    Timestamp = q.Timestamp, BidPrice = q.BidPrice, BidSize = q.BidSize,
                    AskPrice = q.AskPrice, AskSize = q.AskSize, SequenceNumber = q.SequenceNumber
                }).ToList()
            };

            return JsonSerializer.Serialize(snapshot, JsonOptions);
        }

        /// <summary>
        /// Restores market state from a JSON snapshot produced by <see cref="ToJson"/>,
        /// or null when the payload is null/empty.
        /// </summary>
        public static MarketState FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            StateSnapshot snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<StateSnapshot>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            if (snapshot == null) return null;

            var state = new MarketState
            {
                Symbol = string.IsNullOrEmpty(snapshot.Symbol)
                    ? Symbol.Empty
                    : QuantConnect.Symbol.Create(snapshot.Symbol, SecurityType.Crypto, Market.Bybit),
                AssetClass = Enum.TryParse<SecurityType>(snapshot.AssetClass, out var assetClass)
                    ? assetClass
                    : SecurityType.Crypto,
                Timestamp = snapshot.Timestamp,
                LastPrice = snapshot.LastPrice,
                BidPrice = snapshot.BidPrice,
                AskPrice = snapshot.AskPrice,
                Volume = snapshot.Volume,
                TradeCount = snapshot.TradeCount,
                QuoteUpdateCount = snapshot.QuoteUpdateCount,
                BidSize = snapshot.BidSize,
                AskSize = snapshot.AskSize,
                MaxRecentTrades = snapshot.MaxRecentTrades,
                MaxRecentQuotes = snapshot.MaxRecentQuotes
            };

            state.RestoreBook(
                snapshot.BidLevels.Select(l => new OrderBookLevel
                {
                    Price = l.Price, Quantity = l.Quantity, OrderCount = l.OrderCount
                }),
                snapshot.AskLevels.Select(l => new OrderBookLevel
                {
                    Price = l.Price, Quantity = l.Quantity, OrderCount = l.OrderCount
                }));

            state.RestoreRecentTrades(snapshot.RecentTrades.Select(t => new TradeEvent
            {
                Timestamp = t.Timestamp,
                Symbol = state.Symbol,
                AssetClass = state.AssetClass,
                Provenance = new DataProvenance { Venue = state.Symbol.ID.Market, Symbol = state.Symbol, AssetClass = state.AssetClass },
                SequenceNumber = t.SequenceNumber,
                Price = t.Price,
                Quantity = t.Quantity
            }));

            state.RestoreRecentQuotes(snapshot.RecentQuotes.Select(q => new QuoteEvent
            {
                Timestamp = q.Timestamp,
                Symbol = state.Symbol,
                AssetClass = state.AssetClass,
                Provenance = new DataProvenance { Venue = state.Symbol.ID.Market, Symbol = state.Symbol, AssetClass = state.AssetClass },
                SequenceNumber = q.SequenceNumber,
                BidPrice = q.BidPrice,
                BidSize = q.BidSize,
                AskPrice = q.AskPrice,
                AskSize = q.AskSize
            }));

            return state;
        }
    }
}