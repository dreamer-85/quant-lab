namespace QuantConnect.Research.Engine.Observations
{
    /// <summary>
    /// How much of the state carried by an <see cref="Observation"/> is actually new.
    ///
    /// This is the engine's answer to Lean's <c>Slice.IsAllAssetDataAvailable</c> /
    /// <c>DataQuality</c>: without it a caller cannot tell a period that received fresh market
    /// data from a period that merely repeated the last known state, so a filled period is
    /// indistinguishable from a real one. Every observation is stamped, and the value is
    /// published in the output row and in the Python payload so a strategy can filter on it.
    ///
    /// The distinction matters because the observation grid is uniform in wall-clock time while
    /// the data is not. A 100ms grid over minute bars is ~99.8% <see cref="Filled"/>: the rolling
    /// windows a researcher believes are "20 periods" actually span far more time than intended,
    /// and that is only visible if the engine says so.
    /// </summary>
    public enum DataQuality
    {
        /// <summary>
        /// No new events in this period and no prior state to carry forward, so the observation
        /// holds nothing usable. The stream began before the first event arrived.
        /// </summary>
        Missing = 0,

        /// <summary>
        /// No new events in this period; the previous state was carried forward unchanged. The
        /// numbers are real but stale, and any period-scoped aggregate (volume, VWAP, high, low)
        /// describes an empty period rather than the market.
        /// </summary>
        Filled = 1,

        /// <summary>
        /// One or more new events in this period; the state reflects data observed at or before
        /// this period's grid time.
        /// </summary>
        Fresh = 2
    }
}
