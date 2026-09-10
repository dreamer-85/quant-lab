using System.Globalization;
using System.Threading;
using Parquet;
using Parquet.Data;

namespace QuantConnect.Research.Engine.Storage
{
    /// <summary>
    /// Streaming-memory parquet writer that writes row groups incrementally.
    /// Supports memory-bounded writes for large datasets using Parquet.Net 4.x async API.
    /// </summary>
    public class ParquetStreamWriter : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private ParquetWriter _writer;
        private ParquetRowGroupWriter _rowGroupWriter;
        private Dictionary<string, DataField> _columnSchemas = new();
        private Dictionary<string, Type> _clrTypes = new();
        private DataField[] _schemaOrder;
        private bool _finalized;

        /// <summary>
        /// Row group size (rows per group)
        /// </summary>
        public int RowGroupSize { get; set; } = 10000;

        /// <summary>
        /// Rows buffered in the current row group
        /// </summary>
        public int RowCount { get; private set; }

        /// <summary>
        /// Total rows written
        /// </summary>
        public long TotalRowCount { get; private set; }

        /// <summary>
        /// Creates a new ParquetStreamWriter that owns a local file at the given path
        /// </summary>
        public ParquetStreamWriter(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Output path cannot be empty", nameof(path));

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _leaveOpen = false;
        }

