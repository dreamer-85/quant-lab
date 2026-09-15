# ---- Build stage: restore, test and publish the Runner (net10.0) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy everything (build context is pruned by .dockerignore)
COPY . .

# Publish the framework-dependent DLL the runtime stage will execute
RUN dotnet publish Research/Runner/QuantConnect.Research.Runner.csproj \
    -c Release \
    -o /app/publish \
    -p:UseAppHost=false

# ---- Runtime stage: minimal, non-root ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Same roots the deploy scripts use (override with -e at run time)
ENV QUANTLAB_DATA_ROOT=/quantlab/data \
    QUANTLAB_OUTPUT_ROOT=/quantlab/output \
    QUANTLAB_CACHE_ROOT=/quantlab/cache \
    QUANTLAB_TEMP_ROOT=/tmp

# Cloud Run services probe port 8080 by default; EXPOSE documents the intent.
# The container binds 0.0.0.0:${PORT} in --web mode, so it is reachable from
# the Cloud Run frontend and satisfies the startup/health probes.
EXPOSE 8080

# The aspnet base image already pre-configures a 'app' user (UID 1000) —
# reuse it instead of creating another user.
RUN mkdir -p /quantlab/data /quantlab/output /quantlab/cache \
    && chown -R app:app /app /quantlab

USER app
VOLUME ["/quantlab"]

# Default: Cloud Run SERVICE mode (HTTP server on $PORT). Omit `--web` to run
# the one-shot CLI as a Cloud Run JOB (the job spec provides --job-file etc.):
#   gcloud run jobs execute uses the same image with --args=... and no --web.
ENTRYPOINT ["dotnet", "QuantConnect.Research.Runner.dll", "--web"]