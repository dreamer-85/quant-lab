using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;
using QuantConnect.Research.Engine.Validation;

namespace QuantConnect.Research.Engine.Execution
{
    /// <summary>
    /// Executes research jobs locally.
    /// Deterministic streaming pipeline: events -> replay -> observations -> features -> output.
    /// </summary>
    public class LocalResearchExecutor
    {
        /// <summary>
        /// Maximum number of findings echoed to the console. The full set is always written to
        /// validation_report.json, so this only bounds console noise.
        /// </summary>
        private const int ConsoleFindingLimit = 20;

        private readonly IEventDataSource _dataSource;
        private readonly IDataStore _outputStore;
        private readonly Func<ResearchJob, IExperiment> _experimentFactory;
        private readonly ResearchEnvironment _environment;

        /// <summary>
        /// Creates a new LocalResearchExecutor
        /// </summary>
        /// <param name="dataSource">Event source (defaults to local Lean data files)</param>
        /// <param name="outputStore">Output store (defaults to a local file store rooted at the environment output root)</param>
        /// <param name="experimentFactory">Optional factory for experiment instances per job</param>
        /// <param name="environment">Environment roots used when the job does not specify output/checkpoint locations</param>
        public LocalResearchExecutor(IEventDataSource dataSource = null, IDataStore outputStore = null, Func<ResearchJob, IExperiment> experimentFactory = null, ResearchEnvironment environment = null)
        {
            _environment = environment ?? new ResearchEnvironment();
            _dataSource = dataSource ?? new LeanDataEventSource(new LeanDataEventReader(_environment.DataRoot));
            _outputStore = outputStore ?? new LocalFileStore(_environment.OutputRoot);
            _experimentFactory = experimentFactory;
        }

