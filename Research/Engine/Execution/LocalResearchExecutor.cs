using QuantConnect.Research.Engine.LocalData;
using QuantConnect.Research.Engine.Events;
using QuantConnect.Research.Engine.Experiments;
using QuantConnect.Research.Engine.Features;
using QuantConnect.Research.Engine.Jobs;
using QuantConnect.Research.Engine.MarketState;
using QuantConnect.Research.Engine.Observations;
using QuantConnect.Research.Engine.Replay;
using QuantConnect.Research.Engine.Storage;

namespace QuantConnect.Research.Engine.Execution
{
    /// <summary>
    /// Executes research jobs locally.
    /// Deterministic streaming pipeline: events -> replay -> observations -> features -> output.
    /// </summary>
    public class LocalResearchExecutor
    {
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

            try
            {
                if (!job.Validate(out var errors))
                {
                    throw new InvalidOperationException($"Invalid job: {string.Join("; ", errors)}");
                }

                var config = job.CreateReplayConfiguration();
                IExperiment experiment = null;
                if (_experimentFactory != null)
                {
                    experiment = _experimentFactory(job);
                    experiment.Initialize(CreateExperimentContext(job));
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
                            runConfig = new Replay.ReplayConfiguration
                            {
                                StartTime = resumeFrom.AddTicks(1),
                                EndTime = config.EndTime,
                                Symbols = config.Symbols,
                                Venues = config.Venues,
                                EventTypes = config.EventTypes,
                                ObservationInterval = config.ObservationInterval,
                                MaxEvents = config.MaxEvents,
                                EngineVersion = config.EngineVersion,
                                Reorder = config.Reorder,
                                InitialState = restored,
                                InitialEventsProcessed = inProgress.EventsProcessed,
                                InitialNextObservationTime = config.ObservationInterval.HasValue
                                    ? ResumeNextObservationTime(resumeFrom, config.ObservationInterval.Value, config.StartTime)
                                    : null
                            };
                            observationsWritten = inProgress.ObservationsWritten;
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
                        if (job.RawFields != null)
                        {
                            foreach (var rawField in job.RawFields)
                            {
                                row[rawField] = RawFieldValues.For(observation, rawField);
                            }
                        }
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
                        foreach (var resolver in resolvers)
                        {
                            resolver.Complete();
                            unresolved += resolver.UnresolvedCount;
                        }
                        if (unresolved > 0)
                        {
                            Console.WriteLine($"  ...{unresolved} unresolved label(s) for {symbol.Value} (horizon extends beyond data end)");
                        }
                    }
                    else
                    {
                        experiment?.OnOutcome(new OutcomeData
                        {
                            ReferenceTimestamp = replayEngine.LastProcessedTimestamp ?? job.EndTime,
                            Horizon = job.ObservationInterval,
                            OutcomeState = lastState
                        });
                    }

                    var stats = replayEngine.GetStatistics();
                    result.EventsProcessed = stats.EventsProcessed;
                    result.OutputFiles.Add(outputPath);
                    result.SymbolsProcessed++;

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
            return result;
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
        private static int MaxPendingFor(TimeSpan horizon, TimeSpan? observationInterval)
        {
            if (!observationInterval.HasValue || observationInterval.Value <= TimeSpan.Zero) return 0;
            return (int)Math.Ceiling(horizon.Ticks / (double)observationInterval.Value.Ticks) + 1;
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
                Events = replayResult.Events ?? new List<MarketEvent>()
            };
        }

        /// <summary>
        /// Resolves the output root for a job: an explicit job location wins, otherwise the environment root.
        /// Jobs carry only logical content; the physical root is an environment concern.
        /// </summary>
        private string ResolveOutputRoot(ResearchJob job)
        {
            return string.IsNullOrWhiteSpace(job.OutputLocation) ? _environment.OutputRoot : job.OutputLocation;
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
        public DateTime StartTimeUtc { get; set; }
        public DateTime EndTimeUtc { get; set; }
        public TimeSpan Elapsed => EndTimeUtc - StartTimeUtc;

        public override string ToString()
        {
            return Succeeded
                ? $"Job {JobId}: OK, {SymbolsProcessed} symbols (+{SymbolsReused} reused), {EventsProcessed} events, {ObservationsWritten} observations in {Elapsed.TotalSeconds:F1}s"
                : $"Job {JobId}: FAILED - {Error}";
        }
    }
}