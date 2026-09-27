using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Replay;

namespace QuantConnect.Tests.Research.EngineTests
{
    [TestFixture]
    public class Diag
    {
        [Test]
        public void Run()
        {
            var s = Symbol.Create("BTCUSDT", SecurityType.Crypto, Market.Bybit);
            var start = DateTime.Parse("2022-12-13T00:00:00");
            var events = new List<MarketEvent>
            {
                new TradeEvent { Timestamp = start, Symbol = s, AssetClass = SecurityType.Crypto, Price = 100m, Quantity = 1, SequenceNumber = 1 },
            };
            for (var m = 1; m <= 50; m++) events.Add(new ClockTickEvent(start.AddMinutes(m)));

            var e = new EventReplayEngine(new ReplayConfiguration
            {
                StartTime = start, EndTime = start.AddMinutes(50),
                Symbols = new List<Symbol> { s },
                ObservationInterval = TimeSpan.FromMinutes(1), MaxObservations = 5
            }, MarketStateReconstructorFactory.Create(SecurityType.Crypto));

            var res = e.Replay(events.OrderBy(x => x.Timestamp)).ToList();
            TestContext.Out.WriteLine("count=" + res.Count);
            foreach (var r in res) TestContext.Out.WriteLine("  " + r.Timestamp.ToString("HH:mm") + " " + r.Quality);
            var st = e.GetStatistics();
            TestContext.Out.WriteLine($"trunc={st.Truncated} obs={st.ObservationsEmitted} events={st.EventsProcessed}");
        }
    }
}
