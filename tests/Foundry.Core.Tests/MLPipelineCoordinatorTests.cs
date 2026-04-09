using Foundry.Models;
using Foundry.Services;
using Xunit;

namespace Foundry.Core.Tests;

[CollectionDefinition("CoordinatorTests")]
public class CoordinatorTestsCollection { }

/// <summary>
/// Unit tests for MLPipelineCoordinator covering analytics, forecast, embeddings,
/// full pipeline runs, and artifact export — using the fallback engine when Python/ONNX
/// are unavailable (matching CI environment behaviour).
/// </summary>
[Collection("CoordinatorTests")]
public sealed class MLPipelineCoordinatorTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an MLAnalyticsService that always uses the fallback engine
    /// (no Python scripts directory, no ONNX models).
    /// </summary>
    private static MLAnalyticsService BuildFallbackMlService() =>
        new MLAnalyticsService(
            new ProcessRunner(),
            Path.Combine(Path.GetTempPath(), "no-scripts-" + Guid.NewGuid().ToString("N")[..6]),
            onnxEngine: new OnnxMLEngine(Path.Combine(Path.GetTempPath(), "no-models-" + Guid.NewGuid().ToString("N")[..6])),
            cacheTtl: TimeSpan.Zero);

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mlcoord-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private static IReadOnlyList<TrainingAttemptRecord> MakeSampleAttempts() =>
    [
        new TrainingAttemptRecord
        {
            CompletedAt = DateTimeOffset.Now,
            Questions =
            [
                new TrainingAttemptQuestionRecord { Topic = "grounding", Correct = true },
                new TrainingAttemptQuestionRecord { Topic = "grounding", Correct = false },
                new TrainingAttemptQuestionRecord { Topic = "protection", Correct = true },
            ],
        },
    ];

    // -------------------------------------------------------------------------
    // RunMLEmbeddingsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunMLEmbeddingsAsync_FallbackEngine_ReturnsResult()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        var result = await coordinator.RunMLEmbeddingsAsync([]);

        Assert.NotNull(result);
        Assert.Equal("fallback", result.Engine);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task RunMLEmbeddingsAsync_PersistsResultToStore()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var store = new MLResultStore(db);
        var coordinator = new MLPipelineCoordinator(BuildFallbackMlService(), store);

        await coordinator.RunMLEmbeddingsAsync([]);

        var persisted = store.LoadEmbeddings();
        Assert.NotNull(persisted);
        Assert.Equal("fallback", persisted.Engine);
    }

    [Fact]
    public async Task RunMLEmbeddingsAsync_WithQuery_ReturnsResult()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        var result = await coordinator.RunMLEmbeddingsAsync([], query: "test query");

        Assert.NotNull(result);
        Assert.Equal("fallback", result.Engine);
    }

    // -------------------------------------------------------------------------
    // RunFullMLPipelineAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunFullMLPipelineAsync_ReturnsAllThreeResults()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        var result = await coordinator.RunFullMLPipelineAsync(
            MakeSampleAttempts(),
            [],
            [],
            tmpDir.Path);

        Assert.NotNull(result);
        Assert.NotNull(result.Analytics);
        Assert.NotNull(result.Forecast);
        Assert.NotNull(result.Embeddings);
        Assert.NotNull(result.Artifacts);
        Assert.NotEmpty(result.ExportPath);
    }

    [Fact]
    public async Task RunFullMLPipelineAsync_PersistsAllResultsToStore()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var store = new MLResultStore(db);
        var coordinator = new MLPipelineCoordinator(BuildFallbackMlService(), store);

        await coordinator.RunFullMLPipelineAsync(
            MakeSampleAttempts(),
            [],
            [],
            tmpDir.Path);

        Assert.NotNull(store.LoadAnalytics());
        Assert.NotNull(store.LoadForecast());
        Assert.NotNull(store.LoadEmbeddings());
    }

    [Fact]
    public async Task RunFullMLPipelineAsync_AllEnginesAreFallback_WhenNoPythonOrOnnx()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        var result = await coordinator.RunFullMLPipelineAsync([], [], [], tmpDir.Path);

        Assert.Equal("fallback", result.Analytics.Engine);
        Assert.Equal("fallback", result.Forecast.Engine);
        Assert.Equal("fallback", result.Embeddings.Engine);
    }

    [Fact]
    public async Task RunFullMLPipelineAsync_ExportPathPointsToExistingFile()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        var result = await coordinator.RunFullMLPipelineAsync([], [], [], tmpDir.Path);

        Assert.True(File.Exists(result.ExportPath), $"Export file not found: {result.ExportPath}");
    }

    [Fact]
    public async Task RunFullMLPipelineAsync_LastRunTimestampUpdatedInStore()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var store = new MLResultStore(db);
        var coordinator = new MLPipelineCoordinator(BuildFallbackMlService(), store);

        var before = DateTimeOffset.Now.AddSeconds(-1);
        await coordinator.RunFullMLPipelineAsync([], [], [], tmpDir.Path);
        var timestamp = store.LoadLastRunTimestamp();

        Assert.NotNull(timestamp);
        Assert.True(timestamp.Value >= before);
    }

    // -------------------------------------------------------------------------
    // ExportSuiteArtifactsAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportSuiteArtifactsAsync_ReturnsBundleWithFallback()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        var analytics = new MLAnalyticsResult { Ok = false, Engine = "fallback" };
        var embeddings = new MLEmbeddingsResult { Ok = false, Engine = "fallback" };
        var forecast = new MLForecastResult { Ok = false, Engine = "fallback" };

        var bundle = await coordinator.ExportSuiteArtifactsAsync(
            analytics, embeddings, forecast, tmpDir.Path);

        Assert.NotNull(bundle);
    }

    [Fact]
    public async Task ExportSuiteArtifactsAsync_WritesArtifactFileToDisk()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        await coordinator.ExportSuiteArtifactsAsync(
            new MLAnalyticsResult { Ok = false, Engine = "not-run" },
            new MLEmbeddingsResult { Ok = false, Engine = "not-run" },
            new MLForecastResult { Ok = false, Engine = "not-run" },
            tmpDir.Path);

        var artifactsDir = Path.Combine(tmpDir.Path, "ml-artifacts");
        Assert.True(Directory.Exists(artifactsDir));
        Assert.NotEmpty(Directory.GetFiles(artifactsDir, "suite-artifacts-*.json"));
    }

    // -------------------------------------------------------------------------
    // Cancellation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunFullMLPipelineAsync_PreCancelledToken_ThrowsOperationCancelled()
    {
        using var tmpDir = new TempDirectory();
        var db = new FoundryDatabase(tmpDir.Path);
        var coordinator = new MLPipelineCoordinator(
            BuildFallbackMlService(),
            new MLResultStore(db));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RunFullMLPipelineAsync([], [], [], tmpDir.Path, cts.Token));
    }
}
