using System.Text.Json;
using System.Text.Json.Serialization;
using QuantConnect.Research.Engine;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Execution;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Ingest;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.LocalData;

namespace QuantConnect.Research.Runner
{
    /// <summary>
    /// Command-line runner for research jobs.
    /// Executes a job from JSON and writes a manifest describing the outcome.
    /// Usage: QuantConnect.Research.Runner --job-file <path.json> [--data-dir <path>] [--output-dir <path>]
    /// Absent data/output roots are resolved from QUANTLAB_DATA_ROOT/QUANTLAB_OUTPUT_ROOT or defaults.
    /// </summary>
    public static class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(), new TimeSpanJsonConverter() }
        };

        public static int Main(string[] args)
        {
            // Mode resolution. The same image serves two Cloud Run shapes:
            //   - service: ENTRYPOINT = [..., "--web"] and no job args  -> web (HTTP) mode
            //   - job:     ENTRYPOINT = [..., "--web"] plus --job-file/--data-dir/...
            //              args appended by the job spec               -> CLI (one-shot) mode
            var hasWebFlag = args.Any(a => a == "--web" || a == "--web-mode");
            var hasJobArgs = args.Any(a => a.StartsWith("--job-file")
                || a == "--job-file"
                || a.StartsWith("--synthetic-benchmark"));
            if (hasWebFlag && !hasJobArgs)
            {
                return WebServer.Run(args.Where(a => a != "--web" && a != "--web-mode").ToArray()).GetAwaiter().GetResult();
            }

            string jobFile = null;
            string dataDir = null;
            string outputDir = null;
            long syntheticBenchmark = 0;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--job-file":
                        jobFile = args[++i];
                        break;
                    case "--data-dir":
                        dataDir = args[++i];
                        break;
                    case "--output-dir":
                        outputDir = args[++i];
                        break;
                    case "--synthetic-benchmark":
                        syntheticBenchmark = long.Parse(args[++i]);
                        break;
                    default:
                        // The image ENTRYPOINT always passes --web; the CLI path must
                        // tolerate it (mode is decided above by presence of job args).
                        if (args[i] == "--web" || args[i] == "--web-mode")
                        {
                            break;
                        }
                        // Accept the --flag=value form used by Cloud Run job specs.
                        if (TryParseEquals(args[i], "--job-file=", ref jobFile)
                            || TryParseEquals(args[i], "--data-dir=", ref dataDir)
                            || TryParseEquals(args[i], "--output-dir=", ref outputDir)
                            || TryParseEquals(args[i], "--synthetic-benchmark=", ref syntheticBenchmark))
                        {
                            break;
                        }
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return 2;
                }
            }

            // Benchmark mode: run the streaming pipeline over a deterministic synthetic source.
            if (syntheticBenchmark > 0)
            {
                return RunSyntheticBenchmark(syntheticBenchmark, dataDir, outputDir);
            }

            if (string.IsNullOrEmpty(jobFile))
            {
                Console.Error.WriteLine("Missing --job-file <path.json>");
                return 2;
            }
            if (!File.Exists(jobFile))
            {
                Console.Error.WriteLine($"Invalid --job-file: file not found: {jobFile}");
                return 2;
            }

            try
            {
                var (result, manifestPath) = ExecuteJobFile(jobFile, dataDir, outputDir);

                Console.WriteLine(result.ToString());
                foreach (var file in result.OutputFiles)
                {
                    Console.WriteLine($"Output: {file}");
                }
                Console.WriteLine($"Manifest: {manifestPath}");

                return result.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// Parses a `--flag=value` argument for a CLI switch. Returns true when the
        /// argument matches the prefix and reports the extracted value.
        /// </summary>
        private static bool TryParseEquals(string arg, string prefix, ref string value)
        {
            if (!arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            value = arg.Substring(prefix.Length);
            return true;
        }

        private static bool TryParseEquals(string arg, string prefix, ref long value)
        {
            if (!arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            value = long.Parse(arg.Substring(prefix.Length));
            return true;
        }

        /// <summary>
        /// Runs a job from a job JSON file and returns the execution result plus the manifest path.
        /// Shared by the CLI and the web server so both modes produce identical outcomes.
        /// </summary>
        public static (LocalExecutionResult Result, string ManifestPath) ExecuteJobFile(
            string jobFile,
            string dataDir = null,
            string outputDir = null)
        {
            var job = JsonSerializer.Deserialize<ResearchJob>(File.ReadAllText(jobFile), JsonOptions);
            if (job == null)
            {
                throw new InvalidOperationException("Failed to deserialize job");
            }

            var environment = new ResearchEnvironment(dataRoot: dataDir, outputRoot: outputDir);
            LeanBootstrap.EnsureDataFolder(environment.DataRoot);

            // The job carries only logical content; resolve physical roots here.
            if (string.IsNullOrWhiteSpace(job.OutputLocation))
            {
                job.OutputLocation = environment.OutputRoot;
            }

            var executor = new LocalResearchExecutor(
                DataSourceFactory.Create(job, environment),
                environment: environment,
                experimentFactory: ExperimentFactory.Create);
            var result = executor.Execute(job);

            var manifestPath = WriteManifest(job, result);
            return (result, manifestPath);
        }

        /// <summary>
        /// Runs a job from an in-memory job document and returns the execution result plus the
        /// manifest path. Errors are reported via the result (job failure) or thrown (bad input)
        /// so the web server can map them onto HTTP status codes.
        /// </summary>
        public static (LocalExecutionResult Result, string ManifestPath) ExecuteJobDocument(
            string jobJson,
            string dataDir = null,
            string outputDir = null)
        {
            var job = JsonSerializer.Deserialize<ResearchJob>(jobJson, JsonOptions);
            if (job == null)
            {
                throw new InvalidOperationException("Failed to deserialize job");
            }

            var environment = new ResearchEnvironment(dataRoot: dataDir, outputRoot: outputDir);
            LeanBootstrap.EnsureDataFolder(environment.DataRoot);

            if (string.IsNullOrWhiteSpace(job.OutputLocation))
            {
                job.OutputLocation = environment.OutputRoot;
            }

            var executor = new LocalResearchExecutor(
                DataSourceFactory.Create(job, environment),
                environment: environment,
                experimentFactory: ExperimentFactory.Create);
            var result = executor.Execute(job);

            var manifestPath = WriteManifest(job, result);
            return (result, manifestPath);
        }

        /// <summary>
        /// Runs the streaming pipeline end-to-end over a deterministic synthetic source
        /// at the given event scale and reports throughput, peak working set, and row counts.
        /// </summary>
        private static int RunSyntheticBenchmark(long eventCount, string dataDir, string outputDir)
        {
            try
            {
                var environment = new ResearchEnvironment(dataRoot: dataDir, outputRoot: outputDir);
                var symbol = QuantConnect.Symbol.Create("SYNTH", SecurityType.Crypto, Market.Bybit);
                var start = new DateTime(2022, 12, 13, 0, 0, 0, DateTimeKind.Utc);
                var end = start.AddSeconds(eventCount); // one second of dense data per event ~ bounded end

                var source = new SyntheticStreamingEventSource(eventCount, start, symbol);
                var executor = new LocalResearchExecutor(source, environment: environment);
                var job = new ResearchJob
                {
                    JobId = $"bench-{eventCount}",
                    Dataset = "synthetic",
                    Symbols = new List<string> { symbol.Value },
                    AssetClass = "crypto",
                    Venue = "bybit",
                    StartTime = start,
                    EndTime = end,
                    Resolution = Resolution.Second,
                    EventTypes = new List<MarketEventType> { MarketEventType.Trade, MarketEventType.Quote },
                    ObservationInterval = TimeSpan.FromSeconds(5),
                    Features = new List<string> { "mid_price", "spread", "trade_volume" },
                    ExperimentName = "dry-run",
                    OutputLocation = outputDir ?? Path.Combine(Path.GetTempPath(), "quantlab"),
                    OutputFormat = "csv",
                    EnableCheckpointing = false,
                    Reorder = QuantConnect.Research.Engine.Replay.ReorderMode.InOrderStreaming
                };

                GC.Collect();
                GC.WaitForPendingFinalizers();
                var baseline = GC.GetTotalMemory(true);
                var maxWorkingSet = 0L;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var beforeWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
                var result = executor.Execute(job);
                sw.Stop();
                maxWorkingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;

                GC.Collect();
                GC.WaitForPendingFinalizers();
                var liveManaged = GC.GetTotalMemory(true);

                var peakDeltaMb = (maxWorkingSet - beforeWorkingSet) / 1024.0 / 1024.0;
                var managedMb = liveManaged / 1024.0 / 1024.0;
                var ratePerSec = eventCount / sw.Elapsed.TotalSeconds;

                Console.WriteLine(
                    $"BENCH events={eventCount} elapsed={sw.Elapsed.TotalSeconds:F2}s " +
                    $"rate={ratePerSec:F0}/s observations={result.ObservationsWritten} " +
                    $"workingSetDelta={peakDeltaMb:F1}MB liveManaged={managedMb:F1}MB " +
                    $"(peak {maxWorkingSet / 1024.0 / 1024.0:F1}MB)");
                Console.WriteLine(result.Succeeded ? "BENCH OK" : $"BENCH FAILED - {result.Error}");
                return result.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// Writes a manifest describing the execution outcome next to the job outputs
        /// </summary>
        private static string WriteManifest(ResearchJob job, LocalExecutionResult result)
        {
            var manifest = new
            {
                jobId = job.JobId,
                succeeded = result.Succeeded,
                error = result.Error,
                symbolsProcessed = result.SymbolsProcessed,
                symbolsReused = result.SymbolsReused,
                eventsProcessed = result.EventsProcessed,
                observationsWritten = result.ObservationsWritten,
                outputFiles = result.OutputFiles,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                experimentName = result.ExperimentResult?.ExperimentName,
                metrics = result.ExperimentResult?.Metrics ?? new Dictionary<string, object>(),
            };

            var manifestDir = string.IsNullOrEmpty(job.OutputLocation)
                ? Path.GetTempPath()
                : Path.Combine(job.OutputLocation, job.JobId);
            Directory.CreateDirectory(manifestDir);
            var manifestPath = Path.Combine(manifestDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            return manifestPath;
        }
    }

    /// <summary>
    /// Serializes TimeSpan as "c" (e.g. "00:05:00")
    /// </summary>
    public class TimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return TimeSpan.Parse(reader.GetString());
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("c"));
        }
    }
}