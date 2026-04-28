using Foundry.Services;
using Microsoft.Extensions.Logging;

namespace Foundry.Broker;

internal static class DependencyEndpoints
{
    public static void MapDependencyEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        app.MapGet("/api/notifications", (bool? pending, int? limit, FoundryOrchestrator orchestrator) =>
        {
            try
            {
                return Results.Ok(new
                {
                    notifications = orchestrator.ListNotifications(pending ?? false, limit ?? 50),
                });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/notifications");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/notifications/{id}/delivered", (
            string id,
            NotificationDeliveredRequest? request,
            FoundryOrchestrator orchestrator) =>
        {
            try
            {
                var notification = orchestrator.MarkNotificationDelivered(id, request?.DeliveredTo);
                return notification is null
                    ? Results.NotFound(new { error = $"Notification '{id}' not found." })
                    : Results.Ok(notification);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/notifications/{id}/delivered");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/dependencies/poll", async (FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.PollDependenciesAsync(ct));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/dependencies/poll");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/api/models", async (FoundryOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.GetModelsAsync(ct));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/models");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}

internal sealed record NotificationDeliveredRequest(string? DeliveredTo);
