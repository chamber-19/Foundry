using DailyDesk.Services;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace DailyDesk.Broker;

internal static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app, ILogger logger)
    {
        // --- Schedule Endpoints (Phase 8) ---

        app.MapGet("/api/schedules", (OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var schedules = orchestrator.SchedulerStore.ListAll();
                return Results.Ok(new { schedules });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/schedules");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapPost("/api/schedules", (CreateScheduleRequest request, OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var validator = new CreateScheduleRequestValidator();
                var validation = validator.Validate(request);
                if (!validation.IsValid)
                {
                    return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
                }

                var schedule = new DailyDesk.Models.JobSchedule
                {
                    Name = request.Name,
                    JobType = request.JobType,
                    CronExpression = request.CronExpression,
                    Enabled = request.Enabled ?? true,
                    RequestPayload = request.RequestPayload,
                };

                var created = orchestrator.SchedulerStore.Create(schedule);
                return Results.Created($"/api/schedules/{created.Id}", created);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/schedules");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapPut("/api/schedules/{id}", (string id, UpdateScheduleRequest request, OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var validator = new UpdateScheduleRequestValidator();
                var validation = validator.Validate(request);
                if (!validation.IsValid)
                {
                    return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
                }

                var updated = orchestrator.SchedulerStore.Update(id, schedule =>
                {
                    if (request.Name is not null) schedule.Name = request.Name;
                    if (request.CronExpression is not null) schedule.CronExpression = request.CronExpression;
                    if (request.Enabled.HasValue) schedule.Enabled = request.Enabled.Value;
                    if (request.RequestPayload is not null) schedule.RequestPayload = request.RequestPayload;
                });

                if (updated is null)
                {
                    return Results.NotFound(new { error = $"Schedule '{id}' not found." });
                }

                return Results.Ok(updated);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/schedules/{id}");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapDelete("/api/schedules/{id}", (string id, OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var deleted = orchestrator.SchedulerStore.Delete(id);
                if (!deleted)
                {
                    return Results.NotFound(new { error = $"Schedule '{id}' not found." });
                }
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/schedules/{id}");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        // --- Daily Run Endpoint (Phase 8) ---

        app.MapGet("/api/daily-run/latest", (OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var summary = orchestrator.GetLatestDailyRunSummary();
                if (summary is null)
                {
                    return Results.Ok(new { message = "No daily run has been executed yet." });
                }
                return Results.Ok(summary);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/daily-run/latest");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        // --- Workflow Endpoints (Phase 8) ---

        app.MapGet("/api/workflows", (OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var workflows = orchestrator.WorkflowStore.ListAll();
                return Results.Ok(new { workflows });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/workflows");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapPost("/api/workflows", (CreateWorkflowRequest request, OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var validator = new CreateWorkflowRequestValidator();
                var validation = validator.Validate(request);
                if (!validation.IsValid)
                {
                    return Results.BadRequest(new { errors = validation.Errors.Select(e => e.ErrorMessage) });
                }

                var template = new DailyDesk.Models.WorkflowTemplate
                {
                    Name = request.Name,
                    Description = request.Description ?? string.Empty,
                    FailurePolicy = request.FailurePolicy ?? DailyDesk.Models.WorkflowFailurePolicy.Abort,
                    Steps = request.Steps.Select(s => new DailyDesk.Models.WorkflowStep
                    {
                        JobType = s.JobType,
                        Label = s.Label ?? string.Empty,
                        RequestPayload = s.RequestPayload,
                    }).ToList(),
                };

                var created = orchestrator.WorkflowStore.Create(template);
                return Results.Created($"/api/workflows/{created.Id}", created);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/workflows");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapPost("/api/workflows/{id}/run", (string id, OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var template = orchestrator.WorkflowStore.GetById(id);
                if (template is null)
                {
                    return Results.NotFound(new { error = $"Workflow '{id}' not found." });
                }

                var jobIds = new List<string>();
                foreach (var step in template.Steps)
                {
                    var job = orchestrator.JobStore.Enqueue(
                        step.JobType,
                        requestedBy: $"workflow:{template.Name}",
                        requestPayload: step.RequestPayload);
                    jobIds.Add(job.Id);
                }

                return Results.Accepted(value: new
                {
                    workflowId = id,
                    workflowName = template.Name,
                    jobIds,
                    totalSteps = template.Steps.Count,
                });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/workflows/{id}/run");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });

        app.MapDelete("/api/workflows/{id}", (string id, OfficeBrokerOrchestrator orchestrator) =>
        {
            try
            {
                var deleted = orchestrator.WorkflowStore.Delete(id);
                if (!deleted)
                {
                    return Results.NotFound(new { error = $"Workflow '{id}' not found or is a built-in template." });
                }
                return Results.NoContent();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Endpoint {Endpoint} failed", "/api/workflows/{id}");
                return Results.Problem(
                    detail: "An unexpected error occurred. See server logs for details.",
                    statusCode: 500
                );
            }
        });
    }
}

// Phase 8: Schedule request records
internal sealed record CreateScheduleRequest(
    string Name,
    string JobType,
    string CronExpression,
    bool? Enabled,
    string? RequestPayload
);
internal sealed record UpdateScheduleRequest(
    string? Name,
    string? CronExpression,
    bool? Enabled,
    string? RequestPayload
);

// Phase 8: Workflow request records
internal sealed record CreateWorkflowRequest(
    string Name,
    string? Description,
    string? FailurePolicy,
    IReadOnlyList<CreateWorkflowStepRequest> Steps
);
internal sealed record CreateWorkflowStepRequest(
    string JobType,
    string? Label,
    string? RequestPayload
);

internal sealed class CreateScheduleRequestValidator : AbstractValidator<CreateScheduleRequest>
{
    public CreateScheduleRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Name is required.");

        RuleFor(x => x.JobType)
            .NotEmpty()
            .WithMessage("JobType is required.");

        RuleFor(x => x.CronExpression)
            .NotEmpty()
            .WithMessage("CronExpression is required.")
            .Must(cron => DailyDesk.Services.JobSchedulerStore.ComputeNextRun(cron, DateTimeOffset.Now) is not null)
            .WithMessage("CronExpression is not a valid cron expression or simple interval (e.g. 'every 30m', 'every 2h', '0 8 * * *').");
    }
}

internal sealed class UpdateScheduleRequestValidator : AbstractValidator<UpdateScheduleRequest>
{
    public UpdateScheduleRequestValidator()
    {
        RuleFor(x => x.CronExpression)
            .Must(cron => cron is null || DailyDesk.Services.JobSchedulerStore.ComputeNextRun(cron, DateTimeOffset.Now) is not null)
            .WithMessage("CronExpression is not a valid cron expression or simple interval.");
    }
}

internal sealed class CreateWorkflowRequestValidator : AbstractValidator<CreateWorkflowRequest>
{
    public CreateWorkflowRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithMessage("Name is required.");

        RuleFor(x => x.Steps)
            .NotEmpty()
            .WithMessage("At least one step is required.");

        RuleForEach(x => x.Steps)
            .ChildRules(step =>
            {
                step.RuleFor(s => s.JobType)
                    .NotEmpty()
                    .WithMessage("Step JobType is required.");
            });

        RuleFor(x => x.FailurePolicy)
            .Must(p => p is null
                       || p.Equals(DailyDesk.Models.WorkflowFailurePolicy.Abort, StringComparison.OrdinalIgnoreCase)
                       || p.Equals(DailyDesk.Models.WorkflowFailurePolicy.Continue, StringComparison.OrdinalIgnoreCase))
            .WithMessage("FailurePolicy must be 'abort' or 'continue'.");
    }
}