        /// <summary>
        /// Executes the job and returns an execution report
        /// </summary>
        public LocalExecutionResult Execute(ResearchJob job, CancellationToken cancellationToken = default)
        {
            var result = new LocalExecutionResult { JobId = job.JobId, StartTimeUtc = DateTime.UtcNow };

            // Declared out here so the run metadata written after the try/catch can still describe
            // the validation configuration, including for a run that failed before any check ran.
            var validator = new ResearchValidator(ValidationOptions.FromJob(job));

            try
            {
                if (!job.Validate(out var errors))
                {
                    throw new InvalidOperationException($"Invalid job: {string.Join("; ", errors)}");
                }

                validator.Preflight(job);
                if (validator.Enabled)
                {
                    Console.WriteLine($"Validation enabled: {string.Join(", ", validator.EnabledChecks)}");
                }

                // An unresolvable feature or raw field is not a suspicious result, it is a job that
                // cannot run at all: FeatureEngine throws deep inside the per-symbol loop with a bare
                // KeyNotFoundException. Fail here instead, where the suggestion from preflight is
                // available, so the author sees the typo and the nearest real name.
                ThrowIfFeaturesUnresolvable(job);

                // The coverage check can only tell a complete window from a truncated one once it knows
                // both the bounds that were requested and whether the grid was meant to pad up to them.
                validator.SetRequestedWindow(job.StartTime, job.EndTime, job.ObservationInterval, job.FillForward);

                var config = job.CreateReplayConfiguration();
                IExperiment experiment = null;
                if (_experimentFactory != null)
                {
                    experiment = _experimentFactory(job);
                    if (experiment != null)
                    {
                        experiment.Initialize(CreateExperimentContext(job));
                    }
                }

                // Delayed labels: one bounded resolver per horizon, active only when the job declares
                // horizons AND an experiment will consume the resolved outcomes.
                var horizons = experiment != null && job.Horizons is { Count: > 0 }
                    ? job.Horizons.Select(h => HorizonParser.Parse(h)).ToList()
                    : null;

                foreach (var symbol in config.Symbols)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var checkpointManager = CreateCheckpointManager(job);
                    var inProgress = checkpointManager?.TryLoad(job, symbol);
                    var outputPath = BuildOutputPath(job, symbol);
                    if (inProgress?.Completed == true && _outputStore.Exists(outputPath))
                    {
                        result.SymbolsReused++;
                        result.OutputFiles.Add(outputPath);
                        Console.WriteLine($"Reusing completed checkpoint for {symbol.Value}: {inProgress}");
                        continue;
                    }

                    // Resume from an interrupted run: replay the tail segment seeded with the
                    // persisted market state. The persisted state already includes every event up to
                    // and including the checkpoint boundary, so the resumed run starts just after that
                    // boundary (StartTime = boundary + 1 tick) and skips the boundary events entirely;
                    // the first emitted observation is the next grid point. Output is bit-identical to
                    // an uninterrupted run because continuation is boundary-aligned with state carry-forward.
                    var runConfig = config;
                    var observationsWritten = 0L;
                    if (inProgress != null
                        && inProgress.StateJson != null
                        && inProgress.LastObservationTimestamp.HasValue)
                    {
                        var restored = MarketState.MarketStateSerialization.FromJson(inProgress.StateJson);
                        if (restored != null)
                        {
                            var resumeFrom = inProgress.LastObservationTimestamp.Value;
                            observationsWritten = inProgress.ObservationsWritten;
                            runConfig = new Replay.ReplayConfiguration
                            {
                                StartTime = resumeFrom.AddTicks(1),
                                // The tail continues the original grid. Its own StartTime sits one tick
                                // past the last written observation, which would shift every grid point
                                // and stop the tail lining up with the run it continues. An explicit
                                // anchor is the user's stated phase and outranks the start point; without
                                // one, the original StartTime keeps the phase the head ran on.
                                GridAnchor = config.GridAnchor ?? config.StartTime,
                                EndTime = config.EndTime,
                                Symbols = config.Symbols,
                                Venues = config.Venues,
                                EventTypes = config.EventTypes,
                                ObservationInterval = config.ObservationInterval,
                                // Carried explicitly rather than inherited: the tail must pad and cap
                                // exactly as the head did, or the two halves stop being one run.
                                FillForward = config.FillForward,
                                MaxObservations = RemainingBudget(config.MaxObservations, observationsWritten),
                                MaxEvents = config.MaxEvents,
                                EngineVersion = config.EngineVersion,
                                Reorder = config.Reorder,
                                InitialState = restored,
                                InitialEventsProcessed = inProgress.EventsProcessed,
                                InitialNextObservationTime = config.ObservationInterval.HasValue
                                    ? ResumeNextObservationTime(resumeFrom, config.ObservationInterval.Value, config.StartTime)
                                    : null
                            };
                            Console.WriteLine($"Resuming {symbol.Value} from {resumeFrom:O} " +
                                              $"(events={inProgress.EventsProcessed}, obs={inProgress.ObservationsWritten})");
                        }
                    }

                    var eventStream = BuildEventStream(job, symbol, runConfig);
                    var stateReconstructor = MarketStateReconstructorFactory.Create(symbol.SecurityType);
                    var replayEngine = new EventReplayEngine(runConfig, stateReconstructor);
                    var featureEngine = FeatureEngine.FromNames(
                        job.Features,
                        FeatureParameters.ParseJobConfig(job.ExperimentConfig));


                    using var outputStream = _outputStore.OpenWrite(outputPath);
                    using var writer = new ResearchOutputWriter(outputStream, job.OutputFormat);
                    var resolvers = horizons?.Select(h => new DelayedLabelResolver(h, MaxPendingFor(h, config.ObservationInterval))).ToList();
                    MarketState.MarketState lastState = null;

                    foreach (var replayResult in replayEngine.Replay(eventStream))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var observation = ToObservation(replayResult);
                        if (observation == null)
                            continue;

                        lastState = replayResult.State as MarketState.MarketState;

                        var featureResult = featureEngine.Compute(observation);
                        var row = featureResult.ToRow();
                        row["job_id"] = job.JobId;
                        row["symbol"] = symbol.Value;
                        AddTrustColumns(row, observation);

                        // Derived measurements take precedence over raw fields of the same name, matching
                        // what MeasurementCatalog documents. A raw field that collides with a computed
                        // feature is skipped rather than silently overwriting the feature's column; the
                        // preflight check reports the collision so it is never a mystery.
                        if (job.RawFields != null)
                        {
                            foreach (var rawField in job.RawFields)
                            {
                                if (featureResult.Values.ContainsKey(rawField))
                                {
                                    continue;
                                }

                                row[rawField] = RawFieldValues.For(observation, rawField);
                            }
                        }

                        validator.OnObservation(observation, featureResult.Values, NumericColumns(row));
                        writer.WriteRow(row);
                        observationsWritten++;
                        result.ObservationsWritten++;

                        experiment?.OnObservation(observation, featureResult);

                        // Resolve any delayed labels whose horizon has elapsed using this observation
                        // as the realized future price.
                        if (resolvers != null)
                        {
                            foreach (var resolver in resolvers)
                            {
                                foreach (var outcome in resolver.OnObservation(observation))
                                {
                                    experiment.OnOutcome(outcome);
                                }
                            }
                        }

                        // Persist an in-progress checkpoint periodically so an interruption
                        // can be resumed exactly from this boundary.
                        if (checkpointManager != null && observationsWritten % 25000 == 0)
                        {
                            checkpointManager.Save(new ReplayCheckpoint
                            {
                                JobId = job.JobId,
                                Symbol = symbol.Value,
                                ConfigurationHash = job.GetConfigurationHash(),
                                LastObservationTimestamp = replayEngine.LastProcessedTimestamp,
                                ObservationsWritten = observationsWritten,
                                EventsProcessed = replayEngine.EventsProcessed,
                                Completed = false,
                                StateJson = MarketState.MarketStateSerialization.ToJson(lastState)
                            });
                        }
                    }

                    if (resolvers != null)
                    {
                        // Labels whose horizon extends past the end of the stream cannot be resolved.
                        var unresolved = 0;
                        var resolved = 0;
                        foreach (var resolver in resolvers)
                        {
                            resolver.Complete();
                            unresolved += resolver.UnresolvedCount;
                            resolved += resolver.ResolutionCount;
                        }
                        if (unresolved > 0)
                        {
                            Console.WriteLine($"  ...{unresolved} unresolved label(s) for {symbol.Value} (horizon extends beyond data end)");

                            // A high unresolved share means the horizon is longer than the data, or the
                            // observations are further apart than the horizon assumes. Either way the
                            // labels that did resolve are drawn from far fewer periods than expected.
                            var total = unresolved + resolved;
                            if (total > 0 && (double)unresolved / total > UnresolvedLabelRatioThreshold)
                            {
                                validator.Report.Add(new Validation.ValidationFinding
                                {
                                    Check = Validation.ValidationCheckIds.Coverage,
                                    Severity = Validation.ValidationSeverity.Warning,
                                    Symbol = symbol.Value,
                                    Message =
                                        $"{unresolved} of {total} delayed labels ({unresolved * 100d / total:F1}%) never resolved. " +
                                        "A label is resolved by a later observation, so this means the horizon reaches past the " +
                                        "end of the data, or the observation cadence is coarser than the horizon assumes."
                                });
                            }
                        }
                    }
                    else
                    {
                        experiment?.OnOutcome(new OutcomeData
                        {
                            ReferenceTimestamp = replayEngine.LastProcessedTimestamp ?? job.EndTime,
                            Horizon = job.ObservationInterval ?? TimeSpan.Zero,
                            OutcomeState = lastState
                        });
                    }

                    var stats = replayEngine.GetStatistics();
                    result.EventsProcessed = stats.EventsProcessed;
                    result.OutputFiles.Add(outputPath);
                    result.SymbolsProcessed++;

                    validator.ObserveCoverage(
                        replayEngine.FirstObservationTimestamp ?? job.StartTime,
                        replayEngine.LastObservationTimestamp ?? job.StartTime,
                        stats.ObservationsEmitted,
                        stats.LateEvents,
                        stats.Truncated);

                    validator.Complete();
                    validator.Reset();

                    checkpointManager?.Save(new ReplayCheckpoint
                    {
                        JobId = job.JobId,
                        Symbol = symbol.Value,
                        ConfigurationHash = job.GetConfigurationHash(),
                        LastObservationTimestamp = replayEngine.LastProcessedTimestamp,
                        ObservationsWritten = observationsWritten,
                        EventsProcessed = stats.EventsProcessed,
                        Completed = true,
                        CompletedAtUtc = DateTime.UtcNow
                    });
                }

