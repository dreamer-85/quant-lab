using System;
using System.IO;

namespace QuantConnect.Research.Engine
{
    /// <summary>
    /// Resolves environment roots (data / output / cache / temp) for research execution.
    /// Resolution order: explicit constructor value &gt; process environment variable &gt; stable default.
    /// The core engine and ResearchJob carry only logical content; physical roots are an environment concern,
    /// so the same job runs identically wherever the engine is deployed (local workstation or cloud VM).
    /// </summary>
    public sealed class ResearchEnvironment
    {
        /// <summary>
        /// Environment variable for the Lean/raw data root (e.g. <c>&lt;lean&gt;/Data</c>)
        /// </summary>
        public const string DataRootEnvVar = "QUANTLAB_DATA_ROOT";

        /// <summary>
        /// Environment variable for the research output root (results, checkpoints, manifest)
        /// </summary>
        public const string OutputRootEnvVar = "QUANTLAB_OUTPUT_ROOT";

        /// <summary>
        /// Environment variable for the cache/scratch root
        /// </summary>
        public const string CacheRootEnvVar = "QUANTLAB_CACHE_ROOT";

        /// <summary>
        /// Environment variable for the temporary root
        /// </summary>
        public const string TempRootEnvVar = "QUANTLAB_TEMP_ROOT";

        /// <summary>
        /// Root Lean/raw data folder
        /// </summary>
        public string DataRoot { get; }

        /// <summary>
        /// Root folder for research outputs (results, checkpoints, manifest)
        /// </summary>
        public string OutputRoot { get; }

        /// <summary>
        /// Root folder for caches/scratch space
        /// </summary>
        public string CacheRoot { get; }

        /// <summary>
        /// Root folder for temporary files
        /// </summary>
        public string TempRoot { get; }

        /// <summary>
        /// Creates a new environment. Omitted roots fall back to environment variables, then defaults.
        /// Defaults preserve pre-Phase-14 behavior: data = Lean <see cref="Globals.DataFolder"/>,
        /// output = <c>%TEMP%/QuantLab</c>.
        /// </summary>
        public ResearchEnvironment(string dataRoot = null, string outputRoot = null, string cacheRoot = null, string tempRoot = null)
        {
            var output = First(outputRoot, GetEnv(OutputRootEnvVar), Path.Combine(Path.GetTempPath(), "QuantLab"));
            OutputRoot = output;
            var data = First(dataRoot, GetEnv(DataRootEnvVar), Globals.DataFolder);
            DataRoot = data;
            CacheRoot = First(cacheRoot, GetEnv(CacheRootEnvVar), Path.Combine(output, "cache"));
            TempRoot = First(tempRoot, GetEnv(TempRootEnvVar), Path.GetTempPath());
        }

        /// <summary>
        /// Creates a directory under the cache root
        /// </summary>
        public string GetCachePath(string relativePath)
        {
            var full = Path.Combine(CacheRoot, relativePath);
            Directory.CreateDirectory(full);
            return full;
        }

        /// <summary>
        /// Creates a directory under the temp root
        /// </summary>
        public string GetTempPath(string relativePath)
        {
            var full = Path.Combine(TempRoot, relativePath);
            Directory.CreateDirectory(full);
            return full;
        }

        private static string GetEnv(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name);
            }
            catch
            {
                return null;
            }
        }

        private static string First(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
            return string.Empty;
        }
    }
}