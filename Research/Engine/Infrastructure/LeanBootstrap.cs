using QuantConnect.Configuration;
using QuantConnect.Securities;

namespace QuantConnect.Research.Engine
{
    /// <summary>
    /// Bootstraps Lean process-global state to a given data folder.
    /// Streaming local data reads depend on <see cref="Globals.DataFolder"/> so the static
    /// MarketHoursDatabase / SymbolPropertiesDatabase caches resolve from the user's data
    /// directory instead of the process working directory.
    /// </summary>
    public static class LeanBootstrap
    {
        private static readonly object Lock = new object();
        private static string _configuredDataFolder;

        /// <summary>
        /// Ensures <see cref="Globals.DataFolder"/> and the derived static databases point at
        /// the given root data folder. Idempotent; only re-bootstraps when the folder changes.
        /// </summary>
        /// <param name="dataDirectory">Root Lean data folder</param>
        public static void EnsureDataFolder(string dataDirectory)
        {
            var fullPath = Path.GetFullPath(dataDirectory);
            lock (Lock)
            {
                if (string.Equals(_configuredDataFolder, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Config.Set("data-folder", fullPath);
                Globals.Reset();
                MarketHoursDatabase.Reset();
                SymbolPropertiesDatabase.Reset();
                _configuredDataFolder = fullPath;
            }
        }
    }
}