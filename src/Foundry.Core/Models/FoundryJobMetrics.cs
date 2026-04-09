namespace Foundry.Models;

/// <summary>
/// Snapshot of job system metrics for the dashboard endpoint.
/// </summary>
public sealed class FoundryJobMetrics
{
    public int TotalJobs { get; set; }
    public int QueuedCount { get; set; }
    public int RunningCount { get; set; }
    public int SucceededCount { get; set; }
    public int FailedCount { get; set; }
    public double? AverageDurationSeconds { get; set; }
    public int CompletedLastHour { get; set; }
    public int CompletedLastDay { get; set; }
}
