using System.IO;
using Foundry.Core.Agents;
using Foundry.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Foundry.Services;

/// <summary>
/// Central orchestrator for the Foundry broker. Owns shared infrastructure
/// (model provider, knowledge index, job store, scheduler) and routes
/// incoming agent handoffs to registered <see cref="IAgent"/> implementations.
/// </summary>
/// <remarks>
/// Health, lifecycle, knowledge, and workflow concerns are factored into
/// focused partial-class files to keep this file small and navigable.
/// </remarks>
public sealed partial class FoundryOrchestrator
{
    private readonly FoundryBrokerRuntimeMetadata _brokerMetadata;
    private readonly FoundrySettings _settings;
    private readonly string _foundryRootPath;
    private readonly string _knowledgeLibraryPath;
    private readonly string _stateRootPath;
    private readonly IReadOnlyList<string> _additionalKnowledgePaths;

    private readonly IModelProvider _modelProvider;
    private readonly KnowledgeImportService _knowledgeImportService;
    private readonly KnowledgeCoordinator _knowledgeCoordinator;
    private readonly FoundryDatabase _foundryDatabase;
    private readonly FoundryJobStore _jobStore;
    private readonly NotificationStore _notificationStore;
    private readonly ProcessRunner _processRunner;
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStoreService _vectorStoreService;
    private readonly KnowledgeIndexStore _knowledgeIndexStore;
    private readonly JobSchedulerStore _schedulerStore;
    private readonly WorkflowStore _workflowStore;
    private readonly AgentDispatcher _dispatcher;
    private readonly DependencyMonitorService _dependencyMonitorService;

    // Shared synchronization primitives used across partial files.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Timeout constants used in lifecycle initialization.
    private static readonly TimeSpan InstalledModelsLoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LearningLibraryLoadTimeout = TimeSpan.FromSeconds(20);

    private bool _initialized;
    private DateTimeOffset _lastRefreshAt = DateTimeOffset.Now;
    private IReadOnlyList<string> _installedModelCache = Array.Empty<string>();
    private LearningLibrary _learningLibrary = new();

    /// <summary>
    /// Initializes a new <see cref="FoundryOrchestrator"/>.
    /// </summary>
    /// <param name="brokerMetadata">Runtime metadata about the broker host.</param>
    /// <param name="agents">
    /// Optional agents to register for handoff dispatch. Pass via
    /// <c>services.AddFoundryAgent&lt;TAgent&gt;()</c> and let the DI
    /// container inject <c>IEnumerable&lt;IAgent&gt;</c> here.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public FoundryOrchestrator(
        FoundryBrokerRuntimeMetadata brokerMetadata,
        IEnumerable<IAgent>? agents = null,
        ILoggerFactory? loggerFactory = null)
    {
        var lf = loggerFactory ?? NullLoggerFactory.Instance;
        _brokerMetadata = brokerMetadata;
        _foundryRootPath = ResolveFoundryRootPath(AppContext.BaseDirectory);
        _settings = FoundrySettings.Load(_foundryRootPath);
        _knowledgeLibraryPath = _settings.ResolveKnowledgeLibraryPath(_foundryRootPath);
        _stateRootPath = _settings.ResolveStateRootPath(_foundryRootPath);
        Directory.CreateDirectory(_knowledgeLibraryPath);
        Directory.CreateDirectory(_stateRootPath);
        _additionalKnowledgePaths = _settings.ResolveAdditionalKnowledgePaths();

        // Resilience pipelines
        var ollamaPipeline = FoundryResiliencePipelines.BuildOllamaPipeline();
        var pythonPipeline = FoundryResiliencePipelines.BuildPythonSubprocessPipeline();

        // LiteDB persistence
        _foundryDatabase = new FoundryDatabase(_stateRootPath);
        _jobStore = new FoundryJobStore(_foundryDatabase);
        _notificationStore = new NotificationStore(_foundryDatabase);

        _processRunner = new ProcessRunner(lf.CreateLogger<ProcessRunner>());
        _modelProvider = new OllamaService(_settings.OllamaEndpoint, _processRunner, ollamaPipeline, lf.CreateLogger<OllamaService>());
        _knowledgeImportService = new KnowledgeImportService(
            _processRunner,
            Path.Combine(_foundryRootPath, "scripts", "ml", "extract_document_text.py")
        );

        // Semantic search services
        var ollamaUri = new Uri(_settings.OllamaEndpoint.EndsWith("/") ? _settings.OllamaEndpoint : $"{_settings.OllamaEndpoint}/");
        var embeddingHttpClient = new System.Net.Http.HttpClient { BaseAddress = ollamaUri, Timeout = TimeSpan.FromSeconds(90) };
        var ollamaEmbedClient = new OllamaSharp.OllamaApiClient(embeddingHttpClient);
        _embeddingService = new EmbeddingService(ollamaEmbedClient, _settings.OllamaEmbeddingModel, ollamaPipeline, lf.CreateLogger<EmbeddingService>());
        _vectorStoreService = new VectorStoreService(logger: lf.CreateLogger<VectorStoreService>());
        _knowledgeIndexStore = new KnowledgeIndexStore(_foundryDatabase, lf.CreateLogger<KnowledgeIndexStore>());
        _knowledgeCoordinator = new KnowledgeCoordinator(_embeddingService, _vectorStoreService, _knowledgeIndexStore);

        // Scheduled automation & workflow orchestration
        _schedulerStore = new JobSchedulerStore(_foundryDatabase);
        _workflowStore = new WorkflowStore(_foundryDatabase);

        // Agent dispatch
        var registeredAgents = (agents ?? [])
            .Concat([
                new DepReviewerAgent(
                    _modelProvider,
                    _settings.OllamaChatModel,
                    lf.CreateLogger<DepReviewerAgent>(),
                    _settings.StripListPackages)
            ])
            .ToList();
        _dispatcher = new AgentDispatcher(registeredAgents, lf.CreateLogger<AgentDispatcher>());

        var gitHubClient = new GitHubDependencyClient(_settings, logger: lf.CreateLogger<GitHubDependencyClient>());
        _dependencyMonitorService = new DependencyMonitorService(
            _settings,
            gitHubClient,
            _notificationStore,
            _dispatcher,
            lf.CreateLogger<DependencyMonitorService>());
    }

    // --- Public accessors for DI consumers ---

    /// <summary>Job persistence store.</summary>
    public FoundryJobStore JobStore => _jobStore;

    /// <summary>Number of days to retain completed jobs.</summary>
    public int JobRetentionDays => _settings.JobRetentionDays;

    /// <summary>Dependency poll interval for the hosted monitor worker.</summary>
    public TimeSpan DependencyPollingInterval => _dependencyMonitorService.PollingInterval;

    /// <summary>Notification persistence store.</summary>
    public NotificationStore Notifications => _notificationStore;

    /// <summary>Embedding generation service.</summary>
    public EmbeddingService EmbeddingService => _embeddingService;

    /// <summary>Vector store (Qdrant/Chroma) service.</summary>
    public VectorStoreService VectorStoreService => _vectorStoreService;

    /// <summary>Knowledge index metadata store.</summary>
    public KnowledgeIndexStore KnowledgeIndexStore => _knowledgeIndexStore;

    /// <summary>Job schedule store.</summary>
    public JobSchedulerStore SchedulerStore => _schedulerStore;

    /// <summary>Workflow template store.</summary>
    public WorkflowStore WorkflowStore => _workflowStore;

    /// <summary>Knowledge coordinator (indexing + search).</summary>
    public KnowledgeCoordinator Knowledge => _knowledgeCoordinator;
}
