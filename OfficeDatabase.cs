using LiteDB;
using DailyDesk.Models;

namespace DailyDesk.Services;

/// <summary>
/// Manages a single LiteDB database instance for all Office persistence.
/// Replaces JSON file I/O in TrainingStore, OperatorMemoryStore, and OfficeSessionStateStore.
/// </summary>
public sealed class OfficeDatabase : IDisposable
{
    private readonly LiteDatabase _db;
    private bool _disposed;

    public OfficeDatabase(string stateRootPath)
    {
        var root = string.IsNullOrWhiteSpace(stateRootPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DailyDesk"
            )
            : Path.GetFullPath(stateRootPath);
        Directory.CreateDirectory(root);

        var dbPath = Path.Combine(root, "office.db");
        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");

        EnsureIndexes();
    }

    public ILiteCollection<TrainingAttemptRecord> PracticeAttempts =>
        _db.GetCollection<TrainingAttemptRecord>("training_practice_attempts");

    public ILiteCollection<OralDefenseAttemptRecord> DefenseAttempts =>
        _db.GetCollection<OralDefenseAttemptRecord>("training_defense_attempts");

    public ILiteCollection<SessionReflectionRecord> Reflections =>
        _db.GetCollection<SessionReflectionRecord>("training_reflections");

    public ILiteCollection<AgentPolicy> Policies =>
        _db.GetCollection<AgentPolicy>("operator_policies");

    public ILiteCollection<ResearchWatchlist> Watchlists =>
        _db.GetCollection<ResearchWatchlist>("operator_watchlists");

    public ILiteCollection<SuggestedAction> Suggestions =>
        _db.GetCollection<SuggestedAction>("operator_suggestions");

    public ILiteCollection<OperatorActivityRecord> Activities =>
        _db.GetCollection<OperatorActivityRecord>("operator_activities");

    public ILiteCollection<DailyRunTemplate> DailyRuns =>
        _db.GetCollection<DailyRunTemplate>("operator_daily_runs");

    public ILiteCollection<DeskThreadState> DeskThreads =>
        _db.GetCollection<DeskThreadState>("operator_desk_threads");

    public ILiteCollection<BsonDocument> Workflow =>
        _db.GetCollection("operator_workflow");

    public ILiteCollection<BsonDocument> SessionState =>
        _db.GetCollection("session_state");

    public ILiteCollection<OfficeJob> Jobs =>
        _db.GetCollection<OfficeJob>("jobs");

    public ILiteCollection<JobSchedule> JobSchedules =>
        _db.GetCollection<JobSchedule>("job_schedules");

    public ILiteCollection<WorkflowTemplate> WorkflowTemplates =>
        _db.GetCollection<WorkflowTemplate>("workflow_templates");

    public ILiteCollection<PersistedMLResult> MLAnalyticsResults =>
        _db.GetCollection<PersistedMLResult>("ml_analytics");

    public ILiteCollection<PersistedMLResult> MLForecastResults =>
        _db.GetCollection<PersistedMLResult>("ml_forecast");

    public ILiteCollection<PersistedMLResult> MLEmbeddingsResults =>
        _db.GetCollection<PersistedMLResult>("ml_embeddings");

    public ILiteCollection<IndexedDocumentRecord> KnowledgeIndex =>
        _db.GetCollection<IndexedDocumentRecord>("knowledge_index");

    private void EnsureIndexes()
    {
        PracticeAttempts.EnsureIndex(x => x.CompletedAt);
        DefenseAttempts.EnsureIndex(x => x.CompletedAt);
        Reflections.EnsureIndex(x => x.CompletedAt);
        Suggestions.EnsureIndex(x => x.Id);
        Suggestions.EnsureIndex(x => x.CreatedAt);
        Activities.EnsureIndex(x => x.OccurredAt);
        DailyRuns.EnsureIndex(x => x.DateKey);
        Jobs.EnsureIndex(x => x.Id);
        Jobs.EnsureIndex(x => x.Status);
        Jobs.EnsureIndex(x => x.CreatedAt);
        JobSchedules.EnsureIndex(x => x.Id);
        JobSchedules.EnsureIndex(x => x.Enabled);
        WorkflowTemplates.EnsureIndex(x => x.Id);
        KnowledgeIndex.EnsureIndex(x => x.DocumentPath);
    }

    /// <summary>
    /// Checks whether the database has been migrated from JSON files.
    /// Uses a metadata collection to track migration status.
    /// </summary>
    public bool HasMigrated(string storeName)
    {
        var meta = _db.GetCollection("_migration_meta");
        return meta.Exists(Query.EQ("_id", storeName));
    }

    /// <summary>
    /// Marks a store as migrated from JSON.
    /// </summary>
    public void MarkMigrated(string storeName)
    {
        var meta = _db.GetCollection("_migration_meta");
        meta.Upsert(new BsonDocument
        {
            ["_id"] = storeName,
            ["migratedAt"] = DateTime.UtcNow,
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _db.Dispose();
        }
    }
}
