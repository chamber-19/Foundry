using System.Text.Json;
using Foundry.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

/// <summary>
/// Persists latest ML results (analytics, forecast, embeddings) to LiteDB.
/// Stores only the most recent result per type, keyed as "latest".
/// Used to restore ML state after restart so export-artifacts is safe.
/// </summary>
public sealed class MLResultStore
{
    private readonly FoundryDatabase _db;
    private readonly ILogger<MLResultStore> _logger;
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public MLResultStore(FoundryDatabase db, ILogger<MLResultStore>? logger = null)
    {
        _db = db;
        _logger = logger ?? NullLogger<MLResultStore>.Instance;
    }

    /// <summary>
    /// Persists the latest analytics result, replacing any prior record.
    /// </summary>
    public void SaveAnalytics(MLAnalyticsResult result)
    {
        Upsert(_db.MLAnalyticsResults, result, result.Engine, result.Ok);
        _logger.LogDebug("Persisted ML analytics result (engine={Engine}, ok={Ok}).", result.Engine, result.Ok);
    }

    /// <summary>
    /// Persists the latest forecast result, replacing any prior record.
    /// </summary>
    public void SaveForecast(MLForecastResult result)
    {
        Upsert(_db.MLForecastResults, result, result.Engine, result.Ok);
        _logger.LogDebug("Persisted ML forecast result (engine={Engine}, ok={Ok}).", result.Engine, result.Ok);
    }

    /// <summary>
    /// Persists the latest embeddings result, replacing any prior record.
    /// </summary>
    public void SaveEmbeddings(MLEmbeddingsResult result)
    {
        var firstEmbedding = result.Embeddings is { Count: > 0 } ? result.Embeddings[0] : null;
        Upsert(_db.MLEmbeddingsResults, result, result.Engine, result.Ok,
            embeddingModel: result.Engine,
            embeddingDim: firstEmbedding?.Dimensions);
        _logger.LogDebug("Persisted ML embeddings result (engine={Engine}, ok={Ok}).", result.Engine, result.Ok);
    }

    /// <summary>
    /// Loads the last persisted analytics result, or null if none exists.
    /// </summary>
    public MLAnalyticsResult? LoadAnalytics()
    {
        return Load<MLAnalyticsResult>(_db.MLAnalyticsResults);
    }

    /// <summary>
    /// Loads the last persisted forecast result, or null if none exists.
    /// </summary>
    public MLForecastResult? LoadForecast()
    {
        return Load<MLForecastResult>(_db.MLForecastResults);
    }

    /// <summary>
    /// Loads the last persisted embeddings result, or null if none exists.
    /// </summary>
    public MLEmbeddingsResult? LoadEmbeddings()
    {
        return Load<MLEmbeddingsResult>(_db.MLEmbeddingsResults);
    }

    /// <summary>
    /// Returns the timestamp of the most recent persisted ML run across all types,
    /// or null if no results have been persisted.
    /// </summary>
    public DateTimeOffset? LoadLastRunTimestamp()
    {
        DateTimeOffset? latest = null;

        var analytics = _db.MLAnalyticsResults.FindById("latest");
        if (analytics is not null && (latest is null || analytics.CompletedAt > latest))
            latest = analytics.CompletedAt;

        var forecast = _db.MLForecastResults.FindById("latest");
        if (forecast is not null && (latest is null || forecast.CompletedAt > latest))
            latest = forecast.CompletedAt;

        var embeddings = _db.MLEmbeddingsResults.FindById("latest");
        if (embeddings is not null && (latest is null || embeddings.CompletedAt > latest))
            latest = embeddings.CompletedAt;

        return latest;
    }

    private static void Upsert<T>(LiteDB.ILiteCollection<PersistedMLResult> collection, T result, string engine, bool ok,
        string? embeddingModel = null, int? embeddingDim = null)
    {
        var record = new PersistedMLResult
        {
            Id = "latest",
            ResultJson = JsonSerializer.Serialize(result, JsonOptions),
            CompletedAt = DateTimeOffset.Now,
            Engine = engine,
            Ok = ok,
            EmbeddingModel = embeddingModel,
            EmbeddingDim = embeddingDim,
        };
        collection.Upsert(record);
    }

    private T? Load<T>(LiteDB.ILiteCollection<PersistedMLResult> collection)
    {
        var record = collection.FindById("latest");
        if (record is null || string.IsNullOrWhiteSpace(record.ResultJson))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(record.ResultJson, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize persisted ML result from collection, returning null.");
            return default;
        }
    }
}
