using System.IO;
using System.Text;
using DailyDesk.Models;
using DailyDesk.Services.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DailyDesk.Services;

public sealed class OfficeBrokerOrchestrator
{
    private static readonly TimeSpan InstalledModelsLoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SuiteSnapshotLoadTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TrainingHistoryLoadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LearningLibraryLoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OperatorMemoryLoadTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _mlGate = new(1, 1);
    private readonly OfficeBrokerRuntimeMetadata _brokerMetadata;
    private readonly DailySettings _settings;
    private readonly string _officeRootPath;
    private readonly string _knowledgeLibraryPath;
    private readonly string _stateRootPath;
    private readonly IReadOnlyList<string> _additionalKnowledgePaths;

    private readonly IModelProvider _modelProvider;
    private readonly SuiteSnapshotService _suiteSnapshotService;
    private readonly KnowledgeImportService _knowledgeImportService;
    private readonly LiveResearchService _liveResearchService;
    private readonly OperatorMemoryStore _operatorMemoryStore;
    private readonly OfficeSessionStateStore _sessionStore;
    private readonly MLAnalyticsService _mlAnalyticsService;
    private readonly MLPipelineCoordinator _mlPipelineCoordinator;
    private readonly OfficeDatabase _officeDatabase;
    private readonly OfficeJobStore _jobStore;
    private readonly MLResultStore _mlResultStore;
    private readonly ProcessRunner _processRunner;
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private readonly KnowledgeIndexStore _knowledgeIndexStore;

    // Phase 8: Scheduled automation & workflow orchestration
    private readonly JobSchedulerStore _schedulerStore;
    private readonly WorkflowStore _workflowStore;

    // Phase 6: Semantic Kernel agent orchestration
    private readonly OfficeKernelFactory _kernelFactory;
    private readonly Dictionary<string, DeskAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.Now;
    private IReadOnlyList<string> _installedModelCache = Array.Empty<string>();
    private SuiteSnapshot _suiteSnapshot = new();
    private TrainingHistorySummary _trainingHistorySummary = new();
    private LearningLibrary _learningLibrary = new();
    private LearningProfile _learningProfile = new();
    private OperatorMemoryState _operatorMemoryState = new();
    private OfficeLiveSessionState _session = new();
    private MLAnalyticsResult? _latestMLAnalytics;
    private MLEmbeddingsResult? _latestMLEmbeddings;
    private string? _lastMLArtifactExportPath;
    private DateTimeOffset? _lastMLRunAt;

    public OfficeBrokerOrchestrator(OfficeBrokerRuntimeMetadata brokerMetadata, ILoggerFactory? loggerFactory = null)
    {
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        _brokerMetadata = brokerMetadata;
        _officeRootPath = ResolveOfficeRootPath(AppContext.BaseDirectory);
        var settingsRoot = Path.Combine(_officeRootPath, "DailyDesk");
        _settings = DailySettings.Load(settingsRoot);
        _knowledgeLibraryPath = _settings.ResolveKnowledgeLibraryPath(settingsRoot);
        _stateRootPath = _settings.ResolveStateRootPath(settingsRoot);
        Directory.CreateDirectory(_knowledgeLibraryPath);
        Directory.CreateDirectory(_stateRootPath);
        _additionalKnowledgePaths = _settings.ResolveAdditionalKnowledgePaths();

        // Resilience pipelines
        var ollamaPipeline = OfficeResiliencePipelines.BuildOllamaPipeline();
        var webResearchPipeline = OfficeResiliencePipelines.BuildWebResearchPipeline();
        var pythonPipeline = OfficeResiliencePipelines.BuildPythonSubprocessPipeline();

        // LiteDB persistence
        _officeDatabase = new OfficeDatabase(_stateRootPath);
        _jobStore = new OfficeJobStore(_officeDatabase);
        _mlResultStore = new MLResultStore(_officeDatabase, lf.CreateLogger<MLResultStore>());

        _processRunner = new ProcessRunner(lf.CreateLogger<ProcessRunner>());
        _modelProvider = new OllamaService(_settings.OllamaEndpoint, _processRunner, ollamaPipeline, lf.CreateLogger<OllamaService>());
        _suiteSnapshotService = new SuiteSnapshotService(
            _processRunner,
            _settings.SuiteRuntimeStatusEndpoint
        );
        _knowledgeImportService = new KnowledgeImportService(
            _processRunner,
            Path.Combine(_officeRootPath, "DailyDesk", "Scripts", "extract_document_text.py")
        );
        _liveResearchService = new LiveResearchService(_modelProvider, webResearchPipeline);
        _operatorMemoryStore = new OperatorMemoryStore(_stateRootPath, _officeDatabase, lf.CreateLogger<OperatorMemoryStore>());
        _sessionStore = new OfficeSessionStateStore(_stateRootPath, _officeDatabase);
        _mlAnalyticsService = new MLAnalyticsService(
            _processRunner,
            Path.Combine(_officeRootPath, "DailyDesk", "Scripts"),
            new OnnxMLEngine(Path.Combine(_officeRootPath, "DailyDesk", "Models", "onnx")),
            resiliencePipeline: pythonPipeline,
            logger: lf.CreateLogger<MLAnalyticsService>()
        );
        _mlPipelineCoordinator = new MLPipelineCoordinator(_mlAnalyticsService, _mlResultStore);

        // Phase 5: Semantic search services
        var ollamaUri = new Uri(_settings.OllamaEndpoint.EndsWith("/") ? _settings.OllamaEndpoint : $"{_settings.OllamaEndpoint}/");
        var embeddingHttpClient = new System.Net.Http.HttpClient { BaseAddress = ollamaUri, Timeout = TimeSpan.FromSeconds(90) };
        var ollamaEmbedClient = new OllamaSharp.OllamaApiClient(embeddingHttpClient);
        _embeddingService = new EmbeddingService(ollamaEmbedClient, resiliencePipeline: ollamaPipeline, logger: lf.CreateLogger<EmbeddingService>());
        _vectorStoreService = new VectorStoreService(logger: lf.CreateLogger<VectorStoreService>());
        _knowledgeIndexStore = new KnowledgeIndexStore(_officeDatabase, lf.CreateLogger<KnowledgeIndexStore>());

        // Phase 8: Scheduled automation & workflow orchestration
        _schedulerStore = new JobSchedulerStore(_officeDatabase);
        _workflowStore = new WorkflowStore(_officeDatabase);

        // Phase 6: Semantic Kernel agent orchestration
        _kernelFactory = new OfficeKernelFactory(_settings.OllamaEndpoint, lf);
        InitializeAgents(lf);
    }

    /// <summary>
    /// Creates desk-specific SK agents and registers them by route.
    /// </summary>
    private void InitializeAgents(ILoggerFactory lf)
    {
        var agentList = new DeskAgent[]
        {
            new ChiefOfStaffAgent(
                _kernelFactory.CreateKernel(_settings.ChiefModel),
                lf.CreateLogger<ChiefOfStaffAgent>()),
            new EngineeringDeskAgent(
                _kernelFactory.CreateKernel(_settings.MentorModel),
                lf.CreateLogger<EngineeringDeskAgent>()),
            new SuiteContextAgent(
                _kernelFactory.CreateKernel(_settings.RepoModel),
                lf.CreateLogger<SuiteContextAgent>()),
            new GrowthOpsAgent(
                _kernelFactory.CreateKernel(_settings.BusinessModel),
                lf.CreateLogger<GrowthOpsAgent>()),
            new MLEngineerAgent(
                _kernelFactory.CreateKernel(_settings.MLModel),
                lf.CreateLogger<MLEngineerAgent>()),
        };

        foreach (var agent in agentList)
        {
            _agents[agent.RouteId] = agent;
        }
    }

    /// <summary>
    /// Provides access to the job store for the background worker and job endpoints.
    /// </summary>
    public OfficeJobStore JobStore => _jobStore;

    /// <summary>
    /// Configured retention period (in days) for completed jobs.
    /// </summary>
    public int JobRetentionDays => _settings.JobRetentionDays;

    /// <summary>
    /// Provides access to the embedding service for the job worker.
    /// </summary>
    public EmbeddingService EmbeddingService => _embeddingService;

    /// <summary>
    /// Provides access to the vector store for the job worker and search.
    /// </summary>
    public VectorStoreService VectorStoreService => _vectorStoreService;

    /// <summary>
    /// Provides access to the knowledge index tracking store.
    /// </summary>
    public KnowledgeIndexStore KnowledgeIndexStore => _knowledgeIndexStore;

    /// <summary>
    /// Provides access to the job scheduler store for schedule CRUD and worker operations.
    /// </summary>
    public JobSchedulerStore SchedulerStore => _schedulerStore;

    /// <summary>
    /// Provides access to the workflow template store for workflow CRUD operations.
    /// </summary>
    public WorkflowStore WorkflowStore => _workflowStore;

