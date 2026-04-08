using DailyDesk.Services;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace DailyDesk.Broker;

internal static class ResearchEndpoints
{
    public static void MapResearchEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        app.MapPost("/api/research/run", async (ResearchRunRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            var validation = new ResearchRunRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
            }

            try
            {
                var report = await orchestrator.RunResearchAsync(request.Query, request.Perspective, request.SaveToLibrary ?? false, ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { report, state });
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
                logger.LogError(exception, "Office broker research run endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to run research",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/research/save", async (ResearchSaveRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var filePath = await orchestrator.SaveLatestResearchAsync(request.Notes, ct);
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { filePath, state });
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
                logger.LogError(exception, "Office broker research save endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to save research",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        app.MapPost("/api/watchlists/run", async (WatchlistRunRequest request, OfficeBrokerOrchestrator orchestrator, CancellationToken ct) =>
        {
            var validation = new WatchlistRunRequestValidator().Validate(request);
            if (!validation.IsValid)
            {
                return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
            }

            try
            {
                var report = await orchestrator.RunWatchlistAsync(
                    request.WatchlistId,
                    request.SaveToLibrary,
                    ct
                );
                var state = await orchestrator.GetStateAsync(ct);
                return Results.Ok(new { report, state });
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
                logger.LogError(exception, "Office broker watchlist run endpoint failed.");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    title: "Failed to run watchlist",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });
    }
}

internal sealed record ResearchRunRequest(string Query, string? Perspective, bool? SaveToLibrary);
internal sealed record ResearchSaveRequest(string? Notes);
internal sealed record WatchlistRunRequest(string WatchlistId, bool? SaveToLibrary);

internal sealed class ResearchRunRequestValidator : AbstractValidator<ResearchRunRequest>
{
    public ResearchRunRequestValidator()
    {
        RuleFor(x => x.Query)
            .NotEmpty()
            .WithMessage("Query is required.");
    }
}

internal sealed class WatchlistRunRequestValidator : AbstractValidator<WatchlistRunRequest>
{
    public WatchlistRunRequestValidator()
    {
        RuleFor(x => x.WatchlistId)
            .NotEmpty()
            .WithMessage("WatchlistId is required.");
    }
}