                if (validator.Enabled)
                {
                    result.ValidationFindings = validator.Report.Findings.ToList();
                    PersistValidationReport(job, validator);
                    Console.WriteLine(validator.Report.Summarize());

                    var reportable = validator.Report.Findings
                        .Where(f => f.Severity != ValidationSeverity.Info)
                        .ToList();
                    foreach (var finding in reportable.Take(ConsoleFindingLimit))
                    {
                        Console.WriteLine("  " + finding);
                    }

                    if (reportable.Count > ConsoleFindingLimit)
                    {
                        Console.WriteLine($"  ... and {reportable.Count - ConsoleFindingLimit} more in validation_report.json");
                    }

                    validator.ThrowIfFatal();
                }

                if (experiment != null)
                {
                    var experimentResult = experiment.Finalize();
                    result.ExperimentResult = experimentResult;
                    if (experimentResult?.Rows != null && experimentResult.Rows.Count > 0)
                    {
                        var expPath = BuildExperimentOutputPath(job, experimentResult);
                        PersistExperimentOutput(expPath, experimentResult, job.OutputFormat);
                        result.OutputFiles.Add(expPath);
                    }

                    // Named tables are separate artifacts, not extra columns: a trade log and an
                    // equity curve have different shapes and different consumers, and merging them
                    // would mean null-padding one into the other.
                    foreach (var named in experimentResult?.NamedRows ?? new Dictionary<string, List<Dictionary<string, object>>>())
                    {
                        if (named.Value == null || named.Value.Count == 0)
                        {
                            continue;
                        }

                        var tablePath = BuildExperimentTablePath(job, experimentResult, named.Key);
                        PersistTable(tablePath, named.Value, job.OutputFormat);
                        result.OutputFiles.Add(tablePath);
                    }
                }

