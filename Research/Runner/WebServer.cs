using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace QuantConnect.Research.Runner
{
    /// <summary>
    /// Web front-end over the research engine.
    ///
    /// Two jobs are served here. <c>/run</c> and <c>/run-job-file</c> execute a job and return its
    /// result, unchanged, for callers that want a completed answer. The live surface does the
    /// opposite: it starts a session, then streams the account as it is rebuilt from data still
    /// arriving, over Server-Sent Events. SSE rather than WebSockets because the traffic is one-way
    /// and a plain <c>EventSource</c> needs no client library, which matters when the page has to
    /// work by just being opened.
    ///
    /// Endpoints:
    ///   GET  /healthz                 -> 200 OK (probe target)
    ///   GET  /                        -> the live UI
    ///   POST /run                     -> execute a job from a JSON body
    ///   POST /run-job-file            -> execute a job from a path in the body
    ///   GET  /api/live                 -> list active sessions
    ///   POST /api/live                 -> start a session, returns { runId }
    ///   GET  /api/live/{id}            -> session state and latest metrics
    ///   POST /api/live/{id}/stop       -> stop a session
    ///   GET  /api/live/{id}/stream     -> SSE: status, equity, trade, summary, log, complete
    ///   GET  /api/live/{id}/trades     -> the full trade log as JSON
    ///   GET  /api/live/{id}/portfolio  -> the full equity curve as JSON
    /// </summary>
    public static class WebServer
    {
        private static LiveRunManager _live;

        public static async Task<int> Run(string[] args)
        {
            string dataDir = null;
            string outputDir = null;
            string portOverride = null;
            string workRoot = null;
            string datafeeds = null;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--data-dir":
                        dataDir = args[++i];
                        break;
                    case "--output-dir":
                        outputDir = args[++i];
                        break;
                    case "--port":
                        portOverride = args[++i];
                        break;
                    case "--work-root":
                        workRoot = args[++i];
                        break;
                    case "--datafeeds":
                        datafeeds = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return 2;
                }
            }

            var port = portOverride ?? Environment.GetEnvironmentVariable("PORT") ?? "8080";
            var resolvedDatafeeds = datafeeds ?? "datafeeds";
            var resolvedWork = workRoot ?? Path.Combine(Path.GetTempPath(), "quantlab-live");
            _live = new LiveRunManager(resolvedDatafeeds, resolvedWork);

            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            var app = builder.Build();

            app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

            app.MapGet("/", () => Results.Content(Ui.Html, "text/html; charset=utf-8"));

            app.MapGet("/api/live", () => Results.Json(new
            {
                active = _live.Active.Select(r => new
                {
                    r.Id,
                    r.Status,
                    r.Request.Symbol,
                    r.Request.Provider,
                    r.Request.DurationSeconds,
                    workDir = r.WorkDir
                })
            }));

            app.MapPost("/api/live", async (HttpContext ctx) =>
            {
                try
                {
                    LiveRunRequest request;
                    using var reader = new StreamReader(ctx.Request.Body);
                    var body = await reader.ReadToEndAsync(ctx.RequestAborted);
                    request = string.IsNullOrWhiteSpace(body)
                        ? new LiveRunRequest()
                        : JsonSerializer.Deserialize<LiveRunRequest>(body,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                          ?? new LiveRunRequest();

                    if (request is { DurationSeconds: <= 0 or > 3600, TickSeconds: <= 0 })
                    {
                        return Results.BadRequest(new
                        {
                            error = "duration must be 1..3600 seconds and tick at least 1 second"
                        });
                    }

                    var run = _live.Start(request);
                    return Results.Json(new { runId = run.Id, status = run.Status, workDir = run.WorkDir });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 400);
                }
            });

            app.MapGet("/api/live/{id}", (string id) =>
            {
                var run = _live.Get(id);
                return run == null
                    ? Results.NotFound(new { error = $"no session {id}" })
                    : Results.Json(new
                    {
                        run.Id,
                        run.Status,
                        run.Request,
                        metrics = run.LastMetrics,
                        workDir = run.WorkDir
                    });
            });

            app.MapPost("/api/live/{id}/stop", (string id) =>
            {
                var run = _live.Get(id);
                if (run == null)
                {
                    return Results.NotFound(new { error = $"no session {id}" });
                }

                run.Stop();
                return Results.Json(new { runId = id, status = "stopping" });
            });

            app.MapGet("/api/live/{id}/trades", (string id) =>
            {
                var run = _live.Get(id);
                if (run == null)
                {
                    return Results.NotFound(new { error = $"no session {id}" });
                }

                return Results.Json(ReadTable(run, "trades"));
            });

            app.MapGet("/api/live/{id}/portfolio", (string id) =>
            {
                var run = _live.Get(id);
                if (run == null)
                {
                    return Results.NotFound(new { error = $"no session {id}" });
                }

                return Results.Json(ReadTable(run, "portfolio"));
            });

            // Server-Sent Events. Each message is one JSON object, which the page dispatches on
            // its "type". The replay buffer means a client connecting mid-session still receives
            // the curve so far instead of an empty chart.
            app.MapGet("/api/live/{id}/stream", async (HttpContext ctx, string id) =>
            {
                var run = _live.Get(id);
                if (run == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = $"no session {id}" }));
                    return;
                }

                ctx.Response.Headers["Content-Type"] = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["Connection"] = "keep-alive";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                var channel = run.Subscribe();
                try
                {
                    foreach (var line in run.DrainBuffer())
                    {
                        await ctx.Response.WriteAsync($"data: {line}\n\n", ctx.RequestAborted);
                    }

                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                    await foreach (var line in channel.Reader.ReadAllAsync(ctx.RequestAborted))
                    {
                        await ctx.Response.WriteAsync($"data: {line}\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The browser navigated away or closed the tab. Normal.
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"stream error: {ex.Message}");
                }
                finally
                {
                    run.Unsubscribe(channel);
                }
            });

            app.MapPost("/run", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var jobJson = await reader.ReadToEndAsync(ctx.RequestAborted);
                if (string.IsNullOrWhiteSpace(jobJson))
                {
                    return Results.BadRequest(new { error = "empty request body" });
                }

                try
                {
                    var (result, manifestPath) = Program.ExecuteJobDocument(
                        jobJson,
                        dataDir: dataDir ?? Environment.GetEnvironmentVariable("QUANTLAB_DATA_ROOT"),
                        outputDir: outputDir ?? Environment.GetEnvironmentVariable("QUANTLAB_OUTPUT_ROOT"));
                    return Results.Json(Summarize(result, manifestPath));
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = ex.ToString() }, statusCode: 500);
                }
            });

            app.MapPost("/run-job-file", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                string body;
                try
                {
                    body = await reader.ReadToEndAsync(ctx.RequestAborted);
                    var req = JsonSerializer.Deserialize<JsonElement>(body);
                    var jobFile = req.TryGetProperty("jobFile", out var jf) ? jf.GetString() : null;
                    if (string.IsNullOrWhiteSpace(jobFile) || !File.Exists(jobFile))
                    {
                        return Results.BadRequest(new { error = $"job file not found: {jobFile}" });
                    }

                    var reqDataDir = req.TryGetProperty("dataDir", out var dd) ? dd.GetString() : null;
                    var reqOutputDir = req.TryGetProperty("outputDir", out var od) ? od.GetString() : null;
                    var (result, manifestPath) = Program.ExecuteJobFile(
                        jobFile,
                        dataDir: reqDataDir ?? dataDir ?? Environment.GetEnvironmentVariable("QUANTLAB_DATA_ROOT"),
                        outputDir: reqOutputDir ?? outputDir ?? Environment.GetEnvironmentVariable("QUANTLAB_OUTPUT_ROOT"));
                    return Results.Json(Summarize(result, manifestPath));
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = ex.ToString() }, statusCode: 500);
                }
            });

            app.Urls.Add($"http://0.0.0.0:{port}");
            Console.WriteLine($"quantlab web server listening on http://0.0.0.0:{port}");
            Console.WriteLine($"  datafeeds : {resolvedDatafeeds}");
            Console.WriteLine($"  work root : {resolvedWork}");
            await app.RunAsync();
            return 0;
        }

        private static object Summarize(object result, string manifestPath)
        {
            var type = result.GetType();
            object Get(string name) => type.GetProperty(name)?.GetValue(result);

            return new
            {
                jobId = Get("JobId"),
                succeeded = Get("Succeeded"),
                error = Get("Error"),
                symbolsProcessed = Get("SymbolsProcessed"),
                symbolsReused = Get("SymbolsReused"),
                eventsProcessed = Get("EventsProcessed"),
                observationsWritten = Get("ObservationsWritten"),
                outputFiles = Get("OutputFiles"),
                manifestPath
            };
        }

        private static List<Dictionary<string, object>> ReadTable(LiveRun run, string table)
        {
            var path = Path.Combine(run.WorkDir, "out", $"live-{run.Id}", "experiment",
                $"{run.Request.ExperimentName}_{table}.csv");
            if (!File.Exists(path))
            {
                return new List<Dictionary<string, object>>();
            }

            var rows = new List<Dictionary<string, object>>();
            var lines = File.ReadAllLines(path);
            if (lines.Length < 2)
            {
                return rows;
            }

            var header = lines[0].Split(',');
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var cells = line.Split(',');
                var row = new Dictionary<string, object>();
                for (var i = 0; i < header.Length && i < cells.Length; i++)
                {
                    row[header[i]] = cells[i];
                }

                rows.Add(row);
            }

            return rows;
        }
    }
}
