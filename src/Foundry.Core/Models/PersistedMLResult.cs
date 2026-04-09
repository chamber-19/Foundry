namespace Foundry.Models;

/// <summary>
/// Wraps an ML result for LiteDB persistence with metadata.
/// Each result type (analytics, forecast, embeddings) has its own collection.
/// Only the latest result is kept per type.
/// </summary>
public sealed class PersistedMLResult
{
    /// <summary>
    /// Fixed key — only one record per collection (always "latest").
    /// </summary>
    public string Id { get; set; } = "latest";

    /// <summary>
    /// Serialized JSON of the ML result (MLAnalyticsResult, MLForecastResult, or MLEmbeddingsResult).
    /// </summary>
    public string ResultJson { get; set; } = string.Empty;

    /// <summary>
    /// When the ML run completed.
    /// </summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>
    /// Which engine produced the result (e.g. "onnx", "python", "fallback").
    /// </summary>
    public string Engine { get; set; } = string.Empty;

    /// <summary>
    /// Whether the result was a real ML result (true) or a degraded fallback (false).
    /// </summary>
    public bool Ok { get; set; }
}
