using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantConnect.Research.Runner
{
    /// <summary>
    /// One live research session: capture market data while it arrives, run the engine over
    /// everything captured so far, and publish what changed to anyone watching.
    ///
    /// The engine is a batch engine: it reads staged files and writes results when it finishes.
    /// Rather than teach it to be incremental, this re-runs the whole thing on the accumulated
    /// window every tick and publishes only what is new. That is the honest trade for a research
    /// tool: a few thousand events replay in well under a second, and because the account is
    /// deterministic given its input, the numbers a watcher sees at tick N are exactly the numbers
    /// a single run over the same data would produce. Nothing is approximated to make it stream.
    ///
    /// So a live session is a sequence of complete runs, and the curve on screen is the tail of
    /// each one. The alternative, resuming from a checkpoint, would carry replay state forward and
    /// make the running account depend on how the session happened to be chunked.
    /// </summary>
    public sealed class LiveRunManager
    {
        private readonly ConcurrentDictionary<string, LiveRun> _runs = new();
        private readonly string _datafeeds;
        private readonly string _workRoot;

        public LiveRunManager(string datafeeds, string workRoot)
        {
            _datafeeds = datafeeds;
            _workRoot = workRoot;
            Directory.CreateDirectory(_workRoot);
        }

        /// <summary>
        /// Start a live session and return its id
        /// </summary>
        public LiveRun Start(LiveRunRequest request)
        {
            var id = $"{request.Symbol.ToLowerInvariant()}-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
            var run = new LiveRun(id, request, _workRoot);
            _runs[id] = run;

            // Started, not awaited: the HTTP request returns immediately and the browser follows
            // the run over SSE.
            run.Task = Task.Run(() => ExecuteAsync(run));
            return run;
        }

        public LiveRun Get(string id) => _runs.TryGetValue(id, out var run) ? run : null;

        public IReadOnlyCollection<LiveRun> Active =>
            _runs.Values.Where(r => r.Status is "starting" or "running").ToList();

        private async Task ExecuteAsync(LiveRun run)
        {
            var request = run.Request;
            var feedDir = Path.Combine(run.WorkDir, "feeds");
            var outDir = Path.Combine(run.WorkDir, "out");
            Directory.CreateDirectory(feedDir);
            Directory.CreateDirectory(outDir);

            run.Status = "running";
            run.Publish(new { type = "status", status = "running", message = $"capturing {request.Provider} {request.Symbol} for up to {request.DurationSeconds}s" });

            var jobId = $"live-{run.Id}";
            var capture = StartCapture(request, feedDir, run);
            var sentEquity = 0;
            var sentTrades = 0;
            var sentObservations = 0;
            var deadline = DateTime.UtcNow.AddSeconds(request.DurationSeconds + 30);

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(request.TickSeconds), run.Cancellation.Token);

                    if (capture.HasExited && sentEquity > 0)
                    {
                        run.Status = "completed";
                        run.Publish(new
                        {
                            type = "complete",
                            status = "completed",
                            message = $"session finished: {sentEquity} equity points, {sentTrades} trades",
                            equity = run.LastMetrics.TryGetValue("ending_equity", out var e) ? e : null
                        });
                        return;
                    }

                    var window = ReadWindow(feedDir, request);
                    if (window == null)
                    {
                        run.Publish(new { type = "log", message = "waiting for the first market event..." });
                        continue;
                    }

                    if (window.Value.End <= run.LastWindowEnd && sentEquity > 0)
                    {
                        // No new data since the last tick. Re-running would republish the same
                        // account, so say nothing rather than making the log look busier than it is.
                        continue;
                    }

                    run.LastWindowEnd = window.Value.End;
                    var jobPath = Path.Combine(run.WorkDir, "job.json");
                    File.WriteAllText(jobPath, BuildJob(request, window.Value, run));

                    var engine = StartEngine(jobPath, feedDir, outDir, run, out var engineOutput);
                    await engine.WaitForExitAsync(run.Cancellation.Token);
                    var output = await ReadAsync(engineOutput, run.Cancellation.Token);

                    var runStats = ReadRunStats(outDir, jobId);
                    if (engine.ExitCode != 0)
                    {
                        run.Publish(new
                        {
                            type = "error",
                            message = "engine run failed",
                            detail = output.Length > 1200 ? output[^1200..] : output
                        });
                        continue;
                    }

                    PublishNew(run, outDir, request, ref sentEquity, ref sentTrades, ref sentObservations, output,
                        runStats.Events);
                }

                run.Status = capture.HasExited ? "completed" : "stopped";
                run.Publish(new { type = "complete", status = run.Status, message = "session ended" });
            }
            catch (OperationCanceledException)
            {
                run.Status = "stopped";
                run.Publish(new { type = "complete", status = "stopped", message = "stopped by request" });
            }
            catch (Exception ex)
            {
                run.Status = "failed";
                run.Publish(new { type = "error", message = ex.Message, detail = ex.ToString() });
            }
            finally
            {
                TryKill(capture);
                run.Completion.TrySetResult(true);
            }
        }

        /// <summary>
        /// Publish only what changed since the previous tick. The whole account is recomputed each
        /// time, so a delta is enough to keep the browser in step with a full run.
        /// </summary>
        private static void PublishNew(
            LiveRun run,
            string outDir,
            LiveRunRequest request,
            ref int sentEquity,
            ref int sentTrades,
            ref int sentObservations,
            string engineOutput,
            long events)
        {
            var experimentDir = Path.Combine(outDir, $"live-{run.Id}", "experiment");

            var metrics = ReadMetrics(experimentDir);
            if (metrics.Count == 0)
            {
                run.Publish(new { type = "log", message = "engine produced observations but no portfolio yet" });
                return;
            }

            run.LastMetrics = metrics;

            var equity = ReadCsv(Path.Combine(experimentDir, $"{request.ExperimentName}_portfolio.csv"));
            foreach (var row in equity.Skip(sentEquity))
            {
                run.Publish(new
                {
                    type = "equity",
                    time = row.GetValueOrDefault("timestamp"),
                    equity = row.GetValueOrDefault("equity"),
                    cash = row.GetValueOrDefault("cash"),
                    positionValue = row.GetValueOrDefault("position_value"),
                    realized = row.GetValueOrDefault("realized_pnl"),
                    unrealized = row.GetValueOrDefault("unrealized_pnl"),
                    drawdown = row.GetValueOrDefault("drawdown"),
                    openPositions = row.GetValueOrDefault("open_positions")
                });
            }

            if (equity.Count > sentEquity)
            {
                run.Publish(new
                {
                    type = "summary",
                    equity = metrics.GetValueOrDefault("ending_equity"),
                    totalReturn = metrics.GetValueOrDefault("total_return"),
                    maxDrawdown = metrics.GetValueOrDefault("max_drawdown"),
                    realized = metrics.GetValueOrDefault("realized_pnl"),
                    unrealized = metrics.GetValueOrDefault("unrealized_pnl"),
                    fees = metrics.GetValueOrDefault("total_fees"),
                    trades = metrics.GetValueOrDefault("position_count"),
                    fills = metrics.GetValueOrDefault("fill_count"),
                    winRate = metrics.GetValueOrDefault("win_rate"),
                    peakEquity = metrics.GetValueOrDefault("peak_equity")
                });
            }
            sentEquity = equity.Count;

            var trades = ReadCsv(Path.Combine(experimentDir, $"{request.ExperimentName}_trades.csv"));
            foreach (var row in trades.Skip(sentTrades))
            {
                run.Publish(new
                {
                    type = "trade",
                    symbol = row.GetValueOrDefault("symbol"),
                    side = row.GetValueOrDefault("side"),
                    entryTime = row.GetValueOrDefault("entry_time"),
                    exitTime = row.GetValueOrDefault("exit_time"),
                    entryPrice = row.GetValueOrDefault("entry_price"),
                    exitPrice = row.GetValueOrDefault("exit_price"),
                    quantity = row.GetValueOrDefault("quantity"),
                    pnl = row.GetValueOrDefault("pnl"),
                    fees = row.GetValueOrDefault("fees"),
                    isWin = row.GetValueOrDefault("is_win"),
                    equityAfter = row.GetValueOrDefault("equity_after")
                });
            }
            sentTrades = trades.Count;

            var observations = ReadCsv(Path.Combine(outDir, $"live-{run.Id}", request.Symbol, "crypto.csv"));
            if (observations.Count > sentObservations)
            {
                run.Publish(new
                {
                    type = "progress",
                    observations = observations.Count,
                    newObservations = observations.Count - sentObservations,
                    events = events
                });
            }
            sentObservations = observations.Count;
        }

        /// <summary>
        /// Reads the event count and validation findings for a tick out of the manifest the
        /// engine writes beside its outputs.
        ///
        /// The manifest is the source of truth. Scraping the console summary is fragile — the
        /// engine prints "3,395 events" with a thousands separator and the label after the
        /// number, which is exactly the kind of format that silently reads as zero — and a
        /// progress bar stuck on "0 events" is worse than no counter at all.
        /// </summary>
        private static (long Events, int ValidationFindings) ReadRunStats(string outDir, string jobId)
        {
            var manifestPath = Path.Combine(outDir, jobId, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return (0, 0);
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = document.RootElement;
                var events = root.TryGetProperty("eventsProcessed", out var e) && e.TryGetInt64(out var n) ? n : 0;
                return (events, 0);
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }

        private Process StartCapture(LiveRunRequest request, string feedDir, LiveRun run)
        {
            var info = new ProcessStartInfo(_datafeeds)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("pull");
            info.ArgumentList.Add("--market");
            info.ArgumentList.Add("crypto");
            info.ArgumentList.Add("--provider");
            info.ArgumentList.Add(request.Provider);
            info.ArgumentList.Add("--mode");
            info.ArgumentList.Add("live");
            info.ArgumentList.Add("--symbol");
            info.ArgumentList.Add(request.Symbol);
            info.ArgumentList.Add("--duration");
            info.ArgumentList.Add(request.DurationSeconds.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add("--out");
            info.ArgumentList.Add(feedDir);
            info.ArgumentList.Add("--trades");
            info.ArgumentList.Add("--quotes");
            info.ArgumentList.Add("--no-bars");
            // Without this the capture buffers the whole session in memory and only writes
            // the CSVs when the websocket closes, so a live reader never sees a row.
            info.ArgumentList.Add("--stream-to-disk");

            return StartAndPump(info, line => run.Publish(new { type = "log", message = line }), run);
        }

        private static Process StartEngine(
            string jobPath, string feedDir, string outDir, LiveRun run, out string outputPath)
        {
            var engine = Environment.ProcessPath ?? "QuantConnect.Research.Runner";
            if (engine.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                // Started as `dotnet <dll>`; the child needs the same entry assembly.
                var assembly = AppContext.BaseDirectory
                    .TrimEnd(Path.DirectorySeparatorChar)
                    .Split(Path.DirectorySeparatorChar)
                    .Last();
                engine = "dotnet";
                var info = new ProcessStartInfo(engine)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                info.ArgumentList.Add(assembly);
                info.ArgumentList.Add("--job-file");
                info.ArgumentList.Add(jobPath);
                info.ArgumentList.Add("--data-dir");
                info.ArgumentList.Add(feedDir);
                info.ArgumentList.Add("--output-dir");
                info.ArgumentList.Add(outDir);
                var proc = new Process { StartInfo = info, EnableRaisingEvents = true };
                proc.Start();
                outputPath = Path.Combine(Path.GetTempPath(), $"quantlab-engine-{Guid.NewGuid():N}.log");
                Pump(proc, outputPath, run);
                return proc;
            }

            var startInfo = new ProcessStartInfo(engine)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--job-file");
            startInfo.ArgumentList.Add(jobPath);
            startInfo.ArgumentList.Add("--data-dir");
            startInfo.ArgumentList.Add(feedDir);
            startInfo.ArgumentList.Add("--output-dir");
            startInfo.ArgumentList.Add(outDir);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();

            // Drain both pipes into a temp file. Leaving them unread would eventually fill the
            // buffer and deadlock a child that writes more than the pipe holds.
            outputPath = Path.Combine(Path.GetTempPath(), $"quantlab-engine-{Guid.NewGuid():N}.log");
            Pump(process, outputPath, run);
            return process;
        }

        private static Process StartAndPump(ProcessStartInfo info, Action<string> onLine, LiveRun run)
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data.Trim()); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data.Trim()); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }

        private static void Pump(Process process, string path, LiveRun run)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data != null) System.IO.File.AppendAllText(path, e.Data + "\n"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) System.IO.File.AppendAllText(path, e.Data + "\n"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.Exited += (_, _) =>
            {
                try
                {
                    if (!System.IO.File.Exists(path))
                    {
                        System.IO.File.WriteAllText(path, "");
                    }
                }
                catch (Exception ex)
                {
                    run.Publish(new { type = "log", message = $"could not read engine output: {ex.Message}" });
                }
            };
        }

        private static async Task<string> ReadAsync(string path, CancellationToken token)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (System.IO.File.Exists(path))
                {
                    return await System.IO.File.ReadAllTextAsync(path, token);
                }

                await Task.Delay(50, token);
            }

            return "";
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone, or never started. Nothing to clean up either way.
            }
        }

        /// <summary>
        /// The window actually present in the staged files. A live session grows as data arrives, so
        /// the job has to be re-scoped every tick rather than pinned to a window that would be
        /// mostly empty for the first minute of a session.
        /// </summary>
        private static (DateTime Start, DateTime End)? ReadWindow(string feedDir, LiveRunRequest request)
        {
            var dir = Path.Combine(feedDir, "crypto", request.Provider, request.Symbol);
            if (!Directory.Exists(dir))
            {
                return null;
            }

            long? first = null;
            long? last = null;
            foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
            {
                foreach (var line in File.ReadLines(file).Skip(1))
                {
                    var comma = line.IndexOf(',');
                    if (comma <= 0)
                    {
                        continue;
                    }

                    if (!long.TryParse(line.AsSpan(0, comma), out var ms))
                    {
                        continue;
                    }

                    if (first == null || ms < first) first = ms;
                    if (last == null || ms > last) last = ms;
                }
            }

            if (first == null || last == null)
            {
                return null;
            }

            return (
                DateTimeOffset.FromUnixTimeMilliseconds(first.Value).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(last.Value).UtcDateTime.AddSeconds(1));
        }

        private static string BuildJob(LiveRunRequest request, (DateTime Start, DateTime End) window, LiveRun run)
        {
            var job = new JsonObject
            {
                ["jobId"] = $"live-{run.Id}",
                ["dataset"] = "crypto",
                ["assetClass"] = "crypto",
                ["venue"] = request.Provider,
                ["symbols"] = new JsonArray(request.Symbol),
                ["resolution"] = "Minute",
                ["eventTypes"] = new JsonArray("Quote", "Trade"),
                ["observationInterval"] = TimeSpan.FromSeconds(request.ObservationIntervalSeconds).ToString(),
                ["startTime"] = window.Start.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["endTime"] = window.End.ToString("yyyy-MM-ddTHH:mm:ss"),
                ["features"] = new JsonArray(JsonValue.Create(request.Feature)),
                ["rawFields"] = new JsonArray(JsonValue.Create("bid_price"), JsonValue.Create("ask_price"), JsonValue.Create("last_price")),
                ["experimentName"] = request.ExperimentName,
                ["outputFormat"] = "csv",
                ["reorder"] = "FullSort",
                ["enableCheckpointing"] = false,
                ["source"] = new JsonObject
                {
                    ["mode"] = "feed",
                    ["provider"] = request.Provider
                },
                ["experimentConfig"] = new JsonObject
                {
                    ["entry_condition"] = request.EntryCondition,
                    ["exit_condition"] = request.ExitCondition,
                    ["holding_observations"] = request.HoldingObservations.ToString(CultureInfo.InvariantCulture),
                    ["direction"] = request.Direction,
                    ["position_fraction"] = request.PositionFraction.ToString(CultureInfo.InvariantCulture),
                    ["starting_cash"] = "100000",
                    ["fee_bps"] = request.FeeBps.ToString(CultureInfo.InvariantCulture)
                }
            };

            return job.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private static Dictionary<string, object> ReadMetrics(string experimentDir)
        {
            var path = Path.Combine(experimentDir, $"{Path.GetFileName(experimentDir)}_metrics.json");
            var candidates = Directory.Exists(experimentDir)
                ? Directory.GetFiles(experimentDir, "*_metrics.json")
                : Array.Empty<string>();
            if (candidates.Length == 0)
            {
                return new Dictionary<string, object>();
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(candidates[0]))
                       ?? new Dictionary<string, object>();
            }
            catch (Exception)
            {
                // A partially written metrics file is not worth failing a session over; the next
                // tick writes the complete one.
                return new Dictionary<string, object>();
            }
        }

        private static List<Dictionary<string, object>> ReadCsv(string path)
        {
            var rows = new List<Dictionary<string, object>>();
            if (!System.IO.File.Exists(path))
            {
                return rows;
            }

            try
            {
                foreach (var line in System.IO.File.ReadLines(path).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var cells = line.Split(',');
                    var header = HeaderFor(path);
                    var row = new Dictionary<string, object>();
                    for (var i = 0; i < header.Length && i < cells.Length; i++)
                    {
                        row[header[i]] = Coerce(cells[i]);
                    }

                    rows.Add(row);
                }
            }
            catch (IOException)
            {
                return rows;
            }

            return rows;
        }

        private static readonly Dictionary<string, string[]> Headers = new(StringComparer.OrdinalIgnoreCase);

        private static string[] HeaderFor(string path)
        {
            if (Headers.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var header = System.IO.File.ReadLines(path).FirstOrDefault()?.Split(',') ?? Array.Empty<string>();
            Headers[path] = header;
            return header;
        }

        /// <summary>
        /// Keeps numbers numeric on the wire so the browser can plot them without reparsing strings.
        /// </summary>
        private static object Coerce(string cell)
        {
            if (cell.Length == 0)
            {
                return null;
            }

            if (cell.Equals("True", StringComparison.OrdinalIgnoreCase)) return true;
            if (cell.Equals("False", StringComparison.OrdinalIgnoreCase)) return false;

            if (long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return l;
            }

            if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return d;
            }

            return cell;
        }
    }

    /// <summary>
    /// A live session's settings, as the UI sends them
    /// </summary>
    public sealed class LiveRunRequest
    {
        public string Provider { get; set; } = "bybit";
        public string Symbol { get; set; } = "BTCUSDT";
        public int DurationSeconds { get; set; } = 180;
        public int TickSeconds { get; set; } = 5;
        public int ObservationIntervalSeconds { get; set; } = 2;
        public int HoldingObservations { get; set; } = 10;
        public string EntryCondition { get; set; } = "trade_flow < 0";
        public string ExitCondition { get; set; } = "trade_flow >= 0";
        public string Direction { get; set; } = "long";
        public string PositionFraction { get; set; } = "0.5";
        public string FeeBps { get; set; } = "5";
        public string Feature { get; set; } = "trade_flow";
        public string ExperimentName { get; set; } = "position";
    }

    /// <summary>
    /// A live session and the fan-out to whoever is watching it
    /// </summary>
    public sealed class LiveRun
    {
        private readonly ConcurrentQueue<string> _buffer = new();
        private readonly List<Channel> _subscribers = new();
        private readonly object _gate = new();

        public LiveRun(string id, LiveRunRequest request, string workRoot)
        {
            Id = id;
            Request = request;
            WorkDir = Path.Combine(workRoot, id);
            Directory.CreateDirectory(WorkDir);
        }

        public string Id { get; }
        public LiveRunRequest Request { get; }
        public string WorkDir { get; }
        public string Status { get; set; } = "starting";
        public DateTime LastWindowEnd { get; set; } = DateTime.MinValue;
        public Dictionary<string, object> LastMetrics { get; set; } = new();
        public TaskCompletionSource<bool> Completion { get; } = new();
        public Task Task { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();

        public void Stop() => Cancellation.Cancel();

        public void Publish(object payload)
        {
            var line = JsonSerializer.Serialize(payload);
            lock (_gate)
            {
                _subscribers.RemoveAll(c => c.Completed);
                if (_subscribers.Count == 0)
                {
                    // Nobody is watching. A small replay buffer lets a browser that connects a
                    // moment late still see the curve so far instead of an empty chart.
                    _buffer.Enqueue(line);
                    while (_buffer.Count > 2000)
                    {
                        _buffer.TryDequeue(out _);
                    }

                    return;
                }

                foreach (var channel in _subscribers)
                {
                    channel.Send(line);
                }
            }
        }

        public IReadOnlyList<string> DrainBuffer()
        {
            lock (_gate)
            {
                var replay = new List<string>();
                while (_buffer.TryDequeue(out var line))
                {
                    replay.Add(line);
                }

                return replay;
            }
        }

        public Channel Subscribe()
        {
            var channel = new Channel();
            lock (_gate)
            {
                _subscribers.Add(channel);
            }

            return channel;
        }

        public void Unsubscribe(Channel channel)
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Complete();
        }

        /// <summary>
        /// One browser connection's outbound queue
        /// </summary>
        public sealed class Channel
        {
            private readonly System.Threading.Channels.Channel<string> _queue =
                System.Threading.Channels.Channel.CreateUnbounded<string>();

            public bool Completed { get; private set; }

            public void Send(string line) => _queue.Writer.TryWrite(line);

            public void Complete()
            {
                Completed = true;
                _queue.Writer.TryComplete();
            }

            public System.Threading.Channels.ChannelReader<string> Reader => _queue.Reader;
        }
    }
}
