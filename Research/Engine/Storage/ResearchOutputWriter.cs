using System.Globalization;
using System.Text;

namespace QuantConnect.Research.Engine.Storage
{
    /// <summary>
    /// Writes tabular research output to disk.
    /// Supports CSV, JSON, and Parquet formats.
    /// Writes incrementally with bounded memory.
    /// </summary>
    public class ResearchOutputWriter : IDisposable
    {
        private readonly string _format;
        private readonly List<string> _columns;
        private StreamWriter _writer;
        private ParquetStreamWriter _parquetWriter;
        private readonly List<Dictionary<string, object>> _jsonBuffer;
        private readonly List<Dictionary<string, object>> _parquetBuffer;
        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private const int JsonBufferSize = 10000;
        private const int ParquetBufferSize = 10000;
        private bool _isHeaderWritten;
        private readonly object _lock = new();

        /// <summary>
        /// Number of rows written
        /// </summary>
        public long RowCount { get; private set; }

        /// <summary>
        /// Number of columns
        /// </summary>
        public int ColumnCount => _columns.Count;

        public IEnumerable<string> Columns => _columns;

        /// <summary>
        /// Creates a new ResearchOutputWriter over a local file path
        /// </summary>
        /// <param name="path">Output file path</param>
        /// <param name="format">Output format: "csv", "json", or "parquet"</param>
        public ResearchOutputWriter(string path, string format = "csv")
            : this(OpenFile(path, format), format, ownsStream: true)
        {
        }

        /// <summary>
        /// Creates a new ResearchOutputWriter over an existing stream.
        /// The stream is caller-owned (e.g. supplied by <see cref="IDataStore.OpenWrite"/>);
        /// this writer never disposes it.
        /// </summary>
        /// <param name="stream">Target stream</param>
        /// <param name="format">Output format: "csv", "json", or "parquet"</param>
        public ResearchOutputWriter(Stream stream, string format = "csv")
            : this(stream, format, ownsStream: false)
        {
        }

        private ResearchOutputWriter(Stream stream, string format, bool ownsStream)
        {
            _format = format.ToLowerInvariant();
            _columns = new List<string>();
            _jsonBuffer = new List<Dictionary<string, object>>();
            _parquetBuffer = new List<Dictionary<string, object>>();
            _stream = stream;
            _ownsStream = ownsStream;
            RowCount = 0;

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (_format is not "csv" and not "json" and not "parquet")
                throw new ArgumentException($"Unsupported format: {format}. Use 'csv', 'json', or 'parquet'.");

            if (_format == "parquet")
            {
                _parquetWriter = new ParquetStreamWriter(stream, leaveOpen: !_ownsStream);
            }
            else
            {
                _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: !_ownsStream);
            }
        }

        private static FileStream OpenFile(string path, string format)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Output path cannot be empty", nameof(path));

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        /// <summary>
        /// Writes a row of data
        /// </summary>
        public void WriteRow(string[] columnNames, object[] values)
        {
            if (columnNames == null || values == null)
                throw new ArgumentNullException();

            if (columnNames.Length != values.Length)
                throw new ArgumentException("Column names and values must have the same length");

            switch (_format)
            {
                case "csv":
                    WriteCsvRow(columnNames, values);
                    break;
                case "json":
                    WriteJsonRow(columnNames, values);
                    break;
                case "parquet":
                    WriteParquetRow(columnNames, values);
                    break;
            }
        }

        /// <summary>
        /// Writes a row of data from a dictionary
        /// </summary>
        public void WriteRow(Dictionary<string, object> row)
        {
            var sortedKeys = row.Keys.OrderBy(k => k).ToList();
            WriteRow(sortedKeys.ToArray(), sortedKeys.Select(k => row[k]).ToArray());
        }

