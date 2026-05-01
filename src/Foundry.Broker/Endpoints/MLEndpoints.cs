using Foundry.Services;
using Microsoft.Extensions.Logging;

namespace Foundry.Broker;

internal static class MLEndpoints
{
    public static void MapMLEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        // --- Job Status Endpoints ---

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

        // --- Job Metrics Endpoint ---

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
