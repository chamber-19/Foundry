using DailyDesk.Services;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace DailyDesk.Broker;

internal static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        app.MapGet("/api/chat/threads", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var threads = await orchestrator.GetChatThreadsAsync(ct);
                return Results.Ok(new { threads });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker chat threads endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to load chat threads",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/chat/route", async (ChatRouteRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            var validation = new ChatRouteRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
            }

            try
            {
                var route = await orchestrator.SetChatRouteAsync(request.Route, ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { route, state });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker chat route endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to set chat route",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/chat/send", async (ChatSendRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            var validation = new ChatSendRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
            }

            try
            {
                var message = await orchestrator.SendChatAsync(request.Prompt, request.RouteOverride, ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { message, state });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker chat send endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to send chat message",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });
    }
}

internal sealed record ChatRouteRequest(string Route);
internal sealed record ChatSendRequest(string Prompt, string? RouteOverride);

internal sealed class ChatRouteRequestValidator : AbstractValidator<ChatRouteRequest>
{
    public ChatRouteRequestValidator()
    {
        RuleFor(x => x.Route)
            .NotEmpty()
            .WithMessage("Route is required.")
            .Must(route =>
            {
                var trimmed = route?.Trim().ToLowerInvariant();
                return !string.IsNullOrWhiteSpace(trimmed)
                    && OfficeRouteCatalog.KnownRoutes.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
            })
            .WithMessage($"Route must be one of: {string.Join(", ", OfficeRouteCatalog.KnownRoutes)}.");
    }
}

internal sealed class ChatSendRequestValidator : AbstractValidator<ChatSendRequest>
{
    public ChatSendRequestValidator()
    {
        RuleFor(x => x.Prompt)
            .NotEmpty()
            .WithMessage("Prompt is required.");
    }
}
