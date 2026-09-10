namespace QuantConnect.Research.Engine.Storage
{
    /// <summary>
    /// Interface for data storage abstraction.
    /// The engine should not care whether data is on local disk or cloud storage.
    /// </summary>
    public interface IDataStore
    {
        /// <summary>
        /// Stores name of this store
        /// </summary>
        string StoreName { get; }

        /// <summary>
        /// Opens a stream for reading
        /// </summary>
        Stream OpenRead(string path);

        /// <summary>
        /// Opens a stream for writing
        /// </summary>
        Stream OpenWrite(string path);

        /// <summary>
        /// Checks if a path exists
        /// </summary>
        bool Exists(string path);

        /// <summary>
        /// Deletes a path (file or directory)
        /// </summary>
        void Delete(string path);

        /// <summary>
        /// Lists entries in a directory
        /// </summary>
        IEnumerable<string> List(string directoryPath);

        /// <summary>
        /// Creates a directory
        /// </summary>
        void CreateDirectory(string path);

        /// <summary>
        /// Gets the total size of a path (for directories, recursive)
        /// </summary>
        long GetSize(string path);
    }
}