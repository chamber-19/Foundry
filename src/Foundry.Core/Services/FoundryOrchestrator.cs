using System.IO;
using Foundry.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

/// <summary>
/// Thin orchestrator that initializes shared infrastructure and delegates to
/// <see cref="MLPipelineCoordinator"/> and <see cref="KnowledgeCoordinator"/>.
/// Owns the daily workflow, health checks, and aggregate state.
/// </summary>
public sealed class FoundryOrchestrator
{
    private static readonly TimeSpan InstalledModelsLoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LearningLibraryLoadTimeout = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _mlGate = new(1, 1);
    private readonly FoundryBrokerRuntimeMetadata _brokerMetadata;
    private readonly FoundrySettings _settings;
    private readonly string _foundryRootPath;
    private readonly string _knowledgeLibraryPath;
    private readonly string _stateRootPath;
    private readonly IReadOnlyList<string> _additionalKnowledgePaths;

    private readonly IModelProvider _modelProvider;
    private readonly KnowledgeImportService _knowledgeImportService;
    private readonly MLAnalyticsService _mlAnalyticsService;
    private readonly MLPipelineCoordinator _mlPipelineCoordinator;
    private readonly KnowledgeCoordinator _knowledgeCoordinator;
    private readonly FoundryDatabase _foundryDatabase;
    private readonly FoundryJobStore _jobStore;
    private readonly MLResultStore _mlResultStore;
    private readonly ProcessRunner _processRunner;
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private readonly KnowledgeIndexStore _knowledgeIndexStore;
    private readonly JobSchedulerStore _schedulerStore;
    private readonly WorkflowStore _workflowStore;

    private bool _initialized;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.Now;
    private IReadOnlyList<string> _installedModelCache = Array.Empty<string>();
    private LearningLibrary _learningLibrary = new();
    private LearningProfile _learningProfile = new();
    private MLAnalyticsResult? _latestMLAnalytics;
    private MLEmbeddingsResult? _latestMLEmbeddings;
    private string? _lastMLArtifactExportPath;
    private DateTimeOffset? _lastMLRunAt;

    public FoundryOrchestrator(FoundryBrokerRuntimeMetadata brokerMetadata, ILoggerFactory? loggerFactory = null)
    {
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        _brokerMetadata = brokerMetadata;
        _foundryRootPath = ResolveFoundryRootPath(AppContext.BaseDirectory);
        var settingsRoot = Path.Combine(_foundryRootPath, "Foundry");
        _settings = FoundrySettings.Load(settingsRoot);
        _knowledgeLibraryPath = _settings.ResolveKnowledgeLibraryPath(settingsRoot);
        _stateRootPath = _settings.ResolveStateRootPath(settingsRoot);
        Directory.CreateDirectory(_knowledgeLibraryPath);
        Directory.CreateDirectory(_stateRootPath);
        _additionalKnowledgePaths = _settings.ResolveAdditionalKnowledgePaths();

        // Resilience pipelines
        var ollamaPipeline = FoundryResiliencePipelines.BuildOllamaPipeline();
        var pythonPipeline = FoundryResiliencePipelines.BuildPythonSubprocessPipeline();

        // LiteDB persistence
        _foundryDatabase = new FoundryDatabase(_stateRootPath);
        _jobStore = new FoundryJobStore(_foundryDatabase);
        _mlResultStore = new MLResultStore(_foundryDatabase, lf.CreateLogger<MLResultStore>());

        _processRunner = new ProcessRunner(lf.CreateLogger<ProcessRunner>());
        _modelProvider = new OllamaService(_settings.OllamaEndpoint, _processRunner, ollamaPipeline, lf.CreateLogger<OllamaService>());
        _knowledgeImportService = new KnowledgeImportService(
            _processRunner,
            Path.Combine(_foundryRootPath, "Foundry", "Scripts", "extract_document_text.py")
        );
        _mlAnalyticsService = new MLAnalyticsService(
            _processRunner,
            Path.Combine(_foundryRootPath, "Foundry", "Scripts"),
            new OnnxMLEngine(Path.Combine(_foundryRootPath, "Foundry", "Models", "onnx")),
            resiliencePipeline: pythonPipeline,
            logger: lf.CreateLogger<MLAnalyticsService>()
        );
        _mlPipelineCoordinator = new MLPipelineCoordinator(_mlAnalyticsService, _mlResultStore);

        // Semantic search services
        var ollamaUri = new Uri(_settings.OllamaEndpoint.EndsWith("/") ? _settings.OllamaEndpoint : $"{_settings.OllamaEndpoint}/");
        var embeddingHttpClient = new System.Net.Http.HttpClient { BaseAddress = ollamaUri, Timeout = TimeSpan.FromSeconds(90) };
        var ollamaEmbedClient = new OllamaSharp.OllamaApiClient(embeddingHttpClient);
        _embeddingService = new EmbeddingService(ollamaEmbedClient, resiliencePipeline: ollamaPipeline, logger: lf.CreateLogger<EmbeddingService>());
        _vectorStoreService = new VectorStoreService(logger: lf.CreateLogger<VectorStoreService>());
        _knowledgeIndexStore = new KnowledgeIndexStore(_foundryDatabase, lf.CreateLogger<KnowledgeIndexStore>());
        _knowledgeCoordinator = new KnowledgeCoordinator(_embeddingService, _vectorStoreService, _knowledgeIndexStore);

        // Scheduled automation & workflow orchestration
        _schedulerStore = new JobSchedulerStore(_foundryDatabase);
        _workflowStore = new WorkflowStore(_foundryDatabase);
    }

