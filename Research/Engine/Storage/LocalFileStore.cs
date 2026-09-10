namespace QuantConnect.Research.Engine.Storage
{
    /// <summary>
    /// Local filesystem implementation of IDataStore.
    /// Stores data in a configurable root directory.
    /// </summary>
    public class LocalFileStore : IDataStore
    {
        private readonly string _rootDirectory;

        /// <summary>
        /// Store name
        /// </summary>
        public string StoreName => "local";

        /// <summary>
        /// Root directory for all paths
        /// </summary>
        public string RootDirectory => _rootDirectory;

        /// <summary>
        /// Creates a new LocalFileStore
        /// </summary>
        /// <param name="rootDirectory">Root directory for data. Defaults to "data" under working directory.</param>
        public LocalFileStore(string rootDirectory = null)
        {
            _rootDirectory = rootDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

            if (!Directory.Exists(_rootDirectory))
            {
                Directory.CreateDirectory(_rootDirectory);
            }
        }

        /// <summary>
        /// Opens a stream for reading
        /// </summary>
        public Stream OpenRead(string path)
        {
            var fullPath = GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"File not found: {path}", fullPath);

            return File.OpenRead(fullPath);
        }

        /// <summary>
        /// Opens a stream for writing
        /// </summary>
        public Stream OpenWrite(string path)
        {
            var fullPath = GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return File.Create(fullPath);
        }

        /// <summary>
        /// Checks if a path exists
        /// </summary>
        public bool Exists(string path)
        {
            return File.Exists(GetFullPath(path)) || Directory.Exists(GetFullPath(path));
        }

        /// <summary>
        /// Deletes a path (file or directory)
        /// </summary>
        public void Delete(string path)
        {
            var fullPath = GetFullPath(path);

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }

        /// <summary>
        /// Lists entries in a directory
        /// </summary>
        public IEnumerable<string> List(string directoryPath = "")
        {
            var fullPath = GetFullPath(directoryPath);
            if (!Directory.Exists(fullPath))
                yield break;

            foreach (var entry in Directory.GetFileSystemEntries(fullPath))
            {
                yield return Path.GetRelativePath(_rootDirectory, entry);
            }
        }

        /// <summary>
        /// Creates a directory
        /// </summary>
        public void CreateDirectory(string path)
        {
            Directory.CreateDirectory(GetFullPath(path));
        }

        /// <summary>
        /// Gets the total size of a path (for directories, recursive)
        /// </summary>
        public long GetSize(string path)
        {
            var fullPath = GetFullPath(path);

            if (File.Exists(fullPath))
            {
                return new FileInfo(fullPath).Length;
            }

            if (Directory.Exists(fullPath))
            {
                return Directory
                    .EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
            }

            return 0;
        }

        /// <summary>
        /// Resolves a relative path against the root directory
        /// </summary>
        protected string GetFullPath(string path)
        {
            if (Path.IsPathRooted(path))
            {
                // Absolute paths are used as-is
                return path;
            }

            return Path.Combine(_rootDirectory, path);
        }
    }
}