                result.Succeeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.Error = "Cancelled";
            }
            catch (Exception ex)
            {
                result.Error = ex.ToString();
            }

            result.EndTimeUtc = DateTime.UtcNow;

            // Written for successful and failed runs alike: the point of the file is to record what
            // the engine actually did, and a failed run is exactly when that matters most.
            PersistRunMetadata(job, result, validator);

            return result;
        }

        /// <summary>
        /// Writes run_metadata.json beside the run outputs. Everything needed to answer "what did
        /// this run actually consume and produce?" without re-reading the job file: the resolved
        /// source configuration, the subscriptions, the payload contract in force, and the counts
        /// the run finished with. The job file alone cannot answer these, because defaults and
        /// environment roots are resolved at run time.
        /// </summary>
        private void PersistRunMetadata(ResearchJob job, LocalExecutionResult result, ResearchValidator validator)
        {
            try
            {
                var directory = Path.Combine(ResolveOutputRoot(job), job.JobId);
                _outputStore.CreateDirectory(directory);
                var path = Path.Combine(directory, "run_metadata.json");

                var source = job.Source;
                var payload = new
                {
                    schemaVersion = 1,
                    jobId = job.JobId,
                    configurationHash = job.GetConfigurationHash(),
                    succeeded = result.Succeeded,
                    error = string.IsNullOrEmpty(result.Error) ? null : result.Error,
                    engineVersion = job.EngineVersion,
                    dataset = job.Dataset,
                    assetClass = job.AssetClass,
                    venue = job.Venue,
                    resolution = job.Resolution.ToString(),
                    symbols = job.Symbols,
                    startTime = job.StartTime.ToString("O"),
                    endTime = job.EndTime.ToString("O"),
                    observationIntervalSeconds = job.ObservationInterval?.TotalSeconds,
                fillForward = job.FillForward,
                maxObservations = job.MaxObservations,
                gridAnchor = job.GridAnchor?.ToString("O"),
                    eventTypes = job.EventTypes.Select(e => e.ToString()).ToList(),
                    features = job.Features,
                    rawFields = job.RawFields,
                    horizons = job.Horizons,
                    experimentName = job.ExperimentName,
                    strategyScript = job.StrategyScript,
                    reorder = job.Reorder.ToString(),
                    maxEvents = job.MaxEvents,
                    enableCheckpointing = job.EnableCheckpointing,
                    dataRoot = _environment.DataRoot,
                    outputRoot = ResolveOutputRoot(job),
                    source = source == null
                        ? null
                        : new
                        {
                            mode = string.IsNullOrWhiteSpace(source.Mode) ? "file" : source.Mode,
                            provider = source.Provider,
                            category = source.Category,
                            orderBookDepth = source.OrderBookDepth,
                            pageSize = source.PageSize,
                            liveDurationSeconds = source.LiveDurationSeconds,
                            restEndpoint = source.RestEndpoint,
                            wsEndpoint = source.WsEndpoint,
                            archiveFilePath = source.ArchiveFilePath
                        },
                    pythonContract = new
                    {
                        historyPeriods = job.ScriptHistoryPeriods,
                        exposeEvents = job.ScriptExposeEvents
                    },
                    validation = new
                    {
                        enabled = validator.Enabled,
                        mode = ValidationOptions.FromJob(job).Mode.ToString(),
                        checks = validator.EnabledChecks,
                        observationsChecked = validator.Report.ObservationsChecked,
                        findingCount = validator.Report.Findings.Count
                    },
                    stats = new
                    {
                        eventsProcessed = result.EventsProcessed,
                        observationsWritten = result.ObservationsWritten,
                        symbolsProcessed = result.SymbolsProcessed,
                        symbolsReused = result.SymbolsReused,
                        outputFiles = result.OutputFiles
                    },
                    experiment = result.ExperimentResult == null
                        ? null
                        : new
                        {
                            name = result.ExperimentResult.ExperimentName,
                            metrics = result.ExperimentResult.Metrics,
                            metadata = result.ExperimentResult.Metadata
                        },
                    timing = new
                    {
                        startedAtUtc = result.StartTimeUtc.ToString("O"),
                        finishedAtUtc = result.EndTimeUtc.ToString("O"),
                        elapsedSeconds = result.Elapsed.TotalSeconds
                    }
                };

                using var stream = _outputStore.OpenWrite(path);
                using var textWriter = new StreamWriter(stream);
                textWriter.Write(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
            catch (Exception ex)
            {
                // Metadata is an audit aid; failing to write it must not fail an otherwise good run.
                Console.WriteLine($"  warning: could not write run_metadata.json: {ex.Message}");
            }
        }

        /// <summary>
        /// Projects the numeric columns of an output row for validation. Identity columns (timestamp,
        /// symbol, job_id) and any non-decimal value are excluded so the whole-run column checks only
        /// see comparable quantities.
        /// </summary>
        private static Dictionary<string, decimal> NumericColumns(Dictionary<string, object> row)
        {
            var numeric = new Dictionary<string, decimal>(row.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in row)
            {
                switch (kvp.Value)
                {
                    case decimal value:
                        numeric[kvp.Key] = value;
                        break;
                    case int intValue:
                        numeric[kvp.Key] = intValue;
                        break;
                    case long longValue:
                        numeric[kvp.Key] = longValue;
                        break;
                    case double doubleValue:
                        numeric[kvp.Key] = (decimal)doubleValue;
                        break;
                }
            }

            return numeric;
        }

        /// <summary>
        /// Creates the experiment context, additionally exposing the job-level horizon list to the
        /// experiment as the "horizons" config key (comma separated).
        /// </summary>
        private static ExperimentContext CreateExperimentContext(ResearchJob job)
        {
            var context = job.CreateExperimentContext();
            if (job.Horizons is { Count: > 0 })
            {
                context.Configuration["horizons"] = string.Join(",", job.Horizons);
            }
            return context;
        }

        /// <summary>
        /// Builds the event stream for a symbol. Uses incremental stream merging when the configuration
        /// requests streaming replay and the source exposes ordered sub-streams; otherwise falls back to
        /// the source's flat stream (sorted by the replay engine).
        /// </summary>
        private IEnumerable<MarketEvent> BuildEventStream(ResearchJob job, QuantConnect.Symbol symbol, Replay.ReplayConfiguration config)
        {
            if (config.Reorder == Replay.ReorderMode.InOrderStreaming && _dataSource is IStreamingEventSource streamingSource)
            {
                return EventStreamMerger.Merge(streamingSource.GetEventStreams(job, symbol));
            }

            return _dataSource.GetEvents(job, symbol);
        }

        /// <summary>
        /// Creates the checkpoint manager for a job, or null when checkpointing is disabled
        /// </summary>
        private ReplayCheckpointManager CreateCheckpointManager(ResearchJob job)
        {
            if (!job.EnableCheckpointing)
            {
                return null;
            }

            var directory = string.IsNullOrEmpty(job.CheckpointDirectory)
                ? Path.Combine(ResolveOutputRoot(job), job.JobId, "checkpoints")
                : job.CheckpointDirectory;
            _outputStore.CreateDirectory(directory);
            return new ReplayCheckpointManager(_outputStore, directory);
        }

        /// <summary>
        /// Computes the next observation-grid point strictly after the resume boundary, aligned to
        /// the original run's grid anchored at the job start time.
        /// </summary>
        private static DateTime ResumeNextObservationTime(DateTime resumeFrom, TimeSpan interval, DateTime gridAnchor)
        {
            var elapsed = resumeFrom - gridAnchor;
            var steps = (long)Math.Floor(elapsed.Ticks / (double)interval.Ticks) + 1;
            return gridAnchor + TimeSpan.FromTicks(steps * interval.Ticks);
        }

        /// <summary>
        /// Hard cap for the delayed-label pending buffer: the number of observations that fit inside
        /// one horizon window (plus one for the triggering observation). A constant independent of the
        /// dataset size. Falls back to 0 (no cap) when there is no observation grid.
        /// </summary>
        /// <summary>
        /// Share of unresolved delayed labels above which the horizon is reported as inconsistent with
        /// the data or the cadence.
        /// </summary>
        private const double UnresolvedLabelRatioThreshold = 0.25d;

        private static int MaxPendingFor(TimeSpan horizon, TimeSpan? observationInterval)        {
            if (!observationInterval.HasValue || observationInterval.Value <= TimeSpan.Zero) return 0;
            return (int)Math.Ceiling(horizon.Ticks / (double)observationInterval.Value.Ticks) + 1;
        }

        /// <summary>
        /// How much of <see cref="Replay.ReplayConfiguration.MaxObservations"/> a resumed tail may
        /// still spend. The cap is a budget for the run, not for a segment of it, so a head that
        /// already emitted its share leaves the tail the remainder and a run that hit the cap emits
        /// nothing further. 0 (no cap) stays 0 rather than becoming a budget of zero.
        /// </summary>
        private static long RemainingBudget(long maxObservations, long alreadyEmitted)
        {
            if (maxObservations <= 0)
            {
                return 0;
            }

            var remaining = maxObservations - alreadyEmitted;
            return remaining > 0 ? remaining : 0;
        }

        /// <summary>
        /// Converts a replay result to an observation
        /// </summary>
        private static Observation ToObservation(ReplayResult replayResult)
        {
            return new Observation
            {
                Timestamp = replayResult.Timestamp,
                State = replayResult.State as MarketState.MarketState,
                Events = replayResult.Events ?? new List<MarketEvent>(),
                Quality = replayResult.Quality,
                LastEventTimestamp = replayResult.LastEventTimestamp
            };
        }

        /// <summary>
        /// Publishes how much of an observation is real, so a row says whether it is a measurement or
        /// padding. Without these columns a filled period is indistinguishable from a live one in the
        /// output, which is how a cadence mismatch stays invisible.
        /// </summary>
        private static void AddTrustColumns(Dictionary<string, object> row, Observation observation)
        {
            row[Validation.ObservationTrustColumns.Quality] = (int)observation.Quality;
            row[Validation.ObservationTrustColumns.DataAgeMs] = observation.DataAge.HasValue
                ? (decimal)observation.DataAge.Value.TotalMilliseconds
                : -1m;
        }

        /// <summary>
        /// Resolves the output root for a job: an explicit job location wins, otherwise the environment root.
        /// Jobs carry only logical content; the physical root is an environment concern.
        /// </summary>
        /// <remarks>
        /// The result is always absolute. Every path this executor builds is handed to the data
        /// store, which resolves a relative path against its own root; a relative root here would
        /// therefore be appended to the root a second time and the outputs would land one level
        /// deeper than the manifest that reports them, with nothing failing.
        /// </remarks>
        private string ResolveOutputRoot(ResearchJob job)
        {
            var root = string.IsNullOrWhiteSpace(job.OutputLocation) ? _environment.OutputRoot : job.OutputLocation;
            return string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);
        }

        /// <summary>
        /// Builds the output file path for a symbol
        /// </summary>
        private string BuildOutputPath(ResearchJob job, QuantConnect.Symbol symbol)
        {
            var safeSymbol = symbol.Value.Replace(":", "_").Replace("/", "_");
            var extension = job.OutputFormat.ToLowerInvariant() switch
            {
                "csv" => "csv",
                "json" => "json",
                _ => "parquet"
            };

            var directory = Path.Combine(ResolveOutputRoot(job), job.JobId, safeSymbol);
            _outputStore.CreateDirectory(directory);
            return Path.Combine(directory, $"{job.Dataset}.{extension}");
        }

        /// <summary>
        /// Builds the output path for experiment result rows
        /// </summary>
        private string BuildExperimentOutputPath(ResearchJob job, ExperimentResult experimentResult)
        {
            var directory = Path.Combine(ResolveOutputRoot(job), job.JobId, "experiment");
            _outputStore.CreateDirectory(directory);
            var extension = job.OutputFormat.ToLowerInvariant() switch
            {
                "csv" => "csv",
                "json" => "json",
                _ => "parquet"
            };
            return Path.Combine(directory, $"{experimentResult.ExperimentName}.{extension}");
        }

        /// <summary>
        /// Path for one named result table, e.g. experiment/position_trades.csv
        /// </summary>
        private string BuildExperimentTablePath(ResearchJob job, ExperimentResult experimentResult, string table)
        {
            var directory = Path.Combine(ResolveOutputRoot(job), job.JobId, "experiment");
            _outputStore.CreateDirectory(directory);
            var extension = job.OutputFormat.ToLowerInvariant() switch
            {
                "csv" => "csv",
                "json" => "json",
                _ => "parquet"
            };

            var safeTable = SanitizeFileName(table);
            return Path.Combine(directory, $"{experimentResult.ExperimentName}_{safeTable}.{extension}");
        }

        /// <summary>
        /// Reduces a table name to something safe to use as a file name, so an experiment cannot
        /// write outside the output directory by naming a table "../escape".
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "table";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) || c == '.' || c == '/' || c == '\\'
                ? '_'
                : c).ToArray()).Trim('_');

            return string.IsNullOrEmpty(cleaned) ? "table" : cleaned;
        }

        /// <summary>
        /// Rejects a job that names a feature or raw field which does not exist. This is a
        /// configuration mistake rather than a suspicious result, so it aborts regardless of
        /// validation mode: the run could not produce meaningful output either way, and failing here
        /// surfaces the "did you mean" guidance instead of a bare KeyNotFoundException from deep
        /// inside the per-symbol replay loop.
        /// </summary>
        private static void ThrowIfFeaturesUnresolvable(ResearchJob job)
        {
            var problems = JobConfigurationCheck.UnresolvableNames(job);
            if (problems.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Job cannot run: {string.Join("; ", problems)}");
        }

        /// <summary>
        /// Writes the validation report next to the run outputs so findings survive the console.
        /// Written even when nothing was found, so a consumer can tell "validated clean" apart from
        /// "validation did not run".
        /// </summary>
        private void PersistValidationReport(ResearchJob job, ResearchValidator validator)
        {
            var directory = Path.Combine(ResolveOutputRoot(job), job.JobId);
            _outputStore.CreateDirectory(directory);
            var path = Path.Combine(directory, "validation_report.json");

            var payload = new
            {
                jobId = job.JobId,
                configurationHash = job.GetConfigurationHash(),
                mode = ValidationOptions.FromJob(job).Mode.ToString(),
                checks = validator.EnabledChecks,
                observationsChecked = validator.Report.ObservationsChecked,
                raisedCounts = validator.Report.RaisedCounts,
                findings = validator.Report.Findings.Select(f => new
                {
                    f.Check,
                    severity = f.Severity.ToString(),
                    f.Message,
                    f.Symbol,
                    f.Occurrences,
                    firstTimestamp = f.FirstTimestamp?.ToString("O"),
                    lastTimestamp = f.LastTimestamp?.ToString("O")
                })
            };

            using var stream = _outputStore.OpenWrite(path);
            using var textWriter = new StreamWriter(stream);
            textWriter.Write(System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }

        /// <summary>
        /// Persists experiment result rows and metrics through the output store
        /// </summary>
        private void PersistExperimentOutput(string path, ExperimentResult experimentResult, string format)
        {
            using var stream = _outputStore.OpenWrite(path);
            using var writer = new ResearchOutputWriter(stream, format);
            foreach (var row in experimentResult.Rows)
            {
                writer.WriteRow(row);
            }

            if (experimentResult.Metrics.Count > 0)
            {
                var metricsPath = Path.ChangeExtension(path, null) + "_metrics.json";
                using var metricsStream = _outputStore.OpenWrite(metricsPath);
                using var textWriter = new StreamWriter(metricsStream);
                textWriter.Write(System.Text.Json.JsonSerializer.Serialize(experimentResult.Metrics));
            }
        }

        /// <summary>
        /// Persists a single named result table. No metrics sidecar, since metrics belong to the
        /// experiment as a whole rather than to each of its tables.
        /// </summary>
        private void PersistTable(string path, List<Dictionary<string, object>> rows, string format)
        {
            using var stream = _outputStore.OpenWrite(path);
            using var writer = new ResearchOutputWriter(stream, format);
            foreach (var row in rows)
            {
                writer.WriteRow(row);
            }
        }
    }

    /// <summary>
    /// Result of a local research execution
    /// </summary>
    public class LocalExecutionResult
    {
        public string JobId { get; set; }
        public bool Succeeded { get; set; }
        public string Error { get; set; } = string.Empty;
        public int SymbolsProcessed { get; set; }
        public int SymbolsReused { get; set; }
        public long EventsProcessed { get; set; }
        public long ObservationsWritten { get; set; }
        public List<string> OutputFiles { get; set; } = new();
        public ExperimentResult ExperimentResult { get; set; }

        /// <summary>
        /// Validation findings raised for this run, empty when validation is off.
        /// </summary>
        public List<ValidationFinding> ValidationFindings { get; set; } = new();
        public DateTime StartTimeUtc { get; set; }
        public DateTime EndTimeUtc { get; set; }
        public TimeSpan Elapsed => EndTimeUtc - StartTimeUtc;

        public override string ToString()
        {
            if (!Succeeded)
            {
                return $"Job {JobId}: FAILED - {Error}";
            }

            var validation = ValidationFindings.Count == 0
                ? string.Empty
                : $", {ValidationFindings.Count} validation finding(s)";
            return $"Job {JobId}: OK, {SymbolsProcessed} symbols (+{SymbolsReused} reused), " +
                   $"{EventsProcessed} events, {ObservationsWritten} observations{validation} in {Elapsed.TotalSeconds:F1}s";
        }
    }
}