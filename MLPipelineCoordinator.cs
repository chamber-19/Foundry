using DailyDesk.Models;

namespace DailyDesk.Services;

/// <summary>
/// Domain coordinator for ML pipeline operations: analytics, forecast, embeddings, and artifact export.
/// Extracted from OfficeBrokerOrchestrator per TECHNICAL-DEBT.md.
/// </summary>
public sealed class MLPipelineCoordinator
{
    private readonly MLAnalyticsService _mlAnalyticsService;
    private readonly MLResultStore _mlResultStore;

    public MLPipelineCoordinator(
        MLAnalyticsService mlAnalyticsService,
        MLResultStore mlResultStore)
    {
        _mlAnalyticsService = mlAnalyticsService;
        _mlResultStore = mlResultStore;
    }

    /// <summary>
    /// Runs document embeddings and persists the result to the ML result store.
    /// </summary>
    public async Task<MLEmbeddingsResult> RunMLEmbeddingsAsync(
        IReadOnlyList<LearningDocument> documents,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _mlAnalyticsService.RunDocumentEmbeddingsAsync(
            documents,
            query,
            cancellationToken);

        _mlResultStore.SaveEmbeddings(result);
        return result;
    }

    /// <summary>
    /// Runs embeddings, then generates and exports a suite artifact bundle.
    /// The embeddings result is persisted to the ML result store.
    /// Analytics and Forecast return default values.
    /// </summary>
    public async Task<MLPipelineRunResult> RunFullMLPipelineAsync(
        IReadOnlyList<TrainingAttemptRecord> attempts,
        IReadOnlyList<OperatorActivityRecord> decisions,
        IReadOnlyList<LearningDocument> documents,
        string stateRootPath,
        CancellationToken cancellationToken = default)
    {
        var embeddings = await _mlAnalyticsService.RunDocumentEmbeddingsAsync(
            documents,
            null,
            cancellationToken);

        var analytics = new MLAnalyticsResult();
        var forecast = new MLForecastResult();

        var artifacts = await _mlAnalyticsService.GenerateSuiteArtifactsAsync(
            analytics,
            embeddings,
            forecast,
            cancellationToken);

        var exportPath = await _mlAnalyticsService.ExportArtifactsAsync(
            artifacts,
            stateRootPath,
            cancellationToken);

        _mlResultStore.SaveEmbeddings(embeddings);

        return new MLPipelineRunResult
        {
            Analytics = analytics,
            Forecast = forecast,
            Embeddings = embeddings,
            Artifacts = artifacts,
            ExportPath = exportPath,
        };
    }

    /// <summary>
    /// Generates and exports a suite artifact bundle from the provided ML results.
    /// </summary>
    public async Task<SuiteMLArtifactBundle> ExportSuiteArtifactsAsync(
        MLAnalyticsResult analytics,
        MLEmbeddingsResult embeddings,
        MLForecastResult forecast,
        string stateRootPath,
        CancellationToken cancellationToken = default)
    {
        var artifacts = await _mlAnalyticsService.GenerateSuiteArtifactsAsync(
            analytics,
            embeddings,
            forecast,
            cancellationToken);

        await _mlAnalyticsService.ExportArtifactsAsync(
            artifacts,
            stateRootPath,
            cancellationToken);

        return artifacts;
    }
}

/// <summary>
/// Combined result from a full ML pipeline run.
/// </summary>
public sealed class MLPipelineRunResult
{
    public MLAnalyticsResult Analytics { get; init; } = new();
    public MLForecastResult Forecast { get; init; } = new();
    public MLEmbeddingsResult Embeddings { get; init; } = new();
    public SuiteMLArtifactBundle Artifacts { get; init; } = new();
    public string ExportPath { get; init; } = string.Empty;
}