    // --- Public accessors for DI consumers ---

    public FoundryJobStore JobStore => _jobStore;
    public int JobRetentionDays => _settings.JobRetentionDays;
    public EmbeddingService EmbeddingService => _embeddingService;
    public VectorStoreService VectorStoreService => _vectorStoreService;
    public KnowledgeIndexStore KnowledgeIndexStore => _knowledgeIndexStore;
    public JobSchedulerStore SchedulerStore => _schedulerStore;
    public WorkflowStore WorkflowStore => _workflowStore;
    public MLPipelineCoordinator MLPipeline => _mlPipelineCoordinator;
    public KnowledgeCoordinator Knowledge => _knowledgeCoordinator;

    // --- Health ---

    public async Task<FoundryHealthReport> GetDetailedHealthAsync(CancellationToken cancellationToken = default)
    {
        var report = new FoundryHealthReport();

        try
        {
            var reachable = await _modelProvider.PingAsync(cancellationToken);
            report.Ollama = reachable
                ? new SubsystemHealth { Status = HealthStatus.Ok }
                : new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = "Ollama did not respond to ping." };
        }
        catch (Exception ex)
        {
            report.Ollama = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        try
        {
            var version = await _processRunner.CheckPythonAsync(cancellationToken);
            report.Python = version is not null
                ? new SubsystemHealth { Status = HealthStatus.Ok, Detail = version }
                : new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = "Python 3 not found on PATH." };
        }
        catch (Exception ex)
        {
            report.Python = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        try
        {
            _foundryDatabase.Jobs.Count();
            report.LiteDB = new SubsystemHealth { Status = HealthStatus.Ok };
        }
        catch (Exception ex)
        {
            report.LiteDB = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        try
        {
            var runningJobs = _jobStore.ListByStatus(FoundryJobStatus.Running);
            var stuckCount = runningJobs.Count(j =>
                j.StartedAt.HasValue && (DateTimeOffset.Now - j.StartedAt.Value).TotalMinutes > 10);

            report.JobWorker = stuckCount > 0
                ? new SubsystemHealth
                {
                    Status = HealthStatus.Degraded,
                    Detail = $"{stuckCount} job(s) running for more than 10 minutes."
                }
                : new SubsystemHealth
                {
                    Status = HealthStatus.Ok,
                    Detail = $"{runningJobs.Count} job(s) currently running."
                };
        }
        catch (Exception ex)
        {
            report.JobWorker = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        var statuses = new[] { report.Ollama.Status, report.Python.Status, report.LiteDB.Status, report.JobWorker.Status };
        if (statuses.Any(s => s == HealthStatus.Unavailable))
            report.Overall = HealthStatus.Unavailable;
        else if (statuses.Any(s => s == HealthStatus.Degraded))
            report.Overall = HealthStatus.Degraded;
        else
            report.Overall = HealthStatus.Ok;

        return report;
    }

    public async Task<object> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return new
            {
                status = "ok",
                broker = _brokerMetadata.BaseUrl,
                provider = _modelProvider.ProviderId,
                providerReady = _installedModelCache.Count > 0,
                refreshedAt = _lastRefreshAt,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    // --- State ---

    public async Task<FoundryBrokerState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return BuildStateLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private FoundryBrokerState BuildStateLocked()
    {
        return new FoundryBrokerState
        {
            Broker = new FoundryBrokerStatusSection
            {
                Host = _brokerMetadata.Host,
                Port = _brokerMetadata.Port,
                BaseUrl = _brokerMetadata.BaseUrl,
                LoopbackOnly = _brokerMetadata.LoopbackOnly,
                StartedAt = _brokerMetadata.StartedAt,
                LastRefreshAt = _lastRefreshAt,
            },
            Provider = new FoundryProviderSection
            {
                Ready = _installedModelCache.Count > 0,
                InstalledModelCount = _installedModelCache.Count,
                InstalledModels = _installedModelCache,
            },
            ML = BuildMLSectionLocked(),
        };
    }

    private FoundryMLSection BuildMLSectionLocked()
    {
        if (!_settings.EnableMLPipeline)
        {
            return new FoundryMLSection
            {
                Enabled = false,
                Summary = "ML pipeline is not enabled. Set enableMLPipeline to true in settings.",
            };
        }

        var summary = _latestMLAnalytics is not null
            ? $"ML pipeline active ({_latestMLAnalytics.Engine}). Readiness: {_latestMLAnalytics.OverallReadiness:P0}. " +
              $"Weak topics: {_latestMLAnalytics.WeakTopics.Count}. " +
              $"Forecast engine: not run. " +
              $"Embeddings: {_latestMLEmbeddings?.Engine ?? "not run"}."
            : "ML pipeline is enabled but has not been run yet. Use the ML endpoints to analyze your learning data.";

        return new FoundryMLSection
        {
            Enabled = true,
            Summary = summary,
            Analytics = _latestMLAnalytics,
            Forecast = null,
            Embeddings = _latestMLEmbeddings,
            LastArtifactExportPath = _lastMLArtifactExportPath,
            LastRunAt = _lastMLRunAt,
        };
    }

    // --- ML Pipeline (delegated) ---

    public async Task<MLEmbeddingsResult> RunMLEmbeddingsAsync(
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LearningDocument> documents;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            documents = _learningLibrary.Documents;
        }
        finally
        {
            _gate.Release();
        }

        await _mlGate.WaitAsync(cancellationToken);
        try
        {
            var result = await _mlPipelineCoordinator.RunMLEmbeddingsAsync(documents, query, cancellationToken);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                _latestMLEmbeddings = result;
                _lastMLRunAt = DateTimeOffset.Now;
            }
            finally
            {
                _gate.Release();
            }

            return result;
        }
        finally
        {
            _mlGate.Release();
        }
    }

    public async Task<object> RunFullMLPipelineAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LearningDocument> documents;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            documents = _learningLibrary.Documents;
        }
        finally
        {
            _gate.Release();
        }

