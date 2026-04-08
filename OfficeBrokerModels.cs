using DailyDesk.Services;

namespace DailyDesk.Models;

public sealed class OfficeLiveSessionState
{
    public string CurrentRoute { get; set; } = DailyDesk.Services.OfficeRouteCatalog.ChiefRoute;
    public string Focus { get; set; } = "Protection, grounding, standards, drafting safety";
    public string FocusReason { get; set; } =
        "Set a focus manually or start from a review target to begin a guided session.";
    public string Difficulty { get; set; } = "Mixed";
    public int QuestionCount { get; set; } = 6;
    public bool PracticeGenerated { get; set; }
    public bool PracticeScored { get; set; }
    public bool DefenseGenerated { get; set; }
    public bool DefenseScored { get; set; }
    public bool ReflectionSaved { get; set; }
    public string PracticeResultSummary { get; set; } = "No scored practice yet.";
    public string DefenseAnswerDraft { get; set; } = string.Empty;
    public string DefenseScoreSummary { get; set; } = "No scored oral-defense answer yet.";
    public string DefenseFeedbackSummary { get; set; } =
        "Score a typed answer to get rubric feedback and follow-up coaching.";
    public string ReflectionContextSummary { get; set; } =
        "Score a practice or defense session to save a reflection.";
    public string LastScoredSessionMode { get; set; } = string.Empty;
    public string LastScoredSessionFocus { get; set; } = string.Empty;
    public SessionReflectionRecord? LastReflection { get; set; }
    public ResearchReport? LatestResearchReport { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class OfficeBrokerState
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public OfficeBrokerStatusSection Broker { get; init; } = new();
    public OfficeProviderSection Provider { get; init; } = new();
    public OfficeSuiteSection Suite { get; init; } = new();
    public OfficeChatSection Chat { get; init; } = new();
    public OfficeStudySection Study { get; init; } = new();
    public OfficeResearchSection Research { get; init; } = new();
    public OfficeLibrarySection Library { get; init; } = new();
    public OfficeGrowthSection Growth { get; init; } = new();
    public OfficeInboxSection Inbox { get; init; } = new();
    public OfficeMLSection ML { get; init; } = new();
}

public sealed class OfficeBrokerStatusSection
{
    public string Status { get; init; } = "ok";
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string BaseUrl { get; init; } = string.Empty;
    public bool LoopbackOnly { get; init; } = true;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastRefreshAt { get; init; } = DateTimeOffset.Now;
}

public sealed class OfficeProviderSection
{
    public string ActiveProviderId { get; init; } = OllamaService.OllamaProviderId;
    public string ActiveProviderLabel { get; init; } = OllamaService.OllamaProviderLabel;
    public string PrimaryProviderLabel { get; init; } = OllamaService.OllamaProviderLabel;
    public string ConfiguredProviderId { get; init; } = OllamaService.OllamaProviderId;
    public bool Ready { get; init; }
    public int InstalledModelCount { get; init; }
    public IReadOnlyList<string> InstalledModels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<OfficeProviderRoleModel> RoleModels { get; init; } = Array.Empty<OfficeProviderRoleModel>();
    public bool EnableHuggingFaceCatalog { get; init; }
    public string HuggingFaceCatalogUrl { get; init; } = string.Empty;
    public string HuggingFaceTokenEnvVar { get; init; } = string.Empty;
}

public sealed class OfficeProviderRoleModel
{
    public string Role { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public bool Installed { get; init; }
}

public sealed class OfficeSuiteSection
{
    public SuiteSnapshot Snapshot { get; init; } = new();
    public string Pulse { get; init; } = string.Empty;
    public string TrustSummary { get; init; } = string.Empty;
    public DateTimeOffset SnapshotLoadedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class OfficeChatSection
{
    public string CurrentRoute { get; init; } = DailyDesk.Services.OfficeRouteCatalog.ChiefRoute;
    public string CurrentRouteTitle { get; init; } = "Chief of Staff";
    public string ActiveThreadId { get; init; } = string.Empty;
    public string RouteReason { get; init; } = string.Empty;
    public IReadOnlyList<OfficeRouteOption> RouteOptions { get; init; } = Array.Empty<OfficeRouteOption>();
    public IReadOnlyList<string> SuggestedMoves { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuiteContext { get; init; } = Array.Empty<string>();
    public IReadOnlyList<DeskMessageRecord> Transcript { get; init; } = Array.Empty<DeskMessageRecord>();
    public IReadOnlyList<OfficeChatThread> Threads { get; init; } = Array.Empty<OfficeChatThread>();
}

public sealed class OfficeRouteOption
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Perspective { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}

public sealed class OfficeChatThread
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<DeskMessageRecord> Messages { get; init; } = Array.Empty<DeskMessageRecord>();
}

public sealed class OfficeStudySection
{
    public string Focus { get; init; } = string.Empty;
    public string Difficulty { get; init; } = "Mixed";
    public int QuestionCount { get; init; } = 6;
    public string PracticeResultSummary { get; init; } = "No scored practice yet.";
    public string DefenseScoreSummary { get; init; } = "No scored oral-defense answer yet.";
    public string DefenseFeedbackSummary { get; init; } =
        "Score a typed answer to get rubric feedback and follow-up coaching.";
    public string ReflectionContextSummary { get; init; } =
        "Score a practice or defense session to save a reflection.";
    public string PracticePrompt { get; init; } = string.Empty;
    public string DefensePrompt { get; init; } = string.Empty;
    public string LatestScore { get; init; } = string.Empty;
    public string LatestReflection { get; init; } = string.Empty;
    public IReadOnlyList<string> Hints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<OfficeStudyStep> Sequence { get; init; } = Array.Empty<OfficeStudyStep>();
    public TrainingHistorySummary History { get; init; } = new();
}

public sealed class OfficeStudyStep
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Status { get; init; } = "pending";
}

public sealed class OfficeResearchSection
{
    public ResearchReport? LatestReport { get; init; }
    public string Summary { get; init; } = "Run a live research query to pull current web sources into the desk.";
    public string RunSummary { get; init; } = "No live research run yet.";
    public IReadOnlyList<OfficeResearchRun> History { get; init; } = Array.Empty<OfficeResearchRun>();
}

public sealed class OfficeResearchRun
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class OfficeLibrarySection
{
    public string Summary { get; init; } = string.Empty;
    public int TotalDocumentCount { get; init; }
    public IReadOnlyList<OfficeLibraryRoot> Roots { get; init; } = Array.Empty<OfficeLibraryRoot>();
    public IReadOnlyList<OfficeLibraryDocument> Documents { get; init; } = Array.Empty<OfficeLibraryDocument>();
    public LearningLibrary Library { get; init; } = new();
    public LearningProfile Profile { get; init; } = new();
}

public sealed class OfficeLibraryRoot
{
    public string Label { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public bool IsPrimary { get; init; }
    public int DocumentCount { get; init; }
}

public sealed class OfficeLibraryDocument
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class OfficeGrowthSection
{
    public DailyRunTemplate? DailyRun { get; init; }
    public string CareerEngineProgressSummary { get; init; } = string.Empty;
    public string WatchlistSummary { get; init; } = string.Empty;
    public string ApprovalInboxSummary { get; init; } = string.Empty;
    public string SuggestionsSummary { get; init; } = string.Empty;
    public IReadOnlyList<string> ProofTracks { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FocusAreas { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Highlights { get; init; } = Array.Empty<string>();
    public IReadOnlyList<OfficeResearchRun> ResearchRuns { get; init; } = Array.Empty<OfficeResearchRun>();
    public IReadOnlyList<ResearchWatchlist> Watchlists { get; init; } = Array.Empty<ResearchWatchlist>();
}

public sealed class OfficeInboxSection
{
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<SuggestedAction> Approvals { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> QueuedReady { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> RecentResults { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> PendingApproval { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> Open { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> Approved { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> QueuedWork { get; init; } = Array.Empty<SuggestedAction>();
    public IReadOnlyList<SuggestedAction> Recent { get; init; } = Array.Empty<SuggestedAction>();
}

public sealed class OfficePracticeAnswerInput
{
    public int QuestionIndex { get; init; }
    public string SelectedOptionKey { get; init; } = string.Empty;
}

public sealed class OfficeLibraryImportResult
{
    public int ImportedCount { get; init; }
    public IReadOnlyList<string> ImportedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SkippedPaths { get; init; } = Array.Empty<string>();
}

public sealed class OfficeMLSection
{
    public bool Enabled { get; init; }
    public string Summary { get; init; } = "ML pipeline is not enabled. Set enableMLPipeline to true in settings.";
    public MLAnalyticsResult? Analytics { get; init; }
    public MLForecastResult? Forecast { get; init; }
    public MLEmbeddingsResult? Embeddings { get; init; }
    public string? LastArtifactExportPath { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}
