using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// Factory for creating MarketEvent instances from Lean data types
    /// </summary>
    public static class MarketEventFactory
    {
        /// <summary>
        /// Creates a MarketEvent from a BaseData instance
        /// </summary>
        public static MarketEvent Create(IBaseData data, DataProvenance provenance = null)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            return data switch
            {
                Tick tick => CreateFromTick(tick, provenance),
                TradeBar tradeBar => CreateFromTradeBar(tradeBar, provenance),
                QuoteBar quoteBar => CreateFromQuoteBar(quoteBar, provenance),
                _ => CreateCustomFromBaseData(data, provenance)
            };
        }

        /// <summary>
        /// Creates a Trade or Quote event from a Tick
        /// </summary>
        public static MarketEvent CreateFromTick(Tick tick, DataProvenance provenance = null)
        {
            if (tick.TickType == TickType.Trade)
            {
                return TradeEvent.FromTick(tick, provenance);
            }
            else if (tick.TickType == TickType.Quote)
            {
                return QuoteEvent.FromTick(tick, provenance);
            }

            // Default to trade event
            return TradeEvent.FromTick(tick, provenance);
        }

        /// <summary>
        /// Creates a BarEvent from a TradeBar
        /// </summary>
        public static BarEvent CreateFromTradeBar(TradeBar tradeBar, DataProvenance provenance = null)
        {
            return BarEvent.FromTradeBar(tradeBar, provenance);
        }

        /// <summary>
        /// Creates a QuoteEvent from a QuoteBar
        /// </summary>
        public static QuoteEvent CreateFromQuoteBar(QuoteBar quoteBar, DataProvenance provenance = null)
        {
            return QuoteEvent.FromQuoteBar(quoteBar, provenance);
        }

        /// <summary>
        /// Creates a CustomMarketEvent from unsupported BaseData types
        /// </summary>
        private static CustomMarketEvent CreateCustomFromBaseData(IBaseData data, DataProvenance provenance)
        {
            return new CustomMarketEvent
            {
                Timestamp = data.Time,
                Symbol = data.Symbol,
                AssetClass = data.Symbol.SecurityType,
                Provenance = provenance ?? new DataProvenance
                {
                    Venue = data.Symbol.ID.Market,
                    Symbol = data.Symbol,
                    AssetClass = data.Symbol.SecurityType,
                    FeedType = data.DataType.ToString()
                },
                CustomEventType = data.GetType().Name,
                Value = data.Value
            };
        }

        /// <summary>
        /// Creates a batch of MarketEvents from an enumerable of BaseData
        /// </summary>
        public static IEnumerable<MarketEvent> CreateBatch(IEnumerable<IBaseData> dataPoints, DataProvenance provenance = null)
        {
            foreach (var data in dataPoints)
            {
                yield return Create(data, provenance);
            }
        }

        /// <summary>
        /// Creates default provenance for a symbol
        /// </summary>
        public static DataProvenance CreateDefaultProvenance(Symbol symbol, string provider = "Unknown", string venue = "Unknown")
        {
            return new DataProvenance
            {
                Provider = provider,
                Venue = venue,
                Symbol = symbol,
                AssetClass = symbol.SecurityType,
                TimestampPrecision = TimestampPrecision.Milliseconds,
                FeedType = "unknown"
            };
        }
    }
}