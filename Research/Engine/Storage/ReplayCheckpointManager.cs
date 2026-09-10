using System.Text.Json;
using QuantConnect.Research.Engine.Jobs;

namespace QuantConnect.Research.Engine.Storage
{
    /// <summary>
    /// Persists replay checkpoints to the local file system.
    /// Checkpoints are JSON files keyed by symbol and validated by configuration hash,
    /// so a checkpoint from a different configuration is never reused.
    /// </summary>
    public class ReplayCheckpointManager
    {
        private readonly string _directory;
        private readonly IDataStore _store;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Creates a new checkpoint manager rooted at the given local directory
        /// </summary>
        public ReplayCheckpointManager(string directory)
            : this(new LocalFileStore(Path.GetDirectoryName(directory) ?? directory), directory)
        {
        }

        /// <summary>
        /// Creates a new checkpoint manager persisting through the given store.
        /// The directory is a store path (relative for cloud stores, absolute for local stores).
        /// </summary>
        public ReplayCheckpointManager(IDataStore store, string directory)
        {
            _store = store;
            _directory = directory;
        }

        /// <summary>
        /// Resolves the checkpoint file path for a symbol
        /// </summary>
        public string CheckpointPath(QuantConnect.Symbol symbol)
        {
            return PathFor(symbol.Value);
        }

        private string PathFor(string symbolValue)
        {
            return Path.Combine(_directory, symbolValue.Replace(":", "_").Replace("/", "_") + ".json");
        }

        /// <summary>
        /// Loads a completed checkpoint for the given job/symbol.
        /// Returns null when absent, incomplete, or when the configuration hash does not match.
        /// </summary>
        public ReplayCheckpoint TryLoadCompleted(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var checkpoint = TryLoad(job, symbol);
            return checkpoint?.Completed == true ? checkpoint : null;
        }

        /// <summary>
        /// Loads the checkpoint for the given job/symbol regardless of completion state.
        /// Returns null when absent or when the configuration hash does not match.
        /// </summary>
        public ReplayCheckpoint TryLoad(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var path = CheckpointPath(symbol);
            if (_store == null || !_store.Exists(path))
            {
                return null;
            }

            ReplayCheckpoint checkpoint;
            try
            {
                using var stream = _store.OpenRead(path);
                using var reader = new StreamReader(stream);
                checkpoint = JsonSerializer.Deserialize<ReplayCheckpoint>(reader.ReadToEnd(), _jsonOptions);
            }
            catch (Exception)
            {
                return null;
            }

            if (checkpoint == null
                || !string.Equals(checkpoint.ConfigurationHash, job.GetConfigurationHash(), StringComparison.Ordinal))
            {
                return null;
            }

            return checkpoint;
        }

        /// <summary>
        /// Saves progress for a symbol (atomic write)
        /// </summary>
        public void Save(ReplayCheckpoint checkpoint)
        {
            if (_store == null)
                return;

            var path = PathFor(checkpoint.Symbol);
            var tempPath = path + ".tmp";
            var json = JsonSerializer.Serialize(checkpoint, _jsonOptions);

            using (var stream = _store.OpenWrite(tempPath))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
            }

            if (_store.Exists(path))
            {
                _store.Delete(path);
            }
            using (var stream = _store.OpenWrite(path))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
            }
        }
    }
}