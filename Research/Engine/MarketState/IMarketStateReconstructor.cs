using QuantConnect.Research.Engine.Events;

namespace QuantConnect.Research.Engine.MarketState
{
    /// <summary>
    /// Interface for reconstructing market state from events.
    /// Different implementations for different market structures.
    /// </summary>
    public interface IMarketStateReconstructor
    {
        /// <summary>
        /// Creates an initial empty state
        /// </summary>
        MarketState CreateInitialState();

        /// <summary>
        /// Updates state based on a new event
        /// </summary>
        void UpdateState(MarketState state, MarketEvent evt);

        /// <summary>
        /// Gets the market structure type this reconstructor handles
        /// </summary>
        MarketStructureType StructureType { get; }
    }

    /// <summary>
    /// Market structure types
    /// </summary>
    public enum MarketStructureType
    {
        /// <summary>
        /// Central limit order book (crypto, futures, equities)
        /// </summary>
        CentralLimitOrderBook,

        /// <summary>
        /// Quote-based (FX spot)
        /// </summary>
        QuoteBased,

        /// <summary>
        /// Hybrid (some order book, some quote-based)
        /// </summary>
        Hybrid,

        /// <summary>
        /// Unknown/custom
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Reconstructor for central limit order book markets
    /// </summary>
    public class CLOBReconstructor : IMarketStateReconstructor
    {
        public MarketStructureType StructureType => MarketStructureType.CentralLimitOrderBook;

        public MarketState CreateInitialState()
        {
            return new MarketState();
        }

        public void UpdateState(MarketState state, MarketEvent evt)
        {
            state.UpdateFromEvent(evt);
        }
    }

    /// <summary>
    /// Reconstructor for quote-based markets (FX)
    /// </summary>
    public class QuoteBasedReconstructor : IMarketStateReconstructor
    {
        public MarketStructureType StructureType => MarketStructureType.QuoteBased;

        public MarketState CreateInitialState()
        {
            return new MarketState();
        }

        public void UpdateState(MarketState state, MarketEvent evt)
        {
            state.UpdateFromEvent(evt);
        }
    }

    /// <summary>
    /// Factory for creating appropriate state reconstructors
    /// </summary>
    public static class MarketStateReconstructorFactory
    {
        /// <summary>
        /// Creates a reconstructor based on asset class
        /// </summary>
        public static IMarketStateReconstructor Create(QuantConnect.SecurityType assetClass)
        {
            return assetClass switch
            {
                QuantConnect.SecurityType.Crypto => new CLOBReconstructor(),
                QuantConnect.SecurityType.CryptoFuture => new CLOBReconstructor(),
                QuantConnect.SecurityType.Future => new CLOBReconstructor(),
                QuantConnect.SecurityType.Equity => new CLOBReconstructor(),
                QuantConnect.SecurityType.Option => new CLOBReconstructor(),
                QuantConnect.SecurityType.FutureOption => new CLOBReconstructor(),
                QuantConnect.SecurityType.IndexOption => new CLOBReconstructor(),
                QuantConnect.SecurityType.Forex => new QuoteBasedReconstructor(),
                QuantConnect.SecurityType.Cfd => new QuoteBasedReconstructor(),
                QuantConnect.SecurityType.Index => new QuoteBasedReconstructor(),
                _ => new CLOBReconstructor()
            };
        }

        /// <summary>
        /// Creates a reconstructor based on market structure type
        /// </summary>
        public static IMarketStateReconstructor Create(MarketStructureType structureType)
        {
            return structureType switch
            {
                MarketStructureType.CentralLimitOrderBook => new CLOBReconstructor(),
                MarketStructureType.QuoteBased => new QuoteBasedReconstructor(),
                _ => new CLOBReconstructor()
            };
        }
    }
}