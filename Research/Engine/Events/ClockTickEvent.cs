namespace QuantConnect.Research.Engine.Events
{
    /// <summary>
    /// A clock advance carrying no market data.
    ///
    /// The observation frontier normally moves because an event arrived. On a live feed a quiet
    /// market produces no events, so without a clock the frontier would stall and no period would be
    /// emitted until trading resumes — a live run would then disagree with a backtest over the same
    /// events, and a strategy waiting on the next period would stall for as long as the market is
    /// quiet. A tick says only "the clock has reached this time", which lets the frontier close the
    /// periods that have fully elapsed and emit them as padding.
    ///
    /// It is deliberately not a market event in substance: it is never counted as processed, never
    /// applied to state, never folded into an observation's events, and never exposed to a strategy.
    /// Its only effect is to let time pass.
    /// </summary>
    public sealed class ClockTickEvent : MarketEvent
    {
        public override MarketEventType EventType => MarketEventType.ClockTick;

        public ClockTickEvent(DateTime timestamp)
        {
            Timestamp = timestamp;
        }

        public override MarketEvent Clone() => new ClockTickEvent(Timestamp);
    }
}
