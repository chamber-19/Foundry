namespace Foundry.Models;

/// <summary>
/// Represents an async job record with lifecycle tracking.
/// Persisted in the LiteDB jobs collection.
/// </summary>
public sealed class FoundryJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = FoundryJobStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Monotonically increasing counter assigned at enqueue time.
    /// Used as a secondary sort key in <see cref="FoundryJobStore.DequeueNext"/> to guarantee
    /// stable FIFO ordering even when multiple jobs share the same <see cref="CreatedAt"/>
    /// timestamp (e.g. jobs enqueued within the same clock tick).
    /// Legacy records persisted before this field was introduced have the default value 0
    /// and are ordered by <see cref="CreatedAt"/> instead.
    /// </summary>
    public int SequenceNumber { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? ResultJson { get; set; }
    public string? RequestedBy { get; set; }
    public string? RequestPayload { get; set; }
    public string? TraceId { get; set; }
}

/// <summary>
/// Constants for job status values.
/// </summary>
public static class FoundryJobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// Constants for job type values.
/// </summary>
public static class FoundryJobType
{
    public const string MLAnalytics = "ml-analytics";
    public const string MLForecast = "ml-forecast";
    public const string MLEmbeddings = "ml-embeddings";
    public const string MLPipeline = "ml-pipeline";
    public const string MLExportArtifacts = "ml-export-artifacts";
    public const string KnowledgeIndex = "knowledge-index";
    public const string DailyRun = "daily-run";
}