        await _mlGate.WaitAsync(cancellationToken);
        try
        {
            var pipelineResult = await _mlPipelineCoordinator.RunFullMLPipelineAsync(
                Array.Empty<TrainingAttemptRecord>(),
                Array.Empty<object>(),
                documents,
                _stateRootPath,
                cancellationToken
            );

            await _gate.WaitAsync(cancellationToken);
            try
            {
                _latestMLAnalytics = pipelineResult.Analytics;
                _latestMLEmbeddings = pipelineResult.Embeddings;
                _lastMLArtifactExportPath = pipelineResult.ExportPath;
                _lastMLRunAt = DateTimeOffset.Now;
            }
            finally
            {
                _gate.Release();
            }

            return new
            {
                analytics = pipelineResult.Analytics,
                forecast = pipelineResult.Forecast,
                embeddings = pipelineResult.Embeddings,
                artifacts = pipelineResult.Artifacts,
                exportPath = pipelineResult.ExportPath,
            };
        }
        finally
        {
            _mlGate.Release();
        }
    }

    public async Task<SuiteMLArtifactBundle> ExportSuiteArtifactsAsync(CancellationToken cancellationToken = default)
    {
        MLAnalyticsResult analytics;
        MLEmbeddingsResult embeddings;
        MLForecastResult forecast;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            analytics = _latestMLAnalytics ?? new MLAnalyticsResult { Ok = false, Engine = "not-run" };
            embeddings = _latestMLEmbeddings ?? new MLEmbeddingsResult { Ok = false, Engine = "not-run" };
            forecast = new MLForecastResult { Ok = false, Engine = "not-run" };
        }
        finally
        {
            _gate.Release();
        }

        await _mlGate.WaitAsync(cancellationToken);
        try
        {
            var artifacts = await _mlAnalyticsService.GenerateSuiteArtifactsAsync(
                analytics, embeddings, forecast, cancellationToken);

            var exportPath = await _mlAnalyticsService.ExportArtifactsAsync(
                artifacts, _stateRootPath, cancellationToken);

            await _gate.WaitAsync(cancellationToken);
            try
            {
                _lastMLArtifactExportPath = exportPath;
            }
            finally
            {
                _gate.Release();
            }

            return artifacts;
        }
        finally
        {
            _mlGate.Release();
        }
    }

    // --- Knowledge (delegated) ---

    public async Task<KnowledgeIndexResult> RunKnowledgeIndexAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LearningDocument> documents;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            documents = _learningLibrary.Documents;
        }
        finally
        {
            _gate.Release();
        }

        return await _knowledgeCoordinator.RunKnowledgeIndexAsync(documents, cancellationToken);
    }

    public async Task<KnowledgeIndexStatus> GetKnowledgeIndexStatusAsync(CancellationToken cancellationToken = default)
    {
        int totalDocuments;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            totalDocuments = _learningLibrary.Documents.Count;
        }
        finally
        {
            _gate.Release();
        }

        return await _knowledgeCoordinator.GetKnowledgeIndexStatusAsync(totalDocuments, cancellationToken);
    }

    public async Task<FoundryLibraryImportResult> ImportLibraryFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var importedPaths = new List<string>();
        var skippedPaths = new List<string>();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    skippedPaths.Add(path);
                    continue;
                }

                try
                {
                    var library = await _knowledgeImportService.LoadAsync(path, cancellationToken: cancellationToken);
                    if (library.Documents.Count > 0)
                    {
                        importedPaths.Add(path);
                    }
                    else
                    {
                        skippedPaths.Add(path);
                    }
                }
                catch
                {
                    skippedPaths.Add(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new FoundryLibraryImportResult
        {
            ImportedCount = importedPaths.Count,
            ImportedPaths = importedPaths,
            SkippedPaths = skippedPaths,
        };
    }

    // --- Daily Workflow ---

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

        // Step 2: Run ML pipeline
        try
        {
            await RunFullMLPipelineAsync(cancellationToken);
            stepResults.Add(new DailyRunStepResult { Step = "MLPipeline", Success = true });
        }
        catch (Exception ex)
        {
            stepResults.Add(new DailyRunStepResult { Step = "MLPipeline", Success = false, Error = ex.Message });
        }

        // Step 3: Export Suite artifacts
        try
        {
            await ExportSuiteArtifactsAsync(cancellationToken);
            stepResults.Add(new DailyRunStepResult { Step = "ExportArtifacts", Success = true });
        }
        catch (Exception ex)
        {
            stepResults.Add(new DailyRunStepResult { Step = "ExportArtifacts", Success = false, Error = ex.Message });
        }

        // Step 4: Knowledge indexing
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

    // --- Initialization ---

    private async Task EnsureInitializedLockedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await RefreshContextLockedAsync(cancellationToken);
        _initialized = true;
    }

    private async Task RefreshContextLockedAsync(CancellationToken cancellationToken)
    {
        _installedModelCache = await LoadInstalledModelsSafeAsync(cancellationToken);
        _learningLibrary = await LoadLearningLibrarySafeAsync(cancellationToken);
        _lastRefreshAt = DateTimeOffset.Now;
    }

    private async Task<IReadOnlyList<string>> LoadInstalledModelsSafeAsync(CancellationToken cancellationToken)
    {
        return await RunWithTimeoutFallbackAsync(
            () => _modelProvider.GetInstalledModelsAsync(cancellationToken),
            InstalledModelsLoadTimeout,
            Array.Empty<string>());
    }

    private async Task<LearningLibrary> LoadLearningLibrarySafeAsync(CancellationToken cancellationToken)
    {
        return await RunWithTimeoutFallbackAsync(
            () => _knowledgeImportService.LoadAsync(_knowledgeLibraryPath, _additionalKnowledgePaths),
            LearningLibraryLoadTimeout,
            new LearningLibrary());
    }

    private static async Task<T> RunWithTimeoutFallbackAsync<T>(
        Func<Task<T>> action,
        TimeSpan timeout,
        T fallback)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await action();
        }
        catch
        {
            return fallback;
        }
    }

    private static string ResolveFoundryRootPath(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            if (dir.GetDirectories("Foundry").Length > 0 || dir.GetFiles("Foundry.sln").Length > 0)
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return startDirectory;
    }
}

/// <summary>
/// Result of a knowledge indexing run.
/// </summary>
public sealed class KnowledgeIndexResult
{
    public int TotalDocuments { get; set; }
    public int Indexed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
}

/// <summary>
/// Status snapshot of the knowledge index.
/// </summary>
public sealed class KnowledgeIndexStatus
{
    public int TotalDocuments { get; set; }
    public int IndexedDocuments { get; set; }
    public ulong VectorStorePoints { get; set; }
    public string VectorStoreStatus { get; set; } = string.Empty;
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
