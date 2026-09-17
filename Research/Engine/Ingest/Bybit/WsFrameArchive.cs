using System.Text.Json;

namespace QuantConnect.Research.Engine.Ingest.Bybit
{
    /// <summary>
    /// Records the raw WebSocket frames received by a live capture along with their reception
    /// timestamps so the capture can be replayed byte-for-byte later.
    /// </summary>
    public interface IWsFrameArchive : IDisposable
    {
        /// <summary>
        /// Records one received frame and the wall-clock time it arrived.
        /// </summary>
        void Record(string frameJson, DateTime arrivedUtc);

        /// <summary>
        /// Finalizes buffered data. Safe to call more than once.
        /// </summary>
        void Complete();
    }

    /// <summary>
    /// Append-only archive of raw WebSocket frames in JSON-lines format. Each line is a JSON object:
    /// <c>{"a": arrival-unix-ms, "f": "<raw frame json>"}</c>. Written incrementally with bounded memory.
    /// </summary>
    public sealed class WsFrameArchiveWriter : IWsFrameArchive
    {
        private readonly object _lock = new();
        private readonly StreamWriter _writer;
        private long _frames;
        private bool _completed;

        /// <summary>
        /// Number of frames recorded so far
        /// </summary>
        public long Frames => _frames;

        /// <summary>
        /// Creates an archive writer over a file path. The parent directory is created if missing.
        /// </summary>
        public WsFrameArchiveWriter(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Archive path cannot be empty", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read));
        }

        public void Record(string frameJson, DateTime arrivedUtc)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                var receivedMs = new DateTimeOffset(DateTime.SpecifyKind(arrivedUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                var line = JsonSerializer.Serialize(new { a = receivedMs, f = frameJson });
                _writer.WriteLine(line);
                _frames++;
            }
        }

        public void Complete()
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }
                _completed = true;
                _writer.Flush();
            }
        }

        public void Dispose()
        {
            Complete();
            _writer.Dispose();
        }
    }
}