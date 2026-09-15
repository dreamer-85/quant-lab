using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace QuantConnect.Research.Runner
{
    /// <summary>
    /// Cloud Run service front-end over the research engine.
    /// Start with `--web` to run as an HTTP service (Kestrel) instead of a
    /// one-shot CLI job. The container binds 0.0.0.0:$PORT (default 8080),
    /// which works with Cloud Run's startup/health probes.
    ///
    /// Endpoints:
    ///   GET  /healthz        -> 200 OK (probe target)
    ///   GET  /               -> service info JSON
    ///   POST /run            -> executes a job from a JSON body; returns the
    ///                           execution result + manifest (HTTP 200 on
    ///                           success, 400 on bad job, 500 on crash)
    ///   POST /run-job-file   -> same, but the body is { "jobFile": "...", ... }
    ///                           (less common; the /run document form is preferred)
    /// </summary>
    public static class WebServer
    {
        public static async Task<int> Run(string[] args)
        {
            string dataDir = null;
            string outputDir = null;
            string portOverride = null;
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
                    default:
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        return 2;
                }
            }

            var port = portOverride
                ?? Environment.GetEnvironmentVariable("PORT")
                ?? "8080";

            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            var app = builder.Build();

            app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
            app.MapGet("/", () => Results.Ok(new
            {
                service = "quantlab-research-runner",
                mode = "web",
                port,
                dataDir = dataDir ?? Environment.GetEnvironmentVariable("QUANTLAB_DATA_ROOT") ?? "default",
                outputDir = outputDir ?? Environment.GetEnvironmentVariable("QUANTLAB_OUTPUT_ROOT") ?? "default"
            }));

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
                    return Results.Json(new
                    {
                        jobId = result.JobId,
                        succeeded = result.Succeeded,
                        error = result.Error,
                        symbolsProcessed = result.SymbolsProcessed,
                        symbolsReused = result.SymbolsReused,
                        eventsProcessed = result.EventsProcessed,
                        observationsWritten = result.ObservationsWritten,
                        outputFiles = result.OutputFiles,
                        elapsedSeconds = result.Elapsed.TotalSeconds,
                        manifestPath
                    }, statusCode: result.Succeeded ? 200 : 200);
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
                    return Results.Json(new
                    {
                        jobId = result.JobId,
                        succeeded = result.Succeeded,
                        error = result.Error,
                        symbolsProcessed = result.SymbolsProcessed,
                        symbolsReused = result.SymbolsReused,
                        eventsProcessed = result.EventsProcessed,
                        observationsWritten = result.ObservationsWritten,
                        outputFiles = result.OutputFiles,
                        elapsedSeconds = result.Elapsed.TotalSeconds,
                        manifestPath
                    }, statusCode: 200);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = ex.ToString() }, statusCode: 500);
                }
            });

            app.Urls.Add($"http://0.0.0.0:{port}");
            Console.WriteLine($"quantlab web server listening on http://0.0.0.0:{port}");
            await app.RunAsync();
            return 0;
        }
    }
}