        /// <summary>
        /// Finalizes the output and disposes resources
        /// </summary>
        public void Finalize()
        {
            lock (_lock)
            {
                if (_format == "json" && _jsonBuffer.Count > 0)
                {
                    FlushJsonBuffer();
                }

                if (_format == "parquet")
                {
                    FlushParquetBuffer();
                    _parquetWriter?.CloseAsync().GetAwaiter().GetResult();
                    _parquetWriter = null;
                }
                else if (_writer != null)
                {
                    _writer.Flush();
                }
            }
        }

        /// <summary>
        /// Disposes resources
        /// </summary>
        public void Dispose()
        {
            Finalize();
            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }
        }

        /// <summary>
        /// Writes a CSV row
        /// </summary>
        private void WriteCsvRow(string[] columnNames, object[] values)
        {
            lock (_lock)
            {
                if (!_isHeaderWritten)
                {
                    foreach (var column in columnNames)
                    {
                        if (!_columns.Contains(column))
                        {
                            _columns.Add(column);
                        }
                    }

                    _writer?.WriteLine(string.Join(",", _columns.Select(EscapeCsv)));
                    _isHeaderWritten = true;
                }

                var line = string.Join(",", _columns.Select(col =>
                {
                    var index = Array.IndexOf(columnNames, col);
                    return index < 0 ? string.Empty : EscapeCsv(FormatValue(values[index]));
                }));

                _writer?.WriteLine(line);
                RowCount++;
            }
        }

        /// <summary>
        /// Writes a JSON row
        /// </summary>
        private void WriteJsonRow(string[] columnNames, object[] values)
        {
            lock (_lock)
            {
                // Track columns (append-only)
                foreach (var column in columnNames)
                {
                    if (!_columns.Contains(column))
                    {
                        _columns.Add(column);
                    }
                }

                var dict = new Dictionary<string, object>();
                for (int i = 0; i < columnNames.Length; i++)
                {
                    dict[columnNames[i]] = values[i];
                }

                _jsonBuffer.Add(dict);

                if (_jsonBuffer.Count >= JsonBufferSize)
                {
                    FlushJsonBuffer();
                }

                RowCount++;
            }
        }

        /// <summary>
        /// Writes a Parquet row (buffered and flushed in batches)
        /// </summary>
        private void WriteParquetRow(string[] columnNames, object[] values)
        {
            lock (_lock)
            {
                var dict = new Dictionary<string, object>();
                for (int i = 0; i < columnNames.Length; i++)
                {
                    dict[columnNames[i]] = values[i];
                    if (!_columns.Contains(columnNames[i]))
                    {
                        _columns.Add(columnNames[i]);
                    }
                }
                _parquetBuffer.Add(dict);

                if (_parquetBuffer.Count >= ParquetBufferSize)
                {
                    FlushParquetBuffer();
                }

                RowCount++;
            }
        }

        /// <summary>
        /// Flushes the Parquet buffer to disk
        /// </summary>
        private void FlushParquetBuffer()
        {
            if (_parquetBuffer.Count == 0) return;

            var rows = _parquetBuffer.ToArray();
            _parquetWriter?.WriteRowsAsync(rows).GetAwaiter().GetResult();
            _parquetBuffer.Clear();
        }

        /// <summary>
        /// Flushes the JSON buffer to disk
        /// </summary>
        private void FlushJsonBuffer()
        {
            if (_jsonBuffer.Count == 0) return;

            var jsonText = System.Text.Json.JsonSerializer.Serialize(_jsonBuffer);
            _writer?.WriteLine(jsonText);
            _jsonBuffer.Clear();
        }

        /// <summary>
        /// Escapes a value for CSV
        /// </summary>
        private static string EscapeCsv(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>
        /// Formats a value for output
        /// </summary>
        private static string FormatValue(object value)
        {
            if (value == null) return string.Empty;
            if (value is DateTime dt) return dt.ToString("O", CultureInfo.InvariantCulture);
            if (value is decimal d) return d.ToString(CultureInfo.InvariantCulture);
            if (value is double db) return db.ToString("R", CultureInfo.InvariantCulture);
            if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            return value.ToString() ?? string.Empty;
        }
    }
}