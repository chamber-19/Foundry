using Foundry.Services;
using Microsoft.Extensions.Logging;

namespace Foundry.Broker;

internal static class MLEndpoints
{
    public static void MapMLEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        app.MapPost("/api/ml/analytics", async (HttpContext httpContext, FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            var sync = httpContext.Request.Query["sync"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!sync)
            {
                var job = orchestrator.JobStore.Enqueue(Foundry.Models.FoundryJobType.MLAnalytics, "broker");
                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, status = job.Status });
            }

            try
            {
                var analytics = new Foundry.Models.MLAnalyticsResult { Ok = false, Engine = "not-run" };
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { analytics, state });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Foundry broker ML analytics endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to run ML analytics",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/ml/forecast", async (HttpContext httpContext, FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            var sync = httpContext.Request.Query["sync"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!sync)
            {
                var job = orchestrator.JobStore.Enqueue(Foundry.Models.FoundryJobType.MLForecast, "broker");
                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, status = job.Status });
            }

            try
            {
                var forecast = new Foundry.Models.MLForecastResult { Ok = false, Engine = "not-run" };
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { forecast, state });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Foundry broker ML forecast endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to run ML forecast",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/ml/embeddings", async (HttpContext httpContext, MLEmbeddingsRequest request, FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            var sync = httpContext.Request.Query["sync"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!sync)
            {
                var payload = request.Query is not null ? System.Text.Json.JsonSerializer.Serialize(new { query = request.Query }) : null;
                var job = orchestrator.JobStore.Enqueue(Foundry.Models.FoundryJobType.MLEmbeddings, "broker", payload);
                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, status = job.Status });
            }

            try
            {
                var embeddings = await orchestrator.RunMLEmbeddingsAsync(request.Query, ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { embeddings, state });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Foundry broker ML embeddings endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to run ML embeddings",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/ml/pipeline", async (HttpContext httpContext, FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            var sync = httpContext.Request.Query["sync"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!sync)
            {
                var job = orchestrator.JobStore.Enqueue(Foundry.Models.FoundryJobType.MLPipeline, "broker");
                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, status = job.Status });
            }

            try
            {
                var result = await orchestrator.RunFullMLPipelineAsync(ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { result, state });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Foundry broker full ML pipeline endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to run full ML pipeline",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/ml/export-artifacts", async (HttpContext httpContext, FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            var sync = httpContext.Request.Query["sync"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!sync)
            {
                var job = orchestrator.JobStore.Enqueue(Foundry.Models.FoundryJobType.MLExportArtifacts, "broker");
                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, status = job.Status });
            }

            try
            {
                var artifacts = await orchestrator.ExportSuiteArtifactsAsync(ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { artifacts, state });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Foundry broker ML artifact export endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to export ML artifacts",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        // --- Job Status Endpoints (Phase 3) ---

        app.MapGet("/api/jobs", (HttpContext httpContext, FoundryOrchestrator orchestrator) =>
        {
            try
            {
                var statusFilter = httpContext.Request.Query["status"].FirstOrDefault();
                var typeFilter = httpContext.Request.Query["type"].FirstOrDefault();

                IReadOnlyList<Foundry.Models.FoundryJob> jobs;
                if (!string.IsNullOrWhiteSpace(statusFilter))
                {
                    jobs = orchestrator.JobStore.ListByStatus(statusFilter.ToLowerInvariant(), 50);
                }
                else
                {
                    jobs = orchestrator.JobStore.ListRecent(50);
                }

                if (!string.IsNullOrWhiteSpace(typeFilter))
                {
                    jobs = jobs.Where(j => j.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                return Results.Ok(new { jobs, total = orchestrator.JobStore.GetTotalCount() });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/jobs");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        // --- Job Metrics Endpoint (Phase 4) ---

        app.MapGet("/api/jobs/metrics", (FoundryOrchestrator orchestrator) =>
        {
            try
            {
                return Results.Ok(orchestrator.JobStore.GetMetrics());
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/jobs/metrics");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapGet("/api/jobs/{jobId}", (string jobId, FoundryOrchestrator orchestrator) =>
        {
            try
            {
                var job = orchestrator.JobStore.GetById(jobId);
                if (job is null)
                {
                    return Results.NotFound(new { error = $"Job '{jobId}' not found." });
                }
                return Results.Ok(new
                {
                    job.Id,
                    job.Type,
                    job.Status,
                    job.CreatedAt,
                    job.StartedAt,
                    job.CompletedAt,
                    job.Error,
                    job.RequestedBy,
                });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/jobs/{jobId}");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapGet("/api/jobs/{jobId}/result", (string jobId, FoundryOrchestrator orchestrator) =>
        {
            try
            {
                var job = orchestrator.JobStore.GetById(jobId);
                if (job is null)
                {
                    return Results.NotFound(new { error = $"Job '{jobId}' not found." });
                }
                if (job.Status != Foundry.Models.FoundryJobStatus.Succeeded)
                {
                    return Results.BadRequest(new { error = $"Job '{jobId}' has status '{job.Status}'. Result is only available for succeeded jobs." });
                }
                if (string.IsNullOrWhiteSpace(job.ResultJson))
                {
                    return Results.Ok(new { result = (object?)null });
                }

                try
                {
                    var result = System.Text.Json.JsonSerializer.Deserialize<object>(job.ResultJson);
                    return Results.Ok(new { result });
                }
                catch
                {
                    return Results.Ok(new { result = job.ResultJson });
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/jobs/{jobId}/result");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapDelete("/api/jobs/{jobId}", (string jobId, FoundryOrchestrator orchestrator) =>
        {
            try
            {
                var job = orchestrator.JobStore.GetById(jobId);
                if (job is null)
                {
                    return Results.NotFound(new { error = $"Job '{jobId}' not found." });
                }
                if (job.Status is not (Foundry.Models.FoundryJobStatus.Succeeded or Foundry.Models.FoundryJobStatus.Failed))
                {
                    return Results.BadRequest(new { error = $"Job '{jobId}' has status '{job.Status}'. Only completed (succeeded/failed) jobs can be deleted." });
                }

                orchestrator.JobStore.DeleteById(jobId);
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/jobs/{jobId}");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });
    }
}

internal sealed record MLEmbeddingsRequest(string? Query);
