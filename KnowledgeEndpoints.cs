using DailyDesk.Services;
using Microsoft.Extensions.Logging;

namespace DailyDesk.Broker;

internal static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        // --- Knowledge Indexing Endpoints (Phase 5) ---

        app.MapPost("/api/ml/index-knowledge", async (HttpContext httpContext, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            var sync = httpContext.Request.Query["sync"].FirstOrDefault()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            if (!sync)
            {
                var job = orchestrator.JobStore.Enqueue(DailyDesk.Models.OfficeJobType.KnowledgeIndex, "broker");
                return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, status = job.Status });
            }

            try
            {
                var result = await orchestrator.RunKnowledgeIndexAsync(ct);
                return Results.Ok(result);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge indexing endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to index knowledge documents",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapGet("/api/knowledge/index-status", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var status = await orchestrator.GetKnowledgeIndexStatusAsync(ct);
                return Results.Ok(status);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge index status endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to get index status",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        // --- Knowledge Search Endpoint (Phase 9) ---

        app.MapPost("/api/knowledge/search", async (KnowledgeSearchRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var searchService = new DailyDesk.Services.KnowledgeSearchService(
                    orchestrator.EmbeddingService,
                    orchestrator.VectorStoreService);
                var response = await searchService.SearchAsync(
                    request.Query,
                    topK: request.TopK > 0 ? request.TopK : 5,
                    cancellationToken: ct);
                return Results.Ok(response);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Knowledge search endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Knowledge search failed",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/library/import", async (LibraryImportRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var result = await orchestrator.ImportLibraryFilesAsync(request.Paths ?? Array.Empty<string>(), ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { result, state });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker library import endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to import library files",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });
    }
}

internal sealed record LibraryImportRequest(IReadOnlyList<string>? Paths);

// Phase 9: Knowledge search request record
internal sealed record KnowledgeSearchRequest(string Query, int TopK = 5);
