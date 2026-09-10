using Ionic.Zip;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Research.Engine.LocalData
{
    /// <summary>
    /// Reads Lean local data files (zip format) into MarketEvents, bypassing
    /// the full Lean data enumeration stack. Memory-bounded, streaming per file.
    /// </summary>
    public class LeanDataEventReader
    {
        /// <summary>
        /// Root data folder
        /// </summary>
        public string DataDirectory { get; }

        /// <summary>
        /// Creates a new LeanDataEventReader
        /// </summary>
        /// <param name="dataDirectory">Root Lean data folder. Defaults to Globals.DataFolder</param>
        public LeanDataEventReader(string dataDirectory = null)
        {
            DataDirectory = string.IsNullOrEmpty(dataDirectory) ? Globals.DataFolder : dataDirectory;
            LeanBootstrap.EnsureDataFolder(DataDirectory);
        }

        /// <summary>
        /// Reads all events for a symbol over a date range
        /// </summary>
        public IEnumerable<MarketEvent> Read(Symbol symbol, Resolution resolution, TickType tickType, DateTime start, DateTime end)
        {
            var dataType = LeanData.GetDataType(resolution, tickType);
            var factory = (BaseData)Activator.CreateInstance(dataType);
            var config = BuildConfig(symbol, resolution, tickType, dataType);

            for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
            {
                var zipPath = LeanData.GenerateZipFilePath(DataDirectory, symbol, date, resolution, tickType);
                if (!File.Exists(zipPath))
                {
                    continue;
                }

                var entryName = LeanData.GenerateZipEntryName(symbol, date, resolution, tickType);
                foreach (var evt in ReadFile(factory, config, symbol, date, zipPath, entryName))
                {
                    yield return evt;
                }
            }
        }

        /// <summary>
        /// Builds the config for a symbol/resolution/tick type
        /// </summary>
        private static SubscriptionDataConfig BuildConfig(Symbol symbol, Resolution resolution, TickType tickType, Type dataType)
        {
            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            var dataTimeZone = marketHoursDatabase.GetDataTimeZone(symbol.ID.Market, symbol, symbol.SecurityType);
            var exchangeTimeZone = marketHoursDatabase.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType).TimeZone;

            return new SubscriptionDataConfig(dataType, symbol, resolution,
                dataTimeZone, exchangeTimeZone,
                fillForward: false, extendedHours: true, isInternalFeed: true,
                tickType: tickType);
        }

        /// <summary>
        /// Reads a single zip file into MarketEvents
        /// </summary>
        private static IEnumerable<MarketEvent> ReadFile(BaseData factory, SubscriptionDataConfig config, Symbol symbol, DateTime date, string zipPath, string entryName)
        {
            if (factory.GetType().ImplementsStreamReader())
            {
                using (var zip = new ZipFile(zipPath))
                {
                    foreach (var zipEntry in zip.Where(x => entryName == null || string.Equals(x.FileName, entryName, StringComparison.OrdinalIgnoreCase)))
                    {
                        using (var entryReader = new StreamReader(zipEntry.OpenReader()))
                        {
                            while (!entryReader.EndOfStream)
                            {
                                var dataPoint = factory.Reader(config, entryReader, date, false);
                                dataPoint.Symbol = symbol;
                                yield return MarketEventFactory.Create(dataPoint);
                            }
                        }
                    }
                }
            }
            else
            {
                Ionic.Zip.ZipFile zipFile;
                using (var unzipped = Compression.Unzip(zipPath, entryName, out zipFile))
                {
                    if (unzipped == null)
                        yield break;

                    string line;
                    while ((line = unzipped.ReadLine()) != null)
                    {
                        var dataPoint = factory.Reader(config, line, date, false);
                        dataPoint.Symbol = symbol;
                        yield return MarketEventFactory.Create(dataPoint);
                    }
                }
                zipFile.Dispose();
            }
        }
    }
}