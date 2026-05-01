namespace Foundry.Models;

/// <summary>
/// Represents a scheduled job definition stored in LiteDB.
/// Supports simple interval-based or cron-expression-based scheduling.
/// </summary>
public sealed class JobSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;

    /// <summary>
    /// Cron expression (5-part: minute hour day month weekday) or simple interval like "every 30m", "every 2h", "every 1d".
    /// </summary>
    public string CronExpression { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>
    /// Optional JSON payload passed to the enqueued job's RequestPayload.
    /// </summary>
    public string? RequestPayload { get; set; }
}
