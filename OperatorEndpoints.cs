using DailyDesk.Services;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace DailyDesk.Broker;

internal static class OperatorEndpoints
{
    public static void MapOperatorEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        app.MapGet("/api/inbox", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await orchestrator.GetInboxAsync(ct));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Office broker inbox endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to load inbox",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/inbox/resolve", async (InboxResolveRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            var validation = new InboxResolveRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
            }

            try
            {
                var suggestion = await orchestrator.ResolveSuggestionAsync(
                    request.SuggestionId,
                    request.Status,
                    request.Reason,
                    request.Note,
                    ct
                );
                var inbox = await orchestrator.GetInboxAsync(ct);
                return Results.Ok(new { suggestion, inbox });
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
                logger.LogError(exception, "Office broker inbox resolve endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to resolve inbox suggestion",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/inbox/queue", async (InboxQueueRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var suggestion = await orchestrator.QueueSuggestionAsync(
                    request.SuggestionId,
                    request.ApproveFirst ?? false,
                    ct
                );
                var inbox = await orchestrator.GetInboxAsync(ct);
                return Results.Ok(new { suggestion, inbox });
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
                logger.LogError(exception, "Office broker inbox queue endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to queue inbox suggestion",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/history/reset", async (OfficeHistoryResetRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var state = await orchestrator.ResetLocalHistoryAsync(
                    request.ClearTrainingHistory ?? true,
                    ct
                );
                return Results.Ok(new { state });
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
                logger.LogError(exception, "Office broker history reset endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to reset Office local history",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/workspace/reset", async (OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var state = await orchestrator.ResetWorkspaceAsync(ct);
                return Results.Ok(new { state });
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
                logger.LogError(exception, "Office broker workspace reset endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to reset Office workspace",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });
    }
}

internal sealed record InboxResolveRequest(
    string SuggestionId,
    string Status,
    string? Reason,
    string? Note
);
internal sealed record InboxQueueRequest(string SuggestionId, bool? ApproveFirst);
internal sealed record OfficeHistoryResetRequest(bool? ClearTrainingHistory);

internal sealed class InboxResolveRequestValidator : AbstractValidator<InboxResolveRequest>
{
    private static readonly string[] ValidStatuses = ["accepted", "deferred", "rejected"];

    public InboxResolveRequestValidator()
    {
        RuleFor(x => x.SuggestionId)
            .NotEmpty()
            .WithMessage("SuggestionId is required.");

        RuleFor(x => x.Status)
            .NotEmpty()
            .WithMessage("Status is required.")
            .Must(status => ValidStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Status must be one of: accepted, deferred, rejected.");
    }
}