        /// <summary>
        /// Creates a new ParquetStreamWriter writing to the given stream.
        /// When <paramref name="leaveOpen"/> is true the stream is not disposed on close
        /// (e.g. streams supplied by an <see cref="IDataStore.OpenWrite"/>).
        /// </summary>
        public ParquetStreamWriter(Stream stream, bool leaveOpen = false)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            _stream = stream;
            _leaveOpen = leaveOpen;
        }

        /// <summary>
        /// Writes a batch of rows.
        /// First call establishes the schema; subsequent calls must match column set.
        /// </summary>
        public async Task WriteRowsAsync(Dictionary<string, object>[] rows, string[] columnOrder = null)
        {
            if (rows == null || rows.Length == 0)
                return;

            var columns = columnOrder ?? rows[0].Keys.OrderBy(k => k).ToArray();

            if (_writer == null)
            {
                InitializeSchema(columns, rows);
                var schema = new Schema(_schemaOrder);
                _writer = await ParquetWriter.CreateAsync(schema, _stream).ConfigureAwait(false);
            }

            foreach (var col in columns)
            {
                if (!_columnSchemas.ContainsKey(col))
                {
                    throw new InvalidOperationException($"Schema mismatch: unexpected column '{col}' at row {TotalRowCount}");
                }
            }

            var remaining = rows.ToList();
            while (remaining.Count > 0)
            {
                var take = Math.Min(RowGroupSize - RowCount, remaining.Count);
                var chunk = remaining.Take(take).ToArray();
                remaining.RemoveRange(0, take);

                EnsureRowGroup();
                await WriteChunkAsync(columns, chunk).ConfigureAwait(false);

                RowCount += chunk.Length;
                TotalRowCount += chunk.Length;

                if (RowCount >= RowGroupSize)
                {
                    CloseRowGroup();
                }
            }
        }

        /// <summary>
        /// Finalizes the file
        /// </summary>
        public async Task CloseAsync()
        {
            if (_finalized)
                return;
            _finalized = true;

            CloseRowGroup();

            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }

            if (_stream != null && !_leaveOpen)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Disposes resources asynchronously
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the schema from value types
        /// </summary>
        private void InitializeSchema(string[] columns, Dictionary<string, object>[] sampleRows)
        {
            var fields = new List<DataField>();

            foreach (var column in columns)
            {
                object sampleValue = null;
                foreach (var row in sampleRows)
                {
                    if (row.TryGetValue(column, out var val) && val != null)
                    {
                        sampleValue = val;
                        break;
                    }
                }

                var canonicalType = NormalizeType(sampleValue?.GetType() ?? typeof(string));
                var field = CreateField(column, canonicalType);
                fields.Add(field);
                _columnSchemas[column] = field;
                _clrTypes[column] = canonicalType;
            }

            _schemaOrder = fields.ToArray();
        }

        /// <summary>
        /// Normalizes a CLR type to a canonical parquet-compatible type
        /// </summary>
        private static Type NormalizeType(Type type)
        {
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                type == typeof(ushort) || type == typeof(int) || type == typeof(uint))
            {
                return typeof(int);
            }

            if (type == typeof(long) || type == typeof(ulong))
            {
                return typeof(long);
            }

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                return typeof(double);
            }

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
                type == typeof(string) || type == typeof(char) || type == typeof(bool))
            {
                return type;
            }

            return typeof(string);
        }

        /// <summary>
        /// Creates a DataField based on canonical CLR type
        /// </summary>
        private static DataField CreateField(string name, Type canonicalType)
        {
            if (canonicalType == typeof(DateTime)) return new DataField<DateTime>(name);
            if (canonicalType == typeof(DateTimeOffset)) return new DataField<DateTimeOffset>(name);
            if (canonicalType == typeof(int)) return new DataField<int>(name);
            if (canonicalType == typeof(long)) return new DataField<long>(name);
            if (canonicalType == typeof(double)) return new DataField<double>(name);
            if (canonicalType == typeof(bool)) return new DataField<bool>(name);
            return new DataField<string>(name);
        }

        /// <summary>
        /// Ensures a row group is open
        /// </summary>
        private void EnsureRowGroup()
        {
            if (_rowGroupWriter == null)
            {
                _rowGroupWriter = _writer.CreateRowGroup();
            }
        }

        /// <summary>
        /// Closes the current row group
        /// </summary>
        private void CloseRowGroup()
        {
            if (_rowGroupWriter != null)
            {
                _rowGroupWriter.Dispose();
                _rowGroupWriter = null;
                RowCount = 0;
            }
        }

        /// <summary>
        /// Writes a chunk of rows to the current row group
        /// </summary>
        private async Task WriteChunkAsync(string[] columns, Dictionary<string, object>[] chunk)
        {
            var count = chunk.Length;

            foreach (var column in columns)
            {
                var field = _columnSchemas[column];
                var clrType = _clrTypes[column];

                if (clrType == typeof(string))
                {
                    var values = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = chunk[i].TryGetValue(column, out var v) && v != null
                            ? Convert.ToString(v, CultureInfo.InvariantCulture)
                            : null;
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
                else if (clrType == typeof(DateTime))
                {
                    var values = new DateTimeOffset[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = new DateTimeOffset(ConvertToDateTime(chunk[i], column));
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
                else if (clrType == typeof(DateTimeOffset))
                {
                    var values = new DateTimeOffset[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = ConvertToDateTimeOffset(chunk[i], column);
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
                else if (clrType == typeof(double))
                {
                    var values = new double[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = ConvertToDouble(chunk[i], column);
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
                else if (clrType == typeof(long))
                {
                    var values = new long[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = ConvertToLong(chunk[i], column);
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
                else if (clrType == typeof(int))
                {
                    var values = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = ConvertToInt(chunk[i], column);
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
                else if (clrType == typeof(bool))
                {
                    var values = new bool[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = chunk[i].TryGetValue(column, out var v) && v is bool b && b;
                    }
                    await _rowGroupWriter.WriteColumnAsync(new DataColumn(field, values), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        private static DateTime ConvertToDateTime(Dictionary<string, object> row, string column)
        {
            if (row.TryGetValue(column, out var v) && v != null)
            {
                if (v is DateTime dt) return dt;
                if (v is DateTimeOffset dto) return dto.DateTime;
                if (DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    return parsed;
            }
            return DateTime.MinValue;
        }

        private static DateTimeOffset ConvertToDateTimeOffset(Dictionary<string, object> row, string column)
        {
            if (row.TryGetValue(column, out var v) && v != null)
            {
                if (v is DateTimeOffset dto) return dto;
                if (v is DateTime dt) return new DateTimeOffset(dt);
            }
            return DateTimeOffset.MinValue;
        }

        private static double ConvertToDouble(Dictionary<string, object> row, string column)
        {
            if (row.TryGetValue(column, out var v) && v != null)
            {
                if (v is decimal dec) return (double)dec;
                if (v is float f) return f;
                if (v is double d) return d;
                if (v is int i) return i;
                if (v is long l) return l;
                if (v is byte b) return b;
                return double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0d;
            }
            return 0d;
        }

        private static long ConvertToLong(Dictionary<string, object> row, string column)
        {
            if (row.TryGetValue(column, out var v) && v != null)
            {
                if (v is int i) return i;
                if (v is long l) return l;
                if (v is byte b) return b;
                if (v is short s) return s;
                return long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0L;
            }
            return 0L;
        }

        private static int ConvertToInt(Dictionary<string, object> row, string column)
        {
            if (row.TryGetValue(column, out var v) && v != null)
            {
                if (v is int i) return i;
                if (v is short s) return s;
                if (v is byte b) return b;
                if (v is long l) return (int)l;
                if (int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return 0;
        }
    }
}