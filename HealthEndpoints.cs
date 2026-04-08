using DailyDesk.Services;
using Microsoft.Extensions.Logging;

namespace DailyDesk.Broker;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        app.MapGet("/health", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.GetHealthAsync(ct));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker health endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Office broker health check failed",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        // --- Detailed Health Check (Phase 4) ---

        app.MapGet("/api/health", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.GetDetailedHealthAsync(ct));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Detailed health check failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Detailed health check failed",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapGet("/api/state", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.GetStateAsync(ct));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker state endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to build office state",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });
    }
}
