using Foundry.Models;

namespace Foundry.Services;

// Daily workflow execution.
public sealed partial class FoundryOrchestrator
{
    /// <summary>
    /// Executes the daily workflow: refreshes state, then runs the knowledge
    /// index. Returns a step-by-step summary of all outcomes.
    /// </summary>
    public async Task<DailyRunSummary> RunDailyWorkflowAsync(CancellationToken cancellationToken = default)
    {
        var summary = new DailyRunSummary { StartedAt = DateTimeOffset.Now };
        var stepResults = new List<DailyRunStepResult>();

        // Step 1: Refresh state
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await RefreshContextLockedAsync(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
            stepResults.Add(new DailyRunStepResult { Step = "RefreshState", Success = true });
        }
        catch (Exception ex)
        {
            stepResults.Add(new DailyRunStepResult { Step = "RefreshState", Success = false, Error = ex.Message });
        }

        // Step 2: Knowledge indexing
        try
        {
            await RunKnowledgeIndexAsync(cancellationToken);
            stepResults.Add(new DailyRunStepResult { Step = "KnowledgeIndex", Success = true });
        }
        catch (Exception ex)
        {
            stepResults.Add(new DailyRunStepResult { Step = "KnowledgeIndex", Success = false, Error = ex.Message });
        }

        summary.CompletedAt = DateTimeOffset.Now;
        summary.Steps = stepResults;
        summary.OverallSuccess = stepResults.All(r => r.Success);

        return summary;
    }

    /// <summary>
    /// Returns a summary of the most recent daily run job (succeeded or
    /// failed), or <c>null</c> if no daily run has been recorded.
    /// </summary>
    public DailyRunJobSummary? GetLatestDailyRunSummary()
    {
        const int recentJobLimit = 50;

        var jobs = _jobStore.ListByStatus(FoundryJobStatus.Succeeded, recentJobLimit);
        var dailyRunJob = jobs
            .Where(j => j.Type == FoundryJobType.DailyRun)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefault();

        if (dailyRunJob is null)
        {
            var failedJobs = _jobStore.ListByStatus(FoundryJobStatus.Failed, recentJobLimit);
            dailyRunJob = failedJobs
                .Where(j => j.Type == FoundryJobType.DailyRun)
                .OrderByDescending(j => j.CompletedAt)
                .FirstOrDefault();
        }

        if (dailyRunJob is null) return null;

        return new DailyRunJobSummary
        {
            JobId = dailyRunJob.Id,
            Status = dailyRunJob.Status,
            CreatedAt = dailyRunJob.CreatedAt,
            StartedAt = dailyRunJob.StartedAt,
            CompletedAt = dailyRunJob.CompletedAt,
            ResultJson = dailyRunJob.ResultJson,
            Error = dailyRunJob.Error,
        };
    }
}

/// <summary>
/// Result of a daily run workflow execution.
/// </summary>
public sealed class DailyRunSummary
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public bool OverallSuccess { get; set; }
    public List<DailyRunStepResult> Steps { get; set; } = [];
}

/// <summary>
/// Result of a single step in the daily run workflow.
/// </summary>
public sealed class DailyRunStepResult
{
    public string Step { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Summary view of the latest daily run job.
/// </summary>
public sealed class DailyRunJobSummary
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? Error { get; set; }
}