    /// <summary>
    /// Returns a detailed health status for each subsystem.
    /// </summary>
    public async Task<OfficeHealthReport> GetDetailedHealthAsync(CancellationToken cancellationToken = default)
    {
        var report = new OfficeHealthReport();

        // Ollama
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

        // Python
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

        // LiteDB
        try
        {
            // A simple query to confirm the DB is accessible
            _officeDatabase.Jobs.Count();
            report.LiteDB = new SubsystemHealth { Status = HealthStatus.Ok };
        }
        catch (Exception ex)
        {
            report.LiteDB = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        // Job worker — check for stuck jobs as a signal of worker health
        try
        {
            var runningJobs = _jobStore.ListByStatus(OfficeJobStatus.Running);
            var stuckCount = runningJobs.Count(j =>
                j.StartedAt.HasValue && (DateTimeOffset.Now - j.StartedAt.Value).TotalMinutes > 10);

            if (stuckCount > 0)
            {
                report.JobWorker = new SubsystemHealth
                {
                    Status = HealthStatus.Degraded,
                    Detail = $"{stuckCount} job(s) running for more than 10 minutes."
                };
            }
            else
            {
                report.JobWorker = new SubsystemHealth
                {
                    Status = HealthStatus.Ok,
                    Detail = $"{runningJobs.Count} job(s) currently running."
                };
            }
        }
        catch (Exception ex)
        {
            report.JobWorker = new SubsystemHealth { Status = HealthStatus.Unavailable, Detail = ex.Message };
        }

        // Compute overall status
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
                routes = OfficeRouteCatalog.KnownRoutes,
                refreshedAt = _lastRefreshAt,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfficeBrokerState> GetStateAsync(CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<OfficeChatThread>> GetChatThreadsAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return BuildChatThreadsLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SetChatRouteAsync(
        string? route,
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            _session.CurrentRoute = OfficeRouteCatalog.NormalizeRoute(route);
            await SaveSessionLockedAsync(cancellationToken);
            return _session.CurrentRoute;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeskMessageRecord> SendChatAsync(
        string prompt,
        string? routeOverride = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            var route = OfficeRouteCatalog.NormalizeRoute(routeOverride ?? _session.CurrentRoute);
            _session.CurrentRoute = route;
            var routeTitle = OfficeRouteCatalog.ResolveRouteTitle(route);
            var routeModel = ResolveDeskModel(route);
            var userPrompt = prompt.Trim();
            var userMessage = new DeskMessageRecord
            {
                DeskId = route,
                Role = "user",
                Author = "You",
                Kind = "chat",
                Content = userPrompt,
                CreatedAt = DateTimeOffset.Now,
            };
            await AppendDeskMessagesLockedAsync(route, userMessage, cancellationToken);

            string response;
            var deterministicResponse = TryBuildDeterministicDeskResponseLocked(route, userPrompt);
            if (!string.IsNullOrWhiteSpace(deterministicResponse))
            {
                response = deterministicResponse;
            }
            else
            {
                try
                {
                    // Phase 6: Try SK agent dispatch first, fall back to direct model call
                    response = await TryAgentChatLockedAsync(route, userPrompt, cancellationToken)
                        ?? await _modelProvider.GenerateAsync(
                            routeModel,
                            BuildDeskSystemPrompt(route),
                            BuildDeskConversationPromptLocked(route, userPrompt),
                            cancellationToken
                        );
                    if (string.IsNullOrWhiteSpace(response))
                    {
                        response = BuildDeskFallbackResponse(route, userPrompt);
                    }
                }
                catch
                {
                    response = BuildDeskFallbackResponse(route, userPrompt);
                }
            }

            var assistantMessage = new DeskMessageRecord
            {
                DeskId = route,
                Role = "assistant",
                Author = routeTitle,
                Kind = "chat",
                Content = response.Trim(),
                CreatedAt = DateTimeOffset.Now,
            };
            await AppendDeskMessagesLockedAsync(route, assistantMessage, cancellationToken);
            await RecordActivityLockedAsync(
                "desk_chat",
                routeTitle,
                route,
                Truncate(assistantMessage.Content, 220),
                cancellationToken
            );
            await SaveSessionLockedAsync(cancellationToken);
            return assistantMessage;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResearchReport> RunResearchAsync(
        string query,
        string? perspective,
        bool saveToLibrary,
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return await RunResearchCoreLockedAsync(
                query,
                perspective,
                saveToLibrary,
                cancellationToken
            );
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResearchReport> RunWatchlistAsync(
        string watchlistId,
        bool? saveToLibraryOverride = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(watchlistId))
        {
            throw new ArgumentException("Watchlist id is required.", nameof(watchlistId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            var watchlist = _operatorMemoryState.Watchlists.FirstOrDefault(item =>
                item.Id.Equals(watchlistId, StringComparison.OrdinalIgnoreCase)
            );
            if (watchlist is null)
            {
                throw new InvalidOperationException($"Watchlist '{watchlistId}' was not found.");
            }

            if (!watchlist.IsEnabled)
            {
                throw new InvalidOperationException(
                    $"Watchlist '{watchlist.Topic}' is disabled and cannot be run."
                );
            }

            var report = await RunResearchCoreLockedAsync(
                watchlist.Query,
                watchlist.PreferredPerspective,
                saveToLibraryOverride ?? watchlist.SaveToKnowledgeDefault,
                cancellationToken
            );

            var updatedWatchlists = _operatorMemoryState.Watchlists
                .Select(item => new ResearchWatchlist
                {
                    Id = item.Id,
                    Topic = item.Topic,
                    Query = item.Query,
                    Frequency = item.Frequency,
                    PreferredPerspective = item.PreferredPerspective,
                    SaveToKnowledgeDefault = item.SaveToKnowledgeDefault,
                    IsEnabled = item.IsEnabled,
                    LastRunAt = item.Id.Equals(watchlist.Id, StringComparison.OrdinalIgnoreCase)
                        ? report.GeneratedAt
                        : item.LastRunAt,
                })
                .ToList();

            _operatorMemoryState = await _operatorMemoryStore.SaveWatchlistsAsync(
                updatedWatchlists,
                cancellationToken
            );

            await RecordActivityLockedAsync(
                "watchlist_run",
                watchlist.PreferredPerspective,
                watchlist.Topic,
                report.RunSummary,
                cancellationToken
            );

            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SaveLatestResearchAsync(
        string? notes = null,
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            var report = _session.LatestResearchReport;
            if (report is null || report.Sources.Count == 0)
            {
                throw new InvalidOperationException("No live research report is available to save.");
            }

            var filePath = await PersistResearchMarkdownLockedAsync(
                report,
                notes,
                reloadKnowledge: true,
                cancellationToken
            );
            await RecordActivityLockedAsync(
                "research_saved",
                report.Perspective,
                report.Query,
                string.IsNullOrWhiteSpace(notes) ? filePath : $"{filePath} | notes captured",
                cancellationToken
            );
            return filePath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ResearchReport> RunResearchCoreLockedAsync(
        string query,
        string? perspective,
        bool saveToLibrary,
        CancellationToken cancellationToken
    )
    {
        var resolvedPerspective = string.IsNullOrWhiteSpace(perspective)
            ? OfficeRouteCatalog.ResolvePerspective(_session.CurrentRoute)
            : perspective.Trim();
        var model = ResolveResearchModel(resolvedPerspective);
        var resolvedQuery = string.IsNullOrWhiteSpace(query)
            ? "electrical drawing QA workflows review gates automation"
            : query.Trim();

        var report = await _liveResearchService.RunAsync(
            resolvedQuery,
            resolvedPerspective,
            model,
            _suiteSnapshot,
            _trainingHistorySummary,
            _learningProfile,
            _learningLibrary,
            cancellationToken
        );

        _session.LatestResearchReport = report;
        await SaveSessionLockedAsync(cancellationToken);

        if (saveToLibrary)
        {
            await PersistResearchMarkdownLockedAsync(
                report,
                notes: null,
                reloadKnowledge: true,
                cancellationToken
            );
        }

        var suggestions = BuildResearchSuggestions(
            report,
            resolvedPerspective,
            ResolvePolicyRequiresApproval(resolvedPerspective)
        );
        if (suggestions.Count > 0)
        {
            _operatorMemoryState = await _operatorMemoryStore.UpsertSuggestionsAsync(
                suggestions,
                cancellationToken
            );
            _operatorMemoryState = await AutoStageSelfServeSuggestionsLockedAsync(
                suggestions,
                cancellationToken
            );
        }

        await RecordActivityLockedAsync(
            "research_run",
            resolvedPerspective,
            report.Query,
            report.RunSummary,
            cancellationToken
        );

        return report;
    }

    public async Task<OfficeInboxSection> GetInboxAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            return BuildInboxSectionLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SuggestedAction> ResolveSuggestionAsync(
        string suggestionId,
        string status,
        string? reason,
        string? note,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            throw new ArgumentException("Suggestion id is required.", nameof(suggestionId));
        }

        var normalizedStatus = string.IsNullOrWhiteSpace(status)
            ? "deferred"
            : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not ("accepted" or "deferred" or "rejected"))
        {
            throw new ArgumentException("Status must be accepted, deferred, or rejected.", nameof(status));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            var suggestion = ResolveSuggestionByIdLocked(suggestionId);
            if (suggestion is null)
            {
                throw new InvalidOperationException($"Suggestion '{suggestionId}' was not found.");
            }

            var outcomeReason = string.IsNullOrWhiteSpace(reason)
                ? normalizedStatus switch
                {
                    "accepted" => "Accepted from inbox.",
                    "rejected" => "Rejected from inbox.",
                    _ => "Deferred from inbox.",
                }
                : reason.Trim();

            if (suggestion.RequiresApproval && string.IsNullOrWhiteSpace(outcomeReason))
            {
                throw new InvalidOperationException(
                    "A short reason is required for approval-gated suggestions."
                );
            }

            var outcome = new SuggestionOutcome
            {
                Status = normalizedStatus,
                Reason = outcomeReason,
                OutcomeNote = string.IsNullOrWhiteSpace(note) ? string.Empty : note.Trim(),
                RecordedAt = DateTimeOffset.Now,
            };
            _operatorMemoryState = await _operatorMemoryStore.RecordSuggestionOutcomeAsync(
                suggestion.Id,
                outcome,
                cancellationToken
            );
            await RecordActivityLockedAsync(
                $"suggestion_{normalizedStatus}",
                suggestion.SourceAgent,
                suggestion.Title,
                outcome.DisplaySummary,
                cancellationToken
            );

            var updated = ResolveSuggestionByIdLocked(suggestion.Id);
            return updated ?? suggestion;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SuggestedAction> QueueSuggestionAsync(
        string suggestionId,
        bool approveFirst,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(suggestionId))
        {
            throw new ArgumentException("Suggestion id is required.", nameof(suggestionId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            var suggestion = ResolveSuggestionByIdLocked(suggestionId);
            if (suggestion is null)
            {
                throw new InvalidOperationException($"Suggestion '{suggestionId}' was not found.");
            }

            if (suggestion.RequiresApproval && suggestion.IsPending && !approveFirst)
            {
                throw new InvalidOperationException(
                    "Approve this suggestion before queueing it, or set approveFirst=true."
                );
            }

            if (suggestion.RequiresApproval && suggestion.IsPending)
            {
                var acceptedOutcome = new SuggestionOutcome
                {
                    Status = "accepted",
                    Reason = "Approved and queued from inbox.",
                    OutcomeNote = string.Empty,
                    RecordedAt = DateTimeOffset.Now,
                };
                _operatorMemoryState = await _operatorMemoryStore.RecordSuggestionOutcomeAsync(
                    suggestion.Id,
                    acceptedOutcome,
                    cancellationToken
                );
                await RecordActivityLockedAsync(
                    "suggestion_accepted",
                    suggestion.SourceAgent,
                    suggestion.Title,
                    acceptedOutcome.DisplaySummary,
                    cancellationToken
                );
            }
            else if (!suggestion.RequiresApproval && suggestion.IsPending)
            {
                var selfServeOutcome = new SuggestionOutcome
                {
                    Status = "accepted",
                    Reason = "Queued from suggestions.",
                    OutcomeNote = string.Empty,
                    RecordedAt = DateTimeOffset.Now,
                };
                _operatorMemoryState = await _operatorMemoryStore.RecordSuggestionOutcomeAsync(
                    suggestion.Id,
                    selfServeOutcome,
                    cancellationToken
                );
            }

            _operatorMemoryState = await _operatorMemoryStore.UpdateSuggestionExecutionAsync(
                suggestion.Id,
                "queued",
                suggestion.ActionType.Equals("research_followup", StringComparison.OrdinalIgnoreCase)
                    ? "Running research follow-through."
                    : "Queued for follow-through.",
                cancellationToken
            );
            await RecordActivityLockedAsync(
                "suggestion_queued",
                suggestion.SourceAgent,
                suggestion.Title,
                suggestion.ActionType.Equals("research_followup", StringComparison.OrdinalIgnoreCase)
                    ? "Running research follow-through."
                    : "Queued for follow-through.",
                cancellationToken
            );

            var updated = ResolveSuggestionByIdLocked(suggestion.Id);
            if (updated is not null &&
                updated.ActionType.Equals("research_followup", StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteResearchFollowThroughLockedAsync(updated, cancellationToken);
            }

            return updated ?? suggestion;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfficeLibraryImportResult> ImportLibraryFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            var importedPaths = new List<string>();
            var skippedPaths = new List<string>();
            var targetDirectory = Path.Combine(_knowledgeLibraryPath, "Class Notes");
            Directory.CreateDirectory(targetDirectory);

            foreach (var sourcePath in paths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    skippedPaths.Add(sourcePath);
                    continue;
                }

                var fileName = Path.GetFileName(sourcePath);
                var targetPath = GetUniqueKnowledgeImportPath(targetDirectory, fileName);
                File.Copy(sourcePath, targetPath, overwrite: false);
                importedPaths.Add(targetPath);
            }

            _learningLibrary = await _knowledgeImportService.LoadAsync(
                _knowledgeLibraryPath,
                _additionalKnowledgePaths,
                cancellationToken
            );
            _learningProfile = new LearningProfile();
            await RecordActivityLockedAsync(
                "library_import",
                "Chief of Staff",
                "knowledge library",
                $"{importedPaths.Count} file(s) imported.",
                cancellationToken
            );

            return new OfficeLibraryImportResult
            {
                ImportedCount = importedPaths.Count,
                ImportedPaths = importedPaths,
                SkippedPaths = skippedPaths,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfficeBrokerState> ResetLocalHistoryAsync(
        bool clearTrainingHistory,
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            _operatorMemoryState = await _operatorMemoryStore.ResetAsync(cancellationToken);
            _session = await _sessionStore.ResetAsync(cancellationToken);
            _trainingHistorySummary = clearTrainingHistory
                ? await Task.FromResult(new TrainingHistorySummary())
                : await Task.FromResult(new TrainingHistorySummary());
            _learningProfile = new LearningProfile();
            _lastRefreshAt = DateTimeOffset.Now;

            return BuildStateLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OfficeBrokerState> ResetWorkspaceAsync(
        CancellationToken cancellationToken = default
    )
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);

            ResetKnowledgeLibraryRoot();
            _operatorMemoryState = await _operatorMemoryStore.ResetAsync(cancellationToken);
            _session = await _sessionStore.ResetAsync(cancellationToken);
            _trainingHistorySummary = new TrainingHistorySummary();
            _learningLibrary = await _knowledgeImportService.LoadAsync(
                _knowledgeLibraryPath,
                _additionalKnowledgePaths,
                cancellationToken
            );
            _learningProfile = new LearningProfile();
            _lastRefreshAt = DateTimeOffset.Now;

            return BuildStateLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedLockedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await RefreshContextLockedAsync(cancellationToken);
        _initialized = true;
    }

    private async Task RefreshContextLockedAsync(CancellationToken cancellationToken)
    {
        var installedModelsTask = LoadInstalledModelsSafeAsync(cancellationToken);
        var suiteSnapshotTask = LoadSuiteSnapshotSafeAsync(cancellationToken);
        var historySummaryTask = LoadTrainingHistorySummarySafeAsync(cancellationToken);
        var learningLibraryTask = LoadLearningLibrarySafeAsync(cancellationToken);
        var operatorMemoryTask = LoadOperatorMemoryStateSafeAsync(cancellationToken);
        var sessionTask = _sessionStore.LoadAsync(cancellationToken);

        await Task.WhenAll(
            installedModelsTask,
            suiteSnapshotTask,
            historySummaryTask,
            learningLibraryTask,
            operatorMemoryTask,
            sessionTask
        );

        _installedModelCache = await installedModelsTask;
        _suiteSnapshot = await suiteSnapshotTask;
        _trainingHistorySummary = await historySummaryTask;
        _learningLibrary = await learningLibraryTask;
        _operatorMemoryState = await operatorMemoryTask;
        _session = await sessionTask;
        _learningProfile = new LearningProfile();
        NormalizeSessionLocked();
        if (NormalizeHistoricalStateLocked())
        {
            _operatorMemoryState = await _operatorMemoryStore.SaveSnapshotAsync(
                _operatorMemoryState,
                cancellationToken
            );
        }
        _lastRefreshAt = DateTimeOffset.Now;

        // Restore persisted ML results so export-artifacts works after restart
        _latestMLAnalytics ??= _mlResultStore.LoadAnalytics();
        _latestMLEmbeddings ??= _mlResultStore.LoadEmbeddings();
        _lastMLRunAt ??= _mlResultStore.LoadLastRunTimestamp();
    }

    private async Task<IReadOnlyList<string>> LoadInstalledModelsSafeAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunWithTimeoutFallbackAsync(
            token => _modelProvider.GetInstalledModelsAsync(token),
            InstalledModelsLoadTimeout,
            static () => Array.Empty<string>(),
            cancellationToken
        );
    }

    private async Task<SuiteSnapshot> LoadSuiteSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        return await RunWithTimeoutFallbackAsync(
            token => _suiteSnapshotService.LoadAsync(_settings.SuiteRepoPath, token),
            SuiteSnapshotLoadTimeout,
            BuildSuiteSnapshotTimeoutFallback,
            cancellationToken
        );
    }

    private async Task<TrainingHistorySummary> LoadTrainingHistorySummarySafeAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunWithTimeoutFallbackAsync(
            token => Task.FromResult(new TrainingHistorySummary()),
            TrainingHistoryLoadTimeout,
            static () => new TrainingHistorySummary(),
            cancellationToken
        );
    }

    private async Task<LearningLibrary> LoadLearningLibrarySafeAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunWithTimeoutFallbackAsync(
            token => _knowledgeImportService.LoadAsync(
                _knowledgeLibraryPath,
                _additionalKnowledgePaths,
                token
            ),
            LearningLibraryLoadTimeout,
            BuildLearningLibraryTimeoutFallback,
            cancellationToken
        );
    }

    private async Task<OperatorMemoryState> LoadOperatorMemoryStateSafeAsync(
        CancellationToken cancellationToken
    )
    {
        return await RunWithTimeoutFallbackAsync(
            token => _operatorMemoryStore.LoadAsync(token),
            OperatorMemoryLoadTimeout,
            static () => new OperatorMemoryState(),
            cancellationToken
        );
    }

    private static async Task<T> RunWithTimeoutFallbackAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        Func<T> fallbackFactory,
        CancellationToken cancellationToken
    )
    {
        using var timeoutScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutScope.CancelAfter(timeout);
        try
        {
            return await operation(timeoutScope.Token);
        }
        catch
        {
            return fallbackFactory();
        }
    }

    private SuiteSnapshot BuildSuiteSnapshotTimeoutFallback()
    {
        return new SuiteSnapshot
        {
            RepoPath = _settings.SuiteRepoPath,
            RepoAvailable = Directory.Exists(_settings.SuiteRepoPath),
            StatusSummary =
                $"Suite awareness timed out after {SuiteSnapshotLoadTimeout.TotalSeconds:0} seconds during Office refresh.",
            RuntimeDoctorSummary =
                "Suite runtime status is currently unavailable from Office because the refresh timed out.",
            RuntimeDoctorLeadDetail =
                "Office kept loading with local context so the desk stays usable. Retry later.",
        };
    }

    private LearningLibrary BuildLearningLibraryTimeoutFallback()
    {
        var sourceRoots = new[] { _knowledgeLibraryPath }
            .Concat(_additionalKnowledgePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToList();

        return new LearningLibrary
        {
            RootPath = _knowledgeLibraryPath,
            Exists = Directory.Exists(_knowledgeLibraryPath),
            SourceRoots = sourceRoots,
        };
    }

    private void NormalizeSessionLocked()
    {
        _session.CurrentRoute = OfficeRouteCatalog.NormalizeRoute(_session.CurrentRoute);
        _session.Focus = string.IsNullOrWhiteSpace(_session.Focus)
            ? "Protection, grounding, standards, drafting safety"
            : _session.Focus.Trim();
        _session.FocusReason = string.IsNullOrWhiteSpace(_session.FocusReason)
            ? "Set a focus manually or start from a review target to begin a guided session."
            : _session.FocusReason.Trim();
        _session.Difficulty = string.IsNullOrWhiteSpace(_session.Difficulty)
            ? "Mixed"
            : _session.Difficulty.Trim();
        _session.QuestionCount = Math.Clamp(_session.QuestionCount, 3, 10);
    }

    private bool NormalizeHistoricalStateLocked()
    {
        var unifiedBaselineModel = ResolveUnifiedBaselineModelLocked();
        if (string.IsNullOrWhiteSpace(unifiedBaselineModel))
        {
            return false;
        }

        return OfficeHistoricalStateNormalizer.NormalizeBaselineAssertions(
            _operatorMemoryState,
            unifiedBaselineModel
        );
    }

    private OfficeBrokerState BuildStateLocked()
    {
        var chatThreads = BuildChatThreadsLocked();
        var currentRoute = OfficeRouteCatalog.NormalizeRoute(_session.CurrentRoute);
        var inbox = BuildInboxSectionLocked();

        return new OfficeBrokerState
        {
            GeneratedAt = DateTimeOffset.Now,
            Broker = new OfficeBrokerStatusSection
            {
                Status = "ok",
                Host = _brokerMetadata.Host,
                Port = _brokerMetadata.Port,
                BaseUrl = _brokerMetadata.BaseUrl,
                LoopbackOnly = _brokerMetadata.LoopbackOnly,
                StartedAt = _brokerMetadata.StartedAt,
                LastRefreshAt = _lastRefreshAt,
            },
            Provider = new OfficeProviderSection
            {
                ActiveProviderId = _modelProvider.ProviderId,
                ActiveProviderLabel = _modelProvider.ProviderLabel,
                PrimaryProviderLabel = _modelProvider.ProviderLabel,
                ConfiguredProviderId = string.IsNullOrWhiteSpace(_settings.PrimaryModelProvider)
                    ? OllamaService.OllamaProviderId
                    : _settings.PrimaryModelProvider,
                Ready = _installedModelCache.Count > 0,
                InstalledModelCount = _installedModelCache.Count,
                InstalledModels = _installedModelCache,
                RoleModels = BuildProviderRoleModelsLocked(),
                EnableHuggingFaceCatalog = _settings.EnableHuggingFaceCatalog,
                HuggingFaceCatalogUrl = _settings.HuggingFaceMcpUrl,
                HuggingFaceTokenEnvVar = _settings.HuggingFaceTokenEnvVar,
            },
            Suite = new OfficeSuiteSection
            {
                Snapshot = _suiteSnapshot,
                Pulse = BuildQuietSuiteContextSummary(_suiteSnapshot),
                TrustSummary = BuildQuietSuiteTrustSummary(_suiteSnapshot),
                SnapshotLoadedAt = _lastRefreshAt,
            },
            Chat = new OfficeChatSection
            {
                CurrentRoute = currentRoute,
                CurrentRouteTitle = OfficeRouteCatalog.ResolveRouteDisplayTitle(currentRoute),
                ActiveThreadId = chatThreads.FirstOrDefault(thread =>
                    thread.Id.Equals(currentRoute, StringComparison.OrdinalIgnoreCase)
                )?.Id ?? chatThreads.FirstOrDefault()?.Id ?? currentRoute,
                RouteReason = BuildRouteReasonLocked(currentRoute),
                RouteOptions = BuildRouteOptionsLocked(),
                SuggestedMoves = BuildSuggestedMovesLocked(),
                SuiteContext = BuildSuiteContextSignalsLocked(),
                Transcript = BuildTranscriptLocked(chatThreads, currentRoute),
                Threads = chatThreads,
            },
            Study = new OfficeStudySection
            {
                Focus = _session.Focus,
                Difficulty = _session.Difficulty,
                QuestionCount = _session.QuestionCount,
                PracticeResultSummary = _session.PracticeResultSummary,
                DefenseScoreSummary = _session.DefenseScoreSummary,
                DefenseFeedbackSummary = _session.DefenseFeedbackSummary,
                ReflectionContextSummary = _session.ReflectionContextSummary,
                LatestReflection = _session.LastReflection?.DisplaySummary
                    ?? _trainingHistorySummary.ReflectionSummary,
                History = _trainingHistorySummary,
            },
            Research = new OfficeResearchSection
            {
                LatestReport = _session.LatestResearchReport,
                Summary = _session.LatestResearchReport?.Summary
                    ?? "Run a live research query to pull current web sources into the desk.",
                RunSummary = _session.LatestResearchReport?.RunSummary ?? "No live research run yet.",
                History = BuildResearchHistoryLocked(),
            },
            Library = new OfficeLibrarySection
            {
                Summary = _learningLibrary.Documents.Count == 0
                    ? "Office library is blank. Import notes, references, or reviewed source material to begin."
                    : _learningLibrary.Summary,
                TotalDocumentCount = _learningLibrary.Documents.Count,
                Roots = BuildLibraryRootsLocked(),
                Documents = BuildLibraryDocumentsLocked(),
                Library = _learningLibrary,
                Profile = _learningProfile,
            },
            Growth = new OfficeGrowthSection
            {
                DailyRun = _operatorMemoryState.LatestDailyRun,
                CareerEngineProgressSummary = BuildCareerEngineProgressSummary(_operatorMemoryState),
                WatchlistSummary = BuildWatchlistSummary(_operatorMemoryState),
                ApprovalInboxSummary = BuildApprovalInboxSummary(_operatorMemoryState),
                SuggestionsSummary = BuildSuggestionsSummary(_operatorMemoryState),
                ProofTracks = BuildProofTracksLocked(),
                FocusAreas = BuildGrowthFocusAreasLocked(),
                Highlights = BuildGrowthHighlightsLocked(),
                ResearchRuns = BuildResearchHistoryLocked(),
                Watchlists = _operatorMemoryState.Watchlists.OrderBy(item => item.NextDueAt).ToList(),
            },
            Inbox = inbox,
            ML = BuildMLSectionLocked(),
        };
    }

    private void ResetKnowledgeLibraryRoot()
    {
        if (string.IsNullOrWhiteSpace(_knowledgeLibraryPath))
        {
            return;
        }

        Directory.CreateDirectory(_knowledgeLibraryPath);
        var rootDirectory = new DirectoryInfo(_knowledgeLibraryPath);
        foreach (var entry in rootDirectory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
                continue;
            }

            entry.Delete();
        }

        Directory.CreateDirectory(Path.Combine(_knowledgeLibraryPath, "Class Notes"));
        Directory.CreateDirectory(Path.Combine(_knowledgeLibraryPath, "Research"));
        Directory.CreateDirectory(Path.Combine(_knowledgeLibraryPath, "Follow Through"));
    }

    private IReadOnlyList<OfficeChatThread> BuildChatThreadsLocked()
    {
        var threads = _operatorMemoryState.DeskThreads
            .Select(thread => new OfficeChatThread
            {
                Id = thread.DeskId,
                Title = thread.DeskTitle,
                DisplayTitle = OfficeRouteCatalog.ResolveRouteDisplayTitle(thread.DeskId),
                UpdatedAt = thread.UpdatedAt,
                Messages = thread.Messages.OrderBy(item => item.CreatedAt).ToList(),
            })
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToList();

        if (threads.Count > 0)
        {
            return threads;
        }

        return OfficeRouteCatalog.KnownRoutes
            .Select(route => new OfficeChatThread
            {
                Id = route,
                Title = OfficeRouteCatalog.ResolveRouteTitle(route),
                DisplayTitle = OfficeRouteCatalog.ResolveRouteDisplayTitle(route),
                UpdatedAt = DateTimeOffset.Now,
                Messages = Array.Empty<DeskMessageRecord>(),
            })
            .ToList();
    }

    private OfficeInboxSection BuildInboxSectionLocked()
    {
        var pending = _operatorMemoryState.PendingApprovalSuggestions
            .OrderBy(GetInboxSortRank)
            .ThenByDescending(item => item.CreatedAt)
            .ToList();
        var open = _operatorMemoryState.OpenSuggestions
            .OrderBy(GetInboxSortRank)
            .ThenByDescending(item => item.CreatedAt)
            .ToList();
        var approved = _operatorMemoryState.ApprovedSuggestions
            .OrderBy(GetInboxSortRank)
            .ThenByDescending(item => item.ExecutionUpdatedAt ?? item.CreatedAt)
            .ToList();
        var queued = _operatorMemoryState.QueuedWorkSuggestions
            .OrderBy(GetInboxSortRank)
            .ThenByDescending(item => item.ExecutionUpdatedAt ?? item.CreatedAt)
            .ToList();
        var recent = _operatorMemoryState.RecentSuggestions.ToList();

        return new OfficeInboxSection
        {
            Summary =
                $"{pending.Count} pending approval | {open.Count} open | {approved.Count} approved | {queued.Count} queued/running/failed.",
            Approvals = pending,
            QueuedReady = open.Concat(approved).Concat(queued).ToList(),
            RecentResults = recent,
            PendingApproval = pending,
            Open = open,
            Approved = approved,
            QueuedWork = queued,
            Recent = recent,
        };
    }

    private IReadOnlyList<OfficeProviderRoleModel> BuildProviderRoleModelsLocked()
    {
        var installed = new HashSet<string>(_installedModelCache, StringComparer.OrdinalIgnoreCase);
        return new List<OfficeProviderRoleModel>
        {
            new() { Role = "Chief", ModelName = _settings.ChiefModel, Installed = installed.Contains(_settings.ChiefModel) },
            new() { Role = "Engineering", ModelName = _settings.MentorModel, Installed = installed.Contains(_settings.MentorModel) },
            new() { Role = "Suite Context", ModelName = _settings.RepoModel, Installed = installed.Contains(_settings.RepoModel) },
            new() { Role = "Growth", ModelName = _settings.BusinessModel, Installed = installed.Contains(_settings.BusinessModel) },
            new() { Role = "Study Builder", ModelName = _settings.TrainingModel, Installed = installed.Contains(_settings.TrainingModel) },
            new() { Role = "ML Engineer", ModelName = _settings.MLModel, Installed = installed.Contains(_settings.MLModel) },
        };
    }

    private IReadOnlyList<OfficeRouteOption> BuildRouteOptionsLocked()
    {
        return OfficeRouteCatalog.KnownRoutes
            .Select(route => new OfficeRouteOption
            {
                Id = route,
                Label = OfficeRouteCatalog.ResolveRouteDisplayTitle(route),
                Title = OfficeRouteCatalog.ResolveRouteTitle(route),
                Perspective = OfficeRouteCatalog.ResolvePerspective(route),
                Summary = BuildThreadIntro(route),
            })
            .ToList();
    }

    private string BuildRouteReasonLocked(string currentRoute)
    {
        if (!string.IsNullOrWhiteSpace(_session.LatestResearchReport?.Perspective))
        {
            return $"Last live research ran through {_session.LatestResearchReport.Perspective}.";
        }

        return currentRoute switch
        {
            OfficeRouteCatalog.EngineeringRoute =>
                $"Current study focus is {_session.Focus}, so Engineering remains the default route.",
            OfficeRouteCatalog.SuiteRoute =>
                "Suite Context is active because runtime trust or repo signals need operator attention.",
            OfficeRouteCatalog.BusinessRoute =>
                "Growth Ops is active to turn current work into proof, career leverage, and disciplined follow-through.",
            _ =>
                "Chief of Staff stays active when no narrower route has taken over.",
        };
    }

    private IReadOnlyList<string> BuildSuggestedMovesLocked()
    {
        var moves = new List<string>();
        if (_operatorMemoryState.PendingApprovalSuggestions.Count > 0)
        {
            moves.Add($"Resolve {_operatorMemoryState.PendingApprovalSuggestions.Count} approval item(s) in the shared inbox.");
        }

        if (!string.IsNullOrWhiteSpace(_session.Focus))
        {
            moves.Add($"Stay on {_session.Focus} until the guided loop is complete.");
        }

        moves.AddRange(
            _session.LatestResearchReport?.ActionMoves?.Take(3)
            ?? Enumerable.Empty<string>());

        moves.AddRange(
            _trainingHistorySummary.ReviewRecommendations
                .Where(item => item.IsDue)
                .Take(2)
                .Select(item => $"Review now: {item.Topic}."));

        return moves
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private IReadOnlyList<string> BuildSuiteContextSignalsLocked()
    {
        var signals = new List<string>
        {
            BuildQuietSuiteContextSummary(_suiteSnapshot),
            BuildQuietSuiteTrustSummary(_suiteSnapshot),
        };

        if (!string.IsNullOrWhiteSpace(_suiteSnapshot.StatusSummary))
        {
            signals.Add(_suiteSnapshot.StatusSummary);
        }

        signals.AddRange(_suiteSnapshot.HotAreas.Take(3).Select(area => $"Hot area: {area}"));

        return signals
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static IReadOnlyList<DeskMessageRecord> BuildTranscriptLocked(
        IReadOnlyList<OfficeChatThread> threads,
        string currentRoute)
    {
        return threads.FirstOrDefault(thread =>
                thread.Id.Equals(currentRoute, StringComparison.OrdinalIgnoreCase)
            )?.Messages
            ?? threads.FirstOrDefault()?.Messages
            ?? Array.Empty<DeskMessageRecord>();
    }

    private IReadOnlyList<OfficeLibraryRoot> BuildLibraryRootsLocked()
    {
        var documentCounts = _learningLibrary.Documents
            .GroupBy(document => document.SourceRootPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return _learningLibrary.SourceRoots
            .Select((root, index) => new OfficeLibraryRoot
            {
                Label = index == 0 ? "Primary root" : $"Additional root {index}",
                Path = root,
                Exists = Directory.Exists(root),
                IsPrimary = index == 0,
                DocumentCount = documentCounts.TryGetValue(root, out var count) ? count : 0,
            })
            .ToList();
    }

    private IReadOnlyList<OfficeLibraryDocument> BuildLibraryDocumentsLocked()
    {
        return _learningLibrary.Documents
            .OrderByDescending(document => document.LastUpdated)
            .Take(12)
            .Select(document => new OfficeLibraryDocument
            {
                Id = document.FullPath,
                Title = string.IsNullOrWhiteSpace(document.FileName) ? document.RelativePath : document.FileName,
                Path = document.FullPath,
                Summary = document.DisplaySummary,
                UpdatedAt = document.LastUpdated,
            })
            .ToList();
    }

    private IReadOnlyList<string> BuildProofTracksLocked()
    {
        return new[]
        {
            BuildCareerEngineProgressSummary(_operatorMemoryState),
            _trainingHistorySummary.OverallSummary,
            _trainingHistorySummary.DefenseSummary,
            _trainingHistorySummary.ReflectionSummary,
        }
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Take(5)
        .ToList();
    }

    private IReadOnlyList<string> BuildGrowthFocusAreasLocked()
    {
        return new[]
        {
            _settings.EngineeringFocus,
            _settings.CadFocus,
            _settings.CareerFocus,
            _settings.BusinessFocus,
        }
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Take(6)
        .ToList();
    }

    private IReadOnlyList<string> BuildGrowthHighlightsLocked()
    {
        var highlights = new List<string>
        {
            BuildWatchlistSummary(_operatorMemoryState),
            BuildApprovalInboxSummary(_operatorMemoryState),
            BuildSuggestionsSummary(_operatorMemoryState),
        };

        if (_operatorMemoryState.LatestDailyRun is { Objective.Length: > 0 } dailyRun)
        {
            highlights.Add($"Daily objective: {dailyRun.Objective}");
        }

        return highlights
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(6)
            .ToList();
    }

    private IReadOnlyList<OfficeResearchRun> BuildResearchHistoryLocked()
    {
        var runs = new List<OfficeResearchRun>();
        if (_session.LatestResearchReport is { } latest)
        {
            runs.Add(new OfficeResearchRun
            {
                Id = $"{latest.GeneratedAt:yyyyMMddHHmmss}-{CreateSlug(latest.Query)}",
                Title = latest.Query,
                Summary = latest.RunSummary,
                UpdatedAt = latest.GeneratedAt,
            });
        }

        runs.AddRange(
            _operatorMemoryState.Activities
                .Where(activity => activity.EventType is "research_run" or "research_saved")
                .OrderByDescending(activity => activity.OccurredAt)
                .Take(5)
                .Select(activity => new OfficeResearchRun
                {
                    Id = $"{activity.EventType}-{activity.OccurredAt:yyyyMMddHHmmss}",
                    Title = string.IsNullOrWhiteSpace(activity.Topic) ? activity.EventType : activity.Topic,
                    Summary = activity.Summary,
                    UpdatedAt = activity.OccurredAt,
                }));

        return runs
            .GroupBy(run => run.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(run => run.UpdatedAt)
            .Take(8)
            .ToList();
    }

    private async Task AppendDeskMessagesLockedAsync(
        string deskId,
        DeskMessageRecord message,
        CancellationToken cancellationToken
    )
    {
        var thread = ResolveDeskThreadLocked(deskId);
        thread.Messages.Add(message);
        thread.UpdatedAt = message.CreatedAt;
        thread.Messages = thread.Messages.OrderBy(item => item.CreatedAt).TakeLast(120).ToList();
        _operatorMemoryState = await _operatorMemoryStore.SaveDeskThreadsAsync(
            _operatorMemoryState.DeskThreads,
            cancellationToken
        );
    }

    private DeskThreadState ResolveDeskThreadLocked(string deskId)
    {
        var route = OfficeRouteCatalog.NormalizeRoute(deskId);
        var thread = _operatorMemoryState.FindDeskThread(route);
        if (thread is not null)
        {
            return thread;
        }

        var createdAt = DateTimeOffset.Now;
        var created = new DeskThreadState
        {
            DeskId = route,
            DeskTitle = OfficeRouteCatalog.ResolveRouteTitle(route),
            UpdatedAt = createdAt,
            Messages =
            [
                new DeskMessageRecord
                {
                    DeskId = route,
                    Role = "assistant",
                    Author = OfficeRouteCatalog.ResolveRouteTitle(route),
                    Kind = "system",
                    Content = BuildThreadIntro(route),
                    CreatedAt = createdAt,
                },
            ],
        };
        _operatorMemoryState.DeskThreads.Add(created);
        return created;
    }

    private static string BuildThreadIntro(string route) =>
        OfficeRouteCatalog.NormalizeRoute(route) switch
        {
            OfficeRouteCatalog.ChiefRoute =>
                "I route the day across Suite, engineering, CAD, and growth. Ask for a brief, a plan, or a synthesis.",
            OfficeRouteCatalog.EngineeringRoute =>
                "I combine EE coaching, CAD workflow judgment, and training prep. Ask for explanations, drills, or review guidance.",
            OfficeRouteCatalog.SuiteRoute =>
                "I keep the office aware of Suite trust, availability, and workflow context in a calm, read-only way.",
            OfficeRouteCatalog.BusinessRoute =>
                "I translate current capability into growth discipline, offers, proof points, and monetization paths without hype.",
            OfficeRouteCatalog.MLRoute =>
                "I analyze your learning data with ML (Scikit-learn, PyTorch, TensorFlow) and produce insights, forecasts, and Suite-ready artifacts.",
            _ => "Ask for the next move.",
        };

    private static string BuildDeskSystemPrompt(string route)
    {
        return OfficeRouteCatalog.NormalizeRoute(route) switch
        {
            OfficeRouteCatalog.ChiefRoute =>
                """
                You are the Chief of Staff inside Office.
                Route the day across Suite, electrical engineering, CAD workflow judgment, and business operations.
                Stay read-only toward Suite.
                Answer the current request directly. Do not recycle old assistant wording when fresher state is provided.
                Respond with short sections named NEXT MOVE, WHY, and HANDOFF.
                """,
            OfficeRouteCatalog.EngineeringRoute =>
                """
                You are the Engineering Desk inside Office.
                Combine electrical engineering teaching, CAD workflow judgment, practice-test coaching, and oral-defense reasoning.
                Keep answers practical, operator-safe, and tied to review-first production work.
                Lead with the governing principle, then give one bounded next move.
                Do not mention internal model/provider details unless the user explicitly asks.
                Do not echo stale thread wording when fresher state is provided.
                Respond with short sections named ANSWER, CHECKS, and CAD OR SUITE LINK.
                """,
            OfficeRouteCatalog.SuiteRoute =>
                """
                You are the Suite Context desk inside Office.
                Keep the office aware of Suite trust, availability, and workflow context without turning into a repo-planning tool.
                Stay read-only and avoid implementation proposals unless explicitly asked.
                Prefer current runtime facts over older thread summaries.
                Respond with short sections named CONTEXT, TRUST, and WHY IT MATTERS.
                """,
            OfficeRouteCatalog.BusinessRoute =>
                """
                You are Business Ops inside Office.
                Turn current capability into internal operating moves, pilot-shaped offers, and monetization proof without hype.
                Keep the focus on personal growth, real electrical production-control value, and career proof.
                Avoid generic startup language.
                Respond with short sections named MOVE, WHY IT WINS, and WHAT TO PROVE.
                """,
            OfficeRouteCatalog.MLRoute =>
                PromptComposer.BuildMLEngineerSystemPrompt(),
            _ =>
                """
                You are a practical assistant inside Office.
                Respond directly and keep the answer tied to action.
                """,
        };
    }

    private string BuildDeskConversationPromptLocked(string route, string userInput)
    {
        var thread = ResolveDeskThreadLocked(route);
        var history = thread.Messages
            .Where(item => !item.Kind.Equals("system", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6)
            .ToList();
        var knowledgeContext = KnowledgePromptContextBuilder.BuildRelevantContext(
            _learningLibrary,
            new[]
            {
                userInput,
                _session.Focus,
                _learningProfile.CurrentNeed,
                _trainingHistorySummary.ReviewQueueSummary,
                _trainingHistorySummary.DefenseSummary,
            },
            maxDocuments: 2,
            maxTotalCharacters: 1800,
            maxExcerptCharacters: 620
        );

        var builder = new StringBuilder();
        builder.AppendLine("Office operating parameters:");
        builder.AppendLine($"- suite: {_settings.SuiteFocus}");
        builder.AppendLine($"- engineering: {_settings.EngineeringFocus}");
        builder.AppendLine($"- cad: {_settings.CadFocus}");
        builder.AppendLine($"- business: {_settings.BusinessFocus}");
        builder.AppendLine($"- career: {_settings.CareerFocus}");
        builder.AppendLine();
        builder.AppendLine("Current Suite context:");
        builder.AppendLine($"- suite awareness: {BuildQuietSuiteContextSummary(_suiteSnapshot)}");
        builder.AppendLine($"- suite trust: {BuildQuietSuiteTrustSummary(_suiteSnapshot)}");
        builder.AppendLine();
        builder.AppendLine("Current engineering and knowledge context:");
        builder.AppendLine($"- learning profile: {_learningProfile.Summary}");
        builder.AppendLine($"- current need: {_learningProfile.CurrentNeed}");
        builder.AppendLine($"- review queue: {_trainingHistorySummary.ReviewQueueSummary}");
        builder.AppendLine($"- defense summary: {_trainingHistorySummary.DefenseSummary}");
        builder.AppendLine(
            $"- imported knowledge: {JoinOrNone(_learningLibrary.Documents.Take(5).Select(item => item.PromptSummary).ToList())}"
        );
        builder.AppendLine("- relevant notebook evidence:");
        builder.AppendLine(knowledgeContext);
        builder.AppendLine();
        builder.AppendLine("Current growth and operator context:");
        builder.AppendLine($"- daily objective: {_operatorMemoryState.LatestDailyRun?.Objective ?? "no daily run yet"}");
        builder.AppendLine($"- approval inbox: {BuildApprovalInboxSummary(_operatorMemoryState)}");
        builder.AppendLine($"- monetization leads: {JoinOrNone(_suiteSnapshot.MonetizationMoves)}");
        builder.AppendLine();
        builder.AppendLine("Current Office provider context:");
        builder.AppendLine($"- active provider: {_modelProvider.ProviderLabel}");
        builder.AppendLine($"- provider ready: {(_installedModelCache.Count > 0 ? "yes" : "no")}");
        builder.AppendLine($"- installed models: {JoinOrNone(_installedModelCache)}");
        builder.AppendLine(
            $"- role models: Chief={_settings.ChiefModel}; Engineering={_settings.MentorModel}; Suite Context={_settings.RepoModel}; Growth={_settings.BusinessModel}; Study Builder={_settings.TrainingModel}"
        );
        builder.AppendLine(
            "- Only mention model/provider details if the user explicitly asks. Use the provider facts above, not older assistant messages or research history, as the source of truth."
        );
        builder.AppendLine();
        builder.AppendLine("Recent desk thread:");
        foreach (var message in history)
        {
            builder.AppendLine($"{message.Author}: {Truncate(message.Content, 420)}");
        }

        builder.AppendLine();
        builder.AppendLine("User request:");
        builder.AppendLine(userInput);
        builder.AppendLine();
        builder.AppendLine(
            "Keep the answer action-oriented, grounded in the selected desk role, and focused on the current request instead of rehashing older wording."
        );
        return builder.ToString();
    }

    /// <summary>
    /// Attempts to dispatch the chat to the SK agent for the given route.
    /// Returns null if no agent is registered or the agent call fails,
    /// allowing the caller to fall back to the original direct model call.
    /// Also handles multi-turn memory: generates a summary of older messages
    /// when the thread exceeds the summary threshold.
    /// </summary>
    private async Task<string?> TryAgentChatLockedAsync(
        string route,
        string userInput,
        CancellationToken cancellationToken)
    {
        if (!_agents.TryGetValue(route, out var agent))
        {
            return null;
        }

        try
        {
            var thread = ResolveDeskThreadLocked(route);
            var contextBlock = BuildDeskConversationPromptLocked(route, userInput);

            var result = await agent.ChatAsync(
                userInput,
                thread.Messages,
                thread.Summary,
                contextBlock,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(result))
            {
                return null; // Agent returned empty — fall back to direct call
            }

            // Phase 6.3: Update thread summary if the conversation is getting long
            await UpdateThreadSummaryLockedAsync(agent, thread, cancellationToken);

            return result;
        }
        catch
        {
            return null; // Agent failed — fall back to direct call
        }
    }

    /// <summary>
    /// Generates and persists a summary of older messages when the thread
    /// exceeds the configured summary threshold.
    /// </summary>
    private async Task UpdateThreadSummaryLockedAsync(
        DeskAgent agent,
        DeskThreadState thread,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await agent.SummarizeOlderMessagesAsync(thread.Messages, cancellationToken);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                thread.Summary = summary;
                _operatorMemoryState = await _operatorMemoryStore.SaveDeskThreadsAsync(
                    _operatorMemoryState.DeskThreads,
                    cancellationToken);
            }
        }
        catch
        {
            // Summary generation is best-effort; don't break the chat flow
        }
    }

    private string BuildDeskFallbackResponse(string route, string userInput)
    {
        return OfficeRouteCatalog.NormalizeRoute(route) switch
        {
            OfficeRouteCatalog.ChiefRoute =>
                $"NEXT MOVE\nRun one bounded block on {_operatorMemoryState.LatestDailyRun?.Objective ?? _learningProfile.CurrentNeed}.\n\nWHY\nThat keeps the day tied to {_suiteSnapshot.StatusSummary} instead of drifting into generic planning.\n\nHANDOFF\nIf this needs current facts, run live research for: {userInput}",
            OfficeRouteCatalog.EngineeringRoute =>
                $"ANSWER\nStart with the governing principle behind {_trainingHistorySummary.ReviewRecommendations.FirstOrDefault()?.Topic ?? _session.Focus}. Explain what can go wrong if that principle is missed.\n\nCHECKS\nName one calculation, one standard or rule check, and one drawing-review step you would use to validate the answer.\n\nCAD OR SUITE LINK\nTie the explanation back to {_suiteSnapshot.HotAreas.FirstOrDefault() ?? "the current Suite hotspot"} and {_settings.CadFocus}.",
            OfficeRouteCatalog.SuiteRoute =>
                $"CONTEXT\n{BuildQuietSuiteContextSummary(_suiteSnapshot)}\n\nTRUST\n{BuildQuietSuiteTrustSummary(_suiteSnapshot)}\n\nWHY IT MATTERS\nUse Suite as background context for better decisions, not as a prompt to start repo work.",
            OfficeRouteCatalog.BusinessRoute =>
                $"MOVE\nTurn the current work into one proof artifact around {_suiteSnapshot.MonetizationMoves.FirstOrDefault() ?? "drawing production control for electrical teams"}.\n\nWHY IT WINS\nIt shows real operator value and career leverage instead of vague AI positioning.\n\nWHAT TO PROVE\nShow the judgment used, the risk removed, and the workflow tightened.",
            _ => "Work from the current context and choose the next bounded move.",
        };
    }

    private string? TryBuildDeterministicDeskResponseLocked(string route, string userInput)
    {
        if (LooksLikeProviderStatusQuestion(userInput))
        {
            return BuildProviderStatusResponseLocked(route);
        }

        return null;
    }

    private string BuildProviderStatusResponseLocked(string route)
    {
        var routeId = OfficeRouteCatalog.NormalizeRoute(route);
        var unifiedModel = ResolveUnifiedBaselineModelLocked();
        var providerReady = _installedModelCache.Count > 0;
        var installedCount = _installedModelCache.Count;
        var providerLabel = _modelProvider.ProviderLabel;
        var installedSummary = installedCount == 0
            ? "none installed yet"
            : string.Join(", ", _installedModelCache);
        var modelSummary = string.IsNullOrWhiteSpace(unifiedModel)
            ? "Office role models are split across multiple configured models."
            : $"All Office roles currently use `{unifiedModel}`.";

        return routeId switch
        {
            OfficeRouteCatalog.ChiefRoute =>
                $"NEXT MOVE\nKeep the baseline simple: {modelSummary}\n\nWHY\nOffice is on {providerLabel} with provider ready = {(providerReady ? "yes" : "no")} and {installedCount} installed model(s): {installedSummary}.\n\nHANDOFF\nIf you want a specialization split later, add a local override layer before changing shared settings.",
            OfficeRouteCatalog.SuiteRoute =>
                $"CONTEXT\n{modelSummary}\n\nTRUST\nOffice is on {providerLabel}. Provider ready = {(providerReady ? "yes" : "no")}. Installed models: {installedSummary}.\n\nWHY IT MATTERS\nThat confirms the local Office provider state only; it does not change Suite runtime trust.",
            OfficeRouteCatalog.BusinessRoute =>
                $"MOVE\nKeep the baseline unified: {modelSummary}\n\nWHY IT WINS\nOne shared local model reduces drift while you tighten Office workflows.\n\nWHAT TO PROVE\nProvider ready = {(providerReady ? "yes" : "no")}; installed models = {installedCount}; provider = {providerLabel}.",
            _ =>
                $"ANSWER\n{modelSummary}\n\nCHECKS\nProvider: {providerLabel}. Provider ready: {(providerReady ? "yes" : "no")}. Installed models: {installedSummary}.\n\nCAD OR SUITE LINK\nThis confirms the Office model rack is online; keep CAD and Suite decisions review-first.",
        };
    }

    private static bool LooksLikeProviderStatusQuestion(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return false;
        }

        return userInput.Contains("which model", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("what model", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("baseline model", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("office roles use", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("providerready", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("provider ready", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("installed model", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("installedmodel", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("what provider", StringComparison.OrdinalIgnoreCase)
            || userInput.Contains("ollama model", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveDeskModel(string route)
    {
        return OfficeRouteCatalog.NormalizeRoute(route) switch
        {
            OfficeRouteCatalog.ChiefRoute => _settings.ChiefModel,
            OfficeRouteCatalog.EngineeringRoute => _settings.MentorModel,
            OfficeRouteCatalog.SuiteRoute => _settings.RepoModel,
            OfficeRouteCatalog.BusinessRoute => _settings.BusinessModel,
            OfficeRouteCatalog.MLRoute => _settings.MLModel,
            _ => _settings.ChiefModel,
        };
    }

    private string ResolveResearchModel(string perspective) =>
        perspective switch
        {
            "Chief of Staff" => _settings.ChiefModel,
            "Repo Coach" => _settings.RepoModel,
            "Business Strategist" => _settings.BusinessModel,
            _ => _settings.MentorModel,
        };

    private string? ResolveUnifiedBaselineModelLocked()
    {
        var configuredModels = new[]
            {
                _settings.ChiefModel,
                _settings.MentorModel,
                _settings.RepoModel,
                _settings.TrainingModel,
                _settings.BusinessModel,
            }
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return configuredModels.Count == 1 ? configuredModels[0] : null;
    }

    private bool ResolvePolicyRequiresApproval(string perspective)
    {
        var policy = _operatorMemoryState.Policies.FirstOrDefault(item =>
            item.Role.Equals(perspective, StringComparison.OrdinalIgnoreCase)
        );
        if (policy is not null)
        {
            return policy.RequiresApproval;
        }

        return perspective is "Repo Coach" or "Business Strategist";
    }

    private SuggestedAction? ResolveSuggestionByIdLocked(string suggestionId)
    {
        return _operatorMemoryState.Suggestions.FirstOrDefault(item =>
            item.Id.Equals(suggestionId, StringComparison.OrdinalIgnoreCase)
        );
    }

    private async Task RecordActivityLockedAsync(
        string eventType,
        string agent,
        string topic,
        string summary,
        CancellationToken cancellationToken
    )
    {
        _operatorMemoryState = await _operatorMemoryStore.RecordActivityAsync(
            new OperatorActivityRecord
            {
                EventType = eventType,
                Agent = agent,
                Topic = topic,
                Summary = Truncate(summary, 220),
                OccurredAt = DateTimeOffset.Now,
            },
            cancellationToken
        );
    }

    private async Task SaveSessionLockedAsync(CancellationToken cancellationToken)
    {
        _session.UpdatedAt = DateTimeOffset.Now;
        await _sessionStore.SaveAsync(_session, cancellationToken);
    }

    private async Task<OperatorMemoryState> AutoStageSelfServeSuggestionsLockedAsync(
        IReadOnlyList<SuggestedAction> suggestions,
        CancellationToken cancellationToken
    )
    {
        var candidate = suggestions
            .Where(item =>
                !item.RequiresApproval
                && item.ActionType.Equals("research_followup", StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(item => item.Priority.Equals("high", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefault();

        if (candidate is null)
        {
            return _operatorMemoryState;
        }

        var acceptedOutcome = new SuggestionOutcome
        {
            Status = "accepted",
            Reason = "Auto-staged from self-serve research.",
            OutcomeNote =
                "Queued automatically because this agent is allowed to prepare low-risk research follow-through.",
            RecordedAt = DateTimeOffset.Now,
        };

        _operatorMemoryState = await _operatorMemoryStore.RecordSuggestionOutcomeAsync(
            candidate.Id,
            acceptedOutcome,
            cancellationToken
        );
        _operatorMemoryState = await _operatorMemoryStore.UpdateSuggestionExecutionAsync(
            candidate.Id,
            "queued",
            "Auto-queued from self-serve research.",
            cancellationToken
        );
        await RecordActivityLockedAsync(
            "suggestion_auto_queued",
            candidate.SourceAgent,
            candidate.Title,
            "Auto-queued from self-serve research.",
            cancellationToken
        );

        var queuedSuggestion = ResolveSuggestionByIdLocked(candidate.Id);
        if (queuedSuggestion is not null)
        {
            await ExecuteResearchFollowThroughLockedAsync(queuedSuggestion, cancellationToken);
        }

        return _operatorMemoryState;
    }

    private async Task<SuggestedAction> ExecuteResearchFollowThroughLockedAsync(
        SuggestedAction suggestion,
        CancellationToken cancellationToken
    )
    {
        _operatorMemoryState = await _operatorMemoryStore.UpdateSuggestionExecutionAsync(
            suggestion.Id,
            "running",
            "Preparing research follow-through brief.",
            cancellationToken
        );
        await RecordActivityLockedAsync(
            "suggestion_running",
            suggestion.SourceAgent,
            suggestion.Title,
            "Preparing research follow-through brief.",
            cancellationToken
        );

        try
        {
            var brief = await PersistResearchFollowThroughBriefLockedAsync(
                suggestion,
                cancellationToken
            );

            _operatorMemoryState = await _operatorMemoryStore.UpdateSuggestionResearchResultAsync(
                suggestion.Id,
                brief.summary,
                brief.detail,
                brief.sources,
                brief.path,
                cancellationToken
            );
            _operatorMemoryState = await _operatorMemoryStore.UpdateSuggestionExecutionAsync(
                suggestion.Id,
                "completed",
                brief.summary,
                cancellationToken
            );
            await RecordActivityLockedAsync(
                "suggestion_completed",
                suggestion.SourceAgent,
                suggestion.Title,
                brief.summary,
                cancellationToken
            );
        }
        catch (Exception exception)
        {
            var failureSummary = $"Follow-through failed: {exception.Message}";
            _operatorMemoryState = await _operatorMemoryStore.UpdateSuggestionExecutionAsync(
                suggestion.Id,
                "failed",
                failureSummary,
                cancellationToken
            );
            await RecordActivityLockedAsync(
                "suggestion_failed",
                suggestion.SourceAgent,
                suggestion.Title,
                failureSummary,
                cancellationToken
            );
        }

        return ResolveSuggestionByIdLocked(suggestion.Id) ?? suggestion;
    }

    private static IReadOnlyList<SuggestedAction> BuildResearchSuggestions(
        ResearchReport report,
        string perspective,
        bool requiresApproval
    )
    {
        var actions = report.ActionMoves.Count == 0
            ? report.KeyTakeaways.Take(2).ToList()
            : report.ActionMoves.Take(2).ToList();
        var createdAt = DateTimeOffset.Now;

        return actions
            .Select(
                (action, index) =>
                    new SuggestedAction
                    {
                        Title = action,
                        SourceAgent = perspective,
                        ActionType = "research_followup",
                        Priority = index == 0 ? "high" : "medium",
                        Rationale = report.Summary,
                        ExpectedBenefit = action,
                        LinkedArea = report.Query,
                        WhatYouLearn =
                            "This turns live research into a concrete next move instead of leaving it as passive reading.",
                        ProductImpact = perspective switch
                        {
                            "Repo Coach" =>
                                "This can tighten Suite proposals before implementation work starts.",
                            "Business Strategist" =>
                                "This can keep packaging tied to real operator value and current market evidence.",
                            _ =>
                                "This can sharpen the next study or planning step with current source material.",
                        },
                        CareerValue =
                            "This builds evidence that you can turn current research into domain-aware action.",
                        RequiresApproval = requiresApproval,
                        CreatedAt = createdAt,
                    }
            )
            .ToList();
    }

    private async Task<string> PersistResearchMarkdownLockedAsync(
        ResearchReport report,
        string? notes,
        bool reloadKnowledge,
        CancellationToken cancellationToken
    )
    {
        var researchDirectory = Path.Combine(_knowledgeLibraryPath, "Research");
        Directory.CreateDirectory(researchDirectory);

        var slug = CreateSlug(report.Query);
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{slug}.md";
        var filePath = Path.Combine(researchDirectory, fileName);
        var markdown = BuildResearchMarkdown(report, notes);
        await File.WriteAllTextAsync(filePath, markdown, cancellationToken);

        if (reloadKnowledge)
        {
            _learningLibrary = await _knowledgeImportService.LoadAsync(
                _knowledgeLibraryPath,
                _additionalKnowledgePaths,
                cancellationToken
            );
            _learningProfile = new LearningProfile();
        }

        return filePath;
    }

    private async Task<(string path, string summary, string detail, IReadOnlyList<string> sources)>
        PersistResearchFollowThroughBriefLockedAsync(
            SuggestedAction suggestion,
            CancellationToken cancellationToken
        )
    {
        var followThroughDirectory = Path.Combine(_knowledgeLibraryPath, "Follow Through");
        Directory.CreateDirectory(followThroughDirectory);

        var slug = CreateSlug(
            string.IsNullOrWhiteSpace(suggestion.Title) ? suggestion.LinkedArea : suggestion.Title
        );
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{slug}.md";
        var filePath = Path.Combine(followThroughDirectory, fileName);
        var report = _session.LatestResearchReport;
        var sources = report?.Sources
            .Select(source =>
                string.IsNullOrWhiteSpace(source.Url) ? source.DisplaySummary : source.Url
            )
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList()
            ?? [];
        var markdown = BuildResearchFollowThroughMarkdown(suggestion, report, fileName);

        await File.WriteAllTextAsync(filePath, markdown, cancellationToken);

        _learningLibrary = await _knowledgeImportService.LoadAsync(
            _knowledgeLibraryPath,
            _additionalKnowledgePaths,
            cancellationToken
        );
        _learningProfile = new LearningProfile();

        var summary = $"Prepared follow-through brief: {fileName}";
        var detail =
            $"Saved a bounded research follow-through brief under Follow Through for '{suggestion.LinkedArea}'. Review it, then either add notes to the knowledge library or turn it into a concrete Suite or study task.";
        return (filePath, summary, detail, sources);
    }

    private string BuildResearchFollowThroughMarkdown(
        SuggestedAction suggestion,
        ResearchReport? report,
        string fileName
    )
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Research Follow-Through Brief");
        builder.AppendLine();
        builder.AppendLine($"- Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- File: {fileName}");
        builder.AppendLine($"- Source agent: {suggestion.SourceAgent}");
        builder.AppendLine($"- Priority: {suggestion.Priority}");
        builder.AppendLine($"- Linked area: {suggestion.LinkedArea}");
        builder.AppendLine();
        builder.AppendLine("## Prompted move");
        builder.AppendLine(suggestion.Title);
        builder.AppendLine();
        builder.AppendLine("## Why this matters");
        builder.AppendLine(
            string.IsNullOrWhiteSpace(suggestion.Rationale)
                ? "No rationale was captured."
                : suggestion.Rationale
        );
        builder.AppendLine();
        builder.AppendLine("## Expected benefit");
        builder.AppendLine(
            string.IsNullOrWhiteSpace(suggestion.ExpectedBenefit)
                ? "No expected benefit was captured."
                : suggestion.ExpectedBenefit
        );
        builder.AppendLine();
        builder.AppendLine("## Operator framing");
        builder.AppendLine($"- Learning value: {suggestion.WhatYouLearn}");
        builder.AppendLine($"- Product impact: {suggestion.ProductImpact}");
        builder.AppendLine($"- Career value: {suggestion.CareerValue}");
        builder.AppendLine($"- Current study focus: {_session.Focus}");
        builder.AppendLine($"- Current review queue: {_trainingHistorySummary.ReviewQueueSummary}");
        builder.AppendLine($"- Suite context: {BuildQuietSuiteContextSummary(_suiteSnapshot)}");
        builder.AppendLine($"- Suite trust: {BuildQuietSuiteTrustSummary(_suiteSnapshot)}");
        builder.AppendLine();

        if (report is not null)
        {
            builder.AppendLine("## Latest research run");
            builder.AppendLine($"- Query: {report.Query}");
            builder.AppendLine($"- Perspective: {report.Perspective}");
            builder.AppendLine($"- Model: {report.Model}");
            builder.AppendLine($"- Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm}");
            builder.AppendLine($"- Summary: {report.Summary}");
            builder.AppendLine();

            if (report.KeyTakeaways.Count > 0)
            {
                builder.AppendLine("## Key takeaways");
                foreach (var takeaway in report.KeyTakeaways)
                {
                    builder.AppendLine($"- {takeaway}");
                }

                builder.AppendLine();
            }

            if (report.ActionMoves.Count > 0)
            {
                builder.AppendLine("## Suggested next moves");
                foreach (var actionMove in report.ActionMoves)
                {
                    builder.AppendLine($"- {actionMove}");
                }

                builder.AppendLine();
            }

            if (report.Sources.Count > 0)
            {
                builder.AppendLine("## Sources");
                foreach (var source in report.Sources)
                {
                    builder.AppendLine($"- {source.DisplaySummary}: {source.Url}");
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine("## Review-first next check");
        builder.AppendLine(
            "Before treating this as a decision, compare the brief against your own notes, standards, and drawing-review criteria."
        );
        return builder.ToString().Trim() + Environment.NewLine;
    }

    private static string BuildResearchMarkdown(ResearchReport report, string? notes)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Live Research: {report.Query}");
        builder.AppendLine();
        builder.AppendLine($"- Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- Perspective: {report.Perspective}");
        builder.AppendLine($"- Model: {report.Model}");
        builder.AppendLine($"- Source: {report.GenerationSource}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine(report.Summary);
        builder.AppendLine();
        builder.AppendLine("## Key Takeaways");
        builder.AppendLine();
        foreach (var takeaway in report.KeyTakeaways)
        {
            builder.AppendLine($"- {takeaway}");
        }

        builder.AppendLine();
        builder.AppendLine("## Action Moves");
        builder.AppendLine();
        foreach (var action in report.ActionMoves)
        {
            builder.AppendLine($"- {action}");
        }

        builder.AppendLine();
        builder.AppendLine("## Sources");
        builder.AppendLine();
        foreach (var source in report.Sources)
        {
            builder.AppendLine($"### {source.Title}");
            builder.AppendLine();
            builder.AppendLine($"- Domain: {source.Domain}");
            builder.AppendLine($"- URL: {source.Url}");
            builder.AppendLine($"- Search Snippet: {source.SearchSnippet}");
            if (!string.IsNullOrWhiteSpace(source.Extract))
            {
                builder.AppendLine($"- Extract: {source.Extract}");
            }

            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(notes))
        {
            builder.AppendLine("## Operator Notes");
            builder.AppendLine();
            builder.AppendLine(notes.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string CreateSlug(string value)
    {
        var cleaned = new string(
            value
                .Trim()
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray()
        );
        while (cleaned.Contains("--", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        }

        cleaned = cleaned.Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "live-research" : cleaned;
    }

    private static string BuildQuietSuiteContextSummary(SuiteSnapshot snapshot)
    {
        if (!snapshot.RepoAvailable)
        {
            return "Suite awareness is unavailable at the configured path right now.";
        }

        if (!snapshot.RuntimeStatusAvailable)
        {
            return "Suite awareness is connected and read-only. Runtime trust is currently unavailable.";
        }

        return snapshot.RuntimeDoctorState switch
        {
            "ready" => "Suite awareness is connected, read-only, and stable.",
            "needs-attention" => "Suite awareness is connected. Runtime trust needs attention.",
            "unavailable" => "Suite awareness is connected. Runtime trust is unavailable right now.",
            _ => "Suite awareness is connected. Runtime trust is still settling.",
        };
    }

    private static string BuildQuietSuiteTrustSummary(SuiteSnapshot snapshot)
    {
        if (!snapshot.RepoAvailable)
        {
            return "Suite trust cannot be checked until the configured Suite path is available.";
        }

        if (!snapshot.RuntimeStatusAvailable)
        {
            return "Runtime trust is currently unavailable from Office.";
        }

        return snapshot.ActionableIssueCount > 0
            ? "Runtime trust needs attention before you lean on Suite context."
            : "Runtime trust looks steady for read-only awareness.";
    }

    private static string BuildCareerEngineProgressSummary(OperatorMemoryState state)
    {
        var chiefPasses = state.Activities.Count(item =>
            item.EventType.Equals("chief_pass", StringComparison.OrdinalIgnoreCase)
        );
        var researchRuns = state.Activities.Count(item =>
            item.EventType.Equals("research_run", StringComparison.OrdinalIgnoreCase)
            || item.EventType.Equals("watchlist_run", StringComparison.OrdinalIgnoreCase)
        );
        var practice = state.Activities.Count(item =>
            item.EventType.Equals("practice_scored", StringComparison.OrdinalIgnoreCase)
        );
        var defense = state.Activities.Count(item =>
            item.EventType.Equals("defense_scored", StringComparison.OrdinalIgnoreCase)
        );
        var resolved = state.Suggestions.Count(item => !item.IsPending);

        return $"Chief passes {chiefPasses}/8 | Research runs {researchRuns}/8 | Practice {practice}/6 | Defense {defense}/4 | Suggestions resolved {resolved}/10.";
    }

    private static string BuildApprovalInboxSummary(OperatorMemoryState state)
    {
        var pending = state.PendingApprovalSuggestions.Count;
        var resolved = state.Suggestions.Count(item => item.RequiresApproval && !item.IsPending);

        return pending switch
        {
            0 => resolved == 0
                ? "No approvals are pending."
                : $"No approvals are pending. {resolved} recent approval decision{(resolved == 1 ? string.Empty : "s")} recorded.",
            1 => $"{pending} approval is pending. {resolved} recent approval decision{(resolved == 1 ? string.Empty : "s")} recorded.",
            _ => $"{pending} approvals are pending. {resolved} recent approval decision{(resolved == 1 ? string.Empty : "s")} recorded.",
        };
    }

    private static string BuildSuggestionsSummary(OperatorMemoryState state)
    {
        var open = state.OpenSuggestions.Count;
        var approved = state.ApprovedSuggestions.Count;
        return $"{open} open suggestion{(open == 1 ? string.Empty : "s")} | {approved} approved next step{(approved == 1 ? string.Empty : "s")}.";
    }

    private static string BuildWatchlistSummary(OperatorMemoryState state)
    {
        if (state.Watchlists.Count == 0)
        {
            return "No watchlists configured yet. Add one when you want recurring research again.";
        }

        var due = state.DueWatchlists.Count;
        var next = state.Watchlists.Where(item => item.IsEnabled).OrderBy(item => item.NextDueAt).FirstOrDefault();
        return next is null
            ? $"{state.Watchlists.Count} watchlists configured."
            : $"{due} due now | next: {next.Topic} ({next.DueSummary}).";
    }

    private static int GetInboxSortRank(SuggestedAction suggestion)
    {
        if (suggestion.RequiresApproval && suggestion.IsPending)
        {
            return 0;
        }

        if (suggestion.IsPending)
        {
            return 1;
        }

        return suggestion.Status switch
        {
            "deferred" => 2,
            "accepted" => 3,
            "rejected" => 4,
            _ => 5,
        };
    }

    private static string ResolveOfficeRootPath(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            var dailyDeskPath = Path.Combine(current.FullName, "DailyDesk");
            var dailyDeskProjectPath = Path.Combine(dailyDeskPath, "DailyDesk.csproj");
            if (Directory.Exists(dailyDeskPath) && File.Exists(dailyDeskProjectPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", ".."));
    }

    private static string GetUniqueKnowledgeImportPath(string targetDirectory, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidatePath = Path.Combine(targetDirectory, fileName);
        var copyIndex = 2;

        while (File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(targetDirectory, $"{baseName} ({copyIndex}){extension}");
            copyIndex++;
        }

        return candidatePath;
    }

    private static string JoinOrNone(IReadOnlyList<string> items) =>
        items.Count == 0 ? "none recorded" : string.Join("; ", items);

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength].Trim()}...";
    }

    // --- ML Pipeline ---

    public async Task<MLEmbeddingsResult> RunMLEmbeddingsAsync(
        string? query = null,
        CancellationToken cancellationToken = default
    )
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
            var result = await _mlPipelineCoordinator.RunMLEmbeddingsAsync(
                documents,
                query,
                cancellationToken
            );

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

    public async Task<object> RunFullMLPipelineAsync(
        CancellationToken cancellationToken = default
    )
    {
        IReadOnlyList<OperatorActivityRecord> decisions;
        IReadOnlyList<LearningDocument> documents;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedLockedAsync(cancellationToken);
            decisions = _operatorMemoryState.Activities;
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
                decisions,
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

    public async Task<SuiteMLArtifactBundle> ExportSuiteArtifactsAsync(
        CancellationToken cancellationToken = default
    )
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
                analytics,
                embeddings,
                forecast,
                cancellationToken
            );

            var exportPath = await _mlAnalyticsService.ExportArtifactsAsync(
                artifacts,
                _stateRootPath,
                cancellationToken
            );

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

    private OfficeMLSection BuildMLSectionLocked()
    {
        if (!_settings.EnableMLPipeline)
        {
            return new OfficeMLSection
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

        return new OfficeMLSection
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

    /// <summary>
    /// Indexes all knowledge documents by generating embeddings and storing them in the vector store.
    /// Skips documents that have not changed since last indexing.
    /// </summary>
    public async Task<KnowledgeIndexResult> RunKnowledgeIndexAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DailyDesk.Models.LearningDocument> documents;

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

        var indexed = 0;
        var skipped = 0;
        var failed = 0;
        var total = documents.Count;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var textContent = document.ExtractedText ?? document.Summary ?? string.Empty;
            if (string.IsNullOrWhiteSpace(textContent))
            {
                skipped++;
                continue;
            }

            var contentHash = KnowledgeIndexStore.ComputeContentHash(textContent);
            if (!_knowledgeIndexStore.NeedsIndexing(document.RelativePath, contentHash))
            {
                skipped++;
                continue;
            }

            var embedding = await _embeddingService.GenerateEmbeddingAsync(textContent, cancellationToken);
            if (embedding is null)
            {
                failed++;
                continue;
            }

            var vectorId = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(document.RelativePath)))[..32];

            var metadata = new Dictionary<string, string>
            {
                ["path"] = document.RelativePath,
                ["kind"] = document.Kind,
                ["source"] = document.SourceRootLabel,
            };
            if (document.Topics.Count > 0)
            {
                metadata["topics"] = string.Join(", ", document.Topics.Take(5));
            }

            var upserted = await _vectorStoreService.UpsertAsync(vectorId, embedding, metadata, cancellationToken);
            if (upserted)
            {
                _knowledgeIndexStore.MarkIndexed(document.RelativePath, contentHash, vectorId);
                indexed++;
            }
            else
            {
                failed++;
            }
        }

        return new KnowledgeIndexResult
        {
            TotalDocuments = total,
            Indexed = indexed,
            Skipped = skipped,
            Failed = failed,
            IndexedAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Returns the current knowledge index status (indexed vs. total documents).
    /// </summary>
    public async Task<KnowledgeIndexStatus> GetKnowledgeIndexStatusAsync(
        CancellationToken cancellationToken = default)
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

        var indexedCount = _knowledgeIndexStore.GetIndexedCount();
        var collectionInfo = await _vectorStoreService.GetCollectionInfoAsync(cancellationToken);

        return new KnowledgeIndexStatus
        {
            TotalDocuments = totalDocuments,
            IndexedDocuments = indexedCount,
            VectorStorePoints = collectionInfo?.PointsCount ?? 0,
            VectorStoreStatus = collectionInfo?.Status ?? "unreachable",
        };
    }

    // --- Phase 8: Daily Run Workflow ---

    /// <summary>
    /// Executes a full daily workflow:
    /// 1. Refresh state (models, snapshot, training history, knowledge library).
    /// 2. Run ML pipeline (analytics, forecast, embeddings in parallel).
    /// 3. Export Suite artifacts.
    /// 4. Generate operator suggestions based on ML results.
    /// 5. Log the daily run summary.
    /// </summary>
    public async Task<DailyRunSummary> RunDailyWorkflowAsync(
        CancellationToken cancellationToken = default)
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

        // Step 5: Generate operator suggestions based on ML results
        try
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_latestMLAnalytics is not null)
                {
                    var succeededCount = stepResults.Count(r => r.Success);
                    var suggestion = new SuggestedAction
                    {
                        Title = "Daily Run Complete",
                        SourceAgent = "daily-run",
                        Rationale = $"Daily automated workflow completed at {DateTimeOffset.Now:g}. " +
                                    $"{succeededCount} of {stepResults.Count} steps succeeded.",
                        Priority = "low",
                    };
                    await _operatorMemoryStore.UpsertSuggestionsAsync(
                        [suggestion], cancellationToken);
                }
            }
            finally
            {
                _gate.Release();
            }
            stepResults.Add(new DailyRunStepResult { Step = "OperatorSuggestions", Success = true });
        }
        catch (Exception ex)
        {
            stepResults.Add(new DailyRunStepResult { Step = "OperatorSuggestions", Success = false, Error = ex.Message });
        }

        summary.CompletedAt = DateTimeOffset.Now;
        summary.Steps = stepResults;
        summary.OverallSuccess = stepResults.All(r => r.Success);

        return summary;
    }

    /// <summary>
    /// Returns the most recent daily run summary from job history,
    /// or null if no daily run has been executed.
    /// </summary>
    public DailyRunJobSummary? GetLatestDailyRunSummary()
    {
        const int recentJobLimit = 50;

        var jobs = _jobStore.ListByStatus(OfficeJobStatus.Succeeded, recentJobLimit);
        var dailyRunJob = jobs
            .Where(j => j.Type == OfficeJobType.DailyRun)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefault();

        if (dailyRunJob is null)
        {
            // Also check failed runs
            var failedJobs = _jobStore.ListByStatus(OfficeJobStatus.Failed, recentJobLimit);
            dailyRunJob = failedJobs
                .Where(j => j.Type == OfficeJobType.DailyRun)
